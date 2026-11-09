using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Tests.Catalog.Providers;
using Oranda.Core.Tuning;
using Oranda.Core.Tuning.Providers;
using Oranda.Testing;

namespace Oranda.Core.Tests.Tuning.Providers;

// 소유: P0-b. 튜닝 기반 클래스의 본문 흐름(가짜 질의 포트, DB 없음).
public class CatalogTuningServiceTests
{
    private readonly FakeCatalogPort _port = new();
    private static readonly FakeSession Session = new();

    [Fact]
    public async Task Argument_checks_inside_binds_run_after_the_port_is_entered()
    {
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetTableStatsAsync(Session, " ", "EMP", default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetChildCursorsAsync(Session, "", default));

        Assert.Equal(["tuning.table_stats", "tuning.child_cursors"], _port.Entered); // 옮기기 전 bind 람다 안 검사 순서 그대로
        Assert.Empty(_port.Calls);
    }

    [Fact]
    public async Task Table_stats_bind_dictionary_names_and_read_with_column_names_and_prefix()
    {
        _port.Rows["tuning.table_stats"] = FakeCatalogPort.Of(["SCOTT", "EMP", 14L, 1L, 2L, new DateTime(2026, 1, 2), "NO"]);

        var stats = await Service().GetTableStatsAsync(Session, " SCOTT ", "EMP", default);

        Assert.Equal(14, stats!.NumRows);
        var call = Assert.Single(_port.Calls);
        Assert.Equal([("owner", (object?)"SCOTT"), ("table_name", "EMP")], call.Binds.Select(b => (b.Name, b.Value)));
        Assert.True(call.Options.ColumnNames);
        Assert.True(call.Options.DictionaryPrefix);
    }

    [Fact]
    public async Task Plan_controls_without_access_are_empty_but_other_errors_throw()
    {
        var service = Service();
        _port.Errors["tuning.plan_controls"] = new InvalidOperationException(FakeCatalogPort.NoAccessPrefix + " ORA-01031");
        Assert.Empty(await service.GetSqlPlanControlsAsync(Session, "abc", default));

        _port.Errors["tuning.plan_controls"] = new InvalidOperationException("ORA-03113");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSqlPlanControlsAsync(Session, "abc", default));
    }

    [Fact]
    public async Task Trace_reason_hook_marks_the_file_unreadable_and_lines_are_not_queried()
    {
        _port.Rows["tuning.trace_file_self"] = FakeCatalogPort.Of(["/trace/a.trc"]);
        var service = Service(traceReason: "추적 파일 읽기 없음");

        var file = await service.GetTraceFileAsync(Session, default, default);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => service.ReadTraceLinesAsync(Session, file, default));

        Assert.Equal(new TraceFileInfo("/trace/a.trc", false, "추적 파일 읽기 없음"), file);
        Assert.Equal("추적 파일 읽기 없음", ex.Message);
        Assert.Equal(["tuning.trace_file_self"], _port.Entered);
    }

    private Fake Service(string? traceReason = null) => new(new FakeExecutionService(), _port, traceReason);

    private sealed class Fake(IExecutionService execution, FakeCatalogPort port, string? traceReason)
        : CatalogTuningService(execution, null!, null!, statistics: null)
    {
        protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
            port.QueryAsync(id, binds, options);

        protected override bool IsNoAccess(Exception ex) => FakeCatalogPort.IsNoAccess(ex);

        protected override string? TraceUnavailableReason(ISession s) => traceReason;

        protected override Task<long?> PreviousPlanHashAsync(ISession s, CancellationToken ct) => Task.FromResult<long?>(null);

        protected override Task BeginReadOnlyAsync(ISession s, CancellationToken ct) => Task.CompletedTask;
    }
}
