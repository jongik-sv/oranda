using System.Globalization;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.LockMonitor;

/// <summary>TSK-05-03 Lock 모니터 화면 모델: 트리·목록·영향 순위·상세·힌트·거르기·상태·종료 확인·자동 새로 고침·명령 등록.</summary>
public class LockMonitorTests
{
    private static readonly ShellContributions App = ShellContributions.Scan(typeof(LockMonitorViewModel).Assembly);

    private static readonly string[] CsvSamples = ["a", "b,c", "say \"hi\""];

    private static async Task<LockHarness> StartedAsync(ServerCapabilities? caps = null)
    {
        var h = new LockHarness(caps);
        await h.Vm.StartAsync();
        return h;
    }

    // ---- 등록 ----

    [Fact]
    public void Commands_and_panel_are_registered_under_the_monitor_menu()
    {
        var registry = new CommandRegistry(App.Commands);
        foreach (var id in LockMonitorCommands.HandledIds)
        {
            var def = registry.Find(id);
            Assert.NotNull(def);
            Assert.Equal(CommandCategory.Monitor, def!.Category);
        }

        Assert.Contains(LockMonitorCommands.ShowHistory, LockMonitorCommands.HandledIds); // TSK-05-04: 이력 띠 보이기·감추기
        Assert.Contains(App.Panels, p => p.Id == LockMonitorCommands.PanelId && p.Kind == ToolPanelKind.Document);
        var open = registry.Find(LockMonitorCommands.Open)!;
        Assert.Contains("menu:monitor/locks@15", open.Placements);
        Assert.True(registry.CanExecute(LockMonitorCommands.Open, new ContextKeyService()));
    }

    [Fact]
    public void Window_commands_show_only_while_the_lock_monitor_is_open_and_explain_why_they_are_grey()
    {
        var registry = new CommandRegistry(App.Commands);
        var kill = registry.Find(LockMonitorCommands.KillHolder)!;
        var keys = new ContextKeyService();
        Assert.False(MenuVisibility.InScope(kill.When, keys));
        keys.Set("lockMonitorOpen", true);
        Assert.True(MenuVisibility.InScope(kill.When, keys));
        Assert.False(registry.CanExecute(LockMonitorCommands.KillHolder, keys));
        var reason = MenuVisibility.Reason(kill.Enablement, keys, k => k);
        Assert.Contains("lockSessionSelected", reason, StringComparison.Ordinal);
        keys.Set("lockSessionSelected", true);
        keys.Set("lockCanKillSession", true);
        Assert.True(registry.CanExecute(LockMonitorCommands.KillHolder, keys));
    }

    [Fact]
    public void F5_refreshes_locks_only_in_the_lock_monitor_slot()
    {
        Assert.Equal(["lockMonitor"], FocusScopes.SlotsOf("lockMonitorFocus"));
        Assert.False(FocusScopes.Overlaps("lockMonitorFocus", "monitorFocus"));
        Assert.True(FocusScopes.Overlaps("lockMonitorFocus", null));
    }

    // ---- 열기·상태 ----

    [Fact]
    public async Task Start_fills_tree_list_impact_and_hints_and_sets_keys()
    {
        var h = await StartedAsync();
        var vm = h.Vm;

        Assert.True(vm.IsOpen);
        Assert.Equal(LockViewState.Data, vm.State);
        Assert.Equal(8, vm.TreeRows.Count);
        Assert.Equal([0, 1, 2, 1, 0, 1, 0, 1], vm.TreeRows.Select(t => t.Depth));
        var root = vm.TreeRows[0];
        Assert.True(root.IsHolder);
        Assert.Equal("1·1287", root.KeyText);
        Assert.Equal("보유 · 대기자 3", root.EventText);
        Assert.True(root.IsInactive);
        Assert.Equal("(prev) 4w1xq9n2b7c0d", root.SqlText);
        Assert.Equal("MES.LOT_HIST", root.ObjectText);
        Assert.Equal("287 초", vm.TreeRows[1].WaitText);
        Assert.Equal("2·0388", vm.TreeRows[2].KeyText);
        Assert.False(vm.TreeRows[6].IsInactive); // HR 은 현재 SQL 이 있다
        Assert.True(vm.TreeRows[2].TreeLines!.IsLast);

        Assert.Equal(11, vm.Locks.Count);
        Assert.Equal("3·5", vm.TreeCountText);
        Assert.Equal(["1", "1·1287", "MES_APP", "INACTIVE", "3", "287 초", "631 초", "312 초", "JDBC Thin Client"], vm.Impacts[0].Cells);
        Assert.Equal("보유자 3 · 대기 5 · 잠금 11행 · 최대 대기 287 초", vm.StatusText);
        Assert.Contains("읽음", vm.StatusRight, StringComparison.Ordinal);
        Assert.True(h.Keys.Get("lockMonitorOpen"));
        Assert.True(h.Keys.Get("lockCanKillSession"));
        Assert.False(h.Keys.Get("lockSessionSelected"));
        Assert.True(h.Monitor.LastFilter!.WaitingOrBlockingOnly);
        Assert.Equal(0, h.Monitor.SqlCalls); // gv$sql 은 고르기 전에 읽지 않는다

        await vm.StopAsync();
        Assert.True(h.Session.Disposed);
        Assert.Equal(LockViewState.NoConnection, vm.State);
        Assert.False(h.Keys.Get("lockMonitorOpen"));
    }

    [Fact]
    public async Task Lock_cells_show_modes_units_and_the_resource_each_tx_waits_on()
    {
        var h = await StartedAsync();
        var cells = h.Vm.Locks.Select(l => l.Cells).ToList();

        Assert.Equal(["1·1287", "MES_APP", "TX", "MES.LOT_HIST (행)", "X (6)", "—", "312 초", "차단"], cells[0].Take(8));
        Assert.Equal("48 블록", cells[0][9]);
        Assert.Equal("327706", cells[0][10]);
        Assert.Equal("S (4)", cells[7][5]); // TM 요청 모드
        Assert.Equal("HR.ORDERS (블록 ITL)", cells[8][3]);
        Assert.True(LockItem.IsDanger(7, cells[0]));
        Assert.False(LockItem.IsDanger(5, cells[0]));
        Assert.True(LockItem.IsDanger(5, cells[2]));
        Assert.False(LockItem.IsDanger(7, cells[1]));
    }

    [Fact]
    public async Task Hints_name_the_cause_of_each_wait_and_offer_a_read_only_fk_check()
    {
        var h = await StartedAsync();
        var hints = h.Vm.Hints;

        Assert.Equal(4, hints.Count);
        Assert.Equal("3", h.Vm.HintCountText);
        Assert.Equal(LockHintSeverity.Danger, hints[0].Severity);
        Assert.Contains("대기 3 세션", hints[0].Title, StringComparison.Ordinal);
        Assert.Contains("1·1287", hints[0].Body, StringComparison.Ordinal); // INACTIVE 보유자
        Assert.Equal("enq: TM - contention · SCOTT.EMP", hints[1].Title);
        Assert.Contains("외래 키", hints[1].Body, StringComparison.Ordinal);
        var check = hints[1].CopySql!;
        Assert.StartsWith("SELECT", check, StringComparison.Ordinal);
        Assert.Contains("c.owner = 'SCOTT'", check, StringComparison.Ordinal);
        Assert.Contains("c.table_name = 'EMP'", check, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", check, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LockHintSeverity.Info, hints[2].Severity);
        Assert.True(hints[3].IsMuted);
    }

    [Fact]
    public void Library_cache_waits_make_the_last_hint_live()
    {
        var node = new BlockingNode(new SessionKey(1, 1, 1), [new BlockingNode(new SessionKey(1, 2, 2), []) { Event = "library cache pin" }]);
        var items = LockHintsTestBridge.Items(new LockSnapshot([], new BlockingTree([node]), DateTimeOffset.Now));
        var hints = LockHints.Build(items);
        Assert.Equal(LockHintSeverity.Warning, hints[^1].Severity);
        Assert.Contains("DDL 잠금", hints[^1].Todo, StringComparison.Ordinal);
        Assert.Single(hints);
    }

    [Fact]
    public async Task Picking_a_holder_reads_holder_and_longest_waiter_sql_once()
    {
        var h = await StartedAsync();
        var vm = h.Vm;

        vm.SelectedKey = FakeLockMonitor.H1;
        await vm.LoadDetailAsync();
        Assert.True(h.Keys.Get("lockSessionSelected"));
        Assert.True(h.Keys.Get("lockWaiterSelected"));
        Assert.Equal(FakeLockMonitor.W1, vm.SelectedPair.Waiter!.Key);
        Assert.True(vm.HolderHasNoCurrentSql);
        Assert.Contains("보유자 · 1·1287 MES_APP · INACTIVE 312 초", vm.HolderCaption, StringComparison.Ordinal);
        Assert.Contains("SET EVT_STAT = 'DONE'", vm.HolderSql, StringComparison.Ordinal);
        Assert.StartsWith("-- 4w1xq9n2b7c0d", vm.HolderSql, StringComparison.Ordinal);
        Assert.Contains("대기자 · 1·1422 MES_APP · 287 초", vm.WaiterCaption, StringComparison.Ordinal);
        Assert.Contains("'RUN'", vm.WaiterSql, StringComparison.Ordinal);
        Assert.Equal(2, h.Monitor.SqlCalls);
        Assert.Contains("1·1287 의 잠금 2행", vm.LockCaption, StringComparison.Ordinal);

        // 자동 새로 고침(keepDetail)은 같은 세션의 SQL 을 다시 읽지 않고 고름을 남긴다.
        await vm.RefreshKeepingDetailAsync();
        Assert.Equal(FakeLockMonitor.H1, vm.SelectedKey);
        Assert.Equal(2, h.Monitor.SqlCalls);

        // F5 는 다시 읽는다.
        await vm.RefreshAsync();
        Assert.Equal(4, h.Monitor.SqlCalls);

        var copy = vm.SqlForCopy();
        Assert.Contains("-- 보유자 · 1·1287", copy, StringComparison.Ordinal);
        string? opened = null;
        vm.OpenEditor = t => opened = t;
        vm.OpenSqlInEditor();
        Assert.Equal(copy, opened);
    }

    [Fact]
    public async Task Picking_a_waiter_shows_its_parent_as_holder()
    {
        var h = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.W2;
        var (holder, waiter) = h.Vm.SelectedPair;
        Assert.Equal(FakeLockMonitor.W1, holder!.Key);
        Assert.Equal(FakeLockMonitor.W2, waiter!.Key);
        Assert.False(h.Vm.HolderHasNoCurrentSql);
    }

    [Fact]
    public async Task Locked_row_tab_reads_the_row_only_for_row_lock_waits()
    {
        var h = await StartedAsync();
        var vm = h.Vm;
        vm.DetailTabIndex = LockMonitorViewModel.RowTab;

        vm.SelectedKey = FakeLockMonitor.W1;
        await vm.LoadDetailAsync();
        Assert.True(vm.HasLockedRow);
        Assert.Equal("AAAWkqAAHAAAAm/AAC", vm.LockedRowId);
        Assert.Equal(4, vm.LockedRowCells.Count);
        Assert.Equal(["EVT_STAT", "(null)"], vm.LockedRowCells[3]);
        Assert.Equal("SELECT * FROM \"MES\".\"LOT_HIST\" WHERE ROWID = 'AAAWkqAAHAAAAm/AAC'", vm.LockedRowSelect);
        Assert.Equal(1, h.Monitor.RowCalls);

        vm.SelectedKey = FakeLockMonitor.W4; // TM 대기
        await vm.LoadDetailAsync();
        Assert.False(vm.HasLockedRow);
        Assert.Contains("행 잠금 대기가 아닙니다", vm.LockedRowMessage, StringComparison.Ordinal);
        Assert.Equal(1, h.Monitor.RowCalls);

        h.Monitor.LockedRow = null;
        vm.SelectedKey = FakeLockMonitor.W3;
        await vm.LoadDetailAsync();
        Assert.Contains("찾지 못했습니다", vm.LockedRowMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locked_row_error_column_is_shown_as_a_message()
    {
        var h = await StartedAsync();
        h.Monitor.LockedRow = new LockedRowInfo("AAAX", "MES", "LOT_HIST")
        {
            ColumnNames = [LockMonitorViewModel.LockedRowErrorColumn],
            Values = ["ORA-01031: 권한 부족"],
        };
        h.Vm.DetailTabIndex = LockMonitorViewModel.RowTab;
        h.Vm.SelectedKey = FakeLockMonitor.W1;
        await h.Vm.LoadDetailAsync();
        Assert.False(h.Vm.HasLockedRow);
        Assert.Contains("ORA-01031", h.Vm.LockedRowMessage, StringComparison.Ordinal);
        Assert.Empty(h.Vm.LockedRowCells);
    }

    [Fact]
    public async Task Bind_tab_reads_captured_binds_of_the_waiter()
    {
        var h = await StartedAsync();
        h.Vm.DetailTabIndex = LockMonitorViewModel.BindTab;
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        await h.Vm.LoadDetailAsync();
        Assert.True(h.Vm.HasBinds);
        Assert.Equal([":2", "2", "VARCHAR2(32)", "ETCH-09", FakeLockMonitor.Now.AddSeconds(-282).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)], h.Vm.BindCells[1]);
        Assert.Contains("대기자 1·1422", h.Vm.BindCaption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ddl_tab_reads_on_demand_and_locks_itself_when_denied()
    {
        var h = await StartedAsync();
        Assert.Equal(0, h.Monitor.DdlCalls);
        h.Vm.TopTabIndex = LockMonitorViewModel.DdlTab;
        await h.Vm.LoadDdlLocksAsync();
        Assert.Equal(1, h.Monitor.DdlCalls);
        Assert.Equal(3, h.Vm.DdlLocks.Count);
        Assert.True(h.Vm.DdlLocks[1].IsWaiting);
        Assert.Contains("1·0815", h.Vm.DdlSummary, StringComparison.Ordinal);
        Assert.Contains("2 세션", h.Vm.DdlSummary, StringComparison.Ordinal);

        var denied = new LockHarness();
        denied.Monitor.DdlFail = new InvalidOperationException("ORA-00942: table or view does not exist");
        await denied.Vm.StartAsync();
        await denied.Vm.LoadDdlLocksAsync();
        Assert.True(denied.Vm.DdlDenied);
        Assert.Equal("GRANT SELECT ON sys.dba_ddl_locks TO MESADM;", denied.Vm.DdlGrantSql);
        Assert.False(denied.Keys.Get("canQueryDdlLocks"));
        Assert.Equal(LockViewState.Data, denied.Vm.State); // 나머지는 그대로
    }

    [Fact]
    public async Task Filters_go_to_the_service_and_change_the_empty_hint()
    {
        var h = new LockHarness();
        h.Monitor.Quiet = true;
        await h.Vm.StartAsync();
        Assert.Equal(LockViewState.Empty, h.Vm.State);
        Assert.Contains("「대기·차단만」", h.Vm.EmptyHint, StringComparison.Ordinal);

        h.Vm.SetMinWaitThree();
        Assert.Equal(3, h.Monitor.LastFilter!.MinWaitSeconds);
        Assert.Contains("「최소 대기 3 초」·「대기·차단만」", h.Vm.EmptyHint, StringComparison.Ordinal);
        Assert.True(h.Vm.HasFilters);

        h.Vm.UsernameFilter = "  scott ";
        h.Vm.SchemaFilter = "";
        await h.Vm.RefreshAsync();
        Assert.Equal("scott", h.Monitor.LastFilter!.Username);
        Assert.Null(h.Monitor.LastFilter.Schema);

        await h.Vm.ClearFiltersAsync();
        Assert.Equal(0, h.Monitor.LastFilter!.MinWaitSeconds);
        Assert.Null(h.Monitor.LastFilter.Username);
        Assert.True(h.Monitor.LastFilter.WaitingOrBlockingOnly);

        await h.Vm.ShowAllLocksAsync();
        Assert.False(h.Monitor.LastFilter!.WaitingOrBlockingOnly);
        Assert.False(h.Vm.HasFilters);
        Assert.Contains("거르기는 꺼져", h.Vm.EmptyHint, StringComparison.Ordinal);

        h.Vm.MinWaitSeconds = -5;
        Assert.Equal(0, h.Vm.MinWaitSeconds);
    }

    [Fact]
    public async Task Instance_choices_come_from_the_rows_and_filter_by_instance()
    {
        var h = await StartedAsync();
        Assert.Equal(["인스턴스: 전체", "인스턴스 1", "인스턴스 2"], h.Vm.InstanceChoices);
        h.Vm.SelectedInstanceIndex = 2;
        Assert.Equal(2, h.Monitor.LastFilter!.InstanceId);
        h.Vm.SelectedInstanceIndex = 0;
        Assert.Null(h.Monitor.LastFilter!.InstanceId);
    }

    [Fact]
    public async Task Account_without_v_dollar_access_shows_the_banner()
    {
        var h = await StartedAsync(LockHarness.NoPrivilege);
        Assert.False(h.Vm.IsOpen);
        Assert.Equal(LockViewState.Denied, h.Vm.State);
        Assert.True(h.Session.Disposed);
        Assert.True(h.Vm.Access.HasNotice);
        Assert.Equal("GRANT SELECT_CATALOG_ROLE TO MESADM;", h.Vm.Access.GrantSql);
        Assert.Equal(0, h.Monitor.LockCalls);
    }

    [Fact]
    public async Task Privilege_error_while_reading_switches_to_denied_and_stops_auto_refresh()
    {
        var h = await StartedAsync();
        h.Vm.AutoRefresh = true;
        h.Monitor.Fail = new InvalidOperationException("ORA-01031: insufficient privileges");
        await h.Vm.RefreshAsync();
        Assert.Equal(LockViewState.Denied, h.Vm.State);
        Assert.False(h.Vm.AutoRefresh);
        Assert.Empty(h.Vm.TreeRows);
        Assert.Equal("권한 없음 · ORA-01031", h.Vm.StatusText);
    }

    [Fact]
    public async Task Lost_monitor_session_keeps_the_last_data_dimmed_and_offers_reconnect()
    {
        var h = await StartedAsync();
        h.Vm.AutoRefresh = true;
        h.Monitor.Fail = new InvalidOperationException("ORA-03113: end-of-file on communication channel");
        await h.Vm.RefreshAsync();

        Assert.True(h.Vm.HasError);
        Assert.True(h.Vm.ErrorIsDisconnect);
        Assert.True(h.Vm.IsStale);
        Assert.False(h.Vm.AutoRefresh);
        Assert.Equal(LockViewState.Data, h.Vm.State);
        Assert.Equal(8, h.Vm.TreeRows.Count);
        Assert.Contains("모니터 세션이 끊겼습니다 — ORA-03113", h.Vm.ErrorText, StringComparison.Ordinal);
        Assert.Contains("마지막으로 읽은", h.Vm.StatusRight, StringComparison.Ordinal);

        h.Monitor.Fail = null;
        await h.Vm.ReconnectAsync();
        Assert.Equal(2, h.Opened);
        Assert.False(h.Vm.HasError);
        Assert.False(h.Vm.IsStale);
    }

    [Fact]
    public async Task Other_errors_show_a_retry_banner_without_stopping_auto_refresh()
    {
        var h = await StartedAsync();
        h.Vm.AutoRefresh = true;
        h.Monitor.Fail = new InvalidOperationException("ORA-01652: unable to extend temp segment");
        await h.Vm.RefreshAsync();
        Assert.False(h.Vm.ErrorIsDisconnect);
        Assert.True(h.Vm.AutoRefresh);
        Assert.Contains("읽지 못했습니다", h.Vm.ErrorText, StringComparison.Ordinal);
        h.Vm.AutoRefresh = false;
    }

    [Fact]
    public async Task No_session_means_no_connection_state()
    {
        var vm = new LockMonitorViewModel(new FakeLockMonitor(), _ => Task.FromResult<ISession?>(null)) { PostToUi = a => a() };
        await vm.StartAsync();
        Assert.Equal(LockViewState.NoConnection, vm.State);
        Assert.Equal("접속 없음", vm.StatusText);
        Assert.False(vm.CanKill);
    }

    [Fact]
    public async Task First_read_can_be_cancelled()
    {
        var h = new LockHarness();
        h.Monitor.Hold = new TaskCompletionSource();
        var start = h.Vm.StartAsync();
        Assert.Equal(LockViewState.Loading, h.Vm.State);
        Assert.True(h.Vm.IsBusy);
        h.Vm.CancelLoad();
        await start;
        Assert.False(h.Vm.IsBusy);
        Assert.Equal(LockViewState.Empty, h.Vm.State);
        Assert.Contains("취소", h.Vm.EmptyHint, StringComparison.Ordinal);
    }

    // ---- 종료·끊기·이동 ----

    [Fact]
    public async Task Kill_confirmation_lists_the_waiters_and_runs_only_after_yes()
    {
        var h = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        h.Answer = false;

        await h.Vm.KillAsync();
        Assert.Empty(h.Monitor.Killed);
        var r = h.Asked!;
        Assert.Equal("세션 1·1287 을 종료할까요?", r.Question);
        Assert.Equal("이 세션 때문에 3개 세션이 기다립니다.", r.WaiterHeadline);
        Assert.Equal("1·1422 MES_APP (287 초) · 2·0388 MES_RPT (141 초, 1·1422 뒤) · 2·1903 MES_BATCH (203 초)", r.WaiterLine);
        Assert.Equal("ALTER SYSTEM KILL SESSION '1287,40211,@1' IMMEDIATE", r.Statement);
        Assert.Equal("운영 MESADM@MESPRD", r.HeaderTitle);
        Assert.Contains("RAC 인스턴스 1", r.HeaderDetail, StringComparison.Ordinal);
        Assert.Equal(ConnectionEnvironment.Production, r.Environment);
        Assert.Contains(r.Facts, f => f.Key == "트랜잭션" && f.Value == "312 초 · undo 48 블록");
        Assert.Contains(r.Facts, f => f.Key == "사용자" && f.Value == "MES_APP · was-mes01 · JDBC Thin Client");
        Assert.Contains("취소했습니다", h.Vm.StatusText, StringComparison.Ordinal);

        h.Answer = true;
        var before = h.Monitor.LockCalls;
        await h.Vm.KillAsync();
        Assert.Equal((FakeLockMonitor.H1, true), Assert.Single(h.Monitor.Killed));
        Assert.True(h.Monitor.LockCalls > before);
        Assert.Contains("종료했습니다", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiter_without_waiters_gets_no_warning_and_disconnect_uses_the_execution_engine_text()
    {
        var h = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.W5;
        var r = h.Vm.BuildKillRequest(disconnect: true)!;
        Assert.Null(r.WaiterHeadline);
        Assert.Equal("세션 1·0533 의 연결을 끊을까요?", r.Question);
        Assert.Equal("ALTER SYSTEM DISCONNECT SESSION '533,5002,@1' IMMEDIATE", r.Statement);
        Assert.Equal("연결 끊기", r.ActionText);
        Assert.Contains("세션 1·0533", r.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_is_off_without_selection_or_alter_system()
    {
        var h = await StartedAsync(LockHarness.ReadOnly);
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        Assert.False(h.Vm.CanKill);
        Assert.False(h.Keys.Get("lockCanKillSession"));
        await h.Vm.KillAsync();
        Assert.Null(h.Asked);

        var ok = await StartedAsync();
        Assert.False(ok.Vm.CanKill);
        ok.Vm.SelectedKey = FakeLockMonitor.H1;
        Assert.True(ok.Vm.CanKill);
    }

    [Fact]
    public async Task Go_to_session_monitor_passes_sid_and_instance()
    {
        var h = await StartedAsync();
        SessionKey? went = null;
        h.Vm.GoToSessionMonitor = k =>
        {
            went = k;
            return Task.CompletedTask;
        };
        await h.Vm.GoToSessionAsync();
        Assert.Null(went);
        h.Vm.SelectedKey = FakeLockMonitor.W2;
        await h.Vm.GoToSessionAsync();
        Assert.Equal(FakeLockMonitor.W2, went);
    }

    [Fact]
    public void Session_monitor_selects_a_session_now_or_when_it_appears()
    {
        var vm = new SessionMonitorViewModel();
        vm.Sessions.Add(new SessionItem(new SessionRow(new SessionKey(2, 388, 9002), "ACTIVE")));
        vm.SelectWhenListed(new SessionKey(2, 388, 9002));
        Assert.Equal(388, vm.SelectedSession!.Row.Key.Sid);
        vm.SelectWhenListed(new SessionKey(1, 999, 0)); // 아직 없음 — 기억만
        Assert.Equal(388, vm.SelectedSession!.Row.Key.Sid);
    }

    [Fact]
    public async Task Show_plan_uses_the_waiter_sql_on_the_monitor_session()
    {
        var h = await StartedAsync();
        string? sqlId = null;
        ISession? used = null;
        h.Vm.ShowPlan = (s, c, _) =>
        {
            used = s;
            sqlId = c.SqlId;
            return Task.CompletedTask;
        };
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        await h.Vm.ShowPlanAsync();
        Assert.Equal("7h35uxf5uhmm1", sqlId);
        Assert.Same(h.Session, used);
    }

    // ---- 트리·복사·CSV·자동 새로 고침 ----

    [Fact]
    public async Task Collapsing_a_holder_hides_its_waiters_and_survives_refresh()
    {
        var h = await StartedAsync();
        h.Vm.ToggleExpanded(h.Vm.TreeRows[0]);
        Assert.Equal(5, h.Vm.TreeRows.Count);
        Assert.Equal("▸", h.Vm.TreeRows[0].ExpanderGlyph);
        await h.Vm.RefreshAsync();
        Assert.Equal(5, h.Vm.TreeRows.Count);
        h.Vm.ToggleExpanded(h.Vm.TreeRows[0]);
        Assert.Equal(8, h.Vm.TreeRows.Count);
        h.Vm.ToggleExpanded(h.Vm.TreeRows[2]); // 잎은 그대로
        Assert.Equal(8, h.Vm.TreeRows.Count);
    }

    [Fact]
    public async Task Copy_and_csv_cover_tree_and_list_with_quoting()
    {
        var h = await StartedAsync();
        var tsv = h.Vm.BuildCopyText().ReplaceLineEndings("\n");
        Assert.StartsWith("세션\t사용자\t상태\t대기\t이벤트\t객체\tsql_id\n1·1287\tMES_APP\tINACTIVE", tsv, StringComparison.Ordinal);
        Assert.Contains("\n    2·0388\tMES_RPT", tsv, StringComparison.Ordinal);
        Assert.Contains("인스턴스·SID\t사용자\t유형", tsv, StringComparison.Ordinal);

        var csv = h.Vm.BuildLocksCsv();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(12, lines.Length);
        Assert.Equal("인스턴스·SID,사용자,유형,객체 / 자원,보유,요청,ctime,block,트랜잭션 시작,undo,id1,id2", lines[0]);
        Assert.Equal("a,\"b,c\",\"say \"\"hi\"\"\"", string.Join(",", CsvSamples.Select(LockText.Csv)));
        Assert.EndsWith(".csv", LockMonitorViewModel.SuggestedCsvName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auto_refresh_rereads_and_keeps_the_selection()
    {
        var h = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.W3;
        h.Vm.RefreshSeconds = 2;
        var before = h.Monitor.LockCalls;
        h.Vm.AutoRefresh = true;
        await Shell.Headless.HeadlessHost.WaitUntil(() => h.Monitor.LockCalls > before, 8000);
        h.Vm.AutoRefresh = false;
        Assert.Equal(FakeLockMonitor.W3, h.Vm.SelectedKey);
        h.Vm.RefreshSeconds = 500;
        Assert.Equal(LockMonitorViewModel.MaxRefreshSeconds, h.Vm.RefreshSeconds);
    }

    [Fact]
    public async Task Auto_refresh_turns_on_after_the_first_read_when_asked()
    {
        var h = new LockHarness();
        h.Vm.StartAutoRefresh = true;
        await h.Vm.StartAsync();
        Assert.True(h.Vm.AutoRefresh);
        await h.Vm.StopAsync();
        Assert.False(h.Vm.AutoRefresh);
    }

    [Fact]
    public async Task Selection_that_disappears_is_cleared()
    {
        var h = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.W3;
        h.Monitor.Quiet = true;
        await h.Vm.RefreshAsync();
        Assert.Null(h.Vm.SelectedKey);
        Assert.Equal(LockViewState.Empty, h.Vm.State);
    }

    [Fact]
    public void Formatting_helpers()
    {
        Assert.Equal("2·0388", LockText.Key(new SessionKey(2, 388, 1)));
        Assert.Equal("1,020 초", LockText.Seconds(1020));
        Assert.Equal("—", LockText.Seconds(null));
        Assert.Equal("—", LockText.Mode(0));
        Assert.Equal("SSX (5)", LockText.Mode(5));
        Assert.Equal("ALTER SYSTEM KILL SESSION '7,8' IMMEDIATE", LockText.KillStatement(new SessionKey(1, 7, 8), rac: false));
        Assert.True(LockText.IsRowLockWait("enq: TX - row lock contention"));
        Assert.False(LockText.IsRowLockWait(null));
    }
}

/// <summary>시험용: 스냅샷 트리를 화면 모델과 같은 방법으로 펼친다.</summary>
internal static class LockHintsTestBridge
{
    public static IReadOnlyList<LockTreeItem> Items(LockSnapshot snap)
    {
        var monitor = new SnapshotMonitor(snap);
        var vm = new LockMonitorViewModel(monitor, _ => Task.FromResult<ISession?>(new FakeLockSession(LockHarness.Server(LockHarness.Dba))))
        {
            PostToUi = a => a(),
            StartAutoRefresh = false,
        };
        vm.StartAsync().GetAwaiter().GetResult();
        return [.. vm.TreeRows];
    }

    private sealed class SnapshotMonitor(LockSnapshot snap) : IMonitorService
    {
        public Task<LockSnapshot> GetLocksAsync(ISession monitor, LockFilter filter, CancellationToken ct) => Task.FromResult(snap);

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => throw new NotSupportedException();

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct) => throw new NotSupportedException();

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, Oranda.Core.Plans.CursorRef cursor, CancellationToken ct) => throw new NotSupportedException();
    }
}
