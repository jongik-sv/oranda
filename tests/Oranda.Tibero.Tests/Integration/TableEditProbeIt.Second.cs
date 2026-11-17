#pragma warning disable CA1861 // 시험 입력용 상수 배열
namespace Oranda.Tibero.Tests.Integration;

/// <summary>테이블 편집기 Tibero 실측 2차(11~18): 파티션·저장 속성·LOB 저장 절·테이블 이름·신원/숨김 열·도메인·함수 기반/비트맵 인덱스·GTT/IOT·휴지통.</summary>
public partial class TableEditProbeIt
{
    /// <summary>11. 파티션: RANGE/LIST/HASH/INTERVAL/복합 CREATE, 파티션 관리 문장, 카탈로그 뷰.</summary>
    [TiberoWriteFact]
    public async Task P11_partitions()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var range = T("PR");
        var list = T("PL");
        var hash = T("PH");
        var interval = T("PI");
        var composite = T("PC");
        string[] all = [range, list, hash, interval, composite];
        foreach (var t in all)
        {
            await p.DropTableAsync(t);
        }

        try
        {
            await p.ExpectAsync("11 range", "OK", $"CREATE TABLE {range} (ID NUMBER, D DATE) PARTITION BY RANGE (ID) (PARTITION P1 VALUES LESS THAN (100), PARTITION P2 VALUES LESS THAN (200))");
            await p.MustAsync($"INSERT INTO {range} VALUES (50, SYSDATE)");
            await p.MustAsync($"INSERT INTO {range} VALUES (150, SYSDATE)");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("11 add", "OK", $"ALTER TABLE {range} ADD PARTITION P3 VALUES LESS THAN (300)");
            await p.ExpectAsync("11 split", "OK", $"ALTER TABLE {range} SPLIT PARTITION P3 AT (250) INTO (PARTITION P3A, PARTITION P3B)");
            await p.ExpectAsync("11 merge", "OK", $"ALTER TABLE {range} MERGE PARTITIONS P3A, P3B INTO PARTITION P3");
            await p.ExpectAsync("11 rename", "OK", $"ALTER TABLE {range} RENAME PARTITION P3 TO P3X");
            await p.ExpectAsync("11 truncate", "OK", $"ALTER TABLE {range} TRUNCATE PARTITION P1");
            await p.ExpectAsync("11 drop", "OK", $"ALTER TABLE {range} DROP PARTITION P3X");
            await p.ExpectAsync("11 add-maxvalue", "OK", $"ALTER TABLE {range} ADD PARTITION PMAX VALUES LESS THAN (MAXVALUE)");
            Assert.Equal("1", await p.ScalarAsync($"SELECT COUNT(*) FROM {range}")); // TRUNCATE PARTITION P1 이 50 을 지우고 150 이 남는다

            await p.ExpectAsync("11 list", "OK", $"CREATE TABLE {list} (ID NUMBER, C VARCHAR2(5)) PARTITION BY LIST (C) (PARTITION PA VALUES ('A', 'B'), PARTITION PD VALUES (DEFAULT))");
            await p.ExpectAsync("11 list split", "OK", $"ALTER TABLE {list} SPLIT PARTITION PD VALUES ('C') INTO (PARTITION PC, PARTITION PD2)");
            await p.ExpectAsync("11 list add-values", "TBR-7001", $"ALTER TABLE {list} MODIFY PARTITION PA ADD VALUES ('Z')");

            await p.ExpectAsync("11 hash", "OK", $"CREATE TABLE {hash} (ID NUMBER) PARTITION BY HASH (ID) PARTITIONS 4");
            await p.ExpectAsync("11 hash add", "TBR-7002", $"ALTER TABLE {hash} ADD PARTITION");
            await p.ExpectAsync("11 hash coalesce", "TBR-7001", $"ALTER TABLE {hash} COALESCE PARTITION");

            await p.ExpectAsync("11 interval", "OK", $"CREATE TABLE {interval} (ID NUMBER, D DATE) PARTITION BY RANGE (D) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION P0 VALUES LESS THAN (DATE '2026-01-01'))");
            await p.ExpectAsync("11 interval insert", "OK", $"INSERT INTO {interval} VALUES (1, DATE '2026-03-15')");
            await p.SilentAsync("COMMIT");

            await p.ExpectAsync("11 hash add named", "TBR-7002", $"ALTER TABLE {hash} ADD PARTITION P5");
            await p.ExpectAsync("11 range-hash", "TBR-7001", $"CREATE TABLE {composite} (ID NUMBER, K NUMBER) PARTITION BY RANGE (ID) SUBPARTITION BY HASH (K) SUBPARTITIONS 2 (PARTITION P1 VALUES LESS THAN (100), PARTITION P2 VALUES LESS THAN (MAXVALUE))");
            await p.ExpectAsync("11 range-hash explicit", "OK", $"CREATE TABLE {composite} (ID NUMBER, K NUMBER) PARTITION BY RANGE (ID) SUBPARTITION BY HASH (K) (PARTITION P1 VALUES LESS THAN (100) (SUBPARTITION P1S1, SUBPARTITION P1S2), PARTITION P2 VALUES LESS THAN (MAXVALUE) (SUBPARTITION P2S1, SUBPARTITION P2S2))");
            var subs = await p.DumpAsync("11 sub", $"SELECT TABLE_NAME, PARTITION_NAME, SUBPARTITION_NAME, SUBPARTITION_NO, BOUND FROM USER_TAB_SUBPARTITIONS WHERE TABLE_NAME = '{composite}' ORDER BY PARTITION_NAME, SUBPARTITION_NO");
            await p.ExpectAsync("11 exchange", "TBR-7002", $"ALTER TABLE {range} EXCHANGE PARTITION P1 WITH TABLE {hash}");

            var views = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
            foreach (var view in new[] { "USER_PART_TABLES", "USER_TAB_PARTITIONS", "USER_PART_KEY_COLUMNS", "USER_TAB_SUBPARTITIONS", "USER_SUBPART_KEY_COLUMNS", "USER_PART_INDEXES", "USER_IND_PARTITIONS" })
            {
                var columns = await p.ColumnsOfAsync(view);
                views[view] = columns;
                p.Log("11 view", $"{view} = " + (columns is null ? "(no view)" : string.Join(",", columns)));
            }

            var inList = string.Join(", ", all.Select(t => $"'{t}'"));
            var kinds = await p.DumpAsync("11", $"SELECT TABLE_NAME, PARTITIONING_TYPE, SUBPARTITIONING_TYPE, PARTITION_COUNT FROM USER_PART_TABLES WHERE TABLE_NAME IN ({inList}) ORDER BY TABLE_NAME");
            var parts = await p.DumpAsync("11", $"SELECT TABLE_NAME, PARTITION_NAME, PARTITION_NO, BOUND, COMPOSITE, SUBPARTITION_COUNT FROM USER_TAB_PARTITIONS WHERE TABLE_NAME IN ({inList}) ORDER BY TABLE_NAME, PARTITION_NO");
            await p.DumpAsync("11", $"SELECT NAME, OBJECT_TYPE, COLUMN_NAME, COLUMN_POSITION FROM USER_PART_KEY_COLUMNS WHERE NAME IN ({inList}) ORDER BY NAME, COLUMN_POSITION");
            var step = await p.ScalarAsync($"SELECT INTERVAL FROM USER_PART_TABLES WHERE TABLE_NAME = '{interval}'");

            // 뷰는 모두 있으나 열 이름이 Oracle 과 다르다: 경계 = BOUND(Oracle HIGH_VALUE), 순번 = PARTITION_NO(Oracle PARTITION_POSITION).
            Assert.All(views.Values, v => Assert.NotNull(v));
            Assert.Contains("BOUND", views["USER_TAB_PARTITIONS"]!);
            Assert.Contains("PARTITION_NO", views["USER_TAB_PARTITIONS"]!);
            Assert.DoesNotContain("HIGH_VALUE", views["USER_TAB_PARTITIONS"]!);
            Assert.Equal(
                [[composite, "RANGE", "HASH", "2"], [hash, "HASH", "NONE", "4"], [interval, "RANGE", "NONE", "2"], [list, "LIST", "NONE", "3"], [range, "RANGE", "NONE", "3"]],
                kinds.OrderBy(r => r[0], StringComparer.Ordinal).Select(r => r.ToArray()));
            Assert.Equal(["100", "200", "MAXVALUE"], parts.Where(r => r[0] == range).Select(r => r[3]));
            Assert.Equal(["'A','B'", "'C'", "DEFAULT"], parts.Where(r => r[0] == list).Select(r => r[3]));
            Assert.Equal(4, subs.Count);
            Assert.Equal("NUMTOYMINTERVAL(1, 'MONTH')", step);

            // 해시 파티션 이름을 주지 않으면 서버가 <계정>_P<숫자> 를 붙이고, INTERVAL 로 생긴 파티션 경계는 TO_DATE(…) 꼴이다.
            Assert.True(parts.Where(r => r[0] == hash).All(r => r[1]!.StartsWith(p.Owner + "_P", StringComparison.Ordinal)), "hash partitions are not server-named");
            Assert.StartsWith("TO_DATE('01-04-2026 00:00:00'", parts.Single(r => r[0] == interval && r[2] == "2")[3], StringComparison.Ordinal);
        }
        finally
        {
            foreach (var t in all)
            {
                await p.DropTableAsync(t);
            }
        }
    }

    /// <summary>12. 저장 속성: PCTFREE·INITRANS·STORAGE·COMPRESS·TABLESPACE 의 CREATE, ALTER … PCTFREE, MOVE, 카탈로그 열.</summary>
    [TiberoWriteFact]
    public async Task P12_storage_attributes()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("ST");
        var t2 = T("ST2");
        await p.DropTableAsync(t);
        await p.DropTableAsync(t2);
        try
        {
            var ts = await p.DefaultTablespaceAsync();
            await p.ExpectAsync("12 create", "OK", $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, S VARCHAR2(10)) PCTFREE 20 INITRANS 4 STORAGE (INITIAL 64K NEXT 64K) TABLESPACE {ts}");
            const string Attrs = "SELECT PCT_FREE, INI_TRANS, INITIAL_EXTENT, NEXT_EXTENT, COMPRESSION, LOGGING FROM USER_TABLES WHERE TABLE_NAME = ";
            var afterCreate = await p.DumpAsync("12 after create", $"{Attrs}'{t}'");
            await p.ExpectAsync("12 compress", "OK", $"CREATE TABLE {t2} (ID NUMBER) COMPRESS");
            await p.ExpectAsync("12 pctused", "OK", $"ALTER TABLE {t} PCTUSED 50");
            await p.ExpectAsync("12 alter pctfree", "OK", $"ALTER TABLE {t} PCTFREE 30");
            var afterPctfree = await p.DumpAsync("12 after pctfree", $"{Attrs}'{t}'");
            await p.ExpectAsync("12 alter initrans", "OK", $"ALTER TABLE {t} INITRANS 6");
            await p.ExpectAsync("12 alter storage", "OK", $"ALTER TABLE {t} STORAGE (NEXT 128K)");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 'a')");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("12 move", "TBR-7001", $"ALTER TABLE {t} MOVE");
            await p.ExpectAsync("12 move tablespace", "OK", $"ALTER TABLE {t} MOVE TABLESPACE {ts}");
            var indexAfterMove = await p.ScalarAsync($"SELECT STATUS FROM USER_INDEXES WHERE TABLE_NAME = '{t}'");
            p.Log("12 index after move", indexAfterMove ?? "(null)");
            await p.DumpAsync("12 after move", $"{Attrs}'{t}'");
            var pkIndex = await p.ScalarAsync($"SELECT INDEX_NAME FROM USER_INDEXES WHERE TABLE_NAME = '{t}'");
            await p.ExpectAsync("12 rebuild", "OK", $"ALTER INDEX \"{pkIndex}\" REBUILD");
            await p.ExpectAsync("12 move pctfree", "OK", $"ALTER TABLE {t} MOVE TABLESPACE {ts} PCTFREE 25");
            var afterMovePctfree = await p.DumpAsync("12 after move pctfree", $"{Attrs}'{t}'");
            await p.ExpectAsync("12 move compress", "OK", $"ALTER TABLE {t} MOVE COMPRESS");
            await p.ExpectAsync("12 nocompress", "OK", $"ALTER TABLE {t} NOCOMPRESS");
            await p.ExpectAsync("12 logging", "OK", $"ALTER TABLE {t} NOLOGGING");

            var columns = await p.ColumnsOfAsync("USER_TABLES");
            p.Log("12 view", "USER_TABLES = " + (columns is null ? "(no view)" : string.Join(",", columns)));
            Assert.NotNull(columns);
            var final = await p.DumpAsync("12", $"SELECT TABLE_NAME, PCT_FREE, INI_TRANS, INITIAL_EXTENT, NEXT_EXTENT, COMPRESSION, LOGGING FROM USER_TABLES WHERE TABLE_NAME IN ('{t}', '{t2}') ORDER BY TABLE_NAME");
            var segColumns = await p.ColumnsOfAsync("USER_SEGMENTS");
            p.Log("12 view", "USER_SEGMENTS = " + (segColumns is null ? "(no view)" : string.Join(",", segColumns)));

            // INITIAL/NEXT 64K 는 131072 로 올라간다(최소 익스텐트). PCTFREE·INITRANS 는 그대로 읽힌다. USER_TABLES 에 PCT_USED·MAX_TRANS 열이 없다.
            Assert.Equal(["20", "4", "131072", "131072", "NO", "YES"], Assert.Single(afterCreate));
            Assert.Equal("30", Assert.Single(afterPctfree)[0]);
            Assert.Equal("25", Assert.Single(afterMovePctfree)[0]);
            Assert.Equal("UNUSABLE", indexAfterMove); // MOVE 뒤 인덱스 = UNUSABLE (Oracle 과 같음) → REBUILD 필요
            Assert.Equal("0", final.Single(r => r[0] == t)[1]); // MOVE COMPRESS 뒤 PCT_FREE 0
            Assert.Equal(["YES", "YES"], final.Single(r => r[0] == t2)[5..7]); // COMPRESS 로 만든 표 = COMPRESSION YES
            Assert.Equal("NO", final.Single(r => r[0] == t)[6]); // NOLOGGING
            Assert.DoesNotContain("PCT_USED", columns);
            Assert.DoesNotContain("MAX_TRANS", columns);
        }
        finally
        {
            await p.DropTableAsync(t);
            await p.DropTableAsync(t2);
        }
    }

    /// <summary>13. LOB 저장 절: STORE AS (TABLESPACE … ENABLE STORAGE IN ROW CHUNK …), SECUREFILE/BASICFILE, 세그먼트 이름, USER_LOBS.</summary>
    [TiberoWriteFact]
    public async Task P13_lob_storage_clause()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var ts = await p.DefaultTablespaceAsync();
        string[] tables = [T("LB1"), T("LB2"), T("LB3"), T("LB4"), T("LB5")];
        foreach (var t in tables)
        {
            await p.DropTableAsync(t);
        }

        try
        {
            await p.ExpectAsync("13 store-as", "OK", $"CREATE TABLE {tables[0]} (ID NUMBER, C CLOB) LOB (C) STORE AS (TABLESPACE {ts} ENABLE STORAGE IN ROW CHUNK 8192)");
            await p.ExpectAsync("13 named segment", "OK", $"CREATE TABLE {tables[1]} (ID NUMBER, C CLOB) LOB (C) STORE AS {T("LB2_SEG")} (DISABLE STORAGE IN ROW)");
            await p.ExpectAsync("13 securefile", "OK", $"CREATE TABLE {tables[2]} (ID NUMBER, C BLOB) LOB (C) STORE AS SECUREFILE (TABLESPACE {ts})");
            await p.ExpectAsync("13 basicfile", "OK", $"CREATE TABLE {tables[3]} (ID NUMBER, C BLOB) LOB (C) STORE AS BASICFILE (TABLESPACE {ts})");
            await p.MustAsync($"CREATE TABLE {tables[4]} (ID NUMBER)");
            await p.ExpectAsync("13 add lob with store", "OK", $"ALTER TABLE {tables[4]} ADD (C CLOB) LOB (C) STORE AS (DISABLE STORAGE IN ROW)");
            await p.ExpectAsync("13 modify lob", "TBR-7001", $"ALTER TABLE {tables[4]} MODIFY LOB (C) (CACHE)");
            await p.ExpectAsync("13 move lob", "OK", $"ALTER TABLE {tables[4]} MOVE LOB (C) STORE AS (TABLESPACE {ts})");

            var columns = await p.ColumnsOfAsync("USER_LOBS");
            p.Log("13 view", "USER_LOBS = " + (columns is null ? "(no view)" : string.Join(",", columns)));
            var inList = string.Join(", ", tables.Select(t => $"'{t}'"));
            var lobs = await p.DumpAsync("13", $"SELECT TABLE_NAME, COLUMN_NAME, SEGMENT_NAME, IN_ROW FROM USER_LOBS WHERE TABLE_NAME IN ({inList}) ORDER BY TABLE_NAME");
            var lob = lobs.ToDictionary(r => r[0]!);

            // SECUREFILE/BASICFILE 낱말은 받지만 LOB 세그먼트 이름으로 읽힌다(저장 방식이 아님). USER_LOBS 에 CHUNK·CACHE·SECUREFILE 열이 없다.
            Assert.NotNull(columns);
            Assert.DoesNotContain("CHUNK", columns);
            Assert.DoesNotContain("SECUREFILE", columns);
            Assert.Equal(["SECUREFILE", "BASICFILE", T("LB2_SEG")], new[] { tables[2], tables[3], tables[1] }.Select(t => lob[t][2]));
            Assert.Equal(["YES", "NO", "YES"], new[] { tables[0], tables[1], tables[4] }.Select(t => lob[t][3])); // MOVE LOB 의 STORE AS 가 IN ROW 를 기본(켬)으로 되돌린다
            Assert.True(lob[tables[0]][2]!.StartsWith(p.Owner + "_LOB", StringComparison.Ordinal), "unnamed LOB segment is not <owner>_LOB<n>");
        }
        finally
        {
            foreach (var t in tables)
            {
                await p.DropTableAsync(t);
            }
        }
    }

    /// <summary>14. 테이블 이름 바꾸기: ALTER TABLE … RENAME TO, RENAME a TO b, 소유자 붙인 꼴, 딸린 인덱스·제약 이름.</summary>
    [TiberoWriteFact]
    public async Task P14_rename_table()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var a = T("RA");
        var b = T("RB");
        var c = T("RC");
        var d = T("RD");
        string[] all = [a, b, c, d];
        foreach (var t in all)
        {
            await p.DropTableAsync(t);
        }

        try
        {
            await p.MustAsync($"CREATE TABLE {a} (ID NUMBER CONSTRAINT {T("RA_PK")} PRIMARY KEY)");
            await p.ExpectAsync("14 alter rename", "OK", $"ALTER TABLE {p.Owner}.{a} RENAME TO {b}");
            await p.ExpectAsync("14 alter rename owner target", "TBR-7001", $"ALTER TABLE {p.Owner}.{b} RENAME TO {p.Owner}.{c}");
            await p.ExpectAsync("14 rename", "OK", $"RENAME {b} TO {c}");
            await p.ExpectAsync("14 rename owner", "TBR-7001", $"RENAME {p.Owner}.{c} TO {d}");
            var names = await p.DumpAsync("14", $"SELECT TABLE_NAME FROM USER_TABLES WHERE TABLE_NAME IN ('{a}', '{b}', '{c}', '{d}')");
            var pk = await p.DumpAsync("14", $"SELECT CONSTRAINT_NAME, TABLE_NAME FROM USER_CONSTRAINTS WHERE CONSTRAINT_NAME = '{T("RA_PK")}'");

            Assert.Equal(c, Assert.Single(Assert.Single(names)));
            Assert.Equal([T("RA_PK"), c], Assert.Single(pk)); // 제약 이름은 그대로 따라간다
        }
        finally
        {
            foreach (var t in all)
            {
                await p.DropTableAsync(t);
            }
        }
    }

    /// <summary>15. 신원 열(GENERATED … AS IDENTITY) 생성·변경, 숨김 열(INVISIBLE), 도메인.</summary>
    [TiberoWriteFact]
    public async Task P15_identity_invisible_domain()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        string[] tables = [T("ID1"), T("ID2"), T("ID3"), T("IV1"), T("IV2")];
        foreach (var t in tables)
        {
            await p.DropTableAsync(t);
        }

        var domain = T("DOM");
        try
        {
            await p.ExpectAsync("15 identity always", "TBR-7001", $"CREATE TABLE {tables[0]} (ID NUMBER GENERATED ALWAYS AS IDENTITY, N NUMBER)");
            await p.ExpectAsync("15 identity by default", "TBR-7001", $"CREATE TABLE {tables[1]} (ID NUMBER GENERATED BY DEFAULT AS IDENTITY (START WITH 10 INCREMENT BY 5), N NUMBER)");
            await p.ExpectAsync("15 identity on null", "TBR-7001", $"CREATE TABLE {tables[2]} (ID NUMBER GENERATED BY DEFAULT ON NULL AS IDENTITY, N NUMBER)");
            await p.MustAsync($"CREATE TABLE {tables[3]} (ID NUMBER, N NUMBER)");
            await p.ExpectAsync("15 add identity", "TBR-7001", $"ALTER TABLE {tables[3]} ADD (K NUMBER GENERATED ALWAYS AS IDENTITY)");
            await p.ExpectAsync("15 modify identity", "TBR-7001", $"ALTER TABLE {tables[0]} MODIFY (ID GENERATED BY DEFAULT AS IDENTITY)");
            var identityColumns = await p.ColumnsOfAsync("USER_TAB_IDENTITY_COLS");
            p.Log("15 view", "USER_TAB_IDENTITY_COLS = " + (identityColumns is null ? "(no view)" : string.Join(",", identityColumns)));
            var tabCols = await p.ColumnsOfAsync("USER_TAB_COLS");
            p.Log("15 view", "USER_TAB_COLS has IDENTITY_COLUMN = " + (tabCols?.Contains("IDENTITY_COLUMN") == true) + ", HIDDEN_COLUMN = " + (tabCols?.Contains("HIDDEN_COLUMN") == true) + ", USER_GENERATED = " + (tabCols?.Contains("USER_GENERATED") == true));

            await p.ExpectAsync("15 invisible create", "TBR-7001", $"CREATE TABLE {tables[4]} (ID NUMBER, H NUMBER INVISIBLE)");
            await p.ExpectAsync("15 invisible modify", "TBR-7001", $"ALTER TABLE {tables[3]} MODIFY (N INVISIBLE)");
            await p.ExpectAsync("15 visible modify", "TBR-7001", $"ALTER TABLE {tables[3]} MODIFY (N VISIBLE)");

            await p.ExpectAsync("15 domain", "TBR-7001", $"CREATE DOMAIN {domain} AS VARCHAR2(10)");

            // 신원 열·숨김 열·도메인 모두 문법부터 없다(7001). 사전에도 신원 열 뷰·IDENTITY_COLUMN 열이 없다.
            Assert.Null(identityColumns);
            Assert.False(tabCols?.Contains("IDENTITY_COLUMN") == true);
        }
        finally
        {
            foreach (var t in tables)
            {
                await p.DropTableAsync(t);
            }

            await p.SilentAsync($"DROP DOMAIN {domain}");
        }
    }

    /// <summary>16. 함수 기반 인덱스(UPPER(c))·비트맵 인덱스, 카탈로그 표기.</summary>
    [TiberoWriteFact]
    public async Task P16_function_based_and_bitmap_indexes()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("FX");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, S VARCHAR2(10), N NUMBER)");
            await p.ExpectAsync("16 function", "OK", $"CREATE INDEX {T("FX_F")} ON {t} (UPPER(S))");
            await p.ExpectAsync("16 function expr", "OK", $"CREATE INDEX {T("FX_E")} ON {t} (N + 1, ID)");
            await p.ExpectAsync("16 function desc", "OK", $"CREATE INDEX {T("FX_FD")} ON {t} (LOWER(S) DESC)");
            await p.ExpectAsync("16 bitmap", "OK", $"CREATE BITMAP INDEX {T("FX_B")} ON {t} (N)");
            var types = await p.DumpAsync("16", $"SELECT INDEX_NAME, INDEX_TYPE, UNIQUENESS FROM USER_INDEXES WHERE TABLE_NAME = '{t}' ORDER BY INDEX_NAME");
            var cols = await p.DumpAsync("16", $"SELECT INDEX_NAME, COLUMN_NAME, COLUMN_POSITION, DESCEND FROM USER_IND_COLUMNS WHERE TABLE_NAME = '{t}' ORDER BY INDEX_NAME, COLUMN_POSITION");
            var exprs = await p.DumpAsync("16", $"SELECT INDEX_NAME, COLUMN_POSITION, COLUMN_EXPRESSION FROM USER_IND_EXPRESSIONS WHERE TABLE_NAME = '{t}' ORDER BY INDEX_NAME, COLUMN_POSITION");
            var info = await p.DumpTableInfoAsync("16", t);

            Assert.Equal(["BITMAP", "FUNCTION-BASED NORMAL", "FUNCTION-BASED NORMAL", "FUNCTION-BASED NORMAL"], types.Select(r => r[1]));

            // 식 자리 열 이름 = 「- EXPRESSION COLUMN -」(Oracle 은 SYS_NC…$). 식은 USER_IND_EXPRESSIONS 에 서버 표기로.
            Assert.Equal(["- EXPRESSION COLUMN -", "ID"], cols.Where(r => r[0] == T("FX_E")).Select(r => r[1]));
            Assert.Equal("DESC", cols.Single(r => r[0] == T("FX_FD"))[3]);
            Assert.Equal(["(\"N\" + 1)", "UPPER(\"S\")", "LOWER(\"S\")"], exprs.Select(r => r[2]));
            Assert.Equal(["- EXPRESSION COLUMN -"], info.Indexes.Single(i => i.Name == T("FX_F")).Columns); // 공급자는 식을 모른다
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>17. GTT(ON COMMIT DELETE/PRESERVE ROWS)·IOT(ORGANIZATION INDEX) 생성, 카탈로그 표기.</summary>
    [TiberoWriteFact]
    public async Task P17_global_temporary_and_index_organized_tables()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        string[] tables = [T("GD"), T("GP"), T("IO")];
        foreach (var t in tables)
        {
            await p.DropTableAsync(t);
        }

        try
        {
            await p.ExpectAsync("17 gtt delete", "OK", $"CREATE GLOBAL TEMPORARY TABLE {tables[0]} (ID NUMBER) ON COMMIT DELETE ROWS");
            await p.ExpectAsync("17 gtt preserve", "OK", $"CREATE GLOBAL TEMPORARY TABLE {tables[1]} (ID NUMBER) ON COMMIT PRESERVE ROWS");
            await p.ExpectAsync("17 iot", "OK", $"CREATE TABLE {tables[2]} (ID NUMBER, N NUMBER, CONSTRAINT {T("IO_PK")} PRIMARY KEY (ID)) ORGANIZATION INDEX");
            await p.ExpectAsync("17 gtt add column", "OK", $"ALTER TABLE {tables[1]} ADD (N NUMBER)");
            await p.ExpectAsync("17 iot add column", "OK", $"ALTER TABLE {tables[2]} ADD (M NUMBER)");
            var inList = string.Join(", ", tables.Select(t => $"'{t}'"));
            var kinds = await p.DumpAsync("17", $"SELECT TABLE_NAME, TEMPORARY, DURATION, IOT_TYPE FROM USER_TABLES WHERE TABLE_NAME IN ({inList}) ORDER BY TABLE_NAME");
            var iot = await p.DumpAsync("17", $"SELECT INDEX_NAME, INDEX_TYPE FROM USER_INDEXES WHERE TABLE_NAME = '{tables[2]}'");

            Assert.Equal(
                [[tables[0], "YES", "TRANSACTION", null], [tables[1], "YES", "SESSION", null], [tables[2], "NO", null, "IOT"]],
                kinds.Select(r => r.ToArray()));
            Assert.Equal([T("IO_PK"), "IOT - TOP"], Assert.Single(iot));
        }
        finally
        {
            foreach (var t in tables)
            {
                await p.DropTableAsync(t);
            }
        }
    }

    /// <summary>18. 휴지통: DROP TABLE(PURGE 없이) 뒤 되살리기, DROP COLUMN 뒤 되돌림 질의. 남의 휴지통은 건드리지 않는다(내 표만 PURGE TABLE).</summary>
    [TiberoWriteFact]
    public async Task P18_recycle_bin()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("RB");
        var t2 = T("RB2");
        await p.DropTableAsync(t);
        await p.DropTableAsync(t2);
        try
        {
            var recycle = await p.ScalarAsync("SELECT VALUE FROM V$PARAMETERS WHERE NAME = 'USE_RECYCLEBIN'");
            p.Log("18", "USE_RECYCLEBIN = " + TableEditProbe.Show(recycle));
            var rbColumns = await p.ColumnsOfAsync("USER_RECYCLEBIN");
            p.Log("18 view", "USER_RECYCLEBIN = " + (rbColumns is null ? "(no view)" : string.Join(",", rbColumns)));
            var enabled = recycle == "YES";

            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, N NUMBER)");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 1)");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("18 drop", "OK", $"DROP TABLE {t}");
            var inBin = await p.ScalarAsync($"SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME = '{t}'");
            p.Log("18", "in recycle bin = " + inBin);

            // 휴지통이 꺼져 있으면(기본) DROP 은 영구다: 되살리기는 「객체 없음」 7071.
            await p.ExpectAsync("18 flashback", enabled ? "OK" : "TBR-7071", $"FLASHBACK TABLE {t} TO BEFORE DROP");

            await p.MustAsync($"CREATE TABLE {t2} (ID NUMBER, N NUMBER)");
            await p.MustAsync($"INSERT INTO {t2} VALUES (1, 1)");
            await p.MustAsync("COMMIT");
            var stamp = await p.ScalarAsync("SELECT TO_CHAR(SYSTIMESTAMP, 'YYYY-MM-DD HH24:MI:SS.FF6') FROM DUAL");
            await p.ExpectAsync("18 drop column", "OK", $"ALTER TABLE {t2} DROP COLUMN N");

            // DROP COLUMN 뒤 그 앞 시점 읽기: Oracle 은 오류(ORA-01466)지만 Tibero 는 오류 없이 0행을 준다 — 되돌림 수단이 아니다.
            var asOf = await p.DumpAsync("18 as-of after drop column", $"SELECT * FROM {t2} AS OF TIMESTAMP TO_TIMESTAMP('{stamp}', 'YYYY-MM-DD HH24:MI:SS.FF6')");
            await p.ExpectAsync("18 set unused", "TBR-7001", $"ALTER TABLE {t2} SET UNUSED (ID)");

            Assert.NotNull(rbColumns);
            Assert.Equal(enabled ? "1" : "0", inBin);
            Assert.Empty(asOf);
        }
        finally
        {
            await p.DropTableAsync(t);
            await p.DropTableAsync(t2);

            // 휴지통이 켜져 있었다면 내 표만 비운다(PURGE RECYCLEBIN 전체 금지).
            await p.SilentAsync($"PURGE TABLE {t}");
        }
    }
}
