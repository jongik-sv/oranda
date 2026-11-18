using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>크기 올림(AE-2)·입력 한도 표·토큰 추정·모델 이름.</summary>
public class AiTierRoutingTests
{
    private static readonly AiLlmProfile Local = new()
    {
        Id = Guid.NewGuid(), Name = "Ollama", Preset = AiProfilePreset.Ollama, Format = AiApiFormat.OpenAi, BaseUrl = "http://127.0.0.1:11434/v1", Model = "qwen3:8b", MaxInputTokens = 10_000,
    };

    private static AiSettings WithLight(AiLightSettings? light) => new() { Model = "opus", Profiles = [Local], Light = light };

    [Theory]
    [InlineData(7_000, AiTier.Light)]
    [InlineData(7_001, AiTier.Main)]
    public void A_light_request_above_70_percent_of_the_light_limit_goes_to_main(int tokens, AiTier expected)
    {
        var choice = AiTierRouting.Choose(WithLight(new AiLightSettings { ProfileId = Local.Id }), AiTier.Light, tokens);

        Assert.Equal((expected, 10_000, expected == AiTier.Main), (choice.Tier, choice.Limit, choice.Escalated));
        Assert.Equal(expected == AiTier.Light ? AiActiveKind.Profile : AiActiveKind.ClaudeCode, choice.Target.Kind);
    }

    [Fact]
    public void Without_a_light_model_nothing_is_escalated()
    {
        var choice = AiTierRouting.Choose(WithLight(null), AiTier.Light, 5_000_000);

        Assert.Equal((AiTier.Light, AiActiveKind.ClaudeCode, false, (int?)null), (choice.Tier, choice.Target.Kind, choice.Escalated, choice.Limit));
    }

    [Fact]
    public void Main_requests_are_never_changed() =>
        Assert.Equal(AiTier.Main, AiTierRouting.Choose(WithLight(new AiLightSettings { ProfileId = Local.Id }), AiTier.Main, 1).Tier);

    [Fact]
    public void An_unknown_limit_custom_cli_is_not_escalated()
    {
        var settings = WithLight(new AiLightSettings { SystemCliId = "custom:c1" }) with { CustomClis = [new AiCustomCli { Id = "c1", Name = "사내", Command = "x" }] };

        var choice = AiTierRouting.Choose(settings, AiTier.Light, 5_000_000);

        Assert.Equal((AiTier.Light, AiActiveKind.CustomCli, (int?)null), (choice.Tier, choice.Target.Kind, choice.Limit));
    }

    [Theory]
    [InlineData("haiku", 200_000)]
    [InlineData(null, 200_000)]
    [InlineData("opus[1m]", 1_000_000)]
    [InlineData("claude-sonnet-4-5[1M]", 1_000_000)]
    public void Claude_model_limits_are_known_by_the_app(string? model, int limit) =>
        Assert.Equal(limit, AiTierRouting.InputLimit(new AiActiveProfile(AiActiveKind.ClaudeCode), model));

    [Fact]
    public void Codex_and_profiles_use_their_limits()
    {
        var codex = new AiActiveProfile(AiActiveKind.SystemCli, SystemCliId: AiProfilePresets.CodexCliId, SystemCli: new AiSystemCliInfo(AiProfilePresets.CodexCliId, "Codex CLI", true));

        Assert.Equal(AiTierRouting.CodexInputTokens, AiTierRouting.InputLimit(codex, "opus"));
        Assert.Equal(10_000, AiTierRouting.InputLimit(new AiActiveProfile(AiActiveKind.Profile, Local), null));
        Assert.Null(AiTierRouting.InputLimit(new AiActiveProfile(AiActiveKind.ProfileMissing, MissingId: Guid.NewGuid()), null));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("SELECT", 2)]
    [InlineData("주문", 2)]
    [InlineData("SELECT 1 -- 주문", 6)]
    public void Token_estimate_counts_ascii_by_threes_and_other_letters_one_each(string? text, int tokens) =>
        Assert.Equal(tokens, AiTierRouting.EstimateTokens(text));

    [Fact]
    public void Model_labels_name_the_model_without_addresses()
    {
        Assert.Equal("haiku", AiTierRouting.ModelLabel(new AiActiveProfile(AiActiveKind.ClaudeCode), " haiku "));
        Assert.Equal("Claude Code", AiTierRouting.ModelLabel(new AiActiveProfile(AiActiveKind.ClaudeCode), null));
        Assert.Equal("Ollama · qwen3:8b", AiTierRouting.ModelLabel(new AiActiveProfile(AiActiveKind.Profile, Local), "opus"));
        Assert.Equal("Ollama", AiTierRouting.ModelLabel(new AiActiveProfile(AiActiveKind.Profile, Local with { Model = "" }), null));
        Assert.Equal("사내", AiTierRouting.ModelLabel(new AiActiveProfile(AiActiveKind.CustomCli, CustomCli: new AiCustomCli { Id = "c1", Name = "사내", Command = "x" }), null));
    }
}
