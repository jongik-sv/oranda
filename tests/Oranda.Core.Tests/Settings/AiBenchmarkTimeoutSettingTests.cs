using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Settings;

/// <summary>
/// ai.benchmarkTimeoutSeconds(운영 벤치마크 제한 시간): 옛 파일은 30 으로 읽고, 범위 밖·숫자가 아닌 값은 설정 전체를 버리지 않고 30 으로 읽으며,
/// 값을 넘기지 않은 실행 범위는 제한 없음이 아니라 30초다.
/// </summary>
public class AiBenchmarkTimeoutSettingTests
{
    private static AiSettings Load(string json) => JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai;

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ai":{"driver":"claudeCode","model":"opus","effort":"high","maskLiterals":true}}""")]
    public void Old_files_without_the_field_read_as_30_seconds(string json)
    {
        Assert.Equal(30, Load(json).BenchmarkTimeoutSeconds);
        Assert.Equal(30, new AiSettings().BenchmarkTimeoutSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(3600)]
    public void Allowed_values_round_trip(int seconds)
    {
        var settings = new AppSettings { Ai = new AiSettings { BenchmarkTimeoutSeconds = seconds, MaskLiterals = true } };

        var json = JsonSerializer.Serialize(settings, OrandaJsonContext.Default.AppSettings);
        var loaded = Load(json);

        Assert.Equal(seconds, loaded.BenchmarkTimeoutSeconds);
        Assert.True(loaded.MaskLiterals);
        Assert.Equal(settings.Ai, loaded);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("3601")]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("2147483648")]
    [InlineData("12.5")]
    [InlineData("\"abc\"")]
    [InlineData("\"60\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void Out_of_range_or_unreadable_values_read_as_30_and_keep_the_rest_of_the_settings(string value)
    {
        var ai = Load($$$"""{"ai":{"model":"opus","maskLiterals":true,"benchmarkTimeoutSeconds":{{{value}}}}}""");

        Assert.Equal(30, ai.BenchmarkTimeoutSeconds); // 깨진 값이 조용히 제한 없음(0)이 되지 않는다
        Assert.Equal("opus", ai.Model);
        Assert.True(ai.MaskLiterals);
    }

    [Fact]
    public void The_init_setter_also_closes_out_of_range_values_to_30()
    {
        Assert.Equal(30, new AiSettings { BenchmarkTimeoutSeconds = 4 }.BenchmarkTimeoutSeconds);
        Assert.Equal(30, new AiSettings { BenchmarkTimeoutSeconds = -5 }.BenchmarkTimeoutSeconds);
        Assert.Equal(30, new AiSettings { BenchmarkTimeoutSeconds = 3601 }.BenchmarkTimeoutSeconds);
        Assert.Equal(0, new AiSettings { BenchmarkTimeoutSeconds = 0 }.BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void Equals_and_hash_code_include_the_timeout()
    {
        var a = new AiSettings { BenchmarkTimeoutSeconds = 60 };
        var b = new AiSettings { BenchmarkTimeoutSeconds = 60 };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { BenchmarkTimeoutSeconds = 120 });
        Assert.NotEqual(new AiSettings(), b);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(3600, true)]
    [InlineData(4, false)]
    [InlineData(1, false)]
    [InlineData(3601, false)]
    [InlineData(-1, false)]
    public void Valid_range_is_zero_or_5_to_3600(int seconds, bool valid) => Assert.Equal(valid, AiSettings.IsValidBenchmarkTimeout(seconds));

    [Fact]
    public void A_run_scope_without_the_field_filled_is_30_seconds_not_unlimited()
    {
        var profile = new ConnectionProfile("p", "u", "h/s", Environment: ConnectionEnvironment.Production);
        var scope = new AgentRunScope("r", profile, new AiContext("p", ConnectionEnvironment.Production, AiAccessLevel.Query));

        Assert.Equal(30, scope.BenchmarkTimeoutSeconds);
        Assert.Equal(30, new AgentSession(scope).Run.BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void A_run_scope_keeps_an_explicit_value_and_unlimited_only_when_zero_is_given()
    {
        var profile = new ConnectionProfile("p", "u", "h/s", Environment: ConnectionEnvironment.Production);
        var context = new AiContext("p", ConnectionEnvironment.Production, AiAccessLevel.Query);

        Assert.Equal(120, new AgentRunScope("r", profile, context) { BenchmarkTimeoutSeconds = 120 }.BenchmarkTimeoutSeconds);
        Assert.Equal(0, new AgentRunScope("r", profile, context) { BenchmarkTimeoutSeconds = 0 }.BenchmarkTimeoutSeconds);
        Assert.Equal(30, new AgentRunScope("r", profile, context) { BenchmarkTimeoutSeconds = 3 }.BenchmarkTimeoutSeconds);
        Assert.Equal(30, new AgentRunScope("r", profile, context) { BenchmarkTimeoutSeconds = -1 }.BenchmarkTimeoutSeconds);
    }
}
