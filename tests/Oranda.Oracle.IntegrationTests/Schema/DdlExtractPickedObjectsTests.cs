using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Schema;

// 소유: TSK-13-01. DDL 일괄 추출 대상 미리보기 — 미리보기 목록(객체 검색)에서 체크한 객체만 추출한다.
// 26ai(OracleFree)와 11.2(Oracle11g) 두 서버에서 같은 시험을 돈다(TSK-10-03).

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class DdlExtractPickedObjectsTests(OracleFreeFixture db) : DdlExtractPickedObjectsCases(db.DataSource, db.SystemPassword)
{
}

/// <summary>11.2 서버: 객체 검색 11g 변형 SQL·DBMS_METADATA 경로(TSK-10-03 이 더함).</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class DdlExtractPickedObjects11gTests(Oracle11gFixture db) : DdlExtractPickedObjectsCases(db.DataSource, db.SystemPassword)
{
    // 11.2 에는 NO AUTHENTICATION 이 없다(18c+). 이 계정으로는 접속하지 않는다(일회용 비밀번호, 출력하지 않는다). XE 기본 표 공간은 SYSTEM 이라 USERS 를 정한다.
    private readonly string _ownerPassword = "Pk" + Guid.NewGuid().ToString("N")[..16];

    protected override string CreateOwnerSql(string owner) => $"CREATE USER {owner} IDENTIFIED BY \"{_ownerPassword}\" DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS";
}

public abstract class DdlExtractPickedObjectsCases(string dataSource, string systemPassword) : IAsyncLifetime
{
    private readonly string _owner = "PCK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<ISession> _sessions = [];
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "oranda-ddl-pick-" + Guid.NewGuid().ToString("N")[..8]);

    protected virtual string CreateOwnerSql(string owner) => $"CREATE USER {owner} NO AUTHENTICATION QUOTA UNLIMITED ON USERS";

    public async Task InitializeAsync()
    {
        await using var conn = await OpenSystemAsync();
        string[] ddl =
        [
            CreateOwnerSql(_owner),
            $"CREATE TABLE {_owner}.t_a (id NUMBER)",
            $"CREATE TABLE {_owner}.t_b (id NUMBER)",
            $"CREATE TABLE {_owner}.t_c (id NUMBER)",
            // 없는 테이블을 쓰는 프로시저 → INVALID.
            $"CREATE PROCEDURE {_owner}.bad_p AS BEGIN INSERT INTO {_owner}.nope VALUES (1); END;",
        ];
        foreach (var sql in ddl)
        {
            await ExecAsync(conn, sql, ignoreErrors: sql.Contains("bad_p", StringComparison.Ordinal));
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        await using var conn = await OpenSystemAsync();
        await ExecAsync(conn, $"DROP USER {_owner} CASCADE", true);
        if (Directory.Exists(_outDir))
        {
            Directory.Delete(_outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Two_checked_of_three_tables_write_two_files()
    {
        var s = await OpenAsync();
        var preview = await new OracleSchemaAdminProvider().SearchObjectsAsync(s, new ObjectSearchQuery
        {
            Owner = _owner,
            ObjectTypes = ["TABLE"],
            Limit = 5001,
        }, default);
        Assert.Equal(["T_A", "T_B", "T_C"], preview.Select(r => r.Ref.Name).Order(StringComparer.Ordinal));
        Assert.All(preview, r => Assert.Equal("VALID", r.Status));

        // 미리보기에서 T_B 를 체크 해제한 것과 같다.
        var checkedRefs = preview.Select(r => r.Ref).Where(r => r.Name != "T_B").ToList();
        var result = await DdlBulkExporter.ExtractAsync(NewMeta(), s, new DdlExtractRequest
        {
            Owner = _owner,
            ObjectTypes = ["TABLE"],
            Objects = checkedRefs,
            OutputDirectory = _outDir,
            Layout = DdlExtractLayout.FilePerObject,
        }, null, default);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(["T_A.sql", "T_C.sql"], result.Files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(result.Files, f => f.EndsWith("T_B.sql", StringComparison.Ordinal));
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(2, Directory.GetFiles(_outDir, "*.sql", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task An_empty_picked_list_extracts_nothing_instead_of_falling_back_to_the_filter()
    {
        var s = await OpenAsync();

        var result = await DdlBulkExporter.ExtractAsync(NewMeta(), s, new DdlExtractRequest
        {
            Owner = _owner,
            Objects = [],
            OutputDirectory = _outDir,
            Layout = DdlExtractLayout.FilePerObject,
        }, null, default);

        Assert.Empty(result.Items);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task Preview_marks_invalid_objects_and_invalid_only_narrows_the_list()
    {
        var s = await OpenAsync();
        var admin = new OracleSchemaAdminProvider();

        var all = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner }, default);
        var bad = Assert.Single(all, r => r.Ref.Name == "BAD_P");
        Assert.Equal("INVALID", bad.Status);
        Assert.NotNull(bad.LastDdlTime);

        var invalid = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, InvalidOnly = true }, default);
        Assert.Equal(["BAD_P"], invalid.Select(r => r.Ref.Name));
    }

    private static OracleMetadataProvider NewMeta()
        => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(systemPassword);
        var session = await manager.OpenAsync(new ConnectionProfile("t-pick", "system", dataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private async Task<OracleConnection> OpenSystemAsync()
    {
        var profile = new ConnectionProfile("pick-system", "system", dataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(systemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection conn, string sql, bool ignoreErrors = false)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (OracleException) when (ignoreErrors)
        {
        }
    }
}
