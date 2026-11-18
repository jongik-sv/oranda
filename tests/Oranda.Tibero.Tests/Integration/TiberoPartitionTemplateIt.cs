#pragma warning disable CA1861 // 시험 입력용 상수 배열
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Sessions;
using Oranda.Language.Lexing;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 파티션 추천 설계 6장(PA-5·11·12)을 실제 Tibero 로: 하위 파티션 템플릿 절, LOCAL 인덱스로 고친 다시 만들기 스크립트, 월 추가 스크립트.
/// 실측(2026-11-18): <c>SUBPARTITION BY HASH (c) SUBPARTITIONS n</c> = TBR-7001(이름 템플릿은 됨 → 방언 <c>HashSubpartitionsAsTemplate</c>),
/// <c>MODIFY PARTITION BY … ONLINE UPDATE INDEXES</c> = TBR-7001(이미 다시 만들기 방식), 제약의 <c>USING INDEX</c> 만 = TBR-7001·<c>USING INDEX LOCAL</c> 은 됨,
/// 고유 LOCAL 인덱스가 키 열을 안 담으면 TBR-7314.
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>.</item>
/// <item>공유 계정이라 <c>ORT_PA_</c> 객체만 만들고 끝에 지운다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoPartitionTemplateIt(ITestOutputHelper output)
{
    private static readonly TiberoTableEditDialect Dialect = new();

    private static string T(string suffix) => TiberoItSessions.ObjectName("PA_" + suffix);

    /// <summary>RANGE(+INTERVAL)·LIST·HASH × LIST 템플릿·HASH 이름 템플릿을 서버가 받고, 해시 개수 꼴은 받지 않는다.</summary>
    [TiberoWriteFact]
    public async Task Composite_templates_run_and_hash_subpartitions_need_names()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (_, p) = await OpenAsync(it);
        var tables = new[] { T("CL"), T("CH"), T("CLH"), T("CHL"), T("CHH") };
        foreach (var t in tables)
        {
            await DropAsync(p, t);
        }

        try
        {
            var list = new SubpartitionTemplate(PartitionMethod.List, ["C"]) { Rows = [new SubpartitionTemplateRow("SP_A", "'A'"), new SubpartitionTemplateRow("SP_X", "DEFAULT")] };
            var hash = new SubpartitionTemplate(PartitionMethod.Hash, ["N"]) { HashCount = 2 };
            PartitionDraft[] months = [new PartitionDraft("n1", "P1") { Bound = "DATE '2026-01-01'" }];
            PartitionDraft[] regions = [new PartitionDraft("n1", "PA") { Bound = "'A'" }, new PartitionDraft("n2", "PX") { Bound = "DEFAULT" }];
            PartitionDraft[] hashes = [new PartitionDraft("n1", "H1"), new PartitionDraft("n2", "H2")];
            var cases = new (string Table, PartitionScheme Scheme, PartitionDraft[] Rows)[]
            {
                (T("CL"), new PartitionScheme(PartitionMethod.Range, ["D"]) { Interval = "NUMTOYMINTERVAL(1, 'MONTH')", SubpartitionTemplate = list }, months),
                (T("CH"), new PartitionScheme(PartitionMethod.Range, ["D"]) { SubpartitionTemplate = hash }, months),
                (T("CLH"), new PartitionScheme(PartitionMethod.List, ["R"]) { SubpartitionTemplate = hash }, regions),
                (T("CHL"), new PartitionScheme(PartitionMethod.Hash, ["N"]) { SubpartitionTemplate = list }, hashes),
                (T("CHH"), new PartitionScheme(PartitionMethod.Hash, ["R2"]) { SubpartitionTemplate = hash }, hashes),
            };
            foreach (var (table, scheme, rows) in cases)
            {
                Assert.Empty(PartitionDiff.Compute(PartitionTableDraft.None, new PartitionTableDraft(scheme, rows), Dialect).Problems
                    .Where(x => x.ReasonKey != PartitionDiff.ConvertNeedsRebuildKey));
                var clause = PartitionScriptBuilder.CreateClause(scheme, rows, Dialect);
                await p.ExpectAsync(table, "OK", $"CREATE TABLE {table} (D DATE, R VARCHAR2(2), R2 NUMBER, N NUMBER, C VARCHAR2(2)) {clause}");
            }

            await p.ExpectAsync("cl insert", "OK", $"INSERT INTO {T("CL")} VALUES (DATE '2026-05-03', 'A', 1, 1, 'A')");
            Assert.Equal("4", await p.ScalarAsync($"SELECT COUNT(*) FROM USER_TAB_SUBPARTITIONS WHERE TABLE_NAME = '{T("CL")}'")); // 새 달도 템플릿대로
            Assert.Equal("4", await p.ScalarAsync($"SELECT COUNT(*) FROM USER_TAB_SUBPARTITIONS WHERE TABLE_NAME = '{T("CHH")}'"));
            await DropAsync(p, T("CH"));
            await p.ExpectAsync("hash count form", "TBR-7001",
                $"CREATE TABLE {T("CH")} (D DATE, N NUMBER) PARTITION BY RANGE (D) SUBPARTITION BY HASH (N) SUBPARTITIONS 2 (PARTITION P1 VALUES LESS THAN (DATE '2026-01-01'))");
        }
        finally
        {
            foreach (var t in tables)
            {
                await DropAsync(p, t);
            }
        }
    }

    /// <summary>
    /// 다시 만들기 스크립트(앱과 같은 길: <see cref="ColumnReorderBuilder.WithLocalIndexes"/> → <c>RebuildPartitioned</c>)로 RANGE(문자열 YYYYMMDD) + HASH 하위 템플릿,
    /// PK·인덱스 하나를 LOCAL 로. 그 뒤 월 추가 스크립트로 MAXVALUE 를 달마다 나누고, 전역 인덱스는 REBUILD 로 VALID 로 돌아온다.
    /// </summary>
    [TiberoWriteFact]
    public async Task Rebuild_script_makes_local_indexes_and_month_add_splits_maxvalue()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("RB");
        string[] all = [t, $"\"{t}$NEW\"", $"\"{t}$OLD\""];
        foreach (var x in all)
        {
            await DropAsync(p, x);
        }

        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER NOT NULL, YM VARCHAR2(8) NOT NULL, C VARCHAR2(2), CONSTRAINT {T("RB_PK")} PRIMARY KEY (ID, YM))");
            await p.MustAsync($"CREATE INDEX {T("RB_IX")} ON {t} (YM, C)");
            await p.MustAsync($"CREATE INDEX {T("RB_G")} ON {t} (C)");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, '20260105', 'A')");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, '20260212', 'B')");
            await p.MustAsync($"INSERT INTO {t} VALUES (3, '20260301', 'A')");
            await p.MustAsync("COMMIT");

            var provider = TableEditProbe.NewProvider();
            var tableRef = new DbObjectRef(p.Owner, t, "TABLE");
            var info = await provider.GetTableAsync(session, tableRef, CancellationToken.None);
            var scripts = new TiberoColumnReorderScripts();
            var dependent = scripts.DependentPart(
                await provider.GetDdlAsync(session, tableRef, new DdlOptions { SqlTerminator = true }, CancellationToken.None),
                await provider.GetDdlAsync(session, tableRef, new DdlOptions { SqlTerminator = true, IncludeDependentObjects = true }, CancellationToken.None));
            var edited = new PartitionTableDraft(
                new PartitionScheme(PartitionMethod.Range, ["YM"]) { SubpartitionTemplate = new SubpartitionTemplate(PartitionMethod.Hash, ["ID"]) { HashCount = 2 } },
                [new PartitionDraft("n1", "P_202601") { Bound = "'20260201'" }, new PartitionDraft("n2", "P_202602") { Bound = "'20260301'" }, new PartitionDraft("n3", "P_MAX") { Bound = "MAXVALUE" }])
            {
                LocalIndexes = [T("RB_PK"), T("RB_IX")],
            };
            var clause = PartitionScriptBuilder.ConversionClause(PartitionTableDraft.FromTable(info)!, edited, Dialect)!;
            var script = scripts.RebuildPartitioned(tableRef, ColumnOrdering.ByColumnId(info.Columns), enterprise: false, usePrimaryKey: true,
                ColumnReorderBuilder.WithLocalIndexes(dependent, edited.LocalIndexes), clause);
            await RunAsync(p, "rebuild", script);

            Assert.Equal("RANGE/HASH", await p.ScalarAsync($"SELECT PARTITIONING_TYPE || '/' || SUBPARTITIONING_TYPE FROM USER_PART_TABLES WHERE TABLE_NAME = '{t}'"));
            Assert.Equal($"{T("RB_IX")}:LOCAL,{T("RB_PK")}:LOCAL",
                await p.ScalarAsync($"SELECT LISTAGG(INDEX_NAME || ':' || LOCALITY, ',') WITHIN GROUP (ORDER BY INDEX_NAME) FROM USER_PART_INDEXES WHERE TABLE_NAME = '{t}'"));
            Assert.Equal("NO", await p.ScalarAsync($"SELECT PARTITIONED FROM USER_INDEXES WHERE INDEX_NAME = '{T("RB_G")}'"));
            Assert.Equal("3", await p.ScalarAsync($"SELECT COUNT(*) FROM {t}"));
            Assert.Equal("6", await p.ScalarAsync($"SELECT COUNT(*) FROM USER_TAB_SUBPARTITIONS WHERE TABLE_NAME = '{t}'"));

            // 월 추가(PA-5): 다시 읽은 모델 → 3달 SPLIT + 전역 인덱스 REBUILD(나누기 뒤 UNUSABLE, 실측 19).
            var after = await provider.GetTableAsync(session, tableRef, CancellationToken.None);
            var now = PartitionTableDraft.FromInfo(after.Partitioning)!;
            var plan = PartitionMonthScript.Plan(now, 3)!;
            var local = after.Partitioning!.PartitionedIndexes;
            var month = PartitionMonthScript.Script(p.Owner, t, plan, Dialect, [.. after.Indexes.Select(i => i.Name).Where(n => !local.Contains(n, StringComparer.Ordinal))]);
            Assert.Contains($"ALTER INDEX {p.Owner}.{T("RB_G")} REBUILD;", month, StringComparison.Ordinal);
            await RunAsync(p, "month", month);

            Assert.Equal("P_202601,P_202602,P_202603,P_202604,P_202605,P_MAX",
                await p.ScalarAsync($"SELECT LISTAGG(PARTITION_NAME, ',') WITHIN GROUP (ORDER BY PARTITION_NAME) FROM USER_TAB_PARTITIONS WHERE TABLE_NAME = '{t}'"));
            Assert.Equal("1", await p.ScalarAsync($"SELECT COUNT(*) FROM {t} PARTITION (P_202603)"));
            Assert.Equal("VALID", await p.ScalarAsync($"SELECT STATUS FROM USER_INDEXES WHERE INDEX_NAME = '{T("RB_G")}'"));
            Assert.Equal("0", await p.ScalarAsync($"SELECT COUNT(*) FROM USER_IND_SUBPARTITIONS WHERE INDEX_NAME IN ('{T("RB_PK")}', '{T("RB_IX")}') AND STATUS <> 'USABLE'"));
        }
        finally
        {
            foreach (var x in all)
            {
                await DropAsync(p, x);
            }
        }
    }

    /// <summary>스크립트를 편집기와 같은 분리기로 나눠 하나씩 보낸다(주석만 남은 조각은 건너뜀). 모두 OK 여야 한다.</summary>
    private static async Task RunAsync(TableEditProbe p, string label, string script)
    {
        var statements = StatementSplitter.Split(script, SplitOptions.Default)
            .Where(s => s.Kind != StatementKind.SqlPlus
                        && !s.Text.ReplaceLineEndings("\n").Split('\n').All(l => string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith("--", StringComparison.Ordinal)))
            .ToList();
        for (var i = 0; i < statements.Count; i++)
        {
            await p.ExpectAsync($"{label} {i + 1}/{statements.Count}", "OK", statements[i].Text);
        }
    }

    /// <summary>이 회차 시험 표(<c>ORT_PA_</c>, 따옴표 이름 포함)만 지운다.</summary>
    private static Task DropAsync(TableEditProbe p, string table)
    {
        Assert.StartsWith("ORT_PA_", table.TrimStart('"'), StringComparison.Ordinal);
        return p.SilentAsync($"DROP TABLE {table} CASCADE CONSTRAINTS PURGE");
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
