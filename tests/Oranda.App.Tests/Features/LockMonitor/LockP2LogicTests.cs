using System.Globalization;
using Oranda.App.Features.LockMonitor;
using Oranda.Core.Monitoring;

namespace Oranda.App.Tests.Features.LockMonitor;

/// <summary>TSK-05-04 순수 논리: 이력 버퍼 경계, 순환 찾기, 임계치 판정, 스냅샷·이력 파일, 방치·데드락 줄, 그래프 배치.</summary>
public class LockP2LogicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 5, 0, 0, TimeSpan.Zero);

    private static SessionKey K(int inst, long sid, long serial = 1) => new(inst, sid, serial);

    // ---- 이력 버퍼 ------------------------------------------------------------------------------

    [Fact]
    public void History_keeps_points_inside_the_window_and_drops_older_ones_relative_to_the_newest()
    {
        var b = new LockHistoryBuffer(TimeSpan.FromMinutes(30));
        b.Add(new(T0, 1, 10));
        b.Add(new(T0.AddMinutes(30), 2, 20)); // 경계: 정확히 30 분 전 점은 남는다
        Assert.Equal(2, b.Count);

        b.Add(new(T0.AddMinutes(30).AddSeconds(1), 3, 30)); // 이제 T0 은 30 분 1 초 전
        Assert.Equal([2, 3], b.Points.Select(p => p.Waiters));
    }

    [Fact]
    public void History_inserts_out_of_order_points_in_place_and_clamps_negatives()
    {
        var b = new LockHistoryBuffer(TimeSpan.FromMinutes(30));
        b.Add(new(T0.AddSeconds(10), 1, 1));
        b.Add(new(T0, 5, -3)); // 시계가 되돌아감
        b.Add(new(T0.AddSeconds(5), -2, 7));
        Assert.Equal([T0, T0.AddSeconds(5), T0.AddSeconds(10)], b.Points.Select(p => p.At));
        Assert.Equal(0, b.Points[0].MaxWaitSeconds);
        Assert.Equal(0, b.Points[1].Waiters);
    }

    [Fact]
    public void History_retention_change_trims_immediately_and_has_a_floor_and_a_count_cap()
    {
        var b = new LockHistoryBuffer(TimeSpan.FromMinutes(30));
        for (var i = 0; i <= 30; i++)
        {
            b.Add(new(T0.AddMinutes(i), i, i));
        }

        Assert.Equal(31, b.Count);
        b.Retention = TimeSpan.FromMinutes(10);
        Assert.Equal(11, b.Count);
        Assert.Equal(20, b.Points[0].Waiters);

        b.Retention = TimeSpan.Zero; // 바닥 1 분
        Assert.Equal(LockHistoryBuffer.MinRetention, b.Retention);
        Assert.Equal(2, b.Count);

        var big = new LockHistoryBuffer(TimeSpan.FromDays(10));
        for (var i = 0; i < LockHistoryBuffer.MaxPoints + 5; i++)
        {
            big.Add(new(T0.AddSeconds(i), 1, 1));
        }

        Assert.Equal(LockHistoryBuffer.MaxPoints, big.Count);
        Assert.Equal(T0.AddSeconds(5), big.Points[0].At);
    }

    [Fact]
    public void History_peak_is_the_first_point_with_the_most_waiters_and_empty_has_none()
    {
        var b = new LockHistoryBuffer(TimeSpan.FromMinutes(30));
        Assert.Null(b.Peak);
        b.Add(new(T0, 3, 1));
        b.Add(new(T0.AddSeconds(5), 7, 1));
        b.Add(new(T0.AddSeconds(10), 7, 2));
        Assert.Equal(T0.AddSeconds(5), b.Peak!.At);
        b.Clear();
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void History_is_shared_per_profile_and_survives_reopening()
    {
        var id = Guid.NewGuid();
        var first = LockHistoryBuffer.For(id, TimeSpan.FromMinutes(30));
        first.Add(new(T0, 1, 1));
        var again = LockHistoryBuffer.For(id, TimeSpan.FromMinutes(15));
        Assert.Same(first, again);
        Assert.Equal(TimeSpan.FromMinutes(15), again.Retention);
        Assert.NotSame(first, LockHistoryBuffer.For(Guid.NewGuid(), TimeSpan.FromMinutes(30)));
    }

    // ---- 순환 ------------------------------------------------------------------------------------

    [Fact]
    public void Two_session_deadlock_is_found_and_written_from_the_smallest_session()
    {
        var a = K(2, 1903);
        var b = K(1, 388);
        var cycles = WaitCycles.Find([(a, b), (b, a)]);
        var c = Assert.Single(cycles);
        Assert.Equal([b, a], c);
        Assert.Equal("1·0388 → 2·1903 → 1·0388", WaitCycles.Text(c));
    }

    [Fact]
    public void Chain_without_cycle_self_edge_and_empty_input_find_nothing()
    {
        Assert.Empty(WaitCycles.Find([]));
        Assert.Empty(WaitCycles.Find([(K(1, 1), K(1, 2)), (K(1, 2), K(1, 3)), (K(1, 4), K(1, 3))]));
        Assert.Empty(WaitCycles.Find([(K(1, 5), K(1, 5))]));
    }

    [Fact]
    public void Three_session_cycle_with_a_tail_and_a_second_cycle_are_both_found()
    {
        // 1 → 2 → 3 → 1, 꼬리 9 → 1, 따로 7 ⇄ 8. 같은 세션을 serial 0(막는 쪽 모름)으로 적어도 같은 세션이다.
        var cycles = WaitCycles.Find(
        [
            (K(1, 2, 20), K(1, 3, 0)), (K(1, 3, 30), K(1, 1, 0)), (K(1, 1, 10), K(1, 2, 0)), (K(1, 9), K(1, 1)),
            (K(1, 8), K(1, 7)), (K(1, 7), K(1, 8)),
        ]);
        Assert.Equal(2, cycles.Count);
        Assert.Equal([1L, 2L, 3L], cycles[0].Select(k => k.Sid));
        Assert.Equal(10, cycles[0][0].Serial); // serial 을 아는 쪽을 남긴다
        Assert.Equal([7L, 8L], cycles[1].Select(k => k.Sid));
        Assert.Equal(string.Empty, WaitCycles.Text([]));
    }

    [Fact]
    public void Duplicate_edges_and_rac_sessions_with_the_same_sid_are_kept_apart()
    {
        var cycles = WaitCycles.Find([(K(1, 10), K(2, 10)), (K(1, 10), K(2, 10)), (K(2, 10), K(1, 10))]);
        Assert.Equal([K(1, 10), K(2, 10)], Assert.Single(cycles));
    }

    // ---- 임계치 ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(119, 4, false)]
    [InlineData(120, 1, true)] // 경계: 120 초 = 넘음
    [InlineData(10, 5, true)] // 경계: 5 명 = 넘음
    [InlineData(500, 0, false)] // 대기자 없는 보유자는 아님
    public void Rule_is_wait_seconds_or_waiters_inclusive(long wait, int waiters, bool exceeds) =>
        Assert.Equal(exceeds, new LockAlertRule(120, 5).Exceeds(new LockHolderLoad(K(1, 1), "A", waiters, wait)));

    [Fact]
    public void Tracker_alerts_once_per_holder_until_it_drops_below_and_picks_the_worst_new_holder()
    {
        var t = new LockThresholdTracker();
        var rule = new LockAlertRule(120, 5);
        var h1 = new LockHolderLoad(K(1, 1287), "MES_APP", 3, 287);
        var h2 = new LockHolderLoad(K(2, 644), "SCOTT", 6, 46);
        var calm = new LockHolderLoad(K(1, 211), "HR", 1, 8);

        var first = t.Evaluate([h1, h2, calm], rule, T0);
        Assert.Equal(2, first.Exceeding.Count);
        Assert.Equal(K(1, 1287), first.NewAlert!.Holder); // 가장 오래 기다린 쪽
        Assert.Equal(9, first.Waiters);
        Assert.Equal(287, first.MaxWaitSeconds);

        var second = t.Evaluate([h1, h2], rule, T0.AddSeconds(5));
        Assert.Null(second.NewAlert); // 같은 보유자는 다시 안 띄운다
        Assert.True(second.IsExceeded);

        var below = t.Evaluate([h1 with { Waiters = 1, MaxWaitSeconds = 10 }, h2], rule, T0.AddSeconds(10));
        Assert.Null(below.NewAlert);
        Assert.Single(below.Exceeding);

        var back = t.Evaluate([h1, h2], rule, T0.AddSeconds(15));
        Assert.Equal(K(1, 1287), back.NewAlert!.Holder); // 내려갔다 다시 넘으면 다시 알린다

        t.Reset();
        Assert.NotNull(t.Evaluate([h2], rule, T0.AddSeconds(20)).NewAlert);
        var none = t.Evaluate([], rule, T0.AddSeconds(25));
        Assert.False(none.IsExceeded);
        Assert.Equal(0, none.MaxWaitSeconds);
    }

    [Fact]
    public async Task Loads_count_grandchildren_and_take_the_longest_wait_under_each_root()
    {
        var h = new LockHarness();
        await h.Vm.StartAsync();
        var loads = LockThresholdTracker.Loads([.. h.Vm.TreeRows]);
        Assert.Equal(3, loads.Count);
        Assert.Equal(new LockHolderLoad(FakeLockMonitor.H1, "MES_APP", 3, 287), loads[0]);
        Assert.Equal(1, loads[1].Waiters);
        Assert.Equal(8, loads[2].MaxWaitSeconds);
    }

    [Fact]
    public void Alert_message_mentions_holder_waiters_wait_and_the_snapshot()
    {
        var a = new LockAlert(K(1, 1287), "MES_APP", 3, 1287, T0);
        Assert.Equal("1·1287 MES_APP 이(가) 3명을 막고 있고, 가장 오래 기다린 세션이 1,287 초째입니다.", a.Message);
        Assert.EndsWith("스냅샷을 저장했습니다.", (a with { SnapshotPath = "x.txt" }).Message, StringComparison.Ordinal);
    }

    // ---- 파일 ------------------------------------------------------------------------------------

    [Fact]
    public void History_csv_gets_a_header_once_and_snapshot_names_do_not_collide()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p1 = LockHistoryFiles.AppendHistory(dir, "MESADM@MESPRD", new(T0, 3, 287));
            var p2 = LockHistoryFiles.AppendHistory(dir, "MESADM@MESPRD", new(T0.AddSeconds(5), 4, 292));
            Assert.Equal(p1, p2);
            Assert.StartsWith("history-MESADM_MESPRD-", Path.GetFileName(p1), StringComparison.Ordinal);
            var lines = File.ReadAllLines(p1);
            Assert.Equal("at,waiters,max_wait_seconds", lines[0]);
            Assert.Equal(3, lines.Length);
            Assert.EndsWith(",4,292", lines[2], StringComparison.Ordinal);

            var alert = new LockAlert(K(1, 1287), "MES_APP", 3, 287, T0);
            var s1 = LockHistoryFiles.WriteSnapshot(dir, alert, "a");
            var s2 = LockHistoryFiles.WriteSnapshot(dir, alert, "b");
            Assert.NotEqual(s1, s2);
            Assert.EndsWith("-1_1287.txt", s1, StringComparison.Ordinal);
            Assert.EndsWith("-1_1287-2.txt", s2, StringComparison.Ordinal);
            Assert.Equal("b", File.ReadAllText(s2));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("A/B:C*?", "A_B_C__")]
    [InlineData("  ", "lock")]
    [InlineData(null, "lock")]
    public void Safe_name_replaces_characters_files_cannot_have(string? name, string expected) =>
        Assert.Equal(expected, LockHistoryFiles.SafeName(name));

    [Fact]
    public async Task Snapshot_text_lists_time_account_rule_holder_and_each_waiter_with_sql()
    {
        var h = new LockHarness();
        await h.Vm.StartAsync();
        var holder = h.Vm.TreeRows[0];
        var w = h.Vm.TreeRows[2]; // 2·0388, 1·1422 뒤
        var text = LockSnapshotText.Build(new LockAlert(holder.Key, "MES_APP", 3, 287, T0), "MESADM@MESPRD", new LockAlertRule(120, 5),
        [
            new("보유자", holder, "4w1xq9n2b7c0d", "UPDATE MES.LOT_HIST SET X = 1\n"),
            new("대기자", w, "9zq0m3r1kx2pa", null),
        ]);
        Assert.Contains("접속: MESADM@MESPRD", text, StringComparison.Ordinal);
        Assert.Contains("기준: 대기 120 초 이상 또는 대기자 5 명 이상", text, StringComparison.Ordinal);
        Assert.Contains("serial# 40211", text, StringComparison.Ordinal);
        Assert.Contains("== 보유자 1·1287 MES_APP · INACTIVE", text, StringComparison.Ordinal);
        Assert.Contains("UPDATE MES.LOT_HIST SET X = 1", text, StringComparison.Ordinal);
        Assert.Contains("== 대기자 2·0388 MES_RPT · ACTIVE · 141 초 · enq: TX - row lock contention · MES.LOT_HIST · 1·1422 뒤 · sql_id 9zq0m3r1kx2pa", text, StringComparison.Ordinal);
        Assert.Contains("(SQL 을 찾지 못했습니다)", text, StringComparison.Ordinal);
    }

    // ---- 줄 ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 초")]
    [InlineData(59, "59 초")]
    [InlineData(60, "1 분")]
    [InlineData(1020, "17 분")]
    [InlineData(3600, "1 시간 0 분")]
    [InlineData(7500, "2 시간 5 분")]
    [InlineData(-5, "0 초")]
    public void Elapsed_switches_units_at_a_minute_and_an_hour(long seconds, string expected) =>
        Assert.Equal(expected, LockText.Elapsed(seconds));

    [Fact]
    public void Idle_row_shows_units_blocking_in_danger_and_advice_by_program()
    {
        var m = new FakeLockMonitor();
        var scott = new IdleTransactionItem(m.Idle[0], FakeLockMonitor.Now);
        Assert.Equal(IdleTransactionItem.Columns.Length, scott.Cells.Count);
        Assert.Equal("1,018 초", scott.Cells[4]);
        Assert.Equal("17 분", scott.Cells[6]);
        Assert.Equal("1 블록", scott.Cells[7]);
        Assert.Equal("1명", scott.Cells[9]);
        Assert.True(IdleTransactionItem.IsDanger(9, scott.Cells));
        Assert.False(IdleTransactionItem.IsDanger(6, scott.Cells));
        Assert.Contains("사람이 쓰는 도구(SQL Developer)", scott.Advice, StringComparison.Ordinal);

        var etl = new IdleTransactionItem(m.Idle[3], FakeLockMonitor.Now);
        Assert.Equal(LockText.Dash, etl.Cells[9]);
        Assert.False(IdleTransactionItem.IsDanger(9, etl.Cells));
        Assert.StartsWith("앱이 트랜잭션을 연 채", etl.Advice, StringComparison.Ordinal);

        var noStart = new IdleTransactionItem(new IdleTransaction(K(1, 1)) { IdleSeconds = 90 }, FakeLockMonitor.Now);
        Assert.Equal(90, noStart.TransactionSeconds);
        Assert.Equal(string.Empty, noStart.Cells[5]);
    }

    [Theory]
    [InlineData("SQL Developer", true)]
    [InlineData("Toad.exe", true)]
    [InlineData("sqlplus@batch02 (TNS V1-V3)", true)]
    [InlineData("JDBC Thin Client", false)]
    [InlineData(null, false)]
    public void Human_tools_are_recognised(string? program, bool human) => Assert.Equal(human, LockText.IsHumanTool(program));

    [Fact]
    public void Deadlock_row_shows_time_instance_first_line_and_trace_file_name()
    {
        var now = new DateTimeOffset(2026, 10, 24, 14, 7, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 24)));
        var today = new DeadlockItem(new DeadlockEvent(now.AddMinutes(-2), 2,
            ["ORA-00060: Deadlock detected. More info in file /u01/diag/MESPRD2_ora_88412.trc.", "next"]), now);
        Assert.Equal(now.AddMinutes(-2).ToString("HH:mm:ss", CultureInfo.InvariantCulture), today.Cells[0]);
        Assert.Equal("2", today.Cells[1]);
        Assert.StartsWith("ORA-00060", (string)today.Cells[2]!, StringComparison.Ordinal);
        Assert.Equal("MESPRD2_ora_88412.trc", today.TraceFile);
        Assert.Equal("ORA-00060: Deadlock detected.", today.Summary);
        var longLine = new DeadlockItem(new DeadlockEvent(now, 1, ["ORA-00060: Deadlock detected. See Note 60.1 at My Oracle Support for Troubleshooting ORA-60 Errors."]), now);
        Assert.Equal("ORA-00060: Deadlock detected. See Note 60.1 at M …", longLine.Summary);
        Assert.Contains("인스턴스 2", today.FullText, StringComparison.Ordinal);
        Assert.EndsWith("next", today.FullText, StringComparison.Ordinal);

        var old = new DeadlockItem(new DeadlockEvent(now.AddDays(-1), 1, []), now);
        Assert.Contains("-", old.TimeText, StringComparison.Ordinal); // 날짜까지
        Assert.Equal(string.Empty, old.TraceFile);
        Assert.Equal(string.Empty, old.Cells[2]);
    }

    [Theory]
    [InlineData("11.2.0.4.0", true)]
    [InlineData("12.2.0.1.0", true)]
    [InlineData("18.0.0.0.0", true)]
    [InlineData("19.0.0.0.0", false)]
    [InlineData("23.0.0.0.0", false)]
    [InlineData("", false)]
    public void Deadlock_tab_explains_versions_that_do_not_read_the_alert_log(string version, bool hasReason) =>
        Assert.Equal(hasReason, LockMonitorViewModel.DeadlockReason(version) is not null);

    [Fact]
    public void Eleven_g_reason_uses_the_mockup_wording()
    {
        Assert.StartsWith("11g 에서는 alert 로그를 SQL 로 읽을 수 없습니다(12c 부터 v$diag_alert_ext)", LockMonitorViewModel.DeadlockReason("11.2.0.4.0"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_row_text_adds_file_block_and_row_when_known()
    {
        var info = new LockedRowInfo("AAA", "MES", "LOT_HIST") { FileNumber = 7, BlockNumber = 2495, RowNumber = 2 };
        Assert.Equal("MES.LOT_HIST · 파일 7 · 블록 2495 · 행 2", LockMonitorViewModel.LockedRowText(info));
        Assert.Equal("MES.LOT_HIST", LockMonitorViewModel.LockedRowText(new LockedRowInfo("AAA", "MES", "LOT_HIST")));
    }

    // ---- 그래프 ----------------------------------------------------------------------------------

    [Fact]
    public void Chart_layout_places_points_in_the_window_and_scales_to_the_threshold()
    {
        var points = new List<LockHistoryPoint>
        {
            new(T0.AddMinutes(-40), 9, 999), // 창 밖
            new(T0.AddMinutes(-30), 2, 50),
            new(T0.AddMinutes(-15), 4, 100),
            new(T0, 0, 0),
        };
        var layout = LockHistoryChart.Layout(points, TimeSpan.FromMinutes(30), 120);
        Assert.Equal(3, layout.Count);
        Assert.Equal(0, layout[0].X, 6);
        Assert.Equal(0.5, layout[1].X, 6);
        Assert.Equal(1, layout[2].X, 6);
        Assert.True(layout.All(l => l.Bar is >= 0 and <= 1 && l.Line is >= 0 and <= 1));
        Assert.Equal(999 / 999d, LockHistoryChart.Layout(points, TimeSpan.FromMinutes(60), 120)[0].Line, 6);
        Assert.Equal(120 / (120 * 1.15), LockHistoryChart.ThresholdRatio(points.Skip(1).ToList(), 120)!.Value, 6);
        Assert.Null(LockHistoryChart.ThresholdRatio(points, 0));
        Assert.Empty(LockHistoryChart.Layout([], TimeSpan.FromMinutes(30), 120));
    }
}
