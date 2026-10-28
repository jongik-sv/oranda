using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Protocol;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 한 실행 전용 mcp-config 를 쓴다. 실행 토큰이 <c>env</c> 에 들어 있으므로 소유자 전용 실행 폴더에 두고(Unix 는 폴더 0700, 파일 0600을
/// 만들 때부터 적용) 실행이 끝나면 폴더째 지운다. 토큰은 명령 인자에 넣지 않는다(프로세스 목록에 보인다).
/// </summary>
internal static class McpConfigWriter
{
    public static string Write(string runDirectory, string orandactlPath, string pipeName, string runToken)
    {
        if (Path.GetDirectoryName(runDirectory) is { Length: > 0 } parent)
        {
            PrivateDirectory.Ensure(parent);
        }

        PrivateDirectory.Ensure(runDirectory);
        var path = Path.Combine(runDirectory, "mcp.json");

        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [ClaudeCliArguments.McpServerName] = new Dictionary<string, object>
                {
                    ["command"] = orandactlPath,
                    ["args"] = new[] { "mcp", "--pipe", pipeName },
                    ["env"] = new Dictionary<string, string> { [AgentRunToken.EnvironmentVariable] = runToken },
                },
            },
        };

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(JsonSerializer.Serialize(config));
        return path;
    }
}
