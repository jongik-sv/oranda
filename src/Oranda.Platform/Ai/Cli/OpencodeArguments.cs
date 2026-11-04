using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oranda.Platform.Ai.Cli;

/// <summary>opencode 실행 하나에 필요한 값.</summary>
/// <param name="SessionId">이어 말할 대화 id(<c>ses_</c> 로 시작). 새 대화면 null.</param>
internal sealed record OpencodeInvocation(string? SessionId);

/// <summary>
/// <c>opencode run</c> 인자와 잠금 설정. 도구를 막는 수단은 인자가 아니라 환경 변수 <c>OPENCODE_CONFIG_CONTENT</c> 로 주입하는 설정이다(<see cref="BuildConfigContent"/>).
/// 잠금 판단은 모두 이 파일 한 곳에 모은다. 근거(opencode 2.0.32, macOS 에서 가짜 LLM 이 받는 tools 배열을 직접 캡처해 확인):
/// <list type="bullet">
/// <item>최상위 <c>permissions</c> 의 deny-* 만으로는 부족하다: 사용자의 하위 설정 층이 에이전트별 <c>permissions</c> 를 전부 허용으로 두면 도구 12개가 되살아난다.
/// 그래서 잠긴 전용 에이전트(<see cref="AgentName"/>)를 정의하고 <c>default_agent</c> 와 <c>--agent</c> 로 둘 다 지정한다(새 대화·이어 말하기 모두).</item>
/// <item>MCP 도구는 <c>execute</c>(코드 모드, <c>fetch</c> 가 열려 있어 외부 HTTP 가 나간다)를 거쳐야만 호출되므로 쓰지 않는다. 대신 플러그인(<see cref="OpencodePluginFile"/>)이 직접 도구를 등록한다.</item>
/// <item>사용자 전역 MCP 서버는 잠금과 상관없이 자식 프로세스로 뜨고 부모 환경(실행 토큰 포함)을 상속한다: 이름별로 <c>enabled:false</c> 로 덮어 쓴다.</item>
/// </list>
/// 토큰은 인자·설정에 넣지 않는다: 프로세스 환경 변수 하나로만 넘기고 플러그인이 읽는다.
/// </summary>
internal static partial class OpencodeArguments
{
    /// <summary>잠긴 전용 에이전트 이름. 사용자 설정과 겹치지 않게 드문 이름을 쓴다.</summary>
    public const string AgentName = "oranda_locked";

    /// <summary>구조 결과를 내는 도구 이름. 앞으로 생길 내장 도구·앱 도구와 겹치지 않게 접두어를 둔다.</summary>
    public const string FinishToolName = "oranda_finish";

    /// <summary>opencode 가 새 대화에 붙이는 id 모양(<c>ses_</c> + 영숫자). 옵션처럼 보이는 글이 인자로 들어가는 일을 막는다.</summary>
    [GeneratedRegex(@"^ses_[A-Za-z0-9]{8,64}\z")]
    public static partial Regex SessionIdPattern();

    /// <summary>허용 규칙(permission action)에 쓰는 도구 이름 문자. 이 밖의 글자(<c>*</c> 등)는 허용 범위를 넓힐 수 있으므로 실행하지 않는다.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    public static partial Regex ToolNamePattern();

    /// <summary>
    /// opencode 내장 도구로 알려진 이름. 앱 도구가 이 이름을 쓰면 플러그인이 내장 도구를 지우지 못하고 앱 도구도 등록하지 못하며,
    /// 허용 규칙과 스트림 감시는 그 이름을 그대로 통과시키므로 쓰지 못하게 한다(대소문자 구분 없이).
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "read", "write", "edit", "multiedit", "patch", "apply_patch", "bash", "shell", "grep", "glob", "list", "ls",
        "webfetch", "websearch", "codesearch", "fetch", "task", "todowrite", "todoread", "skill", "question", "lsp",
        "execute", "invalid", "plan_enter", "plan_exit",
    };

    public static IReadOnlyList<string> Build(OpencodeInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        // --auto 는 쓰지 않는다(허용 밖 권한 요청을 자동 승인한다). 프롬프트는 표준 입력으로 준다(명령줄 길이 한도와 '-' 로 시작하는 글을 피한다).
        var arguments = new List<string> { "run", "--standalone", "--format", "json", "--agent", AgentName };
        if (invocation.SessionId is { Length: > 0 } sessionId)
        {
            arguments.AddRange(["--session", sessionId]);
        }

        return arguments;
    }

    /// <summary>
    /// <c>OPENCODE_CONFIG_CONTENT</c> 로 주입할 설정 JSON. 잠금 규칙(deny-* 다음에 도구 이름별 allow)은 최상위와 전용 에이전트에 똑같이 넣는다.
    /// <paramref name="mcpServerNames"/> 는 사용자 설정에 있는 MCP 서버 이름이다(모두 끈다).
    /// <paramref name="userSettings"/> 는 사용자 전역 설정에서 옮겨 온 공급자·모델 선택이다(<see cref="OpencodeGlobalConfig.ReadUserSettings"/>): 잠금 키보다 먼저 넣어
    /// 같은 키가 있어도 잠금이 이긴다.
    /// </summary>
    public static string BuildConfigContent(IReadOnlyList<string> allowedTools, IReadOnlyList<string> mcpServerNames, JsonObject? userSettings = null)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        ArgumentNullException.ThrowIfNull(mcpServerNames);

        var root = new JsonObject();
        foreach (var (key, value) in userSettings ?? [])
        {
            root[key] = value?.DeepClone();
        }

        root["default_agent"] = AgentName;
        root["permissions"] = Rules(allowedTools);
        root["agents"] = new JsonObject
        {
            [AgentName] = new JsonObject
            {
                ["mode"] = "primary",
                ["description"] = "Oranda locked agent",
                ["permissions"] = Rules(allowedTools),
            },
        };

        if (mcpServerNames.Count > 0)
        {
            var servers = new JsonObject();
            foreach (var name in mcpServerNames)
            {
                servers[name] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(OperatingSystem.IsWindows() ? "cmd" : "true"),
                    ["enabled"] = false,
                };
            }

            root["mcp"] = servers;
        }

        return root.ToJsonString();
    }

    private static JsonArray Rules(IReadOnlyList<string> allowedTools)
    {
        var rules = new JsonArray { Rule("*", "deny") };
        foreach (var tool in allowedTools)
        {
            rules.Add(Rule(tool, "allow"));
        }

        return rules;
    }

    private static JsonObject Rule(string action, string effect) => new() { ["action"] = action, ["resource"] = "*", ["effect"] = effect };
}
