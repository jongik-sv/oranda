using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// Codex 인자 골든. 기대 목록은 드라이버의 <c>LockConfig</c> 를 참조하지 않고 이 파일에 글자 그대로 적는다:
/// 잠금 플래그 하나가 빠지면(그러면 exec·apply_patch 등이 되살아난다) 이 시험이 실패해야 한다.
/// </summary>
public sealed class CodexArgumentsTests
{
    private static readonly string[] LockFlags =
    [
        "-c", "features.shell_tool=false",
        "-c", "features.unified_exec=false",
        "-c", "features.view_image=false",
        "-c", "features.apps=false",
        "-c", "analytics.enabled=false",
        "-c", "features.browser_use=false",
        "-c", "features.browser_use_external=false",
        "-c", "features.in_app_browser=false",
        "-c", "features.image_generation=false",
        "-c", "features.multi_agent=false",
        "-c", "features.plugins=false",
        "-c", "features.hooks=false",
        "-c", "features.goals=false",
        "-c", "features.tool_suggest=false",
        "-c", "features.skill_search=false",
        "-c", "features.sleep_tool=false",
        "-c", "features.memories=false",
        "-c", "features.request_permissions_tool=false",
        "-c", "web_search=\"disabled\"",
        "-c", "features.shell_snapshot=false",
        "-c", "features.computer_use=false",
        "-c", "features.daemon_auto_start=false",
        "-c", "features.plugin_discovery_prefetch=false",
        "-c", "features.remote_plugin=false",
        "-c", "features.plugin_sharing=false",
        "-c", "features.skill_mcp_dependency_install=false",
        "-c", "features.workspace_dependencies=false",
    ];

    private static CodexInvocation Invocation(string? thread = null, string? schema = null, string[]? images = null, string runDirectory = "/run/dir") =>
        new(thread, runDirectory, "/run/dir/model-catalog.json", "/opt/oranda/orandactl", "pipe-1", "ORANDA_RUN_TOKEN", schema, images);

    private static readonly string[] CommonTail =
    [
        "-c", "model_catalog_json=\"/run/dir/model-catalog.json\"",
        "-c", "skills.include_instructions=false",
        "-c", "project_doc_max_bytes=0",
        "-c", "mcp_servers.orandactl.command=\"/opt/oranda/orandactl\"",
        "-c", "mcp_servers.orandactl.args=[\"mcp\",\"--pipe\",\"pipe-1\"]",
        "-c", "mcp_servers.orandactl.env_vars=[\"ORANDA_RUN_TOKEN\"]",
        "-c", "mcp_servers.orandactl.default_tools_approval_mode=\"approve\"",
        "-c", "mcp_servers.orandactl.tool_timeout_sec=21600",
    ];

    [Fact]
    public void New_conversation_arguments_are_exactly_the_locked_list()
    {
        string[] expected =
        [
            "exec", "--json", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules", "--strict-config",
            "-C", "/run/dir", "-s", "read-only",
            .. LockFlags,
            .. CommonTail,
            "-",
        ];

        Assert.Equal(expected, CodexArguments.Build(Invocation()));
    }

    [Fact]
    public void Resume_arguments_repeat_every_lock_flag_and_use_sandbox_mode_instead_of_dash_C_and_dash_s()
    {
        string[] expected =
        [
            "exec", "resume", "--json", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules", "--strict-config",
            .. LockFlags,
            "-c", "sandbox_mode=\"read-only\"",
            .. CommonTail,
            "01a19f88-3e8b-7531-9054-07ec9d7111ea", "-",
        ];

        Assert.Equal(expected, CodexArguments.Build(Invocation("01a19f88-3e8b-7531-9054-07ec9d7111ea")));
    }

    [Fact]
    public void Schema_and_images_are_added_before_the_conversation_id_and_stdin_marker()
    {
        var fresh = CodexArguments.Build(Invocation(schema: "/run/dir/schema.json", images: ["/run/dir/image-1.png", "/run/dir/image-2.jpg"]));
        Assert.Equal(
            ["--output-schema", "/run/dir/schema.json", "-i", "/run/dir/image-1.png", "-i", "/run/dir/image-2.jpg", "-"],
            fresh.TakeLast(7));

        var resumed = CodexArguments.Build(Invocation("thread-9", "/run/dir/schema.json", ["/run/dir/image-1.png"]));
        Assert.Equal(["--output-schema", "/run/dir/schema.json", "-i", "/run/dir/image-1.png", "thread-9", "-"], resumed.TakeLast(6));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("thread-1")]
    public void Forbidden_arguments_never_appear(string? thread)
    {
        var arguments = CodexArguments.Build(Invocation(thread, "/run/dir/schema.json", ["/run/dir/image-1.png"]));

        Assert.DoesNotContain(arguments, a => a.StartsWith("--dangerously", StringComparison.Ordinal));
        Assert.DoesNotContain("--approve-for-me", arguments);
        Assert.DoesNotContain("--ephemeral", arguments);
        Assert.DoesNotContain("-m", arguments);
        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("model_reasoning_effort", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.Contains("env.", StringComparison.Ordinal)); // -c ...env.X=값 은 프로세스 목록에 값이 보인다
        Assert.Equal(thread is null, arguments.Contains("-C"));
        Assert.Equal(thread is null, arguments.Contains("-s"));
        Assert.Equal(thread is not null, arguments.Contains("resume"));
        Assert.Equal("-", arguments[^1]);
    }

    [Fact]
    public void Token_value_is_never_an_argument_only_the_variable_name()
    {
        var arguments = CodexArguments.Build(Invocation());

        Assert.Contains(arguments, a => a.Contains("env_vars=[\"ORANDA_RUN_TOKEN\"]", StringComparison.Ordinal));
        Assert.Equal(1, arguments.Count(a => a.Contains("TOKEN", StringComparison.Ordinal)));
    }

    [Fact]
    public void Paths_with_spaces_quotes_and_backslashes_become_valid_toml_strings()
    {
        var invocation = Invocation(runDirectory: "/Users/x/Library/Application Support/Oranda") with
        {
            CatalogPath = "C:\\Users\\jji\\a \"b\".json",
            OrandactlPath = "C:\\Program Files\\Oranda\\orandactl.exe",
        };

        var arguments = CodexArguments.Build(invocation);

        Assert.Contains("model_catalog_json=\"C:\\\\Users\\\\jji\\\\a \\\"b\\\".json\"", arguments);
        Assert.Contains("mcp_servers.orandactl.command=\"C:\\\\Program Files\\\\Oranda\\\\orandactl.exe\"", arguments);
        Assert.Contains("/Users/x/Library/Application Support/Oranda", arguments); // -C 값은 TOML 이 아니라 그대로
    }

    [Fact]
    public void Toml_string_escapes_control_characters()
    {
        Assert.Equal("\"a\\nb\\u0001\"", CodexArguments.TomlString("a\nb\u0001"));
    }
}
