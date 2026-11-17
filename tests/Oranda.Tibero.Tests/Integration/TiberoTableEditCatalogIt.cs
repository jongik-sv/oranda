using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 테이블 편집기(T4)가 쓰는 Tibero 카탈로그 질의(<c>meta.columns</c>·<c>meta.constraints</c>·<c>meta.indexes</c>·<c>meta.table_kind</c>)를 실제 서버로 읽는다.
/// 끝에 덧붙인 열(설계 TE-29 값: 길이 기준·FK 삭제 규칙·참조 열·제약 인덱스·서버 이름 여부·DESC·함수 식·인덱스/테이블 테이블스페이스·테이블 종류)과
/// 열 순서(<c>COLUMN_ID</c> 순, 실측 tibero-table-edit-probe.md 1장)를 본다. 열은 이름으로 찾는다(위치는 공급자 매핑 몫).
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>(<see cref="TiberoWriteFactAttribute"/>).</item>
/// <item>공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTableEditCatalogIt(ITestOutputHelper output)
{
    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    /// <summary>가상 열을 나중에 더한 테이블도 열은 COLUMN_ID 순(위치 1부터)으로 오고, 문자 열은 CHAR_USED(B/C)를 함께 준다.</summary>
    [TiberoWriteFact]
    public async Task Columns_come_in_column_id_order_with_char_semantics()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("KC");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, S1 VARCHAR2(10 BYTE), S2 VARCHAR2(10 CHAR), C1 CHAR(3 CHAR), NS NVARCHAR2(5), N NUMBER)");
            await p.MustAsync($"ALTER TABLE {t} ADD (V1 AS (N * 2) VIRTUAL)");
            await p.MustAsync($"ALTER TABLE {t} ADD (V2 NUMBER AS (N + 1) VIRTUAL)");
            await p.MustAsync($"ALTER TABLE {t} ADD (Z DATE)");
            await p.MustAsync($"ALTER TABLE {t} DROP COLUMN S1");
            await p.MustAsync($"ALTER TABLE {t} ADD (S1 VARCHAR2(4))");

            // 여러 번 읽어도 같은 순서(INTERNAL_COLUMN_ID 는 늘 NULL 이라 그것으로 정렬하면 순서가 흔들린다).
            for (var i = 0; i < 5; i++)
            {
                var table = await RunAsync(session, "meta.columns", p.Owner, t);
                Assert.Equal(["ID", "S2", "C1", "NS", "N", "V1", "V2", "Z", "S1"], table.Text("column_name"));
                Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9"], table.Text("position"));
                Assert.Equal([null, "C", "C", "C", null, null, null, null, "B"], table.Text("char_used"));
            }
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>제약 행: FK 삭제 규칙·참조 열(위치 짝), PK·UK 가 만든 인덱스 이름, 검증 여부, 서버가 지은 이름인지.</summary>
    [TiberoWriteFact]
    public async Task Constraint_rows_carry_delete_rule_referenced_columns_index_and_generated_name()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var parent = T("KP");
        var child = T("KD");
        await p.DropTableAsync(child);
        await p.DropTableAsync(parent);
        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (A NUMBER, B NUMBER, U VARCHAR2(5), CONSTRAINT {T("KP_PK")} PRIMARY KEY (A, B), UNIQUE (U))");
            await p.MustAsync($"CREATE TABLE {child} (ID NUMBER, PA NUMBER, PB NUMBER, Q NUMBER, " +
                              $"CONSTRAINT {T("KD_FK1")} FOREIGN KEY (PA, PB) REFERENCES {parent} (A, B) ON DELETE CASCADE, " +
                              $"CONSTRAINT {T("KD_FK2")} FOREIGN KEY (Q, ID) REFERENCES {parent} (A, B) ON DELETE SET NULL, " +
                              $"CONSTRAINT {T("KD_CK")} CHECK (ID > 0))");
            await p.MustAsync($"ALTER TABLE {child} ADD CONSTRAINT {T("KD_FK3")} FOREIGN KEY (ID, Q) REFERENCES {parent} (A, B) ENABLE NOVALIDATE");

            var kids = await RunAsync(session, "meta.constraints", p.Owner, child);
            var fk1 = kids.Where("constraint_name", T("KD_FK1"));
            Assert.Equal(["PA", "PB"], fk1.Select(r => r["column_name"]));
            Assert.Equal(["A", "B"], fk1.Select(r => r["r_column_name"]));
            Assert.All(fk1, r => Assert.Equal("CASCADE", r["delete_rule"]));
            var fk2 = kids.Where("constraint_name", T("KD_FK2"));
            Assert.Equal(["Q", "ID"], fk2.Select(r => r["column_name"]));
            Assert.Equal(["A", "B"], fk2.Select(r => r["r_column_name"]));
            Assert.Equal("SET NULL", fk2[0]["delete_rule"]);
            var fk3 = kids.Where("constraint_name", T("KD_FK3"));
            Assert.Equal("NO ACTION", fk3[0]["delete_rule"]);
            Assert.Equal("NOT VALIDATED", fk3[0]["validated"]);
            Assert.Equal("VALIDATED", fk1[0]["validated"]);
            Assert.Equal("USER NAME", kids.Where("constraint_name", T("KD_CK"))[0]["generated"]);
            Assert.All(kids.Rows, r => Assert.Null(r["index_name"]));

            var parents = await RunAsync(session, "meta.constraints", p.Owner, parent);
            var pk = parents.Where("constraint_name", T("KP_PK"));
            Assert.Equal([T("KP_PK"), T("KP_PK")], pk.Select(r => r["index_name"])); // PK 인덱스 이름 = 제약 이름
            Assert.Equal("USER NAME", pk[0]["generated"]);
            var uk = Assert.Single(parents.Rows, r => r["constraint_type"] == "U");
            Assert.Equal("GENERATED NAME", uk["generated"]); // 서버 이름 <계정>_CON<n> (TE-8)
            Assert.Equal(uk["constraint_name"], uk["index_name"]);
            Assert.Null(uk["r_column_name"]);
            Assert.Null(uk["delete_rule"]);
        }
        finally
        {
            await p.DropTableAsync(child);
            await p.DropTableAsync(parent);
        }
    }

    /// <summary>인덱스 행: 열별 DESC, 함수 기반 식(서버 표기), 인덱스 테이블스페이스, PK·UK 가 만든 인덱스면 그 제약 이름.</summary>
    [TiberoWriteFact]
    public async Task Index_rows_carry_descend_expression_tablespace_and_owning_constraint()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("KI");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER CONSTRAINT {T("KI_PK")} PRIMARY KEY, A NUMBER, S VARCHAR2(10))");
            await p.MustAsync($"CREATE INDEX {T("KI_D")} ON {t} (A DESC, S)");
            await p.MustAsync($"CREATE INDEX {T("KI_F")} ON {t} (UPPER(S), A DESC)");

            var table = await RunAsync(session, "meta.indexes", p.Owner, t);
            var d = table.Where("index_name", T("KI_D"));
            Assert.Equal(["A", "S"], d.Select(r => r["column_name"]));
            Assert.Equal(["DESC", "ASC"], d.Select(r => r["descend"]));
            Assert.All(d, r => Assert.Null(r["column_expression"]));
            Assert.All(d, r => Assert.Null(r["constraint_name"]));

            var f = table.Where("index_name", T("KI_F"));
            Assert.Equal(["- EXPRESSION COLUMN -", "A"], f.Select(r => r["column_name"]));
            Assert.Equal(["UPPER(\"S\")", null], f.Select(r => r["column_expression"]));
            Assert.Equal(["ASC", "DESC"], f.Select(r => r["descend"]));
            Assert.Equal("FUNCTION-BASED NORMAL", f[0]["index_type"]);

            var pk = Assert.Single(table.Where("index_name", T("KI_PK")));
            Assert.Equal(T("KI_PK"), pk["constraint_name"]);
            Assert.All(table.Rows, r => Assert.False(string.IsNullOrEmpty(r["tablespace_name"]), "index tablespace is empty"));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>테이블 종류 행(<c>meta.table_kind</c>): 테이블스페이스·임시(DURATION)·IOT·파티션·외부 여부.</summary>
    [TiberoWriteFact]
    public async Task Table_kind_reads_temporary_iot_partitioned_and_heap()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        string[] tables = [T("KH"), T("KG"), T("KO"), T("KR")];
        foreach (var t in tables)
        {
            await p.DropTableAsync(t);
        }

        try
        {
            await p.MustAsync($"CREATE TABLE {tables[0]} (ID NUMBER)");
            await p.MustAsync($"CREATE GLOBAL TEMPORARY TABLE {tables[1]} (ID NUMBER) ON COMMIT PRESERVE ROWS");
            await p.MustAsync($"CREATE TABLE {tables[2]} (ID NUMBER PRIMARY KEY) ORGANIZATION INDEX");
            await p.MustAsync($"CREATE TABLE {tables[3]} (ID NUMBER) PARTITION BY RANGE (ID) (PARTITION P1 VALUES LESS THAN (10), PARTITION P2 VALUES LESS THAN (MAXVALUE))");

            var kinds = new List<IReadOnlyDictionary<string, string?>>();
            foreach (var t in tables)
            {
                var row = Assert.Single((await RunAsync(session, "meta.table_kind", p.Owner, t)).Rows);
                p.Log("kind", $"{t}: " + string.Join(", ", row.Select(kv => $"{kv.Key}={(kv.Key == "tablespace_name" ? (kv.Value is null ? "null" : "set") : kv.Value ?? "null")}")));
                kinds.Add(row);
            }

            Assert.Equal(["NO", "YES", "NO", "NO"], kinds.Select(k => k["temporary"]));
            Assert.Equal([null, "SESSION", null, null], kinds.Select(k => k["duration"]));
            Assert.Equal([null, null, "IOT", null], kinds.Select(k => k["iot_type"]));
            Assert.Equal(["NO", "NO", "NO", "YES"], kinds.Select(k => k["partitioned"]));
            Assert.Equal(["0", "0", "0", "0"], kinds.Select(k => k["external_count"]));
            Assert.False(string.IsNullOrEmpty(kinds[0]["tablespace_name"]), "heap table tablespace is empty");

            Assert.Empty((await RunAsync(session, "meta.table_kind", p.Owner, T("NO_SUCH"))).Rows);
        }
        finally
        {
            foreach (var t in tables)
            {
                await p.DropTableAsync(t);
            }
        }
    }

    /// <summary>메타데이터 공급자(<c>GetTableAsync</c>)가 실제 서버에서 TE-29 값을 채운다: 길이 기준·FK 규칙·참조 열·DESC·식·제약 인덱스·테이블 종류.</summary>
    [TiberoWriteFact]
    public async Task Metadata_provider_fills_the_table_edit_values_from_the_server()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var parent = T("MP");
        var child = T("MC");
        var gtt = T("MG");
        await p.DropTableAsync(child);
        await p.DropTableAsync(parent);
        await p.DropTableAsync(gtt);
        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (A NUMBER, B NUMBER, CONSTRAINT {T("MP_PK")} PRIMARY KEY (A, B))");
            await p.MustAsync($"CREATE TABLE {child} (ID NUMBER, S VARCHAR2(10 CHAR), C CHAR(2 BYTE), PA NUMBER, PB NUMBER, " +
                              $"CONSTRAINT {T("MC_FK")} FOREIGN KEY (PA, PB) REFERENCES {parent} (A, B) ON DELETE SET NULL)");
            await p.MustAsync($"CREATE INDEX {T("MC_D")} ON {child} (S DESC, ID)");
            await p.MustAsync($"CREATE INDEX {T("MC_F")} ON {child} (UPPER(S))");
            await p.MustAsync($"CREATE GLOBAL TEMPORARY TABLE {gtt} (ID NUMBER) ON COMMIT DELETE ROWS");

            var provider = TableEditProbe.NewProvider();
            var kid = await provider.GetTableAsync(session, new DbObjectRef(p.Owner, child, "TABLE"), CancellationToken.None);
            var dad = await provider.GetTableAsync(session, new DbObjectRef(p.Owner, parent, "TABLE"), CancellationToken.None);
            var temp = await provider.GetTableAsync(session, new DbObjectRef(p.Owner, gtt, "TABLE"), CancellationToken.None);

            Assert.Equal([null, CharSemantics.Characters, CharSemantics.Bytes, null, null], kid.Columns.Select(c => c.CharSemantics));
            var fk = Assert.Single(kid.Constraints);
            Assert.Equal(DeleteRule.SetNull, fk.DeleteRule);
            Assert.Equal(["A", "B"], fk.ReferencedColumns);
            var idx = kid.Indexes.ToDictionary(i => i.Name);
            Assert.Equal([true, false], idx[T("MC_D")].Descending);
            Assert.Equal(["UPPER(\"S\")"], idx[T("MC_F")].Expressions);
            Assert.False(string.IsNullOrEmpty(idx[T("MC_D")].Tablespace), "index tablespace is empty");
            Assert.Equal(T("MP_PK"), Assert.Single(dad.Indexes).ConstraintName);
            Assert.Equal((TableKind.Heap, false), (kid.Kind, kid.IsPartitioned));
            Assert.False(string.IsNullOrEmpty(kid.Tablespace), "table tablespace is empty");
            Assert.Equal(TableKind.Temporary, temp.Kind);
        }
        finally
        {
            await p.DropTableAsync(child);
            await p.DropTableAsync(parent);
            await p.DropTableAsync(gtt);
        }
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

    private static async Task<NamedRows> RunAsync(TiberoSession session, string id, string owner, string table)
    {
        var result = await TiberoCatalogQuery.RunAsync(session, id, TiberoCatalogQuery.TableBinds(new DbObjectRef(owner, table, "TABLE")), CancellationToken.None);
        return new NamedRows(result);
    }

    /// <summary>카탈로그 결과를 열 이름(소문자)으로 읽는다. 없는 열을 찾으면 그 이름을 담아 실패한다.</summary>
    private sealed class NamedRows
    {
        public NamedRows(TiberoSessionQuery.Table table)
        {
            var names = table.Columns.Select(c => c.ToLowerInvariant()).ToList();
            Rows = [.. table.Rows.Select(r => (IReadOnlyDictionary<string, string?>)new Row(names, r))];
        }

        public IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows { get; }

        public List<string?> Text(string column) => [.. Rows.Select(r => r[column])];

        public List<IReadOnlyDictionary<string, string?>> Where(string column, string value) =>
            [.. Rows.Where(r => string.Equals(r[column], value, StringComparison.Ordinal))];
    }

    private sealed class Row : Dictionary<string, string?>
    {
        public Row(IReadOnlyList<string> names, object?[] values)
            : base(StringComparer.Ordinal)
        {
            for (var i = 0; i < names.Count; i++)
            {
                this[names[i]] = TiberoSessionQuery.Str(values[i]);
            }
        }
    }
}
