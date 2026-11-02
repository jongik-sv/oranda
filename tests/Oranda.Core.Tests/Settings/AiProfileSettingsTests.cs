using System.Text.Json;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Settings;

/// <summary>
/// ai.profiles·ai.activeProfileId: 새 칸이 없는 옛 파일은 그대로 읽히고, 새 앱이 쓴 파일은 새 칸을 모르는 옛 형식도 초기화 없이 읽는다.
/// <c>AiDriverKind</c> 에 값을 더하지 않으므로 프로필을 쓰는 파일도 옛 빌드에서는 Claude Code 로 읽힌다.
/// </summary>
public class AiProfileSettingsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly AiLlmProfile Sample = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "사내 서버",
        Preset = AiProfilePreset.Custom,
        Format = AiApiFormat.OpenAi,
        BaseUrl = "http://llm.example:8080",
        Model = "qwen-example",
        MaxInputTokens = 128000,
        MaxOutputTokens = 4096,
        SupportsImages = true,
    };

    [Fact]
    public void Profiles_survive_a_save_and_load_round_trip()
    {
        var settings = new AppSettings { Ai = new AiSettings { Profiles = [Sample], ActiveProfileId = Sample.Id } };

        var json = JsonSerializer.Serialize(settings, OrandaJsonContext.Default.AppSettings);
        var loaded = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai;

        Assert.Equal(Sample.Id, loaded.ActiveProfileId);
        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal(Sample, profile);
        Assert.Contains("\"preset\": \"Custom\"", json, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"OpenAi\"", json, StringComparison.Ordinal);
        Assert.Equal(AiDriverKind.ClaudeCode, loaded.Driver);
    }

    [Fact]
    public void Settings_with_equal_profile_lists_are_equal_so_unchanged_sections_raise_no_change()
    {
        var a = new AiSettings { Profiles = [Sample], ActiveProfileId = Sample.Id };
        var b = new AiSettings { Profiles = [Sample with { }], ActiveProfileId = Sample.Id };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Profiles = [Sample with { Model = "other" }] });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ai":{"driver":"claudeCode","model":"opus","effort":"high","maskLiterals":true}}""")]
    public void Files_without_the_new_fields_load_as_claude_code_without_profiles(string json)
    {
        var ai = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai;

        Assert.Empty(ai.Profiles);
        Assert.Null(ai.ActiveProfileId);
        Assert.Equal(AiDriverKind.ClaudeCode, ai.Driver);
    }

    [Fact]
    public void A_file_written_with_profiles_still_reads_in_the_old_shape_without_resetting()
    {
        var settings = new AppSettings
        {
            Ai = new AiSettings { Model = "opus", MaskLiterals = true, Profiles = [Sample], ActiveProfileId = Sample.Id },
        };
        var json = JsonSerializer.Serialize(settings, OrandaJsonContext.Default.AppSettings);

        // 새 칸을 모르는 옛 빌드의 형식(모르는 칸은 건너뛰고, 열거형 문자열 값은 driver 칸에만 있다)
        var old = JsonSerializer.Deserialize<OldSettingsShape>(json, Web)!;

        Assert.Equal("ClaudeCode", old.Ai!.Driver);
        Assert.Equal("opus", old.Ai.Model);
        Assert.True(old.Ai.MaskLiterals);
    }

    [Theory]
    [InlineData("""{"ai":{"driver":"claudeCode","profiles":null}}""")]
    [InlineData("""{"ai":{"profiles":null,"activeProfileId":null}}""")]
    public void A_hand_edited_null_profiles_list_is_received_as_empty(string json)
    {
        var ai = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai;

        Assert.NotNull(ai.Profiles);
        Assert.Empty(ai.Profiles);
        Assert.Null(ai.ActiveProfileId);
    }

    [Fact]
    public void Null_elements_in_a_hand_edited_profiles_list_are_dropped()
    {
        var json = """{"ai":{"profiles":[null,{"id":"11111111-2222-3333-4444-555555555555","name":"사내"}]}}""";

        var ai = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai;

        Assert.Equal("사내", Assert.Single(ai.Profiles).Name);
    }

    [Theory]
    [InlineData("\"DeepSeek\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public void An_unknown_preset_or_format_value_is_read_as_unknown_not_as_an_error(string value)
    {
        var json = $$$"""{"ai":{"profiles":[{"name":"x","preset":{{{value}}},"format":{{{value}}}}]}}""";

        var profile = Assert.Single(JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai.Profiles);

        Assert.Equal((AiProfilePreset.Unknown, AiApiFormat.Unknown), (profile.Preset, profile.Format));
    }

    [Fact]
    public void Known_values_read_case_insensitively_and_an_unknown_value_writes_back_as_unknown()
    {
        var json = """{"ai":{"profiles":[{"preset":"lmstudio","format":"OPENAI"},{"preset":"DeepSeek","format":"Responses"}]}}""";

        var ai = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai;
        var written = JsonSerializer.Serialize(new AppSettings { Ai = ai }, OrandaJsonContext.Default.AppSettings);

        Assert.Equal((AiProfilePreset.LmStudio, AiApiFormat.OpenAi), (ai.Profiles[0].Preset, ai.Profiles[0].Format));
        Assert.Contains("\"preset\": \"Unknown\"", written, StringComparison.Ordinal);
        Assert.Equal(AiProfilePreset.Unknown, JsonSerializer.Deserialize(written, OrandaJsonContext.Default.AppSettings)!.Ai.Profiles[1].Preset);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"not-a-guid\"")]
    [InlineData("7")]
    [InlineData("{\"a\":1}")]
    public void A_broken_active_profile_id_reads_as_the_empty_id_never_as_claude_code(string value)
    {
        var ai = JsonSerializer.Deserialize($$$"""{"ai":{"driver":"ClaudeCode","activeProfileId":{{{value}}}}}""", OrandaJsonContext.Default.AppSettings)!.Ai;

        Assert.Equal(Guid.Empty, ai.ActiveProfileId);
        Assert.Equal(AiActiveKind.ProfileMissing, AiActiveProfile.Find(ai).Kind);
    }

    [Fact]
    public void An_absent_or_null_active_profile_id_stays_null_for_claude_code()
    {
        Assert.Null(JsonSerializer.Deserialize("""{"ai":{}}""", OrandaJsonContext.Default.AppSettings)!.Ai.ActiveProfileId);
        Assert.Null(JsonSerializer.Deserialize("""{"ai":{"activeProfileId":null}}""", OrandaJsonContext.Default.AppSettings)!.Ai.ActiveProfileId);
    }

    [Fact]
    public void Missing_null_or_non_positive_profile_fields_take_their_defaults()
    {
        var json = """{"ai":{"profiles":[{"name":"x"},{"name":null,"baseUrl":null,"model":null,"maxInputTokens":0,"maxOutputTokens":-1}]}}""";

        var profiles = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai.Profiles;

        Assert.All(profiles, p =>
        {
            Assert.Equal((AiLlmProfile.DefaultMaxInputTokens, AiLlmProfile.DefaultMaxOutputTokens), (p.MaxInputTokens, p.MaxOutputTokens));
            Assert.Equal(("", ""), (p.BaseUrl, p.Model));
        });
        Assert.Equal("", profiles[1].Name);
    }

    [Fact]
    public void A_profile_holds_no_secret_field_because_the_key_lives_in_the_credential_store()
    {
        var names = typeof(AiLlmProfile).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("key", StringComparison.OrdinalIgnoreCase)
            || n.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || n.Contains("password", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Token", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>새 칸 이전의 설정 모양. 열거형은 문자열로 받아 값이 늘어도 읽힘을 확인한다.</summary>
    private sealed class OldSettingsShape
    {
        public OldAiShape? Ai { get; set; }
    }

    private sealed class OldAiShape
    {
        public string? Driver { get; set; }

        public string? Model { get; set; }

        public bool MaskLiterals { get; set; }
    }
}
