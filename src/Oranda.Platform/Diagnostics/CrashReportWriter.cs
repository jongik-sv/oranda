using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Platform.Diagnostics;

/// <summary>충돌 시 로컬 보고서 저장. 소유: TSK-10-01. 네트워크는 쓰지 않는다(전송은 CrashReportSender, 동의 후).</summary>
public sealed partial class CrashReportWriter
{
    /// <summary>보관할 보고서 최대 개수. 넘으면 오래된 것부터 지운다.</summary>
    public const int MaxReports = 20;

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;

    public CrashReportWriter(string directory, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>예외와 환경 정보를 텍스트 파일로 저장하고 경로를 돌려 준다. 비밀번호 같은 값은 가린다.</summary>
    public string Write(Exception exception, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Directory.CreateDirectory(_directory);
        var now = _clock();
        var sb = new StringBuilder();
        sb.AppendLine("Oranda crash report");
        sb.Append("time: ").AppendLine(now.ToString("o", CultureInfo.InvariantCulture));
        sb.Append("version: ").AppendLine(appVersion);
        sb.Append("os: ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        sb.Append("runtime: ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        sb.AppendLine();
        sb.AppendLine(Redact(exception.ToString()));

        var baseName = "crash-" + now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(_directory, baseName + ".txt");
        for (var i = 1; File.Exists(path); i++)
        {
            path = Path.Combine(_directory, $"{baseName}-{i}.txt");
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Prune();
        return path;
    }

    /// <summary>저장된 보고서 경로(최신순).</summary>
    public IReadOnlyList<string> List()
        => Directory.Exists(_directory)
            ? [.. Directory.EnumerateFiles(_directory, "crash-*.txt").OrderByDescending(f => f, StringComparer.Ordinal)]
            : [];

    /// <summary>password=, pwd=, secret=, token= 뒤의 값을 가린다.</summary>
    public static string Redact(string text)
        => SecretPattern().Replace(text, "$1=***");

    private void Prune()
    {
        foreach (var old in List().Skip(MaxReports))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // 지우지 못해도 보고서 저장 자체는 성공이다.
            }
        }
    }

    [GeneratedRegex(@"(?i)\b(password|pwd|secret|token)\s*=\s*[^;\s,""']+")]
    private static partial Regex SecretPattern();
}
