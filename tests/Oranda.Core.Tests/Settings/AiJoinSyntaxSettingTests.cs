using System.Text.Json;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Settings;

/// <summary>ai.joinSyntax: 옛 파일과 읽을 수 없는 값은 설정 전체를 버리지 않고 Oracle 로 읽는다.</summary>
public class AiJoinSyntaxSettingTests
{
    private static AiSettings Load(string json) => JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Ai;

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ai":{"model":"opus"}}""")]
    public void Old_files_without_the_field_read_as_oracle(string json) => Assert.Equal(AiJoinSyntax.Oracle, Load(json).JoinSyntax);

    [Theory]
    [InlineData(AiJoinSyntax.Oracle)]
    [InlineData(AiJoinSyntax.Ansi)]
    public void Values_round_trip(AiJoinSyntax syntax)
    {
        var settings = new AppSettings { Ai = new AiSettings { JoinSyntax = syntax, MaskLiterals = true } };

        var loaded = Load(JsonSerializer.Serialize(settings, OrandaJsonContext.Default.AppSettings));

        Assert.Equal(syntax, loaded.JoinSyntax);
        Assert.Equal(settings.Ai, loaded);
    }

    [Theory]
    [InlineData("\"nonsense\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void Unreadable_values_read_as_oracle_and_keep_the_rest_of_the_settings(string value)
    {
        var ai = Load($$$"""{"ai":{"model":"opus","maskLiterals":true,"joinSyntax":{{{value}}}}}""");

        Assert.Equal(AiJoinSyntax.Oracle, ai.JoinSyntax);
        Assert.Equal("opus", ai.Model);
        Assert.True(ai.MaskLiterals);
    }

    [Fact]
    public void Equals_includes_the_join_syntax()
    {
        var ansi = new AiSettings { JoinSyntax = AiJoinSyntax.Ansi };

        Assert.Equal(ansi, new AiSettings { JoinSyntax = AiJoinSyntax.Ansi });
        Assert.Equal(ansi.GetHashCode(), new AiSettings { JoinSyntax = AiJoinSyntax.Ansi }.GetHashCode());
        Assert.NotEqual(new AiSettings(), ansi);
    }
}
