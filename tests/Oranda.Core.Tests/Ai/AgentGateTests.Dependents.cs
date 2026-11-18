using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Tests.Ai;

// 소유: TSK-15-09. 실행 전 검토의 get_dependents: 문장 없이 「보낼 범위」 에 넣은 바꾸는 객체만 볼 수 있고, 메타데이터 수준이면 된다.
public partial class AgentGateTests
{
    [Fact]
    public async Task Get_dependents_sees_only_the_objects_in_the_send_scope_at_metadata_level()
    {
        var fx = new GateFixture();
        var session = Session(Dev(AiAccessLevel.Metadata), AiAccessLevel.Metadata, statement: null, send: new AgentSendScope(false, ["APP.TB_ORDER"]));

        var ok = await Call(fx, session, AgentToolNames.GetDependents, """{"object":"APP.TB_ORDER"}""");
        Assert.False(ok.Result.IsError);
        Assert.Equal("APP.TB_ORDER", Assert.Single(fx.Handlers[AgentToolNames.GetDependents].Calls).Resolved["object"].Key);

        AssertDenied(await Call(fx, session, AgentToolNames.GetDependents, """{"object":"APP.TB_CUST"}"""), "scope");
        AssertDenied(await Call(fx, session, AgentToolNames.GetTableStats, """{"table":"APP.TB_ORDER"}"""), "level"); // 크기는 계획 수준부터
    }
}
