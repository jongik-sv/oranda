using System.Text.Json;

namespace Oranda.Platform.Ai.Cli;

/// <summary>한 번의 agy 실행에서 바뀌는 값.</summary>
/// <param name="ConversationId">이어 말할 대화 id. 새 대화면 null.</param>
/// <param name="JsonSchema">구조 결과 스키마를 한 줄로 직렬화한 글. 없으면 null.</param>
internal sealed record AgyInvocation(string? ConversationId, string? JsonSchema);

/// <summary>
/// agy 명령줄. 잠금(허용·거부 규칙, 슬래시 명령 끄기, 사용자 정의 에이전트)을 매번 전부 다시 준다 — 이어 말하기 때도 마찬가지다.
/// <c>-p</c> 는 쓰지 않는다(뒤 플래그를 프롬프트로 먹어 rc=2 로 끝난다). 모델·노력은 넘기지 않아 agy 의 기본값을 쓰고,
/// <c>--dangerously-*</c>·<c>--approve-for-me</c> 같은 승인 우회 플래그도 쓰지 않는다. 실행 토큰은 인자에 넣지 않는다(환경 변수로만 넘긴다).
/// </summary>
internal static class AgyCliArguments
{
    /// <summary>에이전트 파일 이름(<c>.agents/agents/oranda.md</c>)이자 <c>--agent</c> 값.</summary>
    public const string AgentName = "oranda";

    /// <summary>
    /// <c>--settings</c> 값: Oranda MCP 서버의 모든 도구만 허용하고 명령·파일 읽기·쓰기·URL 읽기를 거부한다.
    /// (이 규칙만으로는 <c>search_web</c> 같은 도구가 막히지 않는다: 도구 자체를 없애는 일은 에이전트 파일이 한다.)
    /// </summary>
    public const string SettingsJson =
        """{"permissions":{"allow":["mcp(orandactl/*)"],"deny":["command(*)","read_file(*)","write_file(*)","read_url(*)"]}}""";

    public static IReadOnlyList<string> Build(AgyInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = new List<string>
        {
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            "--agent", AgentName,
            "--settings", SettingsJson,
            "--disable-slash-commands",
        };

        if (!string.IsNullOrEmpty(invocation.JsonSchema))
        {
            args.Add("--json-schema");
            args.Add(invocation.JsonSchema);
        }

        if (!string.IsNullOrEmpty(invocation.ConversationId))
        {
            args.Add("--conversation");
            args.Add(invocation.ConversationId);
        }

        return args;
    }

    /// <summary>표준 입력 한 줄: <c>{"event":"user","message":{"content":"…"}}</c> + 줄바꿈. 기본 인코더가 비 ASCII 를 <c>\uXXXX</c> 로 써서 본문에 줄바꿈이 남지 않는다.</summary>
    public static string BuildStandardInput(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return JsonSerializer.Serialize(new { @event = "user", message = new { content = prompt } }) + "\n";
    }
}
