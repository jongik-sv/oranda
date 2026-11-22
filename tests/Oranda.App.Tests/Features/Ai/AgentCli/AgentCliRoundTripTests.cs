using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// 끝 왕복(A5 준비): 실제 <c>orandactl</c> 프로세스 → 연결 파일 → 실제 다리·관문·허가 저장소 → 가짜 처리자.
/// status·tools·tabs·call·stdin 본문·오류 코드의 종료 코드·끊은 뒤 거부를 본다. C·D·E 처리자가 머지되면 <see cref="AgentCliRig"/> 에 실제 처리자를 넣어 같은 흐름을 돌린다.
/// </summary>
[Collection(AgentCliSerial.Name)]
public sealed class AgentCliRoundTripTests
{
    private static RecordingAppHandler[] AppHandlers() =>
    [
        new(AppToolNames.GetStatus, inv => new JsonObject
        {
            ["label"] = inv.Grant.Label,
            ["clients"] = new JsonArray([.. inv.Grant.Clients.Select(c => (JsonNode)c)]),
            ["connections"] = new JsonArray([.. inv.Grant.Connections.Select(c => (JsonNode)new JsonObject { ["alias"] = c.Alias, ["level"] = c.Level.ToString() })]),
        }),
        new(AppToolNames.ListTabs, _ => new JsonObject
        {
            ["tabs"] = new JsonArray(new JsonObject { ["tab_id"] = "t1", ["title"] = "주문 조회", ["version"] = 7, ["locked"] = false }),
        }),
        new(AppToolNames.GetTab, inv => AppToolArgs.Text(inv.Arguments, AppFields.Tab) == "t1"
            ? AgentToolOutput.Ok(new JsonObject { ["tab_id"] = "t1", ["version"] = 7, ["text"] = "select 1 from dual" })
            : AppToolOutputs.TabNotFound(AppToolArgs.Text(inv.Arguments, AppFields.Tab))),
        new(AppToolNames.NewTab, inv => new JsonObject { ["tab_id"] = "t2", ["version"] = 1, ["echo"] = AppToolArgs.Text(inv.Arguments, AppFields.Text) }),
    ];

    [Fact]
    public async Task Status_tools_tabs_and_call_go_through_the_real_bridge()
    {
        var handlers = AppHandlers();
        await using var rig = new AgentCliRig(handlers, [new EchoDbHandler(AgentToolNames.DescribeObject)]);

        var status = await rig.RunAsync(["status"], client: "codex");
        Assert.True(status.ExitCode == 0, status.ToString());
        Assert.Equal("A5 rig", (string?)status.Json["label"]);
        Assert.Contains("codex", status.Json["clients"]!.AsArray().Select(n => (string?)n));

        var tools = await rig.RunAsync(["tools"]);
        Assert.True(tools.ExitCode == 0, tools.ToString());
        var names = tools.Json["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Contains(AppToolNames.ListTabs, names);
        Assert.Contains(AgentToolNames.DescribeObject, names);
        Assert.Contains(AppToolNames.Query, names);

        var tabs = await rig.RunAsync(["tabs"]);
        Assert.True(tabs.ExitCode == 0, tabs.ToString());
        Assert.Equal("주문 조회", (string?)tabs.Json["tabs"]![0]!["title"]); // 한글이 깨지지 않는다

        var call = await rig.RunAsync(["call", AgentToolNames.DescribeObject, "--json", """{"object":"scott.emp"}"""]);
        Assert.True(call.ExitCode == 0, call.ToString());
        Assert.Equal("SCOTT.EMP", (string?)call.Json["object"]); // 관문이 푼 이름
        Assert.Equal("dev-erp", (string?)call.Json["connection"]); // 접속이 하나라 생략해도 그 접속

        var audit = rig.Audit.Entries;
        Assert.Contains(audit, e => e.Tool == AppToolNames.ListTabs && e.Client == "a5-test" && e.RunId == rig.Issue.Grant.GrantId);
        Assert.Contains(audit, e => e.Tool == AgentToolNames.DescribeObject && e.RunId == rig.Issue.Grant.GrantId + ":dev-erp");
    }

    [Fact]
    public async Task Body_from_stdin_and_error_codes_become_exit_codes()
    {
        await using var rig = new AgentCliRig(AppHandlers());

        var created = await rig.RunAsync(["tab", "new", "--title", "AI 제안", "-"], stdin: "select '가' from dual");
        Assert.True(created.ExitCode == 0, created.ToString());
        Assert.Equal("select '가' from dual", (string?)created.Json["echo"]);

        var missing = await rig.RunAsync(["tab", "get", "--tab", "nope"]);
        Assert.Equal(AgentCliExit.ToolFailed, missing.ExitCode);
        Assert.Equal(AgentErrorCodes.TabNotFound, (string?)missing.Json["code"]);

        var unknown = await rig.RunAsync(["call", "drop_everything"]);
        Assert.Equal(AgentCliExit.Usage, unknown.ExitCode);
        Assert.Equal(AgentErrorCodes.UnknownTool, (string?)unknown.Json["code"]);

        var notYet = await rig.RunAsync(["call", AppToolNames.SaveTab, "--json", """{"tab":"t1"}"""]);
        Assert.Equal(AgentCliExit.ToolFailed, notYet.ExitCode);
        Assert.Equal(AgentErrorCodes.NotImplemented, (string?)notYet.Json["code"]);
    }

    [Fact]
    public async Task Disconnecting_deletes_the_file_and_the_cli_reports_unreachable()
    {
        await using var rig = new AgentCliRig(AppHandlers());
        Assert.True(File.Exists(AppDataLocation.EndpointFile(rig.DataDirectory, Environment.ProcessId)));

        rig.Grants.Revoke(rig.Issue.Grant.GrantId);

        var after = await rig.RunAsync(["tabs"]);
        Assert.Equal(AgentCliExit.Unreachable, after.ExitCode);
        Assert.Equal(AgentErrorCodes.NoEndpoint, (string?)after.Json["code"]);
    }

    /// <summary>앱 도구 가짜: 받은 호출을 남기고 정해진 결과를 돌려준다.</summary>
    private sealed class RecordingAppHandler(string toolName, Func<AppToolInvocation, object> respond) : IAppToolHandler
    {
        public string ToolName { get; } = toolName;

        public List<AppToolInvocation> Calls { get; } = [];

        public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add(invocation);
            }

            var result = respond(invocation);
            return ValueTask.FromResult(result as AgentToolOutput ?? AgentToolOutput.Ok((JsonNode)result));
        }
    }

    /// <summary>Db 도구 가짜: 관문이 정리한 인자를 그대로 돌려준다.</summary>
    private sealed class EchoDbHandler(string toolName) : IAgentToolHandler
    {
        public string ToolName { get; } = toolName;

        public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct) =>
            ValueTask.FromResult(AgentToolOutput.Ok(invocation.Arguments.DeepClone()));
    }
}

/// <summary>왕복 시험은 프로세스를 띄우므로 한 번에 하나씩 돈다(같은 pid 의 연결 파일 하나를 쓴다).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentCliSerial
{
    public const string Name = "agent-cli-round-trip";
}
