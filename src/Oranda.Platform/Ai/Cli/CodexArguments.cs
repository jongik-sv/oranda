using System.Text;

namespace Oranda.Platform.Ai.Cli;

/// <summary>Codex 실행 하나에 필요한 값. 모델·추론 노력은 없다: 각 CLI 의 기본 모델을 쓴다(Claude 전용 값이 섞이지 않게).</summary>
/// <param name="ThreadId">이어 말할 대화 id(<c>thread.started</c> 의 thread_id). 새 대화면 null.</param>
/// <param name="RunDirectory">이 실행 전용 폴더(새 대화는 <c>-C</c>, 이어 말하기는 프로세스 작업 폴더).</param>
/// <param name="CatalogPath">도구를 뺀 모델 카탈로그 파일(<see cref="CodexModelCatalog"/>).</param>
/// <param name="OrandactlPath">orandactl 실행 파일.</param>
/// <param name="PipeName">브리지 파이프 이름.</param>
/// <param name="TokenVariable">orandactl 이 상속받을 실행 토큰 환경 변수 이름(값은 인자에 넣지 않는다).</param>
/// <param name="OutputSchemaPath">구조 결과 스키마 파일. 없으면 null.</param>
/// <param name="ImagePaths">붙일 이미지 파일(실행 폴더의 임시 파일).</param>
internal sealed record CodexInvocation(
    string? ThreadId,
    string RunDirectory,
    string CatalogPath,
    string OrandactlPath,
    string PipeName,
    string TokenVariable,
    string? OutputSchemaPath = null,
    IReadOnlyList<string>? ImagePaths = null);

/// <summary>
/// <c>codex exec</c> 인자. 도구를 모두 끄는 잠금 인자는 새 대화와 이어 말하기 <b>둘 다</b> 같은 목록을 매번 전부 준다:
/// 빼면 exec·exec_command·apply_patch·view_image 가 되살아난다. 잠금 인자는 이 파일 한 곳에 모은다(<see cref="LockConfig"/>).
/// <c>--dangerously-*</c>·<c>--ephemeral</c>(이어 말하기가 막힌다)·<c>-m</c>·<c>model_reasoning_effort</c> 는 쓰지 않는다.
/// 토큰은 인자에 넣지 않는다: <c>env_vars</c> 로 orandactl 이 프로세스 환경에서 상속받는다.
/// </summary>
internal static class CodexArguments
{
    public const string McpServerName = "orandactl";

    /// <summary>
    /// MCP 도구 호출 한 건의 시간 제한(초) = 6시간. Codex 기본값에 기대지 않고 명시한다: 벤치마크 호출은 접속 설정에 따라 수 분에서 수십 분까지 걸리고(제한 0 도 가능),
    /// 호출을 중간에 포기하면 운영 DB 쪽 작업이 길어질 수 있다. 멈춘 CLI 는 사용자 중지와 한 차례 실행 제한(<c>CodexDriver.DefaultTimeout</c>)으로 끝낸다.
    /// </summary>
    public const int ToolTimeoutSeconds = 21600;

    /// <summary>
    /// 잠금 <c>-c</c> 값(<c>키=값</c> 모양 그대로). 한 줄을 더하거나 빼면 새 대화·이어 말하기 모두에 반영된다
    /// (사용 통계 끄기 같은 값은 효과가 확인되면 여기에 한 줄로 더한다).
    /// </summary>
    public static IReadOnlyList<string> LockConfig { get; } =
    [
        "features.shell_tool=false",
        "features.unified_exec=false", // 주의: codex features list 는 이 키를 줘도 true 로 남는다(0.166.0). 이 키만 보고 막힌다고 믿지 말 것 — 실제 차단은 모델 카탈로그 덮어쓰기와 E2E 의 도구 선언·강제 호출 검사가 맡는다
        "features.view_image=false",
        "features.apps=false", // 앱 커넥터(mcp__codex_apps__*)를 막는 유일한 플래그
        "analytics.enabled=false", // 사용 통계 전송(analytics-events POST)을 끈다. 백엔드 요청이 0 이 되지는 않는다
        "features.browser_use=false",
        "features.browser_use_external=false",
        "features.in_app_browser=false",
        "features.image_generation=false",
        "features.multi_agent=false",
        "features.plugins=false", // 원격 설치형 플러그인은 --ignore-user-config 로 막히지 않는다
        "features.hooks=false", // --ignore-user-config 는 훅 신뢰 기록만 지운다
        "features.goals=false",
        "features.tool_suggest=false",
        "features.skill_search=false",
        "features.sleep_tool=false",
        "features.memories=false",
        "features.request_permissions_tool=false",
        "web_search=\"disabled\"",

        // 잠금 뒤에도 기본값이 켜져 있어 자식 프로세스를 띄우거나 디스크·네트워크를 쓰는 기능(codex features list 로 확인).
        "features.shell_snapshot=false", // 사용자의 로그인 셸을 띄우고 $CODEX_HOME/shell_snapshots 에 환경 변수 전부(실행 토큰 포함)를 평문으로 쓴다
        "features.computer_use=false",
        "features.daemon_auto_start=false", // 실행이 끝난 뒤에도 남는 백그라운드 프로세스
        "features.plugin_discovery_prefetch=false",
        "features.remote_plugin=false",
        "features.plugin_sharing=false",
        "features.skill_mcp_dependency_install=false",
        "features.workspace_dependencies=false",
    ];

    public static IReadOnlyList<string> Build(CodexInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var resume = invocation.ThreadId is { Length: > 0 };
        var arguments = new List<string> { "exec" };
        if (resume)
        {
            arguments.Add("resume");
        }

        // --strict-config: 잠금 키의 오타·개명을 조용히 무시하지 않고 시작 전에 실패하게 한다(무시되면 도구가 되살아난다).
        arguments.AddRange(["--json", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules", "--strict-config"]);
        if (!resume)
        {
            // 이어 말하기에는 -C·-s 옵션이 없다: 폴더는 프로세스 작업 폴더로, 샌드박스는 sandbox_mode 설정으로 준다.
            arguments.AddRange(["-C", invocation.RunDirectory, "-s", "read-only"]);
        }

        foreach (var setting in LockConfig)
        {
            arguments.AddRange(["-c", setting]);
        }

        if (resume)
        {
            arguments.AddRange(["-c", "sandbox_mode=\"read-only\""]);
        }

        arguments.AddRange(
        [
            "-c", "model_catalog_json=" + TomlString(invocation.CatalogPath),
            "-c", "skills.include_instructions=false",
            "-c", "project_doc_max_bytes=0",
            "-c", $"mcp_servers.{McpServerName}.command=" + TomlString(invocation.OrandactlPath),
            "-c", $"mcp_servers.{McpServerName}.args=" + TomlArray(["mcp", "--pipe", invocation.PipeName]),
            "-c", $"mcp_servers.{McpServerName}.env_vars=" + TomlArray([invocation.TokenVariable]),
            "-c", $"mcp_servers.{McpServerName}.default_tools_approval_mode=\"approve\"",
            "-c", $"mcp_servers.{McpServerName}.tool_timeout_sec={ToolTimeoutSeconds}",
        ]);

        if (invocation.OutputSchemaPath is { Length: > 0 } schema)
        {
            arguments.AddRange(["--output-schema", schema]);
        }

        foreach (var image in invocation.ImagePaths ?? [])
        {
            arguments.AddRange(["-i", image]);
        }

        if (resume)
        {
            arguments.Add(invocation.ThreadId!);
        }

        // 프롬프트는 표준 입력으로 준다(명령줄 길이 한도와 '-' 로 시작하는 글을 피한다).
        arguments.Add("-");
        return arguments;
    }

    /// <summary>TOML 기본 문자열. 역슬래시·따옴표·제어 문자를 이스케이프한다(Windows 경로의 <c>\U</c> 가 깨지지 않게).</summary>
    internal static string TomlString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or '\u007f' => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    internal static string TomlArray(IEnumerable<string> values) => "[" + string.Join(",", values.Select(TomlString)) + "]";
}
