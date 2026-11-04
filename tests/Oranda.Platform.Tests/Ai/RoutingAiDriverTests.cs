using System.Reflection;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai;

/// <summary>
/// 활성 프로필에 따른 경로 선택과 보내기 전 검증. 세션이 없는 요청은 CliDriver 가 <c>session_required</c> 로 끝내므로, 그 오류가 곧 「CLI 경로를 탔다」 는 표지다
/// (프로세스를 띄우지 않고 경로만 확인한다).
/// </summary>
public sealed class RoutingAiDriverTests
{
    private static readonly AiRequest Request = new("질문", new AiContext("p", null, AiAccessLevel.Plan));

    private static readonly AiLlmProfile AnthropicProfile = new()
    {
        Id = Guid.NewGuid(),
        Name = "사내 Anthropic 형식",
        Preset = AiProfilePreset.Custom,
        Format = AiApiFormat.Anthropic,
        BaseUrl = "http://llm.example:8080",
        Model = "m",
    };

    private static readonly AiLlmProfile OpenAiProfile = AnthropicProfile with { Id = Guid.NewGuid(), Name = "사내 OpenAI 형식", Format = AiApiFormat.OpenAi };

    private readonly FakeSettingsStore _settings = new();
    private readonly FakeCredentialStore _credentials = new();

    private RoutingAiDriver Create(IApiAiDriver? api = null, ICredentialStore? credentials = null)
    {
        var cli = new CliDriver(new FakeRunTokens(), new FakeGate(), new FixedBridgeEndpoint("pipe"), new AppDataPaths(Path.GetTempPath()), _settings);
        return new RoutingAiDriver(_settings, credentials ?? _credentials, cli, new Services(api));
    }

    private void Use(AiDriverKind driver, Guid? active = null, params AiLlmProfile[] profiles) =>
        _settings.Current = new AppSettings { Ai = new AiSettings { Driver = driver, ActiveProfileId = active, Profiles = profiles } };

    private static async Task<List<AiEvent>> RunAsync(RoutingAiDriver driver, AiRequest? request = null)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request ?? Request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private static string? CodeOf(IReadOnlyList<AiEvent> events) => Assert.IsType<AiError>(Assert.Single(events)).Code;

    [Fact]
    public async Task Driver_none_ends_with_an_error_and_starts_nothing()
    {
        var api = new ScriptedApiDriver(new AiFinal("x", null, null));
        Use(AiDriverKind.None, OpenAiProfile.Id, OpenAiProfile);

        Assert.Equal("driver_none", CodeOf(await RunAsync(Create(api))));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Claude_code_without_a_profile_goes_to_the_cli_driver()
    {
        Use(AiDriverKind.ClaudeCode, null, OpenAiProfile);

        Assert.Equal("session_required", CodeOf(await RunAsync(Create())));
    }

    [Fact]
    public async Task Anthropic_format_profile_goes_to_the_cli_driver()
    {
        var api = new ScriptedApiDriver();
        Use(AiDriverKind.ClaudeCode, AnthropicProfile.Id, AnthropicProfile);

        // 요청에 실행 범위(Session)가 없어 CliDriver 가 session_required 로 닫는다: 프로필 환경 변수 검사를 지나 CLI 드라이버까지 왔다는 뜻이다.
        Assert.Equal("session_required", CodeOf(await RunAsync(Create(api))));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task OpenAi_format_profile_is_pending_until_an_api_driver_is_registered()
    {
        Use(AiDriverKind.ClaudeCode, OpenAiProfile.Id, OpenAiProfile);

        Assert.Equal("api_driver_pending", CodeOf(await RunAsync(Create())));
    }

    [Fact]
    public async Task OpenAi_format_profile_goes_to_the_api_driver_with_the_request_and_the_resolved_route()
    {
        var final = new AiFinal("답", null, null);
        var api = new ScriptedApiDriver(new AiStarted("r1"), final);
        Use(AiDriverKind.ClaudeCode, OpenAiProfile.Id, OpenAiProfile);
        await _credentials.SaveTextAsync(AiProfileCredentials.KeyFor(OpenAiProfile.Id), "sk-test-key");

        var events = await RunAsync(Create(api));

        Assert.Equal([new AiStarted("r1"), final], events);
        Assert.Same(Request, Assert.Single(api.Requests));
        var route = Assert.Single(api.Routes);
        Assert.Equal(OpenAiProfile, route.Profile);
        Assert.Equal("sk-test-key", route.ApiKey);
    }

    [Fact]
    public async Task A_removed_active_profile_is_profile_missing()
    {
        Use(AiDriverKind.ClaudeCode, Guid.NewGuid(), OpenAiProfile);

        Assert.Equal("profile_missing", CodeOf(await RunAsync(Create())));
    }

    [Theory]
    [InlineData(AiProfilePreset.Unknown, AiApiFormat.OpenAi)]
    [InlineData(AiProfilePreset.Custom, AiApiFormat.Unknown)]
    public async Task A_profile_with_an_unknown_preset_or_format_is_closed_as_invalid(AiProfilePreset preset, AiApiFormat format)
    {
        var api = new ScriptedApiDriver(new AiFinal("x", null, null));
        var profile = OpenAiProfile with { Preset = preset, Format = format };
        Use(AiDriverKind.ClaudeCode, profile.Id, profile);

        Assert.Equal("profile_invalid", CodeOf(await RunAsync(Create(api))));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("llm.example:8080")]
    [InlineData("ftp://llm.example")]
    [InlineData("http://user:secret@llm.example:8080")]
    public async Task An_invalid_address_is_closed_before_any_driver_starts(string address)
    {
        var api = new ScriptedApiDriver(new AiFinal("x", null, null));
        var cliProfile = AnthropicProfile with { BaseUrl = address };
        var apiProfile = OpenAiProfile with { BaseUrl = address };

        Use(AiDriverKind.ClaudeCode, cliProfile.Id, cliProfile);
        Assert.Equal("profile_invalid_url", CodeOf(await RunAsync(Create(api))));

        Use(AiDriverKind.ClaudeCode, apiProfile.Id, apiProfile);
        Assert.Equal("profile_invalid_url", CodeOf(await RunAsync(Create(api))));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_preset_that_needs_a_key_is_closed_without_one_and_passes_with_one()
    {
        var api = new ScriptedApiDriver(new AiFinal("x", null, null));
        var profile = OpenAiProfile with { Preset = AiProfilePreset.OpenAi };
        Use(AiDriverKind.ClaudeCode, profile.Id, profile);

        Assert.Equal("profile_key_missing", CodeOf(await RunAsync(Create(api))));
        Assert.Empty(api.Requests);

        await _credentials.SaveTextAsync(AiProfileCredentials.KeyFor(profile.Id), "  sk-test-key \n");
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(Create(api))));
        Assert.Equal("sk-test-key", Assert.Single(api.Routes).ApiKey);
    }

    [Fact]
    public async Task A_preset_without_a_key_requirement_runs_with_no_key()
    {
        var api = new ScriptedApiDriver(new AiFinal("x", null, null));
        var profile = OpenAiProfile with { Preset = AiProfilePreset.Ollama };
        Use(AiDriverKind.ClaudeCode, profile.Id, profile);

        await RunAsync(Create(api));

        Assert.Null(Assert.Single(api.Routes).ApiKey);
    }

    [Fact]
    public async Task An_unreadable_key_store_is_reported_without_echoing_its_message()
    {
        var profile = OpenAiProfile with { Preset = AiProfilePreset.OpenAi };
        Use(AiDriverKind.ClaudeCode, profile.Id, profile);

        var events = await RunAsync(Create(credentials: new ThrowingCredentialStore()));

        var error = Assert.IsType<AiError>(Assert.Single(events));
        Assert.Equal("profile_key_unreadable", error.Code);
        Assert.DoesNotContain("sk-leaked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preset_without_a_key_requirement_runs_with_no_key_even_when_the_key_store_throws()
    {
        var api = new ScriptedApiDriver(new AiFinal("x", null, null));
        var profile = OpenAiProfile with { Preset = AiProfilePreset.Ollama };
        Use(AiDriverKind.ClaudeCode, profile.Id, profile);

        var events = await RunAsync(Create(api, new ThrowingCredentialStore()));

        Assert.IsType<AiFinal>(Assert.Single(events));
        Assert.Null(Assert.Single(api.Routes).ApiKey);
    }

    [Fact]
    public async Task A_settings_reset_signal_survives_a_restart_through_the_marker_file_and_is_taken_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new AppDataPaths(dir);
            await File.WriteAllTextAsync(paths.SettingsFile, "{ not json");

            using (var first = new SettingsStore(paths, new Oranda.Testing.FakeEventBus(), []))
            {
                await first.LoadAsync(CancellationToken.None);
            }

            using var restarted = new SettingsStore(paths, new Oranda.Testing.FakeEventBus(), []);
            Assert.True(restarted.TryTakeReset());
            Assert.False(restarted.TryTakeReset());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_first_run_after_a_settings_reset_is_refused_once()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(AiDriverKind.ClaudeCode, OpenAiProfile.Id, OpenAiProfile);
        _settings.ResetPending = true;
        var driver = Create(api);

        Assert.Equal("settings_reset", CodeOf(await RunAsync(driver)));
        Assert.Empty(api.Requests);

        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver)));
    }

    [Fact]
    public async Task A_read_only_settings_file_is_not_a_reset_and_never_blocks()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(AiDriverKind.ClaudeCode, OpenAiProfile.Id, OpenAiProfile);
        _settings.IsReadOnly = true;
        var driver = Create(api);

        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver)));
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver)));
    }

    [Fact]
    public async Task A_request_expecting_a_different_profile_is_refused_before_anything_is_sent()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(AiDriverKind.ClaudeCode, OpenAiProfile.Id, OpenAiProfile, AnthropicProfile);
        var driver = Create(api);

        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(AnthropicProfile.Id) })));
        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(null) })));
        Assert.Empty(api.Requests);

        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(OpenAiProfile.Id) })));
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver, Request)));
    }

    [Fact]
    public async Task A_request_expecting_another_address_or_format_for_the_same_profile_is_refused()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(AiDriverKind.ClaudeCode, OpenAiProfile.Id, OpenAiProfile);
        var driver = Create(api);

        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(OpenAiProfile.Id, "https://llm.example") })));
        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(OpenAiProfile.Id, Format: AiApiFormat.Anthropic) })));
        Assert.Empty(api.Requests);

        var same = new AiProfileExpectation(OpenAiProfile.Id, " http://llm.example:8080 ", AiApiFormat.OpenAi);
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver, Request with { Expected = same })));
    }

    [Fact]
    public async Task The_checks_run_in_a_fixed_order_reset_then_none_then_changed_then_profile_state()
    {
        var api = new ScriptedApiDriver();
        var driver = Create(api);

        Use(AiDriverKind.None, OpenAiProfile.Id, OpenAiProfile);
        _settings.ResetPending = true;
        Assert.Equal("settings_reset", CodeOf(await RunAsync(driver)));
        Assert.Equal("driver_none", CodeOf(await RunAsync(driver)));

        var invalid = OpenAiProfile with { Format = AiApiFormat.Unknown };
        Use(AiDriverKind.ClaudeCode, invalid.Id, invalid);
        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(Guid.NewGuid()) })));
        Assert.Equal("profile_invalid", CodeOf(await RunAsync(driver)));
    }

    [Fact]
    public async Task A_key_requiring_preset_on_the_cli_path_is_closed_without_a_key()
    {
        var profile = AnthropicProfile with { Preset = AiProfilePreset.ZAi };
        Use(AiDriverKind.ClaudeCode, profile.Id, profile);

        Assert.Equal("profile_key_missing", CodeOf(await RunAsync(Create())));
    }

    [Fact]
    public async Task A_request_expecting_claude_code_matches_when_no_profile_is_active()
    {
        Use(AiDriverKind.ClaudeCode, null, OpenAiProfile);

        var events = await RunAsync(Create(), Request with { Expected = new AiProfileExpectation(null) });

        Assert.Equal("session_required", CodeOf(events));
    }

    [Fact]
    public async Task Settings_are_read_on_every_run_so_a_profile_change_takes_effect_at_once()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        var driver = Create(api);

        Use(AiDriverKind.ClaudeCode, AnthropicProfile.Id, AnthropicProfile, OpenAiProfile);
        Assert.Equal("session_required", CodeOf(await RunAsync(driver)));

        Use(AiDriverKind.ClaudeCode, OpenAiProfile.Id, AnthropicProfile, OpenAiProfile);
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver)));

        Assert.Equal(2, _settings.LoadCount);
    }

    [Fact]
    public async Task Unreadable_settings_end_with_an_error()
    {
        _settings.LoadFailure = new IOException("disk");

        Assert.Equal("settings_unreadable", CodeOf(await RunAsync(Create())));
    }

    [Fact]
    public void Capabilities_are_fixed_and_always_allow_images() =>
        Assert.True(Create().Capabilities.Images);

    [Fact]
    public void The_route_text_never_shows_the_api_key() =>
        Assert.DoesNotContain("sk-secret", new AiRoute(OpenAiProfile, "sk-secret").ToString(), StringComparison.Ordinal);

    [Fact]
    public void The_router_is_the_only_registered_ai_driver()
    {
        var registered = typeof(PlatformAssembly).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<OrandaServiceAttribute>().Select(a => (Type: t, a.ServiceType)))
            .ToList();

        Assert.Equal(typeof(RoutingAiDriver), Assert.Single(registered, r => r.ServiceType == typeof(IAiDriver)).Type);
        Assert.Contains(registered, r => r.ServiceType == typeof(CliDriver) && r.Type == typeof(CliDriver));
    }

    private sealed class Services(IApiAiDriver? api) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IApiAiDriver) ? api : null;
    }

    private sealed class ThrowingCredentialStore : ICredentialStore
    {
        public Task SaveAsync(string key, ReadOnlyMemory<byte> secret) => Task.CompletedTask;

        public Task<byte[]?> LoadAsync(string key) => throw new IOException("keychain said sk-leaked");

        public Task DeleteAsync(string key) => Task.CompletedTask;
    }
}

/// <summary>
/// 시스템 CLI(Claude Code 외) 경로. 사용할 수 없는 CLI 는 Claude 구독(CliDriver)으로 넘어가지 않고 닫히며,
/// 사용 가능한 CLI 는 등록된 드라이버로만 간다(모델·노력은 비워서). 플랫폼은 주입한 목록으로 양쪽 분기를 지난다.
/// </summary>
public sealed class RoutingAiDriverSystemCliTests
{
    private static readonly AiRequest Request = new("질문", new AiContext("p", null, AiAccessLevel.Plan), Model: "opus", Effort: "high");

    private readonly FakeSettingsStore _settings = new();

    private RoutingAiDriver Create(IReadOnlyList<AiSystemCliInfo> clis, params ISystemCliAiDriver[] drivers)
    {
        var cli = new CliDriver(new FakeRunTokens(), new FakeGate(), new FixedBridgeEndpoint("pipe"), new AppDataPaths(Path.GetTempPath()), _settings);
        return new RoutingAiDriver(_settings, new FakeCredentialStore(), cli, new CliServices(drivers)) { SystemClis = clis };
    }

    private void Use(string? cliId, Guid? profileId = null) =>
        _settings.Current = new AppSettings { Ai = new AiSettings { SystemCliId = cliId, ActiveProfileId = profileId } };

    private static async Task<List<AiEvent>> RunAsync(RoutingAiDriver driver, AiRequest? request = null)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request ?? Request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private static string? CodeOf(IReadOnlyList<AiEvent> events) => Assert.IsType<AiError>(Assert.Single(events)).Code;

    private static IReadOnlyList<AiSystemCliInfo> Clis(bool codexAvailable) =>
    [
        new(AiProfilePresets.ClaudeCodeId, "Claude Code", true),
        new(AiProfilePresets.CodexCliId, "Codex CLI", codexAvailable),
    ];

    [Theory]
    [InlineData(AiProfilePresets.CodexCliId)]
    [InlineData("unknown-cli")]
    [InlineData(AiSettings.UnreadableSystemCliId)]
    public async Task An_unavailable_or_unknown_cli_closes_and_never_reaches_the_claude_driver(string id)
    {
        var fake = new FakeSystemCliDriver(AiProfilePresets.CodexCliId);
        Use(id);

        var code = CodeOf(await RunAsync(Create(Clis(codexAvailable: false), fake)));

        // session_required 였다면 CliDriver 가 불린 것이다.
        Assert.Equal("system_cli_unavailable", code);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task The_default_table_keeps_codex_and_antigravity_closed_on_a_hand_edited_setting()
    {
        var cli = new CliDriver(new FakeRunTokens(), new FakeGate(), new FixedBridgeEndpoint("pipe"), new AppDataPaths(Path.GetTempPath()), _settings);
        var driver = new RoutingAiDriver(_settings, new FakeCredentialStore(), cli, new CliServices());

        foreach (var id in new[] { AiProfilePresets.CodexCliId, AiProfilePresets.AntigravityCliId })
        {
            Use(id);
            Assert.Equal("system_cli_unavailable", CodeOf(await RunAsync(driver)));
        }
    }

    [Fact]
    public async Task An_available_cli_without_a_registered_driver_closes()
    {
        Use(AiProfilePresets.CodexCliId);

        Assert.Equal("system_cli_unavailable", CodeOf(await RunAsync(Create(Clis(codexAvailable: true)))));
        Assert.Equal("system_cli_unavailable", CodeOf(await RunAsync(Create(Clis(codexAvailable: true), new FakeSystemCliDriver("other-cli")))));
    }

    [Fact]
    public async Task An_available_cli_with_a_driver_runs_it_without_model_or_effort()
    {
        var fake = new FakeSystemCliDriver(AiProfilePresets.CodexCliId);
        Use(AiProfilePresets.CodexCliId);

        var events = await RunAsync(Create(Clis(codexAvailable: true), fake));

        Assert.IsType<AiFinal>(Assert.Single(events));
        var seen = Assert.Single(fake.Requests);
        Assert.Null(seen.Model);
        Assert.Null(seen.Effort);
        Assert.Equal("질문", seen.Prompt);
    }

    [Fact]
    public async Task Every_platform_branch_runs_codex()
    {
        Use(AiProfilePresets.CodexCliId);

        foreach (var platform in new[] { AiHostPlatform.MacOS, AiHostPlatform.Windows, AiHostPlatform.Linux })
        {
            var fake = new FakeSystemCliDriver(AiProfilePresets.CodexCliId);
            Assert.IsType<AiFinal>(Assert.Single(await RunAsync(Create(AiProfilePresets.SystemClisFor(platform), fake))));
            Assert.Single(fake.Requests);
        }
    }

    private static AgentSession SessionOn(ConnectionEnvironment environment) =>
        new(new AgentRunScope("r", new ConnectionProfile("p", "u", "h/s", Environment: environment), new AiContext("p", environment, AiAccessLevel.Query)));

    [Theory]
    [InlineData(AiProfilePresets.CodexCliId)]
    [InlineData(AiProfilePresets.AntigravityCliId)]
    public async Task A_system_cli_is_blocked_on_a_production_session_and_starts_nothing(string cliId)
    {
        Use(cliId);
        var fake = new FakeSystemCliDriver(cliId);

        var code = CodeOf(await RunAsync(Create(AiProfilePresets.SystemClisFor(AiHostPlatform.Windows), fake), Request with { Session = SessionOn(ConnectionEnvironment.Production) }));

        Assert.Equal(RoutingAiDriver.SystemCliProductionBlockedCode, code);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData(ConnectionEnvironment.Development)]
    [InlineData(ConnectionEnvironment.Test)]
    public async Task A_system_cli_runs_on_a_non_production_session(ConnectionEnvironment environment)
    {
        Use(AiProfilePresets.CodexCliId);
        var fake = new FakeSystemCliDriver(AiProfilePresets.CodexCliId);

        var events = await RunAsync(Create(AiProfilePresets.SystemClisFor(AiHostPlatform.Windows), fake), Request with { Session = SessionOn(environment) });

        Assert.IsType<AiFinal>(Assert.Single(events));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task An_available_antigravity_row_with_a_driver_runs_it_without_model_or_effort()
    {
        Use(AiProfilePresets.AntigravityCliId);
        var request = Request with { Model = "opus", Effort = "high" };
        IReadOnlyList<AiSystemCliInfo> clis =
        [
            new(AiProfilePresets.ClaudeCodeId, "Claude Code", true),
            new(AiProfilePresets.AntigravityCliId, "Antigravity CLI", true),
        ];

        var fake = new FakeSystemCliDriver(AiProfilePresets.AntigravityCliId);
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(Create(clis, fake), request)));
        var seen = Assert.Single(fake.Requests);
        Assert.Null(seen.Model);
        Assert.Null(seen.Effort);
    }

    /// <summary>「준비 중」 줄(<c>Available = false</c>)은 실제 드라이버를 등록해도 <c>system_cli_unavailable</c> 로 닫히고 CLI 프로세스는 한 건도 시작되지 않는다.</summary>
    [Theory]
    [InlineData(AiProfilePresets.CodexCliId)]
    [InlineData(AiProfilePresets.AntigravityCliId)]
    public async Task A_stored_id_of_a_coming_soon_row_closes_and_starts_no_process(string cliId)
    {
        var launcher = new Cli.FakeLauncher();
        var tokens = new FakeRunTokens();
        var gate = new FakeGate();
        var endpoint = new Cli.FixedEndpoint("bridge-pipe");
        var paths = new AppDataPaths(Path.GetTempPath());
        var codex = new CodexDriver(
            tokens, gate, endpoint, paths, launcher, "/fake/orandactl", () => "/fake/bin", _ => true,
            (_, _, _) => Task.FromResult<(string? Output, string? Error)>((null, "none")), _ => null, "/home/x", TimeSpan.FromMinutes(1),
            (_, _, _) => Task.FromResult<string?>("codex-cli 0.166.0"));
        var agy = new AgyDriver(
            tokens, gate, endpoint, paths, launcher, "/fake/orandactl", () => "/fake/bin", _ => true, "/home/x", TimeSpan.FromMinutes(1),
            (_, _, _) => Task.FromResult<string?>("1.4.12"));
        Use(cliId);

        IReadOnlyList<AiSystemCliInfo> clis =
        [
            new(AiProfilePresets.ClaudeCodeId, "Claude Code", true),
            new(AiProfilePresets.AntigravityCliId, "Antigravity CLI", false),
            new(AiProfilePresets.CodexCliId, "Codex CLI", false),
        ];
        var code = CodeOf(await RunAsync(Create(clis, codex, agy)));

        Assert.Equal("system_cli_unavailable", code);
        Assert.Empty(launcher.Launches);
        Assert.Empty(tokens.Issued);
    }

    [Fact]
    public async Task An_expectation_of_a_different_cli_than_the_setting_is_profile_changed()
    {
        var fake = new FakeSystemCliDriver(AiProfilePresets.CodexCliId);
        var driver = Create(Clis(codexAvailable: true), fake);

        Use(AiProfilePresets.CodexCliId);
        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(null) })));
        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(null, SystemCliId: "other-cli") })));
        Assert.Empty(fake.Requests);
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(null, SystemCliId: AiProfilePresets.CodexCliId) })));

        // 설정이 Claude Code 로 돌아가면 Codex 를 기대한 요청은 거부된다.
        Use(null);
        Assert.Equal("profile_changed", CodeOf(await RunAsync(driver, Request with { Expected = new AiProfileExpectation(null, SystemCliId: AiProfilePresets.CodexCliId) })));
    }

    [Fact]
    public async Task Claude_code_ids_null_and_claude_code_are_the_same_expectation()
    {
        Use(AiProfilePresets.ClaudeCodeId);

        var code = CodeOf(await RunAsync(Create(Clis(codexAvailable: true)), Request with { Expected = new AiProfileExpectation(null) }));

        Assert.Equal("session_required", code);
    }

    private sealed class FakeSystemCliDriver(string cliId) : ISystemCliAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        public string CliId => cliId;

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new AiFinal("답", null, null);
        }
    }

    private sealed class CliServices(params ISystemCliAiDriver[] drivers) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<ISystemCliAiDriver>) ? drivers : null;
    }
}

/// <summary>활성 대상이 Codex CLI 일 때(주입한 표에서 사용 가능으로 켠 경우) 라우터가 실제 Codex 드라이버까지 보내고 Claude 전용 값을 비우는지 확인한다.</summary>
public sealed class RoutingToCodexDriverTests : IDisposable
{
    // Windows 는 이름.exe 를 찾으므로(SystemCliExecutable) 대역 실행 파일 이름도 맞춘다.
    private static readonly string FakeCodex = Path.Combine("/fake/bin", OperatingSystem.IsWindows() ? "codex.exe" : "codex");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-route-codex-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSettingsStore _settings = new();
    private readonly Oranda.Platform.Tests.Ai.Cli.FakeLauncher _launcher = new();

    public RoutingToCodexDriverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [Fact]
    public async Task Codex_cli_setting_reaches_the_codex_driver_with_no_model_effort_or_claude_path()
    {
        var tokens = new FakeRunTokens();
        var gate = new FakeGate();
        var codex = new CodexDriver(
            tokens,
            gate,
            new FixedBridgeEndpoint("pipe"),
            new AppDataPaths(_dir),
            _launcher,
            "/fake/orandactl",
            () => "/fake/bin",
            path => path == FakeCodex || path == "/fake/orandactl",
            (_, _, _) => Task.FromResult<(string?, string?)>((
                """{"models":[{"tool_mode":"x","apply_patch_tool_type":"x","multi_agent_version":"x","experimental_supported_tools":[],"supports_search_tool":true}]}""", null)),
            _ => null,
            "/home/x",
            TimeSpan.FromMinutes(1),
            (_, _, _) => Task.FromResult<string?>("codex-cli 0.166.0"));
        var cli = new CliDriver(tokens, gate, new FixedBridgeEndpoint("pipe"), new AppDataPaths(_dir), _settings);
        var router = new RoutingAiDriver(_settings, new FakeCredentialStore(), cli, new CodexServices(codex))
        {
            SystemClis = [new(AiProfilePresets.ClaudeCodeId, "Claude Code", true), new(AiProfilePresets.CodexCliId, "Codex CLI", true)],
        };
        _settings.Current = new AppSettings
        {
            Ai = new AiSettings { SystemCliId = AiProfilePresets.CodexCliId, Model = "sonnet", Effort = "high", ExecutablePath = "/opt/claude" },
        };
        _launcher.Lines =
        [
            """{"type":"thread.started","thread_id":"th-1"}""",
            """{"type":"item.completed","item":{"id":"m","type":"agent_message","text":"ok"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""",
        ];
        var profile = new Oranda.Core.Connection.ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        var request = new AiRequest("질문", context, Model: "opus", Effort: "max", Session: new Oranda.Core.Ai.Gate.AgentSession(new Oranda.Core.Ai.Gate.AgentRunScope("run-1", profile, context)),
            Expected: new AiProfileExpectation(null, SystemCliId: AiProfilePresets.CodexCliId));

        var events = new List<AiEvent>();
        await foreach (var e in router.RunAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.IsType<AiFinal>(events[^1]);
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(FakeCodex, launch.ExecutablePath); // 설정의 Claude 경로(/opt/claude)가 아니다
        Assert.DoesNotContain("-m", launch.Arguments);
        Assert.DoesNotContain("opus", launch.Arguments);
        Assert.DoesNotContain("sonnet", launch.Arguments);
        Assert.DoesNotContain(launch.Arguments, a => a.Contains("effort", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CodexServices(CodexDriver codex) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<ISystemCliAiDriver>) ? new ISystemCliAiDriver[] { codex } : null;
    }
}
