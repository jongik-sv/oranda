using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>
/// 드라이버·라우터가 내는 오류 코드 중 사용자가 취할 행동이 정해진 것은 그 행동을 알리는 글로 바꾼다. 나머지는 드라이버가 쓴 글을 그대로 보인다.
/// 코드 목록: 라우터 <c>profile_changed</c>·<c>settings_reset</c>·<c>driver_none</c>·<c>profile_*</c>·<c>api_driver_pending</c>,
/// CLI 드라이버 <c>resume_route_mismatch</c>·<c>session_record_failed</c>·<c>session_record_unreadable</c>,
/// API 드라이버 <c>resume_unknown</c>·<c>resume_busy</c>·<c>context_limit</c>·<c>turn_limit</c>·<c>structured_output_invalid</c>·<c>content_filtered</c>·<c>api_error</c>.
/// 다시 시작하라는 안내는 대화 창의 「새 대화」 단추와 「AI 튜닝」 둘 다를 가리킨다(일반 대화에는 튜닝이 없다).
/// </summary>
public static partial class AiErrorText
{
    /// <summary>
    /// 추적 기록에 남길 글에서 비밀로 보이는 것(Bearer·키·토큰·비밀번호 값, 주소의 사용자 정보)을 가린다. 화면에는 쓰지 않는다(화면은 고정 글).
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clean = SqlQuoted().Replace(text, "'***'"); // SQL 문자열 리터럴(실제 값)은 따옴표 안을 먼저 지운다
        clean = Secrets().Replace(clean, m => m.Groups[1].Value + "=***");
        return UserInfo().Replace(clean, "://***@");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?i)\b(bearer|authorization|api[-_ ]?key|apikey|token|password|passwd|secret)\b\W{0,3}\s*(?:bearer\s+|basic\s+)?[^\s,;]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Secrets();

    [System.Text.RegularExpressions.GeneratedRegex(@"'(?:[^']|'')*'", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SqlQuoted();

    [System.Text.RegularExpressions.GeneratedRegex(@"://[^/@\s]+@", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UserInfo();

    private const string Restart = "대화 창의 「새 대화」 단추로 새 대화를 시작하거나 「AI 튜닝」 으로 다시 시작하세요.";

    /// <summary>다시 시작하라는 안내가 붙는 오류 코드와 글.</summary>
    private static string? ForCode(string? code) => code switch
    {
        "profile_changed" => "AI 모델의 설정이 바뀌어 요청을 보내지 않았습니다. 이어 말할 수 없으니 " + Restart,
        "resume_unknown" => "이어 말할 앞 대화를 찾지 못했습니다(앱을 다시 시작했거나 모델이 바뀌었습니다). " + Restart,

        // CLI: 앞 대화를 다른 경로(다른 프로필·구독)가 만들었거나, 소속 기록을 읽지·쓰지 못한 경우. 앞 대화를 지금 서버로 보내지 않으려고 드라이버가 막았다.
        "resume_route_mismatch" => "이 대화는 지금 선택한 AI 모델이 시작한 것이 아니어서 이어 말할 수 없습니다. " + Restart,
        "session_record_unreadable" => "이어 말할 대화의 소속을 확인하지 못해 보내지 않았습니다. " + Restart,
        "session_record_failed" => "대화의 소속을 기록하지 못해 중단했습니다. 앱 데이터 폴더의 쓰기 권한을 확인한 뒤 " + Restart,
        "turn_limit" => "한 번의 실행에서 모델 호출 또는 도구 호출 횟수 한도에 도달해 멈췄습니다. 질문을 나눠 다시 요청하거나 " + Restart,
        _ => null,
    };

    /// <summary>
    /// 대화 창에 보일 오류 글(「오류: 」 접두는 호출 쪽이 붙인다). <c>context_limit</c> 은 한도의 종류(입력 토큰, 메시지 수·이미지 크기, 이번 요청의 이미지 총 크기)를
    /// 드라이버 글이 구분해 말하고 끝에 할 일까지 적으므로 그 글을 그대로 쓴다(글이 비었을 때만 아래 일반 글).
    /// </summary>
    public static string Describe(AiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code == "context_limit")
        {
            return string.IsNullOrWhiteSpace(error.Message)
                ? "이 대화(또는 이번 요청)가 입력 한도(토큰·메시지 수·이미지 크기)를 넘었습니다. " + Restart
                : error.Message;
        }

        switch (error.Code)
        {
            // 구조 결과 형식 오류: 메시지에 어긋난 칸의 경로가 실려 있으므로 안내 글 뒤에 그대로 붙인다(경로를 잃지 않게).
            case "structured_output_invalid":
                return "AI 가 보낸 구조 결과가 형식에 맞지 않습니다. 다시 요청하거나 모델을 바꿔 보세요." + (string.IsNullOrWhiteSpace(error.Message) ? string.Empty : " (" + error.Message.Trim() + ")");

            // 서버가 알린 원인을 드라이버 글이 말한다. 글이 비었을 때만 일반 글.
            case "content_filtered":
                return string.IsNullOrWhiteSpace(error.Message) ? "서버가 응답을 콘텐츠 필터로 막았습니다. 질문이나 첨부를 바꿔 다시 시도하세요." : error.Message;
            case "api_error":
                return string.IsNullOrWhiteSpace(error.Message) ? "서버가 오류를 알려 왔습니다. 서버 상태를 확인하세요." : error.Message;
        }

        return ForCode(error.Code) ?? error.Message;
    }
}
