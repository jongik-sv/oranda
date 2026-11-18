using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Ai;

/// <summary>등급 라우팅(설계 §1.2): 가벼운 일 모델 고르기·답한 모델 알림·기대값·노력 고정·실패 표시(AE-1)·감사용 세션 값.</summary>
public sealed class RoutingLightTierTests
{
    private static readonly AiLlmProfile MainProfile = new()
    {
        Id = Guid.NewGuid(), Name = "사내 유료", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "https://llm.example/v1", Model = "big",
    };

    private static readonly AiLlmProfile LocalProfile = new()
    {
        Id = Guid.NewGuid(), Name = "Ollama", Preset = AiProfilePreset.Ollama, Format = AiApiFormat.OpenAi, BaseUrl = "http://127.0.0.1:11434/v1", Model = "qwen3:8b",
        LocalAiConfirmedUrl = "http://127.0.0.1:11434/v1",
    };

    private static readonly AiRequest Light = new("설명", new AiContext("p", null, AiAccessLevel.Plan), Tier: AiTier.Light);

    private readonly FakeSettingsStore _settings = new();

    private RoutingAiDriver Create(IApiAiDriver api)
    {
        var cli = new CliDriver(new FakeRunTokens(), new FakeGate(), new FixedBridgeEndpoint("pipe"), new AppDataPaths(Path.GetTempPath()), _settings);
        return new RoutingAiDriver(_settings, new FakeCredentialStore(), cli, new ServiceBag().Add<IApiAiDriver>(api));
    }

    private void Use(AiLightSettings? light, Guid? main = null) =>
        _settings.Current = new AppSettings
        {
            Ai = new AiSettings { ActiveProfileId = main ?? MainProfile.Id, Profiles = [MainProfile, LocalProfile], Model = "opus", Effort = "high", Light = light },
        };

    private static async Task<List<AiEvent>> RunAsync(RoutingAiDriver driver, AiRequest request)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task A_light_request_goes_to_the_light_profile_and_announces_it_first()
    {
        var final = new AiFinal("답", null, null);
        var api = new ScriptedApiDriver(final);
        Use(new AiLightSettings { ProfileId = LocalProfile.Id });

        var events = await RunAsync(Create(api), Light);

        Assert.Equal([new AiModelChosen("Ollama · qwen3:8b", AiTier.Light, AiTier.Light), final], events);
        Assert.Equal(LocalProfile, Assert.Single(api.Routes).Profile);
        Assert.Equal("low", Assert.Single(api.Requests).Effort); // 가벼운 일의 노력은 low 고정
    }

    [Fact]
    public async Task Without_a_light_model_a_light_request_behaves_exactly_like_before()
    {
        var final = new AiFinal("답", null, null);
        var api = new ScriptedApiDriver(final);
        Use(null);

        Assert.Equal([final], await RunAsync(Create(api), Light));
        Assert.Equal(MainProfile, Assert.Single(api.Routes).Profile);
        Assert.Same(Light, Assert.Single(api.Requests));
    }

    [Fact]
    public async Task A_main_request_ignores_the_light_model()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(new AiLightSettings { ProfileId = LocalProfile.Id });

        var events = await RunAsync(Create(api), Light with { Tier = AiTier.Main });

        Assert.IsType<AiFinal>(Assert.Single(events));
        Assert.Equal(MainProfile, Assert.Single(api.Routes).Profile);
    }

    [Fact]
    public async Task An_escalated_request_goes_to_main_and_says_so()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(new AiLightSettings { ProfileId = LocalProfile.Id });

        var events = await RunAsync(Create(api), Light with { Tier = AiTier.Main, RequestedTier = AiTier.Light });

        var chosen = Assert.IsType<AiModelChosen>(events[0]);
        Assert.Equal(("사내 유료 · big", true), (chosen.Label, chosen.Escalated));
        Assert.Equal(MainProfile, Assert.Single(api.Routes).Profile);
    }

    [Fact]
    public async Task The_expectation_is_checked_against_the_light_target()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(new AiLightSettings { ProfileId = LocalProfile.Id });
        var driver = Create(api);

        var wrong = await RunAsync(driver, Light with { Expected = new AiProfileExpectation(MainProfile.Id) });
        var right = await RunAsync(driver, Light with { Expected = new AiProfileExpectation(LocalProfile.Id, LocalProfile.BaseUrl, AiApiFormat.OpenAi, LocalAi: true) });

        var error = Assert.IsType<AiError>(Assert.Single(wrong));
        Assert.Equal(("profile_changed", true), (error.Code, error.RetryOnMain));
        Assert.IsType<AiFinal>(right[^1]);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_missing_light_profile_fails_without_falling_back_to_main()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(new AiLightSettings { ProfileId = Guid.NewGuid() });

        var error = Assert.IsType<AiError>(Assert.Single(await RunAsync(Create(api), Light)));

        Assert.Equal(("profile_missing", true), (error.Code, error.RetryOnMain));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Driver_errors_on_the_light_model_offer_a_main_retry_but_main_errors_do_not()
    {
        var api = new ScriptedApiDriver(new AiError("서버 오류", "api_error"));
        Use(new AiLightSettings { ProfileId = LocalProfile.Id });
        var driver = Create(api);

        var light = Assert.IsType<AiError>((await RunAsync(driver, Light))[^1]);
        var main = Assert.IsType<AiError>((await RunAsync(driver, Light with { Tier = AiTier.Main }))[^1]);

        Assert.True(light.RetryOnMain);
        Assert.False(main.RetryOnMain);
        Assert.Equal(2, api.Requests.Count); // 실패해도 다른 모델로 다시 보내지 않았다
    }

    [Fact]
    public async Task The_session_carries_tier_and_model_for_the_audit_log()
    {
        var api = new ScriptedApiDriver(new AiFinal("답", null, null));
        Use(new AiLightSettings { ProfileId = LocalProfile.Id });
        var context = new AiContext("dev", null, AiAccessLevel.Plan);
        AgentSession NewSession() => new(new AgentRunScope("run-1", new ConnectionProfile("dev", "scott", "h/s"), context));
        var driver = Create(api);
        var light = NewSession();
        var main = NewSession();

        await RunAsync(driver, Light with { Session = light });
        await RunAsync(driver, Light with { Tier = AiTier.Main, Session = main });

        Assert.Equal((AiTier.Light, "Ollama · qwen3:8b"), (light.Tier, light.ModelLabel));
        Assert.Equal((AiTier.Main, "사내 유료 · big"), (main.Tier, main.ModelLabel));
    }
}
