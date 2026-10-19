using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Platform.Import;

/// <summary>
/// tnsnames.ora 별칭 일괄 가져오기(CONN-09). 소유: TSK-03-05.
/// 별칭마다 프로필 1개를 만들고 <c>TnsAdmin</c> 은 파일이 있는 폴더로 둔다. 사용자 이름은 파일에 없으므로 자리 표시 이름을 넣는다.
/// </summary>
[OrandaService(typeof(IConnectionImporter))]
internal sealed class TnsNamesImporter : IConnectionImporter
{
    public string SourceName => "tnsnames.ora";

    public IReadOnlyList<string> FileExtensions { get; } = [".ora"];

    public Task<IReadOnlyList<ImportedConnection>> ImportAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        return Task.FromResult(Parse(ImportHelpers.ReadText(path), folder));
    }

    /// <summary>본문의 별칭 목록을 프로필로 바꾼다. 별칭 이름이 같은 항목은 먼저 나온 것만 쓴다.</summary>
    public static IReadOnlyList<ImportedConnection> Parse(string text, string? tnsAdmin)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<ImportedConnection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in Aliases(Strip(text)))
        {
            if (seen.Add(alias))
            {
                var profile = new ConnectionProfile(alias, ImportHelpers.PlaceholderUser, alias, TnsAdmin: tnsAdmin);
                result.Add(new ImportedConnection(profile, "사용자 이름은 파일에 없어 자리 표시 이름을 넣었다."));
            }
        }

        return result;
    }

    private static string Strip(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            sb.Append(hash >= 0 ? line[..hash] : line).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>괄호 깊이 0 에서 줄 머리에 있는 「이름[,이름] =」 만 별칭으로 본다. 항목 본문은 괄호가 균형을 이룰 때까지 건너뛴다.</summary>
    private static List<string> Aliases(string text)
    {
        var names = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var eol = text.IndexOf('\n', i);
            if (eol < 0)
            {
                eol = text.Length;
            }

            var line = text[i..eol];
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            var atMargin = line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '(';
            if (!atMargin || eq <= 0 || line.StartsWith("ifile", StringComparison.OrdinalIgnoreCase))
            {
                i = eol + 1;
                continue;
            }

            names.AddRange(line[..eq].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

            var depth = 0;
            var opened = false;
            var j = i + eq + 1;
            for (; j < text.Length; j++)
            {
                if (text[j] == '(')
                {
                    depth++;
                    opened = true;
                }
                else if (text[j] == ')')
                {
                    depth--;
                }

                if (opened && depth <= 0)
                {
                    break;
                }
            }

            i = j + 1;
        }

        return names;
    }
}
