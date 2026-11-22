using System.Net.Sockets;
using Oranda.Platform.Ai.Bridge;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Ai.Bridge;

/// <summary>비정상 종료가 남긴 소켓 청소(A4): 듣는 쪽 없는 agent.sock 은 지우고 기본 이름으로 다시 연다. Windows 는 소켓 파일이 없어 건너뛴다.</summary>
public sealed class StaleSocketsTests : IDisposable
{
    // macOS sun_path 104바이트: 임시 폴더 아래 짧은 이름으로 둔다.
    private readonly TempDirectory _dir = new("os-");

    public StaleSocketsTests() => Directory.CreateDirectory(Path.Combine(_dir, "run"));

    public void Dispose() => _dir.Dispose();

    private string Run => Path.Combine(_dir, "run");

    [Theory]
    [InlineData("agent.sock", true)]
    [InlineData("agent-123.sock", true)]
    [InlineData("agent-.sock", false)]
    [InlineData("agent-x.sock", false)]
    [InlineData("agent.sock.bak", false)]
    [InlineData("other.sock", false)]
    public void Only_bridge_socket_names_are_considered(string name, bool expected) =>
        Assert.Equal(expected, StaleSockets.IsAgentSocketName(name));

    [Fact]
    public void Dead_sockets_are_removed_and_live_or_foreign_files_stay()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, StaleSockets.Clean(Path.Combine(Run, "agent.sock"), _ => false)); // 명명 파이프는 파일이 없다
            return;
        }

        foreach (var name in new[] { "agent.sock", "agent-111.sock", "agent-222.sock", "notes.txt" })
        {
            File.WriteAllText(Path.Combine(Run, name), string.Empty);
        }

        var removed = StaleSockets.Clean(Path.Combine(Run, "agent.sock"), path => path.EndsWith("agent-222.sock", StringComparison.Ordinal));

        Assert.Equal(2, removed);
        Assert.Equal(["agent-222.sock", "notes.txt"], Directory.GetFiles(Run).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_left_over_socket_does_not_take_the_default_name_away()
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        var path = Path.Combine(Run, "agent.sock");

        // 비정상 종료 흉내: 묶기만 하고 닫는다(파일이 남는다).
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(path));
        }

        Assert.True(File.Exists(path));
        Assert.False(StaleSockets.IsListening(path));

        // 다리는 기본 이름(instance = null)으로 연다.
        await using var server = new AgentPipeServer((_, instance) => instance is null ? path : Path.Combine(Run, $"agent-{instance}.sock"), (_, _) => Task.CompletedTask, "test-user");
        Assert.Equal(path, server.Start());
        Assert.True(StaleSockets.IsListening(path)); // 살아 있는 창의 소켓은 지우지 않는다
        Assert.Equal(0, StaleSockets.Clean(path));
        Assert.True(File.Exists(path));
    }
}
