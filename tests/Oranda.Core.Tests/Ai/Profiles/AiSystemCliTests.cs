using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>시스템 CLI 설정 칸·활성 대상 판단·플랫폼별 사용 가능 표.</summary>
public class AiSystemCliTests
{
    private static readonly AiLlmProfile Profile = new() { Id = Guid.NewGuid(), Name = "사내", BaseUrl = "http://llm.example:8080", Model = "m" };

    private static readonly IReadOnlyList<AiSystemCliInfo> Clis =
    [
        new(AiProfilePresets.ClaudeCodeId, "Claude Code", true),
        new("on-cli", "켜진 CLI", true, SupportsImages: false),
        new("off-cli", "꺼진 CLI", false),
    ];

    private static AiActiveProfile Find(string? cliId, Guid? profileId = null, AiDriverKind driver = AiDriverKind.ClaudeCode) =>
        AiActiveProfile.Find(new AiSettings { Driver = driver, SystemCliId = cliId, ActiveProfileId = profileId, Profiles = [Profile] }, Clis);

    private static AppSettings Read(string json) => JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

    [Fact]
    public void Null_or_claude_code_id_is_claude_code()
    {
        Assert.Equal(AiActiveKind.ClaudeCode, Find(null).Kind);
        Assert.Equal(AiActiveKind.ClaudeCode, Find(AiProfilePresets.ClaudeCodeId).Kind);
    }

    [Fact]
    public void Available_cli_is_system_cli_with_its_row()
    {
        var found = Find("on-cli");

        Assert.Equal(AiActiveKind.SystemCli, found.Kind);
        Assert.Equal("on-cli", found.SystemCliId);
        Assert.False(found.SystemCli!.SupportsImages);
    }

    [Theory]
    [InlineData("off-cli")]
    [InlineData("no-such-cli")]
    [InlineData("")]
    [InlineData(AiSettings.UnreadableSystemCliId)]
    public void Unavailable_or_unknown_cli_is_unavailable_never_claude_code(string id) =>
        Assert.Equal(AiActiveKind.SystemCliUnavailable, Find(id).Kind);

    [Fact]
    public void Profile_id_wins_over_the_cli_id() =>
        Assert.Equal(AiActiveKind.Profile, Find("on-cli", Profile.Id).Kind);

    [Fact]
    public void Driver_none_wins_over_the_cli_id() =>
        Assert.Equal(AiActiveKind.None, Find("on-cli", driver: AiDriverKind.None).Kind);

    [Fact]
    public void Default_list_find_closes_codex_and_antigravity_on_every_platform()
    {
        foreach (var platform in new[] { AiHostPlatform.MacOS, AiHostPlatform.Windows, AiHostPlatform.Linux })
        {
            var clis = AiProfilePresets.SystemClisFor(platform);
            Assert.Equal(AiActiveKind.SystemCliUnavailable, AiActiveProfile.Find(new AiSettings { SystemCliId = AiProfilePresets.CodexCliId }, clis).Kind);
            Assert.Equal(AiActiveKind.SystemCliUnavailable, AiActiveProfile.Find(new AiSettings { SystemCliId = AiProfilePresets.AntigravityCliId }, clis).Kind);
        }
    }

    [Theory]
    [InlineData(AiHostPlatform.MacOS)]
    [InlineData(AiHostPlatform.Windows)]
    [InlineData(AiHostPlatform.Linux)]
    [InlineData(AiHostPlatform.None)]
    public void Codex_and_antigravity_are_not_enabled_on_any_platform(AiHostPlatform platform)
    {
        var clis = AiProfilePresets.SystemClisFor(platform);

        Assert.False(clis.Single(c => c.Id == AiProfilePresets.CodexCliId).Available);
        Assert.False(clis.Single(c => c.Id == AiProfilePresets.AntigravityCliId).Available);
        Assert.Equal(platform != AiHostPlatform.None, clis.Single(c => c.Id == AiProfilePresets.ClaudeCodeId).Available);
    }

    [Fact]
    public void Image_support_flags_follow_the_spec()
    {
        var clis = AiProfilePresets.SystemClisFor(AiHostPlatform.MacOS);

        Assert.True(clis.Single(c => c.Id == AiProfilePresets.ClaudeCodeId).SupportsImages);
        Assert.True(clis.Single(c => c.Id == AiProfilePresets.CodexCliId).SupportsImages);
        Assert.False(clis.Single(c => c.Id == AiProfilePresets.AntigravityCliId).SupportsImages);
    }

    [Fact]
    public void Old_settings_file_without_the_field_is_claude_code()
    {
        var ai = Read("""{"ai":{"driver":"ClaudeCode"}}""").Ai;

        Assert.Null(ai.SystemCliId);
        Assert.Equal(AiActiveKind.ClaudeCode, AiActiveProfile.Find(ai).Kind);
    }

    [Fact]
    public void System_cli_id_round_trips()
    {
        var json = JsonSerializer.Serialize(new AppSettings { Ai = new AiSettings { SystemCliId = "codex-cli" } }, OrandaJsonContext.Default.AppSettings);

        Assert.Equal("codex-cli", Read(json).Ai.SystemCliId);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("""{"a":1}""")]
    [InlineData("[1,2]")]
    public void Unreadable_value_reads_as_the_unreadable_id_without_resetting_settings(string value)
    {
        var settings = Read($$$"""{"ai":{"driver":"ClaudeCode","model":"opus","systemCliId":{{{value}}}}}""");

        Assert.Equal(AiSettings.UnreadableSystemCliId, settings.Ai.SystemCliId);
        Assert.Equal("opus", settings.Ai.Model);
        Assert.Equal(AiActiveKind.SystemCliUnavailable, AiActiveProfile.Find(settings.Ai).Kind);
    }

    [Fact]
    public void Null_value_reads_as_null() =>
        Assert.Null(Read("""{"ai":{"systemCliId":null}}""").Ai.SystemCliId);

    [Fact]
    public void Equality_includes_the_cli_id()
    {
        var a = new AiSettings { SystemCliId = "codex-cli" };

        Assert.Equal(a, new AiSettings { SystemCliId = "codex-cli" });
        Assert.NotEqual(a, new AiSettings());
        Assert.NotEqual(a, new AiSettings { SystemCliId = "other" });
    }

    [Fact]
    public void Expectation_defaults_keep_existing_call_sites()
    {
        var expected = new AiProfileExpectation(null);

        Assert.Null(expected.SystemCliId);
    }
}
