using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>프로필 경로의 CliDriver: 환경 변수·모델·비용·키 가림·이미지 입력(설계 §6.1).</summary>
public sealed class ClaudeProfileRunTests : IDisposable
{
    private const string Key = "sk-test-secret-key-1234567890";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-cliprofile-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly FakeLauncher _launcher = new();

    public ClaudeProfileRunTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task Anthropic_profile_run_sends_profile_model_and_hides_cost_and_key()
    {
        _launcher.Lines = File.ReadAllLines(FixturePath("two-tool-calls.jsonl"));
        string? mcpJson = null;
        _launcher.OnStart = launch => mcpJson = File.ReadAllText(ArgumentAfter(launch.Arguments, "--mcp-config"));

        // 설정에 Claude Code 용 모델(opus), 요청에도 다른 모델(haiku)이 남아 있어도 프로필 서버로 나가는 모델은 프로필 모델뿐이다.
        var events = await RunAsync(NewDriver(settingsModel: "opus"), Request(model: "haiku"), Route(AiProfilePreset.Anthropic, "https://llm.example/v1/"));

        var launch = _launcher.Launches.Single();
        Assert.Equal("qwen3.8-flash", ArgumentAfter(launch.Arguments, "--model"));
        Assert.DoesNotContain("opus", launch.Arguments);
        Assert.DoesNotContain("haiku", launch.Arguments);

        var env = launch.Environment!;
        Assert.Equal("https://llm.example", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal(Key, env["ANTHROPIC_API_KEY"]);
        Assert.Null(env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.Equal("4096", env["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]);

        // 키는 환경 변수에만 있다: 인자·표준 입력·mcp-config 에 없다.
        Assert.DoesNotContain(launch.Arguments, a => a.Contains(Key, StringComparison.Ordinal));
        Assert.DoesNotContain(Key, launch.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, mcpJson!, StringComparison.Ordinal);

        var usage = events.OfType<AiUsage>().Single();
        Assert.Null(usage.CostUsd);
        Assert.IsType<AiFinal>(events[^1]);
    }

    [Fact]
    public async Task Claude_code_run_keeps_cost_and_adds_no_environment()
    {
        _launcher.Lines = File.ReadAllLines(FixturePath("two-tool-calls.jsonl"));

        var events = await RunAsync(NewDriver(settingsModel: "opus"), Request(), AiRoute.ClaudeCode);

        Assert.NotNull(events.OfType<AiUsage>().Single().CostUsd);
        var launch = _launcher.Launches.Single();
        Assert.Equal("opus", ArgumentAfter(launch.Arguments, "--model"));
        Assert.Empty(launch.Environment!);
    }

    [Fact]
    public async Task Bearer_profile_without_key_uses_placeholder_token_and_empty_api_key()
    {
        _launcher.Lines = File.ReadAllLines(FixturePath("two-tool-calls.jsonl"));

        await RunAsync(NewDriver(), Request(), Route(AiProfilePreset.Custom, "http://llm.example:8080", apiKey: null));

        var env = _launcher.Launches.Single().Environment!;
        Assert.Equal("local", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal(string.Empty, env["ANTHROPIC_API_KEY"]);
        Assert.Equal("http://llm.example:8080", env["ANTHROPIC_BASE_URL"]);
    }

    [Theory]
    [InlineData(AiProfilePreset.Gemini, AiApiFormat.OpenAi, "profile_invalid")]
    [InlineData(AiProfilePreset.Custom, AiApiFormat.OpenAi, "profile_invalid")]
    [InlineData(AiProfilePreset.Custom, AiApiFormat.Unknown, "profile_invalid")]
    [InlineData(AiProfilePreset.Unknown, AiApiFormat.Anthropic, "profile_invalid")]
    [InlineData(AiProfilePreset.Ollama, AiApiFormat.Anthropic, "profile_invalid")]
    public async Task Profile_the_cli_cannot_serve_fails_closed_before_launch(AiProfilePreset preset, AiApiFormat format, string code)
    {
        var route = new AiRoute(Profile(preset, "http://llm.example", format), Key);

        Assert.Equal(code, await ErrorCodeAsync(NewDriver(), Request(), route));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task Missing_url_model_or_key_fails_closed_before_launch()
    {
        Assert.Equal("profile_invalid_url", await ErrorCodeAsync(NewDriver(), Request(), Route(AiProfilePreset.Custom, "not a url")));
        Assert.Equal("profile_model_missing", await ErrorCodeAsync(NewDriver(), Request(), Route(AiProfilePreset.Custom, "http://llm.example", model: " ")));
        Assert.Equal("profile_key_missing", await ErrorCodeAsync(NewDriver(), Request(), Route(AiProfilePreset.Anthropic, "https://llm.example", apiKey: null)));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task Cli_not_found_for_a_profile_suggests_the_openai_format()
    {
        var withProfile = Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(cliExists: false), Request(), Route(AiProfilePreset.Custom, "http://llm.example"))));
        var withClaudeCode = Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(cliExists: false), Request(), AiRoute.ClaudeCode)));

        Assert.Equal("cli_not_found", withProfile.Code);
        Assert.Contains("OpenAI 형식을 쓰면 claude 없이 연결할 수 있습니다", withProfile.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", withClaudeCode.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_echoed_by_stderr_or_the_result_text_never_reaches_the_user()
    {
        _launcher.Lines = [];
        _launcher.StandardError = $"auth failed for {Key} at server";
        _launcher.ExitCode = 1;
        var route = Route(AiProfilePreset.Custom, "http://llm.example", apiKey: Key);

        var failed = Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request(), route)));
        Assert.Equal("cli_failed", failed.Code);
        Assert.DoesNotContain(Key, failed.Message, StringComparison.Ordinal);
        Assert.Contains("***", failed.Message, StringComparison.Ordinal);

        _launcher.StandardError = string.Empty;
        _launcher.Lines =
        [
            """{"type":"system","subtype":"init","session_id":"s1","mcp_servers":[{"name":"orandactl","status":"connected"}],"tools":["mcp__orandactl__x"]}""",
            $$"""{"type":"result","subtype":"error","is_error":true,"session_id":"s1","result":"401 invalid key {{Key}}"}""",
        ];
        var errors = (await RunAsync(NewDriver(), Request(), route)).OfType<AiError>().ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.DoesNotContain(Key, e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_echoed_inside_assistant_text_or_the_final_answer_is_hidden_too()
    {
        _launcher.Lines =
        [
            """{"type":"system","subtype":"init","session_id":"s1","mcp_servers":[{"name":"orandactl","status":"connected"}],"tools":["mcp__orandactl__x"]}""",
            $$$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"API Error: 400 bad key {{{Key}}}"}]},"session_id":"s1"}""",
            $$$"""{"type":"result","subtype":"success","is_error":false,"session_id":"s1","result":"done {{{Key}}}","usage":{"input_tokens":1,"output_tokens":1}}""",
        ];

        var events = await RunAsync(NewDriver(), Request(), Route(AiProfilePreset.Custom, "http://llm.example", apiKey: Key));

        Assert.Contains(events, e => e is AiTextDelta);
        Assert.DoesNotContain(events, e => e is AiTextDelta t && t.Text.Contains(Key, StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e is AiFinal f && f.Text!.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_can_only_be_resumed_by_the_route_that_created_it()
    {
        const string profileSession = "11111111-aaaa-4bbb-8ccc-000000000001";
        const string subscriptionSession = "22222222-aaaa-4bbb-8ccc-000000000002";
        var a = Route(AiProfilePreset.Custom, "http://llm-a.example");
        var b = Route(AiProfilePreset.Custom, "http://llm-b.example");

        // 프로필 A 가 세션을 만든다 → 같은 프로필은 이어 말할 수 있다.
        _launcher.Lines = SuccessLines(profileSession);
        Assert.DoesNotContain(await RunAsync(NewDriver(), Request(), a), e => e is AiError);
        Assert.DoesNotContain(await RunAsync(NewDriver(), Request(resumeId: profileSession), a), e => e is AiError);
        Assert.Equal(2, _launcher.Launches.Count);
        var issued = _tokens.Issued.Count;

        // 프로필 A → 프로필 B, 프로필 A → 구독: 거절하고 claude 를 띄우지 않는다.
        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: profileSession), b));
        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: profileSession), AiRoute.ClaudeCode));
        Assert.Equal(2, _launcher.Launches.Count);
        Assert.Equal(issued, _tokens.Issued.Count);

        // 구독이 만든 세션(기록 없음): 구독은 이어 가고, 프로필은 거절한다.
        _launcher.Lines = SuccessLines(subscriptionSession);
        Assert.DoesNotContain(await RunAsync(NewDriver(), Request(), AiRoute.ClaudeCode), e => e is AiError);
        Assert.DoesNotContain(await RunAsync(NewDriver(), Request(resumeId: subscriptionSession), AiRoute.ClaudeCode), e => e is AiError);
        Assert.Equal(4, _launcher.Launches.Count);
        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: subscriptionSession), a));
        Assert.Equal(4, _launcher.Launches.Count);
    }

    [Fact]
    public async Task Changing_the_server_address_of_a_profile_blocks_resuming_its_earlier_sessions()
    {
        const string session = "44444444-aaaa-4bbb-8ccc-000000000004";
        var profile = Profile(AiProfilePreset.Custom, "http://llm-internal.example");
        _launcher.Lines = SuccessLines(session);

        Assert.DoesNotContain(await RunAsync(NewDriver(), Request(), new AiRoute(profile, Key)), e => e is AiError);
        var sameServerNormalized = profile with { BaseUrl = "http://llm-internal.example/v1/" };
        Assert.DoesNotContain(await RunAsync(NewDriver(), Request(resumeId: session), new AiRoute(sameServerNormalized, Key)), e => e is AiError);

        var moved = profile with { BaseUrl = "https://cloud.example/api/anthropic" };
        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: session), new AiRoute(moved, Key)));
        Assert.Equal(2, _launcher.Launches.Count);
    }

    [Fact]
    public async Task Session_id_that_changes_between_init_and_result_is_recorded_too_and_case_cannot_dodge_the_record()
    {
        const string initial = "55555555-aaaa-4bbb-8ccc-00000000000a";
        const string forked = "66666666-AAAA-4bbb-8ccc-00000000000b";
        var route = Route(AiProfilePreset.Custom, "http://llm.example");
        _launcher.Lines =
        [
            SuccessLines(initial)[0],
            $$$"""{"type":"result","subtype":"success","is_error":false,"session_id":"{{{forked}}}","result":"ok","usage":{"input_tokens":1,"output_tokens":1}}""",
        ];

        var events = await RunAsync(NewDriver(), Request(), route);

        Assert.Equal(forked, Assert.IsType<AiFinal>(events[^1]).ResumeId);
        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: forked), AiRoute.ClaudeCode));
        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: forked.ToUpperInvariant()), AiRoute.ClaudeCode));
        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: initial.ToUpperInvariant()), AiRoute.ClaudeCode));
    }

    [Fact]
    public async Task Failing_to_record_the_session_stops_the_profile_run_before_the_app_learns_the_id()
    {
        // 기록 폴더 자리에 같은 이름의 파일이 있어 기록을 만들 수 없다.
        Directory.CreateDirectory(Path.Combine(_dir, "ai"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "ai", "sessions"), "blocker");
        _launcher.Lines = SuccessLines("77777777-aaaa-4bbb-8ccc-000000000007");

        var events = await RunAsync(NewDriver(), Request(), Route(AiProfilePreset.Custom, "http://llm.example"));

        Assert.Equal("session_record_failed", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.True(_launcher.Process!.Killed);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);

        // 구독 경로는 기록을 만들지 않으므로 같은 상태에서도 그대로 동작한다.
        _launcher.Lines = SuccessLines("88888888-aaaa-4bbb-8ccc-000000000008");
        Assert.DoesNotContain(await RunAsync(NewDriver(), Request(), AiRoute.ClaudeCode), e => e is AiError);
    }

    [Fact]
    public async Task Profile_result_before_init_fails_closed_just_like_the_subscription_path()
    {
        _launcher.Lines = [SuccessLines("99999999-aaaa-4bbb-8ccc-000000000009")[1]];

        var events = await RunAsync(NewDriver(), Request(), Route(AiProfilePreset.Custom, "http://llm.example"));

        Assert.Equal("init_missing", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.False(Directory.Exists(Path.Combine(_dir, "ai", "sessions")) && Directory.EnumerateFiles(Path.Combine(_dir, "ai", "sessions")).Any());
    }

    [Fact]
    public async Task Resume_id_that_cannot_be_a_record_name_is_refused_for_profiles_and_left_to_claude_for_subscription()
    {
        _launcher.Lines = SuccessLines("33333333-aaaa-4bbb-8ccc-000000000003");

        Assert.Equal("resume_route_mismatch", await ErrorCodeAsync(NewDriver(), Request(resumeId: "../escape"), Route(AiProfilePreset.Custom, "http://llm.example")));
        Assert.Empty(_launcher.Launches);

        await RunAsync(NewDriver(), Request(resumeId: "../escape"), AiRoute.ClaudeCode);
        Assert.Single(_launcher.Launches);
        Assert.False(Directory.Exists(Path.Combine(_dir, "ai", "escape")));
    }

    private static string[] SuccessLines(string sessionId) =>
    [
        $$$"""{"type":"system","subtype":"init","session_id":"{{{sessionId}}}","mcp_servers":[{"name":"orandactl","status":"connected"}],"tools":["mcp__orandactl__x"]}""",
        $$$"""{"type":"result","subtype":"success","is_error":false,"session_id":"{{{sessionId}}}","result":"ok","usage":{"input_tokens":1,"output_tokens":1}}""",
    ];

    [Fact]
    public async Task Empty_request_fails_before_launch()
    {
        var request = Request() with { Prompt = "  " };

        Assert.Equal("prompt_empty", await ErrorCodeAsync(NewDriver(), request, AiRoute.ClaudeCode));
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Images_are_sent_as_base64_blocks_after_the_text_in_one_stdin_line()
    {
        _launcher.Lines = File.ReadAllLines(FixturePath("two-tool-calls.jsonl"));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        await RunAsync(NewDriver(), Request(images: [new AiImageAttachment("image/png", png)]), Route(AiProfilePreset.Custom, "http://llm.example"));

        var input = _launcher.Launches.Single().StandardInput;
        Assert.EndsWith("\n", input, StringComparison.Ordinal);
        Assert.Equal(1, input.Count(c => c == '\n'));
        var content = JsonDocument.Parse(input).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(["text", "image"], content.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        var source = content[1].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal(png, Convert.FromBase64String(source.GetProperty("data").GetString()!));
    }

    [Fact]
    public async Task Unsupported_image_type_fails_before_launch()
    {
        var request = Request(images: [new AiImageAttachment("image/bmp", [1, 2, 3])]);

        Assert.Equal("image_invalid", await ErrorCodeAsync(NewDriver(), request, AiRoute.ClaudeCode));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    private static string ArgumentAfter(IReadOnlyList<string> arguments, string option) => arguments[arguments.ToList().IndexOf(option) + 1];

    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "Oranda.Platform.Tests", "Ai", "Cli", "Fixtures", name);
    }

    private static AiLlmProfile Profile(AiProfilePreset preset, string baseUrl, AiApiFormat format = AiApiFormat.Anthropic, string model = "qwen3.8-flash")
        => new() { Id = Guid.NewGuid(), Name = "시험 프로필", Preset = preset, Format = format, BaseUrl = baseUrl, Model = model, MaxOutputTokens = 4096 };

    private static AiRoute Route(AiProfilePreset preset, string baseUrl, string? apiKey = Key, string model = "qwen3.8-flash")
        => new(Profile(preset, baseUrl, model: model), apiKey);

    private static AiRequest Request(string? model = null, IReadOnlyList<AiImageAttachment>? images = null, string? resumeId = null)
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest("the prompt", context, resumeId, Model: model, Session: new AgentSession(new AgentRunScope("run-1", profile, context)), Images: images);
    }

    private static async Task<List<AiEvent>> RunAsync(CliDriver driver, AiRequest request, AiRoute route)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, route, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private static async Task<string?> ErrorCodeAsync(CliDriver driver, AiRequest request, AiRoute route)
        => Assert.IsType<AiError>(Assert.Single(await RunAsync(driver, request, route))).Code;

    private CliDriver NewDriver(bool cliExists = true, string? settingsModel = null)
    {
        var cli = Path.Combine(_dir, "claude.exe");
        var orandactl = Path.Combine(_dir, "orandactl.exe");
        return new CliDriver(
            _tokens,
            _gate,
            new FixedEndpoint("bridge-pipe"),
            new AppDataPaths(_dir),
            new FixedSettings(new AppSettings { Ai = new AiSettings { ExecutablePath = cli, Model = settingsModel } }),
            _launcher,
            orandactl,
            () => null,
            path => path == orandactl || (path == cli && cliExists));
    }
}
