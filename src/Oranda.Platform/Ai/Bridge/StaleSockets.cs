using System.Globalization;
using System.Net.Sockets;

namespace Oranda.Platform.Ai.Bridge;

/// <summary>
/// 비정상 종료가 남긴 Unix 소켓 파일 청소(A4). 남은 <c>agent.sock</c> 이 있으면 다음 앱이 기본 이름을 잃고 늘 <c>agent-&lt;pid&gt;.sock</c> 으로 열게 되므로,
/// 다리를 열기 전에 같은 폴더의 <c>agent.sock</c>·<c>agent-&lt;pid&gt;.sock</c> 에 붙어 본다: 듣는 쪽이 없으면 지우고, 붙으면 살아 있는 창이라 그대로 둔다.
/// Windows 명명 파이프(절대 경로가 아닌 이름)는 하지 않는다.
/// </summary>
internal static class StaleSockets
{
    /// <summary><paramref name="socketPath"/> 의 폴더에서 죽은 소켓을 지운다. 지운 수.</summary>
    public static int Clean(string socketPath) => Clean(socketPath, IsListening);

    /// <summary>시험용: 듣는지 판단을 넣는다.</summary>
    internal static int Clean(string socketPath, Func<string, bool> isListening)
    {
        ArgumentNullException.ThrowIfNull(isListening);
        if (OperatingSystem.IsWindows() || !Path.IsPathRooted(socketPath) || Path.GetDirectoryName(socketPath) is not { Length: > 0 } directory
            || !Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "agent*.sock"))
        {
            if (!IsAgentSocketName(Path.GetFileName(path)) || isListening(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 지우지 못하면 지금처럼 -pid 이름으로 연다.
            }
        }

        return removed;
    }

    /// <summary><c>agent.sock</c> 또는 <c>agent-&lt;숫자&gt;.sock</c>.</summary>
    public static bool IsAgentSocketName(string fileName)
    {
        if (fileName == "agent.sock")
        {
            return true;
        }

        const string Prefix = "agent-";
        const string Suffix = ".sock";
        return fileName.StartsWith(Prefix, StringComparison.Ordinal)
            && fileName.EndsWith(Suffix, StringComparison.Ordinal)
            && fileName.Length > Prefix.Length + Suffix.Length
            && int.TryParse(fileName.AsSpan(Prefix.Length, fileName.Length - Prefix.Length - Suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>소켓에 붙어 본다. 붙으면 참(바로 끊는다), 거절(듣는 쪽 없음)이면 거짓. 그 밖의 실패는 지우지 않도록 참으로 본다.</summary>
    public static bool IsListening(string path)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        {
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
