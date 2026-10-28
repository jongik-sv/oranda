namespace Oranda.Platform.Ai.Bridge;

/// <summary>
/// 비밀이 놓일 폴더를 만들고, macOS·Linux 에서는 소유자 전용(0700)으로 맞춘다. .NET 의 CurrentUserOnly 는 소켓 파일을 0600 으로
/// 만들어 주지 않으므로 폴더 권한으로 다른 사용자의 접근을 막는다.
/// </summary>
internal static class PrivateDirectory
{
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // 사용자 프로필 아래 폴더는 기본 ACL 로 소유자 전용이다. 폴더만 만든다.
            Directory.CreateDirectory(path);
            return;
        }

        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, OwnerOnly);

        // 이미 있던 폴더는 만들 때의 모드가 적용되지 않으므로 다시 맞춘다.
        if (File.GetUnixFileMode(path) != OwnerOnly)
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
    }
}
