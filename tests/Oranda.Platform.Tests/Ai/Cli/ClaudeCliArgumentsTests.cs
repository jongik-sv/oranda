using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

public class ClaudeCliArgumentsTests
{
    [Fact]
    public void New_conversation_has_fixed_safety_options_and_no_resume()
    {
        var arguments = ClaudeCliArguments.Build(new ClaudeCliInvocation("C:/run/mcp.json", "{\"type\":\"object\"}", "haiku"));

        Assert.Equal("-p", arguments[0]);
        AssertPair(arguments, "--mcp-config", "C:/run/mcp.json");
        Assert.Contains("--strict-mcp-config", arguments);
        AssertPair(arguments, "--allowedTools", "mcp__orandactl");
        AssertPair(arguments, "--setting-sources", "project");
        AssertPair(arguments, "--tools", string.Empty);
        AssertPair(arguments, "--output-format", "stream-json");
        Assert.Contains("--verbose", arguments);
        AssertPair(arguments, "--json-schema", "{\"type\":\"object\"}");
        AssertPair(arguments, "--model", "haiku");
        Assert.DoesNotContain("--resume", arguments);
        Assert.DoesNotContain("--bare", arguments);

        // 가변 인자 옵션은 맨 끝: 뒤에 값이 더 이어지지 않아야 도구 이름으로 삼키지 않는다.
        var index = arguments.ToList().IndexOf("--disallowedTools");
        Assert.Equal(["--disallowedTools", "WebFetch", "WebSearch", "Bash"], arguments.Skip(index));
    }

    [Fact]
    public void Resume_repeats_mcp_options_so_connectors_never_attach()
    {
        var arguments = ClaudeCliArguments.Build(new ClaudeCliInvocation("C:/run/mcp.json", ResumeId: "sid-1"));

        AssertPair(arguments, "--resume", "sid-1");
        AssertPair(arguments, "--mcp-config", "C:/run/mcp.json");
        Assert.Contains("--strict-mcp-config", arguments);
        AssertPair(arguments, "--allowedTools", "mcp__orandactl");
        AssertPair(arguments, "--setting-sources", "project");
        Assert.DoesNotContain("--json-schema", arguments);
        Assert.DoesNotContain("--model", arguments);
    }

    private static void AssertPair(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0, $"{option} 없음");
        Assert.Equal(value, arguments[index + 1]);
    }
}
