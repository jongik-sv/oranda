namespace Oranda.Core.Connection;

/// <summary>
/// 접속 폴더 규칙. 폴더 경로는 「부모/자식」 형태의 문자열이고 접속의 <see cref="ConnectionProfile.Group"/> 값과 같은 형식이다.
/// 비교는 대소문자를 무시한다.
/// </summary>
public static class ConnectionFolders
{
    public const char Separator = '/';

    /// <summary>새 폴더의 기본 이름.</summary>
    public const string DefaultName = "새 폴더";

    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>각 단계의 앞뒤 공백과 빈 단계를 없앤다. 폴더가 아니면(빈 글자) null.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parts = path.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join(Separator, parts);
    }

    /// <summary>폴더 이름(경로의 마지막 단계)이 쓸 수 있는지: 비어 있지 않고 구분자가 없어야 한다.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !name.Contains(Separator, StringComparison.Ordinal);

    /// <summary>부모 폴더 경로. 맨 위 폴더면 null.</summary>
    public static string? Parent(string path)
    {
        var i = path.LastIndexOf(Separator);
        return i < 0 ? null : path[..i];
    }

    /// <summary>경로의 마지막 단계.</summary>
    public static string NameOf(string path) => path[(path.LastIndexOf(Separator) + 1)..];

    public static string Combine(string? parent, string name) => string.IsNullOrEmpty(parent) ? name : parent + Separator + name;

    public static bool PathEquals(string? a, string? b) => Comparer.Equals(a ?? string.Empty, b ?? string.Empty);

    /// <summary><paramref name="path"/> 가 <paramref name="ancestor"/> 와 같거나 그 아래인지.</summary>
    public static bool IsSameOrUnder(string? path, string ancestor) =>
        path is not null && (PathEquals(path, ancestor) || path.StartsWith(ancestor + Separator, StringComparison.OrdinalIgnoreCase));

    /// <summary>문서에 존재하는 모든 폴더(저장된 목록 + 접속이 가리키는 폴더와 그 상위 폴더), 이름순.</summary>
    public static IReadOnlyList<string> All(ConnectionsDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var set = new Dictionary<string, string>(Comparer);
        foreach (var raw in doc.Folders.Concat(doc.Profiles.Select(p => p.Group ?? string.Empty)))
        {
            for (var path = Normalize(raw); path is not null; path = Parent(path))
            {
                set.TryAdd(path, path);
            }
        }

        return [.. set.Values.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>같은 부모 아래에서 겹치지 않는 새 폴더 이름(「새 폴더」, 「새 폴더 (2)」 …).</summary>
    public static string UniqueName(IEnumerable<string> existing, string? parent, string baseName = DefaultName)
    {
        var taken = new HashSet<string>(existing, Comparer);
        var name = baseName;
        for (var n = 2; taken.Contains(Combine(parent, name)); n++)
        {
            name = $"{baseName} ({n})";
        }

        return name;
    }

    /// <summary>폴더를 만든다. 이미 있으면 그대로.</summary>
    public static ConnectionsDocument Create(ConnectionsDocument doc, string path)
    {
        var normalized = Normalize(path) ?? throw new ConnectionFolderException("폴더 이름이 비어 있다.");
        return All(doc).Contains(normalized, Comparer) ? doc : doc with { Folders = [.. doc.Folders, normalized] };
    }

    /// <summary>
    /// 폴더를 새 경로로 옮기거나 이름을 바꾼다. 안의 접속과 하위 폴더 경로가 함께 바뀐다.
    /// 자기 자신·자기 하위로는 옮길 수 없고, 같은 경로의 폴더가 이미 있으면 거부한다.
    /// </summary>
    public static ConnectionsDocument Move(ConnectionsDocument doc, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var src = Normalize(from) ?? throw new ConnectionFolderException("옮길 폴더가 없다.");
        var dst = Normalize(to) ?? throw new ConnectionFolderException("폴더 이름이 비어 있다.");
        if (PathEquals(src, dst))
        {
            return doc;
        }

        if (IsSameOrUnder(dst, src))
        {
            throw new ConnectionFolderException("폴더를 자기 자신이나 그 하위로 옮길 수 없다.");
        }

        var all = All(doc);
        if (!all.Contains(src, Comparer))
        {
            throw new ConnectionFolderException($"폴더 '{src}' 이(가) 없다.");
        }

        if (all.Contains(dst, Comparer))
        {
            throw new ConnectionFolderException($"폴더 '{dst}' 이(가) 이미 있다.");
        }

        return Rewrite(doc, p => Replace(p, src, dst), includeParentsOfTarget: dst);
    }

    /// <summary>폴더를 지운다. 안의 접속과 하위 폴더는 부모 폴더로 올린다(접속은 지우지 않는다).</summary>
    public static ConnectionsDocument Delete(ConnectionsDocument doc, string path)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var target = Normalize(path) ?? throw new ConnectionFolderException("지울 폴더가 없다.");
        var parent = Parent(target);
        return Rewrite(doc, p => Replace(p, target, parent), includeParentsOfTarget: null);
    }

    /// <summary>접속 하나의 폴더를 바꾼다(<paramref name="folder"/> 가 null 이면 폴더 없음).</summary>
    public static ConnectionsDocument MoveProfile(ConnectionsDocument doc, Guid id, string? folder)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var group = Normalize(folder);
        return doc with { Profiles = [.. doc.Profiles.Select(p => p.Id == id ? p with { Group = group } : p)] };
    }

    /// <summary><paramref name="path"/> 가 <paramref name="from"/> 아래면 <paramref name="to"/> 로 접두어를 바꾼다.</summary>
    private static string? Replace(string? path, string from, string? to)
    {
        if (path is null || !IsSameOrUnder(path, from))
        {
            return path;
        }

        var rest = path[from.Length..];
        return Normalize((to ?? string.Empty) + rest);
    }

    private static ConnectionsDocument Rewrite(ConnectionsDocument doc, Func<string?, string?> map, string? includeParentsOfTarget)
    {
        var folders = new List<string>();
        foreach (var f in doc.Folders)
        {
            if (Normalize(f) is { } n && map(n) is { } mapped)
            {
                folders.Add(mapped);
            }
        }

        if (includeParentsOfTarget is not null)
        {
            folders.Add(includeParentsOfTarget);
        }

        var profiles = doc.Profiles.Select(p => p with { Group = map(Normalize(p.Group)) }).ToList();
        return doc with { Folders = Distinct(folders), Profiles = profiles };
    }

    /// <summary>대소문자를 무시하고 겹침을 없앤다(먼저 나온 철자를 남긴다).</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        [.. paths.Select(Normalize).OfType<string>().Distinct(Comparer)];
}

/// <summary>폴더 작업이 규칙을 어겼다(자기 하위로 옮기기, 이름 겹침 등).</summary>
public sealed class ConnectionFolderException : Exception
{
    public ConnectionFolderException()
    {
    }

    public ConnectionFolderException(string message)
        : base(message)
    {
    }

    public ConnectionFolderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
