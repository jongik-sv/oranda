using Oranda.Core.Ai.Profiles;

namespace Oranda.Core.Ai.Setup;

/// <summary>도우미가 보이는 실패 종류. 화면 글은 <see cref="AiSetupErrorText.For"/> 하나에서 고른다. 새 값은 맨 끝에 더한다.</summary>
public enum AiSetupErrorKind
{
    /// <summary>claude 를 찾지 못했다(설치 안 됨).</summary>
    CliNotFound,

    /// <summary>claude 는 있지만 로그인하지 않았다.</summary>
    NotLoggedIn,

    /// <summary>인터넷·회사 망에 닿지 못했다.</summary>
    Network,

    /// <summary>정한 시간 안에 답이 오지 않았다.</summary>
    Timeout,

    /// <summary>서버가 요청을 거절했다(열쇠·권한·모델 이름).</summary>
    ServerRejected,

    /// <summary>그 밖.</summary>
    Unknown,
}

/// <summary>AI 드라이버 오류 코드(<c>AiError.Code</c>) 중 도우미가 보는 것과 종류 바꾸기.</summary>
public static class AiSetupErrorCodes
{
    /// <summary>실행 파일을 찾지 못함(이미 있는 코드, <c>CliDriver</c>·<c>SystemCliExecutable</c>).</summary>
    public const string CliNotFound = "cli_not_found";

    /// <summary>claude 가 로그인하지 않은 상태(새 코드, 구현은 <c>CliDriver</c>).</summary>
    public const string CliNotLoggedIn = "cli_not_logged_in";

    /// <summary>오류 코드를 종류로 바꾼다. 도우미가 모르는 코드·null 은 <see cref="AiSetupErrorKind.Unknown"/>.</summary>
    public static AiSetupErrorKind KindFor(string? code) => code switch
    {
        CliNotFound => AiSetupErrorKind.CliNotFound,
        CliNotLoggedIn => AiSetupErrorKind.NotLoggedIn,
        _ => AiSetupErrorKind.Unknown,
    };

    // AiProfileProbe 의 실패 글 머리 → 종류.
    private static readonly (string Head, AiSetupErrorKind Kind)[] ProbeHeads =
    [
        ("서버가 제한 시간 안에 응답하지 않았습니다", AiSetupErrorKind.Timeout),
        ("서버에 연결하지 못했습니다", AiSetupErrorKind.Network),
        ("서버가 인증을 거부했습니다", AiSetupErrorKind.ServerRejected),
        ("서버에서 해당 주소나 모델을 찾지 못했습니다", AiSetupErrorKind.ServerRejected),
        ("서버가 오류를 돌려줬습니다", AiSetupErrorKind.ServerRejected),
        ("API 키에 사용할 수 없는 문자가 있습니다", AiSetupErrorKind.ServerRejected),
    ];

    /// <summary>
    /// 회사 AI 서버 연결 시험 결과(<see cref="IAiProfileProbe.TestConnectionAsync"/>)의 오류 종류. 성공이면 null.
    /// 결과에 종류 칸이 없어 Platform <c>AiProfileProbe</c> 의 실패 글 머리로 가른다(그 글을 바꾸면 이 표와 시험을 같이 바꾼다). 모르는 글 = Unknown.
    /// </summary>
    public static AiSetupErrorKind? KindFor(AiConnectionTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Success)
        {
            return null;
        }

        var message = result.Message ?? "";
        foreach (var (head, kind) in ProbeHeads)
        {
            if (message.StartsWith(head, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return AiSetupErrorKind.Unknown;
    }
}

/// <summary>실패 종류별 쉬운 글(한/영). 기술 낱말을 쓰지 않는다.</summary>
public static class AiSetupErrorText
{
    /// <summary>실패 종류 한 줄.</summary>
    /// <param name="kind">실패 종류.</param>
    /// <param name="english">영어 글이면 true.</param>
    public static string For(AiSetupErrorKind kind, bool english) => kind switch
    {
        AiSetupErrorKind.CliNotFound => english ? "Claude Code is not installed on this PC." : "이 PC 에 Claude Code 가 설치되어 있지 않습니다.",
        AiSetupErrorKind.NotLoggedIn => english ? "You are not signed in to Claude yet." : "아직 Claude 에 로그인하지 않았습니다.",
        AiSetupErrorKind.Network => english ? "Could not reach the internet or the company network." : "인터넷이나 회사 망에 연결되지 않습니다.",
        AiSetupErrorKind.Timeout => english ? "The AI did not answer in time. Try again in a moment." : "AI 가 제시간에 답하지 않았습니다. 잠시 뒤 다시 해 보세요.",
        AiSetupErrorKind.ServerRejected => english ? "The AI server refused the request. Check the key or ask your admin." : "AI 서버가 요청을 거절했습니다. 열쇠를 확인하거나 담당자에게 물어보세요.",
        _ => english ? "Something went wrong. Copy the details and send them to your admin." : "알 수 없는 문제가 생겼습니다. 진단 글을 복사해 담당자에게 보내 주세요.",
    };
}
