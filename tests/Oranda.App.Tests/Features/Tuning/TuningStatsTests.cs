using Oranda.App.Features.Tuning;
using Oranda.App.Features.Tuning.Cursors;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Shell;
using Oranda.Core.Plans;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>TSK-06-03 통계 보기·자식 커서: 명령 등록, 표 고르기, 스크립트 문안, ViewModel(정상·경계·오류·취소).</summary>
public class TuningStatsTests
{
    private static readonly ShellContributions App = ShellContributions.Scan(typeof(TableStatsViewModel).Assembly);

    // ---- 등록 ----------------------------------------------------------------------------

    [Fact]
    public void Commands_and_panels_are_registered_in_the_tuning_menu()
    {
        var registry = new CommandRegistry(App.Commands);
        foreach (var id in TableStatsCommands.Ids.Concat(ChildCursorsCommands.Ids))
        {
            Assert.NotNull(registry.Find(id));
        }

        Assert.All(TableStatsCommands.Ids.Where(i => i.StartsWith("oranda.tuning.", StringComparison.Ordinal)), id => Assert.Contains(id, TuningCommands.Ids));
        Assert.Contains("oranda.tuning.childCursors", TuningCommands.Ids);
        Assert.Contains("menu:tuning/stats@200", registry.Find("oranda.tuning.stats")!.Placements);
        Assert.Contains("menu:tuning/childCursors@210", registry.Find("oranda.tuning.childCursors")!.Placements);
        Assert.Contains("context:schema/tableStats@72", registry.Find("oranda.schema.tableStats")!.Placements);
        Assert.Contains(App.Panels, p => p.Id == TuningCommands.StatsPanelId && p.Kind == ToolPanelKind.Document && p.AllowMultiple);
        Assert.Contains(App.Panels, p => p.Id == TuningCommands.ChildCursorsPanelId && p.Kind == ToolPanelKind.Document && p.AllowMultiple);

        var keys = new ContextKeyService();
        Assert.False(registry.CanExecute("oranda.tuning.stats", keys));
        Assert.False(registry.CanExecute("oranda.tuning.childCursors", keys));
        keys.Set("hasSession", true);
        Assert.True(registry.CanExecute("oranda.tuning.stats", keys));
        Assert.True(registry.CanExecute("oranda.tuning.childCursors", keys));
        Assert.False(registry.CanExecute("oranda.tuning.gatherStatsScript", keys)); // 통계 보기 탭이 앞에 있어야
        keys.Set(TableStatsCommands.ActiveKey, true);
        Assert.True(registry.CanExecute("oranda.tuning.gatherStatsScript", keys));
    }

    [Fact]
    public void Parameters_from_other_windows_become_targets()
    {
        var session = new TuningFakeSession(user: "scott");
        Assert.Equal(new TableStatsTarget("MES", "LOT_HIST"), TableStatsCommandHandler.FromParameter("mes.lot_hist", session));
        Assert.Equal(new TableStatsTarget("SCOTT", "EMP"), TableStatsCommandHandler.FromParameter("emp", session));
        Assert.Equal(new TableStatsTarget("SCOTT", "EMP"), TableStatsCommandHandler.WithOwner(new TableStatsTarget(string.Empty, "EMP"), session));
        Assert.Null(TableStatsCommandHandler.WithOwner(new TableStatsTarget(string.Empty, "EMP"), null));
        Assert.Null(TableStatsCommandHandler.FromParameter(42, session));
        Assert.Equal(new CursorRef("7h35uxf5uhmm1"), ChildCursorsCommandHandler.FromParameter(" 7h35uxf5uhmm1 "));
        Assert.Null(ChildCursorsCommandHandler.FromParameter("too-short"));
        var cursor = new CursorRef("7h35uxf5uhmm1", 2, 1);
        Assert.Same(cursor, ChildCursorsCommandHandler.FromParameter(cursor));
    }

    [Fact]
    public async Task Navigation_hands_the_target_to_the_next_command_once()
    {
        var ran = new List<string>();
        var before = ShellCommandRunner.Runner;
        ShellCommandRunner.Runner = id =>
        {
            ran.Add(id);
            return Task.CompletedTask;
        };
        try
        {
            await TuningNavigation.OpenStatsAsync("MES", "LOT_HIST");
            await TuningNavigation.OpenChildCursorsAsync("7h35uxf5uhmm1", 2);
            Assert.Equal(["oranda.tuning.stats", "oranda.tuning.childCursors"], ran);
            Assert.Equal(new TableStatsTarget("MES", "LOT_HIST"), TuningNavigation.TakeStats());
            Assert.Null(TuningNavigation.TakeStats());
            Assert.Equal(new CursorRef("7h35uxf5uhmm1", null, 2), TuningNavigation.TakeCursor());
            Assert.Null(TuningNavigation.TakeCursor());
        }
        finally
        {
            ShellCommandRunner.Runner = before;
        }
    }

    // ---- 표 이름 -------------------------------------------------------------------------

    [Theory]
    [InlineData("mes.lot_hist", "SCOTT", "MES", "LOT_HIST")]
    [InlineData("lot_hist", "scott", "SCOTT", "LOT_HIST")]
    [InlineData("\"Mes\".\"Lot Hist\"", "SCOTT", "Mes", "Lot Hist")]
    [InlineData(" MES.LOT$HIST# ", null, "MES", "LOT$HIST#")]
    public void Target_parses_names(string text, string? owner, string expectOwner, string expectTable) =>
        Assert.Equal(new TableStatsTarget(expectOwner, expectTable), TableStatsTarget.Parse(text, owner));

    [Theory]
    [InlineData("")]
    [InlineData("a.b.c")]
    [InlineData("1abc")]
    [InlineData("mes.")]
    [InlineData("\"open")]
    [InlineData("lot_hist")] // 소유자를 모름
    public void Target_rejects_bad_names(string text) => Assert.Null(TableStatsTarget.Parse(text, null));

    [Fact]
    public void Display_quotes_only_names_that_need_it()
    {
        Assert.Equal("MES.LOT_HIST", new TableStatsTarget("MES", "LOT_HIST").Display);
        Assert.Equal("MES.\"Lot Hist\"", new TableStatsTarget("MES", "Lot Hist").Display);
    }

    private const string Sql = "SELECT lh.lot_id, lh.evt_cd FROM mes.lot_hist lh JOIN lot_mst lm ON lm.lot_id = lh.lot_id WHERE lh.evt_cd = 'HOLD'";

    [Theory]
    [InlineData("mes.lot_hist", 2, "MES", "LOT_HIST")]       // 소유자 위
    [InlineData("mes.lot_hist", 8, "MES", "LOT_HIST")]       // 표 이름 위
    [InlineData("lot_mst lm", 3, "SCOTT", "LOT_MST")]        // 소유자 없는 표
    [InlineData("lh.evt_cd FROM", 1, "MES", "LOT_HIST")]     // 별칭 위
    [InlineData("lh.evt_cd FROM", 5, "MES", "LOT_HIST")]     // 별칭.열 의 열 위
    [InlineData("lm.lot_id", 0, "SCOTT", "LOT_MST")]         // 다른 별칭
    public void Editor_table_follows_the_caret(string near, int offset, string owner, string table)
    {
        var caret = Sql.IndexOf(near, StringComparison.Ordinal) + offset;
        Assert.Equal(new TableStatsTarget(owner, table), EditorTableLocator.Find(Sql, caret, null, "scott"));
    }

    [Theory]
    [InlineData("SELECT", 2)]
    [InlineData("'HOLD'", 3)]  // 문자열 안
    [InlineData("WHERE", 1)]
    public void Editor_table_is_null_on_keywords_and_strings(string near, int offset) =>
        Assert.Null(EditorTableLocator.Find(Sql, Sql.IndexOf(near, StringComparison.Ordinal) + offset, null, "scott"));

    [Fact]
    public void Editor_table_handles_edges()
    {
        Assert.Null(EditorTableLocator.Find(string.Empty, 0, null, "scott"));
        Assert.Null(EditorTableLocator.Find("   ", 2, null, "scott"));
        Assert.Equal(new TableStatsTarget("SCOTT", "EMP"), EditorTableLocator.Find("emp", 3, null, "scott")); // 끝 위치
        Assert.Equal(new TableStatsTarget("SCOTT", "EMP"), EditorTableLocator.Find("emp", 99, null, "scott")); // 범위 밖 → 끝
        Assert.Equal(new TableStatsTarget("Mes", "T"), EditorTableLocator.Find("select * from \"Mes\".t", 16, null, "scott"));
    }

    // ---- 스크립트 -------------------------------------------------------------------------

    [Fact]
    public void Gather_script_names_owner_and_table_and_says_it_writes()
    {
        var body = GatherStatsScript.Build(new TableStatsTarget("MES", "LOT_HIST"), TuningSamples.LotHist().Table, TuningSamples.Now).Body;
        Assert.Contains("실행하지 않았습니다", body, StringComparison.Ordinal);
        Assert.Contains("DB 에 씁니다", body, StringComparison.Ordinal);
        Assert.Contains("DBMS_STATS.GATHER_TABLE_STATS(", body, StringComparison.Ordinal);
        Assert.Contains("ownname          => 'MES',", body, StringComparison.Ordinal);
        Assert.Contains("tabname          => 'LOT_HIST',", body, StringComparison.Ordinal);
        Assert.Contains("MES.LOT_HIST · stale · 2026-10-24 14:10", body, StringComparison.Ordinal);
        Assert.EndsWith("/", body.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal("gather_LOT_HIST.sql", GatherStatsScript.Title(new TableStatsTarget("MES", "LOT_HIST")));
    }

    [Fact]
    public void Gather_script_quotes_mixed_case_and_escapes_quotes()
    {
        var body = GatherStatsScript.Build(new TableStatsTarget("Mes", "O'Brien Tab"), null, TuningSamples.Now).Body;
        Assert.Contains("ownname          => '\"Mes\"',", body, StringComparison.Ordinal);
        Assert.Contains("tabname          => '\"O''Brien Tab\"',", body, StringComparison.Ordinal);
        Assert.Contains("통계 없음", body, StringComparison.Ordinal);
        Assert.Equal("gather_O_Brien_Tab.sql", GatherStatsScript.Title(new TableStatsTarget("Mes", "O'Brien Tab")));
    }

    // ---- 통계 보기 ViewModel ---------------------------------------------------------------

    private static TableStatsViewModel StatsVm(FakeTuningService service, TuningFakeSession? session = null, string table = "LOT_HIST")
    {
        var vm = new TableStatsViewModel(service) { Clock = () => TuningSamples.Now };
        vm.Attach(session ?? new TuningFakeSession(), new TableStatsTarget("MES", table));
        return vm;
    }

    [Fact]
    public async Task Stats_load_summary_columns_indexes_and_the_first_histogram()
    {
        var service = TuningSamples.LotHist();
        var vm = StatsVm(service);
        await vm.LoadAsync();

        Assert.Equal(TuningPanelState.Ready, vm.State);
        Assert.True(vm.ShowContent);
        Assert.True(vm.IsStale);
        Assert.True(vm.ScriptEmphasis);
        Assert.Equal("12,480,331", vm.NumRowsText);
        Assert.Equal("182,044", vm.BlocksText);
        Assert.Equal("96 B", vm.AvgRowLenText);
        Assert.Equal("2026-09-02 22:10", vm.LastAnalyzedText);
        Assert.Equal(51, vm.AgeDays);
        Assert.Equal(9, vm.Columns.Count);
        Assert.Equal("8.0e-8", vm.Columns[0].Cells[3]);
        Assert.Equal("0.0244", vm.Columns[7].Cells[3]);
        Assert.Equal("통계 — MES.LOT_HIST", vm.Title);

        // 히스토그램이 있는 첫 열(EQP_ID HYBRID) — 버킷이 없으면 안내.
        Assert.Equal("EQP_ID", vm.SelectedColumn!.Name);
        Assert.Contains("histogram EQP_ID", service.Calls);
        Assert.Equal("버킷이 없습니다", vm.HistogramHint);

        Assert.Equal(3, vm.Indexes.Count);
        Assert.Equal("HIST_SEQ", vm.Indexes[0].ColumnsText);
        Assert.Equal("LOT_ID, EVT_TIME", vm.Indexes[1].ColumnsText);
        Assert.Equal(ClusteringQuality.Good, vm.Indexes[0].Quality);
        Assert.Equal(ClusteringQuality.Fair, vm.Indexes[1].Quality);
        Assert.Equal(ClusteringQuality.Poor, vm.Indexes[2].Quality);
        Assert.Equal("좋음 · 블록의 1.0 배", vm.Indexes[0].QualityText);
        Assert.Equal("예", vm.Indexes[2].Cells[9]);
        Assert.Empty(vm.IndexStaleNote);
        Assert.Contains("MES.LOT_HIST · 열 9 · 인덱스 3 · stale 예", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picking_a_column_draws_its_histogram_and_none_columns_skip_the_query()
    {
        var service = TuningSamples.LotHist();
        var vm = StatsVm(service);
        await vm.LoadAsync();

        await vm.LoadHistogramAsync(vm.Columns.Single(c => c.Name == "EVT_CD"));
        Assert.Equal(6, vm.HistogramBars.Count);
        Assert.Equal("MOVE", vm.HistogramBars[0].Label);
        Assert.Equal(1, vm.HistogramBars[0].Fraction, 3);
        Assert.Equal("5,210,400", vm.HistogramBars[0].ValueText);
        Assert.True(vm.HistogramBars[^1].Percent > 0); // 작은 버킷도 점은 보인다
        Assert.Empty(vm.HistogramHint);
        Assert.True(vm.HasHistogramWarning); // stale + 51 일
        Assert.Contains("51 일", vm.HistogramWarning, StringComparison.Ordinal);

        vm.ShowShare = true;
        Assert.Equal("41.7 %", vm.HistogramBars[0].ValueText);

        // NONE 열은 서버가 두 끝점을 줘도 「히스토그램 없음」 — 조회하지 않는다.
        service.Calls.Clear();
        await vm.LoadHistogramAsync(vm.Columns.Single(c => c.Name == "HIST_SEQ"));
        Assert.Empty(vm.HistogramBars);
        Assert.Equal("히스토그램이 없습니다 (SIZE 1)", vm.HistogramHint);
        Assert.DoesNotContain(service.Calls, c => c.StartsWith("histogram", StringComparison.Ordinal));
        Assert.False(vm.HasHistogramWarning);
    }

    [Fact]
    public async Task Never_analyzed_table_shows_no_stats_and_emphasizes_the_script()
    {
        var service = TuningSamples.LotHist(stale: false);
        service.Table = new TableStatsRow("MES", "LOT_HIST", null, null, null, null, false);
        service.Columns.Clear();
        var vm = StatsVm(service);
        await vm.LoadAsync();

        Assert.Equal(TuningPanelState.Ready, vm.State);
        Assert.True(vm.HasNoStats);
        Assert.True(vm.ScriptEmphasis);
        Assert.Empty(vm.Columns);
        Assert.Equal("수집 안 함", vm.LastAnalyzedText);
        Assert.Empty(vm.AgeText);
        Assert.Null(vm.SelectedColumn);
    }

    [Fact]
    public async Task Invisible_table_and_privilege_errors_show_the_denied_state()
    {
        var service = new FakeTuningService(); // 줄 없음
        var vm = StatsVm(service);
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Denied, vm.State);
        Assert.Equal("이 계정으로 볼 수 없는 표", vm.HintTitle);
        Assert.False(vm.IsBusy);

        service.Failure = new InvalidOperationException("ORA-01031: insufficient privileges");
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Denied, vm.State);
        Assert.Contains("ORA-01031", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_and_missing_service_parts_show_the_error_state()
    {
        var service = TuningSamples.LotHist();
        service.Failure = new InvalidOperationException("ORA-03113: end-of-file on communication channel\nmore");
        var vm = StatsVm(service);
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Error, vm.State);
        Assert.Equal("ORA-03113: end-of-file on communication channel", vm.HintText);

        service.Failure = new NotSupportedException();
        await vm.LoadAsync();
        Assert.Equal("튜닝 조회가 준비되지 않았습니다", vm.HintTitle);

        // 오류 뒤 고치면 다시 읽힌다(되돌림).
        service.Failure = null;
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Ready, vm.State);
    }

    [Fact]
    public async Task Non_dba_accounts_get_the_stale_cannot_be_judged_note()
    {
        var vm = StatsVm(TuningSamples.LotHist(stale: false), new TuningFakeSession(dba: false));
        await vm.LoadAsync();
        Assert.Contains("stale 판정 불가(권한)", vm.IndexStaleNote, StringComparison.Ordinal);
        Assert.Equal("판정 불가", vm.Indexes[0].Cells[9]);
        Assert.False(vm.Indexes[0].StaleDanger);
    }

    [Fact]
    public async Task Cancel_stops_reading_and_a_new_read_still_works()
    {
        var service = TuningSamples.LotHist();
        service.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = StatsVm(service);
        var load = vm.LoadAsync();
        Assert.True(vm.IsBusy);
        Assert.Equal(TuningPanelState.Loading, vm.State);
        vm.Cancel();
        await load;
        Assert.False(vm.IsBusy);
        Assert.Equal("취소했습니다.", vm.StatusText);
        Assert.Equal(TuningPanelState.NoTarget, vm.State);
        Assert.Empty(vm.Columns);

        service.Gate = null;
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Ready, vm.State);
    }

    [Fact]
    public async Task States_without_session_or_target_and_bad_typed_names()
    {
        var vm = new TableStatsViewModel(new FakeTuningService());
        Assert.Equal(TuningPanelState.NoSession, vm.State);
        Assert.Equal("접속 없음", vm.HintTitle);
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.NoSession, vm.State);

        vm.Attach(new TuningFakeSession(), null);
        Assert.Equal(TuningPanelState.NoTarget, vm.State);
        Assert.False(vm.CanGatherScript);
        Assert.False(vm.OpenGatherScript());

        vm.TargetText = "a.b.c";
        await vm.ApplyTargetTextAsync();
        Assert.Contains("owner.table", vm.StatusText, StringComparison.Ordinal);
        Assert.Null(vm.Target);

        await vm.UseEditorTableAsync();
        Assert.Contains("편집기 커서", vm.StatusText, StringComparison.Ordinal);

        var closed = new TuningFakeSession { State = Oranda.Core.Sessions.SessionState.Closed };
        vm.Attach(closed, new TableStatsTarget("MES", "T"));
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.NoSession, vm.State);
    }

    [Fact]
    public async Task Editor_table_and_typed_name_switch_the_target()
    {
        var service = TuningSamples.LotHist();
        var vm = StatsVm(service);
        vm.EditorTable = () => new TableStatsTarget("MES", "LOT_HIST");
        await vm.UseEditorTableAsync();
        Assert.Equal("MES.LOT_HIST", vm.TargetText);
        Assert.Contains("table MES.LOT_HIST", service.Calls);

        vm.TargetText = "lot_hist";
        await vm.ApplyTargetTextAsync();
        Assert.Equal(new TableStatsTarget("MES", "LOT_HIST"), vm.Target); // 접속 계정 MES
    }

    [Fact]
    public async Task Gather_script_goes_to_a_new_editor_tab_without_touching_the_database()
    {
        var service = TuningSamples.LotHist();
        var vm = StatsVm(service);
        await vm.LoadAsync();
        var calls = service.Calls.Count;
        (string Title, string Body)? opened = null;
        vm.OpenEditor = (title, body) => opened = (title, body);

        Assert.True(vm.OpenGatherScript());
        Assert.Equal("gather_LOT_HIST.sql", opened!.Value.Title);
        Assert.Contains("GATHER_TABLE_STATS", opened.Value.Body, StringComparison.Ordinal);
        Assert.Equal(calls, service.Calls.Count); // 서비스(DB)를 부르지 않는다
        Assert.Contains("직접 실행", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_text_has_summary_columns_and_indexes()
    {
        var vm = StatsVm(TuningSamples.LotHist());
        Assert.Contains("MES.LOT_HIST", vm.BuildCopyText(), StringComparison.Ordinal);
        await vm.LoadAsync();
        var text = vm.BuildCopyText();
        Assert.Contains("EVT_CD\t6\t0", text, StringComparison.Ordinal);
        Assert.Contains("LOT_HIST_IX2\tEQP_ID", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, new TableStatsViewModel().BuildCopyText());
    }

    [Theory]
    [InlineData(100L, 50L, 1000L, ClusteringQuality.Good)]
    [InlineData(101L, 50L, 1000L, ClusteringQuality.Fair)]
    [InlineData(500L, 50L, 1000L, ClusteringQuality.Poor)]
    [InlineData(null, 50L, 1000L, ClusteringQuality.Unknown)]
    [InlineData(10L, 0L, 1000L, ClusteringQuality.Unknown)]
    [InlineData(10L, 5L, null, ClusteringQuality.Unknown)]
    public void Clustering_factor_rating(long? cf, long? blocks, long? rows, ClusteringQuality expected) =>
        Assert.Equal(expected, IndexStatsItem.Judge(cf, blocks, rows));

    // ---- 자식 커서 ViewModel ---------------------------------------------------------------

    private static ChildCursorsViewModel CursorVm(FakeTuningService service, TuningFakeSession? session = null)
    {
        var vm = new ChildCursorsViewModel(service);
        vm.Attach(session ?? new TuningFakeSession(), "7h35uxf5uhmm1");
        return vm;
    }

    [Fact]
    public async Task Children_reasons_and_the_auto_compare_of_two_plans()
    {
        var service = TuningSamples.Cursors();
        var vm = CursorVm(service);
        vm.SqlTextLoader = (_, _, _) => Task.FromResult<string?>("SELECT lh.lot_id FROM mes.lot_hist lh");
        await vm.LoadAsync();

        Assert.Equal(TuningPanelState.Ready, vm.State);
        Assert.Equal(4, vm.Items.Count);
        Assert.Equal("자식 4", vm.ChildCountText);
        Assert.Equal("plan_hash 2", vm.PlanHashCountText);
        Assert.True(vm.PlanHashDiffers);
        Assert.Equal("바인드 인식 예", vm.BindAwareText);
        Assert.Equal("SELECT lh.lot_id FROM mes.lot_hist lh", vm.SqlText);
        Assert.Equal("자식 4 · 인스턴스 2 · plan_hash 2", vm.SummaryText);
        Assert.Equal(["인스턴스: 전체", "인스턴스 1", "인스턴스 2"], vm.Instances);
        Assert.True(vm.HasInstances);

        var c0 = vm.Items[0];
        Assert.Equal(["☑", "1", "0", "1838229974", "Y", "N", "N", "14,882", "42,180", "1,842 ms"], c0.Cells);
        Assert.True(c0.GetsWorse);
        Assert.True(c0.ElapsedWorse);
        Assert.False(vm.Items[1].GetsWorse);
        Assert.Equal("38 ms", vm.Items[1].ElapsedPerExecText);

        // 사유: 사전 뜻, 같은 사유가 이어지면 「위와 같음」, 모르는 열은 열 이름만.
        Assert.Equal(6, vm.Reasons.Count);
        Assert.StartsWith("바인드 값 선택도가 달라", vm.Reasons[0].Meaning, StringComparison.Ordinal);
        Assert.Equal("위와 같음", vm.Reasons[1].Meaning);
        Assert.Equal(string.Empty, vm.Reasons.Single(r => r.Reason.Reason == "SOME_FUTURE_MISMATCH").Meaning);
        Assert.Equal("child 0 은 공유 불가(N) — 새 실행은 1·2·3 으로 갑니다", vm.ReasonFooter);

        // plan_hash 가 다른 첫 두 자식(0·1)이 체크되고 비교가 그려진다.
        Assert.True(vm.Items[0].IsCompared);
        Assert.True(vm.Items[1].IsCompared);
        Assert.True(vm.IsComparing);
        Assert.True(vm.CompareDiffers);
        Assert.Equal("두 계획이 다릅니다", vm.CompareTitle);
        Assert.StartsWith("child 0 (1838229974) ↔ child 1 (3392001144) · 바뀜", vm.CompareSummary, StringComparison.Ordinal);
        Assert.Contains(vm.CompareRows, r => r.Item.IsAdded);
        Assert.Equal("비용 0 → 1", vm.CostHeader);
        Assert.Equal(new TableStatsTarget("MES", "LOT_HIST"), vm.PlanTable);
        Assert.Equal("자식 커서 — 7h35uxf5uhmm1", vm.Title);
    }

    [Fact]
    public async Task Third_check_releases_the_oldest_and_recompares()
    {
        var service = TuningSamples.Cursors();
        var vm = CursorVm(service);
        await vm.LoadAsync();
        service.Calls.Clear();

        vm.Items[3].IsCompared = true; // 0·1 → 1·3
        await HeadlessFree.Until(() => vm.CompareSummary.Contains("child 3", StringComparison.Ordinal));
        Assert.False(vm.Items[0].IsCompared);
        Assert.True(vm.Items[1].IsCompared);
        Assert.True(vm.Items[3].IsCompared);
        Assert.Equal("두 계획이 같습니다", vm.CompareTitle); // 1 과 3 은 같은 plan_hash
        Assert.Contains("plan 3", service.Calls);
        Assert.DoesNotContain("plan 1", service.Calls); // 읽어 둔 계획을 다시 쓴다

        vm.Items[1].IsCompared = false;
        Assert.False(vm.CanCompare);
        Assert.False(vm.IsComparing);
        Assert.Contains("두 자식을", vm.CompareHint, StringComparison.Ordinal);

        vm.Items[2].IsCompared = true;
        await HeadlessFree.Until(() => vm.IsComparing);
        service.Calls.Clear();
        await vm.CompareAsync(reload: true);
        Assert.Contains("plan 2", service.Calls); // 다시 계산은 계획을 다시 읽는다
    }

    [Fact]
    public async Task Instance_filter_narrows_the_table()
    {
        var vm = CursorVm(TuningSamples.Cursors());
        vm.PreferredInstance = 2;
        await vm.LoadAsync();
        Assert.Equal(2, vm.SelectedInstanceIndex);
        Assert.All(vm.VisibleItems, i => Assert.Equal(2, i.Row.InstanceId));
        vm.SelectedInstanceIndex = 0;
        Assert.Equal(4, vm.VisibleItems.Count);
    }

    [Fact]
    public async Task One_child_has_no_compare_and_no_mismatch()
    {
        var service = TuningSamples.Cursors();
        service.Children.RemoveRange(1, 3);
        service.Reasons.Clear();
        var vm = CursorVm(service);
        await vm.LoadAsync();
        Assert.Single(vm.Items);
        Assert.False(vm.CanCompare);
        Assert.False(vm.IsComparing);
        Assert.Equal("불일치 없음", vm.ReasonsHint);
        Assert.Contains("자식이 하나", vm.CompareHint, StringComparison.Ordinal);
        Assert.False(vm.HasInstances);
    }

    [Fact]
    public async Task Aged_out_cursor_and_reason_failure()
    {
        var service = new FakeTuningService();
        var vm = CursorVm(service);
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Empty, vm.State);
        Assert.Equal("공유 풀에서 사라졌습니다", vm.HintTitle);
        Assert.DoesNotContain("reasons", service.Calls);

        var withReasons = TuningSamples.Cursors();
        withReasons.ReasonFailure = new InvalidOperationException("ORA-00904: \"REASON\": invalid identifier");
        vm = CursorVm(withReasons);
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Ready, vm.State); // 사유를 못 읽어도 자식은 보인다
        Assert.Contains("ORA-00904", vm.ReasonsHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_v_dollar_privilege_turns_on_the_banner()
    {
        var service = TuningSamples.Cursors();
        var vm = CursorVm(service, new TuningFakeSession(dba: false, perfViews: false, user: "app"));
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Denied, vm.State);
        Assert.True(vm.Access.HasNotice);
        Assert.Contains("GRANT SELECT_CATALOG_ROLE TO APP;", vm.Access.GrantSql, StringComparison.Ordinal);
        Assert.Empty(service.Calls); // 묻지 않는다

        var other = new TuningFakeSession();
        vm.ChooseSession = _ => Task.FromResult<Oranda.Core.Sessions.ISession?>(other);
        vm.CloseTimeout = TimeSpan.FromMilliseconds(200);
        await vm.OpenWithOtherAccountAsync();
        Assert.Same(other, vm.Session);
        Assert.Equal(TuningPanelState.Ready, vm.State);
        Assert.False(vm.Access.HasNotice);
        vm.Dispose();
        await HeadlessFree.Until(() => other.Disposed > 0);
    }

    [Fact]
    public async Task Privilege_error_while_reading_also_shows_the_banner_and_other_errors_the_error_state()
    {
        var service = TuningSamples.Cursors();
        service.Failure = new InvalidOperationException("ORA-00942: table or view does not exist");
        var vm = CursorVm(service);
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Denied, vm.State);

        service.Failure = new TimeoutException("timeout");
        await vm.LoadAsync();
        Assert.Equal(TuningPanelState.Error, vm.State);
        Assert.Equal("timeout", vm.HintText);
    }

    [Fact]
    public async Task Sql_id_input_states_and_buttons()
    {
        var vm = new ChildCursorsViewModel(TuningSamples.Cursors());
        Assert.Equal(TuningPanelState.NoSession, vm.State);
        vm.Attach(new TuningFakeSession(), null);
        Assert.Equal(TuningPanelState.NoTarget, vm.State);
        Assert.Contains("SQL 모니터", vm.HintText, StringComparison.Ordinal);

        vm.SqlIdText = "abc";
        await vm.ApplySqlIdTextAsync();
        Assert.Contains("13자리", vm.StatusText, StringComparison.Ordinal);
        Assert.Null(vm.SqlId);

        vm.SqlIdText = "7h35uxf5uhmm1";
        await vm.ApplySqlIdTextAsync();
        Assert.Equal(TuningPanelState.Ready, vm.State);

        Assert.False(vm.CanOpenPlan); // 여는 방법이 없으면
        CursorRef? opened = null;
        vm.OpenPlan = (_, c) =>
        {
            opened = c;
            return Task.CompletedTask;
        };
        vm.CurrentItem = vm.Items[2];
        Assert.True(vm.CanOpenPlan);
        await vm.OpenCurrentPlanAsync();
        Assert.Equal(new CursorRef("7h35uxf5uhmm1", 2, 2), opened);

        TableStatsTarget? stats = null;
        vm.OpenStats = (_, t) => stats = t;
        vm.OpenPlanTableStats();
        Assert.Equal(new TableStatsTarget("MES", "LOT_HIST"), stats);

        var copy = vm.BuildCopyText();
        Assert.Contains("sql_id 7h35uxf5uhmm1", copy, StringComparison.Ordinal);
        Assert.Contains("BIND_EQUIV_FAILURE", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_dictionary_has_both_languages()
    {
        Assert.NotNull(CursorMismatchReasons.Meaning("BIND_MISMATCH"));
        Assert.NotNull(CursorMismatchReasons.Meaning("roll_invalid_mismatch"));
        Assert.Null(CursorMismatchReasons.Meaning("NOPE"));
        Assert.True(ChildCursorsViewModel.IsSqlId("7H35UXF5UHMM1"));
        Assert.False(ChildCursorsViewModel.IsSqlId("7h35uxf5uhmm"));
        Assert.False(ChildCursorsViewModel.IsSqlId(null));
    }
}

/// <summary>UI 스레드 없이 조건을 기다린다(비동기 비교가 끝날 때까지).</summary>
internal static class HeadlessFree
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("조건이 시간 안에 참이 되지 않았다.");
            }

            await Task.Delay(10);
        }
    }
}
