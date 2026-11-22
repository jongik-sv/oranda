using System.Runtime.InteropServices;
using Oranda.Platform.Ai.Bridge;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Ai.Bridge;

/// <summary>비정상 종료가 남긴 소켓 청소(A4): 듣는 쪽 없는 agent.sock 은 지우고 기본 이름으로 다시 연다. Windows 는 소켓 파일이 없어 건너뛴다.</summary>
public sealed class StaleSocketsTests : IDisposable
{
    // macOS sun_path 104바이트: TMPDIR(/var/folders/…/T/) 아래 GUID 폴더면 run/agent-4242.sock 이 104바이트를 넘으므로 macOS 는 /tmp 아래에 둔다
    // (절대 경로 접두는 Path.Combine 이 임시 폴더 대신 그대로 쓴다).
    private readonly TempDirectory _dir = OperatingSystem.IsMacOS() ? new("/tmp/os-") : new("os-");

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

        // 비정상 종료 흉내: 묶기만 하고 닫는다(파일이 남는다). .NET Socket 은 닫을 때 묶은 파일을 지우므로(linux 실측) libc 로 직접 한다.
        LeftOverSocket.Create(path);

        Assert.True(File.Exists(path));
        Assert.False(StaleSockets.IsListening(path));

        // 다리는 기본 이름(instance = null)으로 연다.
        await using var server = new AgentPipeServer((_, instance) => instance is null ? path : Path.Combine(Run, $"agent-{instance}.sock"), (_, _) => Task.CompletedTask, "test-user");
        Assert.Equal(path, server.Start());
        Assert.True(StaleSockets.IsListening(path)); // 살아 있는 창의 소켓은 지우지 않는다
        Assert.Equal(0, StaleSockets.Clean(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Left_over_socket_is_removed_by_clean()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Run, "agent-4242.sock");
        LeftOverSocket.Create(path);

        Assert.Equal(1, StaleSockets.Clean(Path.Combine(Run, "agent.sock")));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Socket_that_refuses_us_for_permission_is_kept_because_we_cannot_tell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // connect 는 소켓 파일의 쓰기 권한이 필요하다. 000 이면 EACCES — 듣는지 알 수 없으니 지우지 않는다.
        var path = Path.Combine(Run, "agent.sock");
        LeftOverSocket.Create(path);
        File.SetUnixFileMode(path, UnixFileMode.None);

        Assert.True(StaleSockets.IsListening(path));
        Assert.Equal(0, StaleSockets.Clean(path));
        Assert.True(File.Exists(path));
    }

    /// <summary>libc 로 Unix 소켓을 묶고 닫아 파일만 남긴다(죽은 앱이 남긴 소켓).</summary>
    private static class LeftOverSocket
    {
        public static void Create(string path)
        {
            var fd = SocketFd(1 /* AF_UNIX */, 1 /* SOCK_STREAM */, 0);
            Assert.True(fd >= 0, "socket() failed: " + Marshal.GetLastPInvokeError());
            try
            {
                // sockaddr_un: Linux = sa_family(2바이트) + path[108], macOS = sun_len(1) + sun_family(1) + path[104].
                var bytes = System.Text.Encoding.UTF8.GetBytes(path);
                var address = new byte[110];
                if (OperatingSystem.IsMacOS())
                {
                    address[0] = (byte)address.Length;
                    address[1] = 1;
                }
                else
                {
                    address[0] = 1;
                }

                bytes.CopyTo(address, 2);
                Assert.True(Bind(fd, address, address.Length) == 0, "bind() failed: " + Marshal.GetLastPInvokeError());
            }
            finally
            {
                _ = Close(fd);
            }
        }

        [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
        private static extern int SocketFd(int domain, int type, int protocol);

        [DllImport("libc", EntryPoint = "bind", SetLastError = true)]
        private static extern int Bind(int fd, byte[] address, int length);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);
    }
}
