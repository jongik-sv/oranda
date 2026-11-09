using Oranda.Core.Connection;
using Oranda.Tibero.Connection;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Connection;

public class TiberoConnectionStringFactoryTests
{
    private static ConnectionProfile Profile(string dataSource, string user = "scott") =>
        new("t", user, dataSource, Dbms: Dbms.Tibero);

    [Theory]
    [InlineData("db1:8629/tibero", "Data Source=((INSTANCE=(HOST=db1)(PORT=8629)(DB_NAME=tibero)));User Id=scott;Password=pw;Pooling=false")]
    [InlineData("db1/tibero", "Data Source=((INSTANCE=(HOST=db1)(PORT=8629)(DB_NAME=tibero)));User Id=scott;Password=pw;Pooling=false")]
    [InlineData("[::1]:9000/tb", "Data Source=((INSTANCE=(HOST=::1)(PORT=9000)(DB_NAME=tb)));User Id=scott;Password=pw;Pooling=false")]
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
        Assert.EndsWith(";User Id=\"a;b\";Password=\"p\"\"w;x\";Pooling=false", cs, StringComparison.Ordinal);
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

    [Fact]
    public async Task Probe_without_the_driver_tells_the_user_to_specify_it()
    {
        var probe = new TiberoConnectionProbe(static () => throw new TiberoDriverUnavailableException(DriverCheck.Missing));

        var result = await probe.ProbeAsync(Profile(), "pw", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TiberoDriver.NotSpecifiedMessage, result.Message);
    }

    [Fact]
    public async Task Probe_reads_the_version_through_the_adapter()
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("NAME", typeof(string));
        table.Columns.Add("VALUE", typeof(string));
        table.Rows.Add("PRODUCT_MAJOR", "6");
        table.Rows.Add("PRODUCT_MINOR", " ");
        string? sql = null;
        FakeDriverHost.SetHandler(command =>
        {
            sql = command.CommandText;
            return table;
        });

        var result = await new TiberoConnectionProbe(static () => FakeDriverHost.Driver).ProbeAsync(Profile(), "pw", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("6", result.ServerVersion);
        Assert.Equal("SELECT * FROM V$VERSION", sql);
    }

    [Fact]
    public async Task Probe_stops_waiting_at_the_time_limit()
    {
        using var release = new ManualResetEventSlim();
        FakeDriverHost.SetHandler(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(10)); // 응답 없는 서버
            return null;
        });
        var profile = Profile() with { ConnectTimeoutSeconds = 1 };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await new TiberoConnectionProbe(static () => FakeDriverHost.Driver).ProbeAsync(profile, "pw", CancellationToken.None);
        release.Set();

        Assert.False(result.Succeeded);
        Assert.Contains("제한 시간", result.Message, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8), started.Elapsed.ToString());
    }

    [Fact]
    public async Task Probe_classifies_a_driver_side_failure_and_hides_the_password()
    {
        FakeDriverHost.SetHandler(static _ => throw FakeDriverHost.ClientError(-90401, "ODP-90401:Connection refused by the server. pw=s3cret"));

        var result = await new TiberoConnectionProbe(static () => FakeDriverHost.Driver).ProbeAsync(Profile(), "s3cret", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("TBR-90401", result.Error!.Code);
        Assert.DoesNotContain("s3cret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", result.Error.Message, StringComparison.Ordinal);
    }

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

    [TiberoItFact]
    public async Task Probe_against_a_real_server_reports_the_version()
    {
        var env = new TiberoItEnv();
        // 드라이버 위치(ORANDA_TIBERO_DRIVER)가 없으면 접속할 수 없다 — 서버 정보만 있어도 건너뛴다.
        if (env.Skip() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORANDA_TIBERO_DRIVER")))
        {
            return;
        }

        var profile = new ConnectionProfile("it", env.User, $"{env.Host}:{env.Port}/{env.DbName}", Dbms: Dbms.Tibero);
        var result = await new TiberoConnectionProbe().ProbeAsync(profile, env.Password, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.False(string.IsNullOrEmpty(result.ServerVersion));
    }

}
