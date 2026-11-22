using System.IO.Pipes;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using static Oranda.Core.Ai.Protocol.AgentRpc;

namespace Oranda.Agent.Cli.Tests;

/// <summary>
/// B1·설계 §10-3·4 실측: macOS·Linux 에서 <see cref="PipeBridgeClient"/> 가 절대 경로 소켓(앱의 <c>AgentPipeServer</c> 와 같은 열기 방식)에 붙고,
/// hello 토큰을 내고, 한도를 넘는 소켓 경로는 알아보기 쉬운 오류가 되는가. Windows 에서는 할 일이 없어 그냥 통과한다.
/// </summary>
public sealed class UnixSocketBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine("/tmp", "oc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Client_connects_to_an_absolute_socket_path_sends_the_token_and_calls_a_tool()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "agent-123.sock");
        await using var server = new NamedPipeServerStream(path, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string? token = null;
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var channel = new AgentRpcChannel(server);
            var hello = TryParse((await channel.ReadLineAsync(CancellationToken.None))!)!;
            token = (string?)hello.Params!["run_token"] ?? (string?)hello.Params!["runToken"];
            await channel.WriteAsync(Success(hello.Id, new JsonObject { ["run_id"] = "r", ["server_version"] = "1" }), CancellationToken.None);
            var call = TryParse((await channel.ReadLineAsync(CancellationToken.None))!)!;
            var result = ToNode(new AgentCallToolResult(false, """{"ok":"한글"}"""), AgentRpcJsonContext.Default.AgentCallToolResult);
            await channel.WriteAsync(Success(call.Id, result), CancellationToken.None);
        });

        Assert.True(File.Exists(path));
        await using var client = new PipeBridgeClient(path, "secret-token");
        var answer = await client.CallToolAsync(new AgentClientInfo("test", "1"), "get_status", new JsonObject(), CancellationToken.None);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(answer.IsError);
        Assert.Equal("""{"ok":"한글"}""", answer.Text);
        Assert.Equal("secret-token", token);
    }

    [Fact]
    public async Task Missing_socket_is_oranda_not_running()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var client = new PipeBridgeClient(Path.Combine(_dir, "agent.sock"), "t");

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.ListToolsAsync(new AgentClientInfo("t", "1"), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotRunning, ex.Code);
    }

    [Fact]
    public async Task Socket_path_over_the_system_limit_is_a_clear_bridge_error()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = "/tmp/" + new string('a', 120) + "/agent.sock";
        await using var client = new PipeBridgeClient(path, "t");

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.ListToolsAsync(new AgentClientInfo("t", "1"), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.BridgeError, ex.Code);
        Assert.Equal(CliText.SocketPathTooLong, ex.Message);
    }

    [Theory]
    [InlineData(107, AgentErrorCodes.NotRunning)]
    [InlineData(108, AgentErrorCodes.BridgeError)]
    public async Task Linux_socket_path_limit_is_107_bytes(int length, string code)
    {
        // 실측(2026-11-22, .NET 10, Linux): sun_path 108바이트에 끝 NUL 이 들어가므로 경로는 107바이트까지. 108부터 ArgumentOutOfRangeException.
        // 소켓 파일 모드는 CurrentUserOnly 여도 umask 를 따른다(0775 를 봄) — 다른 사용자를 막는 것은 폴더 0700(PrivateDirectory)이다.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = "/tmp/" + new string('b', length - "/tmp/".Length - "/agent.sock".Length) + "/agent.sock";
        Assert.Equal(length, path.Length);
        await using var client = new PipeBridgeClient(path, "t");

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.ListToolsAsync(new AgentClientInfo("t", "1"), CancellationToken.None));

        Assert.Equal(code, ex.Code);
    }
}
