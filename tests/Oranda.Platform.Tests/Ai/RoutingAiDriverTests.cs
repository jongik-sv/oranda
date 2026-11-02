using System.Reflection;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Composition;
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
