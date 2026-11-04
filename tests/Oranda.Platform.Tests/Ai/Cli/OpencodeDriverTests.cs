using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// 가짜 opencode(<see cref="FakeLauncher"/>)로 드라이버의 실행 흐름·fail-closed·정리를 고정한다. 플러그인 표식은 실제 플러그인 대신 시작 훅에서 쓴다.
/// 도구 이름 <c>describe_object</c> 는 <see cref="FakeGate"/> 가 내주는 앱 도구다.
/// </summary>
public sealed class OpencodeDriverTests : IDisposable
{
    private static readonly string Opencode = Path.Combine("/fake/bin", OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
    private const string Home = "/home/x";
    private const string SessionId = "ses_e5b124059ffeAppIM3ZV7gwvYH";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-opencode-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly FakeLauncher _launcher = new();
    private readonly HashSet<string> _existing = [Opencode];
    private readonly Dictionary<string, string?> _files = [];
    private string? _versionOutput = "opencode v2.0.32";
    private int _versionReads;

    /// <summary>이어 말하기 전 대화 내보내기 결과. 기본은 Oranda 가 만든 권한 규칙 없는 대화.</summary>
    private string? _exported = ExportOf(SessionId);

    private readonly List<string> _exportCalls = [];

    /// <summary>시작 훅이 플러그인 대신 설정 폴더에 남길 표식. null 이면 아무것도 남기지 않는다(플러그인이 로드되지 않음).</summary>
    private Action<string>? _plugin;

    public OpencodeDriverTests()
    {
        Directory.CreateDirectory(_dir);
        _plugin = ReadyFor("describe_object");
        _launcher.OnStart = launch => _plugin?.Invoke(launch.Environment!["OPENCODE_CONFIG_DIR"]!);
    }

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

    private static Action<string> ReadyFor(params string[] tools) =>
        configDirectory => File.WriteAllText(Path.Combine(configDirectory, OpencodePluginFile.ReadyFileName), JsonSerializer.Serialize(new { tools }));

    private OpencodeDriver NewDriver(string? pipeName = "pipe-1") =>
        new(
            _tokens,
            _gate,
            new FixedEndpoint(pipeName),
            new AppDataPaths(_dir),
            _launcher,
            () => "/fake/bin",
            path => _existing.Contains(path),
            path => _files.GetValueOrDefault(path),
            _ => null,
            Home,
            TimeSpan.FromMinutes(1),
            (_, _, _) =>
            {
                _versionReads++;
                return Task.FromResult(_versionOutput);
            },
            (_, id, _, _) =>
            {
                _exportCalls.Add(id);
                return Task.FromResult(_exported);
            });

    private static string ExportOf(string id, string agent = "oranda_locked", string extraInfo = "") =>
        "{\"info\":{\"id\":\"" + id + "\",\"agent\":\"" + agent + "\",\"title\":\"t\"" + extraInfo + "},\"messages\":[]}";

    private static AiRequest Request(string? resume = null, JsonElement? schema = null, string? model = null, IReadOnlyList<AiImageAttachment>? images = null, AgentSession? session = null)
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest(
            "질문\n두 번째 줄",
            context,
            ResumeId: resume,
            OutputSchema: schema,
            Model: model,
            Session: session ?? new AgentSession(new AgentRunScope("run-1", profile, context)),
            Images: images);
    }

    private static async Task<List<AiEvent>> RunAsync(OpencodeDriver driver, AiRequest request, CancellationToken ct = default)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static string? CodeOf(IReadOnlyList<AiEvent> events) => Assert.IsType<AiError>(events[^1]).Code;

    private static string[] GoodRun(string text = "답변입니다.", string id = SessionId) =>
    [
        "{\"type\":\"step_start\",\"sessionID\":\"" + id + "\",\"part\":{\"type\":\"step-start\"}}",
        "{\"type\":\"text\",\"sessionID\":\"" + id + "\",\"part\":{\"type\":\"text\",\"text\":\"" + text + "\"}}",
        "{\"type\":\"step_finish\",\"sessionID\":\"" + id + "\",\"part\":{\"reason\":\"stop\",\"tokens\":{\"input\":10,\"output\":3}}}",
    ];

    private static string ToolLine(string tool, string status = "completed", string input = "{}", string id = SessionId) =>
        "{\"type\":\"tool_use\",\"sessionID\":\"" + id + "\",\"part\":{\"id\":\"call_1\",\"tool\":\"" + tool
        + "\",\"state\":{\"status\":\"" + status + "\",\"input\":" + input + ",\"output\":\"result\"}}}";

    [Fact]
    public async Task A_normal_run_yields_started_text_usage_and_final_and_cleans_up()
    {
        _launcher.Lines = GoodRun();

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal(
            [new AiStarted(SessionId), new AiTextDelta("답변입니다."), new AiUsage(10, 3), new AiFinal("답변입니다.", null, SessionId)],
            events);
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(Opencode, launch.ExecutablePath);
        Assert.Equal("질문\n두 번째 줄", launch.StandardInput);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.True(_launcher.Process!.Killed);
        Assert.False(Directory.Exists(launch.WorkingDirectory)); // 작업 폴더 삭제
        Assert.False(Directory.Exists(launch.Environment!["OPENCODE_CONFIG_DIR"])); // 설정 폴더(플러그인 파일) 삭제
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task The_token_goes_only_into_the_environment_never_into_arguments_config_or_the_plugin_file()
    {
        string? pluginText = null;
        _launcher.Lines = GoodRun();
        _launcher.OnStart = launch =>
        {
            var configDirectory = launch.Environment!["OPENCODE_CONFIG_DIR"]!;
            pluginText = File.ReadAllText(Path.Combine(configDirectory, "plugins", "oranda.js"));
            ReadyFor("describe_object")(configDirectory);
        };

        await RunAsync(NewDriver(), Request());

        var token = Assert.Single(_tokens.Issued);
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(token, launch.Environment![AgentRunToken.EnvironmentVariable]);
        Assert.Equal(token, launch.Secret);
        Assert.DoesNotContain(launch.Arguments, a => a.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(launch.Environment.Where(p => p.Key != AgentRunToken.EnvironmentVariable), p => p.Value?.Contains(token, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(token, pluginText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launch_locks_the_agent_and_removes_inherited_extra_config_for_a_new_conversation_and_for_resume()
    {
        _launcher.Lines = GoodRun();
        await RunAsync(NewDriver(), Request());
        _launcher.Lines = GoodRun();
        await RunAsync(NewDriver(), Request(resume: SessionId));

        Assert.Equal(2, _launcher.Launches.Count);
        foreach (var launch in _launcher.Launches)
        {
            Assert.Contains("--agent", launch.Arguments);
            Assert.Contains("oranda_locked", launch.Arguments);
            Assert.Contains("--standalone", launch.Arguments);
            Assert.DoesNotContain("--auto", launch.Arguments);
            Assert.True(launch.Environment!.ContainsKey("OPENCODE_CONFIG"));
            Assert.Null(launch.Environment["OPENCODE_CONFIG"]); // 부모의 추가 설정 파일을 지운다
            using var config = JsonDocument.Parse(launch.Environment["OPENCODE_CONFIG_CONTENT"]!);
            Assert.Equal("oranda_locked", config.RootElement.GetProperty("default_agent").GetString());
        }

        Assert.DoesNotContain("--session", _launcher.Launches[0].Arguments);
        var resumed = _launcher.Launches[1].Arguments.ToList();
        Assert.Equal(SessionId, resumed[resumed.IndexOf("--session") + 1]);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("th-1")]
    [InlineData("ses_a b")]
    public async Task A_resume_id_that_is_not_an_opencode_session_id_is_refused_without_starting(string resume)
    {
        var events = await RunAsync(NewDriver(), Request(resume: resume));

        Assert.Equal("resume_invalid", CodeOf(events));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task Resuming_into_a_different_conversation_is_closed()
    {
        _launcher.Lines = GoodRun(id: "ses_otherconversation1");

        var events = await RunAsync(NewDriver(), Request(resume: SessionId));

        Assert.Equal("conversation_mismatch", CodeOf(events));
        Assert.True(_launcher.Process!.Killed);
    }

    [Fact]
    public async Task A_tool_call_outside_the_allowed_list_closes_the_run_at_once()
    {
        _launcher.Lines = [GoodRun()[0], ToolLine("read"), .. GoodRun("늦은 글")[1..]];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("unexpected_tool", CodeOf(events));
        Assert.True(_launcher.Process!.Killed);
        Assert.DoesNotContain(events, e => e is AiFinal or AiTextDelta);
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("shell")]
    [InlineData("webfetch")]
    [InlineData("tools.opencode.read")]
    [InlineData("mcp_orandactl_probe")]
    [InlineData(OpencodeArguments.FinishToolName)] // 구조 결과 실행이 아니면 finish 도구도 허용 밖이다
    public async Task Every_tool_that_is_not_an_app_tool_is_refused(string tool)
    {
        _launcher.Lines = [GoodRun()[0], ToolLine(tool, status: "error"), .. GoodRun()[1..]];

        Assert.Equal("unexpected_tool", CodeOf(await RunAsync(NewDriver(), Request())));
    }

    [Fact]
    public async Task A_tool_use_without_a_name_closes_the_run()
    {
        _launcher.Lines = [GoodRun()[0], """{"type":"tool_use","sessionID":"ses_e5b124059ffeAppIM3ZV7gwvYH","part":{"state":{"status":"completed"}}}""", .. GoodRun()[1..]];

        Assert.Equal("unexpected_tool", CodeOf(await RunAsync(NewDriver(), Request())));
    }

    [Fact]
    public async Task An_app_tool_call_becomes_a_card_with_the_gate_judgement()
    {
        _launcher.Lines = [GoodRun()[0], ToolLine("describe_object", input: """{"name":"EMP"}"""), .. GoodRun()[1..]];
        _launcher.OnStart = launch =>
        {
            ReadyFor("describe_object")(launch.Environment!["OPENCODE_CONFIG_DIR"]!);
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "describe_object", AiToolJudgement.Masked, 99, "가림"));
        };

        var events = await RunAsync(NewDriver(), Request());

        Assert.Contains(new AiToolCall("call_1", "describe_object", """{"name":"EMP"}"""), events);
        Assert.Contains(new AiToolResult("call_1", "describe_object", 99, AiToolJudgement.Masked, "가림"), events);
        Assert.IsType<AiFinal>(events[^1]);
    }

    [Fact]
    public async Task A_call_the_gate_never_judged_counts_as_a_failure_and_all_failed_closes_the_run()
    {
        _launcher.Lines = [GoodRun()[0], ToolLine("describe_object", status: "error"), .. GoodRun()[1..]];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Contains(events, e => e is AiToolResult { Judgement: AiToolJudgement.Failed });
        Assert.Equal("all_tools_failed", CodeOf(events));
    }

    [Fact]
    public async Task A_plugin_that_did_not_load_closes_the_run_instead_of_continuing_silently()
    {
        _plugin = null;
        _launcher.Lines = GoodRun();

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("opencode_plugin_not_loaded", CodeOf(events));
        Assert.True(_launcher.Process!.Killed);
        Assert.DoesNotContain(events, e => e is AiFinal or AiTextDelta);
    }

    [Fact]
    public async Task A_run_that_ends_without_any_event_and_without_the_plugin_marker_is_a_plugin_error()
    {
        _plugin = null;
        _launcher.Lines = [];

        Assert.Equal("opencode_plugin_not_loaded", CodeOf(await RunAsync(NewDriver(), Request())));
    }

    [Fact]
    public async Task A_plugin_failure_marker_is_reported_with_its_reason()
    {
        _plugin = dir => File.WriteAllText(Path.Combine(dir, OpencodePluginFile.FailedFileName), "플러그인 설정 실패: boom");
        _launcher.Lines = GoodRun();

        var error = Assert.IsType<AiError>((await RunAsync(NewDriver(), Request()))[^1]);

        Assert.Equal("opencode_plugin_failed", error.Code);
        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("describe_object", "extra_tool")]
    [InlineData()]
    public async Task A_plugin_that_registered_a_different_tool_list_closes_the_run(params string[] registered)
    {
        _plugin = ReadyFor(registered);
        _launcher.Lines = GoodRun();

        Assert.Equal("opencode_plugin_mismatch", CodeOf(await RunAsync(NewDriver(), Request())));
    }

    [Theory]
    [InlineData("opencode v2.0.33")]
    [InlineData("opencode v3.0.0")]
    [InlineData("garbled")]
    [InlineData(null)]
    public async Task An_unverified_or_unreadable_version_is_refused_before_a_token_is_issued(string? version)
    {
        _versionOutput = version;

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_version_unverified", CodeOf(events));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
        Assert.Equal(1, _versionReads);
    }

    [Fact]
    public async Task User_mcp_servers_are_turned_off_in_the_injected_config()
    {
        _files[Path.Combine(Home, ".config", "opencode", "opencode.jsonc")] = """{"mcp": {"rea": {"type": "local", "command": ["rea", "mcp"]}}} // 주석""";
        _launcher.Lines = GoodRun();

        await RunAsync(NewDriver(), Request());

        using var config = JsonDocument.Parse(Assert.Single(_launcher.Launches).Environment!["OPENCODE_CONFIG_CONTENT"]!);
        Assert.False(config.RootElement.GetProperty("mcp").GetProperty("rea").GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("""{"mcp": {"r": {"type": "remote", "url": "https://x.example"}}}""")]
    [InlineData("{ not json")]
    public async Task A_user_config_that_cannot_be_read_or_turned_off_is_refused_without_starting(string content)
    {
        _files[Path.Combine(Home, ".config", "opencode", "opencode.jsonc")] = content;

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("opencode_config_unsupported", CodeOf(events));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
        Assert.Equal(0, _versionReads);
    }

    [Fact]
    public async Task A_structured_run_returns_the_arguments_of_the_finish_tool_as_the_result()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""");
        _plugin = ReadyFor("describe_object", OpencodeArguments.FinishToolName);
        _launcher.Lines = [GoodRun()[0], ToolLine(OpencodeArguments.FinishToolName, input: """{"answer":"끝"}"""), .. GoodRun("요약")[1..]];

        var events = await RunAsync(NewDriver(), Request(schema: schema.RootElement));

        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.Null(final.Text);
        Assert.Equal("끝", final.Result!.Value.GetProperty("answer").GetString());
        Assert.DoesNotContain(events, e => e is AiToolCall { ToolName: OpencodeArguments.FinishToolName });
    }

    [Fact]
    public async Task A_structured_run_without_a_finish_call_or_with_a_failed_one_is_an_error()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}}}""");
        _plugin = ReadyFor("describe_object", OpencodeArguments.FinishToolName);

        _launcher.Lines = GoodRun();
        Assert.Equal("cli_error", CodeOf(await RunAsync(NewDriver(), Request(schema: schema.RootElement))));

        _launcher.Lines = [GoodRun()[0], ToolLine(OpencodeArguments.FinishToolName, status: "error"), .. GoodRun()[1..]];
        Assert.Equal("cli_error", CodeOf(await RunAsync(NewDriver(), Request(schema: schema.RootElement))));
    }

    [Fact]
    public async Task A_structured_schema_that_is_not_an_object_is_refused()
    {
        using var schema = JsonDocument.Parse("""{"type":"array"}""");

        Assert.Equal("schema_unsupported", CodeOf(await RunAsync(NewDriver(), Request(schema: schema.RootElement))));
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task An_error_event_closes_the_run_with_its_message()
    {
        _launcher.Lines = [GoodRun()[0], """{"type":"error","sessionID":"ses_e5b124059ffeAppIM3ZV7gwvYH","error":{"type":"provider.quota","message":"한도 초과"}}"""];

        var error = Assert.IsType<AiError>((await RunAsync(NewDriver(), Request()))[^1]);

        Assert.Equal("cli_error", error.Code);
        Assert.Equal("한도 초과", error.Message);
    }

    [Fact]
    public async Task A_run_that_ends_without_a_finished_step_reports_the_stderr_tail()
    {
        _launcher.Lines = [GoodRun()[0]];
        _launcher.ExitCode = 1;
        _launcher.StandardError = "boom";

        var error = Assert.IsType<AiError>((await RunAsync(NewDriver(), Request()))[^1]);

        Assert.Equal("cli_failed", error.Code);
        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_cut_off_after_a_tool_step_does_not_turn_the_preamble_into_an_answer()
    {
        // 도구를 부른 중간 단계(reason tool-calls)까지 오고 끊기면 마지막 step_finish 가 있어도 최종 단계가 아니다.
        _launcher.Lines =
        [
            GoodRun("도구를 부르겠습니다.")[0],
            GoodRun("도구를 부르겠습니다.")[1],
            ToolLine("describe_object"),
            GoodRun()[2].Replace("\"stop\"", "\"tool-calls\"", StringComparison.Ordinal),
        ];
        _launcher.ExitCode = 1;

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_failed", CodeOf(events));
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task A_finished_run_with_a_failing_exit_code_is_not_a_success()
    {
        _launcher.Lines = GoodRun();
        _launcher.ExitCode = 1;

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_failed", CodeOf(events));
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task The_launch_environment_turns_off_project_and_compatibility_config_layers()
    {
        _launcher.Lines = GoodRun();

        await RunAsync(NewDriver(), Request());

        var environment = Assert.Single(_launcher.Launches).Environment!;
        Assert.Equal("1", environment["OPENCODE_DISABLE_PROJECT_CONFIG"]);
        Assert.Equal("1", environment["OPENCODE_CONFIG_PROJECT_DISABLE"]);
        Assert.Equal("1", environment["OPENCODE_DISABLE_CLAUDE_CODE"]);
    }

    [Fact]
    public async Task Only_the_provider_and_model_choice_of_the_user_config_reaches_the_run_and_the_lock_wins()
    {
        _files[Path.Combine(Home, ".config", "opencode", "opencode.jsonc")] = """
            {
              // 사용자 설정
              "provider": { "example": { "options": { "baseURL": "https://llm.example/v1" } } },
              "model": "example/m",
              "plugin": ["some-plugin"],
              "share": "auto",
              "permissions": [{ "action": "*", "resource": "*", "effect": "allow" }],
              "agents": { "oranda_locked": { "permissions": [{ "action": "*", "resource": "*", "effect": "allow" }] } },
              "experimental": { "policies": [] },
            }
            """;
        _launcher.Lines = GoodRun();

        await RunAsync(NewDriver(), Request());

        using var config = JsonDocument.Parse(Assert.Single(_launcher.Launches).Environment!["OPENCODE_CONFIG_CONTENT"]!);
        var root = config.RootElement;
        Assert.Equal("example/m", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("provider").TryGetProperty("example", out _));
        Assert.False(root.TryGetProperty("plugin", out _));
        Assert.False(root.TryGetProperty("share", out _));
        Assert.False(root.TryGetProperty("experimental", out _));
        Assert.Equal("deny", root.GetProperty("permissions")[0].GetProperty("effect").GetString()); // 사용자의 allow 가 아니라 잠금 규칙
        Assert.Equal("deny", root.GetProperty("agents").GetProperty("oranda_locked").GetProperty("permissions")[0].GetProperty("effect").GetString());
    }

    [Fact]
    public async Task An_unreadable_user_settings_value_closes_the_run()
    {
        _files[Path.Combine(Home, ".config", "opencode", "opencode.jsonc")] = """{ "provider": "not-an-object" }""";
        _launcher.Lines = GoodRun();

        Assert.Equal("opencode_config_unsupported", CodeOf(await RunAsync(NewDriver(), Request())));
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task A_resumed_run_first_checks_that_the_conversation_has_no_stored_permissions()
    {
        _launcher.Lines = GoodRun();

        await RunAsync(NewDriver(), Request(resume: SessionId));

        Assert.Equal([SessionId], _exportCalls);
        Assert.Single(_launcher.Launches);
    }

    [Fact]
    public async Task A_new_conversation_is_not_exported()
    {
        _launcher.Lines = GoodRun();

        await RunAsync(NewDriver(), Request());

        Assert.Empty(_exportCalls);
    }

    [Theory]
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","permissions":[{"action":"*","resource":"*","effect":"allow"}]}}""", "resume_permissions_unexpected")]
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","permission":[{"action":"*"}]}}""", "resume_permissions_unexpected")] // 필드 이름이 바뀌어도 permission 이 들어가면 본다
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","sessionPermissions":{"execute":"allow"}}}""", "resume_permissions_unexpected")]
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"build"}}""", "resume_not_verified")] // Oranda 의 대화가 아님
    [InlineData("""{"info":{"id":"ses_other0000000000000000","agent":"oranda_locked"}}""", "resume_not_verified")]
    [InlineData("""{"messages":[]}""", "resume_not_verified")] // 모양을 알 수 없음
    [InlineData("not json", "resume_not_verified")]
    [InlineData("", "resume_not_verified")]
    [InlineData(null, "resume_not_verified")] // 대화를 못 찾음·시간 초과·상한 초과
    public async Task A_resume_is_refused_unless_the_stored_conversation_is_verified(string? exported, string code)
    {
        _launcher.Lines = GoodRun();
        _exported = exported;

        var events = await RunAsync(NewDriver(), Request(resume: SessionId));

        Assert.Equal(code, CodeOf(events));
        Assert.Empty(_launcher.Launches);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.Empty(_tokens.Issued);
    }

    [Theory]
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","permissions":[]}}""")]
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","permissions":null}}""")]
    public async Task Empty_stored_permissions_are_accepted(string exported)
    {
        _launcher.Lines = GoodRun();
        _exported = exported;

        Assert.IsType<AiFinal>((await RunAsync(NewDriver(), Request(resume: SessionId)))[^1]);
    }

    [Fact]
    public async Task Preconditions_and_unsupported_requests_are_refused_without_starting()
    {
        Assert.Equal("bridge_not_ready", CodeOf(await RunAsync(NewDriver(pipeName: null), Request())));
        Assert.Equal("cli_option_unsupported", CodeOf(await RunAsync(NewDriver(), Request(model: "opus"))));
        Assert.Equal("image_unsupported", CodeOf(await RunAsync(NewDriver(), Request(images: [new AiImageAttachment("image/png", [1, 2, 3])]))));

        var revoked = Request();
        revoked.Session!.Revoke();
        Assert.Equal("session_revoked", CodeOf(await RunAsync(NewDriver(), revoked)));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task A_missing_executable_is_reported_and_the_executable_under_the_home_opencode_folder_is_found()
    {
        _existing.Clear();
        Assert.Equal("cli_not_found", CodeOf(await RunAsync(NewDriver(), Request())));

        var homeInstall = Path.Combine(Home, ".opencode", "bin", OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
        _existing.Add(homeInstall);
        _launcher.Lines = GoodRun();
        await RunAsync(NewDriver(), Request());

        Assert.Equal(homeInstall, Assert.Single(_launcher.Launches).ExecutablePath);
    }

    [Fact]
    public void The_driver_is_registered_for_the_opencode_cli_id_which_is_not_yet_available_on_any_platform()
    {
        Assert.Equal(AiProfilePresets.OpencodeCliId, NewDriver().CliId);
        Assert.All(
            [AiHostPlatform.Windows, AiHostPlatform.MacOS, AiHostPlatform.Linux],
            platform => Assert.False(AiProfilePresets.SystemClisFor(platform).Single(c => c.Id == AiProfilePresets.OpencodeCliId).Available));
    }
}
