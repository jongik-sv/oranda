using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Schemas;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>Codex 스트림 해석·스트림 감시, 모델 카탈로그 잠금, 구조 결과 스키마 변환.</summary>
public sealed class CodexParsingTests
{
    private static List<CodexEvent> ParseAll(params string[] lines)
    {
        var parser = new CodexStreamParser();
        return [.. lines.SelectMany(parser.Parse)];
    }

    [Fact]
    public void Real_locked_run_output_is_translated()
    {
        var events = ParseAll(
            """{"type":"thread.started","thread_id":"01a19f88-3e8b-7531-9054-07ec9d7111ea"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"NO_TOOLS."}}""",
            """{"type":"turn.completed","usage":{"input_tokens":19671,"cached_input_tokens":12672,"output_tokens":106}}""");

        Assert.Equal(
            [
                new CodexThreadStarted("01a19f88-3e8b-7531-9054-07ec9d7111ea"),
                new CodexMessage("NO_TOOLS."),
                new CodexTurnCompleted(19671, 106),
            ],
            events);
    }

    [Fact]
    public void Mcp_tool_call_of_orandactl_is_announced_once_and_completed_with_its_size()
    {
        var events = ParseAll(
            """{"type":"item.started","item":{"id":"i1","type":"mcp_tool_call","server":"orandactl","tool":"describe_object","arguments":{"object":"EMP"},"status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"i1","type":"mcp_tool_call","server":"orandactl","tool":"describe_object","arguments":{"object":"EMP"},"status":"completed","result":{"content":[{"type":"text","text":"abcde"}]}}}""");

        Assert.Equal(new CodexToolStarted("i1", "describe_object", """{"object":"EMP"}"""), events[0]);
        Assert.Equal(new CodexToolDone("i1", "describe_object", false, 5), events[1]);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void A_completed_tool_call_without_a_started_line_is_still_announced_and_a_failed_one_is_an_error()
    {
        var events = ParseAll("""{"type":"item.completed","item":{"id":"i9","type":"mcp_tool_call","server":"orandactl","tool":"t","status":"failed","error":{"message":"boom"}}}""");

        Assert.IsType<CodexToolStarted>(events[0]);
        Assert.Equal(new CodexToolDone("i9", "t", true, 4), events[1]);
    }

    [Theory]
    [InlineData("command_execution")]
    [InlineData("file_change")]
    [InlineData("web_search")]
    [InlineData("todo_list")]
    [InlineData("collab_tool_call")]
    public void Unknown_item_types_are_unexpected_as_soon_as_they_start(string itemType)
    {
        // item.started 에서 이미 막혀야 한다(completed 까지 기다리면 명령이 이미 실행됐다).
        var events = ParseAll($$$"""{"type":"item.started","item":{"id":"x","type":"{{{itemType}}}"}}""");

        Assert.Equal(new CodexUnexpectedItem(itemType), Assert.Single(events));
    }

    [Theory]
    [InlineData("""{"type":"item.started","item":{"id":"x","type":"mcp_tool_call","server":"other","tool":"t"}}""")]
    [InlineData("""{"type":"item.started","item":{"id":"x","type":"mcp_tool_call","tool":"t"}}""")]
    [InlineData("""{"type":"item.updated","item":{"id":"x","type":"mcp_tool_call","server":"orandactl2","tool":"t"}}""")]
    [InlineData("""{"type":"item.started","item":{"id":"x"}}""")]
    [InlineData("""{"type":"item.completed"}""")]
    public void Foreign_servers_and_items_without_a_type_are_unexpected(string line)
    {
        Assert.IsType<CodexUnexpectedItem>(Assert.Single(ParseAll(line)));
    }

    [Fact]
    public void Reasoning_and_error_items_and_top_level_error_events_are_not_failures()
    {
        var events = ParseAll(
            """{"type":"item.completed","item":{"id":"r","type":"reasoning","text":"..."}}""",
            """{"type":"item.completed","item":{"id":"e","type":"error","message":"reconnecting"}}""",
            """{"type":"error","message":"Reconnecting... 2/5"}""",
            "not json at all",
            string.Empty);

        Assert.Empty(events);
    }

    [Fact]
    public void An_unknown_top_level_event_is_unexpected_instead_of_silently_ignored()
    {
        Assert.Equal(new CodexUnexpectedItem("이벤트 tool.exec"), Assert.Single(ParseAll("""{"type":"tool.exec","cmd":"ls"}""")));
    }

    [Fact]
    public void A_warning_that_a_lock_setting_was_ignored_is_fatal_not_a_normal_error_item()
    {
        var events = ParseAll("""{"type":"item.completed","item":{"id":"e","type":"error","message":"Codex is ignoring 1 unrecognized configuration setting: features.shell_tool is ignored"}}""");

        Assert.IsType<CodexUnexpectedItem>(Assert.Single(events));
    }

    [Fact]
    public void Only_turn_failed_is_a_failure_with_its_message()
    {
        var events = ParseAll("""{"type":"turn.failed","error":{"message":"401 Unauthorized"}}""", """{"type":"turn.failed"}""");

        Assert.Equal([new CodexTurnFailed("401 Unauthorized"), new CodexTurnFailed("Codex 실행 오류")], events);
    }

    private static string DebugModels(Action<JsonObject>? change = null)
    {
        var model = new JsonObject
        {
            ["slug"] = "gpt-x",
            ["tool_mode"] = "code_mode_only",
            ["apply_patch_tool_type"] = "freeform",
            ["multi_agent_version"] = "v2",
            ["experimental_supported_tools"] = new JsonArray("a"),
            ["supports_search_tool"] = true,
            ["unknown_future_field"] = 7,
        };
        change?.Invoke(model);
        return new JsonObject { ["models"] = new JsonArray(model, model.DeepClone()) }.ToJsonString();
    }

    [Fact]
    public void Catalog_lock_overwrites_the_five_fields_and_keeps_everything_else()
    {
        var (json, error) = CodexModelCatalog.Lock(DebugModels());

        Assert.Null(error);
        foreach (var model in JsonNode.Parse(json!)!["models"]!.AsArray())
        {
            Assert.Null(model!["tool_mode"]);
            Assert.True(model.AsObject().ContainsKey("tool_mode")); // 키는 남고 값만 null
            Assert.Null(model["apply_patch_tool_type"]);
            Assert.Null(model["multi_agent_version"]);
            Assert.Empty(model["experimental_supported_tools"]!.AsArray());
            Assert.False(model["supports_search_tool"]!.GetValue<bool>());
            Assert.Equal("gpt-x", model["slug"]!.GetValue<string>());
            Assert.Equal(7, model["unknown_future_field"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData("tool_mode")]
    [InlineData("apply_patch_tool_type")]
    [InlineData("multi_agent_version")]
    [InlineData("experimental_supported_tools")]
    [InlineData("supports_search_tool")]
    public void A_catalog_where_no_model_has_one_of_the_five_fields_fails_closed_because_it_may_have_been_renamed(string key)
    {
        var (json, error) = CodexModelCatalog.Lock(DebugModels(m => m.Remove(key)));

        Assert.Null(json);
        Assert.Contains(key, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_that_omits_a_null_field_is_accepted_when_other_models_have_the_key()
    {
        var withKey = JsonNode.Parse(DebugModels())!["models"]![0]!.DeepClone();
        var without = JsonNode.Parse(DebugModels(m => { m.Remove("tool_mode"); m.Remove("multi_agent_version"); }))!["models"]![0]!.DeepClone();
        var output = new JsonObject { ["models"] = new JsonArray(withKey, without) }.ToJsonString();

        var (json, error) = CodexModelCatalog.Lock(output);

        Assert.Null(error);
        foreach (var model in JsonNode.Parse(json!)!["models"]!.AsArray())
        {
            Assert.Null(model!["tool_mode"]);
            Assert.Null(model["multi_agent_version"]);
            Assert.False(model["supports_search_tool"]!.GetValue<bool>());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"models":[]}""")]
    [InlineData("""{"models":[1]}""")]
    [InlineData("""{"list":[{}]}""")]
    public void Unexpected_catalog_output_fails_closed(string output)
    {
        var (json, error) = CodexModelCatalog.Lock(output);

        Assert.Null(json);
        Assert.NotNull(error);
    }

    [Fact]
    public void Strict_schema_requires_every_property_allows_null_for_optional_ones_and_closes_objects()
    {
        var original = TuningResult.Schema();

        var strict = JsonNode.Parse(CodexOutputSchema.ToStrict(original))!;

        var rewrite = strict["properties"]!["rewrites"]!["items"]!;
        Assert.Equal(["label", "sql", "rationale", "benchmark"], rewrite["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["string", "null"], rewrite["properties"]!["benchmark"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("string", rewrite["properties"]!["sql"]!["type"]!.GetValue<string>()); // 원래 필수는 그대로
        Assert.False(rewrite["additionalProperties"]!.GetValue<bool>());
        var index = strict["properties"]!["index_suggestions"]!["items"]!;
        Assert.Equal(["string", "null"], index["properties"]!["existing_index"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["array", "null"], index["properties"]!["existing_columns"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));

        // 원본은 바뀌지 않는다.
        Assert.DoesNotContain("\"null\"", original.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_nulls_are_removed_from_the_result_so_it_matches_the_claude_shape()
    {
        using var result = JsonDocument.Parse("""
            {"summary":"s","rewrites":[{"label":"a","sql":"x","rationale":"r","benchmark":null}],
             "index_suggestions":[{"action":"create","existing_index":null,"existing_columns":null,"table":"T","columns":["C"],"ddl":"d","rationale":"r","risk":null}],
             "evidence":[]}
            """);

        var cleaned = CodexOutputSchema.StripOptionalNulls(result.RootElement, TuningResult.Schema());

        Assert.False(cleaned.GetProperty("rewrites")[0].TryGetProperty("benchmark", out _));
        var suggestion = cleaned.GetProperty("index_suggestions")[0];
        Assert.False(suggestion.TryGetProperty("existing_index", out _));
        Assert.False(suggestion.TryGetProperty("risk", out _));
        Assert.Equal("T", suggestion.GetProperty("table").GetString());
    }
}
