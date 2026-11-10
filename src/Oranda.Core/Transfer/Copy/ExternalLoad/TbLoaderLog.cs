using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// tbLoader 로그 읽기(XL2). 설명서의 로그 예:
/// <code>
/// Record 2 was rejected.
///   TBR-80053 : Some relatively positioned columns are not present in the record.
/// 8 Rows were requested to load.
/// 5 Rows were loaded successfully.
/// 3 Rows were failed to load because of some errors
/// </code>
/// 종료 코드는 설명서에 없다: 0 만 「끝까지 돌았다」로 보고, 그 밖은 실패(실행기가 기본 방식으로 다시 넣는다 — 안전한 쪽). win144 실측으로 고친다.
/// </summary>
public static partial class TbLoaderLog
{
    public static bool ExitOk(int exitCode) => exitCode == 0;

    public static LoaderLogResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LoaderLogResult.Empty;
        }

        long? loaded = null;
        long? requested = null;
        long failed = 0;
        var rejects = new List<LoaderRejected>();
        var fatal = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (Loaded().Match(line) is { Success: true } l)
            {
                loaded = long.Parse(l.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (Requested().Match(line) is { Success: true } r)
            {
                requested = long.Parse(r.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (Failed().Match(line) is { Success: true } f)
            {
                failed = long.Parse(f.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (Rejected().Match(line) is { Success: true } rec)
            {
                var error = i + 1 < lines.Length && lines[i + 1].Trim().StartsWith("TBR-", StringComparison.Ordinal) ? lines[i + 1].Trim() : "거절됨";
                if (rejects.Count < SqlLoaderLog.MaxRejects)
                {
                    rejects.Add(new LoaderRejected(long.Parse(rec.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture), error));
                }
            }
            else if (line.StartsWith("TBR-", StringComparison.Ordinal) && (i == 0 || !Rejected().IsMatch(lines[i - 1].Trim())) && fatal.Count < SqlLoaderLog.MaxFatal)
            {
                fatal.Add(line);
            }
        }

        return new LoaderLogResult(loaded, Math.Max(failed, rejects.Count), 0, 0, rejects, fatal, false, [], requested);
    }

    [GeneratedRegex(@"^(\d+) Rows? were loaded successfully", RegexOptions.IgnoreCase)]
    private static partial Regex Loaded();

    [GeneratedRegex(@"^(\d+) Rows? were requested to load", RegexOptions.IgnoreCase)]
    private static partial Regex Requested();

    [GeneratedRegex(@"^(\d+) Rows? were failed to load", RegexOptions.IgnoreCase)]
    private static partial Regex Failed();

    [GeneratedRegex(@"^Record (\d+) was rejected", RegexOptions.IgnoreCase)]
    private static partial Regex Rejected();
}
