using Oranda.App.Features.LockMonitor;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.LockMonitor;

/// <summary>시안 b 와 같은 잠금 상황(RAC 2, 보유자 3·대기 5·잠금 11행)을 돌려주는 가짜 모니터 서비스.</summary>
internal sealed class FakeLockMonitor : IMonitorService
{
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 14, 7, 23, TimeSpan.Zero);

    public static readonly SessionKey H1 = new(1, 1287, 40211);
    public static readonly SessionKey W1 = new(1, 1422, 9001);
    public static readonly SessionKey W2 = new(2, 388, 9002);
    public static readonly SessionKey W3 = new(2, 1903, 9003);
    public static readonly SessionKey H2 = new(2, 644, 7001);
    public static readonly SessionKey W4 = new(1, 912, 7002);
    public static readonly SessionKey H3 = new(1, 211, 5001);
    public static readonly SessionKey W5 = new(1, 533, 5002);

    public int LockCalls { get; private set; }

    public int SqlCalls { get; private set; }

    public int RowCalls { get; private set; }

    public int BindCalls { get; private set; }

    public int DdlCalls { get; private set; }

    public LockFilter? LastFilter { get; private set; }

    public List<(SessionKey Key, bool Immediate)> Killed { get; } = [];

    public Exception? Fail { get; set; }

    public Exception? DdlFail { get; set; }

    /// <summary>true 면 대기도 잠금도 없는 결과.</summary>
    public bool Quiet { get; set; }

    public LockedRowInfo? LockedRow { get; set; } = new("AAAWkqAAHAAAAm/AAC", "MES", "LOT_HIST")
    {
        FileNumber = 7,
        BlockNumber = 2495,
        RowNumber = 2,
        ColumnNames = ["HIST_SEQ", "LOT_ID", "EQP_ID", "EVT_STAT"],
        Values = ["88120931", "L2610240117", "ETCH-07", null],
    };

    public TaskCompletionSource? Hold { get; set; }

    /// <summary>있으면 이 결과를 그대로 돌려준다(순환·임계치 시험).</summary>
    public LockSnapshot? Custom { get; set; }

    public async Task<LockSnapshot> GetLocksAsync(ISession monitor, LockFilter filter, CancellationToken ct)
    {
        LockCalls++;
        LastFilter = filter;
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        if (Fail is { } ex)
        {
            throw ex;
        }

        if (Custom is { } custom)
        {
            return custom;
        }

        return Quiet ? new LockSnapshot([], new BlockingTree([]), Now) : new LockSnapshot(Rows(), Tree(), Now);
    }

    public Task<IReadOnlyList<BlockerImpact>> GetBlockerImpactAsync(ISession monitor, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BlockerImpact>>(Quiet ? [] :
        [
            new(H1, 3, 287, 631) { Status = "INACTIVE" },
            new(H2, 1, 46, 46) { Status = "INACTIVE" },
            new(H3, 1, 8, 8) { Status = "ACTIVE" },
        ]);

    public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct)
    {
        SqlCalls++;
        var text = cursor.SqlId switch
        {
            "4w1xq9n2b7c0d" => "UPDATE MES.LOT_HIST\n   SET EVT_STAT = 'DONE', UPD_TIME = SYSDATE\n WHERE HIST_SEQ = :1",
            "7h35uxf5uhmm1" => "UPDATE MES.LOT_HIST\n   SET EVT_STAT = 'RUN', EQP_ID = :2\n WHERE HIST_SEQ = :1",
            _ => "SELECT 1 FROM dual",
        };
        return Task.FromResult(new SqlDetail(cursor, text));
    }

    public Task<LockedRowInfo?> GetLockedRowAsync(ISession monitor, SessionKey waiter, CancellationToken ct)
    {
        RowCalls++;
        return Task.FromResult(LockedRow);
    }

    public Task<IReadOnlyList<WaitingBind>> GetWaitingBindsAsync(ISession monitor, WaitingBindTarget target, CancellationToken ct)
    {
        BindCalls++;
        return Task.FromResult<IReadOnlyList<WaitingBind>>(
        [
            new(":1", 1, "NUMBER", "88120931") { CapturedAt = Now.AddSeconds(-282) },
            new(":2", 2, "VARCHAR2(32)", "ETCH-09") { CapturedAt = Now.AddSeconds(-282) },
        ]);
    }

    public int IdleCalls { get; private set; }

    public int DeadlockCalls { get; private set; }

    public int? LastMinIdle { get; private set; }

    /// <summary>시안 d 의 방치 트랜잭션 4줄.</summary>
    public IReadOnlyList<IdleTransaction> Idle { get; set; } =
    [
        new(H2) { Username = "SCOTT", Machine = "DESKTOP-K2J9", Program = "SQL Developer", IdleSeconds = 1018, TransactionStart = Now.AddSeconds(-1020), UndoBlocks = 1, LockCount = 2, BlockedCount = 1, PrevSqlId = "1m8c2v0d9s4tq" },
        new(H1) { Username = "MES_APP", Machine = "was-mes01", Program = "JDBC Thin Client", IdleSeconds = 309, TransactionStart = Now.AddSeconds(-312), UndoBlocks = 48, LockCount = 2, BlockedCount = 3, PrevSqlId = "4w1xq9n2b7c0d" },
        new(new SessionKey(1, 730, 3301)) { Username = "MES_APP", Machine = "was-mes02", Program = "JDBC Thin Client", IdleSeconds = 212, TransactionStart = Now.AddSeconds(-222), UndoBlocks = 2, LockCount = 1 },
        new(new SessionKey(2, 1150, 3302)) { Username = "ETL", Machine = "etl01", Program = "python3.11", IdleSeconds = 95, TransactionStart = Now.AddSeconds(-109), UndoBlocks = 270, LockCount = 3 },
    ];

    public IReadOnlyList<DeadlockEvent> DeadlockEvents { get; set; } =
    [
        new(Now.AddMinutes(-1.5), 2, ["ORA-00060: Deadlock detected. See Note 60.1 at My Oracle Support for Troubleshooting ORA-60 Errors. More info in file /u01/app/oracle/diag/rdbms/mesprd/MESPRD2/trace/MESPRD2_ora_88412.trc."]),
        new(Now.AddHours(-5), 1, ["ORA-00060: Deadlock detected. More info in file /u01/app/oracle/diag/rdbms/mesprd/MESPRD1/trace/MESPRD1_ora_40991.trc."]),
        new(Now.AddDays(-2), 1, ["ORA-00060: Deadlock detected. (오래된 기록)"]),
    ];

    public Task<IReadOnlyList<IdleTransaction>> GetIdleTransactionsAsync(ISession monitor, int minIdleSeconds, CancellationToken ct)
    {
        IdleCalls++;
        LastMinIdle = minIdleSeconds;
        return Task.FromResult<IReadOnlyList<IdleTransaction>>([.. Idle.Where(i => i.IdleSeconds >= minIdleSeconds)]);
    }

    public Task<IReadOnlyList<DeadlockEvent>> GetRecentDeadlocksAsync(ISession monitor, int max, CancellationToken ct)
    {
        DeadlockCalls++;
        return Task.FromResult<IReadOnlyList<DeadlockEvent>>([.. DeadlockEvents.Take(max)]);
    }

    public Task<IReadOnlyList<DdlLockRow>> GetDdlLocksAsync(ISession monitor, CancellationToken ct)
    {
        DdlCalls++;
        if (DdlFail is { } ex)
        {
            throw ex;
        }

        return Task.FromResult<IReadOnlyList<DdlLockRow>>(
        [
            new(new SessionKey(1, 815, 1), "MES", "PKG_LOT_MOVE", "Body") { ModeHeld = "Exclusive", ModeRequested = "None" },
            new(W1, "MES", "PKG_LOT_MOVE", "Body") { ModeHeld = "None", ModeRequested = "Share" },
            new(W3, "MES", "PKG_LOT_MOVE", "Body") { ModeHeld = "None", ModeRequested = "Share" },
        ]);
    }

    public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct)
    {
        Killed.Add((target, immediate));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => throw new NotSupportedException();

    public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => throw new NotSupportedException();

    public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct) => throw new NotSupportedException();

    public static BlockingTree Tree() => new(
    [
        new BlockingNode(H1,
        [
            new BlockingNode(W1,
            [
                new BlockingNode(W2, []) { Username = "MES_RPT", Event = "enq: TX - row lock contention", SecondsInWait = 141, SqlId = "9zq0m3r1kx2pa", LockedObject = "MES.LOT_HIST", Status = "ACTIVE" },
            ]) { Username = "MES_APP", Event = "enq: TX - row lock contention", SecondsInWait = 287, SqlId = "7h35uxf5uhmm1", LockedObject = "MES.LOT_HIST", Status = "ACTIVE" },
            new BlockingNode(W3, []) { Username = "MES_BATCH", Event = "enq: TX - row lock contention", SecondsInWait = 203, SqlId = "7h35uxf5uhmm1", LockedObject = "MES.LOT_HIST", Status = "ACTIVE" },
        ]) { Username = "MES_APP", Status = "INACTIVE" },
        new BlockingNode(H2,
        [
            new BlockingNode(W4, []) { Username = "SCOTT", Event = "enq: TM - contention", SecondsInWait = 46, SqlId = "b0x5t2c7f1ywn", LockedObject = "SCOTT.EMP", Status = "ACTIVE" },
        ]) { Username = "SCOTT", Status = "INACTIVE" },
        new BlockingNode(H3,
        [
            new BlockingNode(W5, []) { Username = "HR", Event = "enq: TX - allocate ITL entry", SecondsInWait = 8, SqlId = "6p2x9w0e1r8tn", LockedObject = "HR.ORDERS", Status = "ACTIVE" },
        ]) { Username = "HR", SqlId = "3k1d0a7q2m9vz", Status = "ACTIVE" },
    ]);

    public static IReadOnlyList<LockRow> Rows()
    {
        LockRow R(SessionKey s, string user, string type, int held, int req, long ctime, bool block, long id1, long id2, string? obj = null,
            int? txAgo = null, long? undo = null, string? prev = null, string? program = null) => new(s, type)
        {
            Username = user, HeldMode = held, RequestedMode = req, CtimeSeconds = ctime, IsBlocking = block, Id1 = id1, Id2 = id2,
            ObjectName = obj, TransactionStart = txAgo is { } a ? Now.AddSeconds(-a) : null, UndoBlocks = undo, PrevSqlId = prev,
            Program = program, Machine = program is null ? null : "was-mes01",
            Status = s == H1 || s == H2 ? "INACTIVE" : "ACTIVE",
        };
        return
        [
            R(H1, "MES_APP", "TX", 6, 0, 312, true, 327706, 258466, txAgo: 312, undo: 48, prev: "4w1xq9n2b7c0d", program: "JDBC Thin Client"),
            R(H1, "MES_APP", "TM", 3, 0, 312, false, 92458, 0, obj: "MES.LOT_HIST"),
            R(W1, "MES_APP", "TX", 0, 6, 287, false, 327706, 258466, txAgo: 287, undo: 2),
            R(W1, "MES_APP", "TX", 6, 0, 287, true, 458771, 90112, txAgo: 287, undo: 2),
            R(W3, "MES_BATCH", "TX", 0, 6, 203, false, 327706, 258466, txAgo: 203, undo: 1),
            R(W2, "MES_RPT", "TX", 0, 6, 141, false, 458771, 90112, txAgo: 141, undo: 1),
            R(H2, "SCOTT", "TM", 3, 0, 1020, true, 73181, 0, obj: "SCOTT.EMP", txAgo: 1020, undo: 1, prev: "1m8c2v0d9s4tq", program: "SQL Developer"),
            R(W4, "SCOTT", "TM", 0, 4, 46, false, 73181, 0, obj: "SCOTT.EMP"),
            R(H3, "HR", "TX", 6, 0, 22, true, 524301, 77120, txAgo: 22, undo: 6, program: "sqlplus@batch02"),
            R(W5, "HR", "TX", 0, 4, 8, false, 524301, 77120),
            R(H3, "HR", "TM", 3, 0, 22, false, 88014, 0, obj: "HR.ORDERS"),
        ];
    }
}

internal sealed class FakeLockSession(ServerInfo server, ConnectionProfile? profile = null) : ISession
{
    public bool Disposed { get; private set; }

    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = profile ?? new("MESPRD", "mesadm", "h:1521/s", Environment: ConnectionEnvironment.Production);

    public ServerInfo Server { get; } = server;

    public SessionPurpose Purpose => SessionPurpose.Monitor;

    public string DbKey => "MESPRD/MESADM";

    public SessionState State { get; set; } = SessionState.Idle;

    public bool HasPendingTransaction => false;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>가짜 서비스·세션으로 만든 Lock 모니터 화면 모델.</summary>
internal sealed class LockHarness
{
    public static readonly ServerCapabilities Dba = new(true, true, true, true, true);
    public static readonly ServerCapabilities ReadOnly = new(false, true, true, false, false);
    public static readonly ServerCapabilities NoPrivilege = new(false, false, false, false, false);

    public LockHarness(ServerCapabilities? caps = null, ConnectionProfile? profile = null)
    {
        Session = new FakeLockSession(Server(caps ?? Dba), profile);
        Vm = new LockMonitorViewModel(Monitor, _ =>
        {
            Opened++;
            return Task.FromResult<ISession?>(Session);
        }, Keys)
        {
            PostToUi = a => a(),
            StartAutoRefresh = false,
            ConfirmKill = r =>
            {
                Asked = r;
                return Task.FromResult(Answer);
            },
        };
    }

    public static ServerInfo Server(ServerCapabilities caps) =>
        new("19.0.0.0.0", "MESPRD1", "h", "MESPRD", "MESPRD", null, false, 1, caps, IsRac: true);

    public FakeLockMonitor Monitor { get; } = new();

    public ContextKeyService Keys { get; } = new();

    public FakeLockSession Session { get; }

    public LockMonitorViewModel Vm { get; }

    public int Opened { get; private set; }

    public LockKillRequest? Asked { get; private set; }

    public bool Answer { get; set; } = true;
}
