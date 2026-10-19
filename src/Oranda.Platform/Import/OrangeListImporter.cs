using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Platform.Import;

/// <summary>
/// Orange 접속 목록(Data/*.lst) 가져오기(CONN-09). 소유: TSK-03-05.
/// 저장소 안에 실제 형식 자료가 없어 두 가지 텍스트 형식을 받는다(결정 참고).
/// (1) INI 구역 — <c>[이름]</c> 아래 <c>User=</c>, <c>Alias=</c>(또는 <c>TNS=</c>·<c>Server=</c>·<c>Host=</c>·<c>DataSource=</c>), <c>Role=</c>(또는 <c>ConnectAs=</c>·<c>Mode=</c>) 키.
/// (2) 구분 줄 — <c>이름;사용자;별칭[;역할[;비밀번호]]</c> (구분자는 탭·<c>;</c>·<c>|</c>·<c>,</c> 중 줄마다 처음 보이는 것).
/// 비밀번호 열·키는 읽지 않고 버린다.
/// </summary>
[OrandaService(typeof(IConnectionImporter))]
internal sealed class OrangeListImporter : IConnectionImporter
{
    private static readonly string[] UserKeys = ["user", "username", "userid", "uid", "id"];
    private static readonly string[] SourceKeys = ["alias", "tns", "tnsname", "server", "host", "datasource", "database", "service", "connectstring", "dbalias"];
    private static readonly string[] RoleKeys = ["role", "connectas", "mode", "privilege"];
    private static readonly char[] Separators = ['\t', ';', '|', ','];
    private static readonly string[] SecretKeys = ["password", "pwd", "pass", "passwd"];

    public string SourceName => "Orange";

    public IReadOnlyList<string> FileExtensions { get; } = [".lst"];

    public Task<IReadOnlyList<ImportedConnection>> ImportAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Parse(ImportHelpers.ReadText(path)));
    }

    public static IReadOnlyList<ImportedConnection> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        return lines.Any(l => l.TrimStart().StartsWith('[') && l.TrimEnd().EndsWith(']')) ? ParseIni(lines) : ParseDelimited(lines);
    }

    private static List<ImportedConnection> ParseIni(string[] lines)
    {
        var result = new List<ImportedConnection>();
        string? name = null;
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (name is not null)
            {
                var hadSecret = keys.Any(p => SecretKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && p.Value.Length > 0);
                if (Build(name, Pick(keys, UserKeys), Pick(keys, SourceKeys), Pick(keys, RoleKeys), hadSecret) is { } c)
                {
                    result.Add(c);
                }
            }

            keys.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                name = line[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                keys[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        Flush();
        return result;
    }

    private static List<ImportedConnection> ParseDelimited(string[] lines)
    {
        var result = new List<ImportedConnection>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            var sep = Separators.FirstOrDefault(c => line.Contains(c, StringComparison.Ordinal));
            if (sep == default)
            {
                continue;
            }

            var cols = line.Split(sep, StringSplitOptions.TrimEntries);
            if (cols.Length < 3)
            {
                continue;
            }

            if (Build(cols[0], cols[1], cols[2], cols.Length > 3 ? cols[3] : null, cols.Length > 4 && cols[4].Length > 0) is { } c)
            {
                result.Add(c);
            }
        }

        return result;
    }

    private static string? Pick(Dictionary<string, string> keys, string[] names)
    {
        foreach (var n in names)
        {
            if (keys.TryGetValue(n, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static ImportedConnection? Build(string name, string? user, string? source, string? role, bool hadSecret)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(user))
        {
            user = ImportHelpers.PlaceholderUser;
            warnings.Add("사용자 이름이 없어 자리 표시 이름을 넣었다.");
        }

        if (hadSecret)
        {
            warnings.Add("비밀번호는 가져오지 않는다. 접속할 때 입력한다.");
        }

        var profile = new ConnectionProfile(name.Trim(), user.Trim(), source.Trim(), ImportHelpers.ParseRole(role));
        return new ImportedConnection(profile, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }
}
