using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Api;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Api;

/// <summary>API 경로 드라이버: 도구 반복·게이트·상한·구조 결과·이어 말하기·오류·취소·키 비노출. 가짜 서버와 가짜 게이트로 확인한다(실제 게이트는 OpenAiApiDriverGateTests).</summary>
public sealed class OpenAiApiDriverTests : IDisposable
{
    private const string Key = "sk-secret-0123456789";

    private static readonly AiLlmProfile Profile = new()
    {
        Id = Guid.NewGuid(),
        Name = "사내 서버",
        Preset = AiProfilePreset.Custom,
        Format = AiApiFormat.OpenAi,
        BaseUrl = "http://llm.example:8080",
        Model = "qwen-test",
        MaxInputTokens = 1000,
        MaxOutputTokens = 777,
    };

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}""").RootElement.Clone();

    private readonly FakeOpenAiServer _server = new();
    private readonly FakeGate _gate = new();
    private readonly List<OpenAiApiDriver> _drivers = [];

    public void Dispose()
    {
        foreach (var driver in _drivers)
        {
            driver.Dispose();
        }
    }

    private OpenAiApiDriver Create(OpenAiApiOptions? options = null, IAgentGate? gate = null)
    {
        var driver = new OpenAiApiDriver(gate ?? _gate, _server, options ?? new OpenAiApiOptions(TimeSpan.FromSeconds(30)));
        _drivers.Add(driver);
        return driver;
    }

    private static AgentSession NewSession() =>
        new(new AgentRunScope("run-1", new ConnectionProfile("dev", "scott", "h/s"), new AiContext("dev", null, AiAccessLevel.Plan)));

    private static AiRequest Request(AgentSession? session, string prompt = "질문", string? resumeId = null, JsonElement? schema = null, string? model = null, IReadOnlyList<AiImageAttachment>? images = null) =>
        new(prompt, new AiContext("dev", null, AiAccessLevel.Plan), resumeId, schema, model, session, null, images);

    private static AiRoute Route(AiLlmProfile? profile = null, string? key = null) => new(profile ?? Profile, key);

    private static async Task<List<AiEvent>> RunAsync(OpenAiApiDriver driver, AiRequest request, AiRoute? route = null, CancellationToken ct = default)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, route ?? Route(), ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static string CodeOf(IEnumerable<AiEvent> events) => Assert.IsType<AiError>(events.Last()).Code!;

    // ---------------------------------------------------------------- 글·요청 모양 (api1·api2)

    [Fact]
    public async Task A_text_answer_streams_deltas_then_usage_then_final_and_sends_the_documented_request()
    {
        _server.EnqueueSse(FakeOpenAiServer.Text("안녕"), FakeOpenAiServer.Text("하세요"), FakeOpenAiServer.Finish(), FakeOpenAiServer.Usage(10, 5));
        var session = NewSession();

        var events = await RunAsync(Create(), Request(session));

        Assert.Collection(
            events,
            e => Assert.StartsWith("api-", Assert.IsType<AiStarted>(e).ResumeId, StringComparison.Ordinal),
            e => Assert.Equal("안녕", Assert.IsType<AiTextDelta>(e).Text),
            e => Assert.Equal("하세요", Assert.IsType<AiTextDelta>(e).Text),
            e => Assert.Equal(new AiUsage(10, 5, null), e),
            e =>
            {
                var final = Assert.IsType<AiFinal>(e);
                Assert.Equal("안녕하세요", final.Text);
                Assert.Null(final.Result);
                Assert.Equal(((AiStarted)events[0]).ResumeId, final.ResumeId);
            });

        var request = Assert.Single(_server.Requests);
        Assert.Equal("http://llm.example:8080/v1/chat/completions", request.Uri.ToString());
        Assert.Null(request.Authorization);
        Assert.Contains("text/event-stream", request.Accept, StringComparison.Ordinal);
        var body = request.Json;
        Assert.Equal("qwen-test", (string?)body["model"]);
        Assert.True((bool)body["stream"]!);
        Assert.True((bool)body["stream_options"]!["include_usage"]!);
        Assert.Equal(777, (int)body["max_tokens"]!);
        Assert.Null(body["max_completion_tokens"]);
        Assert.Equal("system", (string?)request.Messages[0]!["role"]);
        Assert.Equal(("user", "질문"), (((string?)request.Messages[1]!["role"])!, ((string?)request.Messages[1]!["content"])!));
        Assert.Equal(OpenAiApiDriver.ClientName, session.Client);
    }

    [Fact]
    public async Task The_profile_model_wins_over_the_request_model_and_the_openai_preset_uses_max_completion_tokens()
    {
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("ok"));
        var openAi = Profile with { Preset = AiProfilePreset.OpenAi, Model = "gpt-test" };

        await RunAsync(Create(), Request(NewSession(), model: "opus"), Route(openAi, Key));

        var body = Assert.Single(_server.Requests).Json;
        Assert.Equal("gpt-test", (string?)body["model"]);
        Assert.Equal(777, (int)body["max_completion_tokens"]!);
        Assert.Null(body["max_tokens"]);
        Assert.Equal("Bearer " + Key, _server.Requests[0].Authorization);
    }

    [Fact]
    public async Task Images_are_sent_as_data_url_parts_and_text_only_requests_keep_a_plain_string()
    {
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("본 그림"));
        var png = new AiImageAttachment("image/png", [1, 2, 3]);

        await RunAsync(Create(), Request(NewSession(), images: [png]));

        var content = _server.Requests[0].Messages[1]!["content"]!.AsArray();
        Assert.Equal("text", (string?)content[0]!["type"]);
        Assert.Equal("image_url", (string?)content[1]!["type"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)content[1]!["image_url"]!["url"]);
    }

    [Fact]
    public async Task Images_are_refused_when_the_profile_says_unsupported_or_the_type_is_unknown()
    {
        var driver = Create();

        var unsupported = await RunAsync(driver, Request(NewSession(), images: [new AiImageAttachment("image/png", [1])]), Route(Profile with { SupportsImages = false }));
        var badType = await RunAsync(driver, Request(NewSession(), images: [new AiImageAttachment("image/bmp", [1])]));

        Assert.Equal("images_unsupported", CodeOf(unsupported));
        Assert.Equal("image_invalid", CodeOf(badType));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_response_without_done_marker_is_fine_after_finish_reason_and_a_json_body_is_understood()
    {
        _server.Enqueue(FakeOpenAiServer.Sse([FakeOpenAiServer.Text("끝"), FakeOpenAiServer.Finish()], done: false));
        _server.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"index":0,"message":{"role":"assistant","content":"한 번에"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var driver = Create();

        var streamed = await RunAsync(driver, Request(NewSession()));
        var whole = await RunAsync(driver, Request(NewSession()));

        Assert.Equal("끝", Assert.IsType<AiFinal>(streamed.Last()).Text);
        Assert.Equal("한 번에", Assert.IsType<AiFinal>(whole.Last()).Text);
    }

    // ---------------------------------------------------------------- 도구 반복·게이트 (api2)

    [Fact]
    public async Task A_tool_call_goes_through_the_gate_and_its_result_is_sent_back_as_a_tool_message()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", "describe_object", "{\"object\":\"TB_ORDER\"}", 100, 10));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("TB_ORDER 는 주문표입니다.", 150, 20));
        var session = NewSession();

        var events = await RunAsync(Create(), Request(session));

        Assert.Equal("describe_object", _gate.LastTool);
        Assert.Equal("TB_ORDER", (string?)_gate.LastArguments!["object"]);
        Assert.Same(session, _gate.LastSession);
        Assert.Collection(
            events.Where(e => e is AiToolCall or AiToolResult),
            e => Assert.Equal(new AiToolCall("call_1", "describe_object", "{\"object\":\"TB_ORDER\"}"), e),
            e => Assert.Equal(new AiToolResult("call_1", "describe_object", "{\"ok\":true}".Length, AiToolJudgement.Allowed, null), e));
        Assert.Equal(new AiUsage(250, 30, null), events.OfType<AiUsage>().Single());
        Assert.Equal("TB_ORDER 는 주문표입니다.", events.OfType<AiFinal>().Single().Text);

        var first = _server.Requests[0].Json;
        Assert.Equal("describe_object", (string?)first["tools"]![0]!["function"]!["name"]);
        var second = _server.Requests[1].Messages;
        Assert.Equal(["system", "user", "assistant", "tool"], second.Select(m => (string?)m!["role"]));
        Assert.Equal("call_1", (string?)second[2]!["tool_calls"]![0]!["id"]);
        Assert.Equal("call_1", (string?)second[3]!["tool_call_id"]);
        Assert.Equal("{\"ok\":true}", (string?)second[3]!["content"]);
    }

    [Fact]
    public async Task The_assistant_message_is_returned_to_the_server_with_the_fields_it_came_with()
    {
        _server.EnqueueSse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"표를 먼저 본다"}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","extra_content":{"google":{"thought_signature":"SIG=="}},"function":{"name":"describe_object","arguments":"{}"}}]}}]}""",
            FakeOpenAiServer.Finish("tool_calls"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("끝"));

        await RunAsync(Create(), Request(NewSession()));

        var assistant = _server.Requests[1].Messages[2]!;
        Assert.Equal("표를 먼저 본다", (string?)assistant["reasoning_content"]);
        Assert.Equal("SIG==", (string?)assistant["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]);
    }

    [Fact]
    public async Task Unreadable_tool_arguments_never_reach_the_gate_and_empty_arguments_mean_no_arguments()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", "describe_object", "{\"object\":"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_2", "describe_object", string.Empty));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("끝"));

        var events = await RunAsync(Create(), Request(NewSession()));

        var results = events.OfType<AiToolResult>().ToList();
        Assert.Equal((AiToolJudgement.Failed, "invalid_arguments"), (results[0].Judgement, results[0].Note));
        Assert.Equal(AiToolJudgement.Allowed, results[1].Judgement);
        Assert.Equal(new JsonObject().ToJsonString(), _gate.LastArguments!.ToJsonString());
        Assert.Contains("invalid_arguments", (string?)_server.Requests[1].Messages[^1]!["content"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gate_judgement_and_note_are_passed_to_the_tool_card()
    {
        var gate = new StubGate(new AgentGateResponse(new AgentCallToolResult(true, "{\"error\":\"denied\",\"code\":\"level_too_low\"}"), AiToolJudgement.Denied, "level_too_low"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", "get_table_stats", "{\"table\":\"T\"}"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("권한이 없습니다."));

        var events = await RunAsync(Create(gate: gate), Request(NewSession()));

        var result = events.OfType<AiToolResult>().Single();
        Assert.Equal((AiToolJudgement.Denied, "level_too_low"), (result.Judgement, result.Note));
        Assert.Contains("level_too_low", (string?)_server.Requests[1].Messages[^1]!["content"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gate_exception_is_a_tool_failure_that_does_not_leak_the_message_and_a_gate_timeout_is_not_a_cancellation()
    {
        _gate.Throw = true;
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", "describe_object", "{}"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("끝"));
        var thrown = await RunAsync(Create(), Request(NewSession()));

        _gate.Throw = false;
        _gate.ThrowCancel = true;
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_2", "describe_object", "{}"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("끝"));
        var timedOut = await RunAsync(Create(), Request(NewSession()));

        foreach (var events in new[] { thrown, timedOut })
        {
            Assert.Equal(AiToolJudgement.Failed, events.OfType<AiToolResult>().Single().Judgement);
            Assert.IsType<AiFinal>(events.Last());
        }

        Assert.DoesNotContain(_server.Requests, r => r.Body.Contains("secret detail", StringComparison.Ordinal));
        Assert.DoesNotContain(thrown, e => e.ToString()!.Contains("secret detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_or_revoked_session_closes_before_any_request_and_revocation_between_rounds_stops_the_loop()
    {
        var driver = Create();
        var revoked = NewSession();
        revoked.Revoke();

        Assert.Equal("session_required", CodeOf(await RunAsync(driver, Request(null))));
        Assert.Equal("session_revoked", CodeOf(await RunAsync(driver, Request(revoked))));
        Assert.Empty(_server.Requests);

        var session = NewSession();
        var gate = new StubGate(new AgentGateResponse(new AgentCallToolResult(false, "{}"), AiToolJudgement.Allowed)) { OnCall = session.Revoke };
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", "describe_object", "{}"));

        var events = await RunAsync(Create(gate: gate), Request(session));

        Assert.Equal("session_revoked", CodeOf(events));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task The_tool_call_and_model_call_limits_end_with_turn_limit()
    {
        var driver = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxModelCalls: 40, MaxToolCalls: 2));
        for (var i = 0; i < 3; i++)
        {
            _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn($"call_{i}", "describe_object", "{}"));
        }

        var tools = await RunAsync(driver, Request(NewSession()));

        Assert.Equal("turn_limit", CodeOf(tools));
        Assert.Contains("도구", ((AiError)tools.Last()).Message, StringComparison.Ordinal);
        Assert.Equal(2, tools.OfType<AiToolResult>().Count());

        var models = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxModelCalls: 2));
        for (var i = 0; i < 2; i++)
        {
            _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn($"loop_{i}", "describe_object", "{}"));
        }

        var loop = await RunAsync(models, Request(NewSession()));

        Assert.Equal("turn_limit", CodeOf(loop));
        Assert.Contains("모델", ((AiError)loop.Last()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profiles_the_api_path_cannot_run_are_closed_before_any_request()
    {
        var driver = Create();

        Assert.Equal("profile_invalid", CodeOf(await RunAsync(driver, Request(NewSession()), new AiRoute(null, null))));
        Assert.Equal("profile_invalid", CodeOf(await RunAsync(driver, Request(NewSession()), Route(Profile with { Format = AiApiFormat.Anthropic }))));
        Assert.Equal("profile_invalid_url", CodeOf(await RunAsync(driver, Request(NewSession()), Route(Profile with { BaseUrl = "ftp://llm.example" }))));
        Assert.Empty(_server.Requests);
    }

    // ---------------------------------------------------------------- 구조 결과·이어 말하기·한도 (api3)

    [Fact]
    public async Task Structured_output_is_a_hidden_synthetic_tool_whose_arguments_become_the_result()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_s", "StructuredOutput", "{\"summary\":\"인덱스를 추가하세요\"}", 20, 8));
        var driver = Create();

        var events = await RunAsync(driver, Request(NewSession(), schema: Schema));

        Assert.DoesNotContain(events, e => e is AiToolCall or AiToolResult);
        var final = events.OfType<AiFinal>().Single();
        Assert.Null(final.Text);
        Assert.Equal("인덱스를 추가하세요", final.Result!.Value.GetProperty("summary").GetString());
        var tools = _server.Requests[0].Json["tools"]!.AsArray();
        var synthetic = Assert.Single(tools, t => (string?)t!["function"]!["name"] == "StructuredOutput")!;
        Assert.Equal("object", (string?)synthetic["function"]!["parameters"]!["type"]);
        Assert.Equal(new AiUsage(20, 8, null), events.OfType<AiUsage>().Single());

        // 이어 말할 때 StructuredOutput 호출에도 짝이 되는 도구 메시지가 있어야 한다(엄격한 서버가 거부하지 않게).
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("더 있나요?"));
        await RunAsync(driver, Request(NewSession(), resumeId: final.ResumeId));
        var history = _server.Requests[1].Messages;
        Assert.Equal(["system", "user", "assistant", "tool", "user"], history.Select(m => (string?)m!["role"]));
        Assert.Equal("call_s", (string?)history[3]!["tool_call_id"]);
    }

    [Fact]
    public async Task A_text_only_answer_gets_one_re_request_and_then_structured_output_missing()
    {
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("글로 답했습니다"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_s", "StructuredOutput", "{\"summary\":\"x\"}"));
        var recovered = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.NotNull(recovered.OfType<AiFinal>().Single().Result);
        var retry = _server.Requests[1].Messages[^1]!;
        Assert.Equal("user", (string?)retry["role"]);
        Assert.Contains("StructuredOutput", (string?)retry["content"], StringComparison.Ordinal);

        _server.EnqueueSse(FakeOpenAiServer.TextTurn("또 글"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("또 글"));
        var missing = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.Equal("structured_output_missing", CodeOf(missing));
        Assert.Equal(4, _server.Requests.Count);
    }

    [Fact]
    public async Task Unreadable_structured_arguments_are_returned_to_the_model_instead_of_ending_the_run()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_s", "StructuredOutput", "{\"summary\":"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_t", "StructuredOutput", "{\"summary\":\"다시\"}"));

        var events = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.Equal("다시", events.OfType<AiFinal>().Single().Result!.Value.GetProperty("summary").GetString());
        Assert.Contains("invalid_arguments", (string?)_server.Requests[1].Messages[^1]!["content"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resuming_continues_the_same_messages_and_unknown_or_busy_ids_are_refused()
    {
        var driver = Create();
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("첫 답"));
        var first = await RunAsync(driver, Request(NewSession(), "첫 질문"));
        var id = first.OfType<AiFinal>().Single().ResumeId;

        _server.EnqueueSse(FakeOpenAiServer.TextTurn("둘째 답"));
        var second = await RunAsync(driver, Request(NewSession(), "둘째 질문", id));

        Assert.Equal(id, Assert.IsType<AiStarted>(second[0]).ResumeId);
        Assert.Equal(["system", "user", "assistant", "user"], _server.Requests[1].Messages.Select(m => (string?)m!["role"]));
        Assert.Equal(["첫 질문", "첫 답", "둘째 질문"], _server.Requests[1].Messages.Skip(1).Select(m => (string?)m!["content"]));

        Assert.Equal("resume_unknown", CodeOf(await RunAsync(driver, Request(NewSession(), resumeId: "api-nope"))));
    }

    [Fact]
    public async Task A_conversation_started_for_one_server_is_never_resumed_against_another()
    {
        var driver = Create();
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("첫 답"));
        var first = await RunAsync(driver, Request(NewSession(), "SELECT 이 들어 있는 첫 질문"));
        var id = first.OfType<AiFinal>().Single().ResumeId;
        var sent = _server.Requests.Count;

        var otherProfile = await RunAsync(driver, Request(NewSession(), resumeId: id), Route(Profile with { Id = Guid.NewGuid() }));
        var movedAddress = await RunAsync(driver, Request(NewSession(), resumeId: id), Route(Profile with { BaseUrl = "http://other.example:8080" }));

        Assert.Equal("resume_route_mismatch", CodeOf(otherProfile));
        Assert.Equal("resume_route_mismatch", CodeOf(movedAddress));
        Assert.Equal(sent, _server.Requests.Count);

        // 모델 이름만 바뀌거나 주소 끝의 /v1 유무만 다른 같은 서버면 이어 간다.
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("이어서"));
        var same = await RunAsync(driver, Request(NewSession(), resumeId: id), Route(Profile with { Model = "other-model", BaseUrl = "http://llm.example:8080/v1" }));
        Assert.IsType<AiFinal>(same.Last());
    }

    [Fact]
    public async Task A_failed_first_turn_leaves_the_id_usable_and_a_running_conversation_refuses_a_second_run()
    {
        var driver = Create();
        _server.Enqueue(FakeOpenAiServer.Error(HttpStatusCode.InternalServerError, "{}"));
        var failed = await RunAsync(driver, Request(NewSession(), "실패할 질문"));
        var id = Assert.IsType<AiStarted>(failed[0]).ResumeId;

        _server.EnqueueSse(FakeOpenAiServer.TextTurn("이제 됨"));
        var retried = await RunAsync(driver, Request(NewSession(), "다시", id));
        Assert.IsType<AiFinal>(retried.Last());

        // 실패한 실행의 질문(SQL 이 든 첫 글)이 남아 있고, 사용자 메시지가 연달아 오지 않도록 사이에 assistant 메시지가 낀다.
        var resent = _server.Requests[1].Messages;
        Assert.Equal(["system", "user", "assistant", "user"], resent.Select(m => (string?)m!["role"]));
        Assert.Equal("실패할 질문", (string?)resent[1]!["content"]);
        Assert.Equal("다시", (string?)resent[3]!["content"]);

        _server.EnqueueHanging(FakeOpenAiServer.Text("진행 중"));
        using var cts = new CancellationTokenSource();
        var running = driver.RunAsync(Request(NewSession(), "오래 걸림", id), Route(), cts.Token).GetAsyncEnumerator(cts.Token);
        await running.MoveNextAsync();
        await running.MoveNextAsync();

        Assert.Equal("resume_busy", CodeOf(await RunAsync(driver, Request(NewSession(), "끼어들기", id))));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.MoveNextAsync());
        await running.DisposeAsync();
    }

    [Fact]
    public async Task Prompt_tokens_over_the_profile_limit_block_the_next_resume_but_not_the_current_answer()
    {
        var driver = Create();
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("긴 대화의 답", prompt: 5000, completion: 10));

        var first = await RunAsync(driver, Request(NewSession()));
        var blocked = await RunAsync(driver, Request(NewSession(), resumeId: first.OfType<AiFinal>().Single().ResumeId));

        Assert.IsType<AiFinal>(first.Last());
        Assert.Equal("context_limit", CodeOf(blocked));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task The_message_count_cap_and_the_conversation_cap_are_enforced()
    {
        var small = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxMessages: 3, MaxConversations: 2));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("a"));
        var first = await RunAsync(small, Request(NewSession()));
        var firstId = first.OfType<AiFinal>().Single().ResumeId;

        Assert.Equal("context_limit", CodeOf(await RunAsync(small, Request(NewSession(), resumeId: firstId))));

        // 보관 대화가 상한을 넘으면 오래 안 쓴 것부터 버려 모르는 id 가 된다.
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("b"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("c"));
        var second = await RunAsync(small, Request(NewSession()));
        var third = await RunAsync(small, Request(NewSession()));
        Assert.IsType<AiFinal>(second.Last());
        Assert.IsType<AiFinal>(third.Last());
        Assert.Equal("resume_unknown", CodeOf(await RunAsync(small, Request(NewSession(), resumeId: firstId))));
    }

    // ---------------------------------------------------------------- 오류·취소·키 (api4)

    [Fact]
    public async Task Http_errors_get_distinct_codes_and_never_echo_the_key()
    {
        var driver = Create();
        _server.Enqueue(FakeOpenAiServer.Error(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = new JsonObject { ["message"] = $"Incorrect API key provided: {Key}. Authorization: Bearer {Key}" } }.ToJsonString()));
        _server.Enqueue(FakeOpenAiServer.Error(HttpStatusCode.TooManyRequests, "{\"error\":\"slow down\"}"));
        _server.Enqueue(FakeOpenAiServer.Error(HttpStatusCode.InternalServerError, "plain text failure"));
        var moved = new HttpResponseMessage(HttpStatusCode.Found);
        moved.Headers.Location = new Uri("https://other.example/v1/chat/completions?token=" + Key + "&x=1");
        _server.Enqueue(moved);

        var auth = await RunAsync(driver, Request(NewSession()), Route(key: Key));
        var rate = await RunAsync(driver, Request(NewSession()), Route(key: Key));
        var server = await RunAsync(driver, Request(NewSession()), Route(key: Key));
        var redirect = await RunAsync(driver, Request(NewSession()), Route(key: Key));

        Assert.Equal(["api_auth_failed", "api_rate_limited", "api_http_error", "api_redirect"], new[] { auth, rate, server, redirect }.Select(CodeOf));
        Assert.Contains("***", ((AiError)auth.Last()).Message, StringComparison.Ordinal);
        Assert.Contains("slow down", ((AiError)rate.Last()).Message, StringComparison.Ordinal);
        Assert.Contains("500", ((AiError)server.Last()).Message, StringComparison.Ordinal);
        Assert.Contains("https://other.example/v1/chat/completions?token=***&x=***", ((AiError)redirect.Last()).Message, StringComparison.Ordinal);
        foreach (var events in new[] { auth, rate, server, redirect })
        {
            Assert.DoesNotContain(events, e => e.ToString()!.Contains(Key, StringComparison.Ordinal) || e.ToString()!.Contains("0123456789", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_key_that_straddles_the_excerpt_limit_is_still_masked()
    {
        _server.Enqueue(FakeOpenAiServer.Error(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = new JsonObject { ["message"] = new string('x', 290) + " key=" + Key } }.ToJsonString()));

        var events = await RunAsync(Create(), Request(NewSession()), Route(key: Key));

        Assert.Equal("api_auth_failed", CodeOf(events));
        Assert.DoesNotContain(events, e => e.ToString()!.Contains("sk-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_connect_timeout_inside_the_handler_is_a_connection_failure_not_a_silent_server()
    {
        _server.Enqueue(_ => throw new TaskCanceledException("connect", new TimeoutException()));

        Assert.Equal("api_connect_failed", CodeOf(await RunAsync(Create(), Request(NewSession()))));
    }

    [Fact]
    public async Task The_production_handler_does_not_follow_redirects()
    {
        using var handler = OpenAiApiDriver.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);

        // 프로필 경로는 주소와 상관없이 프록시를 거치지 않는다(사용자 결정).
        Assert.False(handler.UseProxy);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_tool_card_is_closed_even_when_the_run_ends_right_after_it()
    {
        var tight = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxMessages: 3));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_2", "describe_object", "{}"));
        var limited = await RunAsync(tight, Request(NewSession()));
        Assert.Equal("context_limit", CodeOf(limited));
        Assert.Equal(limited.OfType<AiToolCall>().Count(), limited.OfType<AiToolResult>().Count());
    }

    [Fact]
    public async Task A_connection_failure_is_an_error_event_and_the_key_stays_out_of_every_event_and_the_body()
    {
        _server.Enqueue(_ => throw new HttpRequestException($"Connection refused (llm.example:8080) token={Key}"));
        var failed = await RunAsync(Create(), Request(NewSession()), Route(key: Key));

        Assert.Equal("api_connect_failed", CodeOf(failed));
        Assert.DoesNotContain(failed, e => e.ToString()!.Contains(Key, StringComparison.Ordinal));

        _server.EnqueueSse(FakeOpenAiServer.TextTurn("정상"));
        var ok = await RunAsync(Create(), Request(NewSession()), Route(key: Key));

        Assert.DoesNotContain(ok, e => e.ToString()!.Contains(Key, StringComparison.Ordinal));
        Assert.DoesNotContain(_server.Requests, r => r.Body.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_silent_server_ends_with_api_timeout_before_and_after_the_headers()
    {
        var options = new OpenAiApiOptions(TimeSpan.FromMilliseconds(300));
        _server.EnqueueAsync(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var noHeaders = await RunAsync(Create(options), Request(NewSession()));

        _server.EnqueueHanging(FakeOpenAiServer.Text("여기까지"));
        var noBody = await RunAsync(Create(options), Request(NewSession()));

        Assert.Equal("api_timeout", CodeOf(noHeaders));
        Assert.Equal("api_timeout", CodeOf(noBody));
        Assert.Contains(noBody, e => e is AiTextDelta { Text: "여기까지" });
    }

    [Fact]
    public async Task Malformed_truncated_and_server_reported_failures_are_error_events()
    {
        var driver = Create();
        _server.Enqueue(FakeOpenAiServer.Sse(["not json"]));
        _server.Enqueue(FakeOpenAiServer.Sse([FakeOpenAiServer.Text("반쯤")], done: false));
        _server.EnqueueSse("""{"error":{"message":"context length exceeded"}}""");
        _server.EnqueueSse(FakeOpenAiServer.Text("잘린"), FakeOpenAiServer.Finish("length"));

        var garbage = await RunAsync(driver, Request(NewSession()));
        var cut = await RunAsync(driver, Request(NewSession()));
        var reported = await RunAsync(driver, Request(NewSession()));
        var length = await RunAsync(driver, Request(NewSession()));

        Assert.Equal(["api_bad_response", "api_bad_response", "api_error", "output_truncated"], new[] { garbage, cut, reported, length }.Select(CodeOf));
        Assert.Contains("context length exceeded", ((AiError)reported.Last()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_server_or_reading_the_stream_or_running_a_tool_throws_cancellation()
    {
        var driver = Create();

        // 서버 응답 대기 중
        using (var cts = new CancellationTokenSource())
        {
            _server.EnqueueAsync(async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(driver, Request(NewSession()), ct: cts.Token));
        }

        // 스트림 읽는 도중
        using (var cts = new CancellationTokenSource())
        {
            _server.EnqueueHanging(FakeOpenAiServer.Text("읽는 중"));
            var events = new List<AiEvent>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var e in driver.RunAsync(Request(NewSession()), Route(), cts.Token))
                {
                    events.Add(e);
                    if (e is AiTextDelta)
                    {
                        // 읽기가 이미 멈춰 기다리는 중에 취소가 들어오게 늦춘다.
                        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
                    }
                }
            });
            Assert.Contains(events, e => e is AiTextDelta);
        }

        // 도구 실행 중
        using (var cts = new CancellationTokenSource())
        {
            _gate.Block = new TaskCompletionSource();
            _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("call_1", "describe_object", "{}"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var e in driver.RunAsync(Request(NewSession()), Route(), cts.Token))
                {
                    if (e is AiToolCall)
                    {
                        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
                    }
                }
            });
        }
    }

    [Fact]
    public async Task Revoking_the_session_while_the_stream_is_open_closes_the_run_with_session_revoked()
    {
        var session = NewSession();
        _server.EnqueueHanging(FakeOpenAiServer.Text("진행"));
        var events = new List<AiEvent>();

        await foreach (var e in Create().RunAsync(Request(session), Route(), CancellationToken.None))
        {
            events.Add(e);
            if (e is AiTextDelta)
            {
                session.Revoke();
            }
        }

        Assert.Equal("session_revoked", CodeOf(events));
    }

    // ---------------------------------------------------------------- 리뷰 수정: 스키마 검사·상한·키·오류 본문

    [Fact]
    public async Task Structured_arguments_missing_a_required_field_go_back_to_the_model_until_the_schema_is_met()
    {
        // 빈 인자, {} , 필수 칸이 없는 객체는 결과로 받지 않고 도구 오류로 돌려 다시 제출하게 한다.
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "StructuredOutput", string.Empty));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c2", "StructuredOutput", "{}"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c4", "StructuredOutput", "{\"summary\":\"완성\"}"));

        var events = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.Equal("완성", events.OfType<AiFinal>().Single().Result!.Value.GetProperty("summary").GetString());
        Assert.Equal(3, _server.Requests.Count);
        for (var i = 1; i <= 2; i++)
        {
            var content = (string?)_server.Requests[i].Messages[^1]!["content"];
            Assert.Contains("invalid_arguments", content, StringComparison.Ordinal);
            Assert.Contains("summary", content, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(events, e => e is AiToolCall or AiToolResult);
    }

    [Fact]
    public async Task Structured_arguments_sent_as_a_json_object_are_kept()
    {
        var call = new JsonObject
        {
            ["index"] = 0,
            ["id"] = "c1",
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "StructuredOutput", ["arguments"] = new JsonObject { ["summary"] = "객체로 옴" } },
        };
        _server.EnqueueSse(FakeOpenAiServer.Chunk(new JsonObject { ["tool_calls"] = new JsonArray(call) }), FakeOpenAiServer.Finish("tool_calls"));

        var events = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.Equal("객체로 옴", events.OfType<AiFinal>().Single().Result!.Value.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task The_synthetic_structured_tool_does_not_spend_the_tool_call_limit()
    {
        var driver = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxToolCalls: 1));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "describe_object", "{}"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c2", "StructuredOutput", "{\"summary\":\"ok\"}"));

        var events = await RunAsync(driver, Request(NewSession(), schema: Schema));

        Assert.Equal("ok", events.OfType<AiFinal>().Single().Result!.Value.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Reaching_the_message_cap_keeps_the_answer_already_received_and_only_blocks_the_next_turn()
    {
        var text = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxMessages: 2));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("받은 답"));

        var answered = await RunAsync(text, Request(NewSession()));

        Assert.DoesNotContain(answered, e => e is AiError);
        var final = answered.OfType<AiFinal>().Single();
        Assert.Equal("받은 답", final.Text);
        Assert.Equal("context_limit", CodeOf(await RunAsync(text, Request(NewSession(), resumeId: final.ResumeId))));

        // 구조 결과도 같다: 제출된 결과는 상한 때문에 버려지지 않는다.
        var structured = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxMessages: 3));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "StructuredOutput", "{\"summary\":\"보존\"}"));

        var result = await RunAsync(structured, Request(NewSession(), schema: Schema));

        Assert.Equal("보존", result.OfType<AiFinal>().Single().Result!.Value.GetProperty("summary").GetString());
        Assert.DoesNotContain(result, e => e is AiError);
    }

    [Fact]
    public async Task Hitting_the_cap_after_a_tool_round_stops_before_the_next_request()
    {
        var driver = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxMessages: 3));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "describe_object", "{}"));

        var events = await RunAsync(driver, Request(NewSession()));

        Assert.Equal("context_limit", CodeOf(events));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Five_images_of_five_megabytes_fit_and_a_too_large_first_request_is_not_called_a_long_conversation()
    {
        var driver = Create();
        var image = new AiImageAttachment("image/png", new byte[5 * 1024 * 1024]);
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("다섯 장 봄"));

        var five = await RunAsync(driver, Request(NewSession(), images: [image, image, image, image, image]));

        Assert.IsType<AiFinal>(five.Last());
        Assert.Single(_server.Requests);

        // 상한(약 36 MB)을 넘는 첫 요청: 요청은 나가지 않고, 안내는 대화가 길다가 아니라 요청(이미지)이 크다고 말한다.
        var seven = await RunAsync(driver, Request(NewSession(), images: [image, image, image, image, image, image, image]));

        var error = Assert.IsType<AiError>(seven.Last());
        Assert.Equal("context_limit", error.Code);
        Assert.DoesNotContain("대화가 너무 길어졌습니다", error.Message, StringComparison.Ordinal);
        Assert.Contains("이미지", error.Message, StringComparison.Ordinal);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public void The_default_limits_are_the_documented_ones()
    {
        var options = new OpenAiApiOptions();

        Assert.Equal(40, options.MaxModelCalls);
        Assert.Equal(60, options.MaxToolCalls);
        Assert.Equal(400, options.MaxMessages);
        Assert.Equal(36L * 1024 * 1024, options.MaxBytes);
        Assert.Equal(12, options.MaxConversations);
    }

    [Fact]
    public async Task A_key_with_a_line_break_is_an_error_event_without_a_request_and_without_the_key()
    {
        const string badKey = "sk-abc\ndef-secret";

        var events = await RunAsync(Create(), Request(NewSession()), Route(key: badKey));

        var error = Assert.IsType<AiError>(events.Last());
        Assert.Equal("api_key_invalid", error.Code);
        Assert.Empty(_server.Requests);
        Assert.DoesNotContain(events, e => e.ToString()!.Contains("def-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bogus-9")]
    [InlineData("euc-kr")]
    [InlineData("ks_c_5601-1987")]
    public async Task An_error_body_with_an_unreadable_charset_still_becomes_an_http_error(string charset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new ByteArrayContent("server exploded"u8.ToArray()) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", $"text/plain; charset={charset}");
        _server.Enqueue(response);

        var events = await RunAsync(Create(), Request(NewSession()), Route(key: Key));

        Assert.Equal("api_http_error", CodeOf(events));
        Assert.Contains("500", ((AiError)events.Last()).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://admin:hunter2@other.example/v1/chat/completions")]
    [InlineData("//admin:hunter2@other.example/v1/chat/completions")]
    [InlineData("admin:hunter2@other.example/v1/chat/completions")]
    [InlineData("http:admin:hunter2@other.example/v1/chat/completions")]
    [InlineData("///admin:hunter2@other.example/v1/chat/completions")]
    [InlineData("/v1/chat/completions?hunter2")]
    public async Task A_redirect_target_never_shows_user_info(string location)
    {
        var moved = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        moved.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        _server.Enqueue(moved);

        var events = await RunAsync(Create(), Request(NewSession()));

        var error = Assert.IsType<AiError>(events.Last());
        Assert.Equal("api_redirect", error.Code);
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", error.Message, StringComparison.Ordinal);
        Assert.Contains("/v1/chat/completions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_echoed_by_the_model_is_masked_in_the_text_events_and_the_final_answer()
    {
        _server.EnqueueSse(FakeOpenAiServer.TextTurn($"키는 {Key} 이고 헤더는 Bearer {Key}"));

        var events = await RunAsync(Create(), Request(NewSession()), Route(key: Key));

        Assert.DoesNotContain(events, e => e.ToString()!.Contains("sk-secret", StringComparison.Ordinal));
        Assert.Contains("***", events.OfType<AiFinal>().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_keys_in_structured_arguments_are_returned_to_the_model_not_thrown()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "StructuredOutput", "{\"summary\":\"a\",\"summary\":\"b\"}"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c2", "StructuredOutput", "{\"summary\":\"c\"}"));

        var events = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.IsType<AiFinal>(events.Last());
    }

    [Fact]
    public async Task A_short_placeholder_key_does_not_mangle_the_answer_text()
    {
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("none of it is nonexistent. Bearer 토큰을 쓰세요"));

        var events = await RunAsync(Create(), Request(NewSession()), Route(key: "none"));

        Assert.Equal("none of it is nonexistent. Bearer 토큰을 쓰세요", events.OfType<AiFinal>().Single().Text);
    }

    [Fact]
    public async Task Korean_answers_are_not_inflated_by_escaping_in_the_stored_assistant_message()
    {
        var driver = Create();
        _server.EnqueueSse(FakeOpenAiServer.TextTurn(new string('가', 1000)));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("다음"));

        var first = await RunAsync(driver, Request(NewSession()));
        await RunAsync(driver, Request(NewSession(), resumeId: first.OfType<AiFinal>().Single().ResumeId));

        // 두 번째 요청 본문에 실린 assistant 메시지가 한글을 이스케이프 없이 담는다(1,000자가 6,000자로 부풀지 않는다).
        var body = _server.Requests[1].Body;
        Assert.Contains(new string('가', 1000), body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 두 번째 리뷰 수정: 안쪽 스키마·반복 실패·게이트 경계

    private static readonly JsonElement TuningSchema = Oranda.Core.Ai.Schemas.TuningResult.Schema();

    private const string ValidTuning = """{"summary":"s","rewrites":[{"label":"a","sql":"select 1 from dual","rationale":"r"}],"index_suggestions":[{"action":"create","table":"T","columns":["C"],"ddl":"CREATE INDEX I ON T(C)","rationale":"r"}],"evidence":[{"tool":"t","finding":"f"}]}""";

    [Theory]
    [InlineData("""{"summary":"s","rewrites":[{"label":"a","sql":"select 2 from dual"}],"index_suggestions":[],"evidence":[]}""", "rewrites[0].rationale")]
    [InlineData("""{"summary":"s","rewrites":[{}],"index_suggestions":[],"evidence":[]}""", "rewrites[0].label")]
    [InlineData("""{"summary":"s","rewrites":[null],"index_suggestions":[],"evidence":[]}""", "rewrites[0]")]
    [InlineData("""{"summary":"s","rewrites":"[]","index_suggestions":[],"evidence":[]}""", "rewrites")]
    [InlineData("""{"summary":"s","rewrites":[],"index_suggestions":[{"action":"create","table":"T","columns":["C"]}],"evidence":[]}""", "index_suggestions[0].ddl")]
    [InlineData("""{"summary":"s","rewrites":[],"index_suggestions":[{"action":"drop","table":"T","columns":["C"],"ddl":"x","rationale":"r"}],"evidence":[]}""", "index_suggestions[0].action")]
    [InlineData("""{"summary":"s","rewrites":[],"index_suggestions":[],"evidence":[{"tool":"t","finding":7}]}""", "evidence[0].finding")]
    public async Task Inner_schema_mismatches_of_the_real_tuning_schema_go_back_to_the_model_with_the_path(string bad, string path)
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "StructuredOutput", bad));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c2", "StructuredOutput", ValidTuning));

        var events = await RunAsync(Create(), Request(NewSession(), schema: TuningSchema));

        var final = events.OfType<AiFinal>().Single();
        Assert.NotNull(Oranda.Core.Ai.Schemas.TuningResult.Parse(final.Result!.Value));
        Assert.Contains(path, (string?)_server.Requests[1].Messages[^1]!["content"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_is_accepted_only_where_the_schema_type_allows_it()
    {
        var schema = JsonDocument.Parse("""{"type":"object","required":["a","b"],"properties":{"a":{"type":["string","null"]},"b":{"type":"string"}}}""").RootElement.Clone();
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "StructuredOutput", """{"a":null,"b":null}"""));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c2", "StructuredOutput", """{"a":null,"b":"x"}"""));

        var events = await RunAsync(Create(), Request(NewSession(), schema: schema));

        Assert.IsType<AiFinal>(events.Last());
        Assert.Equal(2, _server.Requests.Count);
        Assert.Contains("b", (string?)_server.Requests[1].Messages[^1]!["content"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Three_failed_submissions_in_a_row_end_the_run_with_a_dedicated_code_that_names_the_path()
    {
        for (var i = 0; i < 3; i++)
        {
            _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn($"c{i}", "StructuredOutput", """{"summary":"s","rewrites":[{}],"index_suggestions":[],"evidence":[]}"""));
        }

        var events = await RunAsync(Create(), Request(NewSession(), schema: TuningSchema));

        var error = Assert.IsType<AiError>(events.Last());
        Assert.Equal("structured_output_invalid", error.Code);
        Assert.Contains("rewrites[0].label", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, _server.Requests.Count);
    }

    [Fact]
    public async Task Arguments_with_a_lone_surrogate_are_returned_to_the_model_not_thrown()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "StructuredOutput", "{\"summary\":\"\\ud800\"}"));
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c2", "StructuredOutput", "{\"summary\":\"ok\"}"));

        var events = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.IsType<AiFinal>(events.Last());
        Assert.Contains("invalid_arguments", (string?)_server.Requests[1].Messages[^1]!["content"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_huge_error_body_is_clipped_and_still_an_http_error()
    {
        _server.Enqueue(FakeOpenAiServer.Error(HttpStatusCode.InternalServerError, new string('x', 6 * 1024 * 1024)));

        var events = await RunAsync(Create(), Request(NewSession()));

        var error = Assert.IsType<AiError>(events.Last());
        Assert.Equal("api_http_error", error.Code);
        Assert.True(error.Message.Length < 1000);
    }

    [Theory]
    [InlineData("content_filter", "content_filtered")]
    [InlineData("error", "api_error")]
    public async Task Error_finish_reasons_are_not_taken_as_a_normal_answer(string reason, string code)
    {
        _server.EnqueueSse(FakeOpenAiServer.Text("부분"), FakeOpenAiServer.Finish(reason));

        var events = await RunAsync(Create(), Request(NewSession()));

        Assert.Equal(code, CodeOf(events));
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task A_conversation_emptied_by_the_size_cap_says_so_instead_of_blaming_the_token_limit()
    {
        var driver = Create(new OpenAiApiOptions(TimeSpan.FromSeconds(30), MaxMessages: 2));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("a"));
        var first = await RunAsync(driver, Request(NewSession()));

        var again = await RunAsync(driver, Request(NewSession(), resumeId: first.OfType<AiFinal>().Single().ResumeId));

        Assert.Equal("context_limit", CodeOf(again));
        Assert.DoesNotContain("입력 토큰", ((AiError)again.Last()).Message, StringComparison.Ordinal);
    }

    private static string TwoCalls(string firstName, string firstArgs, string secondName, string secondArgs)
    {
        static JsonObject Call(int index, string id, string name, string args) => new()
        {
            ["index"] = index,
            ["id"] = id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args },
        };

        return FakeOpenAiServer.Chunk(new JsonObject { ["tool_calls"] = new JsonArray(Call(0, "k1", firstName, firstArgs), Call(1, "k2", secondName, secondArgs)) });
    }

    [Fact]
    public async Task Without_an_output_schema_a_structured_output_call_is_an_ordinary_tool_that_goes_to_the_gate()
    {
        _server.EnqueueSse(FakeOpenAiServer.ToolCallTurn("c1", "StructuredOutput", "{\"summary\":\"x\"}"));
        _server.EnqueueSse(FakeOpenAiServer.TextTurn("끝"));

        var events = await RunAsync(Create(), Request(NewSession()));

        Assert.Equal("StructuredOutput", _gate.LastTool);
        Assert.Contains(events, e => e is AiToolCall { ToolName: "StructuredOutput" });
        Assert.Null(events.OfType<AiFinal>().Single().Result);
    }

    [Fact]
    public async Task Calls_after_a_submitted_result_in_the_same_message_are_not_executed()
    {
        _server.EnqueueSse(TwoCalls("StructuredOutput", "{\"summary\":\"ok\"}", "describe_object", "{}"), FakeOpenAiServer.Finish("tool_calls"));

        var events = await RunAsync(Create(), Request(NewSession(), schema: Schema));

        Assert.Null(_gate.LastTool);
        Assert.NotNull(events.OfType<AiFinal>().Single().Result);
        Assert.DoesNotContain(events, e => e is AiToolCall or AiToolResult);
    }

    // ---------------------------------------------------------------- 등록

    [Fact]
    public void The_driver_is_registered_as_the_api_path_and_the_only_constructor_the_container_can_satisfy_is_the_gate_one()
    {
        var registered = typeof(PlatformAssembly).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<OrandaServiceAttribute>().Select(a => (Type: t, a.ServiceType)))
            .Where(r => r.ServiceType == typeof(IApiAiDriver))
            .ToList();

        Assert.Equal(typeof(OpenAiApiDriver), Assert.Single(registered).Type);

        // 앱 컨테이너는 등록된 형식(여기서는 IAgentGate 뿐)으로 모두 채울 수 있는 생성자 중 인자가 가장 많은 것을 쓴다.
        var satisfiable = typeof(OpenAiApiDriver)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => c.GetParameters().All(p => p.ParameterType == typeof(IAgentGate)))
            .ToList();
        var ctor = Assert.Single(satisfiable);
        Assert.True(ctor.IsPublic);
    }

    private sealed class StubGate(AgentGateResponse response) : IAgentGate
    {
        public Action? OnCall { get; set; }

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) =>
            [new AgentToolInfo("describe_object", "describe", new JsonObject { ["type"] = "object" })];

        public Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            OnCall?.Invoke();
            return Task.FromResult(response);
        }
    }
}
