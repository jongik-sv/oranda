using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.SessionMonitor;

/// <summary>TSK-09-01 세션 모니터 화면: 명령 등록·메뉴 활성 키, 모니터 세션 열기, 목록·트리·상세, Kill 확인, 트레이스.</summary>
public class SessionMonitorTests
{
    private static readonly ShellContributions App = ShellContributions.Scan(typeof(SessionMonitorViewModel).Assembly);

    private static readonly ServerCapabilities Dba = new(true, true, true, true, true);

    private static readonly ServerCapabilities NoPrivilege = new(false, false, false, false, false);

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SessionMonitorViewModel());
    }

    [Fact]
    public void All_monitor_commands_are_registered()
    {
        var registry = new CommandRegistry(App.Commands);
        foreach (var id in SessionMonitorCommands.Ids)
        {
            var def = registry.Find(id);
            Assert.NotNull(def);
            Assert.Equal(CommandCategory.Monitor, def!.Category);
        }

        Assert.Contains(App.Panels, p => p.Id == SessionMonitorCommands.PanelId && p.Kind == ToolPanelKind.Document);
    }

    [Fact]
    public void Monitor_menus_stay_enabled_for_account_without_privileges()
    {
        var registry = new CommandRegistry(App.Commands);
        var keys = new ContextKeyService();
        var bus = new EventBus();
        SessionMonitorCommands.AttachPrivilegeKeys(bus, keys);
        SessionMonitorCommands.AttachPrivilegeKeys(bus, keys);

        // 접속이 없어도 켜져 있다(열면 프로필을 고른다). 권한이 없는 이유는 창이 안내 띠로 알린다.
        Assert.True(registry.CanExecute("oranda.monitor.sessions", keys));
        Assert.True(registry.CanExecute("oranda.monitor.sql", keys));

        keys.Set("hasSession", true);
        bus.Publish(new SessionOpened(Guid.NewGuid(), Profile(), Server(NoPrivilege), SessionPurpose.Work));
        Assert.True(registry.CanExecute("oranda.monitor.sessions", keys));
        Assert.True(registry.CanExecute("oranda.monitor.sql", keys));
        Assert.False(keys.Get("canKillSession"));

        // 모니터 세션이 열려도(목적 Monitor) 작업 세션 기준 키는 바뀌지 않는다.
        bus.Publish(new SessionOpened(Guid.NewGuid(), Profile(), Server(Dba), SessionPurpose.Monitor));
        Assert.False(keys.Get("canKillSession"));

        bus.Publish(new SessionOpened(Guid.NewGuid(), Profile(), Server(Dba), SessionPurpose.Work));
        Assert.True(keys.Get("canKillSession"));
        Assert.True(keys.Get("canTraceSessions"));
    }

    [Fact]
    public async Task Start_opens_monitor_session_and_fills_list_and_tree()
    {
        var h = new Harness();
        await h.Vm.StartAsync();

        Assert.True(h.Vm.IsOpen);
        Assert.Equal(1, h.Opened);
        Assert.Equal(3, h.Vm.Sessions.Count);
        var root = Assert.Single(h.Vm.BlockingRoots);
        Assert.Equal(10, root.Node.Session.Sid);
        Assert.Contains("SCOTT.EMP", Assert.Single(root.Children).Title, StringComparison.Ordinal);
        Assert.True(h.Keys.Get("monitorSessionOpen"));
        Assert.True(h.Keys.Get("canKillSession"));
        Assert.False(h.Keys.Get("monitorSessionSelected"));
        Assert.Contains("세션 3개 · 대기 중 1개", h.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("scott", h.Monitor.LastFilter?.Username);

        h.Vm.SelectedSession = h.Vm.Sessions[1];
        Assert.True(h.Keys.Get("monitorSessionSelected"));

        await h.Vm.StopAsync();
        Assert.False(h.Vm.IsOpen);
        Assert.True(h.Session.Disposed);
        Assert.False(h.Keys.Get("monitorSessionOpen"));
        Assert.False(h.Keys.Get("monitorSessionSelected"));
    }

    [Fact]
    public async Task Account_without_v_session_access_does_not_open()
    {
        var h = new Harness(NoPrivilege);
        await h.Vm.StartAsync();

        Assert.False(h.Vm.IsOpen);
        Assert.True(h.Session.Disposed);
        Assert.Contains("권한이 없어", h.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(h.Keys.Get("monitorSessionOpen"));
        Assert.Equal(0, h.Monitor.ListCalls);

        // 이유와 해결 방법이 안내 띠에 실제 계정 이름으로 보인다.
        Assert.True(h.Vm.Access.HasNotice);
        Assert.Contains("이 접속(scott)", h.Vm.Access.Notice, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT_CATALOG_ROLE TO SCOTT;", h.Vm.Access.Notice, StringComparison.Ordinal);
        Assert.Equal("GRANT SELECT_CATALOG_ROLE TO SCOTT;", h.Vm.Access.GrantSql);
    }

    [Fact]
    public async Task Kill_shows_target_and_runs_only_after_confirmation()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.SelectedSession = h.Vm.Sessions.Single(s => s.Row.Key.Sid == 10);
        string? asked = null;
        h.Vm.Confirm = m =>
        {
            asked = m;
            return Task.FromResult(false);
        };

        await h.Vm.KillAsync(immediate: true);
        Assert.Empty(h.Monitor.Killed);
        Assert.Contains("SID 10, SERIAL# 100", asked, StringComparison.Ordinal);
        Assert.Contains("SQLPLUS", asked, StringComparison.Ordinal);

        h.Vm.Confirm = _ => Task.FromResult(true);
        var before = h.Monitor.ListCalls;
        await h.Vm.KillAsync(immediate: true);
        Assert.Equal((new SessionKey(1, 10, 100), true), Assert.Single(h.Monitor.Killed));
        Assert.True(h.Monitor.ListCalls > before);
        Assert.Contains("Kill", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_without_confirm_callback_does_nothing()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.SelectedSession = h.Vm.Sessions[0];
        h.Vm.Confirm = null;

        await h.Vm.KillAsync(immediate: false);

        Assert.Empty(h.Monitor.Killed);
    }

    [Fact]
    public async Task Detail_and_trace_show_server_results()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.SelectedSession = h.Vm.Sessions.Single(s => s.Row.Key.Sid == 20);

        await h.Vm.LoadDetailAsync();
        Assert.Contains("UPDATE emp SET sal = 1", h.Vm.DetailText, StringComparison.Ordinal);
        Assert.Contains("[잠금 객체]", h.Vm.DetailText, StringComparison.Ordinal);
        Assert.Contains("EMP", h.Vm.DetailText, StringComparison.Ordinal);

        h.Vm.TraceBinds = true;
        await h.Vm.SetTraceAsync(true);
        Assert.Equal("/diag/free_ora_1.trc", h.Vm.TracePath);
        Assert.True(h.Monitor.LastTrace?.Binds);
        Assert.Contains("/diag/free_ora_1.trc", h.Vm.StatusText, StringComparison.Ordinal);

        await h.Vm.SetTraceAsync(false);
        Assert.Null(h.Vm.TracePath);
    }

    [Fact]
    public async Task Server_error_goes_to_status_line()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Monitor.Fail = new InvalidOperationException("ORA-00942: table or view does not exist\nmore");

        await h.Vm.RefreshAsync();

        Assert.Equal("ORA-00942: table or view does not exist", h.Vm.StatusText);
    }

    [Fact]
    public async Task Delta_mode_and_refresh_interval_limits()
    {
        var h = new Harness();
        await h.Vm.StartAsync();

        var item = h.Vm.Sessions.Single(s => s.Row.Key.Sid == 10);
        Assert.Equal(string.Empty, item.DeltaText);
        h.Vm.DeltaMode = true;
        Assert.Equal("CPU+3 EXEC+2", item.DeltaText);

        h.Vm.RefreshSeconds = 1;
        Assert.Equal(SessionMonitorViewModel.MinRefreshSeconds, h.Vm.RefreshSeconds);
        h.Vm.RefreshSeconds = 600;
        Assert.Equal(SessionMonitorViewModel.MaxRefreshSeconds, h.Vm.RefreshSeconds);
    }

    [Fact]
    public async Task Auto_refresh_polls_until_turned_off()
    {
        var h = new Harness();
        await h.Vm.StartAsync();
        h.Vm.RefreshSeconds = SessionMonitorViewModel.MinRefreshSeconds;
        var before = h.Monitor.ListCalls;

        h.Vm.AutoRefresh = true;
        for (var i = 0; i < 60 && h.Monitor.ListCalls == before; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(h.Monitor.ListCalls > before);
        h.Vm.AutoRefresh = false;
        h.Vm.Dispose();
    }

    [Theory]
    [InlineData(true, false, "ALTER SYSTEM DISCONNECT SESSION '12,345' IMMEDIATE")]
    [InlineData(false, true, "ALTER SYSTEM DISCONNECT SESSION '12,345,@2' POST_TRANSACTION")]
    public void Disconnect_statement_is_dangerous_alter_system(bool immediate, bool rac, string text)
    {
        var stmt = SessionMonitorCommands.DisconnectStatement(new SessionKey(2, 12, 345), immediate, rac);

        Assert.Equal(text, stmt.Text);
        Assert.Equal("ALTER SYSTEM", stmt.Verb);
        Assert.NotNull(stmt.DangerousTarget);
    }

    [Fact]
    public void Detail_text_formats_sections()
    {
        var detail = new SessionDetail(new SessionRow(new SessionKey(1, 5, 6), "ACTIVE") { Username = "SCOTT" })
        {
            CurrentSqlText = "SELECT 1 FROM dual",
            Sections = [new DetailSection("통계", ["이름", "값"], [["execute count", 42m]]), new DetailSection("트랜잭션", ["XID"], [])],
        };

        var text = SessionMonitorViewModel.FormatDetail(detail);

        Assert.Contains("SID 5, SERIAL# 6", text, StringComparison.Ordinal);
        Assert.Contains("SELECT 1 FROM dual", text, StringComparison.Ordinal);
        Assert.Contains("execute count  42", text, StringComparison.Ordinal);
        Assert.Contains("[트랜잭션]\n(없음)", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private static ConnectionProfile Profile() => new("p", "scott", "h:1521/s");

    private static ServerInfo Server(ServerCapabilities caps) =>
        new("23.26.0.0.0", "FREE", "h", "FREEPDB1", "FREE", "FREEPDB1", true, 1, caps);

    private sealed class Harness
    {
        public Harness(ServerCapabilities? caps = null)
        {
            Session = new FakeSession(Server(caps ?? Dba));
            Vm = new SessionMonitorViewModel(Monitor, _ =>
            {
                Opened++;
                return Task.FromResult<ISession?>(Session);
            }, Keys)
            {
                PostToUi = a => a(),
                UsernameFilter = "scott",
                Confirm = _ => Task.FromResult(true),
            };
        }

        public FakeMonitor Monitor { get; } = new();

        public ContextKeyService Keys { get; } = new();

        public FakeSession Session { get; }

        public SessionMonitorViewModel Vm { get; }

        public int Opened { get; private set; }
    }

    private sealed class FakeMonitor : IMonitorService
    {
        public int ListCalls { get; private set; }

        public SessionFilter? LastFilter { get; private set; }

        public List<(SessionKey, bool)> Killed { get; } = [];

        public TraceOptions? LastTrace { get; private set; }

        public Exception? Fail { get; set; }

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct)
        {
            if (Fail is { } ex)
            {
                throw ex;
            }

            ListCalls++;
            LastFilter = f;
            IReadOnlyList<SessionRow> rows =
            [
                new(new SessionKey(1, 10, 100), "INACTIVE")
                {
                    Username = "SCOTT",
                    Program = "SQLPLUS",
                    Deltas = new Dictionary<string, long> { ["CPU used by this session"] = 3, ["execute count"] = 2, ["redo size"] = 0 },
                },
                new(new SessionKey(1, 20, 200), "ACTIVE")
                {
                    Username = "SCOTT",
                    SqlId = "abc",
                    Event = "enq: TX - row lock contention",
                    BlockingSession = new SessionKey(1, 10, 0),
                },
                new(new SessionKey(1, 30, 300), "ACTIVE") { Username = "HR" },
            ];
            return Task.FromResult(rows);
        }

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) =>
            Task.FromResult(new BlockingTree(
            [
                new BlockingNode(new SessionKey(1, 10, 100),
                [
                    new BlockingNode(new SessionKey(1, 20, 200), []) { Username = "SCOTT", Event = "enq: TX", SecondsInWait = 5, LockedObject = "SCOTT.EMP" },
                ]) { Username = "SCOTT" },
            ]));

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) =>
            Task.FromResult(new SessionDetail(new SessionRow(target, "ACTIVE") { SqlId = "abc" })
            {
                CurrentSqlText = "UPDATE emp SET sal = 1 WHERE empno = 7369",
                Sections = [new DetailSection("잠금 객체", ["소유자", "객체", "유형", "잠금 모드"], [["SCOTT", "EMP", "TABLE", "Row-X (SX)"]])],
            });

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct)
        {
            Killed.Add((target, immediate));
            return Task.CompletedTask;
        }

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct)
        {
            LastTrace = options;
            return Task.FromResult(enable ? "/diag/free_ora_1.trc" : null);
        }

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) =>
            throw new NotSupportedException();
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
