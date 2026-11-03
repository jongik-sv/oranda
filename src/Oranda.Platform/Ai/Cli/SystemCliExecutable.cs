using Oranda.Core.Ai.Contracts;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 시스템 CLI 실행 파일 찾기(이름 일반형). PATH 를 훑고, 없으면 흔한 설치 위치(<see cref="KnownDirectories"/>)를 본다.
/// 설정의 실행 파일 경로(<c>AiSettings.ExecutablePath</c>)는 Claude 전용이라 이 찾기는 받지 않는다:
/// Claude 경로 값이 다른 CLI 의 실행 파일로 쓰이면 안 된다. 못 찾으면 다른 CLI 로 넘어가지 않고 <c>cli_not_found</c> 로 닫는다.
/// </summary>
internal static class SystemCliExecutable
{
    /// <summary>
    /// 이름의 실행 파일을 찾는다. Windows 는 <c>이름.exe</c> 를 먼저 보고, 없으면 npm 의 <c>이름.cmd</c> 껍데기를 본다
    /// (<paramref name="nativeBesideShim"/> 이 껍데기 폴더에서 실제 exe 경로를 알려 주면 그 exe 를 쓴다). 그 밖의 OS 는 <c>이름</c> 자체다.
    /// </summary>
    /// <param name="name">확장자 없는 실행 파일 이름.</param>
    /// <param name="knownDirectories">PATH 에 없을 때 더 보는 위치(<see cref="KnownDirectories"/> 등).</param>
    /// <param name="nativeBesideShim">껍데기(.cmd) 폴더 → 그 옆 실제 exe 경로. 없으면 껍데기 자체를 돌려준다.</param>
    public static string? Find(
        string name,
        string? pathVariable,
        Func<string, bool> fileExists,
        IEnumerable<string> knownDirectories,
        Func<string, string>? nativeBesideShim = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(knownDirectories);

        var directories = (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(knownDirectories);
        foreach (var directory in directories)
        {
            if (OperatingSystem.IsWindows())
            {
                var exe = Path.Combine(directory, name + ".exe");
                if (fileExists(exe))
                {
                    return exe;
                }

                var npmShim = Path.Combine(directory, name + ".cmd");
                if (fileExists(npmShim))
                {
                    var native = nativeBesideShim?.Invoke(directory);
                    return native is not null && fileExists(native) ? native : npmShim;
                }
            }
            else
            {
                var candidate = Path.Combine(directory, name);
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 이름의 실행 파일을 찾아 쓸 수 있는지까지 판정한다. 못 찾으면 <c>cli_not_found</c>, 배치 파일이면 <c>cli_unsupported</c>.
    /// </summary>
    /// <param name="name">확장자 없는 실행 파일 이름.</param>
    /// <param name="displayName">오류 글에 쓸 CLI 이름.</param>
    /// <param name="home">사용자 홈 폴더(시험용). null 이면 지금 사용자의 홈.</param>
    public static (string? Executable, AiError? Error) Locate(string name, string displayName, string? pathVariable, Func<string, bool> fileExists, string? home = null)
    {
        var executable = Find(name, pathVariable, fileExists, KnownDirectories(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        if (executable is null)
        {
            return (null, new AiError($"{displayName} 실행 파일을 찾지 못했습니다. {displayName} 를 설치한 뒤 다시 시도하세요.", "cli_not_found"));
        }

        return ClaudeExecutable.IsBatchFile(executable)
            ? (null, new AiError($"{displayName} 의 배치 파일은 인자가 깨져 지원하지 않습니다.", "cli_unsupported"))
            : (executable, null);
    }

    /// <summary>
    /// PATH 에 없을 때 더 찾아보는 흔한 설치 위치(특정 CLI 전용 위치 제외). macOS 에서 Finder·Dock 으로 띄운 앱은 셸의 PATH 를 받지 않아
    /// (<c>/usr/bin:/bin:/usr/sbin:/sbin</c> 뿐) Homebrew·npm 이 설치한 CLI 를 PATH 로는 찾지 못한다.
    /// </summary>
    internal static IEnumerable<string> KnownDirectories(string home)
    {
        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        yield return Path.Combine(home, ".local", "bin");
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(home, "AppData", "Roaming", "npm"); // npm 전역
            yield break;
        }

        yield return "/opt/homebrew/bin"; // Apple Silicon Homebrew
        yield return "/usr/local/bin"; // Intel Homebrew·npm 전역
        yield return Path.Combine(home, ".npm-global", "bin");
    }
}
