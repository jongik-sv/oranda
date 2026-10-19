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

/// <summary>서버 오류(ORA-nnnnn). <see cref="Offset"/> 은 문장 본문 안 0부터 위치.</summary>
public sealed record DbError(string Code, string Message)
{
    /// <summary>ORA 번호(예: 942). 서버 오류가 아니면 0.</summary>
    public int Number { get; init; }

    public int? Offset { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }
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
}
