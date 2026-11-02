using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;
using Oranda.Platform.Ai.Profiles;
using Oranda.Platform.Tests.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Profiles;

public sealed class AiProfileProbeTests
{
    private const string Key = "sk-probe-test-key-ABCDEFG123456";

    // 사내 서버 실측 응답 형태(주소 없음).
    private const string StrataModels =
        """{"object":"list","data":[{"id":"model-example-q4","object":"model","status":{"value":"loaded"},"meta":{"n_ctx":262144},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}}]}""";

    [Fact]
    public async Task OpenAi_format_lists_models_and_reads_image_support_from_architecture()
    {
        using var server = new StubServer(_ => Json(StrataModels));
        using var probe = server.Probe();

        var result = await probe.ListModelsAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example:8080"), CancellationToken.None);

        Assert.True(result.Success);
        var model = Assert.Single(result.Models);
        Assert.Equal("model-example-q4", model.Id);
        Assert.True(model.SupportsImages);
        var call = Assert.Single(server.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("http://llm.example:8080/v1/models", call.Url);
        Assert.Null(call.Authorization); // 키 없는 서버: 인증 헤더를 보내지 않는다
    }

    [Fact]
    public async Task Image_support_is_false_when_listed_without_image_and_null_when_the_field_is_absent()
    {
        using var server = new StubServer(_ => Json("""{"data":[{"id":"text-only","architecture":{"input_modalities":["text"]}},{"id":"unknown"},{"id":"vision","architecture":{"input_modalities":["IMAGE"]}},{"id":"text-only"},{"object":"no-id"}]}"""));
        using var probe = server.Probe();

        var result = await probe.ListModelsAsync(Request(AiProfilePreset.OpenAi, AiApiFormat.OpenAi, "https://api.example/v1", Key), CancellationToken.None);

        Assert.Equal([("text-only", false), ("unknown", (bool?)null), ("vision", true)], result.Models.Select(m => (m.Id, m.SupportsImages)).ToArray());
        Assert.Equal("Bearer " + Key, server.Calls[0].Authorization);
        Assert.Equal("https://api.example/v1/models", server.Calls[0].Url);
    }

    [Fact]
    public async Task Anthropic_preset_uses_x_api_key_and_other_anthropic_format_profiles_use_bearer()
    {
        using var server = new StubServer(_ => Json("""{"data":[{"id":"claude-x"}]}"""));
        using var probe = server.Probe();

        await probe.ListModelsAsync(Request(AiProfilePreset.Anthropic, AiApiFormat.Anthropic, "https://api.example/v1/", Key), CancellationToken.None);
        await probe.ListModelsAsync(Request(AiProfilePreset.ZAi, AiApiFormat.Anthropic, "https://api.example/api/anthropic", Key), CancellationToken.None);
        await probe.ListModelsAsync(Request(AiProfilePreset.Custom, AiApiFormat.Anthropic, "http://llm.example:8080", null), CancellationToken.None);

        var anthropic = server.Calls[0];
        Assert.Equal("https://api.example/v1/models", anthropic.Url);
        Assert.Equal(Key, anthropic.ApiKeyHeader);
        Assert.Null(anthropic.Authorization);
        Assert.Equal("2023-06-01", anthropic.AnthropicVersion);

        var zai = server.Calls[1];
        Assert.Equal("https://api.example/api/anthropic/v1/models", zai.Url);
        Assert.Equal("Bearer " + Key, zai.Authorization);
        Assert.Null(zai.ApiKeyHeader);

        Assert.Equal("Bearer local", server.Calls[2].Authorization); // 키 없는 직접 입력은 CLI 와 같은 자리표시 값
    }

    [Fact]
    public async Task Ollama_reads_vision_from_api_tags_on_the_origin_and_tolerates_a_failing_tags_call()
    {
        using var server = new StubServer(call => call.Url.EndsWith("/v1/models", StringComparison.Ordinal)
            ? Json("""{"object":"list","data":[{"id":"llava:7b"},{"id":"model-c:8b"},{"id":"gemma3"},{"id":"plain"}]}""")
            : Json("""{"models":[{"name":"llava:7b","capabilities":["completion","vision"]},{"name":"model-c:8b","capabilities":["completion","tools"]},{"name":"gemma3:latest","capabilities":["vision"]},{"name":"plain"}]}"""));
        using var probe = server.Probe();
        var request = Request(AiProfilePreset.Ollama, AiApiFormat.OpenAi, "http://localhost:11434/v1");

        var result = await probe.ListModelsAsync(request, CancellationToken.None);

        Assert.Equal([("llava:7b", true), ("model-c:8b", false), ("gemma3", true), ("plain", (bool?)null)], result.Models.Select(m => (m.Id, m.SupportsImages)).ToArray());
        Assert.Equal("http://localhost:11434/api/tags", server.Calls[1].Url);

        using var broken = new StubServer(call => call.Url.EndsWith("/v1/models", StringComparison.Ordinal) ? Json("""{"data":[{"id":"llava:7b"}]}""") : new Reply(HttpStatusCode.InternalServerError, "x"));
        using var brokenProbe = broken.Probe();
        var fallback = await brokenProbe.ListModelsAsync(request, CancellationToken.None);
        Assert.True(fallback.Success);
        Assert.Null(Assert.Single(fallback.Models).SupportsImages);
    }

    [Fact]
    public async Task Failed_list_calls_return_results_without_the_key()
    {
        var request = Request(AiProfilePreset.OpenAi, AiApiFormat.OpenAi, "https://api.example/v1", Key);

        using var unauthorized = new StubServer(_ => new Reply(HttpStatusCode.Unauthorized, $$$"""{"error":{"message":"Incorrect API key provided: {{{Key}}}"}}"""));
        var denied = await unauthorized.Probe().ListModelsAsync(request, CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Empty(denied.Models);
        Assert.Contains("401", denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, denied.Message, StringComparison.Ordinal);

        using var echo = new StubServer(_ => new Reply(HttpStatusCode.InternalServerError, $$$"""{"error":{"message":"bad {{{Key}}} here"}}"""));
        var failed = await echo.Probe().ListModelsAsync(request, CancellationToken.None);
        Assert.DoesNotContain(Key, failed.Message, StringComparison.Ordinal);
        Assert.Contains("500", failed.Message, StringComparison.Ordinal);

        using var broken = new StubServer(_ => Json("not json {"));
        Assert.False((await broken.Probe().ListModelsAsync(request, CancellationToken.None)).Success);

        using var wrongShape = new StubServer(_ => Json("""{"hello":"world"}"""));
        Assert.False((await wrongShape.Probe().ListModelsAsync(request, CancellationToken.None)).Success);

        using var redirect = new StubServer(_ => new Reply(HttpStatusCode.Found, string.Empty, "https://other.example/v1/models?token=abc&x=1#frag"));
        var moved = await redirect.Probe().ListModelsAsync(request, CancellationToken.None);
        Assert.False(moved.Success);
        Assert.Single(redirect.Calls); // 따라가지 않는다
        Assert.Equal("서버가 다른 주소로 옮기라고 응답했습니다(https://other.example/v1/models). 프로필 URL 을 확인하세요.", moved.Message);

        using var relative = new StubServer(_ => new Reply(HttpStatusCode.MovedPermanently, string.Empty, "/elsewhere?k=v"));
        var movedTest = await relative.Probe().TestConnectionAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example:8080", Key, "m"), CancellationToken.None);
        Assert.False(movedTest.Success);
        Assert.Contains("(http://llm.example:8080/elsewhere)", movedTest.Message, StringComparison.Ordinal);
        Assert.Single(relative.Calls);

        using var unreachable = new StubServer(_ => throw new HttpRequestException($"connect failed using {Key}"));
        var offline = await unreachable.Probe().ListModelsAsync(request, CancellationToken.None);
        Assert.False(offline.Success);
        Assert.DoesNotContain(Key, offline.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_url_and_unknown_format_fail_without_any_request()
    {
        using var server = new StubServer(_ => Json("{}"));
        using var probe = server.Probe();

        Assert.False((await probe.ListModelsAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "ftp://x"), CancellationToken.None)).Success);
        Assert.False((await probe.ListModelsAsync(Request(AiProfilePreset.Custom, AiApiFormat.Unknown, "http://llm.example"), CancellationToken.None)).Success);
        Assert.False((await probe.TestConnectionAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "not a url", model: "m"), CancellationToken.None)).Success);
        Assert.False((await probe.TestConnectionAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example", model: " "), CancellationToken.None)).Success);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task User_cancellation_is_rethrown_but_a_time_limit_becomes_a_failed_result()
    {
        using var hanging = new StubServer(_ => throw new InvalidOperationException("unused")) { Hang = true };
        using var probe = hanging.Probe(TimeSpan.FromMilliseconds(100));
        var request = Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example", model: "m");

        var list = await probe.ListModelsAsync(request, CancellationToken.None);
        var test = await probe.TestConnectionAsync(request, CancellationToken.None);
        Assert.False(list.Success);
        Assert.False(test.Success);
        Assert.Contains("제한 시간", list.Message, StringComparison.Ordinal);

        using var cts = new CancellationTokenSource();
        using var slow = hanging.Probe(TimeSpan.FromSeconds(30));
        var pending = slow.ListModelsAsync(request, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task OpenAi_connection_test_sends_one_small_request_without_user_text()
    {
        using var server = new StubServer(_ => Json("""{"choices":[{"message":{"content":"pong"}}]}"""));
        using var probe = server.Probe();

        var result = await probe.TestConnectionAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example:8080/", Key, "model-example"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        var call = Assert.Single(server.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("http://llm.example:8080/v1/chat/completions", call.Url);
        Assert.Equal("Bearer " + Key, call.Authorization);
        var body = JsonDocument.Parse(call.Body!).RootElement;
        Assert.Equal("model-example", body.GetProperty("model").GetString());
        Assert.Equal(16, body.GetProperty("max_tokens").GetInt32());
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("ping", message.GetProperty("content").GetString());
        Assert.DoesNotContain(Key, call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_connection_test_retries_once_with_max_completion_tokens_when_the_server_demands_it()
    {
        var count = 0;
        using var server = new StubServer(_ => ++count == 1
            ? new Reply(HttpStatusCode.BadRequest, """{"error":{"message":"Unsupported parameter: 'max_tokens'. Use 'max_completion_tokens' instead."}}""")
            : Json("""{"choices":[]}"""));
        using var probe = server.Probe();

        var result = await probe.TestConnectionAsync(Request(AiProfilePreset.OpenAi, AiApiFormat.OpenAi, "https://api.example/v1", Key, "gpt-x"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, server.Calls.Count);
        var retry = JsonDocument.Parse(server.Calls[1].Body!).RootElement;
        Assert.Equal(16, retry.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(retry.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task Anthropic_connection_test_posts_to_v1_messages_with_the_auth_the_cli_would_use()
    {
        using var server = new StubServer(_ => Json("""{"type":"message","content":[{"type":"text","text":"pong"}]}"""));
        using var probe = server.Probe();

        var direct = await probe.TestConnectionAsync(Request(AiProfilePreset.Anthropic, AiApiFormat.Anthropic, "https://api.example", Key, "claude-x"), CancellationToken.None);
        var zai = await probe.TestConnectionAsync(Request(AiProfilePreset.ZAi, AiApiFormat.Anthropic, "https://api.example/api/anthropic", Key, "glm-x"), CancellationToken.None);

        Assert.True(direct.Success && zai.Success);
        Assert.Equal("https://api.example/v1/messages", server.Calls[0].Url);
        Assert.Equal(Key, server.Calls[0].ApiKeyHeader);
        Assert.Equal("2023-06-01", server.Calls[0].AnthropicVersion);
        Assert.Equal("https://api.example/api/anthropic/v1/messages", server.Calls[1].Url);
        Assert.Equal("Bearer " + Key, server.Calls[1].Authorization);
        Assert.Equal(16, JsonDocument.Parse(server.Calls[0].Body!).RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Failed_connection_test_reports_elapsed_time_and_hides_the_key()
    {
        using var server = new StubServer(_ => new Reply(HttpStatusCode.NotFound, $$$"""{"error":{"message":"model {{{Key}}} not found"}}"""));
        using var probe = server.Probe();

        var result = await probe.TestConnectionAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example", Key, "nope"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Contains("404", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_connection_test_reports_a_missing_claude_after_the_server_answers_but_http_errors_come_first()
    {
        using var ok = new StubServer(_ => Json("""{"type":"message","content":[]}"""));
        var request = Request(AiProfilePreset.Custom, AiApiFormat.Anthropic, "http://llm.example:8080", null, "m");

        var missing = await ok.Probe(claude: AiProfileProbe.ClaudeStatus.Missing).TestConnectionAsync(request, CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Contains("claude 실행 파일이 없습니다", missing.Message, StringComparison.Ordinal);
        Assert.Contains("OpenAI 형식을 쓰면 claude 없이 연결할 수 있습니다", missing.Message, StringComparison.Ordinal);

        var batch = await ok.Probe(claude: AiProfileProbe.ClaudeStatus.BatchFile).TestConnectionAsync(request, CancellationToken.None);
        Assert.False(batch.Success);
        Assert.Contains("배치 파일", batch.Message, StringComparison.Ordinal);

        Assert.True((await ok.Probe(claude: AiProfileProbe.ClaudeStatus.Found).TestConnectionAsync(request, CancellationToken.None)).Success);

        // OpenAI 형식은 claude 가 필요 없고, HTTP 오류는 claude 확인보다 먼저 나간다.
        var openAi = Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example:8080", null, "m");
        Assert.True((await ok.Probe(claude: AiProfileProbe.ClaudeStatus.Missing).TestConnectionAsync(openAi, CancellationToken.None)).Success);
        using var failing = new StubServer(_ => new Reply(HttpStatusCode.InternalServerError, "x"));
        var http = await failing.Probe(claude: AiProfileProbe.ClaudeStatus.Missing).TestConnectionAsync(request, CancellationToken.None);
        Assert.Contains("500", http.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("claude", http.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_handler_does_not_follow_redirects_so_the_key_never_reaches_another_host()
    {
        // 운영 생성자의 실제 전송 계층으로 확인한다: 리스너 A 가 B 로 302 를 보내도 B 에는 요청이 가지 않아야 한다.
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var targetHit = target.AcceptTcpClientAsync();
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await origin.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            _ = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/steal?t=1\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        using var probe = new AiProfileProbe(new FixedSettings(new AppSettings()));

        var result = await probe.ListModelsAsync(Request(AiProfilePreset.Anthropic, AiApiFormat.Anthropic, $"http://127.0.0.1:{originPort}", Key), CancellationToken.None);
        await serve;

        Assert.False(result.Success);
        Assert.Contains($"http://127.0.0.1:{targetPort}/steal)", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("t=1", result.Message, StringComparison.Ordinal);
        Assert.False(targetHit.IsCompleted, "리디렉션 대상에 연결이 들어왔다.");
    }

    [Fact]
    public async Task Key_is_hidden_before_the_server_error_detail_is_cut_to_length()
    {
        var detail = new string('x', 190) + Key + " tail";
        using var server = new StubServer(_ => new Reply(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { error = new { message = detail } })));

        var result = await server.Probe().ListModelsAsync(Request(AiProfilePreset.OpenAi, AiApiFormat.OpenAi, "https://api.example/v1", Key), CancellationToken.None);

        Assert.DoesNotContain("sk-probe", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFG", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_during_error_handling_still_surfaces_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        using var server = new StubServer(_ =>
        {
            cts.Cancel();
            throw new HttpRequestException("boom");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.Probe().ListModelsAsync(Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example"), cts.Token));
    }

    [Fact]
    public async Task Anthropic_connection_test_streams_like_claude_does_and_accepts_an_sse_answer()
    {
        using var server = new StubServer(_ => Json("event: message_start\ndata: {}\n\nevent: message_stop\ndata: {}\n\n"));

        var result = await server.Probe().TestConnectionAsync(Request(AiProfilePreset.Custom, AiApiFormat.Anthropic, "http://llm.example", null, "m"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(JsonDocument.Parse(server.Calls[0].Body!).RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Unreadable_settings_are_reported_instead_of_guessing_claude_exists()
    {
        using var ok = new StubServer(_ => Json("""{"type":"message"}"""));

        var result = await ok.Probe(claude: AiProfileProbe.ClaudeStatus.SettingsUnreadable)
            .TestConnectionAsync(Request(AiProfilePreset.Custom, AiApiFormat.Anthropic, "http://llm.example", null, "m"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("설정을 읽지 못해", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_to_string_hides_the_key()
    {
        Assert.DoesNotContain(Key, Request(AiProfilePreset.Custom, AiApiFormat.OpenAi, "http://llm.example", Key).ToString(), StringComparison.Ordinal);
    }

    private static AiProfileProbeRequest Request(AiProfilePreset preset, AiApiFormat format, string baseUrl, string? key = null, string model = "m")
        => new(new AiLlmProfile { Id = Guid.NewGuid(), Name = "p", Preset = preset, Format = format, BaseUrl = baseUrl, Model = model }, key);

    private static Reply Json(string body) => new(HttpStatusCode.OK, body);

    private sealed record Reply(HttpStatusCode Status, string Body, string? Location = null);

    private sealed record Call(string Method, string Url, string? Authorization, string? ApiKeyHeader, string? AnthropicVersion, string? Body);

    /// <summary>가짜 전송 계층. 요청 한 건마다 <paramref name="respond"/> 가 응답을 정하고, 보낸 내용(헤더·본문)을 기록한다.</summary>
    private sealed class StubServer(Func<Call, Reply> respond) : IDisposable
    {
        private readonly Func<Call, Reply> _respond = respond;

        public List<Call> Calls { get; } = [];

        /// <summary>true 면 응답하지 않고 취소될 때까지 기다린다.</summary>
        public bool Hang { get; init; }

        public AiProfileProbe Probe(TimeSpan? timeout = null, AiProfileProbe.ClaudeStatus? claude = null)
            => new(new Handler(this), claude is { } status ? _ => Task.FromResult(status) : null, timeout, timeout);

        public void Dispose()
        {
        }

        private sealed class Handler(StubServer owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var call = new Call(
                    request.Method.Method,
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("x-api-key", out var apiKey) ? apiKey.Single() : null,
                    request.Headers.TryGetValues("anthropic-version", out var version) ? version.Single() : null,
                    body);
                owner.Calls.Add(call);
                if (owner.Hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                var reply = owner._respond(call);
                var response = new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
                if (reply.Location is not null)
                {
                    response.Headers.Location = new Uri(reply.Location, UriKind.RelativeOrAbsolute);
                }

                return response;
            }
        }
    }
}
