using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Core.Sessions;
using Oranda.Core.Tests.Catalog.Providers;
using Oranda.Testing;

namespace Oranda.Core.Tests.Monitoring.Providers;

// 소유: P0-b. 모니터 기반 클래스의 본문 흐름(가짜 질의 포트, DB 없음).
public class CatalogMonitorServiceTests
{
    private static readonly SessionKey Target = new(1, 12, 345);

    private readonly FakeCatalogPort _port = new();
    private readonly FakeExecutionService _exec = new();
    private static readonly FakeSession Monitor = new(purpose: SessionPurpose.Monitor);

    [Fact]
    public async Task Sessions_are_mapped_with_trimmed_binds_and_deltas_from_the_previous_call()
    {
        var service = new Service(_exec, _port);
        _port.Rows["session.list"] = FakeCatalogPort.Of(SessionRow(logicalReads: 10));
        _ = await service.GetSessionsAsync(Monitor, new SessionFilter { Username = "  scott ", Text = "   ", ActiveOnly = true }, default);
        _port.Rows["session.list"] = FakeCatalogPort.Of(SessionRow(logicalReads: 15));

        var row = Assert.Single(await service.GetSessionsAsync(Monitor, new SessionFilter(), default));

        Assert.Equal(Target, row.Key);
        Assert.Equal("SCOTT", row.Username);
        Assert.Equal(5, row.Deltas["session logical reads"]);
        Assert.Equal([("username", (object?)"scott"), ("active_only", 1), ("include_bg", 0), ("text", null)],
            _port.Calls[0].Binds.Select(b => (b.Name, b.Value)));
        Assert.True(_port.Calls[0].Options.DictionaryPrefix);
    }

    [Fact]
    public async Task Detail_assembles_seven_sections_turns_server_errors_into_one_line_and_leaves_unavailable_empty()
    {
        _port.Rows["session.detail"] = FakeCatalogPort.Of(SessionRow(logicalReads: 0));
        _port.Rows["session.locks"] = FakeCatalogPort.Of(["SCOTT", "EMP", "TABLE", "RX"]);
        _port.Errors["session.stats"] = new InvalidOperationException("ORA-00942: no table\nmore");

        var detail = await new Service(_exec, _port).GetSessionDetailAsync(Monitor, Target, default);

        Assert.Equal(["트랜잭션", "잠금 객체", "통계", "I/O", "대기 이력", "긴 작업", "열린 커서"], detail.Sections.Select(s => s.Title));
        Assert.Equal("EMP", detail.Sections[1].Rows[0][1]);
        Assert.Equal(["오류"], detail.Sections[2].Columns);
        Assert.Equal("ORA-00942: no table", detail.Sections[2].Rows[0][0]);
        Assert.Empty(detail.UnavailableSections);
        Assert.Null(detail.CurrentCursor);
    }

    [Fact]
    public async Task Section_without_a_source_is_not_queried_and_its_reason_is_recorded()
    {
        _port.Rows["session.detail"] = FakeCatalogPort.Of(SessionRow(logicalReads: 0));

        var detail = await new Service(_exec, _port) { MissingSection = "session.wait_history" }.GetSessionDetailAsync(Monitor, Target, default);

        Assert.DoesNotContain("session.wait_history", _port.Entered);
        var history = detail.Sections.Single(s => s.Title == "대기 이력");
        Assert.Empty(history.Rows);
        Assert.Equal("원본 없음", detail.UnavailableSections["대기 이력"]);
    }

    [Fact]
    public async Task Kill_goes_through_the_execution_service_and_missing_trace_statement_is_not_supported()
    {
        var service = new Service(_exec, _port);

        await service.KillAsync(Monitor, Target, immediate: true, default);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SetTraceAsync(Monitor, Target, true, new TraceOptions(), default));

        var kill = Assert.Single(_exec.Calls);
        Assert.Equal("KILL 12,345 NOW", kill.Statement.Text);
        Assert.Equal(HistorySource.Monitor, kill.Options.Source);
        Assert.Empty(_port.Entered);
    }

    [Fact]
    public void Kill_preview_is_the_statement_kill_sends_and_sends_nothing()
    {
        var service = new Service(_exec, _port);

        var preview = service.PreviewKill(Monitor, Target, immediate: true);

        Assert.Equal("KILL 12,345 NOW", preview.Text);
        Assert.Empty(_exec.Calls);
        Assert.Empty(_port.Entered);
    }

    [Fact]
    public async Task Lock_list_without_access_is_an_empty_snapshot_and_other_server_errors_still_throw()
    {
        var service = new Service(_exec, _port);
        _port.Errors["lock.list"] = new InvalidOperationException(FakeCatalogPort.NoAccessPrefix + " ORA-00942");

        var snapshot = await service.GetLocksAsync(Monitor, new LockFilter(), default);

        Assert.Empty(snapshot.Rows);
        Assert.Equal(["lock.list"], _port.Entered); // 블로킹 트리는 보내지 않는다
        _port.Errors["lock.idle_transactions"] = new InvalidOperationException("ORA-03113");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetIdleTransactionsAsync(Monitor, 60, default));
    }

    private static object?[] SessionRow(long logicalReads)
    {
        var r = new object?[28];
        r[0] = 1;
        r[1] = 12L;
        r[2] = 345L;
        r[3] = "ACTIVE";
        r[4] = "SCOTT";
        r[24] = logicalReads;
        return r;
    }

    private sealed class Service(IExecutionService execution, FakeCatalogPort port) : CatalogMonitorService(execution)
    {
        public string? MissingSection { get; init; }

        protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
            port.QueryAsync(id, binds, options);

        protected override bool IsServerError(Exception ex) => FakeCatalogPort.IsServerError(ex);

        protected override bool IsNoAccess(Exception ex) => FakeCatalogPort.IsNoAccess(ex);

        protected override bool HasQuery(ISession monitor, string id) => true;

        protected override SqlStatement KillStatement(ISession monitor, SessionKey target, bool immediate) =>
            new($"KILL {target.Sid},{target.Serial}{(immediate ? " NOW" : string.Empty)}", default, StatementKind.Other) { Verb = "KILL" };

        protected override string? LockedRowSelect(ISession monitor, string owner, string table) => null;

        protected override Task<(IReadOnlyList<string> Names, IReadOnlyList<string?>? Values)> ReadLockedRowValuesAsync(
            ISession monitor, string sql, string rowId, CancellationToken ct) => throw new NotSupportedException();

        protected override string? SectionUnavailableReason(ISession monitor, string id) => id == MissingSection ? "원본 없음" : null;
    }
}
