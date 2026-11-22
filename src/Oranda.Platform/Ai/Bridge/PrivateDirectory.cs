namespace Oranda.Platform.Ai.Bridge;

/// <summary>
/// 비밀이 놓일 폴더를 만들고, macOS·Linux 에서는 소유자 전용(0700)으로 맞춘다. .NET 의 CurrentUserOnly 는 소켓 파일을 0600 으로
/// 만들어 주지 않으므로 폴더 권한으로 다른 사용자의 접근을 막는다.
/// </summary>
internal static class PrivateDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>폴더를 만들고 0700 으로 맞춘다. 남의 폴더라 맞출 수 없으면 <see cref="UnauthorizedAccessException"/>.</summary>
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // 사용자 프로필 아래 폴더는 기본 ACL 로 소유자 전용이다. 폴더만 만든다.
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnly);

        // 이미 있던 폴더는 만들 때의 모드가 적용되지 않으므로 다시 맞춘다. 권한 바꾸기는 소유자만 할 수 있으므로
        // 다른 사용자가 먼저 만든 폴더(예: /tmp/oranda-<uid>)는 여기서 거부된다.
        if (File.GetUnixFileMode(path) != OwnerOnly)
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
    }

    /// <summary>
    /// <see cref="Ensure"/> 를 해 보고 쓸 수 있으면 참. Unix 에서는 이미 0700 이어도 권한을 한 번 다시 써서 내 폴더인지 확인한다
    /// (남의 0700 폴더는 모드가 맞아 보여도 권한 바꾸기가 거부된다). 실패해도 던지지 않는다.
    /// </summary>
    public static bool TryEnsure(string path)
    {
        try
        {
            Ensure(path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, OwnerOnly);
                return File.GetUnixFileMode(path) == OwnerOnly;
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
