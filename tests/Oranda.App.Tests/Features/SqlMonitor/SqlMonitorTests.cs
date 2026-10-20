using Oranda.App.Features.SqlMonitor;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using PlanResult = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.SqlMonitor;

/// <summary>TSK-09-02 SQL 모니터 화면: 명령 등록, 조건 만들기·사전 정의 구간, 검색, 전문·바인드, 실제 계획.</summary>
public class SqlMonitorTests
{
    private static readonly ShellContributions App = ShellContributions.Scan(typeof(SqlMonitorViewModel).Assembly);

    private static readonly ServerCapabilities Dba = new(true, true, true, true, true);

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SqlMonitorViewModel());
    }

    [Fact]
    public void All_sql_monitor_commands_are_registered_and_menu_follows_keys()
    {
        var registry = new CommandRegistry(App.Commands);
        foreach (var id in SqlMonitorCommands.Ids)
        {
            var def = registry.Find(id);
            Assert.NotNull(def);
            Assert.Equal(CommandCategory.Monitor, def!.Category);
        }

        Assert.Contains(App.Panels, p => p.Id == SqlMonitorCommands.PanelId && p.Kind == ToolPanelKind.Document);

        var keys = new ContextKeyService();
        keys.Set("hasSession", true);
        Assert.True(registry.CanExecute("oranda.monitor.sql", keys));
        Assert.False(registry.CanExecute("oranda.monitor.sqlShowPlan", keys));
        keys.Set("monitorFocus", true);
        keys.Set("monitorSqlOpen", true);
        keys.Set("monitorSqlSelected", true);
        Assert.True(registry.CanExecute("oranda.monitor.sqlSearch", keys));
        Assert.True(registry.CanExecute("oranda.monitor.sqlShowPlan", keys));
        Assert.True(registry.CanExecute("oranda.monitor.sqlShowBinds", keys));
    }

    [Fact]
    public void Preset_fills_conditions_and_editing_a_value_returns_to_custom()
    {
        var vm = new SqlMonitorViewModel();
        var slow = SqlMonitorViewModel.Presets.Single(p => p.Name.StartsWith("느린", StringComparison.Ordinal));

        vm.SelectedPreset = slow;
        var c = vm.BuildCriteria();

        Assert.Equal(1d, c.MinElapsedSeconds);
        Assert.Equal(10_000, c.MinBufferGets);
        Assert.Null(c.MinExecutions);
        Assert.Same(slow, vm.SelectedPreset);

        vm.MinBlocksText = "500";
        Assert.Same(SqlMonitorViewModel.Custom, vm.SelectedPreset);
        Assert.Equal(500, vm.BuildCriteria().MinBufferGets);
        Assert.Equal(["사용자 지정"], SqlMonitorViewModel.Presets.Take(1).Select(p => p.Name));
        Assert.Contains(SqlMonitorViewModel.Presets, p => p.Name.StartsWith("Critical", StringComparison.Ordinal));
        Assert.Contains(SqlMonitorViewModel.Presets, p => p.Name.StartsWith("OLTP", StringComparison.Ordinal));
        Assert.Contains(SqlMonitorViewModel.Presets, p => p.Name.StartsWith("Batch", StringComparison.Ordinal));
    }

    [Fact]
    public void Criteria_parse_text_schema_recent_and_reject_bad_numbers()
    {
        var vm = new SqlMonitorViewModel
        {
            TextFilter = " emp ",
            SchemaFilter = "scott",
            ActiveMinutesText = "30",
            MinExecutionsText = "1,000",
            Top = 5000,
            OrderBy = TopSqlOrder.DiskReads,
        };

        var c = vm.BuildCriteria();
        Assert.Equal("emp", c.TextContains);
        Assert.Equal("scott", c.ParsingSchema);
        Assert.Equal(TimeSpan.FromMinutes(30), c.ActiveWithin);
        Assert.Equal(1000, c.MinExecutions);
        Assert.Equal(1000, c.Top);
        Assert.Equal(TopSqlOrder.DiskReads, c.OrderBy);

        vm.MinElapsedText = "abc";
        Assert.Throws<FormatException>(() => vm.BuildCriteria());
    }

    [Fact]
    public async Task Start_opens_monitor_session_searches_and_sets_menu_keys()
    {
        var h = new Harness();
        h.Vm.MinElapsedText = "1";
        h.Vm.MinBlocksText = "10000";

        await h.Vm.StartAsync();

        Assert.True(h.Vm.IsOpen);
        Assert.Equal(1, h.Opened);
        Assert.Equal(2, h.Vm.Results.Count);
        Assert.Equal(1d, h.Monitor.LastCriteria!.MinElapsedSeconds);
        Assert.Equal(10_000, h.Monitor.LastCriteria.MinBufferGets);
        Assert.True(h.Keys.Get("monitorSqlOpen"));
        Assert.False(h.Keys.Get("monitorSqlSelected"));

        h.Vm.SelectedSql = h.Vm.Results[0];
        Assert.True(h.Keys.Get("monitorSqlSelected"));

        await h.Vm.StartAsync();
        Assert.Equal(1, h.Opened);
        Assert.Equal(2, h.Monitor.SearchCalls);

        await h.Vm.StopAsync();
        Assert.False(h.Vm.IsOpen);
        Assert.False(h.Keys.Get("monitorSqlOpen"));
        Assert.True(h.Session.Disposed);
    }

    [Fact]
    public async Task Account_without_performance_views_cannot_open()
    {
        var h = new Harness(new ServerCapabilities(false, false, false, false, false));

        await h.Vm.StartAsync();

        Assert.False(h.Vm.IsOpen);
        Assert.True(h.Session.Disposed);
        Assert.Equal(0, h.Monitor.SearchCalls);
        Assert.Contains("권한", h.Vm.StatusText, StringComparison.Ordinal);
        Assert.True(h.Vm.Access.HasNotice);
        Assert.StartsWith("GRANT SELECT_CATALOG_ROLE TO ", h.Vm.Access.GrantSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_number_stops_before_the_server_is_called()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.MinElapsedText = "x";

        await h.Vm.SearchAsync();

        Assert.Equal(1, h.Monitor.SearchCalls);
        Assert.Contains("소요 시간", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_binds_and_plan_for_selected_sql()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.SelectedSql = h.Vm.Results[1];

        await h.Vm.ShowBindsAsync();
        Assert.Equal(new CursorRef("sql2", 1, 1), h.Monitor.LastDetail);
        Assert.Contains("select 2", h.Vm.DetailText, StringComparison.Ordinal);
        Assert.Contains(":B1 (NUMBER) = 7", h.Vm.DetailText, StringComparison.Ordinal);
        Assert.Contains("(캡처 안 됨)", h.Vm.DetailText, StringComparison.Ordinal);

        await h.Vm.ShowPlanAsync();
        Assert.Equal(new CursorRef("sql2", 1, 1), h.Plans.LastCursor);
        Assert.Equal("TYPICAL", h.Plans.LastFormat);
        Assert.Contains("TABLE ACCESS FULL", h.Vm.PlanText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_service_not_ready_and_server_failure_show_in_status_line()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.SelectedSql = h.Vm.Results[0];

        h.Plans.Fail = new NotImplementedException();
        await h.Vm.ShowPlanAsync();
        Assert.Contains("준비", h.Vm.StatusText, StringComparison.Ordinal);

        h.Monitor.Fail = new InvalidOperationException("SQL 을 공유 풀에서 찾을 수 없습니다.\n자세히");
        await h.Vm.ShowBindsAsync();
        Assert.Equal("SQL 을 공유 풀에서 찾을 수 없습니다.", h.Vm.StatusText);
    }

    [Fact]
    public async Task Clearing_selection_clears_detail()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.SelectedSql = h.Vm.Results[0];
        await h.Vm.ShowBindsAsync();
        Assert.NotEmpty(h.Vm.DetailText);

        h.Vm.SelectedSql = null;

        Assert.Empty(h.Vm.DetailText);
        Assert.False(h.Keys.Get("monitorSqlSelected"));
    }

    private sealed class Harness
    {
        public Harness(ServerCapabilities? caps = null)
        {
            Session = new FakeSession(new ServerInfo("23.26.0.0.0", "FREE", "h", "FREEPDB1", "FREE", "FREEPDB1", true, 1, caps ?? Dba));
            Vm = new SqlMonitorViewModel(Monitor, Plans, _ =>
            {
                Opened++;
                return Task.FromResult<ISession?>(Session);
            }, Keys)
            {
                PostToUi = a => a(),
            };
        }

        public FakeMonitor Monitor { get; } = new();

        public FakePlans Plans { get; } = new();

        public ContextKeyService Keys { get; } = new();

        public FakeSession Session { get; }

        public SqlMonitorViewModel Vm { get; }

        public int Opened { get; private set; }
    }

    private sealed class FakePlans : IExecutionPlanService
    {
        public CursorRef? LastCursor { get; private set; }

        public string? LastFormat { get; private set; }

        public Exception? Fail { get; set; }

        public Task<PlanResult> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => throw new NotSupportedException();

        public Task<PlanResult> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
        {
            if (Fail is { } ex)
            {
                throw ex;
            }

            LastCursor = cursor;
            LastFormat = format;
            return Task.FromResult(new PlanResult([new PlanStep(0, null, 0, "TABLE ACCESS")], "| 0 | TABLE ACCESS FULL | EMP |"));
        }
    }

    private sealed class FakeMonitor : IMonitorService
    {
        public int SearchCalls { get; private set; }

        public TopSqlCriteria? LastCriteria { get; private set; }

        public CursorRef? LastDetail { get; private set; }

        public Exception? Fail { get; set; }

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
        {
            SearchCalls++;
            LastCriteria = c;
            IReadOnlyList<TopSqlRow> rows =
            [
                new(new CursorRef("sql1", 0, 1), "select 1 from dual") { Executions = 3, ElapsedSeconds = 2.5, BufferGets = 20_000, ParsingSchema = "SCOTT" },
                new(new CursorRef("sql2", 1, 1), "select 2 from emp") { Executions = 1, ElapsedSeconds = 1.2, BufferGets = 12_000, DiskReads = 5 },
            ];
            return Task.FromResult(rows);
        }

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct)
        {
            if (Fail is { } ex)
            {
                throw ex;
            }

            LastDetail = cursor;
            return Task.FromResult(new SqlDetail(cursor, "select 2 from emp")
            {
                Binds = [new CapturedBind(":B1", 1, "NUMBER", "7"), new CapturedBind(":B2", 2, "DATE", null)],
            });
        }

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => throw new NotSupportedException();

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeSession(ServerInfo server) : ISession
    {
        public bool Disposed { get; private set; }

        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("p", "scott", "h:1521/s");

        public ServerInfo Server { get; } = server;

        public SessionPurpose Purpose => SessionPurpose.Monitor;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
