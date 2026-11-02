using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>활성 프로필 찾기와 probe 요청의 키 비노출.</summary>
public class AiActiveProfileTests
{
    private static readonly AiLlmProfile Profile = new() { Id = Guid.NewGuid(), Name = "사내", BaseUrl = "http://llm.example:8080", Model = "m" };

    [Fact]
    public void Driver_none_is_none_even_with_an_active_profile_id() =>
        Assert.Equal(AiActiveKind.None, AiActiveProfile.Find(new AiSettings { Driver = AiDriverKind.None, Profiles = [Profile], ActiveProfileId = Profile.Id }).Kind);

    [Fact]
    public void Claude_code_without_profile_id_is_the_system_cli() =>
        Assert.Equal(AiActiveKind.ClaudeCode, AiActiveProfile.Find(new AiSettings { Profiles = [Profile] }).Kind);

    [Fact]
    public void A_listed_profile_id_selects_the_profile()
    {
        var found = AiActiveProfile.Find(new AiSettings { Profiles = [Profile], ActiveProfileId = Profile.Id });

        Assert.Equal(AiActiveKind.Profile, found.Kind);
        Assert.Same(Profile, found.Profile);
    }

    [Fact]
    public void An_unlisted_profile_id_is_profile_missing()
    {
        var missing = Guid.NewGuid();

        var found = AiActiveProfile.Find(new AiSettings { Profiles = [Profile], ActiveProfileId = missing });

        Assert.Equal((AiActiveKind.ProfileMissing, missing), (found.Kind, found.MissingId));
        Assert.Equal("profile_missing", AiActiveProfile.ProfileMissingCode);
    }

    [Theory]
    [InlineData(AiProfilePreset.Unknown, AiApiFormat.OpenAi)]
    [InlineData(AiProfilePreset.Custom, AiApiFormat.Unknown)]
    public void A_profile_with_an_unknown_preset_or_format_is_invalid(AiProfilePreset preset, AiApiFormat format)
    {
        var profile = Profile with { Preset = preset, Format = format };

        var found = AiActiveProfile.Find(new AiSettings { Profiles = [profile], ActiveProfileId = profile.Id });

        Assert.Equal(AiActiveKind.ProfileInvalid, found.Kind);
        Assert.Equal("profile_invalid", AiActiveProfile.ProfileInvalidCode);
    }

    [Fact]
    public void The_empty_id_of_a_broken_setting_never_matches_a_profile_that_lacks_an_id()
    {
        var withoutId = Profile with { Id = Guid.Empty };

        var found = AiActiveProfile.Find(new AiSettings { Profiles = [withoutId], ActiveProfileId = Guid.Empty });

        Assert.Equal(AiActiveKind.ProfileMissing, found.Kind);
    }

    [Fact]
    public void Probe_request_text_never_shows_the_api_key() =>
        Assert.DoesNotContain("sk-secret", new AiProfileProbeRequest(Profile, "sk-secret").ToString(), StringComparison.Ordinal);
}
