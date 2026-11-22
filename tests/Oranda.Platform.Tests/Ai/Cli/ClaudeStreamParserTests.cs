using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>b0 에서 실제 claude 로 녹화한 stream-json(Fixtures/) 로 읽기 규칙을 확인한다.</summary>
public class ClaudeStreamParserTests
{
    [Fact]
    public void Two_tool_calls_hide_synthetic_tools_and_end_with_structured_output()
    {
        var events = ParseFixture("two-tool-calls.jsonl");

        Assert.IsType<ClaudeSessionStarted>(events[0]);
        var calls = events.OfType<ClaudeToolUse>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("oranda_ping", c.ToolName));
        Assert.Equal(2, events.OfType<ClaudeToolResult>().Count());
        Assert.All(events.OfType<ClaudeToolResult>(), r => Assert.Equal("tok=abc-test-123".Length, r.Size));

        var result = Assert.IsType<ClaudeResult>(events[^1]);
        Assert.False(result.IsError);
        Assert.Equal("tok=abc-test-123, tok=abc-test-123", result.StructuredOutput!.Value.GetProperty("result").GetString());
        Assert.True(result.InputTokens > 0);
        Assert.True(result.OutputTokens > 0);
        Assert.Empty(result.PermissionDenials);
    }

    [Fact]
    public void Denied_tool_shows_permission_denial_in_stream_and_result()
    {
        var events = ParseFixture("permission-denied.jsonl");

        var denied = Assert.Single(events.OfType<ClaudePermissionDenied>());
        Assert.Equal("oranda_ping", denied.ToolName);
        var result = Assert.IsType<ClaudeResult>(events[^1]);
        Assert.Equal(["oranda_ping"], result.PermissionDenials);
    }

    [Fact]
    public void Resume_keeps_the_same_session_id()
    {
        var events = ParseFixture("resume-no-tool.jsonl");

        var started = events.OfType<ClaudeSessionStarted>().Single();
        var result = events.OfType<ClaudeResult>().Single();
        Assert.Equal(started.SessionId, result.SessionId);
        Assert.Empty(events.OfType<ClaudeToolUse>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"rate_limit_event\"}")]
    public void Blank_broken_or_unknown_lines_are_skipped(string line)
    {
        Assert.Empty(new ClaudeStreamParser().Parse(line));
    }

    [Fact]
    public void Logged_out_run_marks_the_error_result_as_not_logged_in()
    {
        // 2.1.330 실측(ai-setup-claude-measure.md): assistant 줄 맨 위 error = authentication_failed, result 는 is_error true·subtype success.
        var result = Assert.IsType<ClaudeResult>(ParseFixture("not-logged-in.jsonl")[^1]);

        Assert.True(result.IsError);
        Assert.True(result.NotLoggedIn);
        Assert.Equal("Not logged in · Please run /login", result.Text);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}""", true)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"API Error: 500 overloaded"}""", false)]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"Not logged in 이라는 글을 설명해 줘"}""", false)]
    public void Not_logged_in_falls_back_to_the_error_result_text(string line, bool expected)
        => Assert.Equal(expected, Assert.IsType<ClaudeResult>(Assert.Single(new ClaudeStreamParser().Parse(line))).NotLoggedIn);

    [Fact]
    public void Other_api_errors_on_the_assistant_line_are_not_not_logged_in()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"Rate limited"}]},"error":"rate_limit","is_api_error_message":true}""");

        var result = Assert.IsType<ClaudeResult>(Assert.Single(parser.Parse("""{"type":"result","subtype":"success","is_error":true,"result":"Rate limited"}""")));

        Assert.False(result.NotLoggedIn);
    }

    private static List<ClaudeStreamEvent> ParseFixture(string name)
    {
        var parser = new ClaudeStreamParser();
        return File.ReadLines(Path.Combine(FindRepositoryRoot(), "tests", "Oranda.Platform.Tests", "Ai", "Cli", "Fixtures", name))
            .SelectMany(parser.Parse)
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
    }
}
