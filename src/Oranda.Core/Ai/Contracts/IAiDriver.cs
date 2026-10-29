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
public sealed record AiDriverCapabilities(bool Skills, bool Tools, bool Resume, bool LocalOnly, bool Streaming);

/// <summary>한 번의 AI 요청.</summary>
/// <param name="Prompt">사용자 글 또는 슬래시 명령.</param>
/// <param name="Context">편집기·접속 문맥(비밀 없음).</param>
/// <param name="ResumeId">앞 대화를 잇는 id(CLI session_id 등). 새 대화면 null.</param>
/// <param name="OutputSchema">결과를 이 JSON 스키마에 맞추도록 요구한다(튜닝 결과 등). null 이면 자유 글.</param>
/// <param name="Model">모델 이름. null 이면 드라이버 기본값.</param>
/// <param name="Effort">추론 노력(<c>AiSettings.EffortLevels</c> 중 하나). null 이면 설정 값, 그것도 없으면 드라이버 기본값.</param>
/// <param name="Session">
/// 도구 호출의 실행 범위. 호출한 쪽이 만들어 넘기고, 도구를 쓰는 드라이버는 <c>IAgentRunTokens.Issue</c> 로 토큰을 발급해 프로세스(또는 API 반복)가 끝나면 <c>Revoke</c> 한다.
/// 호출한 쪽도 같은 객체로 중간에 <see cref="Oranda.Core.Ai.Gate.AgentSession.Revoke"/> 할 수 있다. 도구를 쓰지 않는 요청은 null.
/// </param>
public sealed record AiRequest(
    string Prompt,
    AiContext Context,
    string? ResumeId = null,
    JsonElement? OutputSchema = null,
    string? Model = null,
    Oranda.Core.Ai.Gate.AgentSession? Session = null,
    string? Effort = null);
