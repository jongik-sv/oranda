using System.Runtime.CompilerServices;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExecutionPlan;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;
using TestData = Oranda.Testing.TestData;
using TestResultSet = Oranda.Testing.FakeResultSet;
using TestSession = Oranda.Testing.FakeSession;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>TSK-06-04 계획 창 보강: A-Rows 없음 띠 조건, 힌트 사본, 다시 실행, 보고서 상태, 통계·자식 커서 단추.</summary>
public class PlanBoostTests
{
    internal static readonly SqlStatement Query = new("SELECT e.ename FROM emp e WHERE e.deptno = :d", default, StatementKind.Query)
    {
        Verb = "SELECT",
        BindNames = ["d"],
    };

    /// <summary>A-Rows 가 빈 실제 계획(통계 수집 꺼짐).</summary>
    internal static CorePlan NoActuals(string sqlId = "7h35uxf5uhmm1") => new(
    [
        new(0, null, 0, "SELECT STATEMENT") { Cost = 8412 },
        new(1, 0, 1, "SORT") { Options = "ORDER BY", Cost = 8412, EstimatedRows = 31 },
        new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 8380, EstimatedRows = 31, Risk = PlanRisk.FullScan },
    ], "Plan hash value: 1838229974")
    {
        Cursor = new CursorRef(sqlId, 0),
    };

    /// <summary>A-Rows·시간이 있는 실제 계획.</summary>
    internal static CorePlan WithActuals(string sqlId) => new(
    [
        new(0, null, 0, "SELECT STATEMENT") { Cost = 8412, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.84) },
        new(1, 0, 1, "SORT") { Options = "ORDER BY", Cost = 8412, EstimatedRows = 31, ActualRows = 1208, Starts = 1, ActualTime = TimeSpan.FromSeconds(1.83) },
        new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 8380, EstimatedRows = 31, ActualRows = 1211, Starts = 1,
            ActualTime = TimeSpan.FromSeconds(1.6), Risk = PlanRisk.FullScan | PlanRisk.CardinalityMisestimate },
    ], "Plan hash value: 1838229974")
    {
        Cursor = new CursorRef(sqlId, 0),
    };

    /// <summary>커서마다 정해 둔 계획을 돌려주고 보고서 요청을 기록하는 계획 서비스.</summary>
    internal sealed class FakePlans : IExecutionPlanService
    {
        public Dictionary<string, CorePlan> Cursors { get; } = new(StringComparer.Ordinal);

        public CursorRef? LastCursor { get; set; }

        public Func<CursorRef, bool, string> Report { get; set; } = (_, html) => html ? "<html>report</html>" : "SQL Monitoring Report";

        public List<(string SqlId, bool Html)> ReportCalls { get; } = [];

        public Task<CorePlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
            Task.FromResult(new CorePlan([new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 3 }, new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", Cost = 3 }], "x"));

        public Task<CorePlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
            Cursors.TryGetValue(cursor.SqlId, out var plan) ? Task.FromResult(plan) : throw new InvalidOperationException("공유 풀에서 계획을 찾을 수 없습니다: " + cursor.SqlId);

        public Task<CursorRef?> GetLastCursorAsync(ISession s, CancellationToken ct) => Task.FromResult(LastCursor);

        public Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, bool html, CancellationToken ct)
        {
            ReportCalls.Add((cursor.SqlId, html));
            return Task.FromResult(Report(cursor, html));
        }
    }

    private static PlanViewModel NewVm(FakePlans plans, out TestSession session, LicensePack pack = LicensePack.None)
    {
        session = new TestSession(TestData.Profile(licensePack: pack));
        return new PlanViewModel(plans) { PostToUi = a => a(), HasCommand = _ => false };
    }

    // ---- 힌트 사본 글 ----

    [Theory]
    [InlineData("SELECT * FROM t", "SELECT /*+ GATHER_PLAN_STATISTICS */ * FROM t")]
    [InlineData("select a from t", "select /*+ GATHER_PLAN_STATISTICS */ a from t")]
    [InlineData("-- 머리 주석 SELECT\n/* select */ SELECT a FROM t", "-- 머리 주석 SELECT\n/* select */ SELECT /*+ GATHER_PLAN_STATISTICS */ a FROM t")]
    [InlineData("WITH x AS (SELECT 1 c FROM dual) SELECT c FROM x", "WITH x AS (SELECT /*+ GATHER_PLAN_STATISTICS */ 1 c FROM dual) SELECT c FROM x")]
    [InlineData("SELECT /*+ INDEX(t ix) */ a FROM t", "SELECT /*+ GATHER_PLAN_STATISTICS INDEX(t ix) */ a FROM t")]
    [InlineData("SELECT /*+ gather_plan_statistics */ a FROM t", "SELECT /*+ gather_plan_statistics */ a FROM t")]
    [InlineData("UPDATE t SET a = 'select'", "UPDATE /*+ GATHER_PLAN_STATISTICS */ t SET a = 'select'")]
    public void Hint_goes_right_after_the_first_verb_outside_comments_and_strings(string sql, string expected) =>
        Assert.Equal(expected, PlanStatisticsHint.AddTo(sql));

    [Theory]
    [InlineData("")]
    [InlineData("BEGIN NULL; END;")]
    [InlineData("'SELECT' -- SELECT")]
    [InlineData("\"SELECT\" x")]
    public void Hint_without_a_verb_is_null(string sql) => Assert.Null(PlanStatisticsHint.AddTo(sql));

    [Fact]
    public void Rerun_copy_refuses_dml_and_keeps_binds_and_drops_the_editor_range()
    {
        var dml = new SqlStatement("DELETE FROM t", new Oranda.Core.Text.TextRange(0, 13), StatementKind.Dml) { Verb = "DELETE" };
        var ex = Assert.Throws<InvalidOperationException>(() => PlanStatsRerunner.BuildCopy(dml, PlanRerunMode.Hint));
        Assert.Contains("DML", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => PlanStatsRerunner.BuildCopy(dml, PlanRerunMode.StatisticsLevel));

        var hinted = PlanStatsRerunner.BuildCopy(Query with { Range = new Oranda.Core.Text.TextRange(5, 10) }, PlanRerunMode.Hint);
        Assert.StartsWith("SELECT /*+ GATHER_PLAN_STATISTICS */ e.ename", hinted.Text, StringComparison.Ordinal);
        Assert.Equal(["d"], hinted.BindNames);
        Assert.Equal(default, hinted.Range);
        Assert.Equal(Query.Text, PlanStatsRerunner.BuildCopy(Query, PlanRerunMode.StatisticsLevel).Text);
        Assert.Equal("ALTER SESSION", PlanStatsRerunner.StatisticsLevelStatement().Verb);
        Assert.Equal(StatementKind.Ddl, PlanStatsRerunner.StatisticsLevelStatement().Kind); // ALTER SESSION: 정책이 운영에서도 묻지 않는 동사
    }

    // ---- 띠 조건 ----

    [Fact]
    public void Band_shows_only_for_an_actual_plan_whose_a_rows_are_all_empty()
    {
        Assert.True(PlanViewModel.NeedsStatisticsBand(NoActuals()));
        Assert.False(PlanViewModel.NeedsStatisticsBand(WithActuals("a")));
        Assert.False(PlanViewModel.NeedsStatisticsBand(NoActuals() with { Cursor = null })); // 예상 계획
        Assert.False(PlanViewModel.NeedsStatisticsBand(new CorePlan([], string.Empty) { Cursor = new CursorRef("a") })); // 단계 없음

        // 한 단계라도 A-Rows 가 있으면 통계 수집은 켜져 있었다.
        var partial = NoActuals() with { Steps = [.. NoActuals().Steps.Select((s, i) => i == 2 ? s with { ActualRows = 0 } : s)] };
        Assert.False(PlanViewModel.NeedsStatisticsBand(partial));
    }

    [Fact]
    public void Band_follows_apply_and_hides_on_the_report_tab()
    {
        var vm = NewVm(new FakePlans(), out _);
        vm.Apply(NoActuals(), "실제(ALLSTATS LAST)");
        Assert.True(vm.ShowStatisticsBand);
        Assert.True(vm.IsBandVisible);
        Assert.False(vm.IsEstimated);
        Assert.Contains("A-Rows 없음", vm.StatusText, StringComparison.Ordinal);

        vm.SelectedTabIndex = PlanViewModel.ReportTabIndex;
        Assert.False(vm.IsBandVisible);
        vm.SelectedTabIndex = PlanViewModel.GraphTabIndex;
        Assert.True(vm.IsBandVisible);

        vm.Apply(NoActuals() with { Cursor = null }, "예상");
        Assert.False(vm.ShowStatisticsBand);
        Assert.True(vm.IsEstimated);
        Assert.Contains("PLAN_TABLE", vm.StatusText, StringComparison.Ordinal); // 예상 계획 안내
    }

    // ---- 다시 실행 ----

    [Fact]
    public async Task Rerun_shows_the_new_cursor_plan_and_clears_the_band()
    {
        var plans = new FakePlans();
        plans.Cursors["new0000000001"] = WithActuals("new0000000001");
        var vm = NewVm(plans, out var session);
        vm.Apply(NoActuals(), "실제");
        vm.LastStatement = Query;
        var seen = new List<(PlanRerunMode, SqlStatement, bool)>();
        vm.Rerunner = (mode, stmt, ct) =>
        {
            seen.Add((mode, stmt, vm.IsRerunning));
            return Task.FromResult(new PlanRerunOutcome(session, new CursorRef("new0000000001", 0), 1208));
        };

        await vm.RerunAsync(PlanRerunMode.Hint);

        var (mode, stmt, running) = Assert.Single(seen);
        Assert.Equal(PlanRerunMode.Hint, mode);
        Assert.Same(Query, stmt);
        Assert.True(running); // 실행 중에는 띠 단추가 꺼진다
        Assert.False(vm.IsRerunning);
        Assert.True(vm.CanRerun);
        Assert.Equal("new0000000001", vm.CurrentPlan!.Cursor!.SqlId);
        Assert.False(vm.ShowStatisticsBand);
        Assert.True(vm.HasActuals);
        Assert.StartsWith("실제 행 수를 받았습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("1,208행 읽음", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerun_with_statistics_level_says_the_session_setting_stays()
    {
        var plans = new FakePlans();
        plans.Cursors["s"] = WithActuals("s");
        var vm = NewVm(plans, out var session);
        vm.LastStatement = Query;
        vm.Rerunner = (_, _, _) => Task.FromResult(new PlanRerunOutcome(session, new CursorRef("s"), 3));
        await vm.RerunAsync(PlanRerunMode.StatisticsLevel);
        Assert.Contains("STATISTICS_LEVEL 은 ALL 로 남습니다", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerun_without_a_statement_or_runner_or_with_a_failure_explains_why()
    {
        var vm = NewVm(new FakePlans(), out var session);
        await vm.RerunAsync(PlanRerunMode.Hint);
        Assert.Contains("다시 실행할 문장이 없습니다", vm.StatusText, StringComparison.Ordinal);

        vm.CurrentStatement = () => Query; // 마지막 실행이 없으면 편집기 지금 문장
        await vm.RerunAsync(PlanRerunMode.Hint);
        Assert.Contains("실행 엔진이 없습니다", vm.StatusText, StringComparison.Ordinal);

        vm.Rerunner = (_, _, _) => throw new InvalidOperationException("ORA-00942: table or view does not exist\n상세");
        await vm.RerunAsync(PlanRerunMode.Hint);
        Assert.Equal("다시 실행 실패: ORA-00942: table or view does not exist", vm.StatusText);
        Assert.False(vm.IsRerunning);

        vm.Rerunner = (_, _, ct) => Task.FromException<PlanRerunOutcome>(new OperationCanceledException());
        await vm.RerunAsync(PlanRerunMode.Hint);
        Assert.Equal("다시 실행 취소됨", vm.StatusText);
    }

    [Fact]
    public async Task Always_hint_reruns_once_by_itself_and_not_again_when_rows_stay_empty()
    {
        var plans = new FakePlans();
        plans.Cursors["still"] = NoActuals("still");
        var vm = NewVm(plans, out var session);
        var saved = new List<bool>();
        vm.SaveAlwaysHint = saved.Add;
        vm.AlwaysHint = true;
        Assert.Equal([true], saved);
        vm.LastStatement = Query;
        var runs = 0;
        vm.Rerunner = (_, _, _) =>
        {
            runs++;
            return Task.FromResult(new PlanRerunOutcome(session, new CursorRef("still"), 0));
        };

        vm.Apply(NoActuals(), "실제");
        for (var i = 0; i < 20 && vm.IsRerunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, runs); // 다시 실행해도 A-Rows 가 없으면 또 하지 않는다(무한 반복 없음)
        Assert.Contains("A-Rows 가 아직 없습니다", vm.StatusText, StringComparison.Ordinal);

        vm.LastStatement = Query; // 새로 실행한 문장이면 다시 한 번
        vm.Apply(NoActuals(), "실제");
        Assert.Equal(2, runs);

        vm.AlwaysHint = false;
        vm.LastStatement = Query;
        vm.Apply(NoActuals(), "실제");
        Assert.Equal(2, runs);
    }

    // ---- 보고서 탭 ----

    [Fact]
    public async Task Report_states_follow_cursor_license_and_content()
    {
        var plans = new FakePlans();
        plans.Cursors["r"] = WithActuals("r");
        var vm = NewVm(plans, out var none);

        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.NoCursor, vm.ReportState); // 계획 없음

        vm.Apply(WithActuals("r") with { Cursor = null }, "예상");
        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.NoCursor, vm.ReportState); // 예상 계획

        await vm.ShowCursorAsync(none, new CursorRef("r"));
        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.NoLicense, vm.ReportState);
        Assert.Empty(plans.ReportCalls); // 선언이 없으면 보내지 않는다
        Assert.Contains("Diagnostics + Tuning", vm.ReportMessage, StringComparison.Ordinal);

        var diag = new TestSession(TestData.Profile(licensePack: LicensePack.Diagnostics));
        await vm.ShowCursorAsync(diag, new CursorRef("r"));
        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.NoLicense, vm.ReportState); // Diagnostics 만으로는 모자람

        var full = new TestSession(TestData.Profile(licensePack: LicensePack.DiagnosticsAndTuning));
        await vm.ShowCursorAsync(full, new CursorRef("r"));
        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.Ready, vm.ReportState);
        Assert.Equal("SQL Monitoring Report", vm.ReportText);
        Assert.Equal(("r", false), plans.ReportCalls[^1]);
        Assert.True(vm.CanUseReport);

        plans.Report = (_, _) => "  ";
        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.Empty, vm.ReportState);
        Assert.Equal(PlanViewModel.NoMonitorRecordText, vm.ReportMessage);

        plans.Report = (_, _) => throw new InvalidOperationException("ORA-13647: boom\nmore");
        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.Error, vm.ReportState);
        Assert.EndsWith("ORA-13647: boom", vm.ReportMessage, StringComparison.Ordinal);

        plans.Report = (_, _) => throw new UnauthorizedAccessException("서버 설정이 막음");
        await vm.LoadReportAsync();
        Assert.Equal(PlanReportState.NoLicense, vm.ReportState);
        Assert.Equal("서버 설정이 막음", vm.ReportMessage);
    }

    [Fact]
    public async Task Server_pack_access_lower_than_the_declaration_wins()
    {
        var server = TestData.Server() with { ManagementPackAccess = "DIAGNOSTIC" };
        Assert.False(PlanViewModel.HasTuningPack(new TestSession(TestData.Profile(licensePack: LicensePack.DiagnosticsAndTuning), server)));
        Assert.True(PlanViewModel.HasTuningPack(new TestSession(TestData.Profile(licensePack: LicensePack.DiagnosticsAndTuning))));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Opening_the_report_tab_loads_once_per_cursor_and_html_goes_to_the_browser()
    {
        var plans = new FakePlans();
        plans.Cursors["h"] = WithActuals("h");
        var vm = NewVm(plans, out _);
        var full = new TestSession(TestData.Profile(licensePack: LicensePack.DiagnosticsAndTuning));
        await vm.ShowCursorAsync(full, new CursorRef("h"));
        Assert.Equal(PlanReportState.NoCursor, vm.ReportState); // 탭을 열기 전에는 읽지 않는다
        Assert.Empty(plans.ReportCalls);

        vm.SelectedTabIndex = PlanViewModel.ReportTabIndex;
        for (var i = 0; i < 50 && vm.ReportState != PlanReportState.Ready; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(PlanReportState.Ready, vm.ReportState);
        vm.SelectedTabIndex = 0;
        vm.SelectedTabIndex = PlanViewModel.ReportTabIndex;
        await Task.Delay(30);
        Assert.Single(plans.ReportCalls); // 같은 커서면 다시 읽지 않는다

        string? opened = null;
        vm.OpenFile = p =>
        {
            opened = p;
            return true;
        };
        var path = await vm.OpenReportInBrowserAsync();
        Assert.NotNull(path);
        Assert.Equal(path, opened);
        Assert.EndsWith("oranda-sqlmonitor-h.html", path, StringComparison.Ordinal);
        Assert.Equal("<html>report</html>", File.ReadAllText(path!));
        Assert.Equal(("h", true), plans.ReportCalls[^1]);
        File.Delete(path!);
    }

    // ---- 다른 기능 명령 단추 ----

    [Fact]
    public void Stats_and_child_cursor_buttons_follow_command_registration_and_call_by_id()
    {
        var registered = new HashSet<string>();
        var vm = new PlanViewModel(new FakePlans()) { PostToUi = a => a(), HasCommand = registered.Contains };
        Assert.False(vm.HasStatsCommand);
        Assert.False(vm.ShowTuningButtons);

        registered.Add(PlanViewModel.StatsCommandId);
        vm.RefreshCommandButtons();
        Assert.True(vm.HasStatsCommand);
        Assert.False(vm.HasChildCursorsCommand);
        Assert.True(vm.ShowTuningButtons);
        vm.SelectedTabIndex = PlanViewModel.ReportTabIndex;
        Assert.False(vm.ShowTuningButtons); // 보고서 탭에서는 숨김

        var ran = new List<string>();
        vm.RunCommand = id =>
        {
            ran.Add(id);
            return Task.CompletedTask;
        };
        vm.RunCommand(PlanViewModel.ChildCursorsCommandId);
        Assert.Equal([PlanViewModel.ChildCursorsCommandId], ran);
    }

    [Fact]
    public async Task Stats_and_child_cursor_buttons_pass_the_plan_table_and_sql_id()
    {
        var vm = NewVm(new FakePlans(), out _);
        var stats = new List<(string?, string)>();
        var cursors = new List<(string, int?)>();
        var ran = new List<string>();
        vm.OpenStats = (o, t) =>
        {
            stats.Add((o, t));
            return Task.CompletedTask;
        };
        vm.OpenChildCursors = (id, inst) =>
        {
            cursors.Add((id, inst));
            return Task.CompletedTask;
        };
        vm.RunCommand = id =>
        {
            ran.Add(id);
            return Task.CompletedTask;
        };

        await vm.ShowTableStatsAsync();
        await vm.ShowChildCursorsAsync();
        Assert.Equal([PlanViewModel.StatsCommandId, PlanViewModel.ChildCursorsCommandId], ran); // 계획이 없으면 대상 없이

        vm.Apply(WithActuals("c0000000000001") with { Cursor = new CursorRef("c0000000000001", 0, 2) }, "실제");
        vm.SelectedRow = vm.Rows[0]; // SELECT STATEMENT 는 표가 아니다 → 가장 비싼 표 접근
        await vm.ShowTableStatsAsync();
        Assert.Equal(("MES", "LOT_HIST"), stats[^1]);
        await vm.ShowChildCursorsAsync();
        Assert.Equal(("c0000000000001", (int?)2), cursors[^1]);

        PlanStep[] two =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 9 },
            new(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "A", Cost = 1 },
            new(2, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "S", ObjectName = "B", Cost = 8 },
        ];
        vm.Apply(new CorePlan(two, string.Empty), "예상");
        vm.SelectedRow = vm.Rows[1]; // 고른 표 접근이 먼저
        await vm.ShowTableStatsAsync();
        Assert.Equal(((string?)null, "A"), stats[^1]);
        await vm.ShowChildCursorsAsync(); // 예상 계획은 커서가 없다
        Assert.Equal(PlanViewModel.ChildCursorsCommandId, ran[^1]);
    }

    [Fact]
    public void Graph_follows_the_tree_selection_and_the_color_basis()
    {
        var vm = NewVm(new FakePlans(), out _);
        vm.Apply(WithActuals("g"), "실제");
        Assert.True(vm.HasGraph);
        Assert.True(vm.HasTimeBasis);
        Assert.Equal(PlanGraphColorBasis.Time, vm.GraphColorBasis);
        Assert.Equal(vm.SelectedRow!.Id, vm.GraphSelectedId);

        vm.SelectedRow = vm.Rows[0];
        Assert.Equal(0, vm.GraphSelectedId);
        vm.SelectGraphStep(2);
        Assert.Same(vm.Rows[2], vm.SelectedRow); // 그래프 → 트리

        vm.GraphColorIndex = 1;
        Assert.Equal(PlanGraphColorBasis.Cost, vm.GraphColorBasis);
        Assert.Equal("비용 비중", vm.GraphColorLegend);

        vm.GraphColorBasis = PlanGraphColorBasis.Time;
        vm.Apply(NoActuals() with { Cursor = null }, "예상"); // 시간이 없으면 비용으로
        Assert.Equal(PlanGraphColorBasis.Cost, vm.GraphColorBasis);
        Assert.False(vm.HasTimeBasis);
    }

    // ---- 다시 실행 실행 쪽 ----

    /// <summary>문장마다 결과를 정해 두고 받은 순서를 남긴다.</summary>
    private sealed class Engine : IExecutionService
    {
        public List<SqlStatement> Calls { get; } = [];

        public Func<SqlStatement, ExecutionResult> Result { get; set; } = s => new ExecutionResult(s, ExecutionStatus.Succeeded);

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            Assert.True(options?.SkipHistory); // 앱이 대신 보낸 문장은 이력에 남기지 않는다
            Calls.Add(stmt);
            return Task.FromResult(Result(stmt));
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task Rerunner_sends_alter_then_query_fetches_everything_and_reads_the_last_cursor()
    {
        var session = new TestSession();
        var state = new ExecutionState();
        var binds = new BindVariableStore();
        binds.Add("d").Text = "10";
        var rows = TestResultSet.FromText(["ENAME"], [.. Enumerable.Range(0, 1200).Select(i => new object?[] { "E" + i })]);
        var engine = new Engine { Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { ResultSet = s.Kind == StatementKind.Query ? rows : null } };
        var plans = new FakePlans { LastCursor = new CursorRef("abc", 1) };
        var runner = new PlanStatsRerunner(state, () => binds, () => session, () => engine, () => plans);

        var outcome = await runner.RunAsync(Query, PlanRerunMode.StatisticsLevel, default);

        Assert.Equal(["ALTER SESSION SET STATISTICS_LEVEL = ALL", Query.Text], engine.Calls.Select(c => c.Text));
        Assert.Equal(1200, outcome.Rows);
        Assert.True(rows.IsDisposed);
        Assert.Equal(new CursorRef("abc", 1), outcome.Cursor);
        Assert.False(state.IsBusy(session.Id));

        engine.Calls.Clear();
        engine.Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { ResultSet = TestResultSet.FromText(["X"], ["1"]) };
        await runner.RunAsync(Query, PlanRerunMode.Hint, default);
        Assert.Single(engine.Calls);
        Assert.Contains("GATHER_PLAN_STATISTICS", engine.Calls[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerunner_reports_denied_failed_busy_and_unknown_cursor()
    {
        var session = new TestSession();
        var state = new ExecutionState();
        var binds = new BindVariableStore();
        binds.Add("d").Text = "10";
        var engine = new Engine();
        var plans = new FakePlans();
        var runner = new PlanStatsRerunner(state, () => binds, () => session, () => engine, () => plans);

        engine.Result = s => new ExecutionResult(s, ExecutionStatus.Denied) { Policy = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile, "읽기 전용") };
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Query, PlanRerunMode.StatisticsLevel, default));
        Assert.Equal("읽기 전용", denied.Message);
        Assert.Single(engine.Calls); // ALTER 가 막히면 질의를 보내지 않는다
        Assert.False(state.IsBusy(session.Id));

        engine.Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Query, PlanRerunMode.Hint, default));
        Assert.Contains("sql_id", unknown.Message, StringComparison.Ordinal);

        state.BeginRun(session.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Query, PlanRerunMode.Hint, default));
        state.EndRun(session.Id);

        var none = new PlanStatsRerunner(state, () => binds, () => null, () => engine, () => plans);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.RunAsync(Query, PlanRerunMode.Hint, default));
    }
}
