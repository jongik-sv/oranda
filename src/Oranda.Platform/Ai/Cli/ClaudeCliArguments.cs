namespace Oranda.Platform.Ai.Cli;

/// <summary>claude -p 한 번의 실행에 필요한 값. 계약(AiRequest)과 무관한 내부 형태다.</summary>
/// <param name="McpConfigPath">이 실행 전용 mcp-config 파일 경로(orandactl 항목과 실행 토큰 env 가 들어 있다).</param>
/// <param name="JsonSchema">결과를 맞출 JSON 스키마 글. null 이면 자유 글.</param>
/// <param name="Model">모델 이름이나 별칭. null 이면 claude 기본값.</param>
/// <param name="ResumeId">이어 말할 세션 id. 새 대화면 null.</param>
internal sealed record ClaudeCliInvocation(string McpConfigPath, string? JsonSchema = null, string? Model = null, string? ResumeId = null);

/// <summary>
/// claude -p 인자 목록(b0 탐침으로 확인한 조합). 프롬프트는 인자로 싣지 않는다 — 표준 입력으로 쓰고 닫는다
/// (Windows 명령줄 32K 한도를 넘는 SQL 글과 '-' 로 시작하는 글을 안전하게 넘기기 위해서다).
/// </summary>
internal static class ClaudeCliArguments
{
    /// <summary>mcp-config 에 적는 서버 이름. 도구 이름은 <c>mcp__orandactl__&lt;도구&gt;</c> 가 된다.</summary>
    public const string McpServerName = "orandactl";

    public const string McpToolPrefix = "mcp__" + McpServerName + "__";

    public static IReadOnlyList<string> Build(ClaudeCliInvocation invocation)
    {
        // resume 때도 mcp-config·strict·allowedTools·setting-sources 를 다시 준다. 빼면 orandactl 이 사라지고
        // claude.ai 커넥터(Gmail·Drive 등)가 붙는다. --bare 는 구독 인증을 깨므로 쓰지 않는다.
        var arguments = new List<string>
        {
            "-p",
            "--mcp-config", invocation.McpConfigPath,
            "--strict-mcp-config",
            "--allowedTools", "mcp__" + McpServerName,
            "--setting-sources", "project",

            // 내장 도구(Read·Edit·Task 등)를 모두 끈다. 남는 것은 MCP 도구와 합성 도구뿐이라 파일·셸 통로가 없다.
            "--tools", string.Empty,
            "--output-format", "stream-json",
            "--verbose",
        };

        if (invocation.JsonSchema is not null)
        {
            arguments.Add("--json-schema");
            arguments.Add(invocation.JsonSchema);
        }

        if (invocation.Model is not null)
        {
            arguments.Add("--model");
            arguments.Add(invocation.Model);
        }

        if (invocation.ResumeId is not null)
        {
            arguments.Add("--resume");
            arguments.Add(invocation.ResumeId);
        }

        // 가변 인자 옵션이라 마지막에 둔다(뒤에 오는 값을 도구 이름으로 삼킨다).
        arguments.AddRange(["--disallowedTools", "WebFetch", "WebSearch", "Bash"]);
        return arguments;
    }
}
