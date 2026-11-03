using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>CLI 종류와 무관한 실행 골격(<see cref="SystemCliRunner"/>·<see cref="SystemCliRun"/>)을 고정한다.</summary>
public sealed class SystemCliRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-syscli-runner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly FakeLauncher _launcher = new();

    public SystemCliRunnerTests() => Directory.CreateDirectory(_dir);

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
    public void Preconditions_report_missing_session_revoked_session_closed_bridge_and_missing_orandactl()
    {
        var request = Request();

        Assert.Equal("session_required", NewRunner().CheckPreconditions(request with { Session = null }).Error!.Code);

        request.Session!.Revoke();
        Assert.Equal("session_revoked", NewRunner().CheckPreconditions(request).Error!.Code);

        Assert.Equal("bridge_not_ready", NewRunner(pipeName: null).CheckPreconditions(Request()).Error!.Code);
        Assert.Equal("orandactl_missing", NewRunner(orandactlExists: false).CheckPreconditions(Request()).Error!.Code);

        var ok = NewRunner().CheckPreconditions(Request());
        Assert.Null(ok.Error);
        Assert.Equal("bridge-pipe", ok.PipeName);
    }

    [Fact]
    public async Task Prepare_failure_is_an_error_and_launches_nothing_and_dispose_cleans_up()
    {
        // runs 자리에 파일이 있으면 실행 폴더를 만들 수 없다.
        Directory.CreateDirectory(Path.Combine(_dir, "ai"));
        var runner = NewRunner();
        var run = runner.Begin(Request().Session!, "bridge-pipe", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_dir, "ai", "blocker"), "file");

        var (_, error) = SystemCliRun.Prepare(() =>
        {
            Directory.CreateDirectory(Path.Combine(_dir, "ai", "blocker", "x"));
            return 0;
        });
        await run.DisposeAsync();

        Assert.Equal("cli_start_failed", error!.Code);
        Assert.Empty(_launcher.Launches);
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task Watch_kills_the_process_and_reports_unexpected_tool_for_a_tool_outside_the_allow_list()
    {
        var run = NewRunner().Begin(Request().Session!, "bridge-pipe", CancellationToken.None);
        await using var _ = run;
        var (process, startError) = run.Start(_launcher, Launch(), "fake");
        Assert.Null(startError);

        Assert.Null(run.RejectUnexpectedTool("mcp__orandactl__ping", IsAllowed));
        Assert.False(_launcher.Process!.Killed);

        var error = run.RejectUnexpectedTool("Bash", IsAllowed);

        Assert.NotNull(process);
        Assert.Equal("unexpected_tool", error!.Code);
        Assert.True(_launcher.Process.Killed);

        static bool IsAllowed(string tool) => tool.StartsWith("mcp__orandactl__", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_refuses_a_token_in_the_command_line_and_passes_it_through_the_environment()
    {
        var run = NewRunner().Begin(Request().Session!, "bridge-pipe", CancellationToken.None);
        await using var _ = run;

        var leaked = run.Start(_launcher, Launch(arguments: ["--token", run.Token]), "fake");
        Assert.Equal("cli_start_failed", leaked.Error!.Code);
        Assert.Empty(_launcher.Launches);

        var env = new Dictionary<string, string?> { [run.TokenEnvironment("ORANDA_AGENT_TOKEN").Key] = run.TokenEnvironment("ORANDA_AGENT_TOKEN").Value };
        var started = run.Start(_launcher, Launch(arguments: ["--print"], environment: env), "fake");

        Assert.Null(started.Error);
        Assert.Equal(run.Token, Assert.Single(_launcher.Launches).Environment!["ORANDA_AGENT_TOKEN"]);
    }

    [Fact]
    public async Task Start_failure_closes_with_an_error_without_a_process()
    {
        _launcher.OnStart = _ => throw new InvalidOperationException("cannot start");
        await using var run = NewRunner().Begin(Request().Session!, "bridge-pipe", CancellationToken.None);

        var (process, error) = run.Start(_launcher, Launch(), "fake");

        Assert.Null(process);
        Assert.Equal("cli_start_failed", error!.Code);
    }

    [Fact]
    public async Task Time_limit_cancels_the_run_and_kills_the_process()
    {
        await using var run = NewRunner().Begin(Request().Session!, "bridge-pipe", CancellationToken.None, TimeSpan.FromMilliseconds(100));
        _launcher.Blocking = true;
        var (process, _) = run.Start(_launcher, Launch(), "fake");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unused in process!.ReadLinesAsync(run.CancellationToken))
            {
            }
        });

        Assert.True(run.TimedOut);
        Assert.True(_launcher.Process!.Killed);
        Assert.Equal("cli_timeout", SystemCliRun.TimeoutError("fake", TimeSpan.FromMilliseconds(100)).Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_time_limit()
    {
        using var cts = new CancellationTokenSource();
        await using var run = NewRunner().Begin(Request().Session!, "bridge-pipe", cts.Token, TimeSpan.FromMinutes(5));

        await cts.CancelAsync();

        Assert.False(run.TimedOut);
        Assert.True(run.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_still_returns_unexpected_tool_when_kill_throws()
    {
        _launcher.KillThrows = true;
        var run = NewRunner().Begin(Request().Session!, "bridge-pipe", CancellationToken.None);
        run.Start(_launcher, Launch(), "fake");

        var error = run.RejectUnexpectedTool("Bash", _ => false);

        Assert.Equal("unexpected_tool", error!.Code);
        Assert.True(_launcher.Process!.Killed);
        _launcher.KillThrows = false;
        await run.DisposeAsync();
        Assert.Equal(_tokens.Issued, _tokens.Revoked);
    }

    [Fact]
    public async Task Dispose_finishes_cleanup_even_when_kill_throws()
    {
        _launcher.KillThrows = true;
        var run = NewRunner().Begin(Request().Session!, "bridge-pipe", CancellationToken.None);
        Directory.CreateDirectory(run.RunDirectory);
        run.Start(_launcher, Launch(), "fake");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await run.DisposeAsync());

        Assert.Equal(_tokens.Issued, _tokens.Revoked);
        Assert.False(Directory.Exists(run.RunDirectory));
        Assert.False(_gate.HasSubscribers);
    }

    [Fact]
    public async Task Judgements_queue_per_tool_in_call_order_for_this_run_only()
    {
        await using var run = NewRunner().Begin(Request().Session!, "bridge-pipe", CancellationToken.None);

        _gate.Raise(new AgentToolActivityEventArgs("run-1", 1, "a", AiToolJudgement.Denied, 0, "no"));
        _gate.Raise(new AgentToolActivityEventArgs("run-1", 2, "a", AiToolJudgement.Allowed, 5, null));
        _gate.Raise(new AgentToolActivityEventArgs("other-run", 3, "a", AiToolJudgement.Allowed, 9, null));

        Assert.True(run.Judgements.TryTake("a", out var first));
        Assert.Equal(AiToolJudgement.Denied, first.Judgement);
        Assert.True(run.Judgements.TryTake("a", out var second));
        Assert.Equal(AiToolJudgement.Allowed, second.Judgement);
        Assert.False(run.Judgements.TryTake("a", out _));
        Assert.False(run.Judgements.TryTake("b", out _));
    }

    private SystemCliRunner NewRunner(string? pipeName = "bridge-pipe", bool orandactlExists = true)
    {
        var orandactl = Path.Combine(_dir, "orandactl");
        return new SystemCliRunner(
            _tokens,
            _gate,
            new FixedEndpoint(pipeName),
            new AppDataPaths(_dir),
            orandactl,
            path => orandactlExists && path == orandactl);
    }

    private ClaudeLaunch Launch(IReadOnlyList<string>? arguments = null, IReadOnlyDictionary<string, string?>? environment = null)
        => new(Path.Combine(_dir, "fake-cli"), arguments ?? ["--print"], _dir, "prompt", environment);

    private static AiRequest Request()
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest("the prompt", context, Session: new AgentSession(new AgentRunScope("run-1", profile, context)));
    }
}
