using Oranda.Core.Ai.Setup;
using Oranda.Platform.Ai.Setup;

namespace Oranda.Platform.Tests.Ai.Setup;

/// <summary>계약(<see cref="IClaudeSetupService"/>) 구현: 엔진 결과를 계약 형태로 옮기는지 본다. 가짜 실행기만 쓴다.</summary>
public sealed class ClaudeSetupServiceTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "oranda-setup-svc-home");
    private static readonly string Installed = Path.Combine(Home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
    private const string Link = "\u001b]8;;https://claude.ai/oauth/authorize?abc\u0007https://claude.ai/oauth/authorize?abc\u001b]8;;\u0007";

    private readonly HashSet<string> _files = [];

    [Fact]
    public async Task Find_maps_path_and_version()
    {
        _files.Add(Installed);
        var service = Service(new FakeSetupRunner(_ => FakeScript.Out(0, "2.1.330 (Claude Code)")));

        Assert.Equal(new ClaudeInstallation(Installed, "2.1.330"), await service.FindAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_install_always_carries_the_manual_command_and_a_kind()
    {
        var service = Service(new FakeSetupRunner(_ => new FakeScript([new SetupLine("Download failed", true)], ExitCode: 1)));

        var result = await service.InstallAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Installation);
        Assert.Equal(AiSetupErrorKind.Network, result.ErrorKind);
        Assert.Equal(ClaudeSetupEngine.ManualInstallCommand, result.ManualCommand);
        Assert.Equal("Download failed", result.Detail);
    }

    [Fact]
    public async Task Successful_install_has_no_error_kind()
    {
        var service = Service(new FakeSetupRunner(launch =>
        {
            if (launch.Arguments.Contains("--version"))
            {
                return FakeScript.Out(0, "2.1.330 (Claude Code)");
            }

            _files.Add(Installed);
            return FakeScript.Out(0, "✅ Installation complete!");
        }));

        var result = await service.InstallAsync(null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.ErrorKind);
        Assert.Equal("2.1.330", result.Installation!.Version);
        Assert.False(string.IsNullOrEmpty(result.ManualCommand));
    }

    [Fact]
    public async Task Login_without_claude_does_not_start()
    {
        var start = await Service(new FakeSetupRunner(_ => FakeScript.Out(0))).StartLoginAsync(null, CancellationToken.None);

        Assert.Equal(new ClaudeLoginStart(false, null, null, AiSetupErrorKind.CliNotFound), start);
    }

    [Fact]
    public async Task Login_start_returns_the_address_and_the_handle_ends_the_process()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Hang("Opening browser to sign in…", Link));

        var start = await Service(runner).StartLoginAsync(null, CancellationToken.None);

        Assert.True(start.Started);
        Assert.Equal("https://claude.ai/oauth/authorize?abc", start.BrowserUrl);
        Assert.NotNull(start.Exited);
        Assert.False(start.Exited!.IsCompleted);

        await start.Handle!.DisposeAsync();
        await start.Handle.DisposeAsync(); // 두 번 닫아도 된다

        Assert.True(runner.NoneLeftRunning);
        Assert.NotEqual(0, await start.Exited.WaitAsync(TimeSpan.FromSeconds(10))); // 끊긴 프로세스(신호로 끝남·-1)
    }

    [Fact]
    public async Task Login_address_that_comes_late_is_reported_through_progress()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Hang("Opening browser to sign in…"));
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var start = await Service(runner, loginUrlWait: TimeSpan.FromMilliseconds(50))
            .StartLoginAsync(new InlineProgress(url => reported.TrySetResult(url)), CancellationToken.None);
        Assert.Null(start.BrowserUrl);

        runner.Processes.Single().Emit(Link);

        Assert.Equal("https://claude.ai/oauth/authorize?abc", await reported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await start.Handle!.DisposeAsync();
    }

    [Theory]
    [InlineData("""{"loggedIn": true, "authMethod": "claude.ai", "email": "user@example.com"}""", true, "user@example.com", true)]
    [InlineData("""{"loggedIn": false, "authMethod": "none"}""", false, null, true)]
    [InlineData("Not logged in. Run claude auth login to authenticate.", false, null, false)]
    public async Task Login_status_maps_the_auth_status_json(string output, bool loggedIn, string? account, bool supported)
    {
        _files.Add(Installed);

        var status = await Service(new FakeSetupRunner(_ => FakeScript.Out(0, output))).GetLoginStatusAsync(CancellationToken.None);

        Assert.Equal(new ClaudeLoginStatus(loggedIn, account, supported), status);
    }

    [Fact]
    public async Task Login_status_without_claude_names_the_reason()
    {
        var status = await Service(new FakeSetupRunner(_ => FakeScript.Out(0))).GetLoginStatusAsync(CancellationToken.None);

        Assert.Equal(AiSetupErrorKind.CliNotFound, status.ErrorKind);
        Assert.False(status.LoggedIn);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"OK"}""", null)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}""", "NotLoggedIn")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"API Error: Connection error."}""", "Network")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"API Error: 429 rate"}""", "ServerRejected")]
    [InlineData("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"?"}""", "Unknown")]
    public async Task Test_maps_each_failure_kind(string resultLine, string? kind)
    {
        _files.Add(Installed);

        var result = await Service(new FakeSetupRunner(_ => FakeScript.Out(0, resultLine))).TestConnectionAsync(CancellationToken.None);

        Assert.Equal(kind is null, result.Success);
        Assert.Equal(kind is null ? null : Enum.Parse<AiSetupErrorKind>(kind), result.ErrorKind);
    }

    [Fact]
    public async Task Test_timeout_and_missing_claude_map_to_their_kinds()
    {
        Assert.Equal(AiSetupErrorKind.CliNotFound, (await Service(new FakeSetupRunner(_ => FakeScript.Out(0))).TestConnectionAsync(CancellationToken.None)).ErrorKind);

        _files.Add(Installed);
        var slow = Service(new FakeSetupRunner(_ => FakeScript.Hang()), testTimeout: TimeSpan.FromMilliseconds(50));
        Assert.Equal(AiSetupErrorKind.Timeout, (await slow.TestConnectionAsync(CancellationToken.None)).ErrorKind);
    }

    private ClaudeSetupService Service(FakeSetupRunner runner, TimeSpan? testTimeout = null, TimeSpan? loginUrlWait = null) =>
        new(
            new ClaudeSetupEngine(
                runner,
                new ClaudeSetupHost(
                    _ => Task.FromResult<string?>(null),
                    () => OperatingSystem.IsWindows() ? @"C:\Windows" : "/usr/bin:/bin",
                    path => _files.Contains(path),
                    Home,
                    Path.Combine(Path.GetTempPath(), "oranda-setup-svc-work")),
                testTimeout),
            loginUrlWait ?? TimeSpan.FromSeconds(5));

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
