using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Tests.Ai;

// G5: Host Db 처리자가 낸 행 수(query·find_objects)가 개발 접속에서도 감사 사유 rows=N 으로 남는다(운영 스위치와 무관).
public partial class AgentGateTests
{
    [Fact]
    public async Task Development_query_row_count_goes_into_the_audit_reason()
    {
        var fx = new ExternalFixture(ExtDev());
        fx.Handlers[AppToolNames.Query].Respond = _ => AgentToolOutput.Ok(QueryRows()) with { RowCount = 2 };

        var response = await fx.Call(AppToolNames.Query, """{"sql":"SELECT ID FROM TB_ORDER"}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        Assert.Equal("rows=2", fx.Inner.Audit.Entries[^1].Reason);
    }

    [Fact]
    public async Task Db_list_tool_row_count_goes_into_the_audit_reason_and_tools_without_one_add_nothing()
    {
        var fx = new ExternalFixture(ExtDev());
        fx.Handlers[AppToolNames.FindObjects].Respond = _ => AgentToolOutput.Ok(new JsonObject { ["objects"] = new JsonArray(), ["truncated"] = false }) with { RowCount = 0 };
        fx.Handlers[AppToolNames.GetDdl].Respond = _ => AgentToolOutput.Ok(new JsonObject { ["sql_text"] = "-- ddl" });

        var found = await fx.Call(AppToolNames.FindObjects, """{"name_like":"TB%"}""");
        var foundReason = fx.Inner.Audit.Entries[^1].Reason;
        var ddl = await fx.Call(AppToolNames.GetDdl, """{"object":"TB_ORDER"}""");

        Assert.False(found.Result.IsError, found.Result.Text);
        Assert.Equal("rows=0", foundReason);
        Assert.False(ddl.Result.IsError, ddl.Result.Text);
        Assert.DoesNotContain("rows=", fx.Inner.Audit.Entries[^1].Reason ?? string.Empty, StringComparison.Ordinal);
    }
}
