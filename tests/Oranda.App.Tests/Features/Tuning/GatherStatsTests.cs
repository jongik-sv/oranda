using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>SG-1 스키마 브라우저·통계 보기의 「통계 수집」 실행: 명령, 확인 창 VM, 차례 실행(가짜 세션), 캐시 무효화.</summary>
public class GatherStatsTests
{
    private static readonly ShellContributions App = ShellContributions.Scan(typeof(TableStatsViewModel).Assembly);
    private static readonly TableStatsTarget Emp = new("SCOTT", "EMP");
    private static readonly TableStatsTarget Dept = new("SCOTT", "DEPT");

    private static ConnectionProfile Profile(ConnectionEnvironment env = ConnectionEnvironment.Test, Dbms dbms = Dbms.Oracle, bool readOnly = false) =>
        new("SAMPLE", "SCOTT", "localhost/FREEPDB1", Environment: env, ReadOnly: readOnly) { Dbms = dbms };

    private static GatherStatsDialogViewModel NewDialog(
        FakeExecutionService exec, ConnectionProfile? profile = null, IReadOnlyList<TableStatsTarget>? targets = null, Func<CancellationToken, Task<ISession?>>? open = null)
    {
        var p = profile ?? Profile();
        return new GatherStatsDialogViewModel(p, targets ?? [Emp, Dept], new GatherStatsRunner(exec),
            open ?? (_ => Task.FromResult<ISession?>(new FakeSession(p, purpose: SessionPurpose.Dedicated))));
    }

    private static ExecutionResult Fail(ExecutionCall call, string code, int number, DbErrorKind kind, string message) =>
        new(call.Statement, ExecutionStatus.Failed) { Error = new DbError(code, message) { Number = number, Kind = kind } };

    // ---- 명령 ----------------------------------------------------------------------------

    [Fact]
    public void Schema_browser_gather_command_needs_a_table_node_and_a_writable_session()
    {
        var registry = new CommandRegistry(App.Commands);
        var command = registry.Find(GatherStatsCommands.SchemaId);
        Assert.NotNull(command);
        Assert.Contains("context:schema/tableStats@73", command!.Placements);
        Assert.NotNull(registry.Find(GatherStatsCommands.PanelId));

        var keys = new ContextKeyService();
        keys.Set("schemaFocus", true);
        keys.Set("hasSession", true);
        Assert.False(registry.CanExecute(GatherStatsCommands.SchemaId, keys)); // 테이블 노드가 아님(테이블 목록 폴더 포함)
        keys.Set("tableObjectSelected", true);
        Assert.True(registry.CanExecute(GatherStatsCommands.SchemaId, keys));
        keys.Set("readOnlySession", true);
        Assert.False(registry.CanExecute(GatherStatsCommands.SchemaId, keys));
    }

    [Fact]
    public void Stats_panel_gather_command_follows_the_active_stats_tab()
    {
        var registry = new CommandRegistry(App.Commands);
        var keys = new ContextKeyService();
        Assert.False(registry.CanExecute(GatherStatsCommands.PanelId, keys));
        keys.Set(TableStatsCommands.ActiveKey, true);
        Assert.True(registry.CanExecute(GatherStatsCommands.PanelId, keys));
        keys.Set("readOnlySession", true);
        Assert.False(registry.CanExecute(GatherStatsCommands.PanelId, keys));
    }

    // ---- 확인 창 VM ----------------------------------------------------------------------

    [Fact]
    public void Dialog_defaults_preview_every_table_with_auto_options()
    {
        var vm = NewDialog(new FakeExecutionService());

        Assert.True(vm.AutoSample);
        Assert.True(vm.Cascade);
        Assert.Equal(GatherStatsHistogram.Auto, vm.Histogram);
        Assert.Equal(3, vm.HistogramChoices.Count);
        Assert.Equal(["SCOTT.EMP", "SCOTT.DEPT"], vm.Rows.Select(r => r.Display));
        Assert.Contains("tabname          => 'EMP'", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("tabname          => 'DEPT'", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("DBMS_STATS.AUTO_SAMPLE_SIZE", vm.Preview, StringComparison.Ordinal);
        Assert.True(vm.CanRun);
        Assert.False(vm.IsProduction);
        Assert.Equal(string.Empty, vm.ProductionWarning);
    }

    [Fact]
    public void Bad_percent_or_degree_blocks_running_and_says_why()
    {
        var vm = NewDialog(new FakeExecutionService());

        vm.AutoSample = false;
        vm.SamplePercentText = "abc";
        Assert.False(vm.CanRun);
        Assert.NotEqual(string.Empty, vm.OptionsError);

        vm.SamplePercentText = "20";
        Assert.True(vm.CanRun);
        Assert.Equal(string.Empty, vm.OptionsError);
        Assert.Contains("estimate_percent => 20,", vm.Preview, StringComparison.Ordinal);

        vm.DegreeText = "0";
        Assert.False(vm.CanRun);
        vm.DegreeText = " 4 ";
        Assert.True(vm.CanRun);
        Assert.Contains("degree           => 4", vm.Preview, StringComparison.Ordinal);

        vm.Histogram = GatherStatsHistogram.None;
        vm.Cascade = false;
        Assert.Contains("'FOR ALL COLUMNS SIZE 1'", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("cascade          => FALSE", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Tibero_offers_every_choice_but_leaves_out_auto_invalidate()
    {
        var vm = NewDialog(new FakeExecutionService(), Profile(dbms: Dbms.Tibero));

        Assert.Equal([GatherStatsHistogram.Auto, GatherStatsHistogram.None, GatherStatsHistogram.SkewOnly], vm.HistogramChoices.Select(c => c.Value));
        Assert.Contains("AUTO_SAMPLE_SIZE", vm.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTO_INVALIDATE", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("AUTO_INVALIDATE", NewDialog(new FakeExecutionService()).Preview, StringComparison.Ordinal); // Oracle 은 넣는다
    }

    [Fact]
    public void Production_connection_shows_a_big_warning_and_read_only_cannot_run()
    {
        var prod = NewDialog(new FakeExecutionService(), Profile(ConnectionEnvironment.Production));
        Assert.True(prod.IsProduction);
        Assert.Contains("운영", prod.ProductionWarning, StringComparison.Ordinal);
        Assert.Equal("운영", prod.EnvironmentText);
        Assert.True(prod.CanRun);

        var ro = NewDialog(new FakeExecutionService(), Profile(readOnly: true));
        Assert.False(ro.CanRun);
        Assert.NotEqual(string.Empty, ro.BlockedText);
    }

    [Fact]
    public void Script_text_puts_each_block_with_a_slash_for_the_editor()
    {
        var vm = NewDialog(new FakeExecutionService());

        var script = vm.ScriptText.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.StartsWith("-- ", script, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("END;\n/", StringSplitOptions.None).Length - 1);
        Assert.Equal("gather_2_tables.sql", vm.ScriptTitle);
        Assert.Equal("gather_EMP.sql", NewDialog(new FakeExecutionService(), targets: [Emp]).ScriptTitle);
    }

    // ---- 실행 흐름 -----------------------------------------------------------------------

    [Fact]
    public async Task Run_goes_table_by_table_on_a_dedicated_session_with_history_and_preconfirmation()
    {
        var exec = new FakeExecutionService();
        ISession? opened = null;
        var vm = NewDialog(exec, open: _ =>
        {
            opened = new FakeSession(Profile(), purpose: SessionPurpose.Dedicated);
            return Task.FromResult<ISession?>(opened);
        });
        IReadOnlyList<TableStatsTarget>? done = null;
        vm.Completed += (_, t) => done = t;

        await vm.RunAsync();

        Assert.Equal(2, exec.Calls.Count);
        Assert.All(exec.Calls, c => Assert.Same(opened, c.Session));
        Assert.Contains("'EMP'", exec.Calls[0].Statement.Text, StringComparison.Ordinal);
        Assert.Contains("'DEPT'", exec.Calls[1].Statement.Text, StringComparison.Ordinal);
        Assert.All(exec.Calls, c =>
        {
            Assert.Equal(StatementKind.PlSql, c.Statement.Kind);
            Assert.Equal(HistorySource.SchemaBrowser, c.Options.Source);
            Assert.False(c.Options.SkipHistory);
            Assert.Contains(c.Statement.Text, c.Options.PreConfirmed!);
        });
        Assert.All(vm.Rows, r => Assert.Equal(GatherStatsOutcome.Succeeded, r.Outcome));
        Assert.Equal([Emp, Dept], done);
        Assert.True(vm.IsDone);
        Assert.False(vm.IsRunning);
        Assert.Equal(1, ((FakeSession)opened!).DisposeCount); // 다 쓰면 닫는다
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest_and_privilege_errors_read_plainly()
    {
        var exec = new FakeExecutionService
        {
            Responder = call => call.Statement.Text.Contains("'EMP'", StringComparison.Ordinal)
                ? Fail(call, "ORA-20000", 20000, DbErrorKind.Other, "ORA-20000: Unable to analyze TABLE \"SCOTT\".\"EMP\", insufficient privileges or does not exist")
                : new ExecutionResult(call.Statement, ExecutionStatus.Succeeded) { Elapsed = TimeSpan.FromSeconds(3) },
        };
        var vm = NewDialog(exec);
        IReadOnlyList<TableStatsTarget>? done = null;
        vm.Completed += (_, t) => done = t;

        await vm.RunAsync();

        Assert.Equal(2, exec.Calls.Count);
        Assert.Equal(GatherStatsOutcome.Failed, vm.Rows[0].Outcome);
        Assert.Contains("권한", vm.Rows[0].Message, StringComparison.Ordinal);
        Assert.Equal(GatherStatsOutcome.Succeeded, vm.Rows[1].Outcome);
        Assert.Equal([Dept], done);
        Assert.Contains("1", vm.SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ORA-01031", 1031, DbErrorKind.InsufficientPrivileges, "권한")]
    [InlineData("ORA-00942", 942, DbErrorKind.ObjectNotFound, "권한")]
    [InlineData("ORA-00054", 54, DbErrorKind.ResourceBusy, "ORA-00054")]
    public void Error_messages_are_friendly_for_privileges_and_keep_the_rest(string code, int number, DbErrorKind kind, string expected)
    {
        var text = GatherStatsRunner.Describe(new DbError(code, code + ": something") { Number = number, Kind = kind }, Emp);

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_stops_the_current_table_and_marks_the_rest_cancelled()
    {
        var exec = new FakeExecutionService();
        var vm = NewDialog(exec, targets: [Emp, Dept, new("SCOTT", "BONUS")]);
        exec.Responder = call =>
        {
            vm.Cancel(); // 첫 테이블 실행 중 사용자가 취소
            return new ExecutionResult(call.Statement, ExecutionStatus.Cancelled) { Error = new DbError("ORA-01013", "ORA-01013: user requested cancel") { Kind = DbErrorKind.Cancelled } };
        };

        await vm.RunAsync();

        Assert.Single(exec.Calls);
        Assert.All(vm.Rows, r => Assert.Equal(GatherStatsOutcome.Cancelled, r.Outcome));
        Assert.True(vm.IsDone);
    }

    [Fact]
    public async Task No_session_means_nothing_runs()
    {
        var exec = new FakeExecutionService();
        var vm = NewDialog(exec, open: _ => Task.FromResult<ISession?>(null));

        await vm.RunAsync();

        Assert.Empty(exec.Calls);
        Assert.NotEqual(string.Empty, vm.SummaryText);
        Assert.False(vm.IsRunning);
        Assert.True(vm.CanRun); // 다시 해 볼 수 있다
    }

    [Fact]
    public async Task Session_open_errors_are_shown_not_thrown()
    {
        var vm = NewDialog(new FakeExecutionService(), open: _ => throw new InvalidOperationException("ORA-12541: no listener"));

        await vm.RunAsync();

        Assert.Contains("ORA-12541", vm.SummaryText, StringComparison.Ordinal);
        Assert.False(vm.IsRunning);
    }

    // ---- 끝난 뒤 새로 읽기 -----------------------------------------------------------------

    [Fact]
    public async Task Gathered_tables_drop_the_column_stats_cache_and_reload_open_stats_tabs()
    {
        var tuning = new FakeTuningService();
        tuning.Columns.Add(new ColumnStatsRow("EMPNO", 14, 0, 0.07, "NONE", 1, null));
        ISession session = new TuningFakeSession();
        using var cache = new SchemaColumnStatsViewModel(() => tuning, () => session) { Delay = TimeSpan.Zero };
        await cache.ShowAsync(new DbObjectRef("SCOTT", "EMP", "TABLE"), "EMPNO");
        await cache.ShowAsync(new DbObjectRef("SCOTT", "DEPT", "TABLE"), "EMPNO");
        Assert.Equal(2, tuning.Calls.Count(c => c == "columns"));

        using var empTab = new TableStatsViewModel(tuning);
        empTab.Attach(session, Emp);
        using var otherTab = new TableStatsViewModel(tuning);
        otherTab.Attach(session, new TableStatsTarget("SCOTT", "BONUS"));

        await GatherStatsRefresh.ApplyAsync([Emp], cache, [empTab, otherTab], CancellationToken.None);

        Assert.Contains("table SCOTT.EMP", tuning.Calls);
        Assert.DoesNotContain("table SCOTT.BONUS", tuning.Calls);
        var before = tuning.Calls.Count(c => c == "columns");
        await cache.ShowAsync(new DbObjectRef("SCOTT", "EMP", "TABLE"), "EMPNO");
        await cache.ShowAsync(new DbObjectRef("SCOTT", "DEPT", "TABLE"), "EMPNO");
        Assert.Equal(before + 1, tuning.Calls.Count(c => c == "columns")); // EMP 만 다시 읽는다
    }

    [Fact]
    public async Task Stats_panel_button_hands_its_table_to_the_gather_hook()
    {
        using var vm = new TableStatsViewModel(new FakeTuningService());
        TableStatsTarget? asked = null;
        vm.GatherStats = t =>
        {
            asked = t;
            return Task.CompletedTask;
        };

        Assert.False(vm.CanGatherStats);
        await vm.RunGatherStatsAsync();
        Assert.Null(asked);

        vm.Attach(new TuningFakeSession(), Emp);
        Assert.True(vm.CanGatherStats);
        await vm.RunGatherStatsAsync();
        Assert.Equal(Emp, asked);
    }
}
