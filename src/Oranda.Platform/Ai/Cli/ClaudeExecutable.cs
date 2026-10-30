namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// claude 실행 파일 찾기. 설정에 경로가 있으면 그 경로(없는 파일이면 못 찾은 것으로 본다), 없으면 PATH 를 훑고
/// 거기에 없으면 흔한 설치 위치(<see cref="KnownDirectories"/>)를 본다.
/// Windows 의 npm 설치는 PATH 에 <c>claude.cmd</c> 만 있고 실제 실행 파일은 그 옆
/// <c>node_modules/@anthropic-ai/claude-code/bin/claude.exe</c> 이므로, 인자 따옴표가 깨지지 않게 그 exe 를 직접 쓴다.
/// </summary>
internal static class ClaudeExecutable
{
    /// <summary><c>.cmd</c>·<c>.bat</c> 는 cmd.exe 가 인자를 다시 해석해 줄바꿈에서 잘리고 <c>&amp;</c> 가 명령이 되므로 쓰지 않는다.</summary>
    public static bool IsBatchFile(string path)
        => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <param name="home">사용자 홈 폴더(시험용). null 이면 지금 사용자의 홈.</param>
    public static string? Find(string? configuredPath, string? pathVariable, Func<string, bool> fileExists, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return fileExists(configuredPath) ? configuredPath : null;
        }

        var directories = (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(KnownDirectories(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        foreach (var directory in directories)
        {
            if (OperatingSystem.IsWindows())
            {
                var exe = Path.Combine(directory, "claude.exe");
                if (fileExists(exe))
                {
                    return exe;
                }

                var npmShim = Path.Combine(directory, "claude.cmd");
                if (fileExists(npmShim))
                {
                    var native = Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                    return fileExists(native) ? native : npmShim;
                }
            }
            else
            {
                var candidate = Path.Combine(directory, "claude");
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// PATH 에 없을 때 더 찾아보는 흔한 설치 위치. macOS 에서 Finder·Dock 으로 띄운 앱은 셸의 PATH 를 받지 않아
    /// (<c>/usr/bin:/bin:/usr/sbin:/sbin</c> 뿐) Homebrew·npm·Claude Code 설치기가 넣은 claude 를 PATH 로는 찾지 못한다.
    /// </summary>
    internal static IEnumerable<string> KnownDirectories(string home)
    {
        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        yield return Path.Combine(home, ".local", "bin"); // Claude Code 설치기
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(home, "AppData", "Roaming", "npm"); // npm 전역
            yield break;
        }

        yield return Path.Combine(home, ".claude", "local"); // 예전 Claude Code 로컬 설치
        yield return "/opt/homebrew/bin"; // Apple Silicon Homebrew
        yield return "/usr/local/bin"; // Intel Homebrew·npm 전역
        yield return Path.Combine(home, ".npm-global", "bin");
    }
}
