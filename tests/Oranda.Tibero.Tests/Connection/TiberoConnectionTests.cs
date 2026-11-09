using Oranda.Core.Connection;
using Oranda.Tibero.Connection;

namespace Oranda.Tibero.Tests.Connection;

public class TiberoConnectionStringFactoryTests
{
    private static ConnectionProfile Profile(string dataSource, string user = "scott") =>
        new("t", user, dataSource, Dbms: Dbms.Tibero);

    [Theory]
    [InlineData("db1:8629/tibero", "Data Source=((INSTANCE=(HOST=db1)(PORT=8629)(DB_NAME=tibero)));User Id=scott;Password=pw")]
    [InlineData("db1/tibero", "Data Source=((INSTANCE=(HOST=db1)(PORT=8629)(DB_NAME=tibero)));User Id=scott;Password=pw")]
    [InlineData("[::1]:9000/tb", "Data Source=((INSTANCE=(HOST=::1)(PORT=9000)(DB_NAME=tb)));User Id=scott;Password=pw")]
    public void Build_assembles_the_descriptor(string dataSource, string expected)
    {
        Assert.Equal(expected, new TiberoConnectionStringFactory().Build(Profile(dataSource), "pw"));
    }

    [Fact]
    public void Build_keeps_an_assembled_descriptor()
    {
        var descriptor = "((INSTANCE=(HOST=h)(PORT=1)(DB_NAME=d)))";
        var cs = new TiberoConnectionStringFactory().Build(Profile(descriptor), "pw");
        Assert.StartsWith("Data Source=" + descriptor + ";", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_quotes_values_with_separators()
    {
        var cs = new TiberoConnectionStringFactory().Build(Profile("h:1/d", "a;b"), "p\"w;x");
        Assert.EndsWith(";User Id=\"a;b\";Password=\"p\"\"w;x\"", cs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hostonly")]
    [InlineData("h:99999/d")]
    [InlineData("h:abc/d")]
    [InlineData("h:1/")]
    [InlineData("h)(x:1/d")]
    [InlineData("fe80::1/db")]
    [InlineData("[::1]8629/db")]
    [InlineData("((INSTANCE=(HOST=h)(PORT=1)(DB_NAME=d)));Pooling=false")]
    [InlineData("((INSTANCE=(HOST=h)(PORT=1)(DB_NAME=d))")]
    public void Build_rejects_a_malformed_address(string dataSource)
    {
        Assert.Throws<ArgumentException>(() => new TiberoConnectionStringFactory().Build(Profile(dataSource), "pw"));
    }

    [Fact]
    public void Build_rejects_unsupported_modes()
    {
        var factory = new TiberoConnectionStringFactory();
        Assert.Throws<NotSupportedException>(() => factory.Build(Profile("h/d") with { Auth = AuthMode.SysDba }, "pw"));
        Assert.Throws<NotSupportedException>(() => factory.Build(Profile("h/d") with { ProxyUser = "p" }, "pw"));
    }

    [Fact]
    public void Dbms_is_Tibero()
    {
        Assert.Equal(Dbms.Tibero, new TiberoConnectionStringFactory().Dbms);
    }
}

public class TiberoServerVersionPolicyTests
{
    private readonly TiberoServerVersionPolicy _policy = new();

    [Theory]
    [InlineData("6.2.15")]
    [InlineData("6")]
    [InlineData("7.2.1")]
    [InlineData("Tibero 7  (DB 7.2.1 build 1)")]
    [InlineData("8.0")]
    public void Six_and_later_are_supported(string version)
    {
        Assert.True(_policy.IsSupported(version, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("5.0.1")]
    [InlineData("Tibero 5")]
    public void Older_than_six_is_rejected_with_a_reason(string version)
    {
        Assert.False(_policy.IsSupported(version, out var reason));
        Assert.Contains("Tibero 6", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    public void Unknown_versions_are_rejected(string? version)
    {
        Assert.False(_policy.IsSupported(version, out var reason));
        Assert.NotEqual(string.Empty, reason);
    }

    [Theory]
    [InlineData("Tibero 6  (DB 6.2.15 build 302172)", "6.2.15")]
    [InlineData("6.2.15", "6.2.15")]
    [InlineData("TBMS 7.1", "7.1")]
    [InlineData("Tibero 6", null)]
    [InlineData(null, null)]
    public void ExtractDottedVersion_picks_the_first_dotted_number(string? text, string? expected)
    {
        Assert.Equal(expected, TiberoServerVersionPolicy.ExtractDottedVersion(text));
    }

    [Fact]
    public void FromVersionRows_reads_the_measured_V_VERSION_shape()
    {
        // 실측(6.2.15 서버): PRODUCT_MINOR 는 공백.
        (string?, string?)[] rows = [("PRODUCT_MAJOR", "6"), ("PRODUCT_MINOR", " "), ("BUILD_NUMBER", "302172"), ("PROTOCOL_MAJOR", "2"), ("STABLE_VERSION", "FS06_CS_1902")];
        Assert.Equal("6", TiberoServerVersionPolicy.FromVersionRows(rows));
        Assert.Equal("7.2", TiberoServerVersionPolicy.FromVersionRows([("PRODUCT_MAJOR", "7"), ("PRODUCT_MINOR", "2")]));
    }

    [Fact]
    public void FromVersionRows_falls_back_to_a_dotted_number_or_null()
    {
        Assert.Equal("6.2.15", TiberoServerVersionPolicy.FromVersionRows([("BANNER", "Tibero 6 (DB 6.2.15)")]));
        Assert.Null(TiberoServerVersionPolicy.FromVersionRows([("BUILD_NUMBER", "302172")]));
        Assert.Null(TiberoServerVersionPolicy.FromVersionRows([]));
    }

    [Fact]
    public void Dbms_and_minimum()
    {
        Assert.Equal(Dbms.Tibero, _policy.Dbms);
        Assert.Equal("6", _policy.MinimumText);
        Assert.Contains("6.2.15", _policy.TestedVersions);
    }
}

public class TiberoConnectionProbeTests
{
    private static ConnectionProfile Profile(string dataSource = "h:1/d") => new("t", "u", dataSource, Dbms: Dbms.Tibero);

    [Fact]
    public async Task Probe_reports_a_malformed_address_without_connecting()
    {
        var result = await new TiberoConnectionProbe().ProbeAsync(Profile("nope"), "pw", CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Null(result.Error);
        Assert.NotNull(result.Message);
    }

#if !TIBERO_DRIVER
    [Fact]
    public async Task Probe_without_the_driver_fails_with_a_clear_message()
    {
        var result = await new TiberoConnectionProbe().ProbeAsync(Profile(), "pw", CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(TiberoConnectionProbe.NoDriverMessage, result.Message);
    }
#endif

    [Fact]
    public async Task Probe_honours_a_canceled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TiberoConnectionProbe().ProbeAsync(Profile(), "pw", cts.Token));
    }

    [Fact]
    public void Redact_hides_the_password_in_driver_messages()
    {
        Assert.Equal("bad ***", TiberoConnectionProbe.Redact("bad s3cret", "s3cret"));
        Assert.Equal("bad", TiberoConnectionProbe.Redact("bad", string.Empty));
    }

    [Fact]
    public async Task ChangePassword_is_not_supported_yet()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => new TiberoConnectionProbe().ChangePasswordAsync(Profile(), "a", "b", CancellationToken.None));
    }

    [Fact]
    public async Task Probe_against_a_real_server_reports_the_version()
    {
        var env = new TiberoItEnv();
        if (env.Skip() || !TiberoDriverInfo.IsLinked)
        {
            return;
        }

        var profile = new ConnectionProfile("it", env.User, $"{env.Host}:{env.Port}/{env.DbName}", Dbms: Dbms.Tibero);
        var result = await new TiberoConnectionProbe().ProbeAsync(profile, env.Password, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.False(string.IsNullOrEmpty(result.ServerVersion));
    }

}
