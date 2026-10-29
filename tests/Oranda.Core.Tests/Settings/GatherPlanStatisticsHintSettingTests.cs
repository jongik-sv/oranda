using System.Text.Json;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Settings;

/// <summary>execution.gatherPlanStatisticsHint(실제 계획 때 늘 힌트 넣기): 기본은 켬이고, 저장 파일에 명시적으로 꺼 둔 값은 그대로 따른다.</summary>
public class GatherPlanStatisticsHintSettingTests
{
    private static ExecutionSettings Read(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Execution;

    [Fact]
    public void Default_is_on() => Assert.True(new AppSettings().Execution.GatherPlanStatisticsHint);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"execution":{}}""")]
    [InlineData("""{"execution":{"autoCommit":true}}""")]
    public void Settings_saved_without_the_value_get_the_new_default(string json) => Assert.True(Read(json).GatherPlanStatisticsHint);

    [Fact]
    public void An_explicitly_saved_off_value_is_respected() =>
        Assert.False(Read("""{"execution":{"gatherPlanStatisticsHint":false}}""").GatherPlanStatisticsHint);

    [Fact]
    public void Off_survives_a_save_and_load_round_trip()
    {
        var off = new AppSettings { Execution = new ExecutionSettings { GatherPlanStatisticsHint = false } };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(off, options), options)!;
        Assert.False(back.Execution.GatherPlanStatisticsHint);
    }
}
