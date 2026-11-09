namespace Oranda.Tibero.Tests;

public class TiberoItEnvTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    private static readonly (string, string)[] All =
    [
        ("ORANDA_IT_TIBERO_HOST", "h"),
        ("ORANDA_IT_TIBERO_PORT", "8629"),
        ("ORANDA_IT_TIBERO_DB_NAME", "d"),
        ("ORANDA_IT_TIBERO_USER", "u"),
        ("ORANDA_IT_TIBERO_PASSWORD", "p"),
    ];

    [Fact]
    public void Without_variables_the_test_is_skipped()
    {
        var env = new TiberoItEnv(Env());

        Assert.False(env.Enabled);
        Assert.True(env.Skip());
        Assert.Equal(5, env.Missing.Count);
    }

    [Fact]
    public void Require_mode_fails_when_variables_are_missing_and_names_only_the_missing_ones()
    {
        var env = new TiberoItEnv(Env(("ORANDA_IT_REQUIRE", "1"), ("ORANDA_IT_TIBERO_HOST", "secret-host")));

        var ex = Assert.Throws<InvalidOperationException>(() => env.Skip());

        Assert.Contains("ORANDA_IT_TIBERO_PORT", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ORANDA_IT_TIBERO_HOST", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_all_variables_the_test_runs()
    {
        var env = new TiberoItEnv(Env(All));

        Assert.True(env.Enabled);
        Assert.False(env.Skip());
        Assert.Equal("8629", env.Port);
    }
}
