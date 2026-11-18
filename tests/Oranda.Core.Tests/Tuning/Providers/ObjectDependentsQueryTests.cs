using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Tests.Catalog.Providers;
using Oranda.Core.Tuning;
using Oranda.Core.Tuning.Providers;
using Oranda.Testing;

namespace Oranda.Core.Tests.Tuning.Providers;

// 소유: TSK-15-09. 객체에 기대는 것들(tuning.object_dependents) 줄 읽기와 get_dependents 도구 정의.
public class ObjectDependentsQueryTests
{
    private readonly FakeCatalogPort _port = new();
    private static readonly FakeSession Session = new();

    [Fact]
    public async Task Dependents_bind_owner_and_name_and_read_the_four_kinds()
    {
        _port.Rows["tuning.object_dependents"] = FakeCatalogPort.Of(
            ["DEPENDENT", "APP", "V_ORDER", "VIEW", "VALID"],
            ["FK", "APP", "TB_ORDER_ITEM", "FK_ITEM_ORDER", "ENABLED"],
            ["TRIGGER", "APP", "TRG_ORDER_AUD", "INSERT OR UPDATE", "DISABLED"],
            ["SYNONYM", "PUBLIC", "TB_ORDER", null, null]);

        var rows = await new Fake(new FakeExecutionService(), _port).GetObjectDependentsAsync(Session, " APP ", "TB_ORDER", default);

        Assert.Equal(
            [
                new ObjectDependentRow(ObjectDependentKind.Dependent, "APP", "V_ORDER", "VIEW", "VALID"),
                new ObjectDependentRow(ObjectDependentKind.ForeignKey, "APP", "TB_ORDER_ITEM", "FK_ITEM_ORDER", "ENABLED"),
                new ObjectDependentRow(ObjectDependentKind.Trigger, "APP", "TRG_ORDER_AUD", "INSERT OR UPDATE", "DISABLED"),
                new ObjectDependentRow(ObjectDependentKind.Synonym, "PUBLIC", "TB_ORDER", null, null),
            ],
            rows);
        var call = Assert.Single(_port.Calls);
        Assert.Equal([("owner", (object?)"APP"), ("name", "TB_ORDER")], call.Binds.Select(b => (b.Name, b.Value)));
        Assert.True(call.Options.DictionaryPrefix);
    }

    [Fact]
    public async Task Unknown_kinds_and_rows_without_a_name_are_dropped_and_blank_details_become_null()
    {
        _port.Rows["tuning.object_dependents"] = FakeCatalogPort.Of(
            ["SOMETHING", "APP", "X", null, null],
            ["DEPENDENT", "APP", null, "VIEW", "VALID"],
            ["trigger", "APP", "TRG_A", "  ", " ENABLED "]);

        var rows = await new Fake(new FakeExecutionService(), _port).GetObjectDependentsAsync(Session, "APP", "TB_ORDER", default);

        Assert.Equal([new ObjectDependentRow(ObjectDependentKind.Trigger, "APP", "TRG_A", null, "ENABLED")], rows);
    }

    [Fact]
    public async Task Blank_owner_is_rejected_inside_the_port()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new Fake(new FakeExecutionService(), _port).GetObjectDependentsAsync(Session, " ", "TB_ORDER", default));
        Assert.Empty(_port.Calls);
    }

    [Fact]
    public void Get_dependents_is_a_metadata_level_tool_that_takes_one_object()
    {
        var tool = AgentTools.Find(AgentToolNames.GetDependents)!;

        Assert.Equal(AiAccessLevel.Metadata, tool.MinimumLevel);
        Assert.False(tool.RequiresExplainPlanSetting);
        Assert.Contains("\"required\": [\"object\"]", tool.InputSchemaJson, StringComparison.Ordinal);
    }

    private sealed class Fake(IExecutionService execution, FakeCatalogPort port)
        : CatalogTuningService(execution, null!, null!, statistics: null)
    {
        protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
            port.QueryAsync(id, binds, options);

        protected override bool IsNoAccess(Exception ex) => FakeCatalogPort.IsNoAccess(ex);

        protected override string? TraceUnavailableReason(ISession s) => null;

        protected override Task<long?> PreviousPlanHashAsync(ISession s, CancellationToken ct) => Task.FromResult<long?>(null);

        protected override Task BeginReadOnlyAsync(ISession s, CancellationToken ct) => Task.CompletedTask;
    }
}
