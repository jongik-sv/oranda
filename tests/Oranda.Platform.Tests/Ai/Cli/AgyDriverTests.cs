using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>Antigravity(agy) 드라이버: 인자 골든·에이전트 파일·잠금 감시·실패 판정·정리(가짜 프로세스).</summary>
public sealed class AgyDriverTests : IDisposable
{
    private const string Conversation = "11111111-2222-3333-4444-555555555555";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-agy-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly FakeLauncher _launcher = new();

    public AgyDriverTests()
    {
        Directory.CreateDirectory(_dir);
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_home);
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

    [Fact]
    public async Task New_conversation_command_line_matches_the_golden_list_and_has_no_forbidden_flag()
    {
        _launcher.Lines = [Init(), Result()];

        await RunAsync(NewDriver(), Request());

        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(
            [
                "--output-format", "stream-json",
                "--input-format", "stream-json",
                "--agent", "oranda",
                "--settings", """{"permissions":{"allow":["mcp(orandactl/*)"],"deny":["command(*)","read_file(*)","write_file(*)","read_url(*)"]}}""",
                "--disable-slash-commands",
            ],
            launch.Arguments);
        AssertNoForbiddenFlags(launch.Arguments);
    }

    [Fact]
    public async Task Resume_command_line_repeats_every_lock_flag_and_adds_the_conversation()
    {
        _launcher.Lines = [Init(), Result()];

        await RunAsync(NewDriver(), Request() with { ResumeId = Conversation });

        var arguments = Assert.Single(_launcher.Launches).Arguments;
        Assert.Equal(
            [
                "--output-format", "stream-json",
                "--input-format", "stream-json",
                "--agent", "oranda",
                "--settings", """{"permissions":{"allow":["mcp(orandactl/*)"],"deny":["command(*)","read_file(*)","write_file(*)","read_url(*)"]}}""",
                "--disable-slash-commands",
                "--conversation", Conversation,
            ],
            arguments);
        AssertNoForbiddenFlags(arguments);
    }

    [Fact]
    public async Task Schema_run_adds_json_schema_on_one_line_and_gives_the_agent_the_finish_tool()
    {
        string? agentFile = null;
        _launcher.OnStart = launch => agentFile = File.ReadAllText(Path.Combine(launch.WorkingDirectory, ".agents", "agents", "oranda.md"));
        _launcher.Lines = [Init(), Result(structured: """{"summary":"ok"}""")];
        using var schema = JsonDocument.Parse("""{ "type": "object",\n "properties": {"summary": {"type": "string"}} }""".Replace("\\n", "\n", StringComparison.Ordinal));

        var events = await RunAsync(NewDriver(), Request() with { OutputSchema = schema.RootElement.Clone() });

        var arguments = Assert.Single(_launcher.Launches).Arguments;
        var schemaArgument = arguments[arguments.ToList().IndexOf("--json-schema") + 1];
        Assert.DoesNotContain('\n', schemaArgument);
        Assert.Contains("tools: [finish]", agentFile, StringComparison.Ordinal);
        var final = events.OfType<AiFinal>().Single();
        Assert.Null(final.Text);
        Assert.Equal("ok", final.Result!.Value.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Agent_file_locks_both_customization_switches_and_never_holds_the_token()
    {
        string? agentFile = null;
        _launcher.OnStart = launch => agentFile = File.ReadAllText(Path.Combine(launch.WorkingDirectory, ".agents", "agents", "oranda.md"));
        _launcher.Lines = [Init(), Result()];

        await RunAsync(NewDriver(), Request());

        Assert.Contains("excludeDefaultComponents: true", agentFile, StringComparison.Ordinal);
        Assert.Contains("inheritCustomizations: false", agentFile, StringComparison.Ordinal);
        Assert.Contains("mainAgent: true", agentFile, StringComparison.Ordinal);
        Assert.Contains("subagent: false", agentFile, StringComparison.Ordinal);
        Assert.Contains("tools: []", agentFile, StringComparison.Ordinal);
        Assert.Contains("- name: orandactl", agentFile, StringComparison.Ordinal);
        Assert.DoesNotContain(_tokens.Issued[0], agentFile, StringComparison.Ordinal);
        Assert.DoesNotContain("env:", agentFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Working_directory_is_the_run_folder_the_token_goes_through_the_environment_and_the_prompt_through_standard_input()
    {
        _launcher.Lines = [Init(), Result()];
        string? runFolderUnderRuns = null;
        _launcher.OnStart = launch => runFolderUnderRuns = Path.GetDirectoryName(launch.WorkingDirectory);

        await RunAsync(NewDriver(), Request("한글 \"인용\" 프롬프트"));

        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(Path.Combine(_dir, "ai", "runs"), runFolderUnderRuns);
        Assert.Equal(_tokens.Issued[0], launch.Environment![AgentRunToken.EnvironmentVariable]);
        Assert.Equal(_tokens.Issued[0], launch.Secret); // 표준 오류 끝부분에서 토큰을 가린다
        Assert.Equal("true", launch.Environment[AgyDriver.AutoUpdateVariable]); // 백그라운드 갱신기를 띄우지 않는다(true 만 효과가 있음을 측정)
        Assert.DoesNotContain(launch.Arguments, a => a.Contains(_tokens.Issued[0], StringComparison.Ordinal));
        Assert.DoesNotContain(_tokens.Issued[0], launch.StandardInput, StringComparison.Ordinal);
        Assert.EndsWith("\n", launch.StandardInput, StringComparison.Ordinal);
        using var line = JsonDocument.Parse(launch.StandardInput);
        Assert.Equal("user", line.RootElement.GetProperty("event").GetString());
        Assert.Equal("한글 \"인용\" 프롬프트", line.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.DoesNotContain('\n', launch.StandardInput.TrimEnd('\n'));
        Assert.False(launch.Environment.ContainsKey("GEMINI_API_KEY"));
    }

    [Fact]
    public async Task A_normal_run_yields_started_text_tool_card_with_the_gate_judgement_usage_and_final_then_cleans_up()
    {
        _launcher.Lines =
        [
            Init(),
            Text("안녕"),
            McpStep(2, "ACTIVE", "orandactl", "oranda_ping", "{\"a\":1}"),
            McpStep(2, "DONE", "orandactl", "oranda_ping", "{\"a\":1}", output: "pong"),
            Result(response: "끝"),
        ];
        _launcher.OnStart = _ => _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "oranda_ping", AiToolJudgement.Denied, 0, "허용 수준 때문에 거부"));

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal(Conversation, Assert.IsType<AiStarted>(events[0]).ResumeId);
        Assert.Equal("안녕", Assert.IsType<AiTextDelta>(events[1]).Text);
        var call = Assert.IsType<AiToolCall>(events[2]);
        Assert.Equal(("2", "oranda_ping", "{\"a\":1}"), (call.CallId, call.ToolName, call.ArgumentsSummary));
        var result = Assert.IsType<AiToolResult>(events[3]);
        Assert.Equal(("2", "oranda_ping", AiToolJudgement.Denied, "허용 수준 때문에 거부"), (result.CallId, result.ToolName, result.Judgement, result.Note));
        // 도구 호출이 모두 거부되었으므로 결과 앞에 안내 한 줄이 붙는다.
        Assert.Equal("[안내] 모든 도구 호출이 거부되어 실측 근거가 없습니다.\n\n", Assert.IsType<AiTextDelta>(events[4]).Text);
        Assert.IsType<AiUsage>(events[5]);
        var final = Assert.IsType<AiFinal>(events[6]);
        Assert.Equal(("끝", Conversation), (final.Text, final.ResumeId));
        AssertCleanedUp();
    }

    [Fact]
    public async Task A_tool_result_without_a_gate_record_is_failed_even_when_agy_reports_it_done()
    {
        _launcher.Lines =
        [
            Init(),
            McpStep(2, "ACTIVE", "orandactl", "t1", "{}"),
            McpStep(2, "ERROR", "orandactl", "t1", "{}", error: "x"),
            McpStep(3, "ACTIVE", "orandactl", "t2", "{}"),
            McpStep(3, "DONE", "orandactl", "t2", "{}", output: "ok"),
            Result(),
        ];

        var events = await RunAsync(NewDriver(), Request());

        // 게이트 판단이 없으면 DONE 이어도 실패다. 성공은 게이트 판단이 있을 때만 나온다.
        Assert.Equal([AiToolJudgement.Failed, AiToolJudgement.Failed], events.OfType<AiToolResult>().Select(r => r.Judgement));
    }

    [Fact]
    public async Task A_done_step_without_a_gate_judgement_is_not_a_success_and_closes_with_all_tools_failed()
    {
        // 브리지의 busy·내부 오류는 게이트를 지나지 않고 agy 에는 DONE 으로 올 수 있다.
        _launcher.Lines = UsageLines(("a", "DONE"));

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("all_tools_failed", Assert.IsType<AiError>(events[^1]).Code);
        Assert.DoesNotContain(events, e => e is AiFinal);
        Assert.DoesNotContain(events.OfType<AiToolResult>(), r => r.Judgement is AiToolJudgement.Allowed or AiToolJudgement.Masked);
    }

    [Fact]
    public async Task One_gate_judged_success_plus_a_done_step_without_judgement_passes()
    {
        _launcher.Lines = UsageLines(("ok", "DONE"), ("unjudged", "DONE"));
        _launcher.OnStart = _ => _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "ok", AiToolJudgement.Allowed, 3, null));

        var events = await RunAsync(NewDriver(), Request());

        Assert.IsType<AiFinal>(events[^1]);
        Assert.Equal([AiToolJudgement.Allowed, AiToolJudgement.Failed], events.OfType<AiToolResult>().Select(r => r.Judgement));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("run_command")]
    [InlineData("search_web")]
    [InlineData("list_resources")]
    [InlineData("read_resource")]
    [InlineData("manage_task")]
    [InlineData("finish")]
    public async Task A_tool_outside_the_allow_list_kills_the_process_at_once_with_unexpected_tool(string tool)
    {
        _launcher.Lines = [Init(), BareToolStep(2, "ACTIVE", tool), Result()];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("unexpected_tool", Assert.IsType<AiError>(events[^1]).Code);
        Assert.DoesNotContain(events, e => e is AiFinal);
        Assert.True(_launcher.Process!.Killed);
        AssertCleanedUp();
    }

    [Fact]
    public async Task An_mcp_call_to_a_server_other_than_orandactl_is_unexpected_tool()
    {
        _launcher.Lines = [Init(), McpStep(2, "ACTIVE", "user-server", "anything", "{}"), Result()];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("unexpected_tool", Assert.IsType<AiError>(events[^1]).Code);
        Assert.True(_launcher.Process!.Killed);
        Assert.DoesNotContain(events, e => e is AiToolCall);
    }

    [Fact]
    public async Task Finish_is_allowed_only_in_a_schema_run()
    {
        _launcher.Lines = [Init(), BareToolStep(2, "ACTIVE", "finish"), BareToolStep(2, "DONE", "finish"), Result(structured: """{"summary":"s"}""")];
        using var schema = JsonDocument.Parse("""{"type":"object"}""");

        var events = await RunAsync(NewDriver(), Request() with { OutputSchema = schema.RootElement.Clone() });

        Assert.DoesNotContain(events, e => e is AiError);
        Assert.DoesNotContain(events, e => e is AiToolCall);
        Assert.NotNull(events.OfType<AiFinal>().Single().Result);
    }

    [Fact]
    public async Task An_unknown_step_type_is_unexpected_tool()
    {
        _launcher.Lines = [Init(), """{"event":"step_update","step_update":{"conversation_id":"c","step_index":2,"state":"ACTIVE","step_type":"browser_subagent"}}""", Result()];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("unexpected_tool", Assert.IsType<AiError>(events[^1]).Code);
        Assert.True(_launcher.Process!.Killed);
    }

    [Fact]
    public async Task A_tool_or_result_before_init_is_init_missing()
    {
        _launcher.Lines = [McpStep(2, "ACTIVE", "orandactl", "t", "{}"), Result()];
        Assert.Equal("init_missing", Assert.IsType<AiError>((await RunAsync(NewDriver(), Request()))[^1]).Code);

        var other = new AgyDriverTests();
        other._launcher.Lines = [Result()];
        Assert.Equal("init_missing", Assert.IsType<AiError>((await RunAsync(other.NewDriver(), Request()))[^1]).Code);
        other.Dispose();
    }

    [Fact]
    public async Task Print_timeout_with_success_status_is_a_failure()
    {
        _launcher.Lines = [Init(), """{"event":"result","result":{"conversation_id":"11111111-2222-3333-4444-555555555555","status":"SUCCESS","response":"","finished_reason":"print_timeout"}}"""];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_timeout", Assert.IsType<AiError>(events[^1]).Code);
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task A_result_conversation_id_that_differs_from_init_is_a_failure()
    {
        _launcher.Lines = [Init(), Result(conversation: "99999999-0000-0000-0000-000000000000")];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("conversation_mismatch", Assert.IsType<AiError>(events[^1]).Code);
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task Resuming_a_missing_conversation_is_a_failure_because_agy_silently_starts_a_new_one()
    {
        _launcher.Lines = [Init("99999999-0000-0000-0000-000000000000"), Result(conversation: "99999999-0000-0000-0000-000000000000")];

        var events = await RunAsync(NewDriver(), Request() with { ResumeId = Conversation });

        Assert.Equal("conversation_mismatch", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.True(_launcher.Process!.Killed);
    }

    [Fact]
    public async Task Exit_code_3_reports_the_agy_error_and_an_ending_without_result_is_cli_failed()
    {
        _launcher.Lines = [Init()];
        _launcher.ExitCode = 3;
        _launcher.StandardError = """AGY_ERROR: {"short_error":"quota exceeded","status":429}""";
        var quota = (await RunAsync(NewDriver(), Request()))[^1];
        Assert.Equal("cli_error", Assert.IsType<AiError>(quota).Code);
        Assert.Contains("quota exceeded", ((AiError)quota).Message, StringComparison.Ordinal);

        using var other = new AgyDriverTests();
        other._launcher.Lines = [Init()];
        other._launcher.ExitCode = 1;
        other._launcher.StandardError = "authentication required";
        var failed = (AiError)(await RunAsync(other.NewDriver(), Request()))[^1];
        Assert.Equal("cli_failed", failed.Code);
        Assert.Contains("authentication required", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_executable_is_cli_not_found_without_launching_or_trying_another_cli()
    {
        var events = await RunAsync(NewDriver(agyExists: false), Request());

        Assert.Equal("cli_not_found", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Oversized_input_images_and_model_options_close_without_launching()
    {
        var big = new string('가', 70_000); // 210,000 바이트
        Assert.Equal("input_too_large", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request(big)))).Code);

        var image = new AiImageAttachment("image/png", [1, 2, 3]);
        Assert.Equal("image_unsupported", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request() with { Images = [image] }))).Code);
        Assert.Equal("cli_option_unsupported", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request() with { Model = "x" }))).Code);
        Assert.Equal("cli_option_unsupported", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request() with { Effort = "high" }))).Code);
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task The_input_limit_leaves_margin_below_the_silent_truncation_point()
    {
        Assert.True(AgyDriver.MaxPromptBytes < 192_000);

        _launcher.Lines = [Init(), Result()];
        var events = await RunAsync(NewDriver(), Request(new string('a', AgyDriver.MaxPromptBytes)));

        Assert.IsType<AiFinal>(events[^1]);
    }

    [Fact]
    public async Task Agent_file_write_failure_closes_without_a_process_and_cleans_up()
    {
        // runs 자리에 파일이 있으면 실행 폴더를 만들 수 없다.
        Directory.CreateDirectory(Path.Combine(_dir, "ai"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "ai", "runs"), "not a folder");

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_start_failed", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.Empty(_launcher.Launches);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task The_next_run_deletes_stale_run_folders_left_by_a_crashed_app_but_keeps_recent_ones()
    {
        var runs = Path.Combine(_dir, "ai", "runs");
        var stale = Directory.CreateDirectory(Path.Combine(runs, "crashed-run"));
        var recent = Directory.CreateDirectory(Path.Combine(runs, "other-run-in-progress"));
        stale.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-7);
        _launcher.Lines = [Init(), Result()];

        await RunAsync(NewDriver(), Request());

        Assert.False(Directory.Exists(stale.FullName));
        Assert.True(Directory.Exists(recent.FullName));
    }

    [Fact]
    public async Task Cancellation_kills_the_process_revokes_the_token_removes_the_run_folder_and_unsubscribes()
    {
        _launcher.Blocking = true;
        using var cts = new CancellationTokenSource();
        var driver = NewDriver();

        var run = Task.Run(async () =>
        {
            await foreach (var unused in driver.RunAsync(Request(), cts.Token))
            {
            }
        });
        await _launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(_launcher.Process!.Killed);
        AssertCleanedUp();
    }

    [Fact]
    public void The_run_limit_is_six_hours_and_the_stale_run_age_is_longer_so_a_long_run_folder_is_never_swept()
    {
        Assert.Equal(TimeSpan.FromHours(6), AgyDriver.RunTimeLimit);
        Assert.True(SystemCliRunner.StaleRunAge > AgyDriver.RunTimeLimit);
    }

    [Fact]
    public async Task A_user_stop_ends_the_run_as_a_cancellation_long_before_the_run_limit_and_not_as_a_timeout()
    {
        // 멈춘 CLI 는 실행 제한(6시간)이 아니라 사용자 중지로 끝낸다: 중지는 제한을 기다리지 않고 같은 정리 경로를 탄다.
        _launcher.Blocking = true;
        using var cts = new CancellationTokenSource();
        var driver = NewDriver(timeLimit: AgyDriver.RunTimeLimit);
        var events = new List<AiEvent>();

        var run = Task.Run(async () =>
        {
            await foreach (var e in driver.RunAsync(Request(), cts.Token))
            {
                events.Add(e);
            }
        });
        await _launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(events, e => e is AiError { Code: "cli_timeout" });
        Assert.True(_launcher.Process!.Killed);
        AssertCleanedUp();
    }

    [Fact]
    public async Task Revoking_the_session_does_not_leave_the_token_registered()
    {
        _launcher.Lines = [Init(), Result()];
        var request = Request();

        await RunAsync(NewDriver(), request);

        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.Null(_tokens.Resolve(_tokens.Issued[0]));
    }

    [Fact]
    public async Task The_caller_side_time_limit_closes_with_a_timeout_error_and_kills_the_process()
    {
        _launcher.Blocking = true;

        var events = await RunAsync(NewDriver(timeLimit: TimeSpan.FromMilliseconds(150)), Request());

        Assert.Equal("cli_timeout", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.True(_launcher.Process!.Killed);
        AssertCleanedUp();
    }

    [Theory]
    [InlineData("""{"mcpServers":{"x":{"command":"y"}}}""")]
    [InlineData("""{"servers":[]}""")]
    [InlineData("not json but not empty")]
    [InlineData("[]")]
    public async Task A_global_mcp_config_with_servers_closes_before_any_token_or_process(string content)
    {
        WriteGlobalMcp(content);

        var events = await RunAsync(NewDriver(), Request());

        var error = Assert.IsType<AiError>(Assert.Single(events));
        Assert.Equal("mcp_config_present", error.Code);
        Assert.Contains("mcp_config.json", error.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("{}")]
    [InlineData("""{"mcpServers":{}}""")]
    public async Task An_empty_global_mcp_config_does_not_block_the_run(string content)
    {
        WriteGlobalMcp(content);
        _launcher.Lines = [Init(), Result()];

        var events = await RunAsync(NewDriver(), Request());

        Assert.IsType<AiFinal>(events[^1]);
    }

    [Theory]
    [InlineData(".gemini/config/plugins/p1/mcp_config.json")]
    [InlineData(".gemini/antigravity-cli/plugins/p1/mcp_config.json")]
    [InlineData(".gemini/antigravity/plugins/p1/mcp_config.json")]
    public async Task A_plugin_with_mcp_servers_closes_the_run(string relative)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"mcpServers":{"x":{"command":"y"}}}""");

        var error = Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request())));

        Assert.Equal("mcp_config_present", error.Code);
        Assert.Contains("plugins", error.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Launches);
    }

    [Theory]
    [InlineData(".gemini/config/plugins.json", """{"plugins":[{"path":"/somewhere/else"}]}""", true)]
    [InlineData(".gemini/antigravity-cli/plugins.json", """["/somewhere/else"]""", true)]
    [InlineData(".gemini/config/plugins.json", "{}", false)]
    [InlineData(".gemini/config/plugins.json", """{"plugins":[]}""", false)]
    [InlineData(".gemini/config/plugins/p1/mcp_config.json", """{"mcpServers":{}}""", false)]
    public async Task Plugin_registrations_close_the_run_but_empty_ones_do_not(string relative, string content, bool refused)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _launcher.Lines = [Init(), Result()];

        var events = await RunAsync(NewDriver(), Request());

        if (refused)
        {
            Assert.Equal("mcp_config_present", Assert.IsType<AiError>(Assert.Single(events)).Code);
        }
        else
        {
            Assert.IsType<AiFinal>(events[^1]);
        }
    }

    [Fact]
    public void The_launch_environment_disables_auto_update_with_true_and_clears_an_inherited_token()
    {
        var withToken = AgyDriver.LaunchEnvironment("fake-token");
        var without = AgyDriver.LaunchEnvironment(null);

        Assert.Equal("true", withToken["AGY_CLI_DISABLE_AUTO_UPDATE"]);
        Assert.Equal("fake-token", withToken[AgentRunToken.EnvironmentVariable]);
        Assert.Null(without[AgentRunToken.EnvironmentVariable]);
        Assert.True(without.ContainsKey(AgentRunToken.EnvironmentVariable));
    }

    [Theory]
    [InlineData(".gemini/config/sidecars/s1/sidecar.json", """{"command":"x"}""")]
    [InlineData(".gemini/antigravity-cli/sidecars/s1/sidecar.json", """{"command":"x"}""")]
    [InlineData(".gemini/antigravity/sidecars/s1/anything", "x")]
    [InlineData(".gemini/config/sidecars.json", """{"entries":[{"path":"/somewhere"}]}""")]
    [InlineData(".gemini/antigravity/sidecars.json", """["/somewhere"]""")]
    [InlineData(".gemini/config/plugins/p1/sidecars/s1/sidecar.json", """{"command":"x"}""")]
    [InlineData(".gemini/antigravity-cli/plugins/p1/sidecars/s1/sidecar.json", """{"command":"x"}""")]
    [InlineData(".gemini/config/plugins/p1/sidecars.json", """{"entries":[{"path":"/somewhere"}]}""")]
    [InlineData(".gemini/antigravity-cli/plugins/p1/sidecars.json", "not json but not empty")]
    public async Task A_sidecar_anywhere_closes_the_run_before_any_token_process_or_version_read(string relative, string content)
    {
        var reads = 0;
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var events = await RunAsync(NewDriver(readVersion: (_, _, _) =>
        {
            reads++;
            return Task.FromResult<string?>("1.4.12");
        }), Request());

        var error = Assert.IsType<AiError>(Assert.Single(events));
        Assert.Equal("sidecar_config_present", error.Code);
        Assert.Contains("사이드카", error.Message, StringComparison.Ordinal);
        Assert.Contains(".gemini", error.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(".gemini/config/sidecars")]
    [InlineData(".gemini/antigravity-cli/sidecars")]
    [InlineData(".gemini/config/plugins/p1/sidecars")]
    public async Task Empty_sidecar_folders_do_not_block_the_run_like_on_a_machine_without_sidecars(string folder)
    {
        Directory.CreateDirectory(Path.Combine(_home, folder));
        _launcher.Lines = [Init(), Result()];

        Assert.IsType<AiFinal>((await RunAsync(NewDriver(), Request()))[^1]);
    }

    [Theory]
    [InlineData(".gemini/config/sidecars.json", "")]
    [InlineData(".gemini/config/sidecars.json", "{}")]
    [InlineData(".gemini/antigravity-cli/sidecars.json", """{"entries":[],"inherits":[]}""")]
    [InlineData(".gemini/config/plugins/p1/sidecars.json", "[]")]
    public async Task Empty_sidecar_registrations_do_not_block_the_run(string relative, string content)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _launcher.Lines = [Init(), Result()];

        Assert.IsType<AiFinal>((await RunAsync(NewDriver(), Request()))[^1]);
    }

    [Fact]
    public async Task An_unreadable_sidecar_folder_or_registration_closes_the_run()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var folder = Path.Combine(_home, ".gemini", "config", "sidecars");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "x"), "x");
        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            Assert.Equal("sidecar_config_present", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request()))).Code);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(folder, recursive: true);
        }

        var registration = Path.Combine(_home, ".gemini", "antigravity-cli", "sidecars.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        File.WriteAllText(registration, """{"entries":[{"path":"/x"}]}""");
        File.SetUnixFileMode(registration, UnixFileMode.None);
        try
        {
            Assert.Equal("sidecar_config_present", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request()))).Code);
        }
        finally
        {
            File.SetUnixFileMode(registration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Every_known_global_mcp_location_is_checked()
    {
        foreach (var relative in new[] { ".gemini/config", ".gemini/antigravity", ".gemini/antigravity-cli" })
        {
            var folder = Path.Combine(_home, relative);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "mcp_config.json"), """{"mcpServers":{"x":{}}}""");

            Assert.Equal("mcp_config_present", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(), Request()))).Code);

            File.Delete(Path.Combine(folder, "mcp_config.json"));
        }
    }

    [Fact]
    public async Task A_verified_version_gives_no_notice_and_reads_the_version_before_any_token_is_issued()
    {
        var reads = new List<(string Executable, int IssuedTokens)>();
        _launcher.Lines = [Init(), Text("답변"), Result(response: "답변")];

        var events = await RunAsync(NewDriver(readVersion: (executable, _, _) =>
        {
            reads.Add((executable, _tokens.Issued.Count));
            return Task.FromResult<string?>("1.4.12");
        }), Request());

        Assert.DoesNotContain(events.OfType<AiTextDelta>(), d => d.Text.Contains("[안내]", StringComparison.Ordinal));
        var read = Assert.Single(reads);
        Assert.Equal(0, read.IssuedTokens);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "agy.exe" : "agy", read.Executable, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.5.0")]
    [InlineData("agy version 2.0.1")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("무슨 글")]
    public async Task An_unverified_or_unreadable_version_gives_one_notice_after_the_start_and_does_not_stop_the_run(string? versionOutput)
    {
        _launcher.Lines = [Init(), Text("답변"), Result(response: "답변")];

        var events = await RunAsync(NewDriver(readVersion: (_, _, _) => Task.FromResult(versionOutput)), Request());

        Assert.IsType<AiStarted>(events[0]);
        var notice = Assert.IsType<AiTextDelta>(events[1]);
        Assert.Equal("[안내] 이 버전의 Antigravity CLI 는 Oranda 에서 잠금을 검증하지 않았습니다.\n\n", notice.Text);
        Assert.Single(events.OfType<AiTextDelta>(), d => d.Text.StartsWith("[안내]", StringComparison.Ordinal));
        var final = events.OfType<AiFinal>().Single();
        Assert.Equal("답변", final.Text); // 안내는 최종 글에 들어가지 않는다
    }

    [Fact]
    public async Task A_version_reader_that_throws_or_times_out_still_gives_the_notice_and_runs()
    {
        _launcher.Lines = [Init(), Result()];
        var thrown = await RunAsync(NewDriver(readVersion: (_, _, _) => throw new IOException("cannot start")), Request());
        Assert.Contains(thrown.OfType<AiTextDelta>(), d => d.Text.StartsWith("[안내]", StringComparison.Ordinal));
        Assert.IsType<AiFinal>(thrown[^1]);

        using var other = new AgyDriverTests();
        other._launcher.Lines = [Init(), Result()];
        var slow = await RunAsync(other.NewDriver(readVersion: async (_, limit, ct) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(5), limit); // 시간 상한이 짧다
            await Task.Yield();
            return null; // 상한을 넘겨 읽지 못한 것과 같다
        }), Request());
        Assert.Contains(slow.OfType<AiTextDelta>(), d => d.Text.StartsWith("[안내]", StringComparison.Ordinal));
        Assert.IsType<AiFinal>(slow[^1]);
    }

    [Fact]
    public async Task A_resumed_turn_gives_no_notice_and_does_not_even_read_the_version()
    {
        var reads = 0;
        _launcher.Lines = [Init(), Text("답변"), Result(response: "답변")];

        var events = await RunAsync(NewDriver(readVersion: (_, _, _) =>
        {
            reads++;
            return Task.FromResult<string?>("9.9.9");
        }), Request() with { ResumeId = Conversation });

        Assert.Equal(0, reads);
        Assert.DoesNotContain(events.OfType<AiTextDelta>(), d => d.Text.Contains("[안내]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_that_close_early_neither_read_the_version_nor_give_a_notice()
    {
        var reads = 0;
        SystemCliVersion.Reader reader = (_, _, _) =>
        {
            reads++;
            return Task.FromResult<string?>("9.9.9");
        };
        WriteGlobalMcp("""{"mcpServers":{"x":{}}}""");

        Assert.Equal("mcp_config_present", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(readVersion: reader), Request()))).Code);
        File.Delete(Path.Combine(_home, ".gemini", "config", "mcp_config.json"));
        Assert.Equal("cli_not_found", Assert.IsType<AiError>(Assert.Single(await RunAsync(NewDriver(agyExists: false, readVersion: reader), Request()))).Code);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task The_notice_does_not_touch_the_structured_result_or_the_resume_id()
    {
        _launcher.Lines = [Init(), Result(structured: """{"summary":"s"}""")];
        using var schema = JsonDocument.Parse("""{"type":"object"}""");

        var events = await RunAsync(NewDriver(readVersion: (_, _, _) => Task.FromResult<string?>("0.0.1")), Request() with { OutputSchema = schema.RootElement.Clone() });

        var final = events.OfType<AiFinal>().Single();
        Assert.Null(final.Text);
        Assert.Equal("s", final.Result!.Value.GetProperty("summary").GetString());
        Assert.Equal(Conversation, final.ResumeId);
        Assert.Equal(Conversation, events.OfType<AiStarted>().Single().ResumeId);
    }

    [Fact]
    public async Task The_recorded_finish_stream_of_a_real_schema_run_ends_with_the_structured_result_and_is_closed_without_a_schema()
    {
        var recorded = File.ReadAllLines(FixturePath("agy-real-schema-finish.jsonl"));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        _launcher.Lines = recorded;

        var events = await RunAsync(NewDriver(), Request() with { OutputSchema = schema.RootElement.Clone() });

        Assert.DoesNotContain(events, e => e is AiError);
        var final = events.OfType<AiFinal>().Single();
        Assert.Equal("example summary", final.Result!.Value.GetProperty("summary").GetString());
        Assert.Null(final.Text);
        Assert.Equal("cef634cb-c528-48cc-a554-3b2c2e375ad9", final.ResumeId);

        // 스키마 없는 실행에는 finish 도구가 없으므로 같은 스트림은 닫힌다.
        using var other = new AgyDriverTests();
        other._launcher.Lines = recorded;
        var closed = await RunAsync(other.NewDriver(), Request());
        Assert.Equal("unexpected_tool", Assert.IsType<AiError>(closed[^1]).Code);
        Assert.True(other._launcher.Process!.Killed);
    }

    [Fact]
    public async Task The_system_prompt_names_the_server_and_lists_the_gate_tools_with_their_schemas_and_holds_no_secret()
    {
        string? agentFile = null;
        _launcher.OnStart = launch => agentFile = File.ReadAllText(Path.Combine(launch.WorkingDirectory, ".agents", "agents", "oranda.md"));
        _launcher.Lines = [Init(), Result()];

        await RunAsync(NewDriver(), Request());

        Assert.NotNull(agentFile);
        var prompt = agentFile[agentFile.IndexOf("# System Prompt", StringComparison.Ordinal)..];
        Assert.Contains("\"orandactl\"", prompt, StringComparison.Ordinal);
        Assert.Contains("call_mcp_tool", prompt, StringComparison.Ordinal);
        Assert.Contains("ServerName", prompt, StringComparison.Ordinal);
        Assert.Contains("- describe_object: describe", prompt, StringComparison.Ordinal);
        Assert.Contains("""input schema: {"type":"object"}""", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge-pipe", prompt, StringComparison.Ordinal); // 파이프 이름은 시스템 지시에 넣지 않는다
        Assert.DoesNotContain(_tokens.Issued[0], agentFile, StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_prompt_without_tools_says_so_and_a_multi_line_description_stays_on_one_line()
    {
        Assert.Contains("No Oranda tool is available", AgyAgentFile.SystemPrompt([]), StringComparison.Ordinal);

        var tool = new AgentToolInfo("t1", "first line\n---\nsecond line", System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object"}""")!);
        var text = AgyAgentFile.Render("/x/orandactl", "pipe", withFinishTool: false, [tool]);

        Assert.Equal(2, text.Split('\n').Count(l => l == "---")); // 머리말을 여닫는 두 줄뿐이다.
        Assert.Contains("- t1: first line --- second line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_prompt_for_the_real_tool_list_names_every_tool_and_stays_small()
    {
        var tools = Oranda.Core.Ai.Schemas.AgentTools.All.Select(AgentToolInfo.From).ToList();

        var prompt = AgyAgentFile.SystemPrompt(tools);

        Assert.All(tools, tool => Assert.Contains($"- {tool.Name}: ", prompt, StringComparison.Ordinal));
        Assert.True(prompt.Length < 12_000, $"시스템 지시 길이 {prompt.Length}자: 모델 입력 토큰이 늘어난다(측정값 약 5,600자).");
    }

    [Fact]
    public async Task Tool_calls_with_at_least_one_success_pass()
    {
        _launcher.Lines = UsageLines(("ok", "DONE"), ("bad", "ERROR"));
        _launcher.OnStart = _ => _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "ok", AiToolJudgement.Allowed, 3, null));

        var events = await RunAsync(NewDriver(), Request());

        Assert.IsType<AiFinal>(events[^1]);
        Assert.DoesNotContain(events.OfType<AiTextDelta>(), d => d.Text.Contains("[안내]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Calls_that_all_failed_close_with_all_tools_failed_and_no_result_even_when_some_were_denied()
    {
        _launcher.Lines = UsageLines(("a", "ERROR"), ("b", "DONE"), ("c", "ERROR"));
        _launcher.OnStart = _ => _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "b", AiToolJudgement.Denied, 0, "거부"));

        var events = await RunAsync(NewDriver(), Request());

        var error = Assert.IsType<AiError>(events[^1]);
        Assert.Equal("all_tools_failed", error.Code);
        Assert.Contains("실패 2건, 거부 1건", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is AiFinal or AiUsage);
    }

    [Fact]
    public async Task Failures_without_any_gate_judgement_count_as_failed_and_close()
    {
        _launcher.Lines = UsageLines(("a", "ERROR"), ("b", "ERROR"));

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("all_tools_failed", Assert.IsType<AiError>(events[^1]).Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task When_every_call_was_denied_the_answer_is_shown_with_one_notice_line_only_on_the_first_turn(bool schema, bool resumed)
    {
        _launcher.Lines = UsageLines(("a", "DONE"), ("b", "DONE"));
        _launcher.OnStart = _ =>
        {
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "a", AiToolJudgement.Denied, 0, "거부"));
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 2, "b", AiToolJudgement.Denied, 0, "거부"));
        };
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        var request = Request() with { OutputSchema = schema ? document.RootElement.Clone() : null, ResumeId = resumed ? Conversation : null };

        var events = await RunAsync(NewDriver(), request);

        Assert.IsType<AiFinal>(events[^1]);
        Assert.DoesNotContain(events, e => e is AiError);
        var notices = events.OfType<AiTextDelta>().Where(d => d.Text.StartsWith("[안내]", StringComparison.Ordinal)).ToList();
        if (resumed)
        {
            Assert.Empty(notices); // 이어 말하기 차례의 AI 글은 레포트로 모이므로 안내 없이 통과한다.
        }
        else
        {
            Assert.Equal("[안내] 모든 도구 호출이 거부되어 실측 근거가 없습니다.\n\n", Assert.Single(notices).Text);
            Assert.True(events.IndexOf(notices[0]) < events.FindIndex(e => e is AiFinal));
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task With_no_tool_call_a_structured_first_turn_gets_the_no_evidence_line_and_nothing_else_does(bool schema, bool resumed, bool expectNotice)
    {
        _launcher.Lines = [Init(), Result(structured: schema ? """{"summary":"s"}""" : null)];
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        var request = Request() with { OutputSchema = schema ? document.RootElement.Clone() : null, ResumeId = resumed ? Conversation : null };

        var events = await RunAsync(NewDriver(), request);

        var notices = events.OfType<AiTextDelta>().Where(d => d.Text.StartsWith("[안내]", StringComparison.Ordinal)).Select(d => d.Text).ToList();
        if (expectNotice)
        {
            Assert.Equal("[안내] AI 가 도구를 쓰지 않고 답했으므로 실측 근거가 없습니다.\n\n", Assert.Single(notices));
            var final = events.OfType<AiFinal>().Single();
            Assert.Equal("s", final.Result!.Value.GetProperty("summary").GetString()); // 구조 결과는 그대로다
            Assert.Null(final.Text);
        }
        else
        {
            Assert.Empty(notices);
        }
    }

    private static string[] UsageLines(params (string Tool, string State)[] calls)
    {
        var lines = new List<string> { Init() };
        var index = 2;
        foreach (var (tool, state) in calls)
        {
            lines.Add(McpStep(index, "ACTIVE", "orandactl", tool, "{}"));
            lines.Add(McpStep(index, state, "orandactl", tool, "{}", output: state == "DONE" ? "ok" : null, error: state == "ERROR" ? "boom" : null));
            index++;
        }

        lines.Add(Result(response: "답"));
        return [.. lines];
    }

    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "Oranda.Platform.Tests", "Ai", "Cli", "Fixtures", name);
    }

    [Fact]
    public void The_driver_identifies_as_antigravity_and_is_registered_as_a_system_cli_driver()
    {
        var driver = NewDriver();
        var attribute = typeof(AgyDriver).GetCustomAttributes(typeof(Oranda.Core.Composition.OrandaServiceAttribute), inherit: false);

        Assert.Equal(AiProfilePresets.AntigravityCliId, driver.CliId);
        Assert.Contains(attribute, a => ((Oranda.Core.Composition.OrandaServiceAttribute)a).ServiceType.Name == nameof(ISystemCliAiDriver));
    }

    private void WriteGlobalMcp(string content)
    {
        var folder = Path.Combine(_home, ".gemini", "config");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mcp_config.json"), content);
    }

    private void AssertCleanedUp()
    {
        Assert.NotEmpty(_tokens.Issued);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "ai", "runs")));
        Assert.False(_gate.HasSubscribers);
    }

    private static void AssertNoForbiddenFlags(IReadOnlyList<string> arguments)
    {
        Assert.DoesNotContain("-p", arguments);
        Assert.DoesNotContain("--print", arguments);
        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain("--effort", arguments);
        Assert.DoesNotContain("--approve-for-me", arguments);
        Assert.DoesNotContain(arguments, a => a.StartsWith("--dangerously", StringComparison.Ordinal));
    }

    private static string Init(string conversation = Conversation)
        => new JsonObject
        {
            ["event"] = "init",
            ["conversation_id"] = conversation,
            ["init"] = new JsonObject { ["cwd"] = "/run", ["tools"] = new JsonArray("call_mcp_tool", "read_file", "run_command") },
        }.ToJsonString();

    private static string Step(int index, string state, string stepType, JsonObject? extra = null)
    {
        var step = new JsonObject
        {
            ["conversation_id"] = Conversation,
            ["step_index"] = index,
            ["state"] = state,
            ["step_type"] = stepType,
        };
        foreach (var (name, value) in extra ?? [])
        {
            step[name] = value?.DeepClone();
        }

        return new JsonObject { ["event"] = "step_update", ["step_update"] = step }.ToJsonString();
    }

    private static string Text(string text) => Step(1, "ACTIVE", "agent_response", new JsonObject { ["text_delta"] = text });

    private static string McpStep(int index, string state, string server, string tool, string arguments, string? output = null, string? error = null)
    {
        var info = new JsonObject
        {
            ["name"] = "call_mcp_tool",
            ["parameters"] = new JsonObject { ["ServerName"] = server, ["ToolName"] = tool, ["Arguments"] = JsonNode.Parse(arguments) },
        };
        if (output is not null)
        {
            info["output"] = output;
        }

        if (error is not null)
        {
            info["error"] = new JsonObject { ["type"] = "TOOL_ERROR", ["message"] = error };
        }

        return Step(index, state, "tool", new JsonObject { ["tool_name"] = "call_mcp_tool", ["tool_info"] = info });
    }

    private static string BareToolStep(int index, string state, string tool)
        => Step(index, state, "tool", new JsonObject { ["tool_name"] = tool, ["tool_info"] = new JsonObject { ["name"] = tool, ["parameters"] = new JsonObject() } });

    private static string Result(string response = "", string? structured = null, string? conversation = null)
    {
        var result = new JsonObject
        {
            ["conversation_id"] = conversation ?? Conversation,
            ["status"] = "SUCCESS",
            ["response"] = response,
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
        };
        if (structured is not null)
        {
            result["structured_output"] = JsonNode.Parse(structured);
        }

        return new JsonObject { ["event"] = "result", ["result"] = result }.ToJsonString();
    }

    private static AiRequest Request(string prompt = "the prompt")
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest(prompt, context, Session: new AgentSession(new AgentRunScope("run-1", profile, context)));
    }

    private static async Task<List<AiEvent>> RunAsync(AgyDriver driver, AiRequest request)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private AgyDriver NewDriver(bool agyExists = true, TimeSpan? timeLimit = null, SystemCliVersion.Reader? readVersion = null)
    {
        var agy = Path.Combine(_dir, "bin", OperatingSystem.IsWindows() ? "agy.exe" : "agy");
        var orandactl = Path.Combine(_dir, "orandactl");
        return new AgyDriver(
            _tokens,
            _gate,
            new FixedEndpoint("bridge-pipe"),
            new AppDataPaths(_dir),
            _launcher,
            orandactl,
            () => Path.GetDirectoryName(agy),
            path => path == orandactl || (agyExists && path == agy),
            _home,
            timeLimit ?? TimeSpan.FromMinutes(1),
            readVersion ?? ((_, _, _) => Task.FromResult<string?>("1.4.12")));
    }
}
