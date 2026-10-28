namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// claude 실행 파일 찾기. 설정에 경로가 있으면 그 경로(없는 파일이면 못 찾은 것으로 본다), 없으면 PATH 를 훑는다.
/// Windows 의 npm 설치는 PATH 에 <c>claude.cmd</c> 만 있고 실제 실행 파일은 그 옆
/// <c>node_modules/@anthropic-ai/claude-code/bin/claude.exe</c> 이므로, 인자 따옴표가 깨지지 않게 그 exe 를 직접 쓴다.
/// </summary>
internal static class ClaudeExecutable
{
    /// <summary><c>.cmd</c>·<c>.bat</c> 는 cmd.exe 가 인자를 다시 해석해 줄바꿈에서 잘리고 <c>&amp;</c> 가 명령이 되므로 쓰지 않는다.</summary>
    public static bool IsBatchFile(string path)
        => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    public static string? Find(string? configuredPath, string? pathVariable, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return fileExists(configuredPath) ? configuredPath : null;
        }

        foreach (var directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
}
