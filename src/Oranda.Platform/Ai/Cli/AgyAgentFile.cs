using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Protocol;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 실행 전용 폴더의 <c>.agents/agents/oranda.md</c> 를 쓴다. agy 의 도구·사용자 설정 유입을 막는 유일하게 확인된 수단이다:
/// <c>excludeDefaultComponents: true</c>(내장 규칙·스킬·훅 제외)와 <c>inheritCustomizations: false</c>(사용자 설정 상속 끔)를
/// <b>둘 다</b> 줘야 한다. 하나만 있으면 훅이 실행되거나 <c>view_file</c>·<c>search_web</c> 같은 도구가 나타난다.
/// 도구는 구조 결과 실행이면 <c>[finish]</c>, 아니면 <c>[]</c> 다(스키마 없이 finish 를 쓰면 응답 앞에 <c>{}</c> 가 붙을 수 있다).
/// 실행 토큰은 이 파일에 넣지 않는다: agy 는 에이전트 정의(토큰이 든 env 포함)를 대화 기록
/// (<c>~/.gemini/antigravity-cli/conversations/&lt;id&gt;.db</c>)에 영구히 남기는 것이 확인되었다(E2E 로 확인). 토큰은 프로세스 환경 변수로만 넘기고
/// MCP 서버(orandactl)가 상속한다. 사용자 전역 MCP 서버가 있으면 그 서버도 상속하게 되므로 그 경우 드라이버가 실행을 거부한다.
/// </summary>
internal static class AgyAgentFile
{
    public const string RelativePath = ".agents/agents/oranda.md";

    /// <summary>
    /// MCP 도구 호출 하나의 제한(초). agy 의 기본 제한은 정확히 180초(3분)이고(측정: 200초 걸리는 호출이 180.02초에 오류 단계로 끊김),
    /// 서버 항목의 <c>timeoutSeconds</c> 로 바꿀 수 있음을 측정했다(25초로 두면 25초에 끊김). 벤치마크 호출은 운영 기본값으로도 150초,
    /// 제한을 늘린 설정에서는 훨씬 길 수 있으므로 한 차례 실행 제한(<see cref="AgyDriver.RunTimeLimit"/>)과 같은 6시간으로 둔다.
    /// </summary>
    public const int DefaultToolTimeoutSeconds = 21_600;

    public static string Write(string runDirectory, string orandactlPath, string pipeName, bool withFinishTool, IReadOnlyList<AgentToolInfo>? tools = null, int toolTimeoutSeconds = DefaultToolTimeoutSeconds)
    {
        PrivateDirectory.Ensure(runDirectory);
        var path = Path.Combine(runDirectory, ".agents", "agents", "oranda.md");
        PrivateDirectory.Ensure(Path.Combine(runDirectory, ".agents"));
        PrivateDirectory.Ensure(Path.GetDirectoryName(path)!);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(Render(orandactlPath, pipeName, withFinishTool, tools, toolTimeoutSeconds));
        return path;
    }

    /// <summary>에이전트 파일 내용. 글자열 값은 따옴표 문자열(JSON 이스케이프는 YAML 큰따옴표 문자열과 같다)로 써서 역슬래시·따옴표·비 ASCII 가 깨지지 않게 한다.</summary>
    internal static string Render(string orandactlPath, string pipeName, bool withFinishTool, IReadOnlyList<AgentToolInfo>? tools = null, int toolTimeoutSeconds = DefaultToolTimeoutSeconds)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: oranda\n");
        builder.Append("description: Oranda locked agent\n");
        builder.Append("mainAgent: true\n");
        builder.Append("subagent: false\n");
        builder.Append(withFinishTool ? "tools: [finish]\n" : "tools: []\n");
        builder.Append("excludeDefaultComponents: true\n");
        builder.Append("inheritCustomizations: false\n");
        builder.Append("mcpServers:\n");
        builder.Append("  - name: ").Append(ClaudeCliArguments.McpServerName).Append('\n');
        builder.Append("    command: ").Append(Quote(orandactlPath)).Append('\n');
        builder.Append("    args: [").Append(string.Join(", ", new[] { "mcp", "--pipe", pipeName }.Select(Quote))).Append("]\n");
        builder.Append("    timeoutSeconds: ").Append(toolTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("---\n");
        builder.Append("# System Prompt\n");
        builder.Append(SystemPrompt(tools));
        return builder.ToString();
    }

    /// <summary>
    /// 시스템 지시. 잠금(<c>excludeDefaultComponents</c>)은 MCP 도구 안내까지 빼서 모델이 서버 이름과 도구를 알 수 없다
    /// (<c>call_mcp_tool</c> 설명은 도구 스키마 파일을 읽으라고 하지만 파일 읽기 도구가 없다: 실제 왕복에서 모델이 서버 이름을 수십 개 추측하다 끝났다).
    /// 그래서 서버 이름(<c>orandactl</c>)과 게이트가 이 실행에 내주는 도구의 이름·설명·입력 스키마, 부르는 방법을 여기에 적는다.
    /// 이 파일은 대화 기록에 영구히 남으므로 토큰·파이프 이름 같은 비밀은 넣지 않는다.
    /// </summary>
    internal static string SystemPrompt(IReadOnlyList<AgentToolInfo>? tools)
    {
        var builder = new StringBuilder();
        builder.Append("You are a database tuning assistant inside Oranda.\n\n");
        builder.Append("The only tools you have are the Oranda tools of the MCP server named \"").Append(ClaudeCliArguments.McpServerName).Append("\".\n");
        if (tools is not { Count: > 0 })
        {
            builder.Append("No Oranda tool is available in this run; answer without calling any tool.\n");
            return builder.ToString();
        }

        builder.Append("Call them with the call_mcp_tool tool: set ServerName to \"").Append(ClaudeCliArguments.McpServerName)
            .Append("\", ToolName to one of the tool names below, and Arguments to a JSON object that matches the tool's input schema.\n");
        builder.Append("You cannot read tool schema files, so the schemas below are complete. Do not guess other server names or tool names.\n\n");
        builder.Append("Tools of the \"").Append(ClaudeCliArguments.McpServerName).Append("\" server:\n");
        foreach (var tool in tools)
        {
            builder.Append("\n- ").Append(tool.Name).Append(": ").Append(OneLine(tool.Description)).Append('\n');
            builder.Append("  input schema: ").Append(tool.InputSchema.ToJsonString()).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>줄바꿈을 공백으로 바꿔 한 줄로 만든다(시스템 지시의 목록 모양과, 본문 첫머리에 오는 <c>---</c> 줄로 머리말이 끝나 버리는 일을 막는다).</summary>
    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
