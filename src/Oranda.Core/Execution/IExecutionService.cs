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
    /// 한 문장 실행은 인자로 받은 바인드를 쓰고 이것은 실행 이력(<c>history.saveBindValues</c>)이 값을 남길 때만 읽는다.
    /// </summary>
    public IReadOnlyDictionary<string, BindValue>? Binds { get; init; }

    /// <summary>
    /// 호출한 쪽이 이미 사용자 확인을 받은 문장 텍스트(서수 비교, 치환 변수 처리 뒤 텍스트).
    /// 정책이 Confirm 이고 텍스트가 이 집합에 있으면 <see cref="IExecutionInteraction"/> 을 부르지 않는다.
    /// Deny 는 그대로 거부한다. <see cref="PolicyDecision.RetypeText"/> 가 있는 Confirm(DROP)도 집합에 있으면 통과한다:
    /// 호출한 쪽이 다시 입력을 받는다(설계 6.2.3).
    /// </summary>
    public IReadOnlySet<string>? PreConfirmed { get; init; }

    /// <summary>
    /// 대량 읽기(<see cref="IBulkExecutionService.StreamRowsAsync"/>)가 LOB(CLOB·NCLOB·BLOB) 값을 행과 함께 미리 받을 크기(CLOB·NCLOB 은 글자,
    /// BLOB 은 바이트, PF3). 이보다 짧은 값은 행과 함께 와서 LOB 마다 서버를 다시 부르지 않고, 긴 값은 나머지를 따로 받는다(값은 언제나 끝까지).
    /// null·0 이하(기본) = 미리 받지 않음(위치자만 받고 값은 LOB 마다 따로 읽는 옛 동작). 드라이버가 이 설정을 쓰지 않으면 무시한다.
    /// 다른 실행 경로(<see cref="IExecutionService"/>·결과 그리드)는 읽지 않는다.
    /// </summary>
    public int? LobPrefetchSize { get; init; }

    /// <summary>
    /// 이 실행의 되돌리기 지점 기록 방식(되돌리기 지점 설계 7.5, RD-21). 기본 = 원복 스크립트(운영 접속) + 시점 기록.
    /// LOB 쓰기·데이터 가져오기는 <see cref="Oranda.Core.Rollback.ChangeRecording.PointOnly"/>, 원복 실행·비교 반영은 <see cref="Oranda.Core.Rollback.ChangeRecording.None"/>.
    /// </summary>
    public Oranda.Core.Rollback.ChangeRecording Recording { get; init; } = Oranda.Core.Rollback.ChangeRecording.Normal;
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
