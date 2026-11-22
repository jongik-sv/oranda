using System.Text.Json;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Setup;

/// <summary>도우미 설정 칸(<see cref="AiSettings.Setup"/>)의 왕복과 옛 파일·모르는 값 내성.</summary>
public class AiSetupSettingsTests
{
    private static AppSettings Read(string json) => JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

    [Fact]
    public void An_old_file_without_the_key_gets_defaults()
    {
        var setup = Read("""{ "ai": { "driver": "ClaudeCode" } }""").Ai.Setup;

        Assert.False(setup.FirstRunPromptClosed);
        Assert.Null(setup.LastPath);
        Assert.Null(setup.LastStep);
    }

    [Fact]
    public void Setup_round_trips_through_the_settings_file()
    {
        var setup = new AiSetupSettings { FirstRunPromptClosed = true, LastPath = AiSetupPath.CompanyServer, LastStep = AiSetupStepId.Access, LastProfileId = Guid.NewGuid() };
        var json = JsonSerializer.Serialize(new AppSettings { Ai = new AiSettings { Setup = setup } }, OrandaJsonContext.Default.AppSettings);

        Assert.Contains("\"lastPath\": \"CompanyServer\"", json, StringComparison.Ordinal);
        Assert.Equal(setup, Read(json).Ai.Setup);
    }

    [Theory]
    [InlineData("""{ "ai": { "model": "haiku", "setup": null } }""")]
    [InlineData("""{ "ai": { "model": "haiku", "setup": 3 } }""")]
    [InlineData("""{ "ai": { "model": "haiku", "setup": [true] } }""")]
    [InlineData("""{ "ai": { "model": "haiku", "setup": { "firstRunPromptClosed": "yes", "lastPath": "Robot", "lastStep": 2, "lastProfileId": "not-a-guid" } } }""")]
    [InlineData("""{ "ai": { "model": "haiku", "setup": { "lastPath": { "x": 1 }, "lastStep": ["Login"], "other": { "deep": [1] } } } }""")]
    public void Unreadable_values_fall_back_without_breaking_the_file(string json)
    {
        var settings = Read(json);

        Assert.Equal("haiku", settings.Ai.Model);
        Assert.Equal(new AiSetupSettings(), settings.Ai.Setup);
    }

    [Fact]
    public void Settings_with_different_setup_values_are_not_equal()
    {
        var a = new AiSettings();
        var b = a with { Setup = new AiSetupSettings { FirstRunPromptClosed = true } };

        Assert.NotEqual(a, b);
        Assert.Equal(b, b with { });
    }
}
