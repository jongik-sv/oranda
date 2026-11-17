using Oranda.Core.Execution;

namespace Oranda.Core.History;

/// <summary>실행 요청 출처. 이력 <c>source</c> 열 값이자 <c>ExecutionOptions.Source</c>.</summary>
public enum HistorySource
{
    Editor,
    Script,
    GridEdit,
    SchemaBrowser,
    Monitor,
    Extension,

    /// <summary>AI 에이전트(<c>AgentGate</c>)가 요청한 실행. 정책 규칙 0 이 Query 외 문장을 거부한다.</summary>
    Agent,

    /// <summary>접속 간 데이터 복사(<c>IBulkExecutionService</c>)가 요청한 실행.</summary>
    DataCopy,

    /// <summary>테이블 편집기(객체 상세 편집·새 테이블)의 실행(테이블 편집기 설계 11장). 반영 묶음에 자동으로 들어간다(9.3).</summary>
    TableEditor,

    /// <summary>반영 묶음 「실행…」. 묶음에 다시 넣지 않는다(설계 9.3, TE-16). 실행 표시는 묶음 실행기가 직접 남긴다.</summary>
    ChangeBundle,
}

/// <summary>이력 <c>status</c> 열. <see cref="ExecutionStatus"/> 와 같은 이름.</summary>
public enum HistoryStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Denied,
}

/// <summary><c>oranda.db</c> <c>history</c> 행(data-model 3.2). <see cref="SqlText"/> 는 비밀 가림 후 본문.</summary>
public sealed record HistoryEntry
{
    public long Id { get; init; }

    public Guid ProfileId { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public string? DbKey { get; init; }

    public DateTimeOffset ExecutedAt { get; init; }

    public string SqlText { get; init; } = string.Empty;

    public bool SqlTruncated { get; init; }

    public StatementKind StatementKind { get; init; }

    public HistorySource Source { get; init; }

    public HistoryStatus Status { get; init; }

    public long? ElapsedMs { get; init; }

    public long? RowsAffected { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? BindsJson { get; init; }
}
