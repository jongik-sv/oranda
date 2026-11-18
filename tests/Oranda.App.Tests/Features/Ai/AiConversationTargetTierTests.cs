using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>확인 창·가림·라우터 기대값이 실제로 고른 등급의 대상으로 계산되는지(설계 §1.2 「확인·가림」).</summary>
public class AiConversationTargetTierTests
{
    private static readonly AiLlmProfile Paid = new()
    {
        Id = Guid.NewGuid(), Name = "사내 유료", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "https://llm.example/v1", Model = "big",
    };

    private static readonly AiLlmProfile Local = new()
    {
        Id = Guid.NewGuid(), Name = "Ollama", Preset = AiProfilePreset.Ollama, Format = AiApiFormat.OpenAi, BaseUrl = "http://127.0.0.1:11434/v1", Model = "qwen3:8b",
        MaxInputTokens = 8192, LocalAiConfirmedUrl = "http://127.0.0.1:11434/v1",
    };

    private static readonly AiSettings Settings = new() { Profiles = [Paid, Local], ActiveProfileId = Paid.Id, Light = new AiLightSettings { ProfileId = Local.Id } };

    [Fact]
    public void A_local_light_model_is_not_masked_while_the_paid_main_model_is()
    {
        var light = AiConversationTarget.From(Settings, AiTier.Light);
        var main = AiConversationTarget.From(Settings, AiTier.Main);

        Assert.Equal((Local.Id, true, AiTier.Light), (light.ProfileId, light.LocalAi, light.Tier));
        Assert.Equal((Paid.Id, false, AiTier.Main), (main.ProfileId, main.LocalAi, main.Tier));
        Assert.Equal(Local.Id, light.ToExpectation().ProfileId);
        Assert.True(light.ToExpectation().LocalAi);
        Assert.False(light.IsSameAs(main));
    }

    [Fact]
    public void A_large_light_input_is_chosen_for_main_before_confirmation()
    {
        var small = AiConversationTarget.Choose(Settings, AiTier.Light, 5_000);
        var large = AiConversationTarget.Choose(Settings, AiTier.Light, 6_000);

        Assert.Equal((Local.Id, AiTier.Light, false), (small.ProfileId, small.Tier, small.Escalated));
        Assert.Equal((Paid.Id, AiTier.Main, AiTier.Light, true, false), (large.ProfileId, large.Tier, large.RequestedTier, large.Escalated, large.LocalAi));
    }

    [Fact]
    public void Without_a_light_model_both_tiers_are_the_old_main_target()
    {
        var settings = Settings with { Light = null };

        Assert.True(AiConversationTarget.From(settings).IsSameAs(AiConversationTarget.From(settings, AiTier.Main)));
        Assert.Equal(Paid.Id, AiConversationTarget.From(settings, AiTier.Light).ProfileId);
    }

    [Fact]
    public void A_light_claude_code_model_is_named_in_the_label()
    {
        var settings = new AiSettings { Model = "opus", Light = new AiLightSettings { Model = "haiku" } };

        Assert.Equal("Claude Code · haiku", AiConversationTarget.From(settings, AiTier.Light).Label);
        Assert.Equal("Claude Code", AiConversationTarget.From(settings, AiTier.Main).Label);
    }
}
