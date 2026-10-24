using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;
using PlanResult = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.SqlMonitor;

/// <summary>
/// TSK-06-06 SQL 모니터 튜닝 탭: 대기 분해(표본 진행·취소·빈 결과·오류), 팩 관문(선언·서버 값), AWR 계획 이력·두 plan_hash 비교,
/// 계획 관리, 스크립트 만들기(DB 로 보내지 않음), 자식 커서·Lock 모니터 바로 가기, 튜닝 메뉴 배치.
/// </summary>
public class SqlMonitorTuningTests
{
    internal const string SqlId = "7h35uxf5uhmm1";
    internal const string OtherSqlId = "4w1xq9n2b7c0d";

    private static readonly ShellContributions App = ShellContributions.Scan(typeof(SqlMonitorViewModel).Assembly);

    // ---- 메뉴·명령 ----

    [Fact]
    public void Wait_and_history_commands_sit_at_the_end_of_the_tuning_menu_with_a_script_submenu()
    {
        var commands = App.Commands.Select(c => c.Definition).ToList();
        var registry = new CommandRegistry(App.Commands);
        foreach (var id in SqlMonitorCommands.TuningIds)
        {
            Assert.NotNull(registry.Find(id));
            Assert.Contains(id, Oranda.App.Features.Tuning.TuningCommands.Ids);
        }

        var tuning = MenuModelBuilder.BuildMenuBar(commands, new ShellText(language: "ko"), _ => null, [])[MenuModelBuilder.TopMenus.ToList().IndexOf("tuning")];
        var ids = Flatten(tuning).ToList();
        var waits = ids.IndexOf("oranda.tuning.ashWaits");
        Assert.True(waits > ids.IndexOf("oranda.monitor.sqlShowBinds"));
        Assert.Equal(["oranda.tuning.ashWaits", "oranda.tuning.awrHistory", "oranda.tuning.planControls", "oranda.tuning.tuningAdvisorScript", "oranda.tuning.baselineScript"],
            ids.Where(i => SqlMonitorCommands.TuningIds.Contains(i)));
        var scripts = tuning.Children.Single(c => c.Kind == MenuNodeKind.Submenu && Flatten(c).Contains("oranda.tuning.baselineScript"));
        Assert.Equal("스크립트 만들기", scripts.Title);

        var keys = new ContextKeyService();
        Assert.True(registry.CanExecute("oranda.tuning.ashWaits", keys));
        Assert.False(registry.CanExecute("oranda.tuning.baselineScript", keys));
        keys.Set("monitorSqlSelected", true);
        Assert.True(registry.CanExecute("oranda.tuning.baselineScript", keys));
        Assert.Equal(SqlMonitorViewModel.AwrTab, SqlMonitorCommandHandler.TabFor("oranda.tuning.awrHistory"));
        Assert.Null(SqlMonitorCommandHandler.TabFor("oranda.monitor.sql"));
    }

    private static IEnumerable<string> Flatten(MenuNode node)
    {
        foreach (var c in node.Children)
        {
            if (c.Kind == MenuNodeKind.Command)
            {
                yield return c.CommandId!;
            }

            foreach (var x in Flatten(c))
            {
                yield return x;
            }
        }
    }

    // ---- 대기 분해(팩 없음) ----

    [Fact]
    public async Task Sampling_collects_n_samples_one_second_apart_and_counts_event_shares()
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Samples = i => i switch
        {
            0 => [Sample(1, null), Sample(2, "db file scattered read", "User I/O")],
            1 => [Sample(1, null), Sample(2, "enq: TX - row lock contention", "Application")],
            _ => [Sample(1, null)],
        };
        h.Vm.Waits.DurationSeconds = 5;

        await h.Vm.Waits.StartAsync();

        Assert.Equal(WaitPaneState.Done, h.Vm.Waits.State);
        Assert.Equal(5, h.Tuning.SampleCalls);
        Assert.All(h.Tuning.SampledIds, id => Assert.Equal(SqlId, id));
        Assert.Equal(4, h.Delays.Count);
        Assert.All(h.Delays, d => Assert.Equal(TimeSpan.FromSeconds(1), d));
        Assert.Equal("5 / 5", h.Vm.Waits.ProgressText);
        var cpu = h.Vm.Waits.Events[0];
        Assert.Equal("ON CPU", cpu.Event);
        Assert.Equal("CPU", cpu.WaitClass);
        Assert.Equal("5", cpu.SamplesText);
        Assert.Equal("71 %", cpu.ShareText);
        Assert.Equal("1", cpu.SessionsText);
        Assert.True(cpu.IsCpu);
        Assert.True(h.Vm.Waits.HasResults);
        Assert.Contains(h.Vm.Waits.Events, e => e.IsLockWait && e.IsApplication);
        Assert.Equal(["CPU", "User I/O", "Application"], h.Vm.Waits.Classes.Select(c => c.Share.Label));
        Assert.Contains("표본 7건", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_keeps_the_partial_result_and_stops_asking_the_server()
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Samples = _ => [Sample(1, "db file sequential read", "User I/O")];
        var gate = new TaskCompletionSource();
        h.Vm.Waits.Delay = async (_, ct) =>
        {
            gate.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var run = h.Vm.Waits.StartAsync();
        await gate.Task;
        Assert.Equal(WaitPaneState.Sampling, h.Vm.Waits.State);
        Assert.True(h.Vm.Waits.IsSampling);
        Assert.False(h.Vm.Waits.CanStart);
        Assert.Equal("1 / 30", h.Vm.Waits.ProgressText);
        Assert.True(h.Vm.Waits.HasResults); // 모으는 중에도 부분 결과가 보인다

        h.Vm.Waits.Cancel();
        await run;

        Assert.Equal(WaitPaneState.Cancelled, h.Vm.Waits.State);
        Assert.Equal(1, h.Tuning.SampleCalls);
        Assert.Single(h.Vm.Waits.Events);
        Assert.Contains("취소", h.Vm.StatusText, StringComparison.Ordinal);
        Assert.True(h.Vm.Waits.CanStart);
        Assert.Equal("다시 모으기", h.Vm.Waits.StartText);
    }

    [Fact]
    public async Task Picking_another_sql_while_sampling_cancels_but_a_list_refresh_does_not()
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Samples = _ => [Sample(1, null)];
        var gate = new TaskCompletionSource();
        h.Vm.Waits.Delay = async (_, ct) =>
        {
            gate.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var run = h.Vm.Waits.StartAsync();
        await gate.Task;

        // 목록 새로 고침: 잠깐 선택이 비었다가 같은 sql_id 로 돌아온다 → 계속 모은다.
        var same = h.Vm.SelectedSql;
        h.Vm.SelectedSql = null;
        h.Vm.SelectedSql = same;
        Assert.True(h.Vm.Waits.IsSampling);

        h.Vm.SelectedSql = h.Vm.Results[1];
        await run;
        Assert.Equal(1, h.Tuning.SampleCalls);

        // 다른 SQL 로 바뀐 뒤에는 그 SQL 의 처음 화면.
        Assert.Equal(WaitPaneState.Ready, h.Vm.Waits.State);
        Assert.Null(h.Vm.Waits.TargetSqlId);
        Assert.Empty(h.Vm.Waits.Events);
    }

    [Fact]
    public async Task No_samples_says_the_sql_did_not_run_and_errors_show_the_first_line()
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Samples = _ => [];
        h.Vm.Waits.DurationSeconds = 5;
        await h.Vm.Waits.StartAsync();
        Assert.Equal(WaitPaneState.Empty, h.Vm.Waits.State);
        Assert.True(h.Vm.Waits.HasMessage);
        Assert.Equal("이 sql_id 가 5 초 동안 실행되지 않았습니다", h.Vm.Waits.Message);

        h.Tuning.Fail = new InvalidOperationException("ORA-00942: table or view does not exist\n자세히");
        await h.Vm.Waits.StartAsync();
        Assert.Equal(WaitPaneState.Error, h.Vm.Waits.State);
        Assert.Equal("ORA-00942: table or view does not exist", h.Vm.Waits.HintText);
        Assert.Equal(1, h.Tuning.SampleCalls - 5);
    }

    [Fact]
    public void Duration_is_clamped_and_states_before_a_session_or_selection_explain_what_to_do()
    {
        var vm = new SqlMonitorViewModel();
        Assert.Equal(WaitPaneState.NoSession, vm.Waits.State);
        vm.Waits.DurationSeconds = 1;
        Assert.Equal(SqlWaitsViewModel.MinSeconds, vm.Waits.DurationSeconds);
        vm.Waits.DurationSeconds = 9999;
        Assert.Equal(SqlWaitsViewModel.MaxSeconds, vm.Waits.DurationSeconds);
        Assert.False(vm.Waits.CanStart);
        Assert.Equal(AwrPaneState.NoSession, vm.Awr.State);
        Assert.Equal(PlanControlsState.NoSession, vm.Controls.State);
    }

    [Fact]
    public async Task Without_a_selection_the_tabs_ask_for_a_row()
    {
        var h = await TuningHarness.StartAsync(select: false);
        Assert.Equal(WaitPaneState.NoSql, h.Vm.Waits.State);
        Assert.Equal("SQL 을 고르세요", h.Vm.Waits.Message);
        h.Vm.DetailTabIndex = SqlMonitorViewModel.ControlsTab;
        await Task.Yield();
        Assert.Equal(PlanControlsState.NoSql, h.Vm.Controls.State);
        Assert.Equal(0, h.Tuning.ControlCalls);
    }

    // ---- 팩 관문 ----

    [Fact]
    public async Task Ash_without_a_declared_pack_explains_why_and_how_and_is_never_sent()
    {
        var h = await TuningHarness.StartAsync();
        Assert.False(h.Vm.HasDiagnostics);
        Assert.False(h.Vm.Waits.CanUseAsh);
        Assert.Contains("Diagnostics Pack", h.Vm.Waits.AshTip, StringComparison.Ordinal);

        h.Vm.Waits.UseAsh = true;
        Assert.Equal(WaitPaneState.LicenseOff, h.Vm.Waits.State);
        Assert.True(h.Vm.Waits.IsLicenseOff);
        Assert.Contains("관리 팩", h.Vm.Waits.HintText, StringComparison.Ordinal);
        Assert.Contains("지금 표본", h.Vm.Waits.HintText, StringComparison.Ordinal);

        await h.Vm.Waits.LoadAshAsync();
        await h.Vm.Waits.StartAsync();
        Assert.Equal(0, h.Tuning.AshCalls);
        Assert.Equal(0, h.Tuning.SampleCalls);

        h.Vm.Waits.UseAsh = false;
        Assert.Equal(WaitPaneState.Ready, h.Vm.Waits.State);
    }

    [Fact]
    public async Task Ash_with_the_pack_reads_the_last_minutes_and_has_no_session_count()
    {
        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics);
        var now = new DateTime(2026, 10, 24, 13, 0, 0);
        h.Vm.Waits.Now = () => now;
        h.Vm.Waits.AshMinutes = 30;
        h.Tuning.Ash = [new AshWaitRow("ON CPU", null, 30), new AshWaitRow("db file sequential read", "User I/O", 10)];
        h.Vm.Waits.ModeIndex = 1;
        Assert.True(h.Vm.Waits.UseAsh);

        await h.Vm.Waits.StartAsync();

        Assert.Equal(1, h.Tuning.AshCalls);
        Assert.Equal(now.AddMinutes(-30), h.Tuning.AshSince);
        Assert.Equal(WaitPaneState.Done, h.Vm.Waits.State);
        Assert.Equal("75 %", h.Vm.Waits.Events[0].ShareText);
        Assert.Equal("—", h.Vm.Waits.Events[0].SessionsText);
        Assert.Equal(0, h.Tuning.SampleCalls);

        h.Tuning.Ash = [];
        await h.Vm.Waits.LoadAshAsync();
        Assert.Equal(WaitPaneState.Empty, h.Vm.Waits.State);
    }

    [Fact]
    public async Task Server_pack_access_none_overrides_the_profile_declaration()
    {
        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics, serverPackAccess: "NONE");
        Assert.Equal(LicensePack.None, SqlMonitorViewModel.HeldPack(h.Session));
        Assert.False(h.Vm.HasDiagnostics);
        var off = h.Vm.DiagnosticsOff();
        Assert.NotNull(off);
        Assert.Contains("control_management_pack_access", off!.Value.Reason, StringComparison.Ordinal);

        h.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        await Task.Yield();
        Assert.Equal(AwrPaneState.LicenseOff, h.Vm.Awr.State);
        Assert.Equal(0, h.Tuning.AwrCalls);
    }

    // ---- AWR 계획 이력 ----

    [Fact]
    public async Task Awr_without_the_pack_is_not_sent_and_offers_the_connection_manager()
    {
        var h = await TuningHarness.StartAsync();
        h.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        await h.Vm.Awr.LoadAsync();

        Assert.Equal(AwrPaneState.LicenseOff, h.Vm.Awr.State);
        Assert.Equal("AWR 계획 이력은 Diagnostics Pack 이 필요합니다", h.Vm.Awr.Message);
        Assert.Contains("dba_hist_*", h.Vm.Awr.HintText, StringComparison.Ordinal);
        Assert.Equal(0, h.Tuning.AwrCalls);

        await h.Vm.OpenConnectionManagerAsync();
        Assert.Equal((Id: "oranda.conn.manage", Parameter: (object?)null), h.Commands.Single());
    }

    [Fact]
    public async Task Awr_history_groups_plans_flags_the_slow_one_and_compares_two_plan_hashes()
    {
        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics);
        h.Tuning.Awr = AwrRows();
        h.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        await h.Vm.Awr.EnsureLoadedAsync();

        Assert.Equal(AwrPaneState.Loaded, h.Vm.Awr.State);
        Assert.Equal(1, h.Tuning.AwrCalls);
        Assert.Equal(4, h.Vm.Awr.Rows.Count);
        Assert.Equal([3392001144, 1838229974], h.Vm.Awr.Plans.Select(p => p.PlanHash));
        Assert.Equal("36.0 ms", h.Vm.Awr.Rows[0].MsPerExecText);
        Assert.Equal("1,120", h.Vm.Awr.Rows[0].ExecutionsText);
        Assert.Equal("10-23 09:00", h.Vm.Awr.Rows[0].SnapText);
        Assert.True(h.Vm.Awr.IsSlowPlan(1838229974));
        Assert.False(h.Vm.Awr.IsSlowPlan(3392001144));
        Assert.Equal("plan_hash 2개 · snap 4개", h.Vm.Awr.SummaryText);
        Assert.True(h.Vm.Awr.CanCompare);

        // 같은 sql_id 로 탭을 다시 열면 다시 읽지 않는다.
        await h.Vm.Awr.EnsureLoadedAsync();
        Assert.Equal(1, h.Tuning.AwrCalls);

        h.Vm.Awr.SelectedRow = h.Vm.Awr.Rows[3];
        Assert.Equal(3, h.Vm.Awr.SelectedIndex);
        Assert.Equal((3392001144, 1838229974), h.Vm.Awr.PickPair());
        await h.Vm.Awr.CompareAsync();

        Assert.Equal([3392001144L, 1838229974L], h.Tuning.AwrPlanHashes);
        Assert.NotNull(h.Vm.Awr.Comparison);
        Assert.True(h.Vm.Awr.HasComparison);
        Assert.True(h.Vm.Awr.CompareHashDiffers);
        Assert.Contains(h.Vm.Awr.CompareRows, r => r.Row.Kind == PlanDiffKind.Changed);
        Assert.Contains("3392001144 → 1838229974", h.Vm.Awr.CompareTitle, StringComparison.Ordinal);
        Assert.Contains("바뀜 1", h.Vm.Awr.CompareSummary, StringComparison.Ordinal);

        h.Vm.Awr.ClearComparison();
        Assert.False(h.Vm.Awr.HasComparison);
    }

    [Fact]
    public async Task Awr_empty_and_failing_plan_steps_have_their_own_messages()
    {
        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics);
        h.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        await h.Vm.Awr.LoadAsync();
        Assert.Equal(AwrPaneState.Empty, h.Vm.Awr.State);
        Assert.Equal("AWR 에 이 sql_id 의 이력이 없습니다", h.Vm.Awr.Message);
        Assert.False(h.Vm.Awr.CanCompare);

        h.Tuning.Awr = AwrRows();
        await h.Vm.Awr.LoadAsync();
        h.Tuning.AwrPlanFail = new NotSupportedException();
        await h.Vm.Awr.CompareAsync();
        Assert.Null(h.Vm.Awr.Comparison);
        Assert.True(h.Vm.Awr.HasComparison);
        Assert.Contains("아직 이 조회를 쓸 수 없습니다", h.Vm.Awr.CompareMessage, StringComparison.Ordinal);

        h.Tuning.AwrPlanFail = null;
        h.Tuning.EmptyAwrPlan = true;
        await h.Vm.Awr.CompareAsync();
        Assert.Contains("남아 있지 않습니다", h.Vm.Awr.CompareMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_another_sql_on_the_awr_tab_reads_it_again()
    {
        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics);
        h.Tuning.Awr = AwrRows();
        h.Vm.DetailTabIndex = SqlMonitorViewModel.AwrTab;
        await h.Vm.Awr.EnsureLoadedAsync();
        Assert.Equal(1, h.Tuning.AwrCalls);

        h.Vm.SelectedSql = h.Vm.Results[1];
        await TuningHarness.SettleAsync(() => h.Tuning.AwrCalls == 2);
        Assert.Equal(OtherSqlId, h.Tuning.LastAwrSqlId);
    }

    // ---- 계획 관리 ----

    [Fact]
    public async Task Plan_controls_list_profiles_baselines_and_patches_with_status()
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Controls =
        [
            new SqlPlanControlRow(SqlPlanControlKind.Profile, "SYS_SQLPROF_0192", "DISABLED", new DateTime(2026, 3, 2), "select\n  1 from dual"),
            new SqlPlanControlRow(SqlPlanControlKind.Baseline, "SQL_PLAN_8k2xa1c4", "ENABLED", new DateTime(2026, 8, 11), "select 1") { Accepted = true },
            new SqlPlanControlRow(SqlPlanControlKind.Baseline, "SQL_PLAN_8k2x77f0", "ENABLED", new DateTime(2026, 10, 24), "select 1") { Accepted = false },
        ];
        h.Vm.DetailTabIndex = SqlMonitorViewModel.ControlsTab;
        await TuningHarness.SettleAsync(() => h.Vm.Controls.State == PlanControlsState.Loaded);

        Assert.Equal(1, h.Tuning.ControlCalls);
        Assert.Equal(["SQL Profile", "SQL Plan Baseline", "SQL Plan Baseline"], h.Vm.Controls.Items.Select(i => i.KindText));
        Assert.Equal("DISABLED", h.Vm.Controls.Items[0].StatusText);
        Assert.Equal("select 1 from dual", h.Vm.Controls.Items[0].SqlPreview);
        Assert.Equal("ENABLED · 미승인", h.Vm.Controls.Items[1].StatusText);
        Assert.True(h.Vm.Controls.Items[1].IsUnaccepted);
        Assert.Equal("ENABLED · ACCEPTED", h.Vm.Controls.Items[2].StatusText);
        Assert.Contains("Baseline 2", h.Vm.Controls.SourceText, StringComparison.Ordinal);
        Assert.Contains("보기만", h.Vm.Controls.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_controls_empty_and_privilege_error_screens()
    {
        var h = await TuningHarness.StartAsync();
        await h.Vm.Controls.LoadAsync();
        Assert.Equal(PlanControlsState.Empty, h.Vm.Controls.State);
        Assert.Equal("이 SQL 에 걸린 Profile·Baseline·Patch 가 없습니다", h.Vm.Controls.Message);

        h.Tuning.ControlsFail = new InvalidOperationException("ORA-01031: insufficient privileges");
        await h.Vm.Controls.LoadAsync();
        Assert.Equal(PlanControlsState.Denied, h.Vm.Controls.State);
        Assert.True(h.Vm.Controls.IsDenied);
        Assert.StartsWith("GRANT SELECT_CATALOG_ROLE TO ", h.Vm.Controls.GrantSql, StringComparison.Ordinal);

        h.Tuning.ControlsFail = new InvalidOperationException("ORA-03113: end-of-file on communication channel");
        await h.Vm.Controls.LoadAsync();
        Assert.Equal(PlanControlsState.Error, h.Vm.Controls.State);
    }

    // ---- 스크립트 만들기(DB 로 보내지 않음) ----

    [Fact]
    public async Task Scripts_go_to_a_new_editor_tab_with_a_writes_warning_and_are_never_sent()
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.Children = [new ChildCursorRow(0, 3392001144, false, false, true, 10, 1000, 380_000)];

        Assert.True(h.Vm.CreateTuningAdvisorScript());
        var (title, body) = h.Editors.Single();
        Assert.Equal($"tuning_advisor_{SqlId}.sql", title);
        Assert.StartsWith($"-- 대상: sql_id {SqlId}", body, StringComparison.Ordinal);
        Assert.Contains("DBMS_SQLTUNE.CREATE_TUNING_TASK", body, StringComparison.Ordinal);
        Assert.Contains("DBMS_SQLTUNE.EXECUTE_TUNING_TASK", body, StringComparison.Ordinal);
        Assert.Contains("REPORT_TUNING_TASK", body, StringComparison.Ordinal);
        Assert.Contains("DB 에 씁니다", body, StringComparison.Ordinal);
        Assert.Contains("Tuning Pack", body, StringComparison.Ordinal);
        Assert.Contains("Tuning Pack", h.Vm.StatusText, StringComparison.Ordinal);

        Assert.True(await h.Vm.CreateBaselineScriptAsync());
        (title, body) = h.Editors[1];
        Assert.Equal($"baseline_{SqlId}.sql", title);
        Assert.Contains("DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE", body, StringComparison.Ordinal);
        Assert.Contains("plan_hash_value => 3392001144", body, StringComparison.Ordinal);
        Assert.Contains("(child 0, 38.0 ms/실행)", body, StringComparison.Ordinal);
        Assert.Contains("DB 에 씁니다", body, StringComparison.Ordinal);

        // 서버로 간 것은 자식 커서 읽기(SELECT) 하나뿐 — 스크립트 문안은 보내지 않는다.
        Assert.Equal(1, h.Tuning.ChildCalls);
        Assert.Equal(0, h.Tuning.SampleCalls + h.Tuning.AwrCalls + h.Tuning.AshCalls + h.Tuning.ControlCalls);
    }

    [Fact]
    public async Task Baseline_script_without_a_child_cursor_targets_all_plans_and_no_selection_does_nothing()
    {
        var h = await TuningHarness.StartAsync();
        h.Tuning.ChildFail = new InvalidOperationException("ORA-00942");
        Assert.True(await h.Vm.CreateBaselineScriptAsync());
        Assert.DoesNotContain("plan_hash_value =>", h.Editors.Single().Body, StringComparison.Ordinal);
        Assert.Contains("모든 계획", h.Vm.StatusText, StringComparison.Ordinal);

        h.Vm.SelectedSql = null;
        Assert.False(h.Vm.CreateTuningAdvisorScript());
        Assert.False(await h.Vm.CreateBaselineScriptAsync());
        Assert.Single(h.Editors);
    }

    // ---- 바로 가기 ----

    [Fact]
    public async Task Child_cursor_and_lock_monitor_shortcuts_appear_only_when_their_commands_exist()
    {
        var h = await TuningHarness.StartAsync();
        Assert.False(h.Vm.CanOpenChildCursors);
        Assert.False(h.Vm.CanOpenLockMonitor);
        await h.Vm.OpenChildCursorsAsync();
        Assert.Empty(h.Commands);

        h.Available.Add(SqlMonitorViewModel.ChildCursorsCommand);
        h.Available.Add(SqlMonitorViewModel.LockMonitorCommand);
        Assert.True(h.Vm.CanOpenChildCursors);
        await h.Vm.OpenChildCursorsAsync();
        await h.Vm.OpenLockMonitorAsync();
        Assert.Equal([("oranda.tuning.childCursors", (object?)SqlId), ("oranda.monitor.locks", null)], h.Commands);

        h.Tuning.Samples = _ => [Sample(5, "enq: TX - row lock contention", "Application"), Sample(6, "log file sync", "Commit")];
        h.Vm.Waits.DurationSeconds = 5;
        await h.Vm.Waits.StartAsync();
        Assert.True(h.Vm.Waits.Events.Single(e => e.IsApplication).ShowLockLink);
        Assert.False(h.Vm.Waits.Events.Single(e => !e.IsApplication).ShowLockLink);
        Assert.Equal("기타", h.Vm.Waits.Classes.Last().Share.Label);
    }

    [Fact]
    public async Task Stopping_the_monitor_resets_the_tabs()
    {
        var h = await TuningHarness.StartAsync(pack: LicensePack.Diagnostics);
        h.Tuning.Awr = AwrRows();
        await h.Vm.Awr.LoadAsync();
        Assert.Equal(AwrPaneState.Loaded, h.Vm.Awr.State);

        await h.Vm.StopAsync();
        Assert.Equal(AwrPaneState.NoSession, h.Vm.Awr.State);
        Assert.Empty(h.Vm.Awr.Rows);
        Assert.Equal(WaitPaneState.NoSession, h.Vm.Waits.State);
        Assert.Equal(PlanControlsState.NoSession, h.Vm.Controls.State);
    }

    internal static WaitSample Sample(long sid, string? ev, string? cls = null) => new(DateTime.Now, sid, SqlId, ev, cls);

    internal static List<AwrPlanRow> AwrRows() =>
    [
        new(101, new DateTime(2026, 10, 23, 9, 0, 0), 3392001144, 1120, 1120 * 36_000, 1120 * 5_000),
        new(102, new DateTime(2026, 10, 23, 21, 0, 0), 3392001144, 980, 980 * 41_000, 980 * 5_200),
        new(103, new DateTime(2026, 10, 24, 9, 0, 0), 1838229974, 1402, 1402L * 1_790_000, 1402 * 42_000),
        new(104, new DateTime(2026, 10, 24, 13, 0, 0), 1838229974, 1611, 1611L * 1_842_000, 1611 * 42_180),
    ];
}

/// <summary>시험용 튜닝 서비스: 부른 횟수·인자를 기록하고 정한 값을 돌려준다.</summary>
internal sealed class FakeTuning : ITuningService
{
    public Func<int, IReadOnlyList<WaitSample>> Samples { get; set; } = _ => [];

    public List<AwrPlanRow> Awr { get; set; } = [];

    public IReadOnlyList<AshWaitRow> Ash { get; set; } = [];

    public IReadOnlyList<SqlPlanControlRow> Controls { get; set; } = [];

    public IReadOnlyList<ChildCursorRow> Children { get; set; } = [];

    public Exception? Fail { get; set; }

    public Exception? ControlsFail { get; set; }

    public Exception? ChildFail { get; set; }

    public Exception? AwrPlanFail { get; set; }

    public bool EmptyAwrPlan { get; set; }

    public int SampleCalls { get; private set; }

    public List<string?> SampledIds { get; } = [];

    public int AwrCalls { get; private set; }

    public string? LastAwrSqlId { get; private set; }

    public List<long> AwrPlanHashes { get; } = [];

    public int AshCalls { get; private set; }

    public DateTime AshSince { get; private set; }

    public int ControlCalls { get; private set; }

    public int ChildCalls { get; private set; }

    public Task<IReadOnlyList<WaitSample>> SampleWaitsAsync(ISession s, string? sqlId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var i = SampleCalls++;
        SampledIds.Add(sqlId);
        return Fail is { } ex ? Task.FromException<IReadOnlyList<WaitSample>>(ex) : Task.FromResult(Samples(i));
    }

    public Task<IReadOnlyList<AwrPlanRow>> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct)
    {
        AwrCalls++;
        LastAwrSqlId = sqlId;
        return Task.FromResult<IReadOnlyList<AwrPlanRow>>(Awr);
    }

    public Task<PlanResult> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct)
    {
        if (AwrPlanFail is { } ex)
        {
            throw ex;
        }

        AwrPlanHashes.Add(planHashValue);
        if (EmptyAwrPlan)
        {
            return Task.FromResult(new PlanResult([], string.Empty));
        }

        // 빠른 계획 = 인덱스, 느린 계획 = 전체 읽기.
        var fast = planHashValue == 3392001144;
        IReadOnlyList<PlanStep> steps =
        [
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = fast ? 40 : 9000 },
            new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = fast ? "BY INDEX ROWID" : "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = fast ? 40 : 9000 },
        ];
        return Task.FromResult(new PlanResult(steps, "plan") { PlanHashValue = planHashValue });
    }

    public Task<IReadOnlyList<AshWaitRow>> GetAshWaitsAsync(ISession s, string sqlId, DateTime since, CancellationToken ct)
    {
        AshCalls++;
        AshSince = since;
        return Task.FromResult(Ash);
    }

    public Task<IReadOnlyList<SqlPlanControlRow>> GetSqlPlanControlsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        ControlCalls++;
        return ControlsFail is { } ex ? Task.FromException<IReadOnlyList<SqlPlanControlRow>>(ex) : Task.FromResult(Controls);
    }

    public Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        ChildCalls++;
        return ChildFail is { } ex ? Task.FromException<IReadOnlyList<ChildCursorRow>>(ex) : Task.FromResult(Children);
    }
}

/// <summary>열린 SQL 모니터(가짜 모니터·튜닝 서비스·세션) 하나.</summary>
internal sealed class TuningHarness
{
    private TuningHarness(LicensePack pack, string? serverPackAccess)
    {
        Session = new TuningFakeSession(pack, serverPackAccess);
        Vm = new SqlMonitorViewModel(new TopSqlMonitor(), null, _ => Task.FromResult<ISession?>(Session), Keys)
        {
            PostToUi = a => a(),
            TuningService = Tuning,
            CommandAvailable = id => Available.Contains(id),
            RunCommand = (id, p) =>
            {
                Commands.Add((id, p));
                return Task.CompletedTask;
            },
            OpenEditor = (t, b) => Editors.Add((t, b)),
        };
        Vm.Waits.Delay = (d, _) =>
        {
            Delays.Add(d);
            return Task.CompletedTask;
        };
    }

    public FakeTuning Tuning { get; } = new();

    public ContextKeyService Keys { get; } = new();

    public TuningFakeSession Session { get; }

    public SqlMonitorViewModel Vm { get; }

    public HashSet<string> Available { get; } = [];

    public List<(string Id, object? Parameter)> Commands { get; } = [];

    public List<(string Title, string Body)> Editors { get; } = [];

    public List<TimeSpan> Delays { get; } = [];

    public static async Task<TuningHarness> StartAsync(LicensePack pack = LicensePack.None, string? serverPackAccess = null, bool select = true)
    {
        var h = new TuningHarness(pack, serverPackAccess);
        await h.Vm.StartAsync();
        if (select)
        {
            h.Vm.SelectedSql = h.Vm.Results[0];
        }

        return h;
    }

    /// <summary>뒤에서 시작한 읽기(탭 열기·선택 바꿈)가 끝날 때까지.</summary>
    public static async Task SettleAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(done());
    }

    private sealed class TopSqlMonitor : IMonitorService
    {
        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
        {
            IReadOnlyList<TopSqlRow> rows =
            [
                new(new CursorRef(SqlMonitorTuningTests.SqlId, 0, 1), "SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh WHERE lh.eqp_id = :eqp")
                {
                    Executions = 18_220, ElapsedSeconds = 33_561, CpuSeconds = 21_000, BufferGets = 768_519_600, ParsingSchema = "MES",
                },
                new(new CursorRef(SqlMonitorTuningTests.OtherSqlId, 0, 1), "UPDATE MES.LOT_HIST SET EVT_STAT = 'DONE' WHERE lot_id = :1")
                {
                    Executions = 402_118, ElapsedSeconds = 8_812, CpuSeconds = 4_000, BufferGets = 2_010_590, ParsingSchema = "MES",
                },
            ];
            return Task.FromResult(rows);
        }

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) => Task.FromResult(new SqlDetail(cursor, "select 1"));

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => throw new NotSupportedException();

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();
    }
}

internal sealed class TuningFakeSession(LicensePack pack, string? serverPackAccess) : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = new("MESPRD", "mesadm", "h:1521/s") { LicensePack = pack };

    public ServerInfo Server { get; } = new("19.21.0.0.0", "MESPRD1", "h", "MESPRD", "MESPRD", null, false, 1, new ServerCapabilities(true, true, true, true, true),
        ManagementPackAccess: serverPackAccess);

    public SessionPurpose Purpose => SessionPurpose.Monitor;

    public string DbKey => "MESPRD/MESADM";

    public SessionState State => SessionState.Idle;

    public bool HasPendingTransaction => false;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
