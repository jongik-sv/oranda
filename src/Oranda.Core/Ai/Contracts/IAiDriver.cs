using System.Text.Json;

namespace Oranda.Core.Ai.Contracts;

/// <summary>
/// 제공자와 무관한 AI 한 번의 요청·응답 흐름(설계 §2.3). 화면(대화 창·제안 막대)은 <see cref="AiEvent"/> 만 본다.
/// 도구 호출은 드라이버가 아니라 <c>AgentGate</c> 를 지나므로, 드라이버는 화면에 보일 사건만 넘긴다.
/// </summary>
public interface IAiDriver
{
    AiDriverCapabilities Capabilities { get; }

    /// <summary>요청을 실행하고 사건을 차례로 낸다. 취소하면 <see cref="OperationCanceledException"/> 으로 끝난다. 실패는 <see cref="AiError"/> 사건으로 낸다.</summary>
    IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, CancellationToken ct);
}

/// <summary>드라이버가 할 수 있는 일. 화면은 이 값으로 입력 칸·단추를 켜고 끈다.</summary>
/// <param name="Skills">도메인 스킬(프로젝트 스킬)을 실행할 수 있다.</param>
/// <param name="Tools">Oranda 도구(<c>describe_object</c> 등)를 부를 수 있다. false 면 미리 묶은 문맥을 프롬프트에 넣는다.</param>
/// <param name="Resume">앞 대화를 <see cref="AiRequest.ResumeId"/> 로 이을 수 있다.</param>
/// <param name="LocalOnly">데이터가 이 PC 밖으로 나가지 않는다(로컬 모델).</param>
/// <param name="Streaming">응답 글을 조각(<see cref="AiTextDelta"/>)으로 낸다.</param>
/// <param name="Images">요청에 이미지(<see cref="AiRequest.Images"/>)를 실을 수 있다. 동기 속성이라 활성 프로필별로 답하지 못하므로 라우터는 고정 true 를 내고, 프로필별 지원 여부는 <c>AiLlmProfile.SupportsImages</c> 로 판단한다.</param>
public sealed record AiDriverCapabilities(bool Skills, bool Tools, bool Resume, bool LocalOnly, bool Streaming, bool Images = false);

/// <summary>대화에 붙이는 이미지 한 장. 이미지 안의 값은 가릴 수 없으므로 보내기 전에 호출한 쪽이 사용자 확인을 거친다.</summary>
/// <param name="MediaType"><c>image/png</c>·<c>image/jpeg</c>·<c>image/webp</c>·<c>image/gif</c> 중 하나.</param>
/// <param name="Data">인코딩된 이미지 바이트(base64 가 아닌 원본).</param>
public sealed record AiImageAttachment(string MediaType, byte[] Data);

/// <summary>한 번의 AI 요청.</summary>
/// <param name="Prompt">사용자 글 또는 슬래시 명령.</param>
/// <param name="Context">편집기·접속 문맥(비밀 없음).</param>
/// <param name="ResumeId">앞 대화를 잇는 id(CLI session_id 등). 새 대화면 null.</param>
/// <param name="OutputSchema">결과를 이 JSON 스키마에 맞추도록 요구한다(튜닝 결과 등). null 이면 자유 글.</param>
/// <param name="Model">모델 이름. null 이면 드라이버 기본값.</param>
/// <param name="Effort">추론 노력(<c>AiSettings.EffortLevels</c> 중 하나). null 이면 설정 값, 그것도 없으면 드라이버 기본값.</param>
/// <param name="Session">
/// 도구 호출의 실행 범위. 호출한 쪽이 만들어 넘긴다. CLI 경로의 드라이버는 <c>IAgentRunTokens.Issue</c> 로 토큰을 발급해 프로세스가 끝나면 <c>Revoke</c> 한다.
/// API 경로의 드라이버는 프로세스 안에서 <c>IAgentGate</c> 를 직접 부르므로 토큰과 파이프가 없고, 호출 전마다 <see cref="Oranda.Core.Ai.Gate.AgentSession.IsRevoked"/> 를 확인한다.
/// 호출한 쪽도 같은 객체로 중간에 <see cref="Oranda.Core.Ai.Gate.AgentSession.Revoke"/> 할 수 있다. 도구를 쓰지 않는 요청은 null.
/// </param>
/// <param name="Images">함께 보낼 이미지. null 이거나 비어 있으면 글만 보낸다.</param>
/// <param name="Expected">
/// 호출한 쪽이 화면에서 확인한 활성 프로필. 실행 때 설정의 활성 프로필이 이와 다르면 라우터가 <c>profile_changed</c> 오류로 끝낸다(사용자가 경고 창에서 본 곳과 실제 전송처가 달라지지 않게).
/// null 이면 검사하지 않는다.
/// </param>
public sealed record AiRequest(
    string Prompt,
    AiContext Context,
    string? ResumeId = null,
    JsonElement? OutputSchema = null,
    string? Model = null,
    Oranda.Core.Ai.Gate.AgentSession? Session = null,
    string? Effort = null,
    IReadOnlyList<AiImageAttachment>? Images = null,
    AiProfileExpectation? Expected = null);

/// <summary>
/// 호출한 쪽이 기대하는 활성 프로필. <paramref name="ProfileId"/> 가 null 이면 시스템 CLI 의 Claude Code 를 기대한다.
/// <paramref name="BaseUrl"/>·<paramref name="Format"/> 을 채우면 같은 프로필의 주소·형식이 확인 뒤에 바뀐 경우도 막는다(경고 창에 적힌 곳과 실제 전송처가 달라지지 않게).
/// 이 두 칸은 프로필을 기대할 때만 뜻이 있고, null 이면 비교하지 않는다.
/// <paramref name="SystemCliId"/> 는 프로필이 아닌 시스템 CLI 를 기대할 때의 id 다(null = Claude Code). <paramref name="ProfileId"/> 가 있으면 보지 않는다.
/// </summary>
public sealed record AiProfileExpectation(Guid? ProfileId, string? BaseUrl = null, Oranda.Core.Settings.AiApiFormat? Format = null, string? SystemCliId = null);
