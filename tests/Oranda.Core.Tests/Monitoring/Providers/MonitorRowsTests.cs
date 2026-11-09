using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;

namespace Oranda.Core.Tests.Monitoring.Providers;

// 모니터 질의 행 조립(DB 없음). Oracle.IntegrationTests 의 SessionMonitorLogicTests(TSK-09-01)·LockMonitorLogicTests(TSK-05-02)·
// TopSqlLogicTests(TSK-09-02) 에서 옮겼다(P0a).
public class MonitorRowsTests
{
    private static SessionKey K(long sid, int inst = 1) => new(inst, sid, sid * 10);

    private static BlockingRow Row(long sid, long? blocker, int inst = 1) =>
        new(K(sid, inst), blocker is { } b ? new SessionKey(inst, b, 0) : null) { Username = "U" + sid };

    [Fact]
    public void Tree_roots_are_final_blockers_and_waiters_hang_under_direct_blocker()
    {
        // 10 ← 20 ← 30, 10 ← 40, 50(혼자 막는 쪽) ← 60
        var tree = MonitorRows.BuildBlockingTree(
            [Row(30, 20), Row(20, 10), Row(10, null), Row(40, 10), Row(60, 50), Row(50, null)]);

        Assert.Equal([10L, 50L], tree.Roots.Select(n => n.Session.Sid));
        var root = tree.Roots[0];
        Assert.Equal(K(10), root.Session);
        Assert.Equal([20L, 40L], root.Waiters.Select(n => n.Session.Sid));
        Assert.Equal(30, Assert.Single(root.Waiters[0].Waiters).Session.Sid);
        Assert.Equal("U60", Assert.Single(tree.Roots[1].Waiters).Username);
    }

    [Fact]
    public void Tree_treats_blocker_outside_list_as_root_and_breaks_cycles()
    {
        var outside = MonitorRows.BuildBlockingTree([Row(7, 99)]);
        Assert.Equal(7, Assert.Single(outside.Roots).Session.Sid);

        var cycle = MonitorRows.BuildBlockingTree([Row(1, 2), Row(2, 1)]);
        var root = Assert.Single(cycle.Roots);
        Assert.Equal(1, root.Session.Sid);
        Assert.Equal(2, Assert.Single(root.Waiters).Session.Sid);
    }

    [Fact]
    public void Tree_separates_instances()
    {
        var tree = MonitorRows.BuildBlockingTree([Row(5, null, inst: 1), Row(6, 5, inst: 2), Row(5, null, inst: 2)]);

        Assert.Equal(2, tree.Roots.Count);
        Assert.Empty(tree.Roots[0].Waiters);
        Assert.Equal(new SessionKey(2, 6, 60), Assert.Single(tree.Roots[1].Waiters).Session);
    }

    [Fact]
    public void Deltas_skip_missing_and_decreasing_values()
    {
        var deltas = MonitorRows.ComputeDeltas([10, 5, null, 100, 7], [15, 5, 3, 50, 9]);

        Assert.Equal(5, deltas["CPU used by this session"]);
        Assert.Equal(0, deltas["session logical reads"]);
        Assert.False(deltas.ContainsKey("physical reads"));
        Assert.False(deltas.ContainsKey("execute count"));
        Assert.Equal(2, deltas["redo size"]);
    }

    [Fact]
    public void Lock_types_bind_defaults_to_tm_tx_ul_and_drops_non_alphanumerics()
    {
        Assert.Equal(",TM,TX,UL,", MonitorRows.LockTypesBind([]));
        Assert.Equal(",TM,TX,UL,", MonitorRows.LockTypesBind(["", " ", ",,"]));
        Assert.Equal(",TM,UL,", MonitorRows.LockTypesBind(["tm", " UL ", "TM"]));
        Assert.Equal(",TX,", MonitorRows.LockTypesBind(["T'X"]));
    }

    [Fact]
    public void Locked_values_format_dates_binary_and_long_text()
    {
        Assert.Null(MonitorRows.FormatLockedValue(null));
        Assert.Null(MonitorRows.FormatLockedValue(DBNull.Value));
        Assert.Equal("2026-10-24 13:05:09", MonitorRows.FormatLockedValue(new DateTime(2026, 10, 24, 13, 5, 9)));
        Assert.Equal("2026-10-24 13:05:09.5", MonitorRows.FormatLockedValue(new DateTime(2026, 10, 24, 13, 5, 9, 500)));
        Assert.Equal("1.5", MonitorRows.FormatLockedValue(1.5m));
        Assert.Equal("0x0A0B", MonitorRows.FormatLockedValue(new byte[] { 10, 11 }));

        var bytes = new byte[100];
        Assert.EndsWith("… (100 bytes)", MonitorRows.FormatLockedValue(bytes), StringComparison.Ordinal);
        var text = MonitorRows.FormatLockedValue(new string('x', MonitorRows.LockedValueMaxLength + 10));
        Assert.Equal(MonitorRows.LockedValueMaxLength + 1, text!.Length);
    }

    [Fact]
    public void Lock_row_maps_object_transaction_and_blocker()
    {
        var start = new DateTime(2026, 10, 24, 9, 0, 0);
        object?[] r =
        [
            2, 30L, 300L, "APP", "pc1", "app.exe", "TX", 65537L, 99L, 0, 6, 12L, 0,
            null, null, start, 4L, "enq: TX - row lock contention", 12L, "sqlA", "sqlB", 1, 10L, 100L,
        ];

        var row = MonitorRows.ToLockRow(r);

        Assert.Equal(new SessionKey(2, 30, 300), row.Session);
        Assert.Equal("TX", row.LockType);
        Assert.Equal(0, row.HeldMode);
        Assert.Equal(6, row.RequestedMode);
        Assert.False(row.IsBlocking);
        Assert.Null(row.ObjectName);
        Assert.Equal(new DateTimeOffset(start, TimeSpan.Zero), row.TransactionStart);
        Assert.Equal(new SessionKey(1, 10, 100), row.BlockingSession);
        Assert.Null(row.Status); // 열 24 가 없는 옛 결과
        Assert.Equal("INACTIVE", MonitorRows.ToLockRow([.. r, "INACTIVE"]).Status);

        r[6] = "TM";
        r[12] = 2; // RAC 의 전역 막음
        r[13] = "APP";
        r[14] = "ORDERS";
        r[22] = null;
        var tm = MonitorRows.ToLockRow(r);
        Assert.True(tm.IsBlocking);
        Assert.Equal("APP.ORDERS", tm.ObjectName);
        Assert.Null(tm.BlockingSession);
    }

    [Fact]
    public void Deadlock_event_splits_message_lines_and_keeps_utc_time()
    {
        var at = new DateTime(2026, 10, 24, 1, 2, 3);
        var e = MonitorRows.ToDeadlockEvent([at, 2, "ORA-00060: Deadlock detected.\r\n\r\n  More info in file x.trc.\n"]);

        Assert.Equal(new DateTimeOffset(at, TimeSpan.Zero), e.At);
        Assert.Equal(2, e.InstanceId);
        Assert.Equal(["ORA-00060: Deadlock detected.", "More info in file x.trc."], e.MessageLines);
    }

    [Fact]
    public void Binds_are_ordered_by_position_and_keep_latest_capture_per_position()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var recent = old.AddHours(1);
        var rows = new List<object?[]>
        {
            new object?[] { ":B2", 2m, "NUMBER", "20", old, "YES" },
            new object?[] { ":B1", 1m, "VARCHAR2(32)", "old", old, "YES" },
            new object?[] { ":B1", 1m, "VARCHAR2(32)", "new", recent, "YES" },
            new object?[] { ":B3", 3m, "DATE", null, null, "NO" },
        };

        var binds = MonitorRows.TopSqlBinds(rows);

        Assert.Equal([":B1", ":B2", ":B3"], binds.Select(b => b.Name));
        Assert.Equal("new", binds[0].Value);
        Assert.Equal("20", binds[1].Value);
        Assert.Null(binds[2].Value);
        Assert.Null(binds[2].CapturedAt);
    }
}
