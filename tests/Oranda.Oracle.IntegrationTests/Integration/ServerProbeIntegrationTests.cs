using OdpConfig = global::Oracle.ManagedDataAccess.Client.OracleConfiguration;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;
using DotNet.Testcontainers.Builders;
using Testcontainers.Oracle;

namespace Oranda.Oracle.IntegrationTests.Integration;

/// <summary>
/// 26ai 컨테이너(gvenzl/oracle-free:23-slim)를 한 번 띄우고 시험 클래스가 공유한다.
/// 컨테이너 런타임(podman/Docker)이 없으면 이 클래스의 시험은 실패한다 — 기본 실행은 필터 Category!=Integration 으로 뺀다.
/// </summary>
public sealed class OracleFreeFixture : IAsyncLifetime
{
    // 시험 안에서 만든 임시 값. 저장·출력하지 않는다.
    private readonly OracleContainer _container;

    public OracleFreeFixture()
    {
        SystemPassword = "Pw" + Guid.NewGuid().ToString("N")[..16] + "#1";
        AppPassword = "Ap" + Guid.NewGuid().ToString("N")[..16] + "#1";
        _container = new OracleBuilder("gvenzl/oracle-free:23-slim")
            .WithPassword(SystemPassword)
            // podman 의 로그 API 응답을 Testcontainers 가 읽지 못해(로그 대기 불가) 로그 대기를 빼고, 아래에서 접속 재시도로 기다린다.
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();
    }

    public string SystemPassword { get; }

    public string AppPassword { get; }

    public static string AppUser => "oranda_app";

    /// <summary>Easy Connect: host:port/FREEPDB1</summary>
    public string DataSource => $"{_container.Hostname}:{_container.GetMappedPublicPort(1521)}/FREEPDB1";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // SYSTEM 으로 비 DBA 계정(CREATE SESSION 만)을 만든다.
        var profile = new ConnectionProfile("free-system", "system", DataSource);
        await using var conn = new OracleConnection(profile.BuildConnectionString(SystemPassword));
        await OpenWhenReadyAsync(conn, TimeSpan.FromMinutes(5));
        foreach (var sql in new[]
        {
            $"CREATE USER {AppUser} IDENTIFIED BY \"{AppPassword}\"",
            $"GRANT CREATE SESSION TO {AppUser}",
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task OpenWhenReadyAsync(OracleConnection conn, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await conn.OpenAsync();
                return;
            }
            catch (OracleException) when (DateTime.UtcNow < deadline)
            {
                // 데이터베이스가 아직 열리는 중(ORA-12541/12514/01033 등).
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class OracleFreeProbeTests : IClassFixture<OracleFreeFixture>
{
    private readonly OracleFreeFixture _db;

    public OracleFreeProbeTests(OracleFreeFixture db) => _db = db;

    [Fact]
    public async Task System_account_probe_matches_server()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        await using var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();

        var info = await ServerProbe.ProbeAsync(conn);

        var fullVersion = await ScalarAsync(conn, "SELECT version_full FROM product_component_version WHERE ROWNUM = 1");
        var pdb = await ScalarAsync(conn, "SELECT name FROM v$pdbs WHERE con_id = SYS_CONTEXT('USERENV','CON_ID')");
        var instance = await ScalarAsync(conn, "SELECT instance_name FROM v$instance");
        var sid = await ScalarAsync(conn, "SELECT sid FROM v$mystat WHERE ROWNUM = 1");
        var dbaRole = await ScalarAsync(conn, "SELECT COUNT(*) FROM session_roles WHERE role = 'DBA'");

        Assert.Equal(23, info.MajorVersion);
        Assert.Equal(fullVersion.Split('.')[0], info.Version.Split('.')[0]);
        Assert.True(info.IsAtLeast12c);
        Assert.True(info.IsMultitenant);
        Assert.Equal(pdb, info.ContainerName, ignoreCase: true);
        Assert.Equal("FREEPDB1", info.ContainerName, ignoreCase: true);
        Assert.Equal(instance, info.InstanceName, ignoreCase: true);
        Assert.Equal(long.Parse(sid, CultureInfo.InvariantCulture), info.SessionId);
        Assert.Equal("FREEPDB1", info.ServiceName, ignoreCase: true);
        Assert.Equal(dbaRole != "0", info.Capabilities.IsDba);
        Assert.True(info.Capabilities.IsDba);
        Assert.True(info.Capabilities.CanQueryDictionary);
        Assert.True(info.Capabilities.CanQueryPerformanceViews);
        Assert.True(await CanCallDbmsMonitorAsync(conn));
        Assert.Equal(await CanCallDbmsMonitorAsync(conn), info.Capabilities.CanTraceSessions);
    }

    // 실제로 호출해 본다(자기 세션의 추적을 끄는 호출이라 부작용이 없다).
    private static async Task<bool> CanCallDbmsMonitorAsync(OracleConnection conn)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "BEGIN DBMS_MONITOR.SESSION_TRACE_DISABLE(NULL, NULL); END;";
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (OracleException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Plain_account_probe_reports_no_privileges()
    {
        var profile = new ConnectionProfile("free-app", OracleFreeFixture.AppUser, _db.DataSource);
        await using var conn = new OracleConnection(profile.BuildConnectionString(_db.AppPassword));
        await conn.OpenAsync();

        var info = await ServerProbe.ProbeAsync(conn);

        Assert.Equal("FREEPDB1", info.ContainerName, ignoreCase: true);
        Assert.Equal(23, info.MajorVersion);
        Assert.False(info.Capabilities.IsDba);
        Assert.False(info.Capabilities.CanQueryDictionary);
        Assert.False(info.Capabilities.CanQueryPerformanceViews);
        Assert.False(info.Capabilities.CanKillSession);

        Assert.False(info.Capabilities.CanTraceSessions);
        Assert.False(await CanCallDbmsMonitorAsync(conn));

        // 실제로 막혀 있는지 직접 확인한다(ORA-00942).
        var ex = await Assert.ThrowsAsync<OracleException>(() => ScalarAsync(conn, "SELECT COUNT(*) FROM v$session"));
        Assert.Equal(942, ex.Number);
    }

    private static async Task<string> ScalarAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }
}

/// <summary>
/// 사내 19c / ADB. 환경 변수가 없으면 시험이 아무것도 하지 않고 반환한다(xunit 2.5 에는 동적 Skip 이 없다).
/// ORANDA_IT_19C_CONNSTR, ORANDA_IT_ADB_CONNSTR = ODP.NET 연결 문자열 전체.
/// ORANDA_IT_ADB_WALLET = 지갑 폴더(생략하면 지갑 없는 TLS).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExternalDbProbeTests
{
    [Fact]
    public async Task Onprem_19c_probe_matches_server()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_IT_19C_CONNSTR") is not { Length: > 0 } cs)
        {
            return; // 보류: 사내 19c 접속 정보 필요
        }

        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        var info = await ServerProbe.ProbeAsync(conn);
        Assert.Equal(19, info.MajorVersion);
        await AssertMatchesServerAsync(conn, info);
    }

    [Fact]
    public async Task Adb_probe_matches_server()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_IT_ADB_CONNSTR") is not { Length: > 0 } cs)
        {
            return; // 보류: ADB 인스턴스 필요
        }

        if (Environment.GetEnvironmentVariable("ORANDA_IT_ADB_WALLET") is { Length: > 0 } wallet)
        {
            OdpConfig.TnsAdmin = wallet;
            OdpConfig.WalletLocation = wallet;
        }

        await using var conn = new OracleConnection(cs);
        await conn.OpenAsync();
        var info = await ServerProbe.ProbeAsync(conn);
        Assert.True(info.IsAtLeast12c);
        await AssertMatchesServerAsync(conn, info);
    }

    private static async Task AssertMatchesServerAsync(OracleConnection conn, ServerInfo info)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM product_component_version WHERE ROWNUM = 1";
        var version = Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
        Assert.Equal(version.Split('.')[0], info.Version.Split('.')[0]);

        cmd.CommandText = "SELECT SYS_CONTEXT('USERENV','SID') FROM dual";
        Assert.Equal(Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture), info.SessionId);

        cmd.CommandText = "SELECT COUNT(*) FROM session_roles WHERE role = 'DBA'";
        Assert.Equal(Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0, info.Capabilities.IsDba);
    }
}
