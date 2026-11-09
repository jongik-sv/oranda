using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Plans.Providers;
using Oranda.Core.Sessions;
using Oranda.Core.Tests.Catalog.Providers;
using Oranda.Testing;

namespace Oranda.Core.Tests.Plans.Providers;

// 소유: P0-b. 실행 계획 기반 클래스의 본문 흐름(가짜 질의 포트, DB 없음).
public class CatalogExecutionPlanServiceTests
{
    private readonly FakeCatalogPort _port = new();
    private readonly FakeExecutionService _exec = new();
    private static readonly FakeSession Session = new();

    [Fact]
    public async Task Explain_reads_plan_table_by_column_name_and_normalizes_steps_before_risk()
    {
        _port.Rows["plan.explain_steps"] = new CatalogRows(
            ["ID", "PARENT_ID", "OPERATION", "OPTIONS"],
            [[0, null, "SELECT STATEMENT", null], [1, 0, "TABLE ACCESS FULL", null]]);
        _port.Rows["plan.explain_text"] = FakeCatalogPort.Of(["Plan hash value: 123"], ["line"]);

        var plan = await new Service(_exec, _port, normalize: true)
            .ExplainAsync(Session, new SqlStatement("SELECT * FROM t", default, StatementKind.Query), default);

        var explain = Assert.Single(_exec.Calls);
        Assert.StartsWith("EXPLAIN PLAN SET STATEMENT_ID = 'ORANDA_", explain.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(["plan.explain_steps", "plan.explain_text"], _port.Calls.Select(c => c.Id));
        Assert.True(_port.Calls[0].Options.ColumnNames);
        Assert.True(_port.Calls[0].Options.ReadLong);
        Assert.False(_port.Calls[1].Options.ColumnNames);
        Assert.Equal(["stmt_id", "format"], _port.BindsOf("plan.explain_text").Select(b => b.Name));

        Assert.Equal(PlanRisk.FullScan, plan.Steps[1].Risk);
        Assert.Equal(("TABLE ACCESS", "FULL"), (plan.Steps[1].Operation, plan.Steps[1].Options));
        Assert.Equal(1, plan.Steps[1].Depth);
        Assert.Null(plan.PlanHashValue); // ExplainPlanHash 기본 = 모름
    }

    [Fact]
    public async Task Without_normalize_hook_an_operation_holding_the_method_is_not_flagged()
    {
        _port.Rows["plan.explain_steps"] = new CatalogRows(["ID", "OPERATION"], [[1, "TABLE ACCESS FULL"]]);

        var plan = await new Service(_exec, _port, normalize: false)
            .ExplainAsync(Session, new SqlStatement("SELECT * FROM t", default, StatementKind.Query), default);

        Assert.Equal(PlanRisk.None, Assert.Single(plan.Steps).Risk);
    }

    [Fact]
    public async Task Cursor_plan_keeps_the_first_child_normalizes_steps_and_reads_hash_column()
    {
        _port.Rows["plan.cursor_steps"] = FakeCatalogPort.Of(
            CursorRow(child: 2, id: 0, op: "SELECT STATEMENT", hash: 777),
            CursorRow(child: 2, id: 1, op: "TABLE ACCESS FULL", hash: 777),
            CursorRow(child: 3, id: 0, op: "OTHER CHILD", hash: 888));

        var plan = await new Service(_exec, _port, normalize: true)
            .GetCursorPlanAsync(Session, new CursorRef("abc"), "  ", default);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(PlanRisk.FullScan, plan.Steps[1].Risk);
        Assert.Equal(777, plan.PlanHashValue);
        Assert.Equal(new CursorRef("abc", 2, 1), plan.Cursor);
        Assert.Equal([("sql_id", (object?)"abc"), ("child", 2), ("format", "TYPICAL")],
            _port.BindsOf("plan.cursor_text").Select(b => (b.Name, b.Value)));
        Assert.Null(_port.BindsOf("plan.cursor_steps").Single(b => b.Name == "inst_id").Value);
    }

    [Fact]
    public async Task Monitor_report_picks_the_html_query_and_last_cursor_goes_through_the_hook()
    {
        _port.Rows["plan.monitor_report_html"] = FakeCatalogPort.Of(["<html/>"]);
        var service = new Service(_exec, _port, normalize: false);

        Assert.Equal("<html/>", await service.GetSqlMonitorReportAsync(Session, new CursorRef("abc"), html: true, default));
        Assert.Equal(string.Empty, await service.GetSqlMonitorReportAsync(Session, new CursorRef("abc"), html: false, default));
        Assert.Equal(new CursorRef("last", 4), await service.GetLastCursorAsync(Session, default));
    }

    private static object?[] CursorRow(int child, int id, string op, long hash) =>
        [1, child, id, id == 0 ? null : 0, null, op, null, null, null, null, null, null, null, null, null, null, hash];

    private sealed class Service(IExecutionService execution, FakeCatalogPort port, bool normalize) : CatalogExecutionPlanService(execution)
    {
        protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
            port.QueryAsync(id, binds, options);

        protected override Task<CursorRef?> ReadLastCursorAsync(ISession s, CancellationToken ct) => Task.FromResult<CursorRef?>(new CursorRef("last", 4));

        // Tibero 모양: OPERATION 에 방식까지(「TABLE ACCESS FULL」), OPTIONS NULL.
        protected override RawPlanStep NormalizeStep(RawPlanStep raw) =>
            normalize && raw.Options is null && raw.Operation.EndsWith(" FULL", StringComparison.Ordinal)
                ? raw with { Operation = raw.Operation[..^5], Options = "FULL" }
                : raw;
    }
}
