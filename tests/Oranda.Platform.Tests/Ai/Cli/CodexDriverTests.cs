using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>가짜 codex(<see cref="FakeLauncher"/>)로 Codex 드라이버의 실행 흐름·fail-closed·정리·게이트 판단 옮김을 고정한다.</summary>
public sealed class CodexDriverTests : IDisposable
{
    // Windows 는 이름.exe 를 찾으므로(SystemCliExecutable) 대역 실행 파일 이름도 맞춘다.
    private static readonly string Codex = Path.Combine("/fake/bin", OperatingSystem.IsWindows() ? "codex.exe" : "codex");
    private const string Orandactl = "/fake/orandactl";

    private static readonly string ValidDebugModels = new System.Text.Json.Nodes.JsonObject
    {
        ["models"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["slug"] = "gpt-x",
            ["tool_mode"] = "code_mode_only",
            ["apply_patch_tool_type"] = "freeform",
            ["multi_agent_version"] = "v2",
            ["experimental_supported_tools"] = new System.Text.Json.Nodes.JsonArray("a"),
            ["supports_search_tool"] = true,
        }),
    }.ToJsonString();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-codex-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly FakeLauncher _launcher = new();
    private readonly HashSet<string> _existing = [Codex, Orandactl];
    private readonly Dictionary<string, string> _env = [];
    private readonly List<string> _catalogCalls = [];
    private (string? Output, string? Error) _debugModels = (ValidDebugModels, null);

    public CodexDriverTests() => Directory.CreateDirectory(_dir);

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

    private string? _versionOutput = "codex-cli 0.166.0";
    private int _versionReads;
    private int _issuedAtRead = -1;

    private CodexDriver NewDriver(TimeSpan? timeout = null, string? pipeName = "pipe-1") =>
        new(
            _tokens,
            _gate,
            new FixedEndpoint(pipeName),
            new AppDataPaths(_dir),
            _launcher,
            Orandactl,
            () => "/fake/bin",
            path => _existing.Contains(path),
            (exe, _, _) =>
            {
                _catalogCalls.Add(exe);
                return Task.FromResult(_debugModels);
            },
            name => _env.GetValueOrDefault(name),
            "/home/x",
            timeout ?? TimeSpan.FromMinutes(1),
            (_, _, _) =>
            {
                _versionReads++;
                _issuedAtRead = _tokens.Issued.Count;
                return Task.FromResult(_versionOutput);
            });

    private static AiRequest Request(string? resume = null, JsonElement? schema = null, IReadOnlyList<AiImageAttachment>? images = null, AgentSession? session = null)
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest(
            "질문",
            context,
            ResumeId: resume,
            OutputSchema: schema,
            Model: "opus",
            Effort: "high",
            Session: session ?? new AgentSession(new AgentRunScope("run-1", profile, context)),
            Images: images);
    }

    private static async Task<List<AiEvent>> RunAsync(CodexDriver driver, AiRequest request, CancellationToken ct = default)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static string? CodeOf(IReadOnlyList<AiEvent> events) => Assert.IsType<AiError>(events[^1]).Code;

    private static string[] GoodRun(string text = "답변입니다.") =>
    [
        """{"type":"thread.started","thread_id":"th-1"}""",
        """{"type":"turn.started"}""",
        """{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"__TEXT__"}}""".Replace("__TEXT__", text, StringComparison.Ordinal),
        """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":3}}""",
    ];

    [Fact]
    public async Task A_normal_run_yields_started_text_usage_and_final_and_cleans_up()
    {
        _launcher.Lines = GoodRun();

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal(
            [new AiStarted("th-1"), new AiTextDelta("답변입니다."), new AiUsage(10, 3), new AiFinal("답변입니다.", null, "th-1")],
            events);
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(Codex, launch.ExecutablePath);
        Assert.Equal("질문\n", launch.StandardInput);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.True(_launcher.Process!.Killed);
        Assert.False(Directory.Exists(launch.WorkingDirectory)); // 실행 폴더 삭제
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task The_token_goes_only_into_the_environment_and_model_and_effort_never_reach_the_arguments()
    {
        _launcher.Lines = GoodRun();
        string? catalogOnDisk = null;
        _launcher.OnStart = launch => catalogOnDisk = File.ReadAllText(Path.Combine(launch.WorkingDirectory, "model-catalog.json"));

        await RunAsync(NewDriver(), Request());

        var launch = Assert.Single(_launcher.Launches);
        var token = Assert.Single(_tokens.Issued);
        Assert.Equal(token, launch.Environment!["ORANDA_RUN_TOKEN"]);
        Assert.DoesNotContain(launch.Arguments, a => a.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain("-m", launch.Arguments);
        Assert.DoesNotContain("opus", launch.Arguments);
        Assert.DoesNotContain(launch.Arguments, a => a.Contains("effort", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"tool_mode\":null", catalogOnDisk, StringComparison.Ordinal); // 실행 시점에 잠근 카탈로그가 디스크에 있었다
        Assert.Contains("model_catalog_json=" + CodexArguments.TomlString(Path.Combine(launch.WorkingDirectory, "model-catalog.json")), launch.Arguments);
    }

    [Fact]
    public async Task Resume_uses_the_resume_command_with_the_thread_id()
    {
        _launcher.Lines = GoodRun();

        var events = await RunAsync(NewDriver(), Request("01a19f88-3e8b-7531-9054-07ec9d7111ea"));

        var arguments = Assert.Single(_launcher.Launches).Arguments;
        Assert.Equal(["exec", "resume"], arguments.Take(2));
        Assert.Equal(["01a19f88-3e8b-7531-9054-07ec9d7111ea", "-"], arguments.TakeLast(2));
        Assert.Contains("sandbox_mode=\"read-only\"", arguments);
        Assert.Contains("features.hooks=false", arguments);
        Assert.IsType<AiFinal>(events[^1]);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("format")]
    [InlineData("missing-field")]
    public async Task A_catalog_that_cannot_be_made_closes_without_starting_a_process(string kind)
    {
        _debugModels = kind switch
        {
            "generation" => (null, "debug models 실패"),
            "format" => ("not json", null),
            _ => (ValidDebugModels.Replace("tool_mode", "tool_mode_renamed", StringComparison.Ordinal), null),
        };

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_start_failed", CodeOf(events));
        Assert.Empty(_launcher.Launches);
        Assert.Single(_catalogCalls);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
    }

    [Theory]
    [InlineData("command_execution")]
    [InlineData("file_change")]
    [InlineData("web_search")]
    public async Task An_unknown_item_type_ends_the_run_at_once_with_unexpected_tool(string itemType)
    {
        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            $$$"""{"type":"item.started","item":{"id":"x","type":"{{{itemType}}}"}}""",
            """{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"이 글은 나오면 안 된다"}}""",
            """{"type":"turn.completed","usage":{}}""",
        ];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("unexpected_tool", CodeOf(events));
        Assert.DoesNotContain(events, e => e is AiTextDelta or AiFinal);
        Assert.True(_launcher.Process!.Killed);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
    }

    [Fact]
    public async Task A_tool_call_on_another_mcp_server_ends_the_run_with_unexpected_tool()
    {
        _launcher.Lines = ["""{"type":"item.started","item":{"id":"x","type":"mcp_tool_call","server":"someother","tool":"t","arguments":{}}}"""];

        Assert.Equal("unexpected_tool", CodeOf(await RunAsync(NewDriver(), Request())));
    }

    [Fact]
    public async Task A_kill_that_throws_still_revokes_the_token_removes_the_run_folder_and_unsubscribes_the_gate()
    {
        // 골격의 계약(CliDriver 특성 시험과 같다): 종료가 던져도 정리는 끝까지 하고 예외는 호출 측으로 나간다.
        _launcher.KillThrows = true;
        _launcher.Lines = ["""{"type":"item.started","item":{"id":"x","type":"command_execution"}}"""];

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(NewDriver(), Request()));

        Assert.True(_launcher.Process!.Killed);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.False(Directory.Exists(Assert.Single(_launcher.Launches).WorkingDirectory));
        Assert.False(_gate.HasSubscribers);
    }

    [Theory]
    [InlineData("--last")]
    [InlineData("--dangerously-bypass-approvals-and-sandbox")]
    [InlineData("my-thread-name")]
    public async Task A_resume_id_that_is_not_a_codex_thread_id_is_refused_before_launch(string id)
    {
        var events = await RunAsync(NewDriver(), Request(id));

        Assert.Equal("resume_invalid", CodeOf(events));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_catalogCalls);
    }

    [Fact]
    public async Task The_token_is_registered_as_a_secret_so_the_stderr_tail_is_redacted_and_the_stream_stops_after_turn_failed()
    {
        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            """{"type":"turn.failed","error":{"message":"fail"}}""",
            """{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"실패 뒤 글"}}""",
        ];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal(Assert.Single(_tokens.Issued), Assert.Single(_launcher.Launches).Secret);
        Assert.DoesNotContain(events, e => e is AiTextDelta { Text: "실패 뒤 글" });
    }

    [Fact]
    public async Task A_middle_error_event_is_not_a_failure_and_turn_failed_is()
    {
        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            """{"type":"error","message":"Reconnecting... 1/5"}""",
            """{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"ok"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""",
        ];
        Assert.IsType<AiFinal>((await RunAsync(NewDriver(), Request()))[^1]);

        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            """{"type":"error","message":"Reconnecting... 1/5"}""",
            """{"type":"turn.failed","error":{"message":"401 Unauthorized"}}""",
        ];
        var failed = await RunAsync(NewDriver(), Request());
        var error = Assert.IsType<AiError>(failed[^1]);
        Assert.Equal(("401 Unauthorized", "cli_error"), (error.Message, error.Code));
        Assert.DoesNotContain(failed, e => e is AiFinal);
    }

    [Fact]
    public async Task A_run_that_ends_without_a_result_is_cli_failed_with_the_stderr_tail()
    {
        _launcher.Lines = ["""{"type":"thread.started","thread_id":"th-1"}"""];
        _launcher.StandardError = "something broke";
        _launcher.ExitCode = 1;

        var events = await RunAsync(NewDriver(), Request());

        var error = Assert.IsType<AiError>(events[^1]);
        Assert.Equal("cli_failed", error.Code);
        Assert.Contains("something broke", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_events_move_the_gate_judgement_including_a_denial_to_the_card()
    {
        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            """{"type":"item.started","item":{"id":"c1","type":"mcp_tool_call","server":"orandactl","tool":"describe_object","arguments":{"object":"EMP"}}}""",
            """{"type":"item.completed","item":{"id":"c1","type":"mcp_tool_call","server":"orandactl","tool":"describe_object","status":"completed","result":{"content":[{"type":"text","text":"abc"}]}}}""",
            """{"type":"item.started","item":{"id":"c2","type":"mcp_tool_call","server":"orandactl","tool":"run_query","arguments":{}}}""",
            """{"type":"item.completed","item":{"id":"c2","type":"mcp_tool_call","server":"orandactl","tool":"run_query","status":"failed","error":{"message":"denied"}}}""",
            """{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"끝"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""",
        ];
        var session = Request().Session!;
        // 게이트가 낸 판단(실제 게이트 형식의 도구 이름)을 구독 큐가 받는다. 거부 판단도 그대로 카드로 간다.
        _launcher.OnStart = _ =>
        {
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "describe_object", AiToolJudgement.Masked, 99, "가림"));
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 2, "run_query", AiToolJudgement.Denied, 0, "거부됨"));
            _gate.Raise(new AgentToolActivityEventArgs("other-run", 3, "describe_object", AiToolJudgement.Allowed, 1, null));
        };

        var events = await RunAsync(NewDriver(), Request(session: session));

        var calls = events.OfType<AiToolCall>().ToList();
        Assert.Equal(["describe_object", "run_query"], calls.Select(c => c.ToolName));
        Assert.Contains("EMP", calls[0].ArgumentsSummary, StringComparison.Ordinal);
        var results = events.OfType<AiToolResult>().ToList();
        Assert.Equal(new AiToolResult("c1", "describe_object", 99, AiToolJudgement.Masked, "가림"), results[0]);
        Assert.Equal(new AiToolResult("c2", "run_query", 0, AiToolJudgement.Denied, "거부됨"), results[1]);
    }

    [Fact]
    public async Task A_tool_result_without_a_gate_judgement_is_failed_whatever_the_codex_status_says()
    {
        _launcher.Lines =
        [
            """{"type":"item.completed","item":{"id":"c1","type":"mcp_tool_call","server":"orandactl","tool":"t1","status":"failed","error":{"message":"x"}}}""",
            """{"type":"item.completed","item":{"id":"c2","type":"mcp_tool_call","server":"orandactl","tool":"t2","status":"completed","result":{"content":[]}}}""",
            """{"type":"turn.completed","usage":{}}""",
        ];

        var results = (await RunAsync(NewDriver(), Request())).OfType<AiToolResult>().ToList();

        // orandactl 호출은 모두 게이트를 지난다. 판단 기록이 없으면 completed 여도 게이트를 건너뛴 호출이므로 성공이 아니다.
        Assert.Equal([AiToolJudgement.Failed, AiToolJudgement.Failed], results.Select(r => r.Judgement));
    }

    private static string ToolStart(string id, string tool) =>
        """{"type":"item.started","item":{"id":"__ID__","type":"mcp_tool_call","server":"orandactl","tool":"__TOOL__","arguments":{}}}"""
            .Replace("__ID__", id, StringComparison.Ordinal).Replace("__TOOL__", tool, StringComparison.Ordinal);

    private static string ToolDone(string id, string tool, string status = "completed") =>
        """{"type":"item.completed","item":{"id":"__ID__","type":"mcp_tool_call","server":"orandactl","tool":"__TOOL__","status":"__STATUS__","result":{"content":[{"type":"text","text":"x"}]}}}"""
            .Replace("__ID__", id, StringComparison.Ordinal).Replace("__TOOL__", tool, StringComparison.Ordinal).Replace("__STATUS__", status, StringComparison.Ordinal);

    private const string AnswerLine = """{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"끝"}}""";
    private const string DoneLine = """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""";

    private async Task<List<AiEvent>> RunToolsAsync(string[] toolLines, (string Tool, AiToolJudgement Judgement)[] judged, string? resume = null, JsonElement? schema = null)
    {
        _launcher.Lines = ["""{"type":"thread.started","thread_id":"th-1"}""", .. toolLines, AnswerLine, DoneLine];
        _launcher.OnStart = _ =>
        {
            var n = 0;
            foreach (var (tool, judgement) in judged)
            {
                _gate.Raise(new AgentToolActivityEventArgs("run-1", ++n, tool, judgement, 1, null));
            }
        };
        return await RunAsync(NewDriver(), Request(resume: resume, schema: schema));
    }

    [Fact]
    public async Task One_judged_success_passes_even_beside_a_failed_call_without_judgement()
    {
        var events = await RunToolsAsync(
            [ToolStart("c1", "t1"), ToolDone("c1", "t1"), ToolStart("c2", "t2"), ToolDone("c2", "t2")],
            [("t1", AiToolJudgement.Allowed)]);

        Assert.IsType<AiFinal>(events[^1]);
        Assert.DoesNotContain(events, e => e is AiError);
    }

    [Fact]
    public async Task Calls_without_any_gate_judgement_close_the_run_as_all_tools_failed_and_emit_no_final()
    {
        var events = await RunToolsAsync([ToolStart("c1", "t1"), ToolDone("c1", "t1")], []);

        Assert.Equal("all_tools_failed", CodeOf(events));
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task A_failed_judgement_with_no_success_closes_the_run_even_when_mixed_with_a_denial()
    {
        var events = await RunToolsAsync(
            [ToolStart("c1", "t1"), ToolDone("c1", "t1"), ToolStart("c2", "t2"), ToolDone("c2", "t2", "failed")],
            [("t1", AiToolJudgement.Denied), ("t2", AiToolJudgement.Failed)]);

        Assert.Equal("all_tools_failed", CodeOf(events));
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task Only_denials_do_not_close_the_run_and_a_notice_precedes_the_answer_on_a_new_conversation_only()
    {
        string[] lines = [ToolStart("c1", "t1"), ToolDone("c1", "t1", "failed")];
        (string, AiToolJudgement)[] denied = [("t1", AiToolJudgement.Denied)];

        var first = await RunToolsAsync(lines, denied);
        Assert.IsType<AiFinal>(first[^1]);
        Assert.Contains(first.OfType<AiTextDelta>(), d => d.Text.Contains("모든 도구 호출이 거부되어", StringComparison.Ordinal));

        var resumed = await RunToolsAsync(lines, denied, resume: "01a19f88-3e8b-7531-9054-07ec9d7111ea");
        Assert.IsType<AiFinal>(resumed[^1]);
        Assert.DoesNotContain(resumed.OfType<AiTextDelta>(), d => d.Text.Contains("[안내]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_calls_at_all_notice_only_a_structured_new_conversation_and_never_in_the_result()
    {
        var plain = await RunToolsAsync([], []);
        Assert.DoesNotContain(plain.OfType<AiTextDelta>(), d => d.Text.Contains("[안내]", StringComparison.Ordinal));

        var resumed = await RunToolsAsync([], [], resume: "01a19f88-3e8b-7531-9054-07ec9d7111ea", schema: TuningResult.Schema());
        Assert.DoesNotContain(resumed.OfType<AiTextDelta>(), d => d.Text.Contains("[안내]", StringComparison.Ordinal));
    }

    [Fact]
    public void The_stale_run_age_is_longer_than_the_codex_run_limit_so_a_long_run_folder_is_never_swept()
    {
        Assert.True(SystemCliRunner.StaleRunAge > CodexDriver.DefaultTimeout);
    }

    [Fact]
    public async Task Cancel_kills_the_process_revokes_the_token_and_deletes_the_run_folder()
    {
        _launcher.Blocking = true;
        using var cancel = new CancellationTokenSource();
        var task = Task.Run(() => RunAsync(NewDriver(), Request(), cancel.Token));
        await _launcher.Started.Task;
        var folder = Assert.Single(_launcher.Launches).WorkingDirectory;

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.True(_launcher.Process!.Killed);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_revoked_session_stops_the_run()
    {
        var request = Request();
        _launcher.Lines = GoodRun();
        request.Session!.Revoke();

        Assert.Equal("session_revoked", CodeOf(await RunAsync(NewDriver(), request)));
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task The_time_limit_ends_a_hung_run_with_cli_timeout()
    {
        _launcher.Blocking = true;

        var events = await RunAsync(NewDriver(timeout: TimeSpan.FromMilliseconds(150)), Request());

        Assert.Equal("cli_timeout", CodeOf(events));
        Assert.True(_launcher.Process!.Killed);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
    }

    [Fact]
    public async Task A_missing_executable_is_cli_not_found_and_a_batch_file_is_refused_without_a_process()
    {
        _existing.Remove(Codex);
        Assert.Equal("cli_not_found", CodeOf(await RunAsync(NewDriver(), Request())));

        // 배치 파일(.cmd/.bat)은 인자가 깨지므로 거부한다. 찾기 결과를 .cmd 로 만들어 판정만 확인한다.
        var (executable, error) = SystemCliExecutable.Locate("codex", "Codex CLI", "/fake/bin", path => path == Path.Combine("/fake/bin", "codex.cmd") || path == Path.Combine("/fake/bin", "codex"));
        if (OperatingSystem.IsWindows())
        {
            // Windows 는 .exe 가 없으면 npm 의 .cmd 껍데기를 찾고, 배치 파일이므로 거부한다.
            Assert.Null(executable);
            Assert.Equal("cli_unsupported", error?.Code);
        }
        else
        {
            Assert.Null(error);
            Assert.NotNull(executable);
        }

        Assert.True(Oranda.Platform.Ai.Cli.ClaudeExecutable.IsBatchFile("/x/codex.cmd"));
        Assert.True(Oranda.Platform.Ai.Cli.ClaudeExecutable.IsBatchFile("/x/codex.bat"));
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Preconditions_are_checked_before_anything_else()
    {
        Assert.Equal("session_required", CodeOf(await RunAsync(NewDriver(), Request() with { Session = null })));
        Assert.Equal("bridge_not_ready", CodeOf(await RunAsync(NewDriver(pipeName: null), Request())));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_catalogCalls);
    }

    [Fact]
    public async Task A_structured_request_gets_a_strict_schema_file_and_returns_the_cleaned_result_without_raw_json_text()
    {
        const string answer = """{"summary":"s","rewrites":[{"label":"a","sql":"x","rationale":"r","benchmark":null}],"index_suggestions":[],"evidence":[]}""";
        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "m", type = "agent_message", text = answer } }),
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""",
        ];
        string? schemaOnDisk = null;
        _launcher.OnStart = launch => schemaOnDisk = File.ReadAllText(Path.Combine(launch.WorkingDirectory, "output-schema.json"));

        var events = await RunAsync(NewDriver(), Request(schema: TuningResult.Schema()));

        // JSON 원문을 화면에 내지 않는다. 도구를 안 쓴 구조 결과 실행이므로 근거 없음 안내만 글로 나간다.
        var notice = Assert.Single(events.OfType<AiTextDelta>());
        Assert.Contains("실측 근거가 없습니다", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("summary", notice.Text, StringComparison.Ordinal);
        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.Null(final.Text);
        Assert.False(final.Result!.Value.GetProperty("rewrites")[0].TryGetProperty("benchmark", out _));
        Assert.Contains("\"additionalProperties\":false", schemaOnDisk, StringComparison.Ordinal);
        var arguments = Assert.Single(_launcher.Launches).Arguments;
        Assert.Contains("--output-schema", arguments);
    }

    [Fact]
    public async Task A_structured_request_answered_with_plain_text_is_an_error()
    {
        _launcher.Lines = GoodRun("JSON 이 아닌 글");

        var events = await RunAsync(NewDriver(), Request(schema: TuningResult.Schema()));

        Assert.Equal("cli_error", CodeOf(events));
    }

    [Fact]
    public async Task Images_are_written_to_run_folder_files_and_passed_with_dash_i()
    {
        _launcher.Lines = GoodRun();
        var existed = new List<bool>();
        _launcher.OnStart = launch =>
        {
            var arguments = launch.Arguments.ToList();
            for (var i = 0; i < arguments.Count - 1; i++)
            {
                if (arguments[i] == "-i")
                {
                    existed.Add(File.Exists(arguments[i + 1]) && arguments[i + 1].StartsWith(launch.WorkingDirectory, StringComparison.Ordinal));
                }
            }
        };

        await RunAsync(NewDriver(), Request(images: [new AiImageAttachment("image/png", [1, 2, 3]), new AiImageAttachment("image/jpeg", [4])]));

        Assert.Equal([true, true], existed);
    }

    [Fact]
    public async Task An_empty_prompt_or_unsupported_image_is_refused_before_launch()
    {
        var empty = Request() with { Prompt = " " };
        Assert.Equal("prompt_empty", CodeOf(await RunAsync(NewDriver(), empty)));
        Assert.Equal("image_invalid", CodeOf(await RunAsync(NewDriver(), Request(images: [new AiImageAttachment("image/bmp", [1])]))));
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task The_instructions_notice_is_the_first_text_of_a_new_conversation_only_and_never_in_the_final()
    {
        var home = Path.Combine("/home/x", ".codex", "AGENTS.md");
        _existing.Add(home);
        _launcher.Lines = GoodRun("본문");

        var fresh = await RunAsync(NewDriver(), Request());

        var firstText = fresh.OfType<AiTextDelta>().First();
        Assert.Contains("AGENTS.md", firstText.Text, StringComparison.Ordinal);
        var final = Assert.IsType<AiFinal>(fresh[^1]);
        Assert.Equal("본문", final.Text);
        Assert.DoesNotContain("AGENTS.md", final.Text, StringComparison.Ordinal);

        var resumed = await RunAsync(NewDriver(), Request("01a19f88-3e8b-7531-9054-07ec9d7111ea"));

        Assert.DoesNotContain(resumed.OfType<AiTextDelta>(), d => d.Text.Contains("AGENTS.md", StringComparison.Ordinal));
        Assert.Equal("본문", Assert.IsType<AiFinal>(resumed[^1]).Text);
    }

    [Fact]
    public async Task The_notice_follows_CODEX_HOME_and_is_absent_when_no_file_exists()
    {
        _launcher.Lines = GoodRun();
        Assert.DoesNotContain(await RunAsync(NewDriver(), Request()), e => e is AiTextDelta { Text: var t } && t.Contains("AGENTS.md", StringComparison.Ordinal));

        _env["CODEX_HOME"] = "/custom/codex";
        _existing.Add(Path.Combine("/custom/codex", "AGENTS.md"));
        var events = await RunAsync(NewDriver(), Request());

        Assert.Contains(events, e => e is AiTextDelta { Text: var t } && t.Contains("/custom/codex", StringComparison.Ordinal));
    }

    private const string VersionNotice = "[안내] 이 버전의 Codex CLI 는 Oranda 에서 잠금을 검증하지 않았습니다.\n\n";

    [Theory]
    [InlineData("codex-cli 0.166.0")]
    public async Task A_verified_version_gives_no_notice(string output)
    {
        _versionOutput = output;
        _launcher.Lines = GoodRun();

        var events = await RunAsync(NewDriver(), Request());

        Assert.DoesNotContain(events, e => e is AiTextDelta { Text: var t } && t.Contains("[안내]", StringComparison.Ordinal));
        Assert.Equal(1, _versionReads);
    }

    [Theory]
    [InlineData("codex-cli 0.167.0")]
    [InlineData("무슨 글")]
    [InlineData(null)]
    public async Task An_unverified_or_unreadable_version_is_noticed_once_after_the_start_event_and_the_run_still_happens(string? output)
    {
        _versionOutput = output;
        _launcher.Lines = GoodRun("본문");

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal(new AiStarted("th-1"), events[0]);
        Assert.Equal(new AiTextDelta(VersionNotice), events[1]);
        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.Equal("본문", final.Text); // 안내가 최종 글에 섞이지 않는다
        Assert.Single(_launcher.Launches);
        Assert.Equal(0, _issuedAtRead); // 버전은 토큰 발급 전에 읽는다
    }

    [Fact]
    public async Task The_version_notice_comes_before_the_instructions_notice_and_neither_is_in_the_final_or_the_structured_result()
    {
        _versionOutput = "codex-cli 9.9.9";
        _existing.Add(Path.Combine("/home/x", ".codex", "AGENTS.md"));
        const string answer = """{"summary":"s","rewrites":[],"index_suggestions":[],"evidence":[]}""";
        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "m", type = "agent_message", text = answer } }),
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""",
        ];

        var events = await RunAsync(NewDriver(), Request(schema: TuningResult.Schema()));

        var texts = events.OfType<AiTextDelta>().Select(d => d.Text).ToList();
        Assert.Equal(3, texts.Count); // 버전 안내 · 지시 파일 안내 · 도구 없이 답함 안내
        Assert.Contains("[안내]", texts[0], StringComparison.Ordinal);
        Assert.Contains("AGENTS.md", texts[1], StringComparison.Ordinal);
        Assert.Contains("도구를 쓰지 않고", texts[2], StringComparison.Ordinal);
        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.Null(final.Text);
        Assert.DoesNotContain("[안내]", final.Result!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_version_is_not_read_on_resume_or_when_a_refusal_closes_first_and_the_token_is_not_issued_before_reading()
    {
        _versionOutput = "codex-cli 9.9.9";
        _launcher.Lines = GoodRun();

        var resumed = await RunAsync(NewDriver(), Request("01a19f88-3e8b-7531-9054-07ec9d7111ea"));
        Assert.Equal(0, _versionReads);
        Assert.DoesNotContain(resumed, e => e is AiTextDelta { Text: var t } && t.Contains("[안내]", StringComparison.Ordinal));

        Assert.Equal("session_required", CodeOf(await RunAsync(NewDriver(), Request() with { Session = null })));
        Assert.Equal("resume_invalid", CodeOf(await RunAsync(NewDriver(), Request("--last"))));
        _existing.Remove(Codex);
        Assert.Equal("cli_not_found", CodeOf(await RunAsync(NewDriver(), Request())));
        Assert.Equal(0, _versionReads);
    }

    [Fact]
    public void The_codex_verified_version_list_is_the_probed_one()
    {
        Assert.Equal(["0.166.0"], CodexDriver.VerifiedVersions);
        Assert.Equal("0.166.0", SystemCliVersion.Parse("codex-cli 0.166.0"));
    }

    [Fact]
    public void The_driver_is_registered_for_the_marker_interface_with_the_codex_id()
    {
        var attribute = typeof(CodexDriver).GetCustomAttributes(typeof(Oranda.Core.Composition.OrandaServiceAttribute), false).Cast<Oranda.Core.Composition.OrandaServiceAttribute>().Single();

        Assert.Equal(typeof(ISystemCliAiDriver), attribute.ServiceType);
        Assert.Equal(AiProfilePresets.CodexCliId, NewDriver().CliId);
    }
}
