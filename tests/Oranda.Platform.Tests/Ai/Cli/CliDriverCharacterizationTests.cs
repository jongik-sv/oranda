using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// CliDriver 에서 실행 골격을 떼어 내도 유지돼야 하는 동작(토큰 폐기·실행 폴더 삭제·게이트 판단 전달)을 고정한다.
/// CliDriverTests 가 확인하는 성공·취소 경로는 되풀이하지 않고, 비어 있던 경로만 다룬다.
/// </summary>
public sealed class CliDriverCharacterizationTests : IDisposable
{
    private const string Init =
        """{"type":"system","subtype":"init","session_id":"s1","mcp_servers":[{"name":"orandactl","status":"connected"}],"tools":["StructuredOutput","mcp__orandactl__oranda_ping","mcp__orandactl__oranda_other"]}""";

    private const string Result =
        """{"type":"result","subtype":"success","is_error":false,"session_id":"s1","result":"done"}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-clidriver-char-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly FakeLauncher _launcher = new();

    public CliDriverCharacterizationTests() => Directory.CreateDirectory(_dir);

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
    public async Task Start_failure_revokes_the_token_removes_the_run_folder_and_unsubscribes_the_gate()
    {
        _launcher.OnStart = _ => throw new InvalidOperationException("cannot start");

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_start_failed", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.NotEmpty(_tokens.Issued);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.Empty(Directory.EnumerateDirectories(RunsDirectory));
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task Init_failure_removes_the_run_folder_and_unsubscribes_the_gate()
    {
        _launcher.Lines =
        [
            """{"type":"system","subtype":"init","session_id":"s1","mcp_servers":[{"name":"orandactl","status":"failed"}],"tools":["StructuredOutput"]}""",
        ];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("bridge_unavailable", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.True(_launcher.Process!.Killed);
        Assert.NotEmpty(_tokens.Issued);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.Empty(Directory.EnumerateDirectories(RunsDirectory));
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task Prepare_failure_revokes_the_token_and_unsubscribes_the_gate_without_launching()
    {
        // runs 자리에 파일이 있으면 실행 폴더를 만들 수 없다(IOException → cli_start_failed).
        Directory.CreateDirectory(Path.Combine(_dir, "ai"));
        await File.WriteAllTextAsync(RunsDirectory, "not a folder");

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal("cli_start_failed", Assert.IsType<AiError>(Assert.Single(events)).Code);
        Assert.Empty(_launcher.Launches);
        Assert.NotEmpty(_tokens.Issued);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task Next_run_deletes_stale_run_folders_but_keeps_recent_ones()
    {
        var stale = Directory.CreateDirectory(Path.Combine(EnsureRunsDirectory(), "left-over-stale"));
        var recent = Directory.CreateDirectory(Path.Combine(RunsDirectory, "other-run-in-progress"));
        stale.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-7);
        _launcher.Lines = [Init, Result];

        await RunAsync(NewDriver(), Request());

        Assert.False(Directory.Exists(stale.FullName));
        Assert.True(Directory.Exists(recent.FullName));
    }

    [Fact]
    public async Task Denied_judgement_and_its_note_reach_the_tool_card_in_call_order_per_tool()
    {
        _launcher.Lines =
        [
            Init,
            ToolUse("c1", "oranda_ping"),
            ToolUse("c2", "oranda_other"),
            ToolUse("c3", "oranda_ping"),
            ToolResult("c1", isError: false),
            ToolResult("c2", isError: false),
            ToolResult("c3", isError: false),
            Result,
        ];
        _launcher.OnStart = _ =>
        {
            // 같은 도구의 판단은 호출 순서대로 짝지어지고, 다른 도구의 판단과 섞이지 않는다.
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "oranda_ping", AiToolJudgement.Denied, 0, "허용 수준 때문에 거부"));
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 2, "oranda_other", AiToolJudgement.Masked, 7, "masked"));
            _gate.Raise(new AgentToolActivityEventArgs("run-1", 3, "oranda_ping", AiToolJudgement.Allowed, 5, null));
        };

        var events = await RunAsync(NewDriver(), Request());

        var results = events.OfType<AiToolResult>().ToList();
        Assert.Equal(["c1", "c2", "c3"], results.Select(r => r.CallId));
        Assert.Equal([AiToolJudgement.Denied, AiToolJudgement.Masked, AiToolJudgement.Allowed], results.Select(r => r.Judgement));
        Assert.Equal([0, 7, 5], results.Select(r => r.Size));
        Assert.Equal("허용 수준 때문에 거부", results[0].Note);
        Assert.Equal("masked", results[1].Note);
        Assert.Null(results[2].Note);
    }

    [Fact]
    public async Task Tool_result_without_a_gate_record_is_reported_as_failed_when_it_is_an_error_and_allowed_otherwise()
    {
        _launcher.Lines =
        [
            Init,
            ToolUse("c1", "oranda_ping"),
            ToolUse("c2", "oranda_other"),
            ToolResult("c1", isError: true),
            ToolResult("c2", isError: false),
            Result,
        ];

        var events = await RunAsync(NewDriver(), Request());

        Assert.Equal(
            [AiToolJudgement.Failed, AiToolJudgement.Allowed],
            events.OfType<AiToolResult>().Select(r => r.Judgement));
    }

    [Fact]
    public async Task Kill_throwing_still_revokes_the_token_removes_the_run_folder_and_unsubscribes_the_gate()
    {
        _launcher.Lines = [Init, Result];
        _launcher.KillThrows = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(NewDriver(), Request()));

        Assert.True(_launcher.Process!.Killed);
        Assert.NotEmpty(_tokens.Issued);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.Empty(Directory.EnumerateDirectories(RunsDirectory));
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task Cancellation_unsubscribes_the_gate()
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
        Assert.True(_gate.HasSubscribers);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(_gate.HasSubscribers);
    }

    private string RunsDirectory => Path.Combine(_dir, "ai", "runs");

    private string EnsureRunsDirectory()
    {
        Directory.CreateDirectory(RunsDirectory);
        return RunsDirectory;
    }

    private static string ToolUse(string id, string tool)
        => """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"ID","name":"mcp__orandactl__TOOL","input":{}}]},"session_id":"s1"}"""
            .Replace("ID", id, StringComparison.Ordinal)
            .Replace("TOOL", tool, StringComparison.Ordinal);

    private static string ToolResult(string id, bool isError)
    {
        var flag = isError ? "true" : "false";
        return """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"ID","content":[{"type":"text","text":"x"}],"is_error":FLAG}]},"session_id":"s1"}"""
            .Replace("ID", id, StringComparison.Ordinal)
            .Replace("FLAG", flag, StringComparison.Ordinal);
    }

    private static AiRequest Request()
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest("the prompt", context, Session: new AgentSession(new AgentRunScope("run-1", profile, context)));
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

    private CliDriver NewDriver()
    {
        var cli = Path.Combine(_dir, "claude.exe");
        var orandactl = Path.Combine(_dir, "orandactl.exe");
        return new CliDriver(
            _tokens,
            _gate,
            new FixedEndpoint("bridge-pipe"),
            new AppDataPaths(_dir),
            new FixedSettings(new AppSettings { Ai = new AiSettings { ExecutablePath = cli } }),
            _launcher,
            orandactl,
            () => null,
            path => path == orandactl || path == cli);
    }
}
