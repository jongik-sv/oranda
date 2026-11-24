using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Tests.Ai;

// 결정 ②(2026-11-24): 운영 접속의 set_binds·run_tab --binds-json·query --binds-json 감사에는 바인드 이름·형식만(값 없음). 개발·시험은 query 의 binds 와 같이 값까지.
public partial class AgentGateTests
{
    [Fact]
    public async Task Production_set_binds_and_run_tab_audit_only_names_and_types()
    {
        var fx = new ExternalFixture(ExtDev(), ExtPrd());
        fx.App(AppToolNames.SetBinds).Respond = _ => AgentToolOutput.Ok(new JsonObject { ["status"] = "set" }) with { ProfileId = ExtPrdId };
        fx.App(AppToolNames.RunTab).Respond = _ => AppToolOutputs.OkWithRows(new JsonObject { ["status"] = "succeeded" }, 1) with { ProfileId = ExtPrdId };

        await fx.Call(AppToolNames.SetBinds, """{"tab":"t1","binds":{"PAK_PST_DD":{"value":"20261101","type":"date"},"DEPT":10}}""");
        await fx.Call(AppToolNames.RunTab, """{"tab":"t1","binds":{":CODE":"A-77"}}""");

        var set = fx.Inner.Audit.Entries[^2];
        var run = fx.Inner.Audit.Entries[^1];
        Assert.Contains("binds={PAK_PST_DD:date,DEPT:number}", set.Arguments, StringComparison.Ordinal);
        Assert.Contains("binds={CODE:varchar2}", run.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("20261101", set.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("A-77", run.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_binds_audit_keeps_values_like_query_and_unknown_connections_drop_them()
    {
        var fx = new ExternalFixture(ExtDev(), ExtPrd());
        fx.App(AppToolNames.SetBinds).Respond = _ => AgentToolOutput.Ok(new JsonObject { ["status"] = "set" }) with { ProfileId = ExtDevId };
        await fx.Call(AppToolNames.SetBinds, """{"tab":"t1","binds":{"FROM_DT":"20261101"}}""");
        Assert.Contains("binds={\"FROM_DT\":\"20261101\"}", fx.Inner.Audit.Entries[^1].Arguments, StringComparison.Ordinal);

        // 접속을 모르는 호출(없는 탭·허가 밖 탭)은 운영일 수도 있으니 값을 남기지 않는다.
        fx.App(AppToolNames.SetBinds).Respond = _ => AppToolOutputs.TabNotFound("t9");
        await fx.Call(AppToolNames.SetBinds, """{"tab":"t9","binds":{"FROM_DT":"20261102"}}""");
        Assert.Contains("binds={FROM_DT:varchar2}", fx.Inner.Audit.Entries[^1].Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("20261102", fx.Inner.Audit.Entries[^1].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_query_binds_follow_the_same_names_only_policy()
    {
        var fx = new ExternalFixture(ExtPrd());

        await fx.Call(AppToolNames.Query, """{"sql":"SELECT 1 FROM TB_ORDER WHERE ID = :ID","binds":{"ID":10}}""");

        var audit = fx.Inner.Audit.Entries[^1];
        Assert.Equal(AppToolNames.Query, audit.Tool);
        Assert.Contains("binds={ID:number}", audit.Arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"A":"x","B":1.5,"C":true,"D":null,"E":{"value":"2026-11-01","type":"Date"},"F":{"value":3}}""", "{A:varchar2,B:number,C:boolean,D:null,E:date,F:number}")]
    [InlineData("""[1]""", "<invalid>")]
    public void Bind_names_and_types_never_carry_values(string json, string expected) =>
        Assert.Equal(expected, AgentGate.BindNamesAndTypes(JsonNode.Parse(json)));
}
