using System.Text.Json;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// opencode 가 읽을 사용자 설정 파일에서 MCP 서버 이름을 모은다. 사용자 MCP 서버는 잠금(도구 거부)과 상관없이 자식 프로세스로 떠서
/// 부모 환경(실행 토큰 포함)을 상속하므로, 드라이버가 이름별로 <c>enabled:false</c> 로 덮어 쓴다(<see cref="OpencodeArguments.BuildConfigContent"/>).
/// <para>
/// <c>opencode debug config</c> 는 쓰지 않는다: 실행 중인 공유 백그라운드 서비스의 설정을 돌려주어 이 실행에 적용될 설정이 아니다(측정으로 확인).
/// 읽는 위치: <c>$XDG_CONFIG_HOME/opencode</c>(없으면 <c>~/.config/opencode</c>)와 macOS 관리 폴더의 <c>opencode.json[c]</c>,
/// 그리고 실행 폴더의 상위 폴더들의 <c>opencode.json[c]</c>·<c>.opencode/opencode.json[c]</c>(프로젝트 설정). 부모의 <c>OPENCODE_CONFIG</c> 는 실행 환경에서 지우므로 읽지 않는다.
/// </para>
/// 읽을 수 없거나(JSONC 파싱 실패 포함) 모양을 모르면 이름을 추측하지 않고 문제로 돌려준다 — 호출 측은 실행하지 않고 닫는다(fail-closed).
/// </summary>
internal static class OpencodeGlobalConfig
{
    private static readonly string[] FileNames = ["opencode.jsonc", "opencode.json"];

    private static readonly JsonDocumentOptions JsoncOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// 사용자 설정에 있는 MCP 서버 이름과 문제. 문제가 있으면(Problem 이 null 이 아님) 이름은 쓰지 않는다.
    /// </summary>
    /// <param name="environment">환경 변수 읽기(<c>XDG_CONFIG_HOME</c>).</param>
    /// <param name="home">사용자 홈 폴더.</param>
    /// <param name="runDirectory">이 실행의 폴더(상위 폴더의 프로젝트 설정을 찾는 시작점).</param>
    /// <param name="readText">파일 읽기. 파일이 없으면 null. 읽다가 실패하면 예외(IOException·UnauthorizedAccessException).</param>
    public static (IReadOnlyList<string> Names, string? Problem) ReadMcpServerNames(
        Func<string, string?> environment,
        string home,
        string runDirectory,
        Func<string, string?> readText)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readText);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in CandidateFiles(environment, home, runDirectory))
        {
            string? text;
            try
            {
                text = readText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ([], $"설정 파일을 읽지 못했습니다: {file}");
            }

            if (text is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text, JsoncOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return ([], $"설정 파일의 모양을 알 수 없습니다: {file}");
                }

                if (!document.RootElement.TryGetProperty("mcp", out var mcp))
                {
                    continue;
                }

                if (mcp.ValueKind != JsonValueKind.Object)
                {
                    return ([], $"설정 파일의 mcp 항목 모양을 알 수 없습니다: {file}");
                }

                foreach (var server in mcp.EnumerateObject())
                {
                    // 확인된 local 형식만 덮어 쓸 수 있다. remote 등은 덮어 쓰는 모양을 확인하지 못했으므로 추측하지 않고 닫는다.
                    if (server.Value.ValueKind != JsonValueKind.Object
                        || !server.Value.TryGetProperty("type", out var type)
                        || type.ValueKind != JsonValueKind.String
                        || !string.Equals(type.GetString(), "local", StringComparison.Ordinal))
                    {
                        return ([], $"MCP 서버 「{server.Name}」 의 형식을 알 수 없어(local 만 지원) 끌 수 없습니다: {file}");
                    }

                    names.Add(server.Name);
                }
            }
            catch (JsonException)
            {
                return ([], $"설정 파일을 읽지 못했습니다(JSON 형식 오류): {file}");
            }
        }

        return ([.. names], null);
    }

    private static IEnumerable<string> CandidateFiles(Func<string, string?> environment, string home, string runDirectory)
    {
        var configHome = environment("XDG_CONFIG_HOME");
        var globalDirectory = Path.Combine(string.IsNullOrWhiteSpace(configHome) ? Path.Combine(home, ".config") : configHome, "opencode");
        foreach (var name in FileNames)
        {
            yield return Path.Combine(globalDirectory, name);
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (var name in FileNames)
            {
                yield return Path.Combine("/Library/Application Support/opencode", name);
            }
        }

        // 프로젝트 설정: 실행 폴더에서 위로 올라가며 본다(opencode 는 작업 폴더의 상위 설정을 찾는다).
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(runDirectory)); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            foreach (var name in FileNames)
            {
                yield return Path.Combine(directory, name);
                yield return Path.Combine(directory, ".opencode", name);
            }
        }
    }
}
