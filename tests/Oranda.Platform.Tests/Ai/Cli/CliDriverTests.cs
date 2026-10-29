using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

public sealed class CliDriverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-clidriver-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly FakeLauncher _launcher = new();

    public CliDriverTests() => Directory.CreateDirectory(_dir);

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
    public async Task Successful_run_translates_the_recorded_stream_and_cleans_up()
    {
        _launcher.Lines = File.ReadAllLines(FixturePath("two-tool-calls.jsonl"));
        _launcher.OnStart = launch =>
        {
            // 실행 중에는 mcp-config 가 있고, 토큰·파이프 이름이 그 안에 있다.
            var config = JsonDocument.Parse(File.ReadAllText(ArgumentAfter(launch.Arguments, "--mcp-config")));
            var server = config.RootElement.GetProperty("mcpServers").GetProperty("orandactl");
            Assert.Equal(_tokens.Issued[0], server.GetProperty("env").GetProperty("ORANDA_RUN_TOKEN").GetString());
            Assert.Equal(["mcp", "--pipe", "bridge-pipe"], server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
            Assert.DoesNotContain(_tokens.Issued[0], launch.Arguments);

            // 게이트가 두 번 판단했다(먼저 가림, 다음 허용).
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "oranda_ping", AiToolJudgement.Masked, 11, "masked"));
            _gate.Raise(new AgentToolActivityEventArgs("other-run", 1, "oranda_ping", AiToolJudgement.Denied, 1, null));
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 2, "oranda_ping", AiToolJudgement.Allowed, 12, null));
        };
        var driver = NewDriver();

        var events = await RunAsync(driver, Request(resumeId: "sid-prev", model: "haiku", schema: """{"type":"object"}""", effort: "high"));

        Assert.IsType<AiStarted>(events[0]);
        Assert.Equal(2, events.OfType<AiToolCall>().Count());
        var results = events.OfType<AiToolResult>().ToList();
        Assert.Equal([AiToolJudgement.Masked, AiToolJudgement.Allowed], results.Select(r => r.Judgement));
        Assert.Equal([11, 12], results.Select(r => r.Size));
        Assert.IsType<AiUsage>(events[^2]);
        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.Equal("tok=abc-test-123, tok=abc-test-123", final.Result!.Value.GetProperty("result").GetString());
        Assert.NotNull(final.ResumeId);

        // 인자·프롬프트
        var launch = _launcher.Launches.Single();
        Assert.Equal("the prompt", launch.Prompt);
        Assert.Equal("sid-prev", ArgumentAfter(launch.Arguments, "--resume"));
        Assert.Equal("haiku", ArgumentAfter(launch.Arguments, "--model"));
        Assert.Equal("high", ArgumentAfter(launch.Arguments, "--effort"));
        Assert.Equal("""{"type":"object"}""", ArgumentAfter(launch.Arguments, "--json-schema"));
        Assert.True(Directory.Exists(launch.WorkingDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(launch.WorkingDirectory));

        // 정리: 토큰 폐기, 프로세스 종료, 실행 폴더 삭제, 게이트 구독 해제
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.True(_launcher.Process!.Killed);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "ai", "runs")));
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task Request_without_session_bridge_orandactl_or_cli_fails_with_a_code()
    {
        Assert.Equal("session_required", await ErrorCodeAsync(NewDriver(), Request(withSession: false)));
        Assert.Equal("bridge_not_ready", await ErrorCodeAsync(NewDriver(pipeName: null), Request()));
        Assert.Equal("orandactl_missing", await ErrorCodeAsync(NewDriver(orandactlExists: false), Request()));
        Assert.Equal("cli_not_found", await ErrorCodeAsync(NewDriver(cliExists: false), Request()));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task Revoked_session_and_batch_file_cli_are_refused_before_launch()
    {
        var revoked = Request();
        revoked.Session!.Revoke();
        Assert.Equal("session_revoked", await ErrorCodeAsync(NewDriver(), revoked));
        Assert.Equal("cli_unsupported", await ErrorCodeAsync(NewDriver(cliFile: "claude.cmd"), Request()));
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task Unexpected_mcp_server_in_init_stops_the_run_and_kills_the_process()
    {
        _launcher.Lines =
        [
            """{"type":"system","subtype":"init","session_id":"s1","mcp_servers":[{"name":"orandactl","status":"connected"},{"name":"claude.ai Gmail","status":"connected"}],"tools":["mcp__orandactl__x"]}""",
            """{"type":"result","subtype":"success","is_error":false,"session_id":"s1","structured_output":{"rows":1}}""",
        ];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("unexpected_mcp_server", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.True(_launcher.Process!.Killed);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
    }

    [Fact]
    public async Task Effort_falls_back_to_the_settings_value_when_the_request_has_none()
    {
        _launcher.Lines =
        [
            """{"type":"result","subtype":"success","is_error":false,"session_id":"s1","structured_output":{"rows":1}}""",
        ];

        await RunAsync(NewDriver(settingsEffort: "medium"), Request());

        Assert.Equal("medium", ArgumentAfter(_launcher.Launches.Single().Arguments, "--effort"));
    }

    [Fact]
    public async Task Tool_call_before_init_fails_closed()
    {
        _launcher.Lines =
        [
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"mcp__orandactl__x","input":{}}]},"session_id":"s1"}""",
        ];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("init_missing", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.True(_launcher.Process!.Killed);
    }

    [Theory]
    [InlineData("""[{"name":"orandactl","status":"connected"}]""", """["StructuredOutput","mcp__orandactl__a","ToolSearch"]""", null)]
    [InlineData("""[{"name":"orandactl","status":"failed"}]""", """["StructuredOutput"]""", "bridge_unavailable")]
    [InlineData("""[]""", """["mcp__orandactl__a"]""", "bridge_unavailable")]
    [InlineData("""[{"name":"orandactl","status":"connected"}]""", """["StructuredOutput"]""", "bridge_unavailable")]
    [InlineData("""[{"name":"orandactl","status":"connected"}]""", """["Read","mcp__orandactl__a"]""", "unexpected_tool")]
    [InlineData("""[{"name":"other","status":"connected"},{"name":"orandactl","status":"connected"}]""", """["mcp__orandactl__a"]""", "unexpected_mcp_server")]
    public void Init_validation_requires_only_a_connected_orandactl_with_tools(string servers, string tools, string? expectedCode)
    {
        var parser = new ClaudeStreamParser();
        var init = Assert.IsType<ClaudeSessionStarted>(Assert.Single(parser.Parse(
            """{"type":"system","subtype":"init","session_id":"s1","mcp_servers":""" + servers + ""","tools":""" + tools + "}")));

        Assert.Equal(expectedCode, CliDriver.ValidateInit(init)?.Code);
    }

    [Fact]
    public async Task Process_that_ends_without_a_result_reports_cli_failed_with_stderr()
    {
        _launcher.Lines = [];
        _launcher.StandardError = "boom on stderr";
        _launcher.ExitCode = 3;

        var events = await RunAsync(NewDriver(), Request());

        var error = Assert.IsType<AiError>(Assert.Single(events));
        Assert.Equal("cli_failed", error.Code);
        Assert.Contains("종료 코드 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("boom on stderr", error.Message, StringComparison.Ordinal);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_revokes_the_token()
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
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "ai", "runs")));
    }

    [Fact]
    public void Executable_lookup_prefers_the_native_exe_next_to_the_npm_shim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\npm\claude.cmd",
            @"C:\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe",
        };

        Assert.Equal(@"C:\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", ClaudeExecutable.Find(null, @"C:\x;C:\npm", existing.Contains));
        Assert.Equal(@"C:\own\claude.exe", ClaudeExecutable.Find(@"C:\own\claude.exe", @"C:\npm", p => p == @"C:\own\claude.exe"));
        Assert.Null(ClaudeExecutable.Find(@"C:\missing.exe", @"C:\npm", existing.Contains));
        Assert.Null(ClaudeExecutable.Find(null, @"C:\none", existing.Contains));
        Assert.True(ClaudeExecutable.IsBatchFile(@"C:\npm\claude.cmd"));
        Assert.False(ClaudeExecutable.IsBatchFile(@"C:\npm\claude.exe"));
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

    private static AiRequest Request(bool withSession = true, string? resumeId = null, string? model = null, string? schema = null, string? effort = null)
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest(
            "the prompt",
            context,
            resumeId,
            schema is null ? null : JsonDocument.Parse(schema).RootElement.Clone(),
            model,
            withSession ? new AgentSession(new AgentRunScope("run-1", profile, context)) : null,
            effort);
    }

    private static async Task<List<AiEvent>> RunAsync(CliDriver driver, AiRequest request)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private static async Task<string?> ErrorCodeAsync(CliDriver driver, AiRequest request)
        => Assert.IsType<AiError>(Assert.Single(await RunAsync(driver, request))).Code;

    private CliDriver NewDriver(string? pipeName = "bridge-pipe", bool orandactlExists = true, bool cliExists = true, string cliFile = "claude.exe", string? settingsEffort = null)
    {
        var cli = Path.Combine(_dir, cliFile);
        var orandactl = Path.Combine(_dir, "orandactl.exe");
        return new CliDriver(
            _tokens,
            _gate,
            new FixedEndpoint(pipeName),
            new AppDataPaths(_dir),
            new FixedSettings(new AppSettings { Ai = new AiSettings { ExecutablePath = cli, Effort = settingsEffort } }),
            _launcher,
            orandactl,
            () => null,
            path => path == orandactl ? orandactlExists : path == cli && cliExists);
    }

    private sealed class FixedEndpoint(string? pipeName) : IAgentBridgeEndpoint
    {
        public string? PipeName { get; } = pipeName;
    }

    private sealed class FixedSettings(AppSettings settings) : ISettingsStore
    {
        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(settings));

        public Task SaveAsync(AppSettings value, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeLauncher : IClaudeProcessLauncher
    {
        public string[] Lines { get; set; } = [];

        public string StandardError { get; set; } = string.Empty;

        public int ExitCode { get; set; }

        public bool Blocking { get; set; }

        public Action<ClaudeLaunch>? OnStart { get; set; }

        public List<ClaudeLaunch> Launches { get; } = [];

        public FakeProcess? Process { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IClaudeProcess Start(ClaudeLaunch launch)
        {
            Launches.Add(launch);
            OnStart?.Invoke(launch);
            Process = new FakeProcess(this);
            Started.TrySetResult();
            return Process;
        }
    }

    private sealed class FakeProcess(FakeLauncher launcher) : IClaudeProcess
    {
        public bool Killed { get; private set; }

        public string StandardErrorTail => launcher.StandardError;

        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (launcher.Blocking)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            foreach (var line in launcher.Lines)
            {
                yield return line;
            }
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(launcher.ExitCode);

        public void Kill() => Killed = true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
