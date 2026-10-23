using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>매퍼 폴더 안 매퍼의 최상위 요소 하나(id·원문만 들고 범위는 버린다).</summary>
/// <param name="Kind">종류.</param>
/// <param name="Name">요소 이름 그대로(<c>select</c>·<c>resultMap</c>·<c>cache</c> …).</param>
/// <param name="Id">id 속성(없으면 null).</param>
/// <param name="Text">요소 원문(여는 태그 ~ 닫는 태그).</param>
public sealed record MapperFolderElement(MyBatisStatementKind Kind, string Name, string? Id, string Text)
{
    /// <summary>select·insert·update·delete 인가.</summary>
    public bool IsStatement => Kind is MyBatisStatementKind.Select or MyBatisStatementKind.Insert
        or MyBatisStatementKind.Update or MyBatisStatementKind.Delete;
}

/// <summary>매퍼 폴더 안 매퍼 파일 하나.</summary>
/// <param name="Path">파일 절대 경로.</param>
/// <param name="Namespace">루트 mapper 의 namespace(없으면 null).</param>
/// <param name="Elements">최상위 요소(원문 순서).</param>
public sealed record MapperFolderFile(string Path, string? Namespace, IReadOnlyList<MapperFolderElement> Elements);

/// <summary>파일 하나를 다시 읽은 결과(감시·다시 훑기). <paramref name="File"/> 이 null 이면 매퍼가 아니거나(<paramref name="Exists"/> 참) 지워졌다.</summary>
public sealed record MapperFolderChange(string Path, MapperFolderFile? File, bool Exists);

/// <summary>sql 조각·resultMap 을 쓰는 곳 하나(요소 하나 안에서 쓴 수).</summary>
/// <param name="MapperPath">쓴 매퍼 파일 경로.</param>
/// <param name="MapperNamespace">쓴 매퍼 namespace.</param>
/// <param name="ElementId">쓴 요소 id(없으면 요소 이름).</param>
/// <param name="Count">그 요소 안에서 쓴 수.</param>
public sealed record MapperFolderUsage(string MapperPath, string? MapperNamespace, string ElementId, int Count);

/// <summary>
/// 매퍼 폴더 하나를 훑은 결과(MB-10, TSK-03-07). 한 번 만든 색인은 바뀌지 않는다.
/// 훑기는 <c>*.xml</c> 을 하위 폴더까지 읽고, 루트가 <c>mapper</c> 가 아니거나 깨진 xml 은 거른다(<see cref="SkippedCount"/>).
/// 숨은 폴더(<c>.</c> 로 시작)와 빌드 출력 폴더(<c>target</c>·<c>build</c>·<c>bin</c>·<c>obj</c>·<c>node_modules</c>)는 보지 않는다(같은 매퍼의 복사본).
/// 파일이 바뀌면 <see cref="WithFiles"/> 가 새 색인을 돌려준다. 조각 찾기·문장 찾기·쓰는 곳 찾기는 메모리만 본다(계산기가 UI 스레드에서 동기로 묻는다).
/// </summary>
public sealed partial class MapperFolderIndex
{
    // 루트 요소 이름은 앞부분만 보고 정한다(pom.xml·큰 데이터 xml 을 통째로 읽지 않게). 그 안에서 못 정하면 전체를 파서에 맡긴다.
    private const int SniffBytes = 8 * 1024;

    private static readonly string[] ExcludedFolders = ["target", "build", "bin", "obj", "node_modules"];

    private readonly FrozenDictionary<string, MapperFolderFile[]> _byNamespace;

    private MapperFolderIndex(string root, IReadOnlyList<MapperFolderFile> files, IReadOnlySet<string> skipped)
    {
        Root = root;
        Files = files;
        Skipped = skipped;
        _byNamespace = files.Where(f => f.Namespace is not null)
            .GroupBy(f => f.Namespace!, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>폴더 절대 경로.</summary>
    public string Root { get; }

    /// <summary>매퍼 파일(경로 순).</summary>
    public IReadOnlyList<MapperFolderFile> Files { get; }

    /// <summary>거른 xml(루트가 mapper 가 아니거나 깨짐) 경로.</summary>
    public IReadOnlySet<string> Skipped { get; }

    /// <summary>거른 xml 수.</summary>
    public int SkippedCount => Skipped.Count;

    /// <summary>빈 색인(폴더만 정함).</summary>
    public static MapperFolderIndex Empty(string root) => new(root, [], new HashSet<string>(PathComparer));

    /// <summary>폴더를 훑는다. 호출한 스레드를 막지 않게 스레드 풀에서 읽는다.</summary>
    public static Task<MapperFolderIndex> ScanAsync(string root, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return Task.Run(() => Scan(root, ct), ct);
    }

    /// <summary>
    /// 폴더를 훑는다(부른 스레드가 끝날 때까지 기다린다). 파일 읽기·파싱은 코어 수만큼 병렬로 하고(TSK-03-13), 결과는 경로순이다.
    /// 취소되면 <see cref="OperationCanceledException"/>. 읽을 수 없거나 깨진 파일은 거름 목록에 넣는다.
    /// </summary>
    public static MapperFolderIndex Scan(string root, CancellationToken ct) => Scan(root, Environment.ProcessorCount, ct);

    /// <summary>병렬도를 정해 훑는다(시험이 1 로 차례 읽기와 결과를 대조한다).</summary>
    internal static MapperFolderIndex Scan(string root, int parallelism, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        var full = Path.GetFullPath(root);
        ct.ThrowIfCancellationRequested();
        var paths = EnumerateXml(full).Where(p => !IsExcluded(full, p)).ToArray();
        var read = new MapperFolderFile?[paths.Length];
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism };
        Parallel.For(0, paths.Length, options, i => read[i] = ReadFile(paths[i]));

        var files = new List<MapperFolderFile>(paths.Length);
        var skipped = new HashSet<string>(PathComparer);
        for (int i = 0; i < paths.Length; i++)
        {
            if (read[i] is { } file)
            {
                files.Add(file);
            }
            else
            {
                skipped.Add(paths[i]);
            }
        }

        files.Sort((a, b) => PathComparer.Compare(a.Path, b.Path));
        return new MapperFolderIndex(full, files, skipped);
    }

    /// <summary>
    /// 파일 하나를 읽는다. 매퍼가 아니거나(루트 이름), 깨졌거나, 읽을 수 없으면 null.
    /// 파일은 한 번만 연다: 앞부분을 읽어 루트 이름을 보고, 매퍼(또는 앞부분으로 못 정함)면 같은 흐름에서 나머지를 이어 읽는다.
    /// </summary>
    public static MapperFolderFile? ReadFile(string path)
    {
        IMyBatisMapperDocument document;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            long size = stream.Length;
            var buffer = new byte[(int)Math.Clamp(size + 1, 1, SniffBytes)];
            int length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            // 앞부분만 보고 루트 이름을 정한다: 매퍼가 아닌 큰 xml 을 통째로 읽지 않는다.
            if (SniffRoot(buffer.AsSpan(0, length)) is { } rootName && !string.Equals(rootName, "mapper", StringComparison.Ordinal))
            {
                return null;
            }

            if (length == buffer.Length)
            {
                // 앞부분을 다 채웠다 = 뒤가 더 있을 수 있다. 읽는 동안 파일이 자라도 끝까지 읽는다.
                using var all = new MemoryStream((int)Math.Min(Math.Max(size, length), Array.MaxLength));
                all.Write(buffer, 0, length);
                stream.CopyTo(all);
                document = new MapperParser().Parse(all.GetBuffer().AsSpan(0, (int)all.Length));
            }
            else
            {
                document = new MapperParser().Parse(buffer.AsSpan(0, length));
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return null; // 읽을 수 없거나 깨진 파일은 거른다(파일 하나 때문에 훑기 전체가 실패하지 않게)
        }

        var elements = document.Elements
            .Select(e => new MapperFolderElement(MyBatisStatementEntry.KindOf(e.Kind), e.Name, e.Id, e.Text))
            .ToArray();
        return new MapperFolderFile(Path.GetFullPath(path), document.MapperNamespace, elements);
    }

    /// <summary>
    /// 여러 파일을 바꾼 새 색인(경로마다 다시 읽은 결과로 바꾸거나 빼거나 거름 목록에 넣는다). 바뀐 것이 없으면 이 색인 그대로.
    /// </summary>
    public MapperFolderIndex WithFiles(IReadOnlyCollection<MapperFolderChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return this;
        }

        var byPath = Files.ToDictionary(f => f.Path, PathComparer);
        var skipped = new HashSet<string>(Skipped, PathComparer);
        bool changed = false;
        foreach (var c in changes)
        {
            var full = Path.GetFullPath(c.Path);
            var before = byPath.GetValueOrDefault(full);
            bool wasSkipped = skipped.Contains(full);
            byPath.Remove(full);
            skipped.Remove(full);
            if (c.File is not null)
            {
                byPath[full] = c.File;
            }
            else if (c.Exists)
            {
                skipped.Add(full);
            }

            changed |= !Equals(before, c.File) && !(before is not null && c.File is not null && SameContent(before, c.File))
                || wasSkipped != (c.File is null && c.Exists);
        }

        if (!changed)
        {
            return this;
        }

        var files = byPath.Values.ToList();
        files.Sort((a, b) => PathComparer.Compare(a.Path, b.Path));
        return new MapperFolderIndex(Root, files, skipped);
    }

    /// <summary><paramref name="folder"/> 아래 매퍼가 있는가(폴더가 지워지거나 옮겨졌을 때 다시 훑을지 정한다).</summary>
    public bool HasFilesUnder(string folder)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return Files.Any(f => f.Path.StartsWith(prefix, comparison));
    }

    /// <summary>숨은 폴더·빌드 출력 폴더 안 경로인가(훑기와 감시가 함께 거른다).</summary>
    public bool IsExcluded(string path) => IsExcluded(Root, path);

    /// <summary>이 폴더 안 경로인가.</summary>
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// include 의 sql 조각(<c>ns.id</c> 또는 <paramref name="currentNamespace"/> 안의 <c>id</c>). namespace 가 같은 매퍼가 둘 이상이면 경로 순 첫 파일.
    /// 돌려주는 조각의 <see cref="MyBatisFragment.Namespace"/> 는 늘 채운다(계산기의 순환 판정 키가 <c>namespace.id</c>).
    /// </summary>
    public MyBatisFragment? ResolveFragment(string refid, string? currentNamespace)
    {
        ArgumentNullException.ThrowIfNull(refid);
        foreach (var (ns, id) in Candidates(refid, currentNamespace))
        {
            if (_byNamespace.TryGetValue(ns, out var files))
            {
                foreach (var file in files)
                {
                    if (file.Elements.FirstOrDefault(e => e.Kind == MyBatisStatementKind.Sql && string.Equals(e.Id, id, StringComparison.Ordinal)) is { } frag)
                    {
                        return new MyBatisFragment(id, ns, frag.Text, file.Path);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 문장(select·insert·update·delete) 찾기. <paramref name="exact"/> 면 <c>namespace.id</c> 또는 <c>id</c> 와 똑같은 것만,
    /// 아니면 <c>namespace.id</c> 안에 <paramref name="query"/> 가 든 것(대소문자 무시). 결과는 경로·원문 순.
    /// </summary>
    public IReadOnlyList<MyBatisStatementLocation> FindStatements(string query, bool exact)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = query.Trim();
        if (q.Length == 0)
        {
            return [];
        }

        var found = new List<MyBatisStatementLocation>();
        foreach (var file in Files)
        {
            foreach (var e in file.Elements)
            {
                if (!e.IsStatement || e.Id is not { } id)
                {
                    continue;
                }

                var qualified = file.Namespace is null ? id : file.Namespace + "." + id;
                bool hit = exact
                    ? string.Equals(q, qualified, StringComparison.Ordinal) || string.Equals(q, id, StringComparison.Ordinal)
                    : qualified.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    found.Add(new MyBatisStatementLocation(file.Path, file.Namespace, id, "folder"));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// <paramref name="ns"/> 매퍼의 sql 조각(<paramref name="kind"/> = Sql) 또는 resultMap(ResultMap) <paramref name="id"/> 를 쓰는 곳.
    /// sql 조각 = <c>&lt;include refid&gt;</c>, resultMap = <c>resultMap</c>·<c>extends</c> 속성. 이름에 namespace 가 없으면 같은 매퍼 안에서만 센다.
    /// </summary>
    public IReadOnlyList<MapperFolderUsage> FindUsages(string? ns, string id, MyBatisStatementKind kind)
    {
        ArgumentNullException.ThrowIfNull(id);
        var pattern = kind switch
        {
            MyBatisStatementKind.Sql => IncludeRef(),
            MyBatisStatementKind.ResultMap => ResultMapRef(),
            _ => null,
        };
        if (pattern is null)
        {
            return [];
        }

        var qualified = ns is null ? null : ns + "." + id;
        var found = new List<MapperFolderUsage>();
        foreach (var file in Files)
        {
            bool sameMapper = string.Equals(file.Namespace, ns, StringComparison.Ordinal);
            foreach (var e in file.Elements)
            {
                int count = 0;
                foreach (Match m in pattern.Matches(e.Text))
                {
                    // resultMap 속성은 쉼표로 여럿을 적을 수 있다(중첩 결과 집합).
                    foreach (var part in m.Groups["r"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (string.Equals(part, qualified, StringComparison.Ordinal) || (sameMapper && string.Equals(part, id, StringComparison.Ordinal)))
                        {
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    found.Add(new MapperFolderUsage(file.Path, file.Namespace, e.Id ?? e.Name, count));
                }
            }
        }

        return found;
    }

    /// <summary>경로 비교(리눅스만 대소문자 구분).</summary>
    internal static StringComparer PathComparer { get; } = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static bool SameContent(MapperFolderFile a, MapperFolderFile b) =>
        string.Equals(a.Namespace, b.Namespace, StringComparison.Ordinal) && a.Elements.SequenceEqual(b.Elements);

    private static bool IsExcluded(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Take(segments.Length - 1).Any(d => d.StartsWith('.') || ExcludedFolders.Contains(d, StringComparer.OrdinalIgnoreCase));
    }

    // ns.id 는 마지막 점에서 나눈다. 점이 없으면 지금 매퍼 namespace 안의 id.
    private static IEnumerable<(string Ns, string Id)> Candidates(string refid, string? currentNamespace)
    {
        int dot = refid.LastIndexOf('.');
        if (dot > 0 && dot < refid.Length - 1)
        {
            yield return (refid[..dot], refid[(dot + 1)..]);
        }
        else if (dot < 0 && currentNamespace is not null)
        {
            yield return (currentNamespace, refid);
        }
    }

    private static List<string> EnumerateXml(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        try
        {
            return Directory.EnumerateFiles(root, "*.xml", options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>앞부분에서 본 루트 요소 이름. xml 선언·주석·DOCTYPE 은 건너뛴다. 정하지 못하면 null.</summary>
    internal static string? SniffRoot(ReadOnlySpan<byte> bytes)
    {
        var head = bytes[..Math.Min(bytes.Length, SniffBytes)];
        string text;
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
        {
            text = (head[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(head[2..]);
        }
        else
        {
            text = Encoding.Latin1.GetString(head); // 태그 이름은 ASCII 라 utf-8·euc-kr 모두 이대로 읽힌다
        }

        var m = RootTag().Match(text);
        return m.Success ? m.Groups["n"].Value : null;
    }

    // 선언(<?...?>)·주석(<!--...-->)·DOCTYPE(<!...>)을 건너뛴 첫 여는 태그.
    [GeneratedRegex(@"\A(?:\s|﻿|ï»¿|<\?.*?\?>|<!--.*?-->|<![^>]*>)*<(?<n>[A-Za-z_][\w.\-:]*)", RegexOptions.Singleline)]
    private static partial Regex RootTag();

    [GeneratedRegex("""<include\b[^>]*\brefid\s*=\s*(["'])(?<r>[^"']*)\1""")]
    private static partial Regex IncludeRef();

    [GeneratedRegex("""\b(?:resultMap|extends)\s*=\s*(["'])(?<r>[^"']*)\1""")]
    private static partial Regex ResultMapRef();
}
