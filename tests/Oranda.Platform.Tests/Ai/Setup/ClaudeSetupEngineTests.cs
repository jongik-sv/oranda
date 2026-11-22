using Oranda.Platform.Ai.Setup;

namespace Oranda.Platform.Tests.Ai.Setup;

/// <summary>claude 준비 엔진: 가짜 실행기만 쓴다(실제 claude·인터넷을 부르지 않는다).</summary>
public sealed class ClaudeSetupEngineTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "oranda-setup-home");
    private static readonly string Installed = Path.Combine(Home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");

    private readonly HashSet<string> _files = [];

    // ---- 찾기·판

    [Fact]
    public async Task Find_returns_null_when_claude_is_nowhere()
    {
        var runner = new FakeSetupRunner(_ => throw new InvalidOperationException("띄우면 안 된다"));

        Assert.Null(await Engine(runner).FindAsync(CancellationToken.None));
        Assert.Empty(runner.Launches);
    }

    [Fact]
    public async Task Find_looks_in_the_installer_folder_even_when_path_is_old_and_reads_the_version()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Out(0, "2.1.330 (Claude Code)"));

        var found = await Engine(runner).FindAsync(CancellationToken.None);

        Assert.Equal(new ClaudeFound(Installed, "2.1.330"), found);
        Assert.Equal(["--version"], runner.Launches.Single().Arguments);
    }

    [Fact]
    public async Task Find_uses_the_configured_path_and_keeps_it_found_when_the_version_cannot_be_read()
    {
        var configured = Path.Combine(Home, "own", "claude");
        _files.Add(configured);
        var runner = new FakeSetupRunner(_ => FakeScript.Out(1, "boom"));

        Assert.Equal(new ClaudeFound(configured, null), await Engine(runner, configured: configured).FindAsync(CancellationToken.None));
    }

    // ---- 설치

    [Fact]
    public async Task Install_streams_plain_lines_then_finds_claude_again()
    {
        var runner = new FakeSetupRunner(launch =>
        {
            if (launch.Arguments.Contains("--version"))
            {
                return FakeScript.Out(0, "2.1.330 (Claude Code)");
            }

            _files.Add(Installed); // 설치 스크립트가 끝나면 파일이 생긴다
            return FakeScript.Out(0, "Setting up Claude Code...", "\u001b[32m✅ Installation complete!\u001b[0m");
        });
        var progress = new List<string>();

        var outcome = await Engine(runner).InstallAsync(new SyncProgress(progress), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(new ClaudeFound(Installed, "2.1.330"), outcome.Found);
        Assert.Equal(["Setting up Claude Code...", "✅ Installation complete!"], progress);
        var install = runner.Launches[0];
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("powershell", install.FileName);
            // 숨은 창 PowerShell 5.1 은 949 코드 페이지라 출력을 UTF-8 로 바꾼 뒤 돌린다.
            Assert.Equal("[Console]::OutputEncoding=[Text.Encoding]::UTF8; irm https://claude.ai/install.ps1 | iex", install.Arguments[^1]);
        }
        else
        {
            Assert.Equal("/bin/bash", install.FileName);
            // pipefail: curl 이 실패하면 파이프 전체가 실패로 끝나게 한다.
            Assert.Equal(["-c", "set -o pipefail; curl -fsSL https://claude.ai/install.sh | bash"], install.Arguments);
        }
    }

    [Fact]
    public async Task Failed_install_gives_the_last_error_line_as_reason_and_a_manual_command()
    {
        var runner = new FakeSetupRunner(_ => new FakeScript(
            [new SetupLine("Setting up", false), new SetupLine("Download failed", true), new SetupLine("token=abcd1234secret", false)],
            ExitCode: 1));
        var engine = Engine(runner);

        var outcome = await engine.InstallAsync(null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Found);
        Assert.Equal("Download failed", outcome.Reason);
        Assert.Equal(
            OperatingSystem.IsWindows() ? "irm https://claude.ai/install.ps1 | iex" : "curl -fsSL https://claude.ai/install.sh | bash",
            ClaudeSetupEngine.ManualInstallCommand);
    }

    [Theory]
    [InlineData("Failed to download binary: The remote server returned an error: (503)", "Network")]
    [InlineData("Failed to get latest version: Unable to connect to the remote server", "Network")]
    [InlineData("Claude Code does not support 32-bit Windows.", "Unknown")]
    public async Task Install_script_errors_are_sorted_by_kind(string errorLine, string kind)
    {
        // install.ps1 은 Write-Error 뒤 exit 1(W2 실측), install.sh 는 표준 오류 글 뒤 exit 1.
        var runner = new FakeSetupRunner(_ => new FakeScript([new SetupLine("Setting up Claude Code...", false), new SetupLine(errorLine, true)], ExitCode: 1));

        var outcome = await Engine(runner).InstallAsync(null, CancellationToken.None);

        Assert.Equal(Enum.Parse<Oranda.Core.Ai.Setup.AiSetupErrorKind>(kind), outcome.ErrorKind);
    }

    [Fact]
    public async Task Install_log_lines_hide_secrets_and_address_queries()
    {
        var runner = new FakeSetupRunner(_ => FakeScript.Out(1, "token=abcd1234secret", "see https://a.example/x?state=zzz9"));
        var progress = new List<string>();

        var outcome = await Engine(runner).InstallAsync(new SyncProgress(progress), CancellationToken.None);

        Assert.DoesNotContain(progress, l => l.Contains("abcd1234secret", StringComparison.Ordinal) || l.Contains("zzz9", StringComparison.Ordinal));
        Assert.DoesNotContain("zzz9", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_that_exits_zero_after_a_download_error_is_a_network_failure()
    {
        // pipefail 이 없는 셸: curl 이 실패해도 안쪽 bash 가 빈 입력으로 0 을 낸다.
        var runner = new FakeSetupRunner(_ => new FakeScript([new SetupLine("curl: (6) Could not resolve host: claude.ai", true)], ExitCode: 0));

        var outcome = await Engine(runner).InstallAsync(null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(Oranda.Core.Ai.Setup.AiSetupErrorKind.Network, outcome.ErrorKind);
    }

    [Fact]
    public async Task Install_that_ends_well_but_leaves_no_claude_is_a_failure()
    {
        var runner = new FakeSetupRunner(_ => FakeScript.Out(0, "done"));

        var outcome = await Engine(runner).InstallAsync(null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Reason);
    }

    [Fact]
    public async Task Install_that_cannot_start_is_a_failure_with_a_reason()
    {
        var runner = new FakeSetupRunner(_ => FakeScript.Missing());

        var outcome = await Engine(runner).InstallAsync(null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
    }

    [Fact]
    public async Task Cancelling_install_kills_the_process_tree()
    {
        var runner = new FakeSetupRunner(_ => FakeScript.Hang("Setting up"));
        using var cts = new CancellationTokenSource();
        var progress = new List<string>();

        var run = Engine(runner).InstallAsync(new SyncProgress(progress, _ => cts.Cancel()), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(runner.Processes.Single().Killed);
        Assert.True(runner.NoneLeftRunning);
    }

    // ---- 로그인·상태

    [Fact]
    public async Task Login_starts_auth_login_in_the_background_and_hands_out_the_address()
    {
        _files.Add(Installed);
        var link = "\u001b]8;;https://claude.ai/oauth/authorize?abc\u0007https://claude.ai/oauth/authorize?abc\u001b]8;;\u0007";
        var runner = new FakeSetupRunner(_ => FakeScript.Hang("Opening browser to sign in…", "If the browser didn't open, visit:", link));

        await using (var login = await Engine(runner).StartLoginAsync(CancellationToken.None))
        {
            Assert.NotNull(login);
            Assert.Equal("https://claude.ai/oauth/authorize?abc", await login.Url.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(["auth", "login"], runner.Launches.Single().Arguments);
            Assert.False(runner.Processes.Single().Killed);
        }

        // 끊기(정리) 뒤 프로세스가 남지 않는다.
        Assert.True(runner.Processes.Single().Killed);
        Assert.True(runner.NoneLeftRunning);
    }

    [Fact]
    public async Task Login_address_is_null_when_claude_ends_without_one()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Out(1, "Error: something"));

        await using var login = await Engine(runner).StartLoginAsync(CancellationToken.None);

        Assert.Null(await login!.Url.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await login.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Login_without_claude_returns_null()
    {
        Assert.Null(await Engine(new FakeSetupRunner(_ => FakeScript.Out(0))).StartLoginAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1, """{"loggedIn": false, "authMethod": "none", "apiProvider": "firstParty"}""", "LoggedOut", null)]
    [InlineData(0, """{"loggedIn": true, "authMethod": "claude.ai", "email": "user@example.com", "newField": 1}""", "LoggedIn", "user@example.com")]
    [InlineData(1, "Not logged in. Run claude auth login to authenticate.", "Unsupported", null)]
    [InlineData(2, "error: unknown command 'auth'", "Unsupported", null)]
    public async Task Status_reads_auth_status_json(int exitCode, string output, string kind, string? email)
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Out(exitCode, output.Split('\n')));

        var status = await Engine(runner).StatusAsync(CancellationToken.None);

        Assert.Equal(new ClaudeLoginCheck(Enum.Parse<ClaudeLoginCheckKind>(kind), email), status);
        Assert.Equal(["auth", "status", "--json"], runner.Launches.Single().Arguments);
    }

    [Fact]
    public async Task Status_that_cannot_start_is_a_one_off_failure_not_unsupported()
    {
        // 한 번 못 띄운 것(바이러스 검사로 늦은 첫 실행 등)을 「상태 확인을 지원 안 함」 으로 굳히지 않는다.
        _files.Add(Installed);
        var status = await Engine(new FakeSetupRunner(_ => FakeScript.Missing())).StatusAsync(CancellationToken.None);

        Assert.Equal(ClaudeLoginCheckKind.Failed, status.Kind);
    }

    [Fact]
    public async Task Status_without_claude_is_not_found()
    {
        Assert.Equal(ClaudeLoginCheckKind.NotFound, (await Engine(new FakeSetupRunner(_ => FakeScript.Out(0))).StatusAsync(CancellationToken.None)).Kind);
    }

    // ---- 연결 시험

    [Fact]
    public async Task Test_succeeds_on_a_normal_result_and_runs_without_tools_or_mcp()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Out(
            0,
            """{"type":"system","subtype":"init","session_id":"s1","mcp_servers":[],"tools":[]}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"OK"}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"session_id":"s1","result":"OK"}"""));

        var outcome = await Engine(runner).TestAsync(CancellationToken.None);

        Assert.Equal(ClaudeTestFailure.None, outcome.Failure);
        var arguments = runner.Launches.Single().Arguments;
        Assert.Equal("-p", arguments[0]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Equal(string.Empty, arguments[arguments.ToList().IndexOf("--tools") + 1]);
        Assert.DoesNotContain("--mcp-config", arguments);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}""", "NotLoggedIn")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"API Error: Connection error. (getaddrinfo ENOTFOUND api.anthropic.com)"}""", "Network")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"API Error: 403 {\"error\":{\"message\":\"Request not allowed\"}}"}""", "ServerRefused")]
    [InlineData("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"무슨 일"}""", "Unknown")]
    public async Task Test_failures_are_sorted_by_kind(string resultLine, string expected)
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Out(1, resultLine));

        Assert.Equal(Enum.Parse<ClaudeTestFailure>(expected), (await Engine(runner).TestAsync(CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Test_keeps_the_result_when_the_output_pipe_stays_open_after_it()
    {
        // 손자가 출력 관을 쥐어 흐름이 닫히지 않아도, 결과 줄을 받았으면 시간 초과가 아니다.
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Hang("""{"type":"result","subtype":"success","is_error":false,"result":"OK"}"""));

        var outcome = await Engine(runner, testTimeout: TimeSpan.FromSeconds(30)).TestAsync(CancellationToken.None);

        Assert.Equal(ClaudeTestFailure.None, outcome.Failure);
        Assert.True(runner.Processes.Single().Killed);
    }

    [Fact]
    public void Server_status_wins_over_the_word_network()
    {
        var result = new Oranda.Platform.Ai.Cli.ClaudeResult(true, null, "API Error: 500 upstream network glitch", null, null, 0, 0, []);

        Assert.Equal(ClaudeTestFailure.ServerRefused, ClaudeSetupEngine.Classify(result));
    }

    [Fact]
    public async Task Test_without_claude_is_not_found()
    {
        Assert.Equal(ClaudeTestFailure.NotFound, (await Engine(new FakeSetupRunner(_ => FakeScript.Out(0))).TestAsync(CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Test_that_ends_without_a_result_is_unknown_with_the_error_tail()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => new FakeScript([new SetupLine("segfault", true)], ExitCode: 139));

        var outcome = await Engine(runner).TestAsync(CancellationToken.None);

        Assert.Equal(ClaudeTestFailure.Unknown, outcome.Failure);
        Assert.Contains("segfault", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_that_takes_too_long_times_out_and_kills_claude()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Hang());

        var outcome = await Engine(runner, testTimeout: TimeSpan.FromMilliseconds(100)).TestAsync(CancellationToken.None);

        Assert.Equal(ClaudeTestFailure.Timeout, outcome.Failure);
        Assert.True(runner.Processes.Single().Killed);
    }

    [Fact]
    public async Task Cancelling_the_test_throws_and_kills_claude()
    {
        _files.Add(Installed);
        var runner = new FakeSetupRunner(_ => FakeScript.Hang());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(runner).TestAsync(cts.Token));
        Assert.True(runner.Processes.Single().Killed);
    }

    private ClaudeSetupEngine Engine(FakeSetupRunner runner, string? configured = null, TimeSpan? testTimeout = null) =>
        new(runner, new ClaudeSetupHost(
            _ => Task.FromResult(configured),
            () => OperatingSystem.IsWindows() ? @"C:\Windows" : "/usr/bin:/bin",
            path => _files.Contains(path),
            Home,
            Path.Combine(Path.GetTempPath(), "oranda-setup-work")),
            testTimeout);

    /// <summary>바로 부르는 진행 알림(Progress&lt;T&gt; 는 다른 스레드로 미뤄 순서가 흔들린다).</summary>
    private sealed class SyncProgress(List<string> sink, Action<string>? onReport = null) : IProgress<string>
    {
        public void Report(string value)
        {
            sink.Add(value);
            onReport?.Invoke(value);
        }
    }
}
