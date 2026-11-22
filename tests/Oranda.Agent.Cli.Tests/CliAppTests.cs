using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Agent.Cli.Tests;

/// <summary>B2·B4: 명령 → 가짜 앱 호출 → stdout JSON·종료 코드(계약 §4).</summary>
public sealed class CliAppTests : IDisposable
{
    private readonly TempDataDir _dir = new();
    private readonly CliHarness _cli;

    public CliAppTests()
    {
        _cli = new CliHarness(_dir.Path);
        _dir.Write(100);
        _cli.AlivePids.Add(100);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Tool_result_is_one_json_line_with_korean_unescaped_and_exit_zero()
    {
        _cli.Bridge.Respond = (_, _) => new AgentCallToolResult(false, """{"tabs":[{"tab_id":"t1","title":"주문 조회"}]}""");

        var exit = await _cli.RunAsync("tabs");

        Assert.Equal(0, exit);
        var output = _cli.OutJson();
        Assert.Equal("주문 조회", (string?)output["tabs"]![0]!["title"]);
        Assert.Contains("주문 조회", _cli.Out.ToString(), StringComparison.Ordinal);
        var call = Assert.Single(_cli.Bridge.Calls);
        Assert.Equal(AppToolNames.ListTabs, call.Name);
        Assert.Equal(new BridgeTarget("/tmp/agent-100.sock", "token-100", External: true), Assert.Single(_cli.Targets));
    }

    [Fact]
    public async Task Structured_result_wins_over_text()
    {
        _cli.Bridge.Respond = (_, _) => new AgentCallToolResult(false, "ignored", new JsonObject { ["ok"] = 1 });

        await _cli.RunAsync("status");

        Assert.Equal(1, (int)_cli.OutJson()["ok"]!);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("stale")]
    public async Task Rejected_or_stale_proposal_is_success(string status)
    {
        _cli.Bridge.Respond = (_, _) => new AgentCallToolResult(false, $$"""{"proposal_id":"p1","status":"{{status}}"}""");
        _cli.Stdin = "SELECT 2 FROM dual";

        var exit = await _cli.RunAsync("tab", "propose", "--tab", "t1", "--base-version", "3", "-");

        Assert.Equal(0, exit);
        Assert.Equal(status, (string?)_cli.OutJson()["status"]);
        Assert.Equal("SELECT 2 FROM dual", (string?)_cli.Bridge.Calls[0].Arguments!["new_text"]);
    }

    [Theory]
    [InlineData(AgentErrorCodes.Level, AgentCliExit.Denied)]
    [InlineData(AgentErrorCodes.Locked, AgentCliExit.Denied)]
    [InlineData(AgentErrorCodes.TabNotFound, AgentCliExit.ToolFailed)]
    [InlineData(AgentErrorCodes.InvalidArguments, AgentCliExit.Usage)]
    [InlineData(AgentErrorCodes.RunRevoked, AgentCliExit.Unreachable)]
    [InlineData("something_new", AgentCliExit.ToolFailed)]
    public async Task Tool_error_code_sets_the_exit_code_and_the_error_json_is_kept(string code, int exit)
    {
        _cli.Bridge.Respond = (_, _) => new AgentCallToolResult(true, $$"""{"error":"no","code":"{{code}}"}""");

        Assert.Equal(exit, await _cli.RunAsync("tab", "get"));
        Assert.Equal(code, (string?)_cli.OutJson()["code"]);
    }

    [Fact]
    public async Task Tool_error_without_json_is_a_tool_failure()
    {
        _cli.Bridge.Respond = (_, _) => new AgentCallToolResult(true, "plain failure");

        Assert.Equal(AgentCliExit.ToolFailed, await _cli.RunAsync("tab", "get"));
        Assert.Equal("plain failure", (string?)_cli.OutJson()["text"]);
    }

    [Fact]
    public async Task App_not_running_gives_exit_three_with_error_json()
    {
        _cli.Bridge.Throw = new BridgeException(AgentErrorCodes.NotRunning, CliText.NotRunning);

        Assert.Equal(AgentCliExit.Unreachable, await _cli.RunAsync("tabs"));
        var error = _cli.OutJson();
        Assert.Equal(AgentErrorCodes.NotRunning, (string?)error["code"]);
        Assert.Equal(CliText.NotRunning, (string?)error["error"]);
    }

    [Fact]
    public async Task Two_windows_give_exit_five_with_candidates_and_no_call()
    {
        _dir.Write(200, "둘째 창");
        _cli.AlivePids.Add(200);

        Assert.Equal(AgentCliExit.InstanceChoice, await _cli.RunAsync("tabs"));
        var error = _cli.OutJson();
        Assert.Equal(AgentErrorCodes.MultipleInstances, (string?)error["code"]);
        Assert.Equal(2, error["instances"]!.AsArray().Count);
        Assert.Empty(_cli.Bridge.Calls);

        Assert.Equal(0, await new CliHarnessRun(_cli).RunAsync("--instance", "둘째 창", "tabs"));
        Assert.Equal("token-200", _cli.Targets[^1].Token);
    }

    [Fact]
    public async Task Usage_error_gives_exit_two_and_a_hint_on_stderr()
    {
        Assert.Equal(AgentCliExit.Usage, await _cli.RunAsync("tab", "get", "--bogus"));

        Assert.Equal(AgentErrorCodes.Usage, (string?)_cli.OutJson()["code"]);
        Assert.Contains("orandactl help", _cli.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_cli.Targets);
    }

    [Fact]
    public async Task Text_mode_writes_readable_text_and_errors_to_stderr()
    {
        _cli.Bridge.Respond = (_, _) => new AgentCallToolResult(false,
            """{"tab_id":"t1","version":7,"text":"SELECT 1\nFROM dual","tabs":[{"id":"a","dirty":true},{"id":"bb","dirty":false}]}""");

        Assert.Equal(0, await _cli.RunAsync("tab", "get", "--text"));
        var text = _cli.Out.ToString();
        Assert.Contains("version: 7\n", text, StringComparison.Ordinal);
        Assert.Contains("text:\nSELECT 1\nFROM dual\n", text, StringComparison.Ordinal);
        Assert.Contains("  id  dirty\n  a   true\n  bb  false\n", text, StringComparison.Ordinal);

        _cli.Bridge.Respond = (_, _) => new AgentCallToolResult(true, """{"error":"denied here","code":"level"}""");
        _cli.Out.GetStringBuilder().Clear();
        Assert.Equal(AgentCliExit.Denied, await _cli.RunAsync("tab", "get", "--text"));
        Assert.Equal(string.Empty, _cli.Out.ToString());
        Assert.Contains("orandactl: level: denied here", _cli.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_lists_names_descriptions_and_schemas()
    {
        _cli.Bridge.Tools = [new AgentToolInfo("get_status", "State.", JsonNode.Parse("""{"type":"object"}""")!)];

        Assert.Equal(0, await _cli.RunAsync("tools"));

        var tool = _cli.OutJson()["tools"]![0]!;
        Assert.Equal("get_status", (string?)tool["name"]);
        Assert.Equal("object", (string?)tool["input_schema"]!["type"]);
    }

    [Fact]
    public async Task Status_all_lists_files_without_tokens_and_without_calling_the_app()
    {
        _dir.Write(300, "꺼진 창", token: "secret-token-300");

        Assert.Equal(0, await _cli.RunAsync("status", "--all"));

        var output = _cli.OutJson();
        var instances = output["instances"]!.AsArray();
        Assert.Equal([100, 300], instances.Select(i => (int)i!["pid"]!));
        Assert.False((bool)instances[1]!["alive"]!);
        Assert.Equal("dev-erp", (string?)instances[0]!["connections"]![0]!["alias"]);
        Assert.DoesNotContain("token", _cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Empty(_cli.Targets);
    }

    [Fact]
    public async Task Help_goes_to_stdout_with_exit_zero()
    {
        Assert.Equal(0, await _cli.RunAsync("help"));

        Assert.Contains("orandactl tab propose", _cli.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_name_comes_from_oranda_client_or_claude_code()
    {
        _cli["CLAUDECODE"] = "1";
        await _cli.RunAsync("tabs");
        _cli["ORANDA_CLIENT"] = "codex";
        await _cli.RunAsync("tabs");

        Assert.Equal(["Claude Code", "codex"], _cli.Bridge.Calls.Select(c => c.Client.Name));
    }

    [Fact]
    public async Task Mcp_from_outside_finds_the_endpoint_file_and_is_named_oranda()
    {
        Assert.Equal(0, await _cli.RunAsync("mcp"));

        Assert.Equal(CliApp.ExternalMcpServerName, _cli.McpServerName);
        Assert.Equal("token-100", Assert.Single(_cli.Targets).Token);
    }

    [Fact]
    public async Task Mcp_from_the_app_keeps_pipe_and_run_token_and_the_old_name()
    {
        _cli[AgentRunToken.EnvironmentVariable] = "run-token";

        Assert.Equal(0, await _cli.RunAsync("mcp", "--pipe", "oranda-agent-x"));

        Assert.Equal(CliApp.BuiltInMcpServerName, _cli.McpServerName);
        Assert.Equal(new BridgeTarget("oranda-agent-x", "run-token", External: false), Assert.Single(_cli.Targets));
    }

    [Fact]
    public async Task Mcp_starts_even_when_no_window_is_connected()
    {
        foreach (var file in Directory.GetFiles(Oranda.Core.Storage.AppDataLocation.AgentsDirectory(_dir.Path)))
        {
            File.Delete(file);
        }

        // 가짜 RunMcp 가 tools/list 를 한 번 부른다. 붙을 곳은 그 요청 때 찾으므로 서버는 이미 떴고, 그 요청만 no_endpoint 다.
        Assert.Equal(AgentCliExit.Unreachable, await _cli.RunAsync("mcp"));
        Assert.Equal(CliApp.ExternalMcpServerName, _cli.McpServerName);
        Assert.Empty(_cli.Targets);
    }

    /// <summary>같은 가짜 바깥으로 한 번 더 돌린다(출력 버퍼만 비움).</summary>
    private sealed class CliHarnessRun(CliHarness cli)
    {
        public Task<int> RunAsync(params string[] args)
        {
            cli.Out.GetStringBuilder().Clear();
            return cli.RunAsync(args);
        }
    }
}
