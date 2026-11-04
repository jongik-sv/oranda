using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary><c>--format json</c> 사건 읽기. 실제 실행에서 캡처한 모양(opencode 2.0.32)을 따른다.</summary>
public sealed class OpencodeStreamParserTests
{
    private static List<OpencodeEvent> Parse(string line) => [.. OpencodeStreamParser.Parse(line)];

    [Fact]
    public void A_completed_tool_use_carries_name_status_input_and_output_size()
    {
        var events = Parse("""{"type":"tool_use","sessionID":"ses_1","part":{"id":"call_1","type":"tool","tool":"describe_object","state":{"status":"completed","input":{"name":"EMP"},"output":"abcd"}}}""");

        var tool = Assert.IsType<OpencodeTool>(Assert.Single(events));
        Assert.Equal(new OpencodeTool("ses_1", "call_1", "describe_object", "completed", """{"name":"EMP"}""", 4, null), tool);
    }

    [Fact]
    public void A_failed_tool_use_carries_the_error_text()
    {
        var tool = Assert.IsType<OpencodeTool>(Assert.Single(Parse("""{"type":"tool_use","sessionID":"s","part":{"id":"c","tool":"x","state":{"status":"error","input":{},"error":"nope"}}}""")));

        Assert.Equal("error", tool.Status);
        Assert.Equal("nope", tool.Error);
    }

    [Theory]
    [InlineData("""{"type":"tool_use","sessionID":"s","part":{"id":"c","tool":"read","state":""")] // 잘린 JSON
    [InlineData("""  {"type":"tool_use","sessionID":"s","part":{"tool":"read"}}""")] // 앞 공백이 있는 줄
    public void A_tool_use_line_that_cannot_be_read_closes_the_run(string line)
        => Assert.IsType<OpencodeUnexpected>(Assert.Single(Parse(line)));

    [Fact]
    public void A_broken_line_that_is_not_a_tool_use_is_dropped()
        => Assert.Empty(Parse("""{"type":"text","part":{"text":"""));

    [Theory]
    [InlineData("""{"type":"tool_use","sessionID":"s","part":{"id":"c","state":{"status":"completed"}}}""")]
    [InlineData("""{"type":"tool_use","sessionID":"s"}""")]
    [InlineData("""{"type":"tool_use","sessionID":"s","part":{"tool":"","state":{}}}""")]
    public void A_tool_use_without_a_name_is_unexpected_so_the_run_is_closed(string line)
        => Assert.IsType<OpencodeUnexpected>(Assert.Single(Parse(line)));

    [Fact]
    public void Text_step_start_step_finish_and_error_are_read()
    {
        Assert.Equal(new OpencodeText("s", "안녕"), Assert.Single(Parse("""{"type":"text","sessionID":"s","part":{"type":"text","text":"안녕"}}""")));
        Assert.Equal(new OpencodeStepStart("s"), Assert.Single(Parse("""{"type":"step_start","sessionID":"s","part":{}}""")));
        Assert.Equal(
            new OpencodeStepFinish("s", "stop", 10, 5),
            Assert.Single(Parse("""{"type":"step_finish","sessionID":"s","part":{"reason":"stop","tokens":{"input":10,"output":5,"reasoning":1,"cache":{"read":0,"write":0}}}}""")));
        Assert.Equal(
            new OpencodeError("s", "quota"),
            Assert.Single(Parse("""{"type":"error","sessionID":"s","error":{"type":"provider.quota","message":"quota"}}""")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain log line")]
    [InlineData("{not json")]
    [InlineData("""{"type":"something_new","sessionID":"s"}""")]
    [InlineData("""{"noType":1}""")]
    [InlineData("[1,2]")]
    public void Lines_that_are_not_known_events_are_dropped(string line)
        => Assert.Empty(Parse(line));
}
