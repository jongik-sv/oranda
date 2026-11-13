using Oranda.Core.Connection;
using Oranda.Core.Grid;
using Oranda.Core.Plans;

namespace Oranda.Core.Execution;

/// <summary>실행 결과 상태. 이름은 이력 <c>status</c> 열 값과 같다.</summary>
public enum ExecutionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Denied,
}

/// <summary>
/// 서버 오류. <see cref="Code"/> 는 DBMS 원문 코드(<c>ORA-01013</c>·<c>TBR-17001</c>) 또는 앱 자체 오류 코드(<c>ORANDA-*</c>).
/// <see cref="Offset"/> 은 문장 본문 안 0부터 위치. 의미 판단은 번호 대신 <see cref="Kind"/> 로 한다(D-046).
/// </summary>
public sealed record DbError(string Code, string Message)
{
    /// <summary>
    /// DBMS 원문 오류 번호 — 드라이버가 준 값 그대로(부호 포함, 예: ORA-00942 → 942). 서버 오류가 아니면 0.
    /// 사람이 보는 표기는 <see cref="Code"/> 다.
    /// </summary>
    public int Number { get; init; }

    public int? Offset { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    /// <summary><see cref="Code"/>·<see cref="Number"/> 를 낸 DBMS. 앱 자체 오류는 기본값(Oracle) 그대로 둔다.</summary>
    public Dbms Dbms { get; init; } = Dbms.Oracle;

    /// <summary>의미 분류(<see cref="IDbErrorClassifier"/> 가 채움). 채우지 않으면 <see cref="DbErrorKind.Other"/>.</summary>
    public DbErrorKind Kind { get; init; } = DbErrorKind.Other;
}

/// <summary>문장 1개의 실행 결과. 질의면 <see cref="ResultSet"/> 이 있고 호출자가 Dispose 한다.</summary>
public sealed record ExecutionResult(SqlStatement Statement, ExecutionStatus Status)
{
    public TimeSpan Elapsed { get; init; }

    /// <summary>DML 영향 행 수. 질의·DDL 은 null.</summary>
    public long? RowsAffected { get; init; }

    public IResultSet? ResultSet { get; init; }

    public DbError? Error { get; init; }

    /// <summary><see cref="ExecutionStatus.Denied"/> 이거나 확인을 거친 경우의 정책 판단.</summary>
    public PolicyDecision? Policy { get; init; }

    /// <summary>실행 후 DBMS_OUTPUT 에서 거둔 줄.</summary>
    public IReadOnlyList<string> DbmsOutput { get; init; } = [];

    /// <summary>OUT·INOUT·반환값 바인드 결과(이름 → 값). REF CURSOR 는 <see cref="IResultSet"/> 값.</summary>
    public IReadOnlyDictionary<string, object?> OutValues { get; init; } = new Dictionary<string, object?>();

    /// <summary>실행 후 미커밋 트랜잭션 여부.</summary>
    public bool TransactionPending { get; init; }

    /// <summary>실제 계획 조회용 커서 식별(DISPLAY_CURSOR). 모르면 null.</summary>
    public CursorRef? Cursor { get; init; }

    /// <summary>원복 스크립트·시점 기록 요약(되돌리기 지점 설계 6.1 의 8단계). 기록하지 않은 실행은 null.</summary>
    public Oranda.Core.Rollback.RollbackStatementSummary? Rollback { get; init; }
}
