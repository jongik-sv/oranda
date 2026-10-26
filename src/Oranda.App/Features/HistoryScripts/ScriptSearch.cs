using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>
/// 스크립트 창 검색(TSK-10-01): 연결한 폴더를 하위까지 훑어 파일 이름(낱말 모두 포함, 대소문자 무시)과
/// 선택하면 파일 내용(대소문자 무시 일반 글자, 정규식 아님)을 찾는다. UI 와 무관한 정적 논리라 스레드 풀에서 돌린다.
/// 파일은 편집기가 파일을 여는 것과 같은 <see cref="FileEncoding.Decode"/> 로 읽는다.
/// </summary>
public static class ScriptSearch
{
    /// <summary>일치 한도(D-025). 넘으면 거기서 멈추고 <see cref="ScriptSearchResult.LimitReached"/>.</summary>
    public const int MaxMatches = 1000;

    /// <summary>이보다 큰 파일은 내용을 읽지 않고 건너뛴다(D-025).</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024;

    private const int MaxDepth = 40;
    private const int PreviewLength = 200;
    private const int LeadContext = 40;
    private const int ProgressEvery = 20;

    /// <summary>스레드 풀에서 돌린다. 취소하면 <see cref="OperationCanceledException"/>(부분 결과는 돌려주지 않는다).</summary>
    public static Task<ScriptSearchResult> RunAsync(
        ScriptSearchQuery query,
        IReadOnlyList<string> roots,
        IProgress<ScriptSearchProgress>? progress = null,
        int maxMatches = MaxMatches,
        long maxFileBytes = MaxFileBytes,
        CancellationToken ct = default)
        => Task.Run(() => Run(query, roots, progress, maxMatches, maxFileBytes, ct), ct);

    /// <summary>호출한 스레드에서 바로 찾는다(시험·<see cref="RunAsync"/> 의 본체).</summary>
    public static ScriptSearchResult Run(
        ScriptSearchQuery query,
        IEnumerable<string> roots,
        IProgress<ScriptSearchProgress>? progress = null,
        int maxMatches = MaxMatches,
        long maxFileBytes = MaxFileBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roots);
        var text = query.Text ?? string.Empty;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return new ScriptSearchResult([], 0, false, 0, 0);
        }

        var run = new Walker(words, query.IncludeContent ? text.Trim() : null, maxMatches, maxFileBytes, progress, ct);
        foreach (var root in roots)
        {
            if (run.Done)
            {
                break;
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootName = Path.GetFileName(trimmed) is { Length: > 0 } n ? n : trimmed;
            run.Walk(root, rootName, 0);
        }

        return new ScriptSearchResult(run.Files, run.Matches, run.LimitReached, run.SkippedLarge, run.Unreadable);
    }

    private sealed class Walker(string[] words, string? needle, int limit, long maxBytes, IProgress<ScriptSearchProgress>? progress, CancellationToken ct)
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private int _scanned;

        public List<ScriptSearchFile> Files { get; } = [];

        public int Matches { get; private set; }

        public bool LimitReached { get; private set; }

        public int SkippedLarge { get; private set; }

        public int Unreadable { get; private set; }

        public bool Done => LimitReached;

        public void Walk(string dir, string display, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (!_dirs.Add(RealPath(dir)))
            {
                return; // 링크 순환·중복: 이미 본 실제 경로는 건너뛴다
            }

            List<string> files;
            List<string> subDirs;
            try
            {
                files = Directory.EnumerateFiles(dir).Where(ScriptsViewModel.IsScriptFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                subDirs = depth >= MaxDepth ? [] : Directory.EnumerateDirectories(dir).Where(d => !IsHidden(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Unreadable++;
                return;
            }

            foreach (var file in files)
            {
                if (Done)
                {
                    return;
                }

                Visit(file, display);
            }

            foreach (var sub in subDirs)
            {
                if (Done)
                {
                    return;
                }

                Walk(sub, display + Path.DirectorySeparatorChar + Path.GetFileName(sub), depth + 1);
            }
        }

        /// <summary>트리와 같은 숨김 판정(<see cref="FileAttributes.Hidden"/>; 유닉스에서는 점으로 시작하는 이름도 숨김).</summary>
        private static bool IsHidden(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string RealPath(string dir)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                var shown = info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.ResolveLinkTarget(returnFinalTarget: true) is { } target ? target.FullName : info.FullName;
                return shown.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return dir;
            }
        }

        private void Visit(string file, string display)
        {
            ct.ThrowIfCancellationRequested();
            if (!_seen.Add(file))
            {
                return;
            }

            if (++_scanned % ProgressEvery == 0)
            {
                progress?.Report(new ScriptSearchProgress(_scanned, Matches));
            }

            var name = Path.GetFileName(file);
            var nameHit = words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
            List<ScriptSearchLine> lines = needle is null ? [] : ReadLines(file);
            if (!nameHit && lines.Count == 0)
            {
                return;
            }

            if (Matches >= limit && lines.Count == 0)
            {
                LimitReached = true; // 한도를 채운 뒤에 또 맞는 파일이 있다
                return;
            }

            Files.Add(new ScriptSearchFile(file, name, display, nameHit, lines));
            Matches += Math.Max(1, lines.Count);
        }

        /// <summary>파일 안에서 줄마다 찾는다. 큰 파일·바이너리·읽기 오류는 줄 일치 없음으로 건너뛴다(큰 파일·오류는 따로 센다).</summary>
        private List<ScriptSearchLine> ReadLines(string file)
        {
            var hits = new List<ScriptSearchLine>();
            byte[] bytes;
            try
            {
                if (new FileInfo(file).Length > maxBytes)
                {
                    SkippedLarge++;
                    return hits;
                }

                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Unreadable++;
                return hits;
            }

            if (LooksBinary(bytes))
            {
                return hits;
            }

            var (text, _) = FileEncoding.Decode(bytes);
            var room = Math.Max(0, limit - Matches); // 이 파일이 넣을 수 있는 남은 자리
            var pos = 0;
            var lineNo = 0;
            while (pos <= text.Length)
            {
                if ((lineNo & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                lineNo++;
                var end = text.AsSpan(pos).IndexOfAny('\r', '\n');
                var lineEnd = end < 0 ? text.Length : pos + end;
                var idx = text.AsSpan(pos, lineEnd - pos).IndexOf(needle!.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    if (hits.Count >= room)
                    {
                        LimitReached = true; // 한도 다음 일치가 나왔다
                        break;
                    }

                    hits.Add(Preview(lineNo, text, pos, lineEnd, pos + idx));
                }

                if (end < 0)
                {
                    break;
                }

                pos = lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1);
            }

            return hits;
        }

        private ScriptSearchLine Preview(int lineNo, string text, int lineStart, int lineEnd, int matchAt)
        {
            var begin = lineStart;
            while (begin < matchAt && char.IsWhiteSpace(text[begin]))
            {
                begin++; // 들여쓰기는 미리보기에서 뺀다
            }

            var lead = false;
            if (matchAt - begin > LeadContext + 20)
            {
                begin = matchAt - LeadContext;
                lead = true;
            }

            var length = Math.Min(lineEnd - begin, PreviewLength);
            var chars = text.AsSpan(begin, length).ToArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i]))
                {
                    chars[i] = ' '; // 탭 등은 한 칸 공백(길이를 유지해 일치 위치가 어긋나지 않는다)
                }
            }

            var shift = lead ? 1 : 0;
            var preview = lead ? "…" + new string(chars) : new string(chars);

            // 미리보기 안의 일치를 모두 강조 구간으로 모은다(앞에서부터 겹치지 않게).
            var ranges = new List<ScriptMatchRange>();
            var from = shift;
            while (from <= preview.Length - needle!.Length)
            {
                var at = preview.AsSpan(from).IndexOf(needle.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                ranges.Add(new ScriptMatchRange(from + at, needle.Length));
                from += at + needle.Length;
            }

            return new ScriptSearchLine(lineNo, preview, ranges);
        }

        /// <summary>BOM 없는 파일의 앞 8 KB 에 NUL 이 있으면 글이 아닌 파일로 본다(UTF-16 은 BOM 이 있으면 글).</summary>
        private static bool LooksBinary(byte[] bytes)
        {
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                return false;
            }

            return Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0;
        }
    }
}
