using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>가벼운 일 모델 칸(<see cref="AiSettings.Light"/>)의 읽기·쓰기 내성과 등급별 대상 고르기(설계 §1.2).</summary>
public class AiLightTierTests
{
    private static readonly AiLlmProfile Main = new() { Id = Guid.NewGuid(), Name = "사내", BaseUrl = "https://llm.example", Model = "big" };

    private static readonly AiLlmProfile Local = new() { Id = Guid.NewGuid(), Name = "Ollama", Preset = AiProfilePreset.Ollama, BaseUrl = "http://127.0.0.1:11434", Model = "qwen3:8b" };

    private static AppSettings Read(string json) => JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

    [Fact]
    public void A_file_without_the_key_has_no_light_model() =>
        Assert.Null(Read("""{"ai":{"driver":"ClaudeCode"}}""").Ai.Light);

    [Fact]
    public void Light_round_trips_through_the_settings_file()
    {
        var light = new AiLightSettings { ProfileId = Local.Id, SystemCliId = "codex-cli", Model = "haiku", Effort = "low" };
        var json = JsonSerializer.Serialize(new AppSettings { Ai = new AiSettings { Light = light } }, OrandaJsonContext.Default.AppSettings);

        Assert.Equal(light, Read(json).Ai.Light);
    }

    [Theory]
    [InlineData("""{"ai":{"light":null,"joinSyntax":"ansi"}}""")]
    [InlineData("""{"ai":{"light":5,"joinSyntax":"ansi"}}""")]
    [InlineData("""{"ai":{"light":"haiku","joinSyntax":"ansi"}}""")]
    [InlineData("""{"ai":{"light":[1,{"model":"x"}],"joinSyntax":"ansi"}}""")]
    public void An_unreadable_light_value_is_same_as_main_and_the_file_survives(string json)
    {
        var ai = Read(json).Ai;

        Assert.Null(ai.Light);
        Assert.Equal(AiJoinSyntax.Ansi, ai.JoinSyntax);
    }

    [Fact]
    public void Broken_light_fields_never_turn_into_claude_code()
    {
        var ai = Read("""{"ai":{"driver":"ClaudeCode","light":{"profileId":"not-a-guid","systemCliId":7,"model":{"a":1},"effort":true,"extra":[1]},"joinSyntax":"ansi"}}""").Ai;

        Assert.Equal(new AiLightSettings { ProfileId = Guid.Empty, SystemCliId = AiSettings.UnreadableSystemCliId }, ai.Light);
        Assert.Equal(AiActiveKind.ProfileMissing, AiActiveProfile.Find(ai, AiTier.Light).Kind);
        Assert.Equal(AiJoinSyntax.Ansi, ai.JoinSyntax);
    }

    [Fact]
    public void Settings_with_different_light_models_are_not_equal()
    {
        var a = new AiSettings { Light = new AiLightSettings { Model = "haiku" } };

        Assert.Equal(a, new AiSettings { Light = new AiLightSettings { Model = "haiku" } });
        Assert.NotEqual(a, new AiSettings { Light = new AiLightSettings { Model = "sonnet" } });
        Assert.NotEqual(a, new AiSettings());
    }

    [Fact]
    public void Without_light_both_tiers_pick_the_main_target()
    {
        var settings = new AiSettings { Profiles = [Main, Local], ActiveProfileId = Main.Id, Light = new AiLightSettings { Effort = "low" } };

        Assert.Same(settings, settings.ForTier(AiTier.Light));
        Assert.Equal(AiActiveProfile.Find(settings), AiActiveProfile.Find(settings, AiTier.Light));
        Assert.Same(Main, AiActiveProfile.Find(settings, AiTier.Light).Profile);
    }

    [Fact]
    public void Light_profile_is_picked_only_for_light_requests()
    {
        var settings = new AiSettings { Profiles = [Main, Local], ActiveProfileId = Main.Id, Light = new AiLightSettings { ProfileId = Local.Id } };

        Assert.Same(Main, AiActiveProfile.Find(settings, AiTier.Main).Profile);
        Assert.Same(Local, AiActiveProfile.Find(settings, AiTier.Light).Profile);
        Assert.Same(Main, AiActiveProfile.Find(settings).Profile);
    }

    [Fact]
    public void Light_claude_code_model_replaces_the_main_model_and_effort()
    {
        var settings = new AiSettings { Model = "opus", Effort = "high", ExecutablePath = "/bin/claude", Profiles = [Main], ActiveProfileId = Main.Id, Light = new AiLightSettings { Model = "haiku" } };

        var light = settings.ForTier(AiTier.Light);

        Assert.Equal(AiActiveKind.ClaudeCode, AiActiveProfile.Find(settings, AiTier.Light).Kind);
        Assert.Equal((null as Guid?, null as string, "haiku", null as string, "/bin/claude", null as AiLightSettings), (light.ActiveProfileId, light.SystemCliId, light.Model, light.Effort, light.ExecutablePath, light.Light));
        Assert.Equal(settings.Profiles, light.Profiles);
    }

    [Fact]
    public void An_unavailable_light_cli_stays_unavailable_and_does_not_fall_back_to_main()
    {
        var settings = new AiSettings { Light = new AiLightSettings { SystemCliId = "no-such-cli" } };

        Assert.Equal(AiActiveKind.SystemCliUnavailable, AiActiveProfile.Find(settings, AiTier.Light).Kind);
        Assert.Equal(AiActiveKind.ClaudeCode, AiActiveProfile.Find(settings, AiTier.Main).Kind);
    }

    [Fact]
    public void Driver_none_is_none_for_the_light_tier_too() =>
        Assert.Equal(AiActiveKind.None, AiActiveProfile.Find(new AiSettings { Driver = AiDriverKind.None, Light = new AiLightSettings { Model = "haiku" } }, AiTier.Light).Kind);

    [Fact]
    public void Requests_are_main_unless_asked_and_commands_have_fixed_tiers()
    {
        Assert.Equal(AiTier.Main, new AiRequest("p", new AiContext(null, null, AiAccessLevel.Off)).Tier);
        Assert.Equal(
            (AiTier.Main, AiTier.Main, AiTier.Light, AiTier.Light, AiTier.Main),
            (AiCommandTiers.Tune, AiCommandTiers.ExplainPlan, AiCommandTiers.ExplainSql, AiCommandTiers.FixError, AiCommandTiers.General));
    }

    [Fact]
    public void Model_chosen_event_tells_when_a_light_request_went_to_main()
    {
        Assert.True(new AiModelChosen("opus", AiTier.Main, AiTier.Light).Escalated);
        Assert.False(new AiModelChosen("haiku", AiTier.Light, AiTier.Light).Escalated);
        Assert.False(new AiModelChosen("opus", AiTier.Main).Escalated);
        Assert.False(new AiError("x").RetryOnMain);
    }
}
