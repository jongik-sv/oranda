using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.Core.Monitoring;

/// <summary>
/// 세션·SQL 모니터(TRD 4.8). 전용 세션(<see cref="SessionPurpose.Monitor"/>)에서 주기 조회한다. RAC 면 gv$.
/// 쿼리는 카탈로그 <c>session.*</c>·<c>sql.*</c>. Kill 은 실행 엔진(정책 관문 「위험 명령」)을 거친다.
/// 구현: Oranda.Oracle <c>OracleMonitorService</c>(partial — 세션 부분 TSK-09-01, Top SQL 부분 TSK-09-02).
/// TRD 6장의 네 메서드 외 나머지는 TSK-02-03 이 덧붙였다.
/// </summary>
public interface IMonitorService
{
    /// <summary>세션 목록. 델타(SESS-06)는 이전 결과와 비교해 <see cref="SessionRow.Deltas"/> 에 채운다. TSK-09-01.</summary>
    Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct);

    /// <summary>블로킹 트리. TSK-09-01.</summary>
    Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct);

    /// <summary>세션 상세(현재·직전 SQL, 트랜잭션, 잠금 객체, 통계). TSK-09-01.</summary>
    Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct);

    /// <summary><c>ALTER SYSTEM KILL SESSION</c>(또는 DISCONNECT). TSK-09-01.</summary>
    Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct);

    /// <summary>DBMS_MONITOR 트레이스 켜기/끄기. 켜면 트레이스 파일 경로(v$diag_info)를 돌려준다. TSK-09-01.</summary>
    Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct);

    /// <summary>Top SQL. TSK-09-02.</summary>
    Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct);

    /// <summary>SQL 전문·바인드 캡처. TSK-09-02.</summary>
    Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct);
}

/// <summary>세션 식별. 비 RAC 는 <see cref="InstanceId"/> = 1.</summary>
public readonly record struct SessionKey(int InstanceId, long Sid, long Serial);

public sealed record SessionFilter
{
    public string? Username { get; init; }

    public bool ActiveOnly { get; init; }

    public bool IncludeBackground { get; init; }

    /// <summary>사용자·머신·프로그램·모듈·SQL ID 에 대한 부분 문자열.</summary>
    public string? Text { get; init; }
}

public sealed record SessionRow(SessionKey Key, string Status)
{
    public string? Username { get; init; }

    public string? OsUser { get; init; }

    public string? Machine { get; init; }

    public string? Program { get; init; }

    public string? Module { get; init; }

    public string? Action { get; init; }

    public string? ClientIdentifier { get; init; }

    public string? SqlId { get; init; }

    public string? PrevSqlId { get; init; }

    public string? Event { get; init; }

    public string? WaitClass { get; init; }

    public long? SecondsInWait { get; init; }

    public long? LastCallElapsedSeconds { get; init; }

    public SessionKey? BlockingSession { get; init; }

    public DateTimeOffset? LogonTime { get; init; }

    /// <summary>통계 이름 → 직전 조회 대비 증가량.</summary>
    public IReadOnlyDictionary<string, long> Deltas { get; init; } = new Dictionary<string, long>();
}

public sealed record BlockingNode(SessionKey Session, IReadOnlyList<BlockingNode> Waiters)
{
    public string? Username { get; init; }

    public string? Event { get; init; }

    public long? SecondsInWait { get; init; }

    public string? SqlId { get; init; }

    /// <summary>잠긴 객체(<c>OWNER.NAME</c>).</summary>
    public string? LockedObject { get; init; }
}

/// <summary>최종 블로커를 뿌리로 하는 트리 목록.</summary>
public sealed record BlockingTree(IReadOnlyList<BlockingNode> Roots);

public sealed record SessionDetail(SessionRow Session)
{
    public string? CurrentSqlText { get; init; }

    public string? PreviousSqlText { get; init; }

    public CursorRef? CurrentCursor { get; init; }

    public IReadOnlyList<Metadata.DetailSection> Sections { get; init; } = [];
}

public sealed record TraceOptions
{
    public bool Waits { get; init; } = true;

    public bool Binds { get; init; }
}

public enum TopSqlOrder
{
    ElapsedTime,
    CpuTime,
    BufferGets,
    DiskReads,
    Executions,
    ElapsedPerExecution,
}

/// <summary>Top SQL 조건(SQLM). 범위 조건은 null 이면 쓰지 않는다.</summary>
public sealed record TopSqlCriteria
{
    public TopSqlOrder OrderBy { get; init; } = TopSqlOrder.ElapsedTime;

    public int Top { get; init; } = 50;

    public double? MinElapsedSeconds { get; init; }

    public long? MinBufferGets { get; init; }

    public long? MinExecutions { get; init; }

    public string? TextContains { get; init; }

    public string? ParsingSchema { get; init; }

    public TimeSpan? ActiveWithin { get; init; }
}

public sealed record TopSqlRow(CursorRef Cursor, string SqlTextPreview)
{
    public long Executions { get; init; }

    public double ElapsedSeconds { get; init; }

    public double CpuSeconds { get; init; }

    public long BufferGets { get; init; }

    public long DiskReads { get; init; }

    public long RowsProcessed { get; init; }

    public string? ParsingSchema { get; init; }

    public string? Module { get; init; }

    public DateTimeOffset? LastActiveTime { get; init; }
}

public sealed record CapturedBind(string Name, int Position, string? DataType, string? Value)
{
    public DateTimeOffset? CapturedAt { get; init; }
}

public sealed record SqlDetail(CursorRef Cursor, string FullText)
{
    public IReadOnlyList<CapturedBind> Binds { get; init; } = [];
}
