using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Schema;

// 계획 T17b(설계 18.4, T14 의 Oracle 몫): 파티션 편집을 실제 Oracle 에서 본다 — 공급자가 읽은 파티션(meta.part_scheme·detail_partitions·part_indexes) → 원본 모델 →
// 편집 → PartitionScriptBuilder 문장 실행 → 다시 읽어 맞는지, 안 고치면 0문장, UPDATE INDEXES 로 전역 인덱스가 VALID 로 남는지(Oracle 방언은 REBUILD 를 붙이지 않음),
// 해시 ADD·COALESCE, 경계 채우기, 새 테이블 절, 일반 → 파티션 MODIFY … ONLINE(12.2+).
// 11g XE 는 파티션 기능이 없어(ORA-00439) 방언이 막는지와 비파티션 테이블 읽기만 본다. 상주 컨테이너 환경 변수는 TableEditIntegrationTests 와 같다.
[Trait("Category", "Integration")]
public sealed class PartitionEditIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _owner = "TE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _password = "Te" + Guid.NewGuid().ToString("N")[..16] + "x1";
    private readonly List<ISession> _sessions = [];
    private readonly ITableEditDialect _dialect = new OracleTableEditDialect();
    private bool _enabled;
    private bool _partitioning;

    public PartitionEditIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    public async Task InitializeAsync()
    {
        _enabled = Env("HOST") is not null && Env("SERVICE") is not null && Env("ADMIN_USER") is not null && Env("ADMIN_PASSWORD") is not null;
        if (!_enabled)
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            }

            return;
        }

        await using var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
        await admin.OpenAsync();
        await ExecAsync(admin, $"CREATE USER {_owner} IDENTIFIED BY \"{_password}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users");
        try
        {
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE TO {_owner}");
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = "SELECT value FROM v$option WHERE parameter = 'Partitioning'";
            _partitioning = string.Equals((await cmd.ExecuteScalarAsync())?.ToString(), "TRUE", StringComparison.Ordinal);
        }
        catch
        {
            await DropOwnerAsync(); // InitializeAsync 가 실패하면 xUnit 은 DisposeAsync 를 부르지 않는다
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        if (_enabled)
        {
            await DropOwnerAsync();
        }
    }

    [Fact]
    public async Task Range_edits_round_trip_through_the_server_and_keep_global_indexes_valid()
    {
        if (Skip(needsPartitioning: true))
        {
            return;
        }

        await OwnerExecAsync(
            "CREATE TABLE pe (id NUMBER, d DATE) PARTITION BY RANGE (id) (PARTITION p1 VALUES LESS THAN (100), PARTITION p2 VALUES LESS THAN (200), PARTITION p3 VALUES LESS THAN (300))",
            "CREATE INDEX pe_g ON pe (d)",
            "CREATE INDEX pe_l ON pe (id) LOCAL",
            "INSERT INTO pe VALUES (50, SYSDATE)",
            "INSERT INTO pe VALUES (150, SYSDATE)",
            "INSERT INTO pe VALUES (250, SYSDATE)",
            "COMMIT");
        var s = await OpenAsync();

        var (info, original) = await ReadAsync(s, "PE");
        Assert.Equal(["P1", "P2", "P3"], original.Partitions.Select(x => x.Name));
        Assert.Equal(["100", "200", "300"], original.Partitions.Select(x => x.Bound));
        Assert.Equal(["PE_L"], info.Partitioning!.PartitionedIndexes);
        Assert.Empty(Build(s, "PE", original, original, info)); // 안 고치면 0문장

        // 1회: P1 비우기, P2 를 120 에서 나누고 남는 쪽 이름 P2X, P3 이름 P3R, 끝에 P4 < 400, INTERVAL 100.
        var rows = original.Partitions;
        var edited = original with
        {
            Scheme = original.Scheme! with { Interval = "100" },
            Partitions =
            [
                rows[0] with { IsTruncated = true },
                new PartitionDraft("n1", "P2A") { Bound = "120" },
                rows[1] with { Name = "P2X" },
                rows[2] with { Name = "P3R" },
                new PartitionDraft("n2", "P4") { Bound = "400" },
            ],
        };
        var first = Build(s, "PE", original, edited, info);
        Log(first);
        Assert.DoesNotContain(first, x => x.Contains("REBUILD", StringComparison.Ordinal)); // Oracle 은 UPDATE INDEXES 로 전역 인덱스가 남는다
        Assert.All(first.Where(x => x.Contains("SPLIT", StringComparison.Ordinal) || x.Contains("TRUNCATE", StringComparison.Ordinal)),
            x => Assert.Contains("UPDATE INDEXES", x, StringComparison.Ordinal));
        await OwnerExecAsync([.. first]);

        var (info2, after) = await ReadAsync(s, "PE");
        Assert.Equal([("P1", "100"), ("P2A", "120"), ("P2X", "200"), ("P3R", "300"), ("P4", "400")], after.Partitions.Select(x => (x.Name, x.Bound!)));
        Assert.Equal("100", after.Scheme!.Interval);
        Assert.Equal("VALID", await ScalarAsync("SELECT status FROM user_indexes WHERE index_name = 'PE_G'"));
        Assert.Equal("150,250", await ScalarAsync("SELECT LISTAGG(id, ',') WITHIN GROUP (ORDER BY id) FROM pe"));
        Assert.Empty(Build(s, "PE", after, after, info2)); // 다시 읽은 원본도 0문장

        // 2회: P2A 를 P2X 로 합치기, P4 지우기, INTERVAL 끔(끄기가 맨 앞 — 그래야 마지막 파티션 DROP 이 된다, ORA-14758).
        var r2 = after.Partitions;
        var edited2 = after with
        {
            Scheme = after.Scheme with { Interval = null },
            Partitions = [r2[0], r2[1] with { MergedInto = r2[2].RowKey }, r2[2], r2[3], r2[4] with { IsDropped = true }],
        };
        var second = Build(s, "PE", after, edited2, info2);
        Log(second);
        Assert.StartsWith("ALTER TABLE " + _owner + ".PE SET INTERVAL ()", second[0], StringComparison.Ordinal);
        await OwnerExecAsync([.. second]);

        var (_, after2) = await ReadAsync(s, "PE");
        Assert.Equal([("P1", "100"), ("P2X", "200"), ("P3R", "300")], after2.Partitions.Select(x => (x.Name, x.Bound!)));
        Assert.Null(after2.Scheme!.Interval);
        Assert.Equal("VALID", await ScalarAsync("SELECT status FROM user_indexes WHERE index_name = 'PE_G'"));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM user_ind_partitions WHERE index_name = 'PE_L' AND status <> 'USABLE'"));
        Assert.Equal("150,250", await ScalarAsync("SELECT LISTAGG(id, ',') WITHIN GROUP (ORDER BY id) FROM pe"));
    }

    [Fact]
    public async Task List_split_from_default_and_hash_add_and_coalesce()
    {
        if (Skip(needsPartitioning: true))
        {
            return;
        }

        await OwnerExecAsync(
            "CREATE TABLE pel (id NUMBER, c VARCHAR2(5)) PARTITION BY LIST (c) (PARTITION pa VALUES ('A'), PARTITION px VALUES (DEFAULT))",
            "INSERT INTO pel VALUES (1, 'W')",
            "COMMIT",
            "CREATE TABLE peh (id NUMBER) PARTITION BY HASH (id) PARTITIONS 2",
            "INSERT INTO peh SELECT LEVEL FROM dual CONNECT BY LEVEL <= 20",
            "COMMIT");
        var s = await OpenAsync();

        var (info, original) = await ReadAsync(s, "PEL");
        Assert.Empty(Build(s, "PEL", original, original, info));
        var edited = original with
        {
            Partitions =
            [
                original.Partitions[0],
                new PartitionDraft("n1", "PW") { Bound = "'W', 'V'", SplitFrom = original.Partitions[1].RowKey },
                original.Partitions[1],
            ],
        };
        var script = Build(s, "PEL", original, edited, info);
        Log(script);
        await OwnerExecAsync([.. script]);

        var (info2, after) = await ReadAsync(s, "PEL");
        Assert.Equal([("PA", "'A'"), ("PW", "'W','V'"), ("PX", "DEFAULT")], after.Partitions.Select(x => (x.Name, x.Bound!.Replace(" ", string.Empty, StringComparison.Ordinal))));
        Assert.Equal("1", await ScalarAsync("SELECT COUNT(*) FROM pel PARTITION (pw)"));
        Assert.Empty(Build(s, "PEL", after, after, info2));

        // 해시: Oracle 방언은 ADD·COALESCE 를 막지 않는다(Tibero 는 막음). 서버 이름 파티션(SYS_P…).
        var (hinfo, horiginal) = await ReadAsync(s, "PEH");
        Assert.All(horiginal.Partitions, x => Assert.True(_dialect.IsServerName(x.Name), x.Name + " is not a server name"));
        var hadd = horiginal with { Partitions = [.. horiginal.Partitions, new PartitionDraft("n1", "H3")] };
        Assert.Empty(PartitionDiff.Compute(horiginal, hadd, _dialect).Problems);
        var add = Build(s, "PEH", horiginal, hadd, hinfo);
        Log(add);
        await OwnerExecAsync([.. add]);
        var (hinfo2, hafter) = await ReadAsync(s, "PEH");
        Assert.Equal(3, hafter.Partitions.Count);
        Assert.Equal("H3", hafter.Partitions[^1].Name);

        var coalesce = Build(s, "PEH", hafter, hafter with { Partitions = [.. hafter.Partitions.Take(2), hafter.Partitions[2] with { IsDropped = true }] }, hinfo2);
        Log(coalesce);
        Assert.Contains(coalesce, x => x.Contains("COALESCE PARTITION", StringComparison.Ordinal));
        await OwnerExecAsync([.. coalesce]);
        var (_, hafter2) = await ReadAsync(s, "PEH");
        Assert.Equal(2, hafter2.Partitions.Count);
        Assert.Equal("20", await ScalarAsync("SELECT COUNT(*) FROM peh"));
    }

    /// <summary>「경계 채우기」 월 줄(<c>DATE '…'</c>)이 그대로 ADD 되고(서버는 TO_DATE(…) 꼴로 돌려줌), INTERVAL 새 테이블 절도 서버가 받는다.</summary>
    [Fact]
    public async Task Bound_filler_rows_and_create_clause_run_on_the_server()
    {
        if (Skip(needsPartitioning: true))
        {
            return;
        }

        await OwnerExecAsync("CREATE TABLE pem (id NUMBER, d DATE) PARTITION BY RANGE (d) (PARTITION p_2026_01 VALUES LESS THAN (DATE '2026-02-01'))");
        var s = await OpenAsync();

        var (info, original) = await ReadAsync(s, "PEM");
        var fill = BoundFiller.Fill(new BoundFill(BoundUnit.Month, 3) { StartDate = new DateOnly(2026, 2, 1) }, [.. original.Partitions.Select(x => x.Name)]);
        var edited = original with { Partitions = [.. original.Partitions, .. fill] };
        var script = Build(s, "PEM", original, edited, info);
        Assert.Equal(3, script.Count);
        await OwnerExecAsync([.. script]);

        var (info2, after) = await ReadAsync(s, "PEM");
        Assert.Equal(["P_2026_01", "P_2026_02", "P_2026_03", "P_2026_04"], after.Partitions.Select(x => x.Name));
        _output.WriteLine("bounds = " + string.Join(" | ", after.Partitions.Select(x => x.Bound)));
        Assert.Contains("2026-05-01", after.Partitions[^1].Bound, StringComparison.Ordinal);
        Assert.Empty(Build(s, "PEM", after, after, info2));

        var clause = PartitionScriptBuilder.CreateClause(new PartitionScheme(PartitionMethod.Range, ["D"]) { Interval = "NUMTOYMINTERVAL(1, 'MONTH')" }, fill, _dialect);
        await OwnerExecAsync($"CREATE TABLE pec (id NUMBER, d DATE) {clause}");
        var (cinfo, _) = await ReadAsync(s, "PEC");
        Assert.Equal(("RANGE", "NUMTOYMINTERVAL(1, 'MONTH')", 3), (cinfo.Partitioning!.Method, cinfo.Partitioning.Interval, cinfo.Partitioning.Partitions.Count));
    }

    /// <summary>일반 → 파티션: 12.2+ 는 <c>MODIFY PARTITION BY … ONLINE</c> 한 문장(TE-41). 데이터·인덱스가 남는다.</summary>
    [Fact]
    public async Task Plain_table_converts_online_in_one_statement()
    {
        if (Skip(needsPartitioning: true))
        {
            return;
        }

        await OwnerExecAsync(
            "CREATE TABLE pon (id NUMBER PRIMARY KEY, d DATE)",
            "INSERT INTO pon SELECT LEVEL, DATE '2026-01-01' + LEVEL FROM dual CONNECT BY LEVEL <= 50",
            "COMMIT");
        var s = await OpenAsync();
        if (!OracleTableEditDialect.AtLeast(s.Server, 12, 2))
        {
            _output.WriteLine("건너뜀: 12.2 미만은 MODIFY PARTITION BY … ONLINE 이 없다(다시 만들기 방식)");
            return;
        }

        Assert.True(_dialect.SupportsOnlinePartitionConversion(s.Server));
        Assert.Null(_dialect.UnsupportedReasonKey(TableEditFeature.PartitionConversion, s.Server));

        var info = await NewProvider().GetTableAsync(s, Ref("PON"), default);
        Assert.False(info.IsPartitioned);
        Assert.Null(info.Partitioning);
        var original = PartitionTableDraft.FromTable(info)!;
        Assert.Null(original.Scheme);
        var edited = new PartitionTableDraft(new PartitionScheme(PartitionMethod.Range, ["ID"]),
            [new PartitionDraft("n1", "P1") { Bound = "26" }, new PartitionDraft("n2", "PMAX") { Bound = "MAXVALUE" }]);

        var script = Build(s, "PON", original, edited, info);
        Log(script);
        Assert.EndsWith(" ONLINE", Assert.Single(script), StringComparison.Ordinal);
        await OwnerExecAsync([.. script]);

        var (_, after) = await ReadAsync(s, "PON");
        Assert.Equal([("P1", "26"), ("PMAX", "MAXVALUE")], after.Partitions.Select(x => (x.Name, x.Bound!)));
        Assert.Equal("25", await ScalarAsync("SELECT COUNT(*) FROM pon PARTITION (p1)"));
        Assert.Equal("50", await ScalarAsync("SELECT COUNT(*) FROM pon"));
    }

    /// <summary>파티션 기능이 없는 서버(11g XE·SE): 방언이 막고, 서버도 ORA-00439. 비파티션 테이블은 파티션 정보 없이 읽힌다(part 질의를 묻지 않음).</summary>
    [Fact]
    public async Task Without_the_partitioning_option_the_dialect_blocks_and_plain_tables_read()
    {
        if (Skip(needsPartitioning: false))
        {
            return;
        }

        await OwnerExecAsync("CREATE TABLE pnp (id NUMBER)");
        var s = await OpenAsync();
        var info = await NewProvider().GetTableAsync(s, Ref("PNP"), default);
        Assert.False(info.IsPartitioned);
        Assert.Null(info.Partitioning);
        Assert.Equal(PartitionTableDraft.None, PartitionTableDraft.FromTable(info));
        _output.WriteLine($"{s.Server.Version} {s.Server.Edition} partitioning={_partitioning}");

        if (_partitioning)
        {
            Assert.Null(_dialect.UnsupportedReasonKey(TableEditFeature.Partitioning, s.Server));
            return;
        }

        Assert.Equal(OracleTableEditDialect.NeedsEnterpriseKey, _dialect.UnsupportedReasonKey(TableEditFeature.Partitioning, s.Server));
        var ex = await Assert.ThrowsAsync<OracleException>(() => OwnerExecAsync("CREATE TABLE pnx (id NUMBER) PARTITION BY HASH (id) PARTITIONS 2"));
        Assert.Equal(439, ex.Number);
    }

    private List<string> Build(ISession s, string table, PartitionTableDraft original, PartitionTableDraft edited, TableInfo info)
    {
        var local = info.Partitioning?.PartitionedIndexes ?? [];
        var global = info.Indexes.Select(i => i.Name).Where(n => !local.Contains(n, StringComparer.Ordinal)).ToList();
        return [.. PartitionScriptBuilder.Build(_owner, table, original, edited, _dialect, s.Server, global).Select(x => x.Statement.Text)];
    }

    private void Log(IEnumerable<string> statements)
    {
        foreach (var x in statements)
        {
            _output.WriteLine(x.Replace(_owner, "<owner>", StringComparison.Ordinal));
        }
    }

    private async Task<(TableInfo Info, PartitionTableDraft Draft)> ReadAsync(ISession s, string table)
    {
        var info = await NewProvider().GetTableAsync(s, Ref(table), default);
        Assert.True(info.IsPartitioned, table + " is not read as partitioned");
        Assert.NotNull(info.Partitioning);
        return (info, PartitionTableDraft.FromInfo(info.Partitioning)!);
    }

    private DbObjectRef Ref(string name) => new(_owner, name, "TABLE");

    private bool Skip(bool needsPartitioning)
    {
        if (!_enabled)
        {
            _output.WriteLine("건너뜀: ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
            return true;
        }

        if (needsPartitioning && !_partitioning)
        {
            _output.WriteLine("건너뜀: 이 서버에는 파티션 기능이 없다(V$OPTION Partitioning = FALSE, 11g XE)");
            return true;
        }

        return false;
    }

    private static OracleMetadataProvider NewProvider() => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(_password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-partition-edit", _owner, DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private async Task OwnerExecAsync(params string[] statements)
    {
        await using var conn = Connect(_owner, _password);
        await conn.OpenAsync();
        foreach (var sql in statements)
        {
            await ExecAsync(conn, sql);
        }
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var conn = Connect(_owner, _password);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task DropOwnerAsync()
    {
        await using var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
        await admin.OpenAsync();
        await ExecAsync(admin, $"DROP USER {_owner} CASCADE");
    }

    private static OracleConnection Connect(string user, string password) =>
        new(new ConnectionProfile("t-partition-edit", user, DataSource).BuildConnectionString(password));

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
