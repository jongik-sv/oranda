using Oranda.App.Features.Tuning.Report;
using Oranda.App.Tests.Features.Execution;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>TSK-06-07 보고서 시험용 가짜: 시안(tuning-report.html)과 같은 MES.LOT_HIST 문장.</summary>
internal static class ReportFixtures
{
    public const string SqlId = "7h35uxf5uhmm1";

    public const string Sql = """
        SELECT lh.lot_id, lh.evt_time, lh.evt_cd, lm.prod_id, lm.qty
          FROM mes.lot_hist lh
          JOIN mes.lot_mst  lm ON lm.lot_id = lh.lot_id
         WHERE lh.eqp_id   = :B1
           AND lh.evt_time >= :B2
           AND lh.lot_id   = :B3
           AND lh.evt_cd NOT IN (SELECT evt_cd FROM mes.evt_skip WHERE site = :B4)
         ORDER BY lh.evt_time
        """;

    public static readonly DateTime Now = new(2026, 10, 24, 14, 12, 8);

    public static CorePlan Plan(long hash = 1838229974) => new(
        [
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 8412, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.84) },
            new PlanStep(1, 0, 1, "SORT") { Options = "ORDER BY", Cost = 8412, EstimatedRows = 31, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.84) },
            new PlanStep(2, 1, 2, "HASH JOIN") { Options = "ANTI NA", Cost = 8410, EstimatedRows = 31, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.83), AccessPredicates = "\"LH\".\"EVT_CD\"=\"EVT_CD\"" },
            new PlanStep(3, 2, 3, "NESTED LOOPS") { Cost = 8402, EstimatedRows = 31, ActualRows = 1211, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.79) },
            new PlanStep(4, 3, 4, "TABLE ACCESS")
            {
                Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 8380, EstimatedRows = 31, ActualRows = 1211, Starts = 1,
                ActualTime = TimeSpan.FromSeconds(1.60), Risk = PlanRisk.FullScan | PlanRisk.CardinalityMisestimate,
                FilterPredicates = "(\"LH\".\"EQP_ID\"=:B1 AND \"LH\".\"EVT_TIME\">=:B2 AND INTERNAL_FUNCTION(\"LH\".\"LOT_ID\")=:B3)",
            },
            new PlanStep(5, 3, 4, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectOwner = "MES", ObjectName = "LOT_MST", Cost = 2, EstimatedRows = 1, ActualRows = 1208, Starts = 1211, ActualTime = TimeSpan.FromSeconds(0.18) },
            new PlanStep(6, 5, 5, "INDEX") { Options = "UNIQUE SCAN", ObjectOwner = "MES", ObjectName = "LOT_MST_PK", Cost = 1, EstimatedRows = 1, ActualRows = 1211, Starts = 1211, ActualTime = TimeSpan.FromSeconds(0.11), AccessPredicates = "\"LM\".\"LOT_ID\"=\"LH\".\"LOT_ID\"" },
            new PlanStep(7, 2, 3, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "EVT_SKIP", Cost = 3, EstimatedRows = 12, ActualRows = 12, Starts = 1, ActualTime = TimeSpan.FromSeconds(0.001), FilterPredicates = "\"SITE\"=:B4" },
        ],
        """
        Query Block Name / Object Alias (identified by operation id):
        -------------------------------------------------------------
           4 - SEL$1 / LH@SEL$1
           5 - SEL$1 / LM@SEL$1
           6 - SEL$1 / LM@SEL$1
           7 - SEL$2 / EVT_SKIP@SEL$2
        """)
    { PlanHashValue = hash };

    public static FakeSession Session(ConnectionEnvironment env = ConnectionEnvironment.Production, LicensePack pack = LicensePack.None) =>
        new(new ConnectionProfile("MESPRD", "MESADM", "h:1521/prd", Environment: env, LicensePack: pack));

    public static TuningReportOptions Options(bool binds = false, bool english = false, IReadOnlySet<TuningReportSection>? sections = null) => new()
    {
        SqlId = SqlId,
        IncludeBindValues = binds,
        English = english,
        WaitSampleSeconds = 3,
        Sections = sections ?? new HashSet<TuningReportSection>(Enum.GetValues<TuningReportSection>()),
    };

    public static TuningReportCollector Collector(FakeReportTuning tuning, IMonitorService? monitor = null) =>
        new(tuning, monitor ?? new FakeReportMonitor()) { Delay = (_, _) => Task.CompletedTask, Now = () => Now, AppVersion = "1.3.0" };

    public static async Task<TuningReportData> CollectAsync(
        FakeReportTuning? tuning = null, ISession? session = null, TuningReportOptions? options = null, IMonitorService? monitor = null)
    {
        var data = await Collector(tuning ?? new FakeReportTuning(), monitor).CollectAsync(session ?? Session(), options ?? Options(), null, CancellationToken.None);
        return data with { Context = data.Context with { CreatedAt = new DateTimeOffset(Now, TimeSpan.FromHours(9)) } };
    }
}

/// <summary>가짜 튜닝 서비스. <see cref="FailOn"/> 에 이름을 넣으면 그 조회가 ORA-00942 로 실패한다. 부른 조회 이름을 <see cref="Calls"/> 에 남긴다.</summary>
internal sealed class FakeReportTuning : ITuningService
{
    public HashSet<string> FailOn { get; } = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    /// <summary>이 조회에 들어오면 취소한다(취소 시험).</summary>
    public string? CancelOn { get; set; }

    public CancellationTokenSource? Cts { get; set; }

    public bool NoChildren { get; set; }

    private void Enter(string name, string? detail = null)
    {
        Calls.Add(detail is null ? name : name + ":" + detail);
        if (CancelOn == name)
        {
            Cts?.Cancel();
            Cts?.Token.ThrowIfCancellationRequested();
        }

        if (FailOn.Contains(name) || (detail is not null && FailOn.Contains(name + ":" + detail)))
        {
            throw new InvalidOperationException("ORA-00942: table or view does not exist\nline 2");
        }
    }

    public Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        Enter(nameof(GetChildCursorsAsync));
        IReadOnlyList<ChildCursorRow> rows = NoChildren ? [] :
        [
            new(0, 1838229974, true, false, false, 14882, 14882L * 42180, 14882L * 1_842_000) { InstanceId = 1 },
            new(1, 3392001144, true, true, true, 2940, 2940L * 612, 2940L * 38_000) { InstanceId = 1 },
            new(2, 1838229974, true, true, true, 310, 310L * 41960, 310L * 1_790_000) { InstanceId = 1 },
            new(3, 3392001144, true, true, true, 88, 88L * 604, 88L * 35_000) { InstanceId = 1 },
        ];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        Enter(nameof(GetSharedCursorReasonsAsync));
        IReadOnlyList<SharedCursorReason> rows = [new(1, "BIND_EQUIV_FAILURE"), new(2, "BIND_EQUIV_FAILURE"), new(3, "ROLL_INVALID_MISMATCH")];
        return Task.FromResult(rows);
    }

    public Task<CorePlan> GetChildPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
    {
        Enter(nameof(GetChildPlanAsync), cursor.ChildNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(ReportFixtures.Plan(cursor.ChildNumber is 1 or 3 ? 3392001144 : 1838229974) with { Cursor = cursor });
    }

    public Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        Enter(nameof(GetSqlStatsAsync));
        return Task.FromResult<SqlStatsRow?>(new SqlStatsRow(4, 1, 18220, 33_561_000_000, 9_402_000_000, 768_519_600, 565_220_840, 22_009_760)
        {
            ApplicationWaitMicros = 6_040_000_000, UserIoWaitMicros = 12_000_000_000, ParseCalls = 120, Fetches = 36000,
            ParsingSchema = "MESADM", Module = "MES-WEB", FirstLoadTime = "2026-10-20/08:02:11", LastActiveTime = ReportFixtures.Now,
        });
    }

    public Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        Enter(nameof(GetTableStatsAsync), table);
        return Task.FromResult<TableStatsRow?>(table switch
        {
            "LOT_HIST" => new TableStatsRow(owner, table, 12_480_331, 182_044, 96, new DateTime(2026, 9, 2, 22, 10, 0), true),
            "LOT_MST" => new TableStatsRow(owner, table, 402_118, 6_210, 121, new DateTime(2026, 10, 22, 22, 4, 0), false),
            "EVT_SKIP" => new TableStatsRow(owner, table, 12, 5, 18, new DateTime(2026, 10, 1, 22, 0, 0), false),
            _ => null,
        });
    }

    public Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        Enter(nameof(GetColumnStatsAsync), table);
        IReadOnlyList<ColumnStatsRow> rows = table switch
        {
            "LOT_HIST" =>
            [
                new("EQP_ID", 1904, 0, 0.000525, "HYBRID", 254, null),
                new("EVT_TIME", 11_902_114, 0, 8.4e-8, "NONE", 1, null),
                new("LOT_ID", 398_201, 0, 2.5e-6, "NONE", 1, null),
                new("EVT_CD", 6, 0, 4.0e-8, "FREQUENCY", 6, null),
            ],
            "LOT_MST" => [new("LOT_ID", 402_118, 0, 2.5e-6, "NONE", 1, null)],
            _ => [new("SITE", 3, 0, 0.33, "NONE", 1, null)],
        };
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct)
    {
        Enter(nameof(GetHistogramAsync), table + "." + column);
        IReadOnlyList<HistogramBucket> rows =
        [
            new(4_610_000, "MOVE_IN", 4_610_000), new(9_130_000, "MOVE_OUT", 4_520_000), new(10_230_000, "HOLD", 1_100_000),
            new(11_710_000, "REWORK", 1_480_000), new(12_150_000, "SCRAP", 440_000), new(12_480_000, "<SPLIT&\"x\">", 330_000),
        ];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        Enter(nameof(GetIndexStatsAsync), table);
        IReadOnlyList<IndexStatsRow> rows = table switch
        {
            "LOT_HIST" => [new("LOT_HIST_PK", "UNIQUE", 190_112, 31_840, 12_480_331, 2, null, false), new("LOT_HIST_IX2", "NONUNIQUE", 11_900_000, 28_002, 1904, 2, null, false)],
            "LOT_MST" => [new("LOT_MST_PK", "UNIQUE", 6_880, 1_120, 402_118, 1, null, false)],
            _ => [],
        };
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<IndexColumnsRow>> GetIndexColumnsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        Enter(nameof(GetIndexColumnsAsync), table);
        IReadOnlyList<IndexColumnsRow> rows = table switch
        {
            "LOT_HIST" => [new("LOT_HIST_PK", ["HIST_SEQ"]), new("LOT_HIST_IX2", ["EQP_ID"])],
            "LOT_MST" => [new("LOT_MST_PK", ["LOT_ID"])],
            _ => [],
        };
        return Task.FromResult(rows);
    }

    private int _samples;

    public Task<IReadOnlyList<WaitSample>> SampleWaitsAsync(ISession s, string? sqlId, CancellationToken ct)
    {
        Enter(nameof(SampleWaitsAsync));
        var i = _samples++;
        IReadOnlyList<WaitSample> rows =
        [
            new(DateTime.Now, 101, sqlId, null, null),
            new(DateTime.Now, 102, sqlId, i % 2 == 0 ? "db file scattered read" : null, i % 2 == 0 ? "User I/O" : null),
            new(DateTime.Now, 103, sqlId, i == 0 ? "enq: TX - row lock contention" : "db file scattered read", i == 0 ? "Application" : "User I/O"),
        ];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<AshWaitRow>> GetAshWaitsAsync(ISession s, string sqlId, DateTime since, CancellationToken ct)
    {
        Enter(nameof(GetAshWaitsAsync));
        IReadOnlyList<AshWaitRow> rows = [new("ON CPU", "CPU", 74), new("db file scattered read", "User I/O", 59), new("gc cr multi block request", "Cluster", 9)];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<AwrPlanRow>> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct)
    {
        Enter(nameof(GetAwrPlanHistoryAsync));
        IReadOnlyList<AwrPlanRow> rows =
        [
            new(901, new DateTime(2026, 10, 23, 10, 0, 0), 3392001144, 400, 400L * 38_000, 400L * 612),
            new(902, new DateTime(2026, 10, 23, 11, 0, 0), 1838229974, 380, 380L * 1_842_000, 380L * 42180),
        ];
        return Task.FromResult(rows);
    }

    public Task<CorePlan> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct)
    {
        Enter(nameof(GetAwrPlanAsync), planHashValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var plan = ReportFixtures.Plan(planHashValue);
        return Task.FromResult(plan with { Steps = [.. plan.Steps.Select(st => st with { ActualRows = null, ActualTime = null, Starts = null })] });
    }

    public Task<IReadOnlyList<TuningEnvRow>> GetTuningEnvironmentAsync(ISession s, CancellationToken ct)
    {
        Enter(nameof(GetTuningEnvironmentAsync));
        IReadOnlyList<TuningEnvRow> rows =
        [
            new(TuningEnvKind.Version, "banner", "Oracle Database 19c Enterprise Edition Release 19.0.0.0.0 - Production"),
            new(TuningEnvKind.Parameter, "optimizer_features_enable", "19.1.0"),
            new(TuningEnvKind.Parameter, "optimizer_mode", "ALL_ROWS"),
            new(TuningEnvKind.Parameter, "cursor_sharing", "EXACT"),
            new(TuningEnvKind.Nls, "NLS_CHARACTERSET", "AL32UTF8"),
            new(TuningEnvKind.Nls, "NLS_DATE_FORMAT", "YYYY-MM-DD HH24:MI:SS"),
        ];
        return Task.FromResult(rows);
    }
}

/// <summary>가짜 모니터 서비스: SQL 전문·바인드 캡처만.</summary>
internal sealed class FakeReportMonitor : IMonitorService
{
    public string Text { get; set; } = ReportFixtures.Sql;

    public bool Fail { get; set; }

    public IReadOnlyList<CapturedBind> Binds { get; set; } =
    [
        new(":B1", 1, "VARCHAR2(32)", "EQP-<01>&\"'"),
        new(":B2", 2, "DATE", "10/01/2026 00:00:00"),
        new(":B3", 3, "NUMBER", "4471029"),
        new(":B4", 4, "VARCHAR2(8)", null),
    ];

    public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) =>
        Fail ? throw new InvalidOperationException("SQL 을 공유 풀에서 찾을 수 없습니다: " + cursor.SqlId)
            : Task.FromResult(new SqlDetail(cursor, Text) { Binds = Binds });

    public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => throw new NotSupportedException();

    public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => throw new NotSupportedException();

    public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

    public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct) => throw new NotSupportedException();
}
