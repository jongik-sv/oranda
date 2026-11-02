using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// 프로필 경로로 실제 claude 를 도는 시험. 환경 변수 <c>ORANDA_AI_E2E=1</c> 일 때만 돈다(기본은 건너뜀).
/// 가짜 Anthropic 서버(이 PC 의 임의 포트)를 띄워 claude 가 보낸 요청의 모델·헤더·배경 호출을 확인한다. 구독 한도를 쓰지 않는다.
/// 실서버 시험은 <c>ORANDA_AI_E2E_BASE_URL</c>·<c>ORANDA_AI_E2E_MODEL</c> 이 있을 때만 돈다(조정자가 실행한다).
/// </summary>
[Collection(RealClaudeGroup.Name)]
public sealed class ClaudeProfileEndToEndTests : IDisposable
{
    private const string Key = "sk-fake-e2e-key-0123456789";

    private const string FakeModel = "fake-model-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-profile-e2e-" + Guid.NewGuid().ToString("N"));

    public ClaudeProfileEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [EndToEndTheory]
    [InlineData(AiProfilePreset.Anthropic, true)]
    [InlineData(AiProfilePreset.Custom, false)]
    [InlineData(AiProfilePreset.ZAi, true)]
    public async Task Real_claude_talks_only_to_the_profile_server_with_the_profile_model_and_auth_header(AiProfilePreset preset, bool withKey)
    {
        await using var server = new FakeAnthropicServer();
        var profile = Profile(preset, server.BaseUrl, FakeModel);

        var (events, _) = await RunAsync(new AiRoute(profile, withKey ? Key : null), "Say pong.", [new AiImageAttachment("image/png", Png(8, 8))]);

        Assert.DoesNotContain(events, e => e is AiError);
        Assert.Contains("pong", Assert.IsType<AiFinal>(events[^1]).Text, StringComparison.Ordinal);
        Assert.Null(events.OfType<AiUsage>().Single().CostUsd);

        // 가짜 서버로 온 요청은 모두 /v1/messages 이고 모델은 프로필 모델뿐이다(다른 호스트로 나간 호출은 이 서버가 볼 수 없다).
        var requests = server.Requests;
        Assert.NotEmpty(requests);
        Assert.All(requests, r => Assert.Equal("/v1/messages", r.Path));
        Assert.All(requests, r => Assert.Equal(FakeModel, JsonDocument.Parse(r.Body).RootElement.GetProperty("model").GetString()));

        // 프로필의 최대 출력(CLAUDE_CODE_MAX_OUTPUT_TOKENS)이 요청의 max_tokens 가 된다.
        Assert.All(requests, r => Assert.Equal(4096, JsonDocument.Parse(r.Body).RootElement.GetProperty("max_tokens").GetInt32()));

        // 헤더 종류: Anthropic 프리셋 = x-api-key, 그 밖(Z.AI·직접 입력) = Authorization Bearer.
        foreach (var request in requests)
        {
            if (preset == AiProfilePreset.Anthropic)
            {
                Assert.Equal(Key, request.Headers.GetValueOrDefault("x-api-key"));
                Assert.False(request.Headers.ContainsKey("authorization"));
            }
            else
            {
                Assert.Equal("Bearer " + (withKey ? Key : "local"), request.Headers.GetValueOrDefault("authorization"));
                Assert.True(string.IsNullOrEmpty(request.Headers.GetValueOrDefault("x-api-key")));
            }
        }

        // 첫 요청에 이미지가 실렸다.
        Assert.Contains("\"type\":\"image\"", requests[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 세션 교차 재현·방어 확인: 프로필 A 로 만든 세션을 프로필 B(다른 서버)·Claude Code 경로에서 이어 말하려 하면 claude 를 띄우지 않고 거절해야 한다.
    /// 거절하지 않으면 claude 가 저장된 앞 대화를 B 서버로 보낸다(작업 폴더가 달라도 ~/.claude/projects 전체에서 세션 id 를 찾는다).
    /// </summary>
    [EndToEndFact]
    public async Task Resuming_a_session_created_on_another_route_is_refused_before_any_request_leaves()
    {
        await using var serverA = new FakeAnthropicServer();
        await using var serverB = new FakeAnthropicServer();
        var routeA = new AiRoute(Profile(AiProfilePreset.Custom, serverA.BaseUrl, FakeModel), null);
        var routeB = new AiRoute(Profile(AiProfilePreset.Custom, serverB.BaseUrl, FakeModel), null);
        const string marker = "SECRET-SQL-MARKER-7f3a";

        var (first, _) = await RunAsync(routeA, $"Remember {marker}. Say pong.", null);
        var sessionId = Assert.IsType<AiFinal>(first[^1]).ResumeId;
        Assert.False(string.IsNullOrEmpty(sessionId));

        // 프로필 A → 프로필 B
        var (toB, _) = await RunAsync(routeB, "continue", null, resumeId: sessionId);
        var leaked = serverB.Requests.Any(r => r.Body.Contains(marker, StringComparison.Ordinal));
        Assert.True(serverB.Requests.Count == 0 && !leaked, $"프로필 B 서버가 요청 {serverB.Requests.Count}건을 받았다(앞 대화 유출={leaked}).");
        Assert.Equal("resume_route_mismatch", Assert.IsType<AiError>(Assert.Single(toB)).Code);

        // 프로필 A → 구독 Claude Code 경로(구독 경로는 실제 서버로 나가므로 실행 전 거절만 확인한다)
        var (toCode, _) = await RunAsync(AiRoute.ClaudeCode, "continue", null, resumeId: sessionId);
        Assert.Equal("resume_route_mismatch", Assert.IsType<AiError>(Assert.Single(toCode)).Code);

        // 같은 프로필 A 로는 이어진다.
        var requestsBefore = serverA.Requests.Count;
        var (again, _) = await RunAsync(routeA, "again", null, resumeId: sessionId);
        Assert.DoesNotContain(again, e => e is AiError);
        Assert.True(serverA.Requests.Count > requestsBefore);
    }

    /// <summary>
    /// 부모 환경에 프록시 변수가 있어도 프로필 실행은 프로필 서버에 직접 연결한다. 프록시 자리에 다른 가짜 서버를 두고 요청이 한 건도 가지 않는지 본다.
    /// 프로세스 환경 변수를 잠깐 바꾸므로 같은 클래스의 다른 시험과 겹치지 않게 이 시험만 환경을 건드린다(같은 컬렉션이라 직렬 실행).
    /// </summary>
    [EndToEndFact]
    public async Task Proxy_variables_in_the_parent_environment_are_ignored_and_the_profile_server_is_reached_directly()
    {
        await using var server = new FakeAnthropicServer();
        await using var proxy = new FakeAnthropicServer();
        string[] names = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names)
            {
                // NO_PROXY 는 비운다: 시험 PC 에 localhost 우회가 설정돼 있으면 프록시 변수를 지우는 코드가 빠져도 통과하기 때문이다.
                Environment.SetEnvironmentVariable(name, name.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase) ? string.Empty : proxy.BaseUrl);
            }

            var (events, _) = await RunAsync(new AiRoute(Profile(AiProfilePreset.Custom, server.BaseUrl, FakeModel), null), "Say pong.", null);

            Assert.DoesNotContain(events, e => e is AiError);
            Assert.Empty(proxy.Requests);
            Assert.NotEmpty(server.Requests);
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [EndToEndFact]
    public async Task Real_claude_accepts_a_multi_megabyte_image_on_standard_input()
    {
        await using var server = new FakeAnthropicServer();
        var big = Png(1600, 1600, noise: true);
        Assert.True(big.Length > 3 * 1024 * 1024, $"시험 이미지가 너무 작다: {big.Length}");

        var (events, _) = await RunAsync(new AiRoute(Profile(AiProfilePreset.Custom, server.BaseUrl, FakeModel), null), "Describe the image.", [new AiImageAttachment("image/png", big)]);

        Assert.DoesNotContain(events, e => e is AiError);
        Assert.IsType<AiFinal>(events[^1]);
        Assert.Contains("\"type\":\"image\"", server.Requests[0].Body, StringComparison.Ordinal);

        // 줄 하나가 십수 MB 여도 claude 가 끝까지 읽고 답했다. 이미지는 claude 가 줄여서 보내므로 서버가 받은 크기는 비교하지 않는다.
    }

    /// <summary>실서버: 이미지 + 도구 + 이어 말하기 + 구조 결과. 조정자가 환경 변수를 주고 실행한다.</summary>
    [RealServerFact]
    public async Task Real_server_runs_image_tool_resume_and_structured_output_through_claude()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ORANDA_AI_E2E_BASE_URL")!;
        var model = Environment.GetEnvironmentVariable("ORANDA_AI_E2E_MODEL")!;
        var key = Environment.GetEnvironmentVariable("ORANDA_AI_E2E_API_KEY");
        var route = new AiRoute(Profile(AiProfilePreset.Custom, baseUrl, model), string.IsNullOrEmpty(key) ? null : key);
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"rows":{"type":"integer"},"color":{"type":"string"}},"required":["rows","color"]}""").RootElement.Clone();

        var (first, gate) = await RunAsync(
            route,
            "The attached image is one solid color. Call the describe_object tool exactly once with object set to EMP. Then answer with num_rows in rows and the image color name in color.",
            [new AiImageAttachment("image/png", Png(64, 64, red: 255))],
            schema);

        Assert.DoesNotContain(first, e => e is AiError);
        Assert.Contains(first, e => e is AiToolCall { ToolName: "describe_object" });
        Assert.Equal("describe_object", gate.LastTool);
        var final = Assert.IsType<AiFinal>(first[^1]);
        Assert.Equal(1234, final.Result!.Value.GetProperty("rows").GetInt32());
        Assert.Contains("red", final.Result!.Value.GetProperty("color").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(final.ResumeId));

        var (second, _) = await RunAsync(route, "What color was the image I attached before? Answer in one word.", null, resumeId: final.ResumeId);

        Assert.DoesNotContain(second, e => e is AiError);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<AiFinal>(second[^1]).Text));
    }

    private async Task<(List<AiEvent> Events, DescribeObjectGate Gate)> RunAsync(
        AiRoute route,
        string prompt,
        IReadOnlyList<AiImageAttachment>? images,
        JsonElement? schema = null,
        string? resumeId = null)
    {
        var tokens = new FakeRunTokens();
        var gate = new DescribeObjectGate();
        // macOS 의 유닉스 소켓 경로 한도(104자)에 맞게 짧은 이름을 쓴다.
        var unique = Guid.NewGuid().ToString("N")[..8];
        await using var host = new AgentBridgeHost(gate, tokens, (_, instance) => $"or-pe-{unique}" + (instance is null ? string.Empty : "-" + instance));
        host.Start();
        var driver = new CliDriver(
            tokens,
            gate,
            host,
            new AppDataPaths(_dir),
            new FixedSettings(new AppSettings()),
            new SystemClaudeProcessLauncher(),
            FindOrandactl(),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists);
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        var request = new AiRequest(prompt, context, resumeId, schema, Session: new AgentSession(new AgentRunScope("pe2e-run", profile, context)), Images: images);

        var events = new List<AiEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await foreach (var e in driver.RunAsync(request, route, timeout.Token))
        {
            events.Add(e);
        }

        Assert.Equal(tokens.Issued, tokens.Revoked);
        return (events, gate);
    }

    private static AiLlmProfile Profile(AiProfilePreset preset, string baseUrl, string model)
        => new() { Id = Guid.NewGuid(), Name = "e2e", Preset = preset, Format = AiApiFormat.Anthropic, BaseUrl = baseUrl, Model = model, MaxOutputTokens = 4096 };

    private static string FindOrandactl()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        var exe = OperatingSystem.IsWindows() ? "orandactl.exe" : "orandactl";
        var path = Path.Combine(directory!.FullName, "src", "Oranda.Agent.Cli", "bin", "Debug", "net10.0", exe);
        return File.Exists(path) ? path : throw new FileNotFoundException("orandactl 을 먼저 빌드하세요.", path);
    }

    /// <summary>올바른 PNG(RGB 8비트). <paramref name="noise"/> 면 무작위 픽셀이라 압축되지 않아 크기가 커진다.</summary>
    private static byte[] Png(int width, int height, bool noise = false, byte red = 0)
    {
        var random = new Random(11);
        var raw = new byte[height * (1 + (width * 3))];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + (width * 3));
            for (var x = 0; x < width; x++)
            {
                var at = row + 1 + (x * 3);
                if (noise)
                {
                    random.NextBytes(raw.AsSpan(at, 3));
                }
                else
                {
                    raw[at] = red;
                }
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BitConverter.GetBytes(width).Reverse().ToArray().CopyTo(header, 0);
        BitConverter.GetBytes(height).Reverse().ToArray().CopyTo(header, 4);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(BitConverter.GetBytes(data.Length).Reverse().ToArray());
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        stream.Write(BitConverter.GetBytes(~crc).Reverse().ToArray());
    }

    /// <summary>
    /// describe_object 도구 하나를 가진 게이트. 모델이 답에 쓸 값(num_rows)을 돌려준다(<c>FakeGate</c> 의 응답은 값이 없어 구조 결과를 맞출 수 없다).
    /// </summary>
    private sealed class DescribeObjectGate : IAgentGate
    {
        public string? LastTool { get; private set; }

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) =>
        [
            new AgentToolInfo(
                "describe_object",
                "Describes a table or view: returns its row count (num_rows) and column list.",
                JsonNode.Parse("""{"type":"object","properties":{"object":{"type":"string"}},"required":["object"]}""")!),
        ];

        public Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            LastTool = toolName;
            const string text = """{"object":"EMP","num_rows":1234,"columns":["EMPNO","ENAME"]}""";
            ToolCompleted?.Invoke(this, new AgentToolActivityEventArgs(session.RunId, session.NextSequence(), toolName, AiToolJudgement.Allowed, text.Length, null));
            return Task.FromResult(new AgentGateResponse(new AgentCallToolResult(false, text), AiToolJudgement.Allowed));
        }
    }

    private sealed record RecordedRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);

    /// <summary>
    /// 가짜 Anthropic 서버. <c>/v1/messages</c> 에만 글 "pong" 으로 답한다(스트리밍이면 SSE, 아니면 JSON). 받은 요청을 모두 기록한다.
    /// 응답마다 연결을 닫는다(구현을 단순하게 하려고).
    /// </summary>
    private sealed class FakeAnthropicServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<RecordedRequest> _requests = [];
        private readonly Task _loop;

        public FakeAnthropicServer()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await Task.WhenAny(_loop, Task.Delay(2000));
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream);
                    if (request is null)
                    {
                        return;
                    }

                    lock (_requests)
                    {
                        _requests.Add(request);
                    }

                    var streaming = request.Body.Contains("\"stream\":true", StringComparison.Ordinal);
                    await WriteResponseAsync(stream, request.Path == "/v1/messages", streaming);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // 클라이언트가 먼저 끊었다.
                }
            }
        }

        private static async Task<RecordedRequest?> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var chunk = new byte[16 * 1024];
            int headerEnd;
            while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0)
                {
                    return null;
                }

                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            var head = Encoding.ASCII.GetString([.. buffer.Take(headerEnd)]).Split("\r\n");
            var requestLine = head[0].Split(' ');
            var headers = head.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
            var length = headers.TryGetValue("content-length", out var text) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var body = buffer.Skip(headerEnd + 4).ToList();
            while (body.Count < length)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0)
                {
                    break;
                }

                body.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            var path = requestLine[1].Split('?')[0];
            return new RecordedRequest(requestLine[0], path, headers, Encoding.UTF8.GetString([.. body]));
        }

        private static int IndexOfHeaderEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Sse(string name, string json) => $"event: {name}\ndata: {json}\n\n";

        private static async Task WriteResponseAsync(NetworkStream stream, bool known, bool streaming)
        {
            string contentType;
            string body;
            var status = "200 OK";
            if (!known)
            {
                status = "404 Not Found";
                contentType = "application/json";
                body = """{"type":"error","error":{"type":"not_found_error","message":"unknown path"}}""";
            }
            else if (streaming)
            {
                contentType = "text/event-stream";
                body =
                    Sse("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"fake-model-1","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""")
                    + Sse("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")
                    + Sse("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"pong"}}""")
                    + Sse("content_block_stop", """{"type":"content_block_stop","index":0}""")
                    + Sse("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}""")
                    + Sse("message_stop", """{"type":"message_stop"}""");
            }
            else
            {
                contentType = "application/json";
                body = """{"id":"msg_1","type":"message","role":"assistant","model":"fake-model-1","content":[{"type":"text","text":"pong"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":2}}""";
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }
}

/// <summary>환경 변수 <c>ORANDA_AI_E2E=1</c> 이 없으면 건너뛴다.</summary>
public sealed class EndToEndTheoryAttribute : TheoryAttribute
{
    public EndToEndTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_AI_E2E") != "1")
        {
            Skip = "ORANDA_AI_E2E=1 일 때만 실제 claude 를 부른다.";
        }
    }
}

/// <summary><c>ORANDA_AI_E2E_BASE_URL</c> 과 <c>ORANDA_AI_E2E_MODEL</c> 이 모두 있을 때만 돈다(실서버 시험).</summary>
public sealed class RealServerFactAttribute : FactAttribute
{
    public RealServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORANDA_AI_E2E_BASE_URL"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORANDA_AI_E2E_MODEL")))
        {
            Skip = "ORANDA_AI_E2E_BASE_URL 과 ORANDA_AI_E2E_MODEL 이 있을 때만 실서버를 부른다.";
        }
    }
}

/// <summary>실제 claude 를 띄우는 시험은 한 번에 하나씩 돈다(프로세스 환경 변수를 바꾸는 시험이 있어 서로 겹치면 안 된다).</summary>
[CollectionDefinition(Name)]
public sealed class RealClaudeGroup
{
    public const string Name = "real-claude";
}
