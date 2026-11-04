using System.Text.Json;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>opencode 인자와 잠금 설정을 고정한다: 새 대화·이어 말하기 모두 같은 잠금 인자를 주고, 토큰·자동 승인이 없다.</summary>
public sealed class OpencodeArgumentsTests
{
    [Fact]
    public void A_new_conversation_runs_the_locked_agent_in_json_mode_without_auto_approval()
    {
        var arguments = OpencodeArguments.Build(new OpencodeInvocation(null));

        Assert.Equal(["run", "--standalone", "--format", "json", "--agent", "oranda_locked"], arguments);
        Assert.DoesNotContain("--auto", arguments);
        Assert.DoesNotContain("--session", arguments);
    }

    [Fact]
    public void Resuming_keeps_every_lock_argument_and_adds_the_session()
    {
        var fresh = OpencodeArguments.Build(new OpencodeInvocation(null));
        var resumed = OpencodeArguments.Build(new OpencodeInvocation("ses_abcdef123456"));

        Assert.Equal([.. fresh, "--session", "ses_abcdef123456"], resumed);
    }

    [Fact]
    public void The_config_denies_everything_then_allows_only_the_given_tools_for_the_top_level_and_the_locked_agent()
    {
        using var config = JsonDocument.Parse(OpencodeArguments.BuildConfigContent(["describe_object", OpencodeArguments.FinishToolName], []));
        var root = config.RootElement;

        Assert.Equal("oranda_locked", root.GetProperty("default_agent").GetString());
        var expected = new[] { "*:deny", "describe_object:allow", "oranda_finish:allow" };
        Assert.Equal(expected, RulesOf(root.GetProperty("permissions")));
        var agent = root.GetProperty("agents").GetProperty("oranda_locked");
        Assert.Equal("primary", agent.GetProperty("mode").GetString());
        Assert.Equal(expected, RulesOf(agent.GetProperty("permissions")));
        Assert.False(root.TryGetProperty("mcp", out _));
    }

    [Fact]
    public void User_mcp_servers_are_turned_off_by_name_in_the_local_shape()
    {
        using var config = JsonDocument.Parse(OpencodeArguments.BuildConfigContent([], ["rea", "other"]));

        var mcp = config.RootElement.GetProperty("mcp");
        foreach (var name in new[] { "rea", "other" })
        {
            var server = mcp.GetProperty(name);
            Assert.Equal("local", server.GetProperty("type").GetString());
            Assert.False(server.GetProperty("enabled").GetBoolean());
        }
    }

    [Theory]
    [InlineData("describe_object", true)]
    [InlineData("a-b_C9", true)]
    [InlineData("*", false)]
    [InlineData("a*", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData("도구", false)]
    [InlineData("describe_object\n", false)] // 끝 줄바꿈 하나도 받지 않는다
    public void Tool_names_must_be_plain_so_an_allow_rule_cannot_widen(string name, bool allowed)
        => Assert.Equal(allowed, OpencodeArguments.ToolNamePattern().IsMatch(name));

    [Theory]
    [InlineData("ses_e5b124059ffeAppIM3ZV7gwvYH", true)]
    [InlineData("--help", false)]
    [InlineData("ses_", false)]
    [InlineData("ses_a b", false)]
    [InlineData("th-1", false)]
    [InlineData("ses_e5b124059ffeAppIM3ZV7gwvYH\n", false)]
    public void Only_opencode_session_ids_are_accepted_for_resume(string id, bool valid)
        => Assert.Equal(valid, OpencodeArguments.SessionIdPattern().IsMatch(id));

    [Theory]
    [InlineData("read")]
    [InlineData("bash")]
    [InlineData("webfetch")]
    [InlineData("execute")]
    [InlineData("Grep")] // 대소문자 구분 없이
    public void App_tools_cannot_reuse_a_builtin_tool_name(string name)
        => Assert.Contains(name, OpencodeArguments.ReservedToolNames);

    private static string[] RulesOf(JsonElement rules)
        => [.. rules.EnumerateArray().Select(r => $"{r.GetProperty("action").GetString()}:{r.GetProperty("effect").GetString()}")];
}
