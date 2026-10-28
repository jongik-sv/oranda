using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.SqlMonitor;

/// <summary>
/// TSK-14-17 튜닝 추천: SQL 모니터 「튜닝 추천」 탭(점수순 목록·행 선택 상세·튜닝 도우미/자식 커서/편집기·내부 스키마 토글·sql_id 바로 가기·권한 오류)과
/// 세션 모니터 「튜닝 추천」 탭(활성 세션 SQL 검사·고른 세션 줄·SQL 모니터로 이동), 명령 등록. Headless 화면은 조회 결과 그리드에 줄이 보이는지까지.
/// </summary>
public class TuningRecommendationTests
{
    private const long Sec = 1_000_000;
    private const string Slow = "5slow0000000a";
    private const string Heavy = "6heavy000000b";
    private const string Calm = "7calm00000000";
    private const string Internal = "8sys000000000";

    private static readonly ShellContributions App = ShellContributions.Scan(typeof(SqlMonitorViewModel).Assembly);

    private static TuningCandidateSet Candidates() => new(
    [
        new SqlStatsSample(Slow) { InstanceId = 1, PlanHashValue = 11, ParsingSchema = "APP_OWNER", SqlText = "SELECT * FROM orders o WHERE o.status = :s", Executions = 10, ElapsedMicros = 50 * Sec, CpuMicros = 10 * Sec, UserIoWaitMicros = 35 * Sec, BufferGets = 10 * 500_000, RowsProcessed = 100 },
        new SqlStatsSample(Heavy) { InstanceId = 1, PlanHashValue = 22, ParsingSchema = "APP_OWNER", SqlText = "SELECT COUNT(*) FROM order_items", Executions = 1000, ElapsedMicros = 20 * Sec, CpuMicros = 18 * Sec, BufferGets = 1000L * 20_000, RowsProcessed = 1000 },
        new SqlStatsSample(Calm) { InstanceId = 1, ParsingSchema = "APP_OWNER", SqlText = "SELECT 1 FROM dual", Executions = 1000, ElapsedMicros = Sec, CpuMicros = Sec, BufferGets = 1000 },
        new SqlStatsSample(Internal) { InstanceId = 1, ParsingSchema = "SYS", SqlText = "select obj# from obj$", Executions = 1, ElapsedMicros = 30 * Sec, CpuMicros = 29 * Sec },
    ], 1000 * Sec);

    // ---- 명령 ----

    [Fact]
    public void Recommend_command_is_registered_in_the_tuning_menu_and_opens_the_recommend_tab()
    {
        var registry = new CommandRegistry(App.Commands);
        Assert.NotNull(registry.Find(SqlMonitorViewModel.RecommendCommand));
        Assert.Contains(SqlMonitorViewModel.RecommendCommand, Oranda.App.Features.Tuning.TuningCommands.Ids);
        Assert.Equal(SqlMonitorViewModel.RecommendCommand, SessionTuningHintsViewModel.RecommendCommand);
        Assert.Equal(SqlMonitorViewModel.RecommendTab, SqlMonitorCommandHandler.TabFor(SqlMonitorViewModel.RecommendCommand));
        Assert.True(registry.CanExecute(SqlMonitorViewModel.RecommendCommand, new ContextKeyService()));

        var tuning = MenuModelBuilder.BuildMenuBar([.. App.Commands.Select(c => c.Definition)], new ShellText(language: "ko"), _ => null, [])[MenuModelBuilder.TopMenus.ToList().IndexOf("tuning")];
        Assert.Contains(SqlMonitorViewModel.RecommendCommand, Flatten(tuning));
        Assert.Equal("튜닝 추천 (규칙 기반)", new ShellText(language: "ko").Lookup("Cmd." + SqlMonitorViewModel.RecommendCommand));
    }

    // ---- SQL 모니터 튜닝 추천 ----

    [Fact]
    public async Task Refresh_lists_recommendations_by_score_and_skips_internal_schemas_until_turned_off()
    {
        var h = await Harness.StartAsync();

        await h.Vm.Recommendations.RefreshAsync();

        var r = h.Vm.Recommendations;
        Assert.True(r.IsLoaded);
        Assert.Equal([Slow, Heavy], r.Items.Select(i => i.SqlId));
        Assert.True(r.Items[0].Score >= r.Items[1].Score);
        Assert.Contains("실행당 오래 걸림", r.Items[0].ReasonText, StringComparison.Ordinal);
        Assert.Equal(5.0m, r.Items[0].ElapsedPerExec);
        Assert.Equal(500_000, r.Items[0].GetsPerExec);
        Assert.Equal(1, h.Tuning.CandidateCalls);
        Assert.Equal([Slow, Heavy], h.Tuning.PlanCalls); // 점수 상위만 계획을 읽는다(부하 제한)
        Assert.Contains("튜닝 추천 2건", r.StatusText, StringComparison.Ordinal);

        // 계획 규칙(큰 표 전체 스캔)이 점수에 들어간다.
        Assert.Contains(r.Items[0].Recommendation.Reasons, x => x.Rule == TuningRule.FullScanLargeTable);

        r.ExcludeInternal = false;
        Assert.Contains(r.Items, i => i.SqlId == Internal);
        Assert.Equal(1, h.Tuning.CandidateCalls); // 토글은 다시 묻지 않는다
    }

    [Fact]
    public async Task Selecting_a_row_shows_all_reasons_evidence_and_the_full_sql_text()
    {
        var h = await Harness.StartAsync();
        await h.Vm.Recommendations.RefreshAsync();
        var r = h.Vm.Recommendations;

        r.SelectedItem = r.Items[1];
        await Harness.SettleAsync(() => r.DetailText.Contains("[SQL 전문]", StringComparison.Ordinal));

        Assert.Contains("SQL ID " + Heavy, r.DetailText, StringComparison.Ordinal);
        Assert.Contains("[추천 이유]", r.DetailText, StringComparison.Ordinal);
        Assert.Contains("실행당 논리 읽기 많음", r.DetailText, StringComparison.Ordinal);
        Assert.Contains("실행당 buffer_gets 20,000", r.DetailText, StringComparison.Ordinal);
        Assert.Contains("FULL TEXT OF " + Heavy, r.DetailText, StringComparison.Ordinal);
        Assert.True(r.HasSelection);
    }

    [Fact]
    public async Task Assistant_child_cursors_and_editor_actions_reuse_existing_commands()
    {
        var h = await Harness.StartAsync();
        h.Available.Add(TuningRecommendationsViewModel.AssistantCommand);
        h.Available.Add(SqlMonitorViewModel.ChildCursorsCommand);
        await h.Vm.Recommendations.RefreshAsync();
        var r = h.Vm.Recommendations;
        r.SelectedItem = r.Items[0];
        Assert.True(r.CanOpenAssistant);

        await r.OpenInAssistantAsync();
        await r.OpenChildCursorsAsync();
        await r.OpenInEditorAsync();

        Assert.Equal(new (string, object?)[] { (TuningRecommendationsViewModel.AssistantCommand, null), (SqlMonitorViewModel.ChildCursorsCommand, Slow) }, h.Commands);
        Assert.Equal(2, h.Editors.Count);
        Assert.Equal("FULL TEXT OF " + Slow, h.Editors[0].Body); // 도우미는 편집기 현재 문장을 읽으므로 전문만 넣는다
        Assert.StartsWith("-- 튜닝 추천: sql_id " + Slow, h.Editors[1].Body, StringComparison.Ordinal);
        Assert.Contains("FULL TEXT OF " + Slow, h.Editors[1].Body, StringComparison.Ordinal);

        h.Available.Clear();
        Assert.False(r.CanOpenAssistant);
        await r.OpenInAssistantAsync();
        Assert.Single(h.Commands, c => c.Id == TuningRecommendationsViewModel.AssistantCommand);
    }

    [Fact]
    public async Task Focus_selects_a_listed_sql_or_reads_one_that_is_not_listed()
    {
        var h = await Harness.StartAsync();

        await h.Vm.Recommendations.FocusAsync(Heavy); // 아직 안 읽었으면 먼저 읽는다
        Assert.Equal(Heavy, h.Vm.Recommendations.SelectedItem?.SqlId);

        await h.Vm.Recommendations.FocusAsync(Calm); // 규칙에 안 걸려 목록에 없다 → 따로 읽어 맨 아래에
        Assert.Equal(Calm, h.Vm.Recommendations.SelectedItem?.SqlId);
        Assert.Equal(0, h.Vm.Recommendations.SelectedItem!.Score);
        Assert.Equal([Calm], h.Tuning.NarrowedTo);
        Assert.Contains("걸리지 않았습니다", h.Vm.Recommendations.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, h.Tuning.CandidateCalls - h.Tuning.NarrowedTo.Count);
    }

    [Fact]
    public async Task Privilege_error_explains_which_views_are_needed_and_no_session_says_so()
    {
        var closed = new SqlMonitorViewModel(new Monitor(), null, _ => Task.FromResult<ISession?>(null)) { PostToUi = a => a(), TuningService = new Tuning() };
        await closed.Recommendations.RefreshAsync();
        Assert.Contains("모니터 세션이 열려 있지 않습니다", closed.Recommendations.StatusText, StringComparison.Ordinal);
        Assert.False(closed.Recommendations.CanRefresh);

        var h = await Harness.StartAsync();
        h.Tuning.Fail = new InvalidOperationException("ORA-00942: table or view does not exist");
        await h.Vm.Recommendations.RefreshAsync();

        Assert.Contains("ORA-00942", h.Vm.Recommendations.StatusText, StringComparison.Ordinal);
        Assert.Contains("gv$sqlstats", h.Vm.Recommendations.StatusText, StringComparison.Ordinal);
        Assert.Empty(h.Vm.Recommendations.Items);
        Assert.False(h.Vm.Recommendations.IsBusy);
    }

    [Fact]
    public Task Recommend_tab_shows_the_list_in_the_result_grid_and_picks_the_row() => HeadlessHost.Run(async () =>
    {
        var h = await Harness.StartAsync();
        await h.Vm.Recommendations.RefreshAsync();
        var view = new TuningRecommendationsView { DataContext = h.Vm.Recommendations };
        var w = new Window { Content = view, Width = 1100, Height = 500 };
        w.Show();
        try
        {
            var grid = view.FindControl<MonitorGrid>("RecommendList")!;
            await HeadlessHost.WaitUntil(() => grid.Model is { RowCount: 2 } && grid.GridControl.Bounds.Height > 0);
            var m = grid.Model!;
            Assert.Equal(["점수", "이유", "SQL ID", "스키마", "실행", "실행당 경과(s)", "실행당 읽기", "SQL"], m.Columns.Select(c => c.Name));
            Assert.Equal(Slow, m.GetCellText(0, 2));
            Assert.True(m.IsRightAligned(0));

            // 그리드에서 행을 고르면 화면 모델의 선택·상세가 따라온다.
            m.SelectRow(1);
            Dispatcher.UIThread.RunJobs();
            await HeadlessHost.WaitUntil(() => h.Vm.Recommendations.SelectedItem?.SqlId == Heavy);
            Assert.Contains("SQL ID " + Heavy, h.Vm.Recommendations.DetailText, StringComparison.Ordinal);
        }
        finally
        {
            w.Close();
        }
    });

    // ---- 세션 모니터 튜닝 추천 ----

    [Fact]
    public async Task Session_tab_checks_active_sessions_sql_marks_the_selected_session_and_jumps_to_sql_monitor()
    {
        var s = await SessionHarness.StartAsync();
        var hints = s.Vm.TuningHints;
        Assert.Contains("아직 검사하지 않았습니다", Select(s, 20).SelectedSessionText, StringComparison.Ordinal);

        await hints.CheckAsync();

        Assert.Equal([Slow], s.Tuning.NarrowedTo); // INACTIVE 세션(Calm)과 SQL 없는 세션은 빼고 활성 세션 SQL 만, 한 번에
        var hint = Assert.Single(hints.Items);
        Assert.Equal(Slow, hint.SqlId);
        Assert.True(hint.IsRecommended);
        Assert.Equal(["20,200", "30,300"], hint.Sessions.Select(x => x.KeyText));
        Assert.Contains("활성 세션 SQL 1개 중 튜닝 추천 1개", hints.StatusText, StringComparison.Ordinal);

        Select(s, 30);
        Assert.Same(hint, hints.SelectedHint);
        Assert.Contains("세션 30,300 의 SQL " + Slow + " 튜닝 추천:", hints.SelectedSessionText, StringComparison.Ordinal);
        Assert.Contains("실행당 오래 걸림", hints.SelectedEvidence, StringComparison.Ordinal);

        s.Available.Add(SessionTuningHintsViewModel.RecommendCommand);
        Assert.True(hints.CanOpenInSqlMonitor);
        await hints.OpenInSqlMonitorAsync();
        Assert.Equal(new (string, object?)[] { (SessionTuningHintsViewModel.RecommendCommand, Slow) }, s.Commands);
    }

    [Fact]
    public async Task Session_tab_without_active_sql_does_not_ask_the_server()
    {
        var s = await SessionHarness.StartAsync(activeSql: false);

        await s.Vm.TuningHints.CheckAsync();

        Assert.Equal(0, s.Tuning.CandidateCalls);
        Assert.Contains("활성 세션이 없습니다", s.Vm.TuningHints.StatusText, StringComparison.Ordinal);
    }

    private static SessionTuningHintsViewModel Select(SessionHarness s, long sid)
    {
        s.Vm.SelectedSession = s.Vm.Sessions.First(x => x.Row.Key.Sid == sid);
        return s.Vm.TuningHints;
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

    // ---- 가짜 ----

    private sealed class Tuning : ITuningService
    {
        public Exception? Fail { get; set; }

        public int CandidateCalls { get; private set; }

        public List<string> NarrowedTo { get; } = [];

        public List<string> PlanCalls { get; } = [];

        public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct)
        {
            CandidateCalls++;
            if (Fail is { } ex)
            {
                return Task.FromException<TuningCandidateSet>(ex);
            }

            var all = Candidates();
            if (query.SqlIds is { } ids)
            {
                NarrowedTo.AddRange(ids);
                return Task.FromResult(new TuningCandidateSet([.. all.Rows.Where(r => ids.Contains(r.SqlId))], 0));
            }

            return Task.FromResult(all);
        }

        public Task<IReadOnlyList<PlanFindingLine>> GetPlanFindingsAsync(ISession s, int? instanceId, string sqlId, long planHashValue, CancellationToken ct)
        {
            PlanCalls.Add(sqlId);
            IReadOnlyList<PlanFindingLine> plan = sqlId == Slow
                ? [new PlanFindingLine(2, "TABLE ACCESS", "FULL", "APP_OWNER", "ORDERS") { TableBlocks = 80_000, TableRows = 5_000_000 }]
                : [];
            return Task.FromResult(plan);
        }
    }

    private sealed class Monitor : IMonitorService
    {
        public IReadOnlyList<SessionRow> Sessions { get; set; } = [];

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct) => Task.FromResult<IReadOnlyList<TopSqlRow>>([]);

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) => Task.FromResult(new SqlDetail(cursor, "FULL TEXT OF " + cursor.SqlId));

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => Task.FromResult(Sessions);

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => Task.FromResult(new BlockingTree([]));

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Harness
    {
        private Harness()
        {
            Vm = new SqlMonitorViewModel(new Monitor(), null, _ => Task.FromResult<ISession?>(new TuningFakeSession(LicensePack.None, null)))
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
        }

        public Tuning Tuning { get; } = new();

        public SqlMonitorViewModel Vm { get; }

        public HashSet<string> Available { get; } = [];

        public List<(string Id, object? Parameter)> Commands { get; } = [];

        public List<(string Title, string Body)> Editors { get; } = [];

        public static async Task<Harness> StartAsync()
        {
            var h = new Harness();
            await h.Vm.StartAsync();
            Assert.True(h.Vm.IsOpen);
            return h;
        }

        public static Task SettleAsync(Func<bool> done) => TuningHarness.SettleAsync(done);
    }

    private sealed class SessionHarness
    {
        private SessionHarness(bool activeSql)
        {
            Monitor.Sessions =
            [
                new SessionRow(new SessionKey(1, 10, 100), "INACTIVE") { Username = "APP_OWNER", SqlId = Calm },
                new SessionRow(new SessionKey(1, 20, 200), "ACTIVE") { Username = "APP_OWNER", SqlId = activeSql ? Slow : null },
                new SessionRow(new SessionKey(1, 30, 300), "ACTIVE") { Username = "APP_OWNER", SqlId = activeSql ? Slow : null },
            ];
            Vm = new SessionMonitorViewModel(Monitor, _ => Task.FromResult<ISession?>(new TuningFakeSession(LicensePack.None, null)))
            {
                PostToUi = a => a(),
                TuningService = Tuning,
                CommandAvailable = id => Available.Contains(id),
                RunCommand = (id, p) =>
                {
                    Commands.Add((id, p));
                    return Task.CompletedTask;
                },
            };
        }

        public Monitor Monitor { get; } = new();

        public Tuning Tuning { get; } = new();

        public SessionMonitorViewModel Vm { get; }

        public HashSet<string> Available { get; } = [];

        public List<(string Id, object? Parameter)> Commands { get; } = [];

        public static async Task<SessionHarness> StartAsync(bool activeSql = true)
        {
            var h = new SessionHarness(activeSql);
            await h.Vm.StartAsync();
            Assert.True(h.Vm.IsOpen);
            Assert.Equal(3, h.Vm.Sessions.Count);
            return h;
        }
    }
}
