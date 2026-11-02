using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Platform.Ai.Api;

namespace Oranda.Platform.Tests.Ai.Api;

/// <summary>스트림 조각 조립(api1): HTTP 없이 조각 글만으로 확인한다.</summary>
public sealed class OpenAiTurnAccumulatorTests
{
    private static OpenAiTurn Assemble(params string[] chunks)
    {
        var accumulator = new OpenAiTurnAccumulator();
        foreach (var chunk in chunks)
        {
            using var document = JsonDocument.Parse(chunk);
            accumulator.Apply(document.RootElement);
        }

        return accumulator.Finish();
    }

    [Fact]
    public void Text_pieces_are_joined_and_each_piece_is_reported_as_a_delta()
    {
        var accumulator = new OpenAiTurnAccumulator();
        var deltas = new List<string?>();
        foreach (var chunk in FakeOpenAiServer.TextTurn("안녕", 7, 3).Prepend(FakeOpenAiServer.Text("하세요 ")))
        {
            using var document = JsonDocument.Parse(chunk);
            deltas.Add(accumulator.Apply(document.RootElement).TextDelta);
        }

        var turn = accumulator.Finish();

        Assert.Equal("하세요 안녕", turn.Text);
        Assert.Equal(["하세요 ", "안녕"], deltas.Where(d => d is not null));
        Assert.Equal("stop", turn.FinishReason);
        Assert.Equal(new OpenAiUsage(7, 3), turn.Usage);
        Assert.Empty(turn.ToolCalls);
    }

    [Fact]
    public void Fragmented_tool_call_arguments_are_assembled_per_index()
    {
        var turn = Assemble(
            FakeOpenAiServer.ToolCall(0, "call_a", "describe_object", string.Empty),
            FakeOpenAiServer.ToolCall(1, "call_b", "get_table_stats", string.Empty),
            FakeOpenAiServer.ToolCall(0, null, null, "{\"object\":"),
            FakeOpenAiServer.ToolCall(1, null, null, "{\"table\":\"T\"}"),
            FakeOpenAiServer.ToolCall(0, null, null, "\"TB_ORDER\"}"),
            FakeOpenAiServer.Finish("tool_calls"));

        Assert.Equal(["call_a", "call_b"], turn.ToolCalls.Select(c => c.Id));
        Assert.Equal(["describe_object", "get_table_stats"], turn.ToolCalls.Select(c => c.Name));
        Assert.Equal("{\"object\":\"TB_ORDER\"}", turn.ToolCalls[0].ArgumentsText);
        Assert.Equal("{\"table\":\"T\"}", turn.ToolCalls[1].ArgumentsText);
    }

    [Fact]
    public void A_usage_only_chunk_with_empty_choices_and_a_missing_index_still_assemble()
    {
        // 인자가 한 번에 오고 index 가 없는 서버(id 로 짝짓는다), 끝 조각 뒤에 choices 가 빈 usage 조각이 온다.
        var turn = Assemble(
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_x","type":"function","function":{"name":"describe_object","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":9}}""");

        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal(("call_x", "describe_object", "{}"), (call.Id, call.Name, call.ArgumentsText));
        Assert.Equal(new OpenAiUsage(120, 9), turn.Usage);
    }

    [Fact]
    public void Repeated_role_and_name_in_every_chunk_do_not_corrupt_the_values()
    {
        var turn = Assemble(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"a","tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"describe_object","arguments":"{\"o\""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"b","tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"describe_object","arguments":":1}"}}]}}]}""",
            FakeOpenAiServer.Finish("tool_calls"));

        var message = JsonNode.Parse(turn.AssistantMessageJson)!;
        Assert.Equal("assistant", (string?)message["role"]);
        Assert.Equal("ab", (string?)message["content"]);
        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal(("c1", "describe_object", "{\"o\":1}"), (call.Id, call.Name, call.ArgumentsText));
    }

    [Fact]
    public void Unknown_fields_such_as_reasoning_content_and_gemini_signatures_survive_into_the_assistant_message()
    {
        var turn = Assemble(
            """{"choices":[{"index":0,"delta":{"reasoning_content":"생각 "}}]}""",
            """{"choices":[{"index":0,"delta":{"reasoning_content":"중","tool_calls":[{"index":0,"id":"c1","type":"function","extra_content":{"google":{"thought_signature":"SIG=="}},"function":{"name":"describe_object","arguments":"{}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"vendor_extra":{"k":[1,2]}},"finish_reason":"tool_calls"}]}""");

        var message = JsonNode.Parse(turn.AssistantMessageJson)!;
        Assert.Equal("생각 중", (string?)message["reasoning_content"]);
        Assert.Equal("[1,2]", message["vendor_extra"]!["k"]!.ToJsonString());
        var call = message["tool_calls"]![0]!;
        Assert.Equal("SIG==", (string?)call["extra_content"]!["google"]!["thought_signature"]);
        Assert.Null(call["index"]);
        Assert.Equal("function", (string?)call["type"]);
    }

    [Fact]
    public void A_tool_call_without_an_id_gets_a_generated_one_and_the_same_value_is_sent_back()
    {
        var turn = Assemble(FakeOpenAiServer.ToolCall(0, null, "describe_object", "{}"), FakeOpenAiServer.Finish("tool_calls"));

        var call = Assert.Single(turn.ToolCalls);
        Assert.StartsWith("call_", call.Id, StringComparison.Ordinal);
        Assert.Equal(call.Id, (string?)JsonNode.Parse(turn.AssistantMessageJson)!["tool_calls"]![0]!["id"]);
    }

    [Fact]
    public void A_non_streaming_message_body_and_an_error_chunk_are_understood()
    {
        var turn = Assemble("""{"choices":[{"index":0,"message":{"role":"assistant","content":"한 번에"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2}}""");
        Assert.Equal("한 번에", turn.Text);
        Assert.Equal("stop", turn.FinishReason);

        var accumulator = new OpenAiTurnAccumulator();
        using var error = JsonDocument.Parse("""{"error":{"message":"context length exceeded","type":"invalid_request_error"}}""");
        Assert.Equal("context length exceeded", accumulator.Apply(error.RootElement).Error);
    }

    [Fact]
    public void Complete_calls_without_index_or_id_stay_separate_and_a_fractional_index_does_not_throw()
    {
        var turn = Assemble(
            """{"choices":[{"delta":{"tool_calls":[{"function":{"name":"describe_object","arguments":"{}"}},{"function":{"name":"get_table_stats","arguments":"{\"t\":1}"}}]}}]}""",
            """{"choices":[{"index":1.5,"delta":{},"finish_reason":"tool_calls"}]}""");

        Assert.Equal(["describe_object", "get_table_stats"], turn.ToolCalls.Select(c => c.Name));
        Assert.Equal(["{}", "{\"t\":1}"], turn.ToolCalls.Select(c => c.ArgumentsText));
    }
}
