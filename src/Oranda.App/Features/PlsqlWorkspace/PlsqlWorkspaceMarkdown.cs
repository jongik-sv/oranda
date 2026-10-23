using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.App.Features.PlsqlEditor;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간을 사람이 읽고 나눠 가질 수 있는 Markdown 파일(<c>.plsql-workspace.md</c>)로 쓰고 읽는다(B-3). SQL 작업 공간의 <c>.workspace.md</c> 와 같은 모양이다:
/// 탭마다 <c>## 제목</c> 아래 <c>&lt;!-- 메타 --&gt;</c> 주석과 <c>```sql</c> 코드 블록. 서버 객체 탭의 제목은 <c>소유자.이름 (유형)</c> 이다.
/// 접속은 프로필 ID(PC 마다 다름)가 아니라 이름으로 적고, 읽을 때 이름이 같은 프로필을 찾는다(없으면 접속 없음).
/// 줄끝은 늘 LF 이고, 글은 코드 블록 안에 그대로 두며 끝에 줄바꿈이 없으면 하나 붙는다.
/// </summary>
public static class PlsqlWorkspaceMarkdown
{
    /// <summary>작업 공간 파일 확장자(끝 일치, 대소문자 무시).</summary>
    public const string Extension = ".plsql-workspace.md";

    /// <summary>이 형식의 판. 더 큰 판(새 버전 앱이 쓴 파일)은 읽지 않는다.</summary>
    public const int CurrentVersion = 1;

    private const string MarkerName = "oranda-plsql-workspace";

    private static readonly Regex MarkerLine = new(@"^<!--\s*" + MarkerName + @"\s+(\d+)\s*-->\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex ConnectionLine = new(@"^<!--\s*connection:\s*(.+?)\s*-->\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex DdlTimeLine = new(@"^<!--\s*lastDdlTime:\s*(.+?)\s*-->\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex FileLine = new(@"^<!--\s*file:\s*(.+?)\s*-->\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex EncodingLine = new(@"^<!--\s*encoding:\s*(.+?)\s*-->\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex Line1ShiftLine = new(@"^<!--\s*line1Shift:\s*(\d+)\s*-->\s*$", RegexOptions.CultureInvariant);

    /// <summary>울타리 여는 줄. 정보 문자열은 그 울타리 글자(<c>`</c> 또는 <c>~</c>)를 뺀 아무 글자나 받는다(<c>```sql title=x</c>).</summary>
    private static readonly Regex FenceOpen = new(@"^(?:(?<f>`{3,})[^`]*|(?<f>~{3,})[^~]*)$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownEncodings = new(StringComparer.Ordinal) { "utf-8", "utf-8-bom", "utf-16", "utf-16be", "cp949" };

    /// <summary>경로가 PL/SQL 작업 공간 파일이다.</summary>
    public static bool IsWorkspaceFile(string? path) => path is not null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>작업 공간 이름으로 저장 대화상자에 제안할 파일 이름(경로 구분자 등 못 쓰는 글자는 「-」).</summary>
    public static string SuggestFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Append('/').Append('\\').ToHashSet();
        var clean = new string([.. (name ?? string.Empty).Select(c => bad.Contains(c) ? '-' : c)]).Trim();
        return (clean.Length == 0 ? "PL-SQL" : clean) + Extension;
    }

    /// <summary>파일 이름에서 작업 공간 이름(확장자 뺌).</summary>
    public static string NameFromPath(string path)
    {
        var file = Path.GetFileName(path);
        return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? file[..^Extension.Length] : Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>
    /// 작업 공간을 Markdown 으로. <paramref name="connectionName"/> = 프로필 ID → 이름(모르면 null: 접속 줄을 쓰지 않는다).
    /// 파일에 묶인 탭이 서버 객체 대상도 가졌으면 파일 탭으로 쓴다(제목 + <c>file</c> 줄).
    /// </summary>
    public static string Write(PlsqlWorkspaceFile file, Func<Guid, string?> connectionName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(connectionName);
        var sb = new StringBuilder();
        sb.Append("# ").Append(OneLine(file.Name)).Append('\n').Append('\n');
        sb.Append("<!-- ").Append(MarkerName).Append(' ').Append(CurrentVersion.ToString(CultureInfo.InvariantCulture)).Append(" -->\n");
        foreach (var tab in file.Tabs)
        {
            var isFile = !string.IsNullOrEmpty(tab.FilePath);
            sb.Append('\n').Append("## ").Append(Heading(tab, isFile)).Append('\n').Append('\n');
            if (tab.ConnectionProfileId is { } id && connectionName(id) is { Length: > 0 } conn)
            {
                sb.Append("<!-- connection: ").Append(OneLine(conn)).Append(" -->\n");
            }

            if (tab.Line1Shift > 0)
            {
                sb.Append("<!-- line1Shift: ").Append(tab.Line1Shift.ToString(CultureInfo.InvariantCulture)).Append(" -->\n");
            }

            if (tab.LastDdlTime is { } ddl)
            {
                sb.Append("<!-- lastDdlTime: ").Append(ddl.ToString("o", CultureInfo.InvariantCulture)).Append(" -->\n");
            }

            if (isFile)
            {
                sb.Append("<!-- file: ").Append(OneLine(tab.FilePath!)).Append(" -->\n");
                if (tab.Encoding is { Length: > 0 } enc && !string.Equals(enc, "utf-8", StringComparison.Ordinal))
                {
                    sb.Append("<!-- encoding: ").Append(OneLine(enc)).Append(" -->\n");
                }
            }

            var text = PlsqlSourceText.Normalize(tab.Text);
            var fence = new string('`', Math.Max(3, LongestRun(text, '`') + 1));
            sb.Append('\n').Append(fence).Append("sql\n");
            sb.Append(text);
            if (text.Length > 0 && !text.EndsWith('\n'))
            {
                sb.Append('\n');
            }

            sb.Append(fence).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Markdown 을 작업 공간으로. 표지(<c># 이름</c>·판 주석)가 없는 손으로 쓴 글도 읽는다(이름 = <paramref name="fallbackName"/>, 코드 블록이 없으면 글 전체가 탭 하나).
    /// <paramref name="profileId"/> = 접속 이름 → 프로필 ID(없으면 null: 접속 없음). 모든 탭은 고치지 않은 상태이고 작업 공간 ID 는 새로 정한다.
    /// 더 새 판 앱이 쓴 파일이면 <see cref="FormatException"/>.
    /// </summary>
    public static PlsqlWorkspaceFile Read(string text, string fallbackName, Func<string, Guid?> profileId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(profileId);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var name = fallbackName;
        var tabs = new List<PlsqlTabFile>();
        string? title = null;
        Meta meta = default;
        var tookBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("# ", StringComparison.Ordinal) && tabs.Count == 0 && title is null)
            {
                name = line[2..].Trim();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                title = line[3..].Trim();
                meta = default;
                tookBlock = false;
                continue;
            }

            if (MarkerLine.Match(line) is { Success: true } marker)
            {
                if (!int.TryParse(marker.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                {
                    throw new FormatException("판 표지를 읽을 수 없다");
                }

                if (version > CurrentVersion)
                {
                    throw new FormatException("더 새 판 앱이 쓴 PL/SQL 작업 공간 파일이다");
                }

                continue;
            }

            if (ConnectionLine.Match(line) is { Success: true } conn)
            {
                meta = meta with { Connection = conn.Groups[1].Value };
                continue;
            }

            if (DdlTimeLine.Match(line) is { Success: true } ddl)
            {
                meta = meta with
                {
                    LastDdlTime = DateTime.TryParse(ddl.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null,
                };
                continue;
            }

            if (FileLine.Match(line) is { Success: true } fileLine)
            {
                meta = meta with { FilePath = fileLine.Groups[1].Value };
                continue;
            }

            if (Line1ShiftLine.Match(line) is { Success: true } shiftLine)
            {
                meta = meta with { Line1Shift = int.TryParse(shiftLine.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var shift) ? shift : 0 };
                continue;
            }

            if (EncodingLine.Match(line) is { Success: true } encLine)
            {
                meta = meta with { Encoding = encLine.Groups[1].Value };
                continue;
            }

            var open = FenceOpen.Match(line);
            if (!open.Success)
            {
                continue;
            }

            var fence = open.Groups["f"].Value;
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                var close = lines[j].TrimEnd();
                if (close.Length >= fence.Length && close.All(c => c == fence[0]))
                {
                    break;
                }

                body.Add(lines[j]);
            }

            i = j;
            if (tookBlock)
            {
                continue; // 한 제목 아래 두 번째 코드 블록은 설명용으로 보고 건너뛴다.
            }

            tabs.Add(MakeTab(title ?? "PL/SQL " + (tabs.Count + 1).ToString(CultureInfo.InvariantCulture), body, meta, profileId));
            tookBlock = title is not null;
        }

        if (tabs.Count == 0)
        {
            tabs.Add(new PlsqlTabFile { Title = fallbackName, Text = text });
        }

        return new PlsqlWorkspaceFile
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name.Length == 0 ? fallbackName : name,
            ActiveTab = 0,
            Tabs = tabs,
        };
    }

    private static PlsqlTabFile MakeTab(string heading, List<string> body, Meta meta, Func<string, Guid?> profileId)
    {
        var text = body.Count == 0 ? string.Empty : string.Join('\n', body) + "\n";
        var server = meta.FilePath is null ? ParseServerHeading(heading) : null;
        return new PlsqlTabFile
        {
            Owner = server?.Owner,
            Name = server?.Name,
            Type = server?.Type,
            ConnectionProfileId = meta.Connection is { } c ? profileId(c) : null,
            LastDdlTime = meta.LastDdlTime,
            Title = server is null ? heading : null, // 서버 객체 탭의 제목은 복원할 때 정한다(본문이면 「(본문)」)
            Text = text,
            IsDirty = false,
            FilePath = meta.FilePath,
            Encoding = meta.FilePath is null ? null : meta.Encoding is { } enc && KnownEncodings.Contains(enc) ? enc : "utf-8", // 모르는 이름은 utf-8
            Line1Shift = server is null ? 0 : meta.Line1Shift,
        };
    }

    private static string Heading(PlsqlTabFile tab, bool isFile) =>
        !isFile && tab is { Owner.Length: > 0, Name.Length: > 0, Type.Length: > 0 }
            ? OneLine($"{Ident(tab.Owner)}.{Ident(tab.Name)} ({tab.Type})")
            : OneLine(tab.Title ?? (tab.Owner is { } o && tab.Name is { } n ? $"{o}.{n}" : "새 PL/SQL"));

    private static readonly Regex PlainIdent = new(@"^[A-Z][A-Z0-9_$#]*$", RegexOptions.CultureInvariant);

    /// <summary>일반 식별자(대문자 시작, 대문자·숫자·<c>_$#</c>)가 아니면 큰따옴표 식별자로 쓴다(공백·점·소문자 등이 제목의 경계와 섞이지 않게).</summary>
    private static string Ident(string id) => PlainIdent.IsMatch(id) ? id : "\"" + id.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary><c>소유자.이름 (유형)</c> 이고 유형이 소스를 가진 객체 유형이면 그 셋(따옴표 식별자는 풀어서). 아니면 null(그냥 탭 제목).</summary>
    private static (string Owner, string Name, string Type)? ParseServerHeading(string heading)
    {
        var m = Regex.Match(heading, @"^(?<id>.+)\s+\((?<type>[A-Z][A-Z ]*[A-Z])\)$", RegexOptions.CultureInvariant);
        if (!m.Success || !PlsqlSourceText.SourceTypes.Contains(m.Groups["type"].Value))
        {
            return null;
        }

        var id = m.Groups["id"].Value;
        var pos = 0;
        if (TakeIdent(id, ref pos) is not { } owner || pos >= id.Length || id[pos] != '.')
        {
            return null;
        }

        pos++;
        return TakeIdent(id, ref pos) is { } name && pos == id.Length ? (owner, name, m.Groups["type"].Value) : null;
    }

    /// <summary>식별자 하나를 읽는다. 따옴표면 <c>""</c> 를 풀고, 아니면 점·공백·따옴표 전까지. 비었거나 닫히지 않으면 null.</summary>
    private static string? TakeIdent(string text, ref int pos)
    {
        if (pos < text.Length && text[pos] == '"')
        {
            var sb = new StringBuilder();
            for (var i = pos + 1; i < text.Length; i++)
            {
                if (text[i] != '"')
                {
                    sb.Append(text[i]);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    pos = i + 1;
                    return sb.Length > 0 ? sb.ToString() : null;
                }
            }

            return null;
        }

        var start = pos;
        while (pos < text.Length && text[pos] != '.' && text[pos] != '"' && !char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }

        return pos > start ? text[start..pos] : null;
    }

    private static string OneLine(string s) => s.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }

    private readonly record struct Meta(string? Connection, DateTime? LastDdlTime, string? FilePath, string? Encoding, int Line1Shift = 0);
}
