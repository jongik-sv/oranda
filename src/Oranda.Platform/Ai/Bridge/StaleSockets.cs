using System.Globalization;
using System.IO.Pipes;

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

    /// <summary>붙어 볼 때 기다리는 시간. 살아 있는 다리는 바로 받으므로 짧게 둔다.</summary>
    private const int ProbeMilliseconds = 200;

    /// <summary>
    /// 소켓에 붙어 본다(다리와 같은 <see cref="NamedPipeClientStream"/>: 절대 경로면 그 소켓). 붙으면 참(바로 끊는다), 시한 안에 못 붙으면
    /// 듣는 쪽이 없는 것으로 보고 거짓. 권한 거부처럼 판단할 수 없는 실패는 지우지 않도록 참으로 본다.
    /// 소켓 API 를 직접 쓰지 않는 까닭: 제품 코드는 통신 라이브러리를 참조하지 않는다(SafetyCrossTests).
    /// </summary>
    public static bool IsListening(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", path, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(ProbeMilliseconds);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false; // 거절(듣는 쪽 없음). 형식은 System.Net.Primitives 에 있다.
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
