using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 파티션 편집(T14, 설계 18.4)을 실제 Tibero 서버로: 공급자가 읽은 파티션 → 원본 모델 → 편집 → <see cref="PartitionScriptBuilder"/> 문장을 보내고 다시 읽어 맞는지,
/// 안 고치면 0문장, 나누기·합치기 뒤 전역 인덱스가 다시 VALID(방언의 REBUILD), 경계 채우기 줄이 그대로 ADD 되는지 본다.
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>.</item>
/// <item>공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoPartitionEditIt(ITestOutputHelper output)
{
    private static readonly TiberoTableEditDialect Dialect = new();

    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    [TiberoWriteFact]
    public async Task Range_edits_round_trip_through_the_server_and_keep_global_indexes_valid()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("PE");
        var gix = T("PE_G");
        var lix = T("PE_L");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, D DATE) PARTITION BY RANGE (ID) (PARTITION P1 VALUES LESS THAN (100), PARTITION P2 VALUES LESS THAN (200), PARTITION P3 VALUES LESS THAN (300))");
            await p.MustAsync($"CREATE INDEX {gix} ON {t} (D)");
            await p.MustAsync($"CREATE INDEX {lix} ON {t} (ID) LOCAL");
            foreach (var v in new[] { 50, 150, 250 })
            {
                await p.MustAsync($"INSERT INTO {t} VALUES ({v}, SYSDATE)");
            }

            await p.MustAsync("COMMIT");

            var (info, original) = await ReadAsync(session, p.Owner, t);
            Assert.Equal(["P1", "P2", "P3"], original.Partitions.Select(x => x.Name));
            Assert.Equal(["100", "200", "300"], original.Partitions.Select(x => x.Bound));
            Assert.Equal([lix], info.Partitioning!.PartitionedIndexes);
            Assert.Empty(Build(p.Owner, t, original, original, info)); // 안 고치면 0문장

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
            var first = Build(p.Owner, t, original, edited, info);
            Log(p, first);
            Assert.Contains(first, s => s.StartsWith($"ALTER INDEX {p.Owner}.{gix} REBUILD", StringComparison.Ordinal)); // 나누기 뒤 전역 인덱스(실측 19)
            Assert.DoesNotContain(first, s => s.Contains(lix, StringComparison.Ordinal));
            foreach (var sql in first)
            {
                await p.ExpectAsync("pe 1", "OK", sql);
            }

            var (info2, after) = await ReadAsync(session, p.Owner, t);
            Assert.Equal([("P1", "100"), ("P2A", "120"), ("P2X", "200"), ("P3R", "300"), ("P4", "400")], after.Partitions.Select(x => (x.Name, x.Bound!)));
            Assert.Equal("100", after.Scheme!.Interval);
            Assert.Equal("VALID", await p.ScalarAsync($"SELECT STATUS FROM USER_INDEXES WHERE INDEX_NAME = '{gix}'"));
            Assert.Equal("150,250", await p.ScalarAsync($"SELECT LISTAGG(ID, ',') WITHIN GROUP (ORDER BY ID) FROM {t}"));
            Assert.Empty(Build(p.Owner, t, after, after, info2)); // 다시 읽은 원본도 0문장

            // 2회: P2A 를 P2X 로 합치기, P4 지우기, INTERVAL 끔.
            var r2 = after.Partitions;
            var edited2 = after with
            {
                Scheme = after.Scheme with { Interval = null },
                Partitions = [r2[0], r2[1] with { MergedInto = r2[2].RowKey }, r2[2], r2[3], r2[4] with { IsDropped = true }],
            };
            var second = Build(p.Owner, t, after, edited2, info2);
            Log(p, second);
            foreach (var sql in second)
            {
                await p.ExpectAsync("pe 2", "OK", sql);
            }

            var (_, after2) = await ReadAsync(session, p.Owner, t);
            Assert.Equal([("P1", "100"), ("P2X", "200"), ("P3R", "300")], after2.Partitions.Select(x => (x.Name, x.Bound!)));
            Assert.Null(after2.Scheme!.Interval);
            Assert.Equal("VALID", await p.ScalarAsync($"SELECT STATUS FROM USER_INDEXES WHERE INDEX_NAME = '{gix}'"));
            Assert.Equal("0", await p.ScalarAsync($"SELECT COUNT(*) FROM USER_IND_PARTITIONS WHERE INDEX_NAME = '{lix}' AND STATUS <> 'USABLE'"));
            Assert.Equal("150,250", await p.ScalarAsync($"SELECT LISTAGG(ID, ',') WITHIN GROUP (ORDER BY ID) FROM {t}"));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    [TiberoWriteFact]
    public async Task List_split_from_default_add_and_hash_limits()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var list = T("PEL");
        var hash = T("PEH");
        await p.DropTableAsync(list);
        await p.DropTableAsync(hash);
        try
        {
            await p.MustAsync($"CREATE TABLE {list} (ID NUMBER, C VARCHAR2(5)) PARTITION BY LIST (C) (PARTITION PA VALUES ('A'), PARTITION PX VALUES (DEFAULT))");
            await p.MustAsync($"INSERT INTO {list} VALUES (1, 'W')");
            await p.MustAsync("COMMIT");
            var (info, original) = await ReadAsync(session, p.Owner, list);
            Assert.Empty(Build(p.Owner, list, original, original, info));

            var edited = original with
            {
                Partitions =
                [
                    original.Partitions[0],
                    new PartitionDraft("n1", "PW") { Bound = "'W', 'V'", SplitFrom = original.Partitions[1].RowKey },
                    original.Partitions[1],
                ],
            };
            var script = Build(p.Owner, list, original, edited, info);
            Log(p, script);
            foreach (var sql in script)
            {
                await p.ExpectAsync("pel", "OK", sql);
            }

            var (_, after) = await ReadAsync(session, p.Owner, list);
            Assert.Equal([("PA", "'A'"), ("PW", "'W','V'"), ("PX", "DEFAULT")], after.Partitions.Select(x => (x.Name, x.Bound!)));
            Assert.Equal("1", await p.ScalarAsync($"SELECT COUNT(*) FROM {list} PARTITION (PW)"));

            await p.MustAsync($"CREATE TABLE {hash} (ID NUMBER) PARTITION BY HASH (ID) PARTITIONS 2");
            var (hinfo, horiginal) = await ReadAsync(session, p.Owner, hash);
            Assert.True(horiginal.Partitions.All(x => Dialect.IsServerName(x.Name) || x.Name.StartsWith(p.Owner + "_P", StringComparison.Ordinal)), "hash partitions are server-named");
            var hadd = horiginal with { Partitions = [.. horiginal.Partitions, new PartitionDraft("n1", "H3")] };
            Assert.Empty(Build(p.Owner, hash, horiginal, hadd, hinfo));
            Assert.Equal([TiberoTableEditDialect.HashPartitionChangeKey], PartitionDiff.Compute(horiginal, hadd, Dialect).Problems.Select(x => x.ReasonKey));
            await p.ExpectAsync("peh add (server refuses too)", "TBR-7002", $"ALTER TABLE {hash} ADD PARTITION H3");
        }
        finally
        {
            await p.DropTableAsync(list);
            await p.DropTableAsync(hash);
        }
    }

    /// <summary>「경계 채우기」 월 줄(<c>DATE '…'</c>)이 그대로 ADD 되고, INTERVAL 테이블 새로 만들기 절도 서버가 받는다.</summary>
    [TiberoWriteFact]
    public async Task Bound_filler_rows_and_create_clause_run_on_the_server()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("PEM");
        var created = T("PEC");
        await p.DropTableAsync(t);
        await p.DropTableAsync(created);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, D DATE) PARTITION BY RANGE (D) (PARTITION P_2026_01 VALUES LESS THAN (DATE '2026-02-01'))");
            var (info, original) = await ReadAsync(session, p.Owner, t);
            var fill = BoundFiller.Fill(new BoundFill(BoundUnit.Month, 3) { StartDate = new DateOnly(2026, 2, 1) }, [.. original.Partitions.Select(x => x.Name)]);
            var edited = original with { Partitions = [.. original.Partitions, .. fill] };
            var script = Build(p.Owner, t, original, edited, info);
            Assert.Equal(3, script.Count);
            foreach (var sql in script)
            {
                await p.ExpectAsync("pem", "OK", sql);
            }

            var (info2, after) = await ReadAsync(session, p.Owner, t);
            Assert.Equal(["P_2026_01", "P_2026_02", "P_2026_03", "P_2026_04"], after.Partitions.Select(x => x.Name));
            p.Log("pem", "bounds = " + string.Join(" | ", after.Partitions.Select(x => x.Bound)));
            Assert.Empty(Build(p.Owner, t, after, after, info2));

            var clause = PartitionScriptBuilder.CreateClause(new PartitionScheme(PartitionMethod.Range, ["D"]) { Interval = "NUMTOYMINTERVAL(1, 'MONTH')" }, fill, Dialect);
            await p.ExpectAsync("pec", "OK", $"CREATE TABLE {created} (ID NUMBER, D DATE) {clause}");
            var (cinfo, _) = await ReadAsync(session, p.Owner, created);
            Assert.Equal(("RANGE", "NUMTOYMINTERVAL(1, 'MONTH')", 3), (cinfo.Partitioning!.Method, cinfo.Partitioning.Interval, cinfo.Partitioning.Partitions.Count));
        }
        finally
        {
            await p.DropTableAsync(t);
            await p.DropTableAsync(created);
        }
    }

    private static List<string> Build(string owner, string table, PartitionTableDraft original, PartitionTableDraft edited, TableInfo info)
    {
        var local = info.Partitioning?.PartitionedIndexes ?? [];
        var global = info.Indexes.Select(i => i.Name).Where(n => !local.Contains(n, StringComparer.Ordinal)).ToList();
        return [.. PartitionScriptBuilder.Build(owner, table, original, edited, Dialect, null, global).Select(s => s.Statement.Text)];
    }

    /// <summary>문장을 출력에 남긴다(계정 이름은 가림).</summary>
    private static void Log(TableEditProbe p, IEnumerable<string> statements)
    {
        foreach (var s in statements)
        {
            p.Log("sql", s);
        }
    }

    private static async Task<(TableInfo Info, PartitionTableDraft Draft)> ReadAsync(TiberoSession session, string owner, string table)
    {
        var info = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(owner, table, "TABLE"), CancellationToken.None);
        Assert.True(info.IsPartitioned, table + " is not read as partitioned");
        return (info, PartitionTableDraft.FromInfo(info.Partitioning)!);
    }

    private async Task<(TiberoSession Session, TableEditProbe Probe)> OpenAsync(TiberoItSessions it)
    {
        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(owner), "the session user of the connection is unknown");
        var probe = new TableEditProbe(session, output, owner!.ToUpperInvariant(), it.Env.DbName);
        probe.Hide(await probe.ScalarAsync("SELECT DEFAULT_TABLESPACE FROM USER_USERS"), "<ts>");
        return (session, probe);
    }
}
