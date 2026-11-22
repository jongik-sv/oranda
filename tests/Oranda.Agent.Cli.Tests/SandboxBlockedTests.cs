using System.Diagnostics;
using System.IO.Pipes;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli.Tests;

/// <summary>
/// CLI 샌드박스(맥 실측: Codex read-only·workspace-write 는 소켓 connect EPERM, agy 는 agents 폴더 읽기·ps EPERM)에서
/// 「Oranda 가 꺼짐·연결 파일 없음」 대신 <see cref="AgentErrorCodes.SandboxBlocked"/> 로 알리는가.
/// linux 에서는 폴더 권한 000 으로 같은 거부(EACCES)를 만든다. Windows 에서는 할 일이 없어 그냥 통과한다.
/// </summary>
public sealed class SandboxBlockedTests : IDisposable
{
    private readonly TempDataDir _dir = new();
    private readonly List<string> _locked = [];

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _dir.Dispose();
            return;
        }

        foreach (var path in _locked)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _dir.Dispose();
    }

    [Fact]
    public void Unreadable_agents_folder_is_sandbox_blocked_not_no_endpoint()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _dir.Write(100);
        Lock(AppDataLocation.AgentsDirectory(_dir.Path));
        var locator = new EndpointLocator(_dir.Path, _ => true, () => TempDataDir.Now);

        var ex = Assert.Throws<BridgeException>(() => locator.Choose(null));

        Assert.Equal(AgentErrorCodes.SandboxBlocked, ex.Code);
        Assert.Equal(AgentCliExit.Unreachable, AgentCliExit.For(ex.Code));
        Assert.Contains("Operation not permitted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_endpoint_file_is_sandbox_blocked_but_a_readable_one_still_wins()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _dir.Write(100);
        var locked = AppDataLocation.EndpointFile(_dir.Path, 100);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        _locked.Add(locked);
        var locator = new EndpointLocator(_dir.Path, _ => true, () => TempDataDir.Now);

        Assert.Equal(AgentErrorCodes.SandboxBlocked, Assert.Throws<BridgeException>(() => locator.Choose(null)).Code);

        _dir.Write(200);
        Assert.Equal(200, locator.Choose(null).Pid);
    }

    [Fact]
    public void Missing_data_folder_is_still_no_endpoint()
    {
        var locator = new EndpointLocator(Path.Combine(_dir.Path, "none"), _ => true, () => TempDataDir.Now);

        Assert.Equal(AgentErrorCodes.NoEndpoint, Assert.Throws<BridgeException>(() => locator.Choose(null)).Code);
    }

    [Fact]
    public void Process_check_counts_a_pid_it_may_not_signal_as_alive_and_an_exited_one_as_dead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(EndpointLocator.IsProcessAlive(Environment.ProcessId));
        Assert.True(EndpointLocator.IsProcessAlive(1)); // init 은 root 것이라 kill(1, 0) = EPERM — 그래도 살아 있다(샌드박스의 ps 거부와 같은 모양)

        using var exited = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        exited.WaitForExit();
        var pid = exited.Id;
        exited.Dispose();
        Assert.False(EndpointLocator.IsProcessAlive(pid));
    }

    [Fact]
    public async Task Socket_connect_permission_denied_is_sandbox_blocked()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Combine(_dir.Path, "run");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "agent.sock");
        await using var server = new NamedPipeServerStream(path, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Lock(folder);
        await using var client = new PipeBridgeClient(path, "t");

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.ListToolsAsync(new AgentClientInfo("t", "1"), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.SandboxBlocked, ex.Code);
    }

    [Fact]
    public async Task Cli_prints_operation_not_permitted_on_stderr_and_exits_three()
    {
        var cli = new CliHarness(_dir.Path);
        _dir.Write(100);
        cli.AlivePids.Add(100);
        cli.Bridge.Throw = new BridgeException(AgentErrorCodes.SandboxBlocked, CliText.SandboxBlocked);

        Assert.Equal(AgentCliExit.Unreachable, await cli.RunAsync("tabs"));

        Assert.Equal(AgentErrorCodes.SandboxBlocked, (string?)cli.OutJson()["code"]);
        Assert.Contains("Operation not permitted", cli.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("network_access", cli.OutJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inside_codex_without_network_the_error_names_the_codex_setting()
    {
        var cli = new CliHarness(_dir.Path) { ["CODEX_SANDBOX_NETWORK_DISABLED"] = "1" };
        _dir.Write(100);
        cli.AlivePids.Add(100);
        cli.Bridge.Throw = new BridgeException(AgentErrorCodes.SandboxBlocked, CliText.SandboxBlocked);

        Assert.Equal(AgentCliExit.Unreachable, await cli.RunAsync("tabs"));

        Assert.Contains("sandbox_workspace_write.network_access=true", (string?)cli.OutJson()["error"], StringComparison.Ordinal);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private void Lock(string folder)
    {
        File.SetUnixFileMode(folder, UnixFileMode.None);
        _locked.Add(folder);
    }
}
