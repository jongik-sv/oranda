using Oranda.Core.Connection;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>DB 없는 세션(통계·자식 커서 화면 시험용).</summary>
internal sealed class TuningFakeSession(bool dba = true, bool perfViews = true, string user = "mes") : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = new("MESQA", user, "h:1521/mesqa", Id: new Guid("c1f6c2d4-0000-4000-8000-0000000000cc"), Environment: ConnectionEnvironment.Test);

    public ServerInfo Server { get; } = new("19.0.0.0.0", "MESQA1", "h", "MESQA", "MESQA", null, false, 77,
        new ServerCapabilities(dba, true, perfViews, dba, dba), SessionUser: user.ToUpperInvariant());

    public SessionPurpose Purpose => SessionPurpose.Work;

    public string DbKey => "MESQA/MESQA/" + user.ToUpperInvariant();

    public SessionState State { get; set; } = SessionState.Idle;

    public bool HasPendingTransaction => false;

    public int Disposed { get; private set; }

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed++;
        State = SessionState.Closed;
        return ValueTask.CompletedTask;
    }
}

/// <summary>가짜 튜닝 서비스. 값·예외·멈춤(<see cref="Gate"/>)을 시험이 정한다. 부른 순서를 남긴다.</summary>
internal sealed class FakeTuningService : ITuningService
{
    public TableStatsRow? Table { get; set; }

    public List<ColumnStatsRow> Columns { get; } = [];

    public List<IndexStatsRow> Indexes { get; } = [];

    public List<IndexColumnsRow> IndexColumns { get; } = [];

    public Dictionary<string, List<HistogramBucket>> Histograms { get; } = new(StringComparer.Ordinal);

    public List<ChildCursorRow> Children { get; } = [];

    public List<SharedCursorReason> Reasons { get; } = [];

    public Dictionary<int, CorePlan> ChildPlans { get; } = [];

    public Exception? Failure { get; set; }

    public Exception? ReasonFailure { get; set; }

    /// <summary>있으면 표 통계 읽기가 이것을 기다린다(취소 시험).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<string> Calls { get; } = [];

    public async Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        Calls.Add($"table {owner}.{table}");
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (Failure is { } f)
        {
            throw f;
        }

        return Table;
    }

    public Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        Calls.Add("columns");
        return Task.FromResult<IReadOnlyList<ColumnStatsRow>>(Columns);
    }

    public Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct)
    {
        Calls.Add("histogram " + column);
        return Task.FromResult<IReadOnlyList<HistogramBucket>>(Histograms.TryGetValue(column, out var b) ? b : []);
    }

    public Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        Calls.Add("indexes");
        return Task.FromResult<IReadOnlyList<IndexStatsRow>>(Indexes);
    }

    public Task<IReadOnlyList<IndexColumnsRow>> GetIndexColumnsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IndexColumnsRow>>(IndexColumns);

    public Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        Calls.Add("children " + sqlId);
        if (Failure is { } f)
        {
            throw f;
        }

        return Task.FromResult<IReadOnlyList<ChildCursorRow>>(Children);
    }

    public Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        Calls.Add("reasons");
        if (ReasonFailure is { } f)
        {
            throw f;
        }

        return Task.FromResult<IReadOnlyList<SharedCursorReason>>(Reasons);
    }

    public Task<CorePlan> GetChildPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
    {
        Calls.Add("plan " + cursor.ChildNumber);
        return Task.FromResult(ChildPlans[cursor.ChildNumber!.Value]);
    }
}

/// <summary>시안(화면 e·f)과 같은 모양의 자료.</summary>
internal static class TuningSamples
{
    public static readonly DateTime Now = new(2026, 10, 24, 14, 10, 0);

    public static FakeTuningService LotHist(bool stale = true)
    {
        var f = new FakeTuningService
        {
            Table = new TableStatsRow("MES", "LOT_HIST", 12_480_331, 182_044, 96, new DateTime(2026, 9, 2, 22, 10, 0), stale),
        };
        var analyzed = new DateTime(2026, 9, 2, 22, 10, 0);
        f.Columns.AddRange(
        [
            new("HIST_SEQ", 12_480_331, 0, 8.0e-8, "NONE", 1, analyzed),
            new("LOT_ID", 398_201, 0, 2.5e-6, "NONE", 1, analyzed),
            new("EQP_ID", 1_904, 0, 5.3e-4, "HYBRID", 254, analyzed),
            new("EVT_CD", 6, 0, 4.0e-8, "FREQUENCY", 6, analyzed),
            new("EVT_TIME", 11_902_114, 0, 8.4e-8, "NONE", 1, analyzed),
            new("EVT_STAT", 3, 0, 4.0e-8, "FREQUENCY", 3, analyzed),
            new("LINE_CD", 14, 1_204_559, 4.0e-8, "TOP-FREQUENCY", 12, analyzed),
            new("UPD_USER", 41, 0, 0.0244, "NONE", 1, analyzed),
            new("UPD_TIME", 10_880_422, 88_120, 9.2e-8, "NONE", 1, analyzed),
        ]);
        f.Histograms["EVT_CD"] =
        [
            new(5_210_400, "MOVE", 5_210_400),
            new(8_330_880, "TRACK_IN", 3_120_480),
            new(11_440_920, "TRACK_OUT", 3_110_040),
            new(12_120_300, "HOLD", 679_380),
            new(12_460_100, "RELEASE", 339_800),
            new(12_480_331, "SPLIT", 20_231),
        ];
        f.Histograms["EVT_STAT"] = [new(9_000_000, "DONE", 9_000_000), new(12_000_000, "RUN", 3_000_000), new(12_480_331, "WAIT", 480_331)];
        f.Histograms["HIST_SEQ"] = [new(0, "1", 0), new(1, "88123004", 1)]; // NONE 이어도 두 끝점이 온다(화면이 거른다)
        f.Indexes.AddRange(
        [
            new("LOT_HIST_PK", "UNIQUE", 190_112, 31_840, 12_480_331, 2, analyzed, stale),
            new("LOT_HIST_IX1", "NONUNIQUE", 2_410_880, 44_210, 12_301_882, 3, analyzed, stale),
            new("LOT_HIST_IX2", "NONUNIQUE", 11_902_447, 28_002, 1_904, 2, analyzed, stale),
        ]);
        f.IndexColumns.AddRange(
        [
            new("LOT_HIST_PK", ["HIST_SEQ"]),
            new("LOT_HIST_IX1", ["LOT_ID", "EVT_TIME"]),
            new("LOT_HIST_IX2", ["EQP_ID"]),
        ]);
        return f;
    }

    public static FakeTuningService Cursors()
    {
        var f = new FakeTuningService();
        f.Children.AddRange(
        [
            new(0, 1_838_229_974, true, false, false, 14_882, 14_882L * 42_180, 14_882L * 1_842_000) { InstanceId = 1 },
            new(1, 3_392_001_144, true, true, true, 2_940, 2_940L * 612, 2_940L * 38_000) { InstanceId = 1 },
            new(2, 1_838_229_974, true, true, true, 310, 310L * 41_960, 310L * 1_790_000) { InstanceId = 2 },
            new(3, 3_392_001_144, true, true, true, 88, 88L * 604, 88L * 35_000) { InstanceId = 2 },
        ]);
        f.Reasons.AddRange(
        [
            new(1, "BIND_EQUIV_FAILURE"),
            new(2, "BIND_EQUIV_FAILURE"),
            new(2, "BIND_LENGTH_UPGRADEABLE"),
            new(3, "ROLL_INVALID_MISMATCH"),
            new(3, "OPTIMIZER_MISMATCH"),
            new(3, "SOME_FUTURE_MISMATCH"),
        ]);
        f.ChildPlans[0] = new CorePlan(
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 8_412, EstimatedRows = 31 },
            new(1, 0, 1, "SORT") { Options = "ORDER BY", Cost = 8_412, EstimatedRows = 31 },
            new(2, 1, 2, "HASH JOIN") { Options = "ANTI NA", Cost = 8_410, EstimatedRows = 31 },
            new(3, 2, 3, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 8_380, EstimatedRows = 31 },
            new(4, 2, 3, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectOwner = "MES", ObjectName = "LOT_MST", Cost = 2, EstimatedRows = 1 },
        ], "Plan hash value: 1838229974") { PlanHashValue = 1_838_229_974 };
        f.ChildPlans[1] = new CorePlan(
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 21, EstimatedRows = 1_203 },
            new(1, 0, 1, "SORT") { Options = "ORDER BY", Cost = 21, EstimatedRows = 1_203 },
            new(2, 1, 2, "HASH JOIN") { Options = "ANTI NA", Cost = 19, EstimatedRows = 1_203 },
            new(3, 2, 3, "TABLE ACCESS") { Options = "BY INDEX ROWID BATCHED", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 6, EstimatedRows = 1_211 },
            new(4, 3, 4, "INDEX") { Options = "RANGE SCAN", ObjectOwner = "MES", ObjectName = "LOT_HIST_IX1", Cost = 3, EstimatedRows = 1_211 },
            new(5, 2, 3, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectOwner = "MES", ObjectName = "LOT_MST", Cost = 2, EstimatedRows = 1 },
        ], "Plan hash value: 3392001144") { PlanHashValue = 3_392_001_144 };
        f.ChildPlans[2] = f.ChildPlans[0];
        f.ChildPlans[3] = f.ChildPlans[1];
        return f;
    }
}
