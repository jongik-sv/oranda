using System.Reflection;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Tibero.Monitoring;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Monitoring;

/// <summary>
/// Tibero 모니터 서비스 단위 시험(서버 없음). Kill 문장·SERIAL# 교정·「볼 수 없음」 규칙·없는 구획 이유를 정적 도우미로 확인한다.
/// 가짜 드라이버 처리기가 정적이라 FakeDriver 컬렉션에 둔다(병렬 금지).
/// </summary>
[Collection("FakeDriver")]
public sealed class TiberoMonitorServiceTests : IDisposable
{
    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    public TiberoMonitorServiceTests() => FakeDriverHost.Reset();

    public void Dispose() => FakeDriverHost.Reset();

    [Fact]
    public void Service_is_registered_for_tibero_as_the_monitor_service()
    {
        var attribute = Assert.Single(typeof(TiberoMonitorService).GetCustomAttributes<OrandaServiceAttribute>(inherit: false));

        Assert.Equal(typeof(IMonitorService), attribute.ServiceType);
        Assert.Equal((Dbms?)Dbms.Tibero, attribute.ForDbms);
        Assert.True(typeof(IMonitorService).IsAssignableFrom(typeof(TiberoMonitorService)));
        Assert.True(typeof(CatalogMonitorService).IsAssignableFrom(typeof(TiberoMonitorService)));
    }

    [Fact]
    public void Kill_statement_is_alter_system_kill_session_with_sid_and_serial()
    {
        var stmt = TiberoMonitorService.KillStatement(new SessionKey(1, 130, 4711));

        Assert.Equal("ALTER SYSTEM KILL SESSION (130, 4711)", stmt.Text);
        Assert.Equal(StatementKind.Other, stmt.Kind);
        Assert.Equal("ALTER SYSTEM", stmt.Verb);
        Assert.Equal("130,4711", stmt.DangerousTarget);
    }

    [Fact]
    public void Kill_statement_corrects_a_negative_serial()
    {
        var stmt = TiberoMonitorService.KillStatement(new SessionKey(1, 130, -2138093902));

        Assert.Equal("ALTER SYSTEM KILL SESSION (130, 2156873394)", stmt.Text);
        Assert.Equal("130,2156873394", stmt.DangerousTarget);
    }

    [Fact]
    public void Kill_statement_ignores_the_instance_id()
    {
        var one = TiberoMonitorService.KillStatement(new SessionKey(1, 5, 6));
        var two = TiberoMonitorService.KillStatement(new SessionKey(2, 5, 6));

        Assert.Equal(one.Text, two.Text);
        Assert.DoesNotContain("@", one.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Kill_statement_is_the_same_whether_immediate_or_not()
    {
        var service = new TiberoMonitorService(null!);
        var method = typeof(TiberoMonitorService).GetMethod("KillStatement",
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Oranda.Core.Sessions.ISession), typeof(SessionKey), typeof(bool)])!;
        var target = new SessionKey(1, 9, -1);

        var plain = (SqlStatement)method.Invoke(service, [null, target, false])!;
        var immediate = (SqlStatement)method.Invoke(service, [null, target, true])!;

        Assert.Equal(plain, immediate);
        Assert.Equal("ALTER SYSTEM KILL SESSION (9, 4294967295)", immediate.Text);
    }

    [Fact]
    public void Kill_preview_shows_the_corrected_statement_and_sends_nothing()
    {
        var exec = new Oranda.Testing.FakeExecutionService();
        var service = new TiberoMonitorService(exec);
        var monitor = new Oranda.Testing.FakeSession(Oranda.Testing.TestData.Profile() with { Dbms = Dbms.Tibero });

        var preview = service.PreviewKill(monitor, new SessionKey(0, 130, -2138093902), immediate: true);

        Assert.Equal("ALTER SYSTEM KILL SESSION (130, 2156873394)", preview.Text);
        Assert.Equal("130,2156873394", preview.DangerousTarget);
        Assert.Empty(exec.Calls);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(-1L, 4294967295L)]
    [InlineData(int.MinValue, 2147483648L)]
    [InlineData(-2138093902L, 2156873394L)]
    public void Kill_serial_adds_2_pow_32_to_negatives_only(long shown, long expected) =>
        Assert.Equal(expected, TiberoMonitorService.KillSerial(shown));

    [Fact]
    public void Missing_locked_row_query_counts_as_no_access()
    {
        Assert.True(TiberoMonitorService.IsNoAccessError(new CatalogQueryNotFoundException("lock.locked_row", 6, Dbms.Tibero)));
    }

    [Fact]
    public void Other_missing_catalog_query_is_not_swallowed()
    {
        Assert.False(TiberoMonitorService.IsNoAccessError(new CatalogQueryNotFoundException("lock.list", 6, Dbms.Tibero)));
    }

    [Fact]
    public void Unrelated_exception_is_not_no_access()
    {
        Assert.False(TiberoMonitorService.IsNoAccessError(new InvalidOperationException("x")));
        Assert.False(TiberoMonitorService.IsNoAccessError(new NotSupportedException()));
    }

    [Fact]
    public void Missing_object_server_error_is_no_access()
    {
        Assert.True(TiberoMonitorService.IsNoAccessError(FakeDriverHost.ServerError(-8033, "TBR-8033: not found")));
    }

    [Fact]
    public void Other_server_error_is_not_no_access()
    {
        Assert.False(TiberoMonitorService.IsNoAccessError(FakeDriverHost.ServerError(-12040, "TBR-12040: cancelled")));
    }

    [Fact]
    public void Wait_history_has_no_catalog_query_and_gets_its_own_reason()
    {
        Assert.False(TiberoMonitorService.CatalogHas(Server, "session.wait_history"));
        Assert.Equal(TiberoMonitorService.WaitHistoryUnavailable, TiberoMonitorService.UnavailableReason(Server, "session.wait_history"));
    }

    [Fact]
    public void Another_missing_query_gets_the_generic_reason()
    {
        Assert.Equal(TiberoMonitorService.SectionViewMissing, TiberoMonitorService.UnavailableReason(Server, "session.no_such_view"));
    }

    [Theory]
    [InlineData("session.stats")]
    [InlineData("session.io")]
    [InlineData("session.transaction")]
    [InlineData("session.locks")]
    [InlineData("session.longops")]
    [InlineData("session.open_cursors")]
    [InlineData("session.plan")]
    public void Existing_queries_have_no_unavailable_reason(string id)
    {
        Assert.True(TiberoMonitorService.CatalogHas(Server, id));
        Assert.Null(TiberoMonitorService.UnavailableReason(Server, id));
    }

    [Fact]
    public void There_is_no_locked_row_or_deadlock_query()
    {
        Assert.False(TiberoMonitorService.CatalogHas(Server, "lock.locked_row"));
        Assert.False(TiberoMonitorService.CatalogHas(Server, "lock.deadlocks"));
    }

    [Fact]
    public async Task Locked_row_values_fail_as_a_faulted_task_with_the_reason()
    {
        var service = new TiberoMonitorService(null!);
        var method = typeof(TiberoMonitorService).GetMethod("ReadLockedRowValuesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var task = (Task)method.Invoke(service, [null, "SELECT 1", "AAA", CancellationToken.None])!;

        Assert.True(task.IsFaulted); // 동기로 던지지 않고 실패한 작업으로 돌려준다
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => task);
        Assert.Equal(TiberoMonitorService.LockedRowUnavailable, ex.Message);
    }

    [Fact]
    public void Trace_statement_is_not_overridden_so_trace_is_not_supported()
    {
        // 기본 본문의 TraceStatement 는 null → SetTraceAsync 가 NotSupportedException(실서버 확인은 IT).
        var trace = typeof(TiberoMonitorService).GetMethod("TraceStatement", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Equal(typeof(CatalogMonitorService), trace!.DeclaringType);
    }
}
