using System;
using System.Collections.Generic;
using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 상태 표시줄 띠·연결 창이 보는 「외부 에이전트 연결」 하나(설계서 §3.1). 허가(Grant)·다리의 실제 구현은 레인 A 계약이 머지된 뒤 이 입구에 잇는다.
/// 구현이 등록되지 않으면 띠는 연결을 보이지 않는다.
/// </summary>
public interface IExternalAgentLink
{
    /// <summary>지금 연결. 끊겼거나 켜지 않았으면 null.</summary>
    ExternalAgentLinkState? Current { get; }

    /// <summary>연결·끊김·연장·클라이언트 추가 때 난다. 어느 스레드에서든 날 수 있다.</summary>
    event EventHandler? Changed;

    /// <summary>외부 허가의 도구 호출 하나가 끝날 때(거부 포함) 난다. 어느 스레드에서든 날 수 있다.</summary>
    event EventHandler<ExternalAgentCall>? CallCompleted;

    /// <summary>「끊기」: 허가와 하위 세션을 폐기하고 연결 파일을 지운다.</summary>
    void Disconnect();

    /// <summary>「연장」: 만료를 지금부터 다시 기본 기한(8시간)으로 늘린다.</summary>
    void Extend();
}

/// <summary>연결 상태. <paramref name="ExpiresAt"/> 가 null 이면 앱을 끌 때까지.</summary>
/// <param name="Clients">hello 로 붙은 클라이언트 이름(붙은 순서).</param>
/// <param name="Production">허가에 운영 접속이 들어 있다(띠를 경고색으로, §3.2).</param>
public sealed record ExternalAgentLinkState(IReadOnlyList<string> Clients, DateTimeOffset? ExpiresAt, bool Production = false);

/// <summary>도구 호출 한 건(시각은 띠가 받은 때로 찍는다).</summary>
/// <param name="Size">돌려준 글 길이(자).</param>
public sealed record ExternalAgentCall(string? Client, string Tool, AiToolJudgement Judgement, int Size);
