using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Core.Ai.Setup;

/// <summary>
/// 진단 글에 넣을 값. 비밀이 들어올 칸을 두지 않는다(열쇠·주소·접속 이름·계정 없음).
/// 글 칸(판·OS)도 만들 때 판 모양·짧은 글자만 남기고 걸러 낸다.
/// </summary>
/// <param name="AppVersion">Oranda 판.</param>
/// <param name="OsDescription">운영체제 이름(<c>RuntimeInformation.OSDescription</c> 등).</param>
/// <param name="Path">고른 갈래. 고르기 전 null.</param>
/// <param name="ClaudeFound">claude 를 찾았다.</param>
/// <param name="ClaudeVersion">claude 판. 모르면 null.</param>
/// <param name="ErrorKind">실패 종류. 없으면 null.</param>
/// <param name="At">시각.</param>
public sealed record AiSetupDiagnosticInfo(
    string? AppVersion,
    string? OsDescription,
    AiSetupPath? Path,
    bool ClaudeFound,
    string? ClaudeVersion,
    AiSetupErrorKind? ErrorKind,
    DateTimeOffset At);

/// <summary>
/// 연결 시험 실패 때 복사할 진단 글과 「담당자에게 묻기」 글. 각 단계는 이 두 함수만 쓴다.
/// 비밀번호·열쇠·접속 주소·DB 별칭·사용자 이름을 넣지 않는다.
/// </summary>
public static partial class AiSetupDiagnostics
{
    private const int MaxOsLength = 60;

    /// <summary>연결 시험 실패용 진단 글(여러 줄).</summary>
    public static string ForTestFailure(AiSetupDiagnosticInfo info, bool english)
    {
        ArgumentNullException.ThrowIfNull(info);
        var b = new StringBuilder();
        b.AppendLine(english ? "[Oranda AI connection check]" : "[Oranda AI 연결 진단]");
        b.AppendLine(CultureInfo.InvariantCulture, $"Oranda: {VersionOf(info.AppVersion)}");
        b.AppendLine(CultureInfo.InvariantCulture, $"OS: {OsOf(info.OsDescription)}");
        b.AppendLine(CultureInfo.InvariantCulture, $"{(english ? "Way" : "방법")}: {PathText(info.Path, english)}");
        b.AppendLine(CultureInfo.InvariantCulture, $"Claude Code: {ClaudeText(info, english)}");
        b.AppendLine(CultureInfo.InvariantCulture, $"{(english ? "Error" : "오류")}: {(info.ErrorKind is { } kind ? kind.ToString() : "-")}");
        b.Append(CultureInfo.InvariantCulture, $"{(english ? "Time" : "시각")}: {info.At.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        return b.ToString();
    }

    /// <summary>담당자에게 보낼 메신저 글(여러 줄): 무엇을 받으면 되는지 한 줄 + Oranda 판·OS·claude 설치 여부.</summary>
    public static string ForAdmin(AiSetupDiagnosticInfo info, bool english)
    {
        ArgumentNullException.ThrowIfNull(info);
        var b = new StringBuilder();
        b.AppendLine(english
            ? "Hello. I want to connect AI in Oranda. Should I use a company Claude account, or a company AI server file (.oranda-ai)? Please send me the account or the file."
            : "안녕하세요. Oranda 에서 AI 를 연결하려고 합니다. 회사 Claude 계정과 회사 AI 서버 설정 파일(.oranda-ai) 중 무엇을 쓰면 되는지 알려 주시고, 계정이나 파일을 보내 주세요.");
        b.AppendLine(CultureInfo.InvariantCulture, $"Oranda: {VersionOf(info.AppVersion)}");
        b.AppendLine(CultureInfo.InvariantCulture, $"OS: {OsOf(info.OsDescription)}");
        b.Append(CultureInfo.InvariantCulture, $"Claude Code: {ClaudeText(info, english)}");
        return b.ToString();
    }

    private static string PathText(AiSetupPath? path, bool english) => path switch
    {
        AiSetupPath.ClaudeAccount => english ? "Company Claude account" : "회사 Claude 계정",
        AiSetupPath.CompanyServer => english ? "Company AI server" : "회사 AI 서버",
        AiSetupPath.NotSure => english ? "Not sure" : "잘 모르겠어요",
        _ => "-",
    };

    private static string ClaudeText(AiSetupDiagnosticInfo info, bool english)
    {
        if (!info.ClaudeFound)
        {
            return english ? "not installed" : "설치 안 됨";
        }

        var version = VersionOf(info.ClaudeVersion);
        return english ? $"installed ({version})" : $"설치됨 ({version})";
    }

    // 판 글에서 숫자.숫자(.숫자) 모양만 남긴다. 그 밖의 글(주소·열쇠)이 따라 나가지 않게 한다.
    private static string VersionOf(string? text)
    {
        var match = text is null ? Match.Empty : VersionPattern().Match(text);
        return match.Success ? match.Value : "-";
    }

    // OS 이름은 주소·메일(:/@ 가 든 낱말)을 빼고 글자·숫자·공백·. ( ) - _ 만 남겨 짧게 자른다.
    private static string OsOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "-";
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.IndexOfAny([':', '/', '@', '\\', '=']) < 0)
            .Select(w => new string([.. w.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '(' or ')' or '-' or '_')]))
            .Where(w => w.Length > 0);
        var joined = string.Join(' ', words);
        if (joined.Length > MaxOsLength)
        {
            joined = joined[..MaxOsLength];
        }

        return joined.Length == 0 ? "-" : joined;
    }

    [GeneratedRegex(@"(?<![\d.])\d{1,4}\.\d{1,4}(\.\d{1,6})?(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
