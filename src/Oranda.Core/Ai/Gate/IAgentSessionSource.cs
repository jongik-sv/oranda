using Oranda.Core.Sessions;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 에이전트 세션의 출처. 두 가지를 맡는다: (1) 진행 중인 AI 요청 찾기, (2) 조회에 쓸 DB 세션 고르기.
/// 어느 DB 세션을 쓸지(이번 회차는 모니터 세션 재사용, 이후 전용 에이전트 세션)는 이 인터페이스 뒤로 숨긴다 —
/// 사용자 작업 세션과 섞이지 않는다는 약속만 지키면 된다(설계 §5.1-4).
/// 실행 토큰으로 요청을 찾는 일은 <see cref="IAgentRunTokens"/> 가 맡는다.
/// </summary>
public interface IAgentSessionSource
{
    /// <summary>실행 id 로 진행 중인 AI 요청을 찾는다. 없으면 null.</summary>
    AgentSession? Find(string runId);

    /// <summary>
    /// 조회에 쓸 DB 세션. 호출한 쪽이 닫지 않는다(소유는 세션 관리자). 이번 회차는 모니터 세션을 재사용하므로 읽기 전용 트랜잭션을 강제하지 못한다.
    /// 그래서 <c>FOR UPDATE</c>·DB 링크·사용자 함수는 게이트가 문장 분석(<see cref="AgentStatementInfo"/>)으로 미리 거부한다.
    /// 전용 에이전트 세션(<c>SET TRANSACTION READ ONLY</c>)은 다음 회차다.
    /// </summary>
    ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct);
}

/// <summary>
/// 실행 토큰 발급·조회·폐기(조정 결정 2026-10-28). 토큰은 <c>claude -p</c> 기동마다(<c>--resume</c> 포함) 새로 발급하고
/// 한 실행 범위(<see cref="AgentRunScope"/>)에 묶이며 프로세스가 끝나면 폐기한다. 값은 <c>AgentRunToken.Generate()</c>.
/// </summary>
public interface IAgentRunTokens
{
    /// <summary>실행 범위에 묶인 새 토큰을 발급하고, 그 범위의 <see cref="AgentSession"/> 을 등록한다.</summary>
    string Issue(AgentSession session);

    /// <summary>토큰으로 실행을 찾는다. 모르거나 폐기됐으면 null(고정 시간 비교).</summary>
    AgentSession? Resolve(string token);

    /// <summary>토큰을 폐기한다. 그 실행의 세션 등록을 지우고 <see cref="AgentSession.Revoke"/> 를 불러, 이미 연결된 파이프에서도 이후 도구 호출이 거부되게 한다.</summary>
    void Revoke(string token);
}
