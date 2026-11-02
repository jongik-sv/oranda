using System.Diagnostics;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Ai.Profiles;

namespace Oranda.Platform.Tests.Ai.Cli;

public class ClaudeProfileEnvironmentTests
{
    private const string Key = "sk-env-test-key-9876543210";

    [Fact]
    public void Claude_code_adds_nothing()
    {
        var (variables, error) = ClaudeProfileEnvironment.Resolve(AiRoute.ClaudeCode);

        Assert.Null(error);
        Assert.Empty(variables);
    }

    [Fact]
    public void Anthropic_preset_sends_the_key_as_api_key_and_removes_the_auth_token()
    {
        var (variables, error) = ClaudeProfileEnvironment.Resolve(Route(AiProfilePreset.Anthropic, "https://api.example/v1", Key));

        Assert.Null(error);
        Assert.Equal(Key, variables["ANTHROPIC_API_KEY"]);
        Assert.Null(variables["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("https://api.example", variables["ANTHROPIC_BASE_URL"]);
        Assert.Equal("1", variables["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.Equal("7777", variables["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]);
    }

    [Theory]
    [InlineData(AiProfilePreset.ZAi)]
    [InlineData(AiProfilePreset.Custom)]
    public void Other_anthropic_format_presets_send_the_key_as_auth_token_with_an_empty_api_key(AiProfilePreset preset)
    {
        var (variables, error) = ClaudeProfileEnvironment.Resolve(Route(preset, "https://api.example/api/anthropic", Key));
        var (keyless, _) = ClaudeProfileEnvironment.Resolve(Route(preset, "https://api.example/api/anthropic", null));

        Assert.Null(error);
        Assert.Equal(Key, variables["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal(string.Empty, variables["ANTHROPIC_API_KEY"]);
        Assert.Equal("local", keyless["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("https://api.example/api/anthropic", variables["ANTHROPIC_BASE_URL"]);
    }

    [Fact]
    public void Tier_model_variables_are_filled_with_the_profile_model()
    {
        var (variables, _) = ClaudeProfileEnvironment.Resolve(Route(AiProfilePreset.Custom, "http://llm.example", null));

        // 지우기만 하면 보조 호출이 내장 Claude 모델명으로 나간다: 프로필 모델로 채운다.
        foreach (var name in ClaudeProfileEnvironment.TierModelVariables)
        {
            Assert.Equal("m", variables[name]);
        }
    }

    [Fact]
    public void Profile_launch_drops_every_inherited_anthropic_claude_and_otel_variable_except_the_allowed_ones()
    {
        var parent = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/u",
            ["USERPROFILE"] = "C:/Users/u",
            ["SYSTEMROOT"] = "C:/Windows",
            ["TEMP"] = "/tmp",
            ["UNRELATED"] = "keep",
            ["CLAUDE_CONFIG_DIR"] = "/home/u/.claude-alt",
            ["CLAUDE_CODE_GIT_BASH_PATH"] = "C:/Git/bash.exe",

            // 한 가지씩 알려진 이름을 지우면 다음 우회가 나온다: 접두사 전체를 넘기지 않는다.
            ["ANTHROPIC_UNIX_SOCKET"] = "/tmp/x.sock",
            ["ANTHROPIC_AUTH_TOKEN"] = "parent-subscription-token",
            ["ANTHROPIC_CUSTOM_HEADERS"] = "X-Leak: 1",
            ["ANTHROPIC_MODEL"] = "parent-model",
            ["ANTHROPIC_FUTURE_SETTING"] = "x",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["CLAUDE_CODE_USE_VERTEX"] = "1",
            ["CLAUDE_CODE_USE_FOUNDRY"] = "1",
            ["CLAUDE_CODE_USE_ANTHROPIC_AWS"] = "1",
            ["CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD"] = "1",
            ["CLAUDE_CODE_USE_MANTLE"] = "1",
            ["CLAUDE_CODE_USE_GATEWAY"] = "1",
            ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_FUTURE_SWITCH"] = "1",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://collector.example",
            ["OTEL_LOG_USER_PROMPTS"] = "1",
        };
        var (variables, _) = ClaudeProfileEnvironment.Resolve(Route(AiProfilePreset.Anthropic, "https://api.example", Key));

        SystemClaudeProcessLauncher.ApplyEnvironment(parent, Launch(variables, isolate: true));

        foreach (var name in new[]
        {
            "ANTHROPIC_UNIX_SOCKET", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_MODEL", "ANTHROPIC_FUTURE_SETTING",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_USE_ANTHROPIC_AWS",
            "CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD", "CLAUDE_CODE_USE_MANTLE", "CLAUDE_CODE_USE_GATEWAY", "CLAUDE_CODE_ENABLE_TELEMETRY",
            "CLAUDECODE", "CLAUDE_FUTURE_SWITCH", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_LOG_USER_PROMPTS",
        })
        {
            Assert.False(parent.ContainsKey(name), name);
        }

        Assert.Equal("/home/u", parent["HOME"]);
        Assert.Equal("C:/Users/u", parent["USERPROFILE"]);
        Assert.Equal("C:/Windows", parent["SYSTEMROOT"]);
        Assert.Equal("/tmp", parent["TEMP"]);
        Assert.Equal("keep", parent["UNRELATED"]);
        Assert.Equal("/home/u/.claude-alt", parent["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("C:/Git/bash.exe", parent["CLAUDE_CODE_GIT_BASH_PATH"]);

        // 프로필 값은 격리 뒤에 적용된다.
        Assert.Equal(Key, parent["ANTHROPIC_API_KEY"]);
        Assert.Equal("https://api.example", parent["ANTHROPIC_BASE_URL"]);
        Assert.Equal("1", parent["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
    }

    [Fact]
    public void Claude_code_launch_keeps_the_parent_environment_untouched()
    {
        var parent = new Dictionary<string, string?> { ["PATH"] = "/usr/bin", ["ANTHROPIC_UNIX_SOCKET"] = "/tmp/x.sock", ["CLAUDE_CODE_USE_BEDROCK"] = "1", ["OTEL_X"] = "1", ["HTTPS_PROXY"] = "http://proxy.example:3128" };

        SystemClaudeProcessLauncher.ApplyEnvironment(parent, Launch(ClaudeProfileEnvironment.Resolve(AiRoute.ClaudeCode).Variables, isolate: false));

        Assert.Equal("/tmp/x.sock", parent["ANTHROPIC_UNIX_SOCKET"]);
        Assert.Equal("1", parent["CLAUDE_CODE_USE_BEDROCK"]);
        Assert.Equal("1", parent["OTEL_X"]);
        Assert.Equal("http://proxy.example:3128", parent["HTTPS_PROXY"]);
    }

    [Theory]
    [InlineData("http://llm.example:8080")]
    [InlineData("https://intranet-llm-gateway.corp.example/v1")]
    [InlineData("https://10.1.2.3")]
    [InlineData("https://api.example.com/v1")]
    public void Proxy_variables_are_never_passed_to_a_profile_whatever_the_address(string baseUrl)
    {
        var (variables, _) = ClaudeProfileEnvironment.Resolve(Route(AiProfilePreset.Custom, baseUrl, null));
        var parent = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HTTP_PROXY"] = "http://proxy.example:3128",
            ["HTTPS_PROXY"] = "http://proxy.example:3128",
            ["ALL_PROXY"] = "http://proxy.example:3128",
            ["NO_PROXY"] = "localhost",
            ["http_proxy"] = "http://proxy.example:3128",
            ["https_proxy"] = "http://proxy.example:3128",
            ["all_proxy"] = "http://proxy.example:3128",
            ["no_proxy"] = "localhost",
        };

        SystemClaudeProcessLauncher.ApplyEnvironment(parent, Launch(variables, isolate: true));

        Assert.Equal(["PATH"], parent.Keys.Where(k => k.Contains("proxy", StringComparison.OrdinalIgnoreCase) || k == "PATH"));
    }

    [Fact]
    public void Probe_transport_uses_no_proxy_and_follows_no_redirects()
    {
        using var handler = AiProfileProbe.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void Applying_to_a_parent_environment_replaces_and_removes_the_inherited_values()
    {
        var parent = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["ANTHROPIC_AUTH_TOKEN"] = "parent-subscription-token",
            ["ANTHROPIC_BASE_URL"] = "https://parent.example",
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "claude-haiku-parent",
            ["UNRELATED"] = "keep",
        };
        var (variables, _) = ClaudeProfileEnvironment.Resolve(Route(AiProfilePreset.Anthropic, "https://api.example", Key));

        SystemClaudeProcessLauncher.ApplyEnvironment(parent, Launch(variables));

        Assert.Equal(Key, parent["ANTHROPIC_API_KEY"]);
        Assert.Equal("https://api.example", parent["ANTHROPIC_BASE_URL"]);
        Assert.False(parent.ContainsKey("ANTHROPIC_AUTH_TOKEN")); // 제거 목록(null)만으로도 지워진다
        Assert.Equal("m", parent["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("keep", parent["UNRELATED"]);
        Assert.StartsWith(Path.GetDirectoryName(Launch(variables).ExecutablePath)!, parent["PATH"], StringComparison.Ordinal);
    }

    [Fact]
    public void Real_process_start_info_carries_the_combination_and_claude_code_adds_nothing()
    {
        var (variables, _) = ClaudeProfileEnvironment.Resolve(Route(AiProfilePreset.ZAi, "https://api.example/api/anthropic", Key));

        var profileInfo = SystemClaudeProcessLauncher.CreateStartInfo(Launch(variables, isolate: true));
        var codeInfo = SystemClaudeProcessLauncher.CreateStartInfo(Launch(ClaudeProfileEnvironment.Resolve(AiRoute.ClaudeCode).Variables));

        Assert.Equal(Key, profileInfo.Environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal(string.Empty, profileInfo.Environment["ANTHROPIC_API_KEY"]);
        Assert.Equal("https://api.example/api/anthropic", profileInfo.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("1", profileInfo.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.DoesNotContain(profileInfo.ArgumentList, a => a.Contains(Key, StringComparison.Ordinal));
        Assert.True(profileInfo.RedirectStandardInput);

        // 이 시험을 도는 PC 의 부모 환경이 무엇이든, 프로필 실행에는 허용된 이름과 프로필이 정한 값만 ANTHROPIC/CLAUDE/OTEL 접두사로 남는다.
        foreach (var name in profileInfo.Environment.Keys.Where(ClaudeProfileEnvironment.IsScrubbed))
        {
            Assert.Contains(name, variables.Where(v => v.Value is not null).Select(v => v.Key));
        }

        // Claude Code 경로: 부모 환경에서 PATH 앞 폴더만 더할 뿐 ANTHROPIC_* 는 그대로 둔다.
        var fresh = new ProcessStartInfo().Environment;
        foreach (var name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_MAX_OUTPUT_TOKENS" })
        {
            Assert.Equal(fresh.TryGetValue(name, out var inherited) ? inherited : null, codeInfo.Environment.TryGetValue(name, out var actual) ? actual : null);
        }
    }

    [Fact]
    public void Launch_to_string_never_prints_the_key_or_the_input()
    {
        var (variables, _) = ClaudeProfileEnvironment.Resolve(Route(AiProfilePreset.Anthropic, "https://api.example", Key));

        var text = Launch(variables).ToString();

        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SQL", text, StringComparison.Ordinal);
    }

    private static ClaudeLaunch Launch(IReadOnlyDictionary<string, string?> variables, bool isolate = false)
        => new(Path.Combine("opt", "claude", "claude"), ["-p"], Path.GetTempPath(), "SECRET-SQL", variables, IsolateEnvironment: isolate);

    private static AiRoute Route(AiProfilePreset preset, string baseUrl, string? key)
        => new(
            new AiLlmProfile { Id = Guid.NewGuid(), Name = "p", Preset = preset, Format = AiApiFormat.Anthropic, BaseUrl = baseUrl, Model = "m", MaxOutputTokens = 7777 },
            key);
}
