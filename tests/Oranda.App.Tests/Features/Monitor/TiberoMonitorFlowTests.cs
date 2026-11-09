using Oranda.App.Features;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Tests.Features.LockMonitor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.TiberoMonitor;

/// <summary>
/// X5: 모니터 창을 여는 명령 3개는 「지금 탭의 DBMS」로 막지 않는다. 기본 대상이 모니터 구현 없는 DBMS(Tibero)면 창이 상태줄에 이유를 보이고,
/// 「다른 접속…」으로 Oracle 프로필을 고르면 열린다. 카탈로그 문장이 없는 세션의 종료 요청은 null + 이유(U5)다.
/// </summary>
public class TiberoMonitorFlowTests
{
    private const string Reason = "Tibero 프로필은 아직 모니터 서비스가 없어 열 수 없습니다.";

    private static readonly ConnectionProfile TiberoProfile = new("tb", "SYS", "h:8629/s", Id: Guid.NewGuid(), Dbms: Dbms.Tibero);

    private static readonly ConnectionProfile OracleProfile = new("ora", "SYSTEM", "h:1521/s", Id: Guid.NewGuid());

    private static FakeLockSession OracleSession() => new(LockHarness.Server(LockHarness.Dba), OracleProfile);

    private static FakeLockSession TiberoSession() => new(LockHarness.Server(LockHarness.Dba) with { Dbms = Dbms.Tibero }, TiberoProfile);

    private static string? EnablementOf(string id)
    {
        var registration = ShellTestHelpers.AppContributions.Commands.Single(c => c.Definition.Id == id);
        return registration.Definition.Enablement;
    }

    // ---- 1. 명령 속성 ----

    [Theory]
    [InlineData("oranda.monitor.sessions")]
    [InlineData(LockMonitorCommands.Open)]
    [InlineData("oranda.monitor.sql")]
    public void Window_opening_commands_are_not_gated_by_the_active_tabs_dbms(string id)
    {
        Assert.DoesNotContain("noImplMonitor", EnablementOf(id) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_monitor_family_commands_keep_the_no_impl_monitor_gate_with_their_own_gates()
    {
        Assert.Contains("noImplMonitor", EnablementOf("oranda.tuning.planControls"), StringComparison.Ordinal);
        Assert.Contains("noImplMonitor", EnablementOf(SqlMonitorViewModel.RecommendCommand), StringComparison.Ordinal);

        var ash = EnablementOf("oranda.tuning.ashWaits");
        Assert.Contains("noImplMonitor", ash, StringComparison.Ordinal);
        Assert.Contains("unsupportedAsh", ash, StringComparison.Ordinal);

        var awr = EnablementOf("oranda.tuning.awrHistory");
        Assert.Contains("noImplMonitor", awr, StringComparison.Ordinal);
        Assert.Contains("unsupportedAwrReport", awr, StringComparison.Ordinal);
    }

    // ---- 2~3. 기본 대상이 Tibero → 상태줄에 이유, 「다른 접속…」으로 Oracle 을 고르면 열림 ----

    [Fact]
    public async Task Session_monitor_reports_the_reason_then_opens_the_chosen_oracle_session()
    {
        var oracle = OracleSession();
        var vm = new SessionMonitorViewModel(new FlowMonitor(), _ => throw new InvalidOperationException(Reason))
        {
            PostToUi = a => a(),
            ChooseSession = _ => Task.FromResult<ISession?>(oracle),
        };

        await vm.StartAsync();

        Assert.False(vm.IsOpen);
        Assert.Null(vm.MonitorSession);
        Assert.Equal("모니터 세션을 열지 못했습니다: " + Reason, vm.StatusText);

        await vm.OpenWithOtherAccountAsync();

        Assert.True(vm.IsOpen);
        Assert.Same(oracle, vm.MonitorSession);
        Assert.Single(vm.Sessions);
    }

    [Fact]
    public async Task Lock_monitor_reports_the_reason_then_opens_the_chosen_oracle_session()
    {
        var h = new LockHarness();
        var oracle = OracleSession();
        h.Vm.OpenSession = _ => throw new InvalidOperationException(Reason);
        h.Vm.ChooseSession = _ => Task.FromResult<ISession?>(oracle);

        await h.Vm.StartAsync();

        Assert.False(h.Vm.IsOpen);
        Assert.Equal(LockViewState.NoConnection, h.Vm.State);
        Assert.Equal("모니터 세션을 열지 못했습니다: " + Reason, h.Vm.StatusText);
        Assert.Equal(0, h.Monitor.LockCalls);

        await h.Vm.OpenWithOtherAccountAsync();

        Assert.True(h.Vm.IsOpen);
        Assert.Same(oracle, h.Vm.MonitorSession);
        Assert.Equal(LockViewState.Data, h.Vm.State);
        Assert.Equal(1, h.Monitor.LockCalls);
    }

    [Fact]
    public async Task Sql_monitor_reports_the_reason_then_opens_the_chosen_oracle_session()
    {
        var oracle = OracleSession();
        var vm = new SqlMonitorViewModel(new FlowMonitor(), null, _ => throw new InvalidOperationException(Reason))
        {
            PostToUi = a => a(),
            ChooseSession = _ => Task.FromResult<ISession?>(oracle),
        };

        await vm.StartAsync();

        Assert.False(vm.IsOpen);
        Assert.Null(vm.MonitorSession);
        Assert.Equal("모니터 세션을 열지 못했습니다: " + Reason, vm.StatusText);

        await vm.OpenWithOtherAccountAsync();

        Assert.True(vm.IsOpen);
        Assert.Same(oracle, vm.MonitorSession);
        Assert.Single(vm.Results);
    }

    // ---- 4. U5: 카탈로그 문장이 없는 세션의 종료 요청 ----

    [Fact]
    public async Task Kill_request_without_a_catalog_statement_is_null_with_the_reason_and_nothing_is_confirmed_or_run()
    {
        var monitor = new FakeLockMonitor();
        var tibero = TiberoSession();
        var confirms = 0;
        var vm = new LockMonitorViewModel(new Services(new DbmsServiceTable(), monitor))
        {
            PostToUi = a => a(),
            StartAutoRefresh = false,
            OpenSession = _ => Task.FromResult<ISession?>(tibero),
            ConfirmKill = _ =>
            {
                confirms++;
                return Task.FromResult(true);
            },
        };
        await vm.StartAsync();
        Assert.True(vm.IsOpen);
        vm.SelectedKey = FakeLockMonitor.H1;
        Assert.True(vm.CanKill);
        var expected = CatalogRouting.Unavailable(tibero);
        Assert.Contains("Tibero", expected, StringComparison.Ordinal);

        Assert.Null(vm.BuildKillRequest(disconnect: false));
        Assert.Equal(expected, vm.StatusText);

        vm.StatusText = string.Empty;
        Assert.Null(vm.BuildKillRequest(disconnect: true));
        Assert.Equal(expected, vm.StatusText);

        vm.StatusText = string.Empty;
        await vm.KillAsync();
        await vm.DisconnectAsync();
        Assert.Equal(expected, vm.StatusText);
        Assert.Equal(0, confirms);
        Assert.Empty(monitor.Killed);
    }

    [Fact]
    public async Task Kill_request_uses_the_service_preview_statement_and_shows_the_corrected_retype_name()
    {
        var monitor = new FakeLockMonitor
        {
            Preview = k => new SqlStatement($"ALTER SYSTEM KILL SESSION ({k.Sid}, 2156873394)", default, StatementKind.Other)
            {
                Verb = "ALTER SYSTEM",
                DangerousTarget = $"{k.Sid},2156873394",
            },
        };
        var tibero = TiberoSession();
        var vm = new LockMonitorViewModel(new Services(new DbmsServiceTable(), monitor))
        {
            PostToUi = a => a(),
            StartAutoRefresh = false,
            OpenSession = _ => Task.FromResult<ISession?>(tibero),
        };
        await vm.StartAsync();
        vm.SelectedKey = FakeLockMonitor.H1;

        var request = vm.BuildKillRequest(disconnect: false)!;

        Assert.Equal("ALTER SYSTEM KILL SESSION (1287, 2156873394)", request.Statement);
        var note = Assert.Single(request.Facts, f => f.Key == "재입력 이름").Value;
        Assert.StartsWith("1287,2156873394", note, StringComparison.Ordinal);
        Assert.Contains("40211", note, StringComparison.Ordinal); // 목록에 보이던 SERIAL#

        // 이름이 격자와 같으면(Oracle 포함) 줄을 더하지 않는다.
        Assert.Null(LockText.KillTargetNote(FakeLockMonitor.H1, "1287,40211"));
        Assert.Null(LockText.KillTargetNote(FakeLockMonitor.H1, "1287,40211,@1"));
        Assert.Null(LockText.KillTargetNote(FakeLockMonitor.H1, null));
    }

    [Fact]
    public void Session_monitor_disconnect_statement_is_null_without_a_catalog_statement()
    {
        var tibero = TiberoSession();
        var key = new SessionKey(1, 7, 8);

        Assert.Null(SessionMonitorCommands.DisconnectStatement(new Services(new DbmsServiceTable(), null), tibero, key, true, false));
        Assert.Null(LockText.KillStatement(new Services(new DbmsServiceTable(), null), tibero, key, rac: false));
    }

    // ---- 가짜들 ----

    private sealed class Services(IDbmsServiceSelector selector, IMonitorService? monitor) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IDbmsServiceSelector))
            {
                return selector;
            }

            return serviceType == typeof(IMonitorService) ? monitor : null;
        }
    }

    private sealed class FlowMonitor : IMonitorService
    {
        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct)
        {
            IReadOnlyList<SessionRow> rows = [new(new SessionKey(1, 10, 100), "ACTIVE") { Username = "SCOTT" }];
            return Task.FromResult(rows);
        }

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => Task.FromResult(new BlockingTree([]));

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
        {
            IReadOnlyList<TopSqlRow> rows = [new(new CursorRef("sql1", 0, 1), "select 1 from dual") { Executions = 3, ElapsedSeconds = 2.5 }];
            return Task.FromResult(rows);
        }

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) => throw new NotSupportedException();

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) => throw new NotSupportedException();
    }
}
