using Oranda.Core.Plans;

namespace Oranda.Core.Monitoring;

// Lock 모니터 조회 형(PRD 10장 LK-01~12, TSK-05-01 계약). 조회 TSK-05-02, 화면 TSK-05-03·04.
// 각 필드의 Oracle 출처는 주석에 적는다(gv$ 기준, 비 RAC 는 InstanceId = 1).

/// <summary>잠금 목록 한 줄(LK-02). gv$lock 한 행 + gv$session.</summary>
public sealed record LockRow(SessionKey Session, string LockType)
{
    /// <summary>gv$session.username.</summary>
    public string? Username { get; init; }

    /// <summary>gv$session.machine.</summary>
    public string? Machine { get; init; }

    /// <summary>gv$session.program.</summary>
    public string? Program { get; init; }

    /// <summary>gv$lock.id1 (TM 이면 object_id, TX 면 undo 세그먼트·슬롯 묶음).</summary>
    public long Id1 { get; init; }

    /// <summary>gv$lock.id2 (TX 면 undo 순번).</summary>
    public long Id2 { get; init; }

    /// <summary>gv$lock.lmode 0~6 (0 없음, 3 RX, 4 S, 5 SRX, 6 X). 0 이면 보유하지 않고 기다리기만 함.</summary>
    public int HeldMode { get; init; }

    /// <summary>gv$lock.request 0~6. 0 이 아니면 이 세션은 이 자원을 기다리는 중.</summary>
    public int RequestedMode { get; init; }

    /// <summary>gv$lock.ctime — 현재 모드를 얻은 뒤(또는 대기한 지) 경과 초.</summary>
    public long CtimeSeconds { get; init; }

    /// <summary>gv$lock.block — 1 이면 다른 세션을 막고 있음(RAC 는 2 도 막음으로 본다).</summary>
    public bool IsBlocking { get; init; }

    /// <summary>TM 일 때 id1=object_id 로 찾은 <c>OWNER.NAME</c>(dba_objects 또는 카탈로그 접두 표). TM 이 아니면 null.</summary>
    public string? ObjectName { get; init; }

    /// <summary>TX 일 때 gv$transaction.start_date. 트랜잭션이 없으면 null.</summary>
    public DateTimeOffset? TransactionStart { get; init; }

    /// <summary>TX 일 때 gv$transaction.used_ublk — 사용 중인 undo 블록 수.</summary>
    public long? UndoBlocks { get; init; }

    /// <summary>gv$session.event.</summary>
    public string? Event { get; init; }

    /// <summary>gv$session.seconds_in_wait.</summary>
    public long? SecondsInWait { get; init; }

    /// <summary>gv$session.sql_id (현재 SQL).</summary>
    public string? SqlId { get; init; }

    /// <summary>gv$session.prev_sql_id.</summary>
    public string? PrevSqlId { get; init; }

    /// <summary>gv$session.blocking_instance + blocking_session + 그 세션의 serial#. 막는 세션이 없으면 null.</summary>
    public SessionKey? BlockingSession { get; init; }
}

/// <summary>Lock 목록 거르기(LK-04). 기본값 = 거르지 않음(백그라운드·시스템 잠금은 항상 제외).</summary>
public sealed record LockFilter
{
    /// <summary>최소 대기 초(gv$lock.ctime, 요청 모드가 있는 행 기준). 기본 0.</summary>
    public int MinWaitSeconds { get; init; }

    /// <summary>gv$session.username 정확 일치(대소문자 무시).</summary>
    public string? Username { get; init; }

    /// <summary>잠긴 객체의 owner(스키마). TM 행에만 적용.</summary>
    public string? Schema { get; init; }

    /// <summary>특정 인스턴스만(gv$lock.inst_id). null 이면 전체.</summary>
    public int? InstanceId { get; init; }

    /// <summary>true 면 기다리거나(request &gt; 0) 막는(block &gt; 0) 행만.</summary>
    public bool WaitingOrBlockingOnly { get; init; }

    /// <summary>잠금 유형 목록(gv$lock.type, 예 TM·TX·UL). 비어 있으면 TM·TX·UL 기본.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];
}

/// <summary>Lock 한 번 조회 결과(LK-01·02). <see cref="WaitTree"/> 는 세션 모니터의 <see cref="BlockingTree"/> 를 재사용한다.</summary>
public sealed record LockSnapshot(IReadOnlyList<LockRow> Rows, BlockingTree WaitTree, DateTimeOffset CollectedAt);

/// <summary>보유자 영향(LK-07). 보유자별 gv$session.blocking_* 로 모은 대기자 집계.</summary>
public sealed record BlockerImpact(SessionKey Holder, int WaiterCount, long MaxWaitSeconds, long TotalWaitSeconds);

/// <summary>잠긴 행(LK-06). 대기 세션의 row_wait_obj#·file#·block#·row# → DBMS_ROWID.ROWID_CREATE 로 만든 rowid 와 그 행의 값.</summary>
public sealed record LockedRowInfo(string RowId, string Owner, string Table)
{
    /// <summary>SELECT * FROM owner.table WHERE ROWID = :r 의 열 이름(결과 열 순서).</summary>
    public IReadOnlyList<string> ColumnNames { get; init; } = [];

    /// <summary>열 값 문자열(<see cref="ColumnNames"/> 와 같은 순서, NULL 은 null).</summary>
    public IReadOnlyList<string?> Values { get; init; } = [];
}

/// <summary>DDL·라이브러리 잠금 한 줄(LK-09). dba_ddl_locks.</summary>
public sealed record DdlLockRow(SessionKey Session, string Owner, string Name, string Type)
{
    /// <summary>dba_ddl_locks.mode_held (None·Null·Share·Exclusive).</summary>
    public string? ModeHeld { get; init; }

    /// <summary>dba_ddl_locks.mode_requested.</summary>
    public string? ModeRequested { get; init; }
}

/// <summary>대기 세션 SQL 의 바인드 값(LK-12). gv$sql_bind_capture — 캡처된 값만이라 최신이 아닐 수 있다.</summary>
public sealed record WaitingBind(string Name, int Position, string? DataType, string? Value);

/// <summary>대기 바인드 조회 대상. <see cref="Cursor"/>(sql_id+child) 또는 <see cref="Session"/> 중 하나를 채운다.</summary>
public sealed record WaitingBindTarget
{
    /// <summary>gv$sql_bind_capture.sql_id·child_number 로 직접 찾는다.</summary>
    public CursorRef? Cursor { get; init; }

    /// <summary>세션의 gv$session.sql_id·sql_child_number 로 찾는다(<see cref="Cursor"/> 가 없을 때).</summary>
    public SessionKey? Session { get; init; }
}

/// <summary>방치 트랜잭션(LK-12). gv$transaction 이 있고 gv$session.status = INACTIVE 인 세션.</summary>
public sealed record IdleTransaction(SessionKey Session)
{
    /// <summary>gv$session.username.</summary>
    public string? Username { get; init; }

    /// <summary>gv$session.machine.</summary>
    public string? Machine { get; init; }

    /// <summary>gv$session.program.</summary>
    public string? Program { get; init; }

    /// <summary>gv$transaction.start_date.</summary>
    public DateTimeOffset? TransactionStart { get; init; }

    /// <summary>gv$session.last_call_et — 마지막 호출 뒤 경과 초(INACTIVE 면 방치 시간).</summary>
    public long IdleSeconds { get; init; }

    /// <summary>gv$transaction.used_ublk.</summary>
    public long? UndoBlocks { get; init; }

    /// <summary>이 세션이 막고 있는 대기자 수(gv$session.blocking_session 역집계).</summary>
    public int BlockedCount { get; init; }

    /// <summary>gv$session.prev_sql_id — 방치 직전 마지막 SQL.</summary>
    public string? PrevSqlId { get; init; }
}

/// <summary>데드락 한 건(LK-12, TSK-05-04). 알림 로그(v$diag_alert_ext 의 ORA-00060)에서 읽는다.</summary>
public sealed record DeadlockEvent(DateTimeOffset At, int InstanceId, IReadOnlyList<string> MessageLines);
