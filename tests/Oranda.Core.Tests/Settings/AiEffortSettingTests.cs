using System.Text.Json;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Settings;

/// <summary>ai.effort(<c>claude --effort</c> 기본 값): 없으면 null(CLI 기본)이고, 저장한 값은 그대로 돌아온다. 고를 수 있는 값 목록은 저장하지 않는다.</summary>
public class AiEffortSettingTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ai":{"model":"opus"}}""")]
    public void Settings_saved_without_the_value_use_the_cli_default(string json) =>
        Assert.Null(JsonSerializer.Deserialize<AppSettings>(json, Web)!.Ai.Effort);

    [Fact]
    public void Effort_survives_a_save_and_load_round_trip()
    {
        var json = JsonSerializer.Serialize(new AppSettings { Ai = new AiSettings { Effort = "xhigh" } }, Web);
        Assert.Contains("\"effort\":\"xhigh\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("effortLevels", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("xhigh", JsonSerializer.Deserialize<AppSettings>(json, Web)!.Ai.Effort);
    }

    [Fact]
    public void Levels_are_the_values_the_cli_accepts() =>
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], AiSettings.EffortLevels);
}
