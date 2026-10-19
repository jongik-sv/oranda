using Oranda.Core.Connection;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.Core.Execution;

/// <summary>
/// 모든 SQL 실행의 단일 경로(TRD 4.3, 4.9). 파이프라인: 치환 변수 → 바인드 → 정책 관문 → 드라이버 실행.
/// 구현: Oranda.Oracle(TSK-06-01), 실행 클래스는 internal(TC-05).
/// 이벤트: <c>StatementExecuted</c>, <c>TransactionStateChanged</c>, <c>DbmsOutputReceived</c>, <c>PolicyDecisionMade</c>.
/// </summary>
public interface IExecutionService
{
    /// <param name="s">작업 세션 또는 전용 세션</param>
    /// <param name="stmt">실행할 문장</param>
    /// <param name="binds">바인드 이름(<c>:</c> 제외) → 값</param>
    /// <param name="options">null 이면 <see cref="ExecutionOptions.Default"/>(편집기에서 실행)</param>
    /// <param name="ct">취소. 취소되면 <c>OracleCommand.Cancel()</c> 후 <see cref="ExecutionStatus.Cancelled"/></param>
    Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt,
                                       IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options,
                                       CancellationToken ct);

    /// <summary>문장 단위 순차 실행(EXEC-10). 문장마다 결과 하나를 낸다.</summary>
    IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts,
                                       ScriptErrorMode mode, ExecutionOptions? options, CancellationToken ct);
}

/// <summary>실행 요청의 출처·조건. TRD 6장 시그니처에 덧붙인 매개변수(정책 관문과 이력에 필요).</summary>
public sealed record ExecutionOptions
{
    public static ExecutionOptions Default { get; } = new();

    /// <summary>요청한 기능. 이력 <c>source</c> 열과 정책 판단에 쓴다.</summary>
    public HistorySource Source { get; init; } = HistorySource.Editor;

    /// <summary>카탈로그 쿼리의 <c>license</c> 요구. 프로필 보유 선언보다 높으면 드라이버로 보내지 않는다(SAFE-07).</summary>
    public LicensePack RequiredLicense { get; init; } = LicensePack.None;

    /// <summary>true 면 이력에 남기지 않는다(앱 내부 점검 쿼리 등).</summary>
    public bool SkipHistory { get; init; }

    /// <summary>
    /// 스크립트 실행(<see cref="IExecutionService.ExecuteScriptAsync"/>)의 바인드 값(이름 → 값, 대소문자 무시).
    /// 문장마다 그 문장의 바인드 이름에 해당하는 값만 쓴다. null 이면 바인드 없이 실행한다.
    /// </summary>
    public IReadOnlyDictionary<string, BindValue>? Binds { get; init; }
}

/// <summary>스크립트 오류 처리(EXEC-10). 설정 <c>execution.scriptErrorMode</c> 와 같은 형식.</summary>
public enum ScriptErrorMode
{
    Ask,
    Stop,
    Continue,
}

/// <summary><see cref="ScriptErrorMode.Ask"/> 일 때 사용자의 답.</summary>
public enum ScriptErrorAction
{
    Stop,
    Continue,
    ContinueAll,
}
