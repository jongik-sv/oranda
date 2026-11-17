#pragma warning disable CA1861 // 시험 입력용 상수 배열
using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Lexing;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 테이블 편집기 Tibero 실측(설계서 docs/superpowers/specs/2026-11-17-table-editor-design.md 12장, 결과 = docs/design/tibero-table-edit-probe.md).
/// 문장마다 성공·실패·오류 번호·<see cref="DbErrorKind"/>(기존 분류기)·카탈로그 표기를 단언하거나 시험 출력에 남긴다. Tibero 방언의 회귀 시험이다.
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>. 없으면 건너뛴다(<see cref="TiberoWriteFactAttribute"/>).</item>
/// <item>공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다(<see cref="TableEditProbe"/>).</item>
/// </list>
/// 1~10 = 설계서 12장, 11~18 = 2차 항목(<c>TableEditProbeIt.Second.cs</c>).
/// </summary>
[Trait("Category", "Integration")]
public partial class TableEditProbeIt(ITestOutputHelper output)
{
    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    private async Task<TableEditProbe> OpenAsync(TiberoItSessions it)
    {
        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(owner), "the session user of the connection is unknown");
        output.WriteLine($"[server] Tibero major version {session.Server.MajorVersion}");
        return new TableEditProbe(session, output, owner!.ToUpperInvariant(), it.Env.DbName);
    }

    /// <summary>1. 가상 열 추가(형식 생략·명시·VIRTUAL 생략)와 가상 식 변경.</summary>
    [TiberoWriteFact]
    public async Task P01_virtual_columns_add_and_modify_expression()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("VC");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, A NUMBER, B VARCHAR2(10))");
            await p.MustAsync($"INSERT INTO {t} (ID, A, B) VALUES (1, 10, 'x')");
            await p.MustAsync("COMMIT");

            await p.ExpectAsync("1 add no type", "OK", $"ALTER TABLE {t} ADD (V1 AS (A * 2) VIRTUAL)");
            await p.ExpectAsync("1 add typed", "OK", $"ALTER TABLE {t} ADD (V2 NUMBER AS (A + 1) VIRTUAL)");
            await p.ExpectAsync("1 add no VIRTUAL", "OK", $"ALTER TABLE {t} ADD (V3 AS (UPPER(B)))");
            await p.ExpectAsync("1 add GENERATED ALWAYS", "OK", $"ALTER TABLE {t} ADD (V4 NUMBER GENERATED ALWAYS AS (A - 1) VIRTUAL)");
            var values = await p.DumpAsync("1", $"SELECT V1, V2, V3, V4 FROM {t}");

            // 가상 식 변경은 Tibero 가 받지 않는다(형식 있든 없든) → 방언은 DROP + ADD 와 ⚠ (설계서 8.2).
            await p.ExpectAsync("1 modify expr", "TBR-8081", $"ALTER TABLE {t} MODIFY (V1 AS (A * 3))");
            await p.ExpectAsync("1 modify typed expr", "TBR-8081", $"ALTER TABLE {t} MODIFY (V2 NUMBER AS (A + 2) VIRTUAL)");
            await p.ExpectAsync("1 bad expr", "TBR-8026", $"ALTER TABLE {t} ADD (V5 AS (NO_SUCH_COL + 1) VIRTUAL)");
            await p.ExpectAsync("1 insert virtual", "TBR-8143", $"INSERT INTO {t} (ID, V1) VALUES (2, 5)");
            await p.SilentAsync("ROLLBACK");
            await p.ExpectAsync("1 drop virtual", "OK", $"ALTER TABLE {t} DROP COLUMN V4");

            // *_TAB_COLS 의 INTERNAL_COLUMN_ID 는 늘 NULL, COLUMN_ID 는 0부터다 — ORDER BY INTERNAL_COLUMN_ID 는 순서를 정하지 못한다.
            var ids = await p.DumpAsync("1", $"SELECT COLUMN_NAME, COLUMN_ID, INTERNAL_COLUMN_ID FROM USER_TAB_COLS WHERE TABLE_NAME = '{t}' ORDER BY COLUMN_ID");
            await p.DumpAsync("1 all_tab_cols", $"SELECT COLUMN_NAME, COLUMN_ID, INTERNAL_COLUMN_ID FROM ALL_TAB_COLS WHERE OWNER = '{p.Owner}' AND TABLE_NAME = '{t}' ORDER BY INTERNAL_COLUMN_ID");
            var info = await p.DumpTableInfoAsync("1", t);

            Assert.Equal(["20", "11", "X", "9"], values[0]);
            Assert.Equal(["ID", "A", "B", "V1", "V2", "V3"], ids.Select(r => r[0]));
            Assert.Equal(["0", "1", "2", "3", "4", "5"], ids.Select(r => r[1]));
            Assert.All(ids, r => Assert.Null(r[2]));
            var v1 = info.Columns.Single(c => c.Name == "V1");
            var v3 = info.Columns.Single(c => c.Name == "V3");
            Assert.True(v1.IsVirtual);
            Assert.Equal("(\"A\" * 2)", v1.DefaultValue); // 식은 바뀌지 않았다. 서버 표기 = 괄호·따옴표·대문자
            Assert.Equal(("VARCHAR", 10), (v3.DataType, v3.Length)); // 형식 생략 가상 열은 식에서 형식·길이를 받는다
            Assert.Equal("UPPER(\"B\")", v3.DefaultValue);
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>2. 열 이름 바꾸기, 인덱스 이름 바꾸기(소유자 붙인 꼴 포함).</summary>
    [TiberoWriteFact]
    public async Task P02_rename_column_and_index()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("RN");
        var i1 = T("RN_I1");
        var i2 = T("RN_I2");
        var i3 = T("RN_I3");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, A NUMBER, B NUMBER)");
            await p.MustAsync($"CREATE INDEX {i1} ON {t} (A)");

            await p.ExpectAsync("2 rename column", "OK", $"ALTER TABLE {p.Owner}.{t} RENAME COLUMN A TO A2");
            await p.ExpectAsync("2 rename column to existing", "TBR-7103", $"ALTER TABLE {t} RENAME COLUMN A2 TO B");
            await p.ExpectAsync("2 rename index", "OK", $"ALTER INDEX {p.Owner}.{i1} RENAME TO {i2}");
            await p.ExpectAsync("2 rename index owner target", "TBR-7001", $"ALTER INDEX {p.Owner}.{i2} RENAME TO {p.Owner}.{i3}"); // 새 이름에 소유자를 붙이면 안 된다(Oracle 과 같음)
            var rows = await p.DumpAsync("2", $"SELECT INDEX_NAME, COLUMN_NAME FROM USER_IND_COLUMNS WHERE TABLE_NAME = '{t}' AND INDEX_NAME = '{i2}'");

            Assert.Equal([i2, "A2"], Assert.Single(rows)); // 인덱스는 바뀐 열 이름을 따라간다
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>3. MODIFY 실패: 길이 줄이기(넘치는 행)·NOT NULL(NULL 행)·형식 바꾸기(데이터 있음)·정밀도 줄이기. 오류 번호와 분류.</summary>
    [TiberoWriteFact]
    public async Task P03_modify_failures_with_data()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("MD");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, S VARCHAR2(10), N NUMBER(10), NL VARCHAR2(5), D DATE, C CHAR(5), E VARCHAR2(10))");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 'abcdefghij', 12345, NULL, SYSDATE, 'ab', NULL)");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, 'ab', 1, 'x', SYSDATE, 'cd', NULL)");
            await p.MustAsync("COMMIT");

            await p.ExpectAsync("3 shrink-overflow", "TBR-7413", $"ALTER TABLE {t} MODIFY (S VARCHAR2(5))");
            await p.ExpectAsync("3 shrink-fits", "OK", $"ALTER TABLE {t} MODIFY (S VARCHAR2(10 CHAR))");
            await p.ExpectAsync("3 grow", "OK", $"ALTER TABLE {t} MODIFY (S VARCHAR2(20))");
            await p.ExpectAsync("3 not-null-with-null", "TBR-7045", $"ALTER TABLE {t} MODIFY (NL NOT NULL)");
            await p.ExpectAsync("3 not-null-ok", "OK", $"ALTER TABLE {t} MODIFY (S NOT NULL)");
            await p.ExpectAsync("3 null-again", "OK", $"ALTER TABLE {t} MODIFY (S NULL)");
            await p.ExpectAsync("3 null-when-null", "TBR-7157", $"ALTER TABLE {t} MODIFY (S NULL)");
            await p.ExpectAsync("3 type-varchar-to-number", "TBR-7080", $"ALTER TABLE {t} MODIFY (S NUMBER)");
            await p.ExpectAsync("3 type-number-to-varchar", "TBR-7080", $"ALTER TABLE {t} MODIFY (N VARCHAR2(20))");
            await p.ExpectAsync("3 type-date-to-timestamp", "OK", $"ALTER TABLE {t} MODIFY (D TIMESTAMP)");
            await p.ExpectAsync("3 type-char-to-varchar", "OK", $"ALTER TABLE {t} MODIFY (C VARCHAR2(5))");
            await p.ExpectAsync("3 type-empty-col", "OK", $"ALTER TABLE {t} MODIFY (E NUMBER)");
            await p.ExpectAsync("3 precision-overflow", "TBR-7238", $"ALTER TABLE {t} MODIFY (N NUMBER(3))");
            await p.ExpectAsync("3 precision-fits", "TBR-7238", $"ALTER TABLE {t} MODIFY (N NUMBER(5))");
            await p.ExpectAsync("3 default", "OK", $"ALTER TABLE {t} MODIFY (NL DEFAULT 'z')");
            await p.ExpectAsync("3 default-null", "OK", $"ALTER TABLE {t} MODIFY (NL DEFAULT NULL)");
            await p.ExpectAsync("3 multi-attr", "OK", $"ALTER TABLE {t} MODIFY (S VARCHAR2(30) DEFAULT 'q' NOT NULL)");
            var info = await p.DumpTableInfoAsync("3", t);
            var col = info.Columns.ToDictionary(c => c.Name);
            Assert.Equal(("VARCHAR", 30, false, "'q'"), (col["S"].DataType, col["S"].Length, col["S"].IsNullable, col["S"].DefaultValue));
            Assert.Equal("NULL", col["NL"].DefaultValue); // DEFAULT NULL 뒤 사전에 글자 NULL 이 남는다 — 정규화에서 「기본값 없음」과 같게 본다
            Assert.Equal("TIMESTAMP(6)", col["D"].DataType); // DATE → TIMESTAMP 는 데이터가 있어도 된다
            Assert.Equal(("VARCHAR", 5), (col["C"].DataType, col["C"].Length));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>4. 제약: PK/UK/CHECK 추가(어기는 행 포함), FK ON DELETE CASCADE/SET NULL, ENABLE/DISABLE, DROP.</summary>
    [TiberoWriteFact]
    public async Task P04_constraints()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var parent = T("CP");
        var child = T("CC");
        await p.DropTableAsync(child);
        await p.DropTableAsync(parent);
        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (ID NUMBER, CODE VARCHAR2(10), QTY NUMBER)");
            await p.MustAsync($"CREATE TABLE {child} (ID NUMBER, PID NUMBER, PID2 NUMBER)");
            await p.MustAsync($"INSERT INTO {parent} VALUES (1, 'A', 5)");
            await p.MustAsync($"INSERT INTO {parent} VALUES (2, 'A', -1)");
            await p.MustAsync($"INSERT INTO {parent} VALUES (2, 'B', 3)");
            await p.MustAsync($"INSERT INTO {child} VALUES (10, 1, 1)");
            await p.MustAsync($"INSERT INTO {child} VALUES (11, 9, 1)");
            await p.MustAsync($"INSERT INTO {child} VALUES (12, 2, 1)");
            await p.MustAsync("COMMIT");

            await p.ExpectAsync("4 pk-dup", "TBR-7161", $"ALTER TABLE {p.Owner}.{parent} ADD CONSTRAINT {T("CP_PK")} PRIMARY KEY (ID)");
            await p.ExpectAsync("4 uk-dup", "TBR-7161", $"ALTER TABLE {parent} ADD CONSTRAINT {T("CP_UK")} UNIQUE (CODE)");
            await p.ExpectAsync("4 check-violate", "TBR-7161", $"ALTER TABLE {parent} ADD CONSTRAINT {T("CP_CK")} CHECK (QTY > 0)");
            await p.MustAsync($"DELETE FROM {parent} WHERE CODE = 'B' OR QTY < 0");
            await p.MustAsync($"INSERT INTO {parent} VALUES (2, 'B', 3)");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("4 pk", "OK", $"ALTER TABLE {parent} ADD CONSTRAINT {T("CP_PK")} PRIMARY KEY (ID)");
            await p.ExpectAsync("4 uk", "OK", $"ALTER TABLE {parent} ADD CONSTRAINT {T("CP_UK")} UNIQUE (CODE)");
            await p.ExpectAsync("4 check", "OK", $"ALTER TABLE {parent} ADD CONSTRAINT {T("CP_CK")} CHECK (QTY > 0)");
            await p.ExpectAsync("4 fk-orphan", "TBR-7161", $"ALTER TABLE {child} ADD CONSTRAINT {T("CC_FK")} FOREIGN KEY (PID) REFERENCES {p.Owner}.{parent} (ID) ON DELETE CASCADE");
            await p.ExpectAsync("4 fk-orphan-novalidate", "OK", $"ALTER TABLE {child} ADD CONSTRAINT {T("CC_FKN")} FOREIGN KEY (PID) REFERENCES {parent} (ID) ENABLE NOVALIDATE");
            await p.ExpectAsync("4 drop-novalidate", "OK", $"ALTER TABLE {child} DROP CONSTRAINT {T("CC_FKN")}");
            await p.MustAsync($"DELETE FROM {child} WHERE PID = 9");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("4 fk-cascade", "OK", $"ALTER TABLE {child} ADD CONSTRAINT {T("CC_FK")} FOREIGN KEY (PID) REFERENCES {p.Owner}.{parent} (ID) ON DELETE CASCADE");
            await p.ExpectAsync("4 fk-set-null", "OK", $"ALTER TABLE {child} ADD CONSTRAINT {T("CC_FK2")} FOREIGN KEY (PID2) REFERENCES {parent} (ID) ON DELETE SET NULL");
            var rules = await p.DumpAsync("4", $"SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE, DELETE_RULE, STATUS, VALIDATED, SEARCH_CONDITION FROM USER_CONSTRAINTS WHERE TABLE_NAME IN ('{parent}', '{child}') ORDER BY CONSTRAINT_NAME");
            var childInfo = await p.DumpTableInfoAsync("4", child);
            Assert.Equal(["CASCADE", "SET NULL"], rules.Where(r => r[1] == "R").Select(r => r[2]));
            Assert.Equal("QTY > 0", rules.Single(r => r[0] == T("CP_CK"))[5]);
            Assert.All(childInfo.Constraints, k => Assert.Equal(parent, k.ReferencedTable?.Name));

            await p.ExpectAsync("4 disable", "OK", $"ALTER TABLE {parent} DISABLE CONSTRAINT {T("CP_CK")}");
            await p.MustAsync($"INSERT INTO {parent} VALUES (3, 'C', -5)");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("4 enable-violating", "TBR-7161", $"ALTER TABLE {parent} ENABLE CONSTRAINT {T("CP_CK")}");
            await p.ExpectAsync("4 enable-novalidate", "OK", $"ALTER TABLE {parent} ENABLE NOVALIDATE CONSTRAINT {T("CP_CK")}");
            var novalidate = await p.DumpAsync("4", $"SELECT CONSTRAINT_NAME, STATUS, VALIDATED FROM USER_CONSTRAINTS WHERE CONSTRAINT_NAME = '{T("CP_CK")}'");
            Assert.Equal(["ENABLED", "NOT VALIDATED"], Assert.Single(novalidate)[1..]);
            await p.MustAsync($"DELETE FROM {parent} WHERE ID = 3");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("4 disable-again", "OK", $"ALTER TABLE {parent} DISABLE CONSTRAINT {T("CP_CK")}");
            await p.ExpectAsync("4 enable", "OK", $"ALTER TABLE {parent} ENABLE CONSTRAINT {T("CP_CK")}");
            await p.ExpectAsync("4 disable-pk-with-fk", "TBR-7217", $"ALTER TABLE {parent} DISABLE CONSTRAINT {T("CP_PK")}");

            await p.ExpectAsync("4 delete-parent", "OK", $"DELETE FROM {parent} WHERE ID = 1");
            var left = await p.DumpAsync("4 after-delete", $"SELECT ID, PID, PID2 FROM {child} ORDER BY ID");
            Assert.Equal(["12", "2", null], Assert.Single(left)); // 10 은 CASCADE 로 지워지고 12 의 PID2 는 SET NULL
            await p.SilentAsync("ROLLBACK");

            await p.ExpectAsync("4 drop-pk-with-fk", "TBR-7156", $"ALTER TABLE {parent} DROP CONSTRAINT {T("CP_PK")}");
            await p.ExpectAsync("4 drop-fk", "OK", $"ALTER TABLE {child} DROP CONSTRAINT {T("CC_FK")}");
            await p.ExpectAsync("4 drop-fk2", "OK", $"ALTER TABLE {child} DROP CONSTRAINT {T("CC_FK2")}");
            await p.ExpectAsync("4 drop-pk", "OK", $"ALTER TABLE {parent} DROP CONSTRAINT {T("CP_PK")}");
            await p.ExpectAsync("4 drop-missing", "TBR-7076", $"ALTER TABLE {parent} DROP CONSTRAINT {T("NO_SUCH")}");
            await p.ExpectAsync("4 dup-name", "TBR-7104", $"ALTER TABLE {child} ADD CONSTRAINT {T("CP_UK")} UNIQUE (ID)");
        }
        finally
        {
            await p.DropTableAsync(child);
            await p.DropTableAsync(parent);
        }
    }

    /// <summary>5. DESC 인덱스·UNIQUE 인덱스, 카탈로그 표기(함수 기반처럼 보이는지).</summary>
    [TiberoWriteFact]
    public async Task P05_desc_and_unique_indexes()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("IX");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, A NUMBER, B VARCHAR2(10))");
            await p.ExpectAsync("5 desc", "OK", $"CREATE INDEX {p.Owner}.{T("IX_D")} ON {p.Owner}.{t} (A DESC, B)");
            await p.ExpectAsync("5 unique", "OK", $"CREATE UNIQUE INDEX {p.Owner}.{T("IX_U")} ON {p.Owner}.{t} (ID)");
            await p.ExpectAsync("5 unique-desc", "OK", $"CREATE UNIQUE INDEX {T("IX_UD")} ON {t} (B DESC)");
            await p.ExpectAsync("5 asc", "OK", $"CREATE INDEX {T("IX_A")} ON {t} (B ASC, A)");
            var types = await p.DumpAsync("5", $"SELECT INDEX_NAME, INDEX_TYPE, UNIQUENESS FROM USER_INDEXES WHERE TABLE_NAME = '{t}' ORDER BY INDEX_NAME");
            var cols = await p.DumpAsync("5", $"SELECT INDEX_NAME, COLUMN_NAME, COLUMN_POSITION, DESCEND FROM USER_IND_COLUMNS WHERE TABLE_NAME = '{t}' ORDER BY INDEX_NAME, COLUMN_POSITION");

            // Oracle 과 달리 DESC 인덱스는 NORMAL 이고 열 이름이 그대로, DESCEND = DESC 로 온다(함수 기반처럼 보이지 않는다).
            Assert.All(types, r => Assert.Equal("NORMAL", r[1]));
            var first = cols.Single(r => r[0] == T("IX_D") && r[2] == "1");
            Assert.Equal(("A", "DESC"), (first[1], first[3]));
            var exprCols = await p.ColumnsOfAsync("USER_IND_EXPRESSIONS");
            p.Log("5", "USER_IND_EXPRESSIONS columns = " + (exprCols is null ? "(no view)" : string.Join(",", exprCols)));
            if (exprCols is not null)
            {
                await p.DumpAsync("5", $"SELECT * FROM USER_IND_EXPRESSIONS WHERE TABLE_NAME = '{t}'");
            }

            var info = await p.DumpTableInfoAsync("5", t);
            Assert.Equal(["A", "B"], info.Indexes.Single(i => i.Name == T("IX_D")).Columns); // 공급자는 DESC 를 버린다 — 편집기는 DESCEND 를 따로 읽어야 한다
            await p.ExpectAsync("5 drop", "OK", $"DROP INDEX {p.Owner}.{T("IX_A")}");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>6. 식별자 길이 한도: 열·제약·인덱스 이름 30/31/128/129 바이트, 한글 이름(서버 문자 집합 기준 바이트).</summary>
    [TiberoWriteFact]
    public async Task P06_identifier_length_limits()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("LN");
        await p.DropTableAsync(t);
        try
        {
            await p.DumpAsync("6", "SELECT PARAMETER, VALUE FROM NLS_DATABASE_PARAMETERS ORDER BY PARAMETER");
            await p.DumpAsync("6", "SELECT LENGTHB('가'), LENGTHB(N'가') FROM DUAL");
            var unit = int.Parse(await p.ScalarAsync("SELECT LENGTHB('가') FROM DUAL") ?? "0", System.Globalization.CultureInfo.InvariantCulture); // 서버 문자 집합의 한글 한 글자 바이트(실측 서버 = 3)
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, K30 NUMBER, K31 NUMBER, K128 NUMBER, K129 NUMBER)");
            foreach (var n in new[] { 30, 31, 128, 129 })
            {
                await p.ExpectAsync($"6 column {n}", n <= 128 ? "OK" : "TBR-23007", $"ALTER TABLE {t} ADD (C{TableEditProbe.Repeat("X", n - 1)} NUMBER)");
            }

            foreach (var n in new[] { 30, 31, 128, 129 })
            {
                var name = TableEditProbe.Prefix + "K" + TableEditProbe.Repeat("X", n - TableEditProbe.Prefix.Length - 1);
                await p.ExpectAsync($"6 constraint {n}", n <= 128 ? "OK" : "TBR-23007", $"ALTER TABLE {t} ADD CONSTRAINT {name} CHECK (ID > {n})");
            }

            foreach (var n in new[] { 30, 31, 128, 129 })
            {
                var name = TableEditProbe.Prefix + "I" + TableEditProbe.Repeat("X", n - TableEditProbe.Prefix.Length - 1);
                await p.ExpectAsync($"6 index {n}", n <= 128 ? "OK" : "TBR-23007", $"CREATE INDEX {name} ON {t} (K{n})");
            }

            foreach (var n in new[] { 10, 11, 15, 16, 42, 43, 64, 65 })
            {
                var name = TableEditProbe.Repeat("가", n);
                await p.ExpectAsync($"6 korean column {n} chars", n * unit <= 128 ? "OK" : "TBR-23007", $"ALTER TABLE {t} ADD (\"{name}\" NUMBER)");
            }

            foreach (var n in new[] { 7, 8, 30, 31, 40, 41 })
            {
                var name = TableEditProbe.Prefix + TableEditProbe.Repeat("나", n);
                await p.ExpectAsync($"6 korean constraint ORT_TE_+{n} chars", TableEditProbe.Prefix.Length + (n * unit) <= 128 ? "OK" : "TBR-23007", $"ALTER TABLE {t} ADD CONSTRAINT \"{name}\" CHECK (ID > -{n})");
            }

            await p.DumpAsync("6", $"SELECT COLUMN_ID, INTERNAL_COLUMN_ID, LENGTH(COLUMN_NAME), LENGTHB(COLUMN_NAME) FROM USER_TAB_COLS WHERE TABLE_NAME = '{t}' ORDER BY INTERNAL_COLUMN_ID");
            await p.DumpAsync("6", $"SELECT LENGTH(CONSTRAINT_NAME), LENGTHB(CONSTRAINT_NAME) FROM USER_CONSTRAINTS WHERE TABLE_NAME = '{t}' ORDER BY 2");
            await p.DumpAsync("6", $"SELECT LENGTH(INDEX_NAME), LENGTHB(INDEX_NAME) FROM USER_INDEXES WHERE TABLE_NAME = '{t}' ORDER BY 2");
            await p.SilentAsync($"DROP TABLE {t} CASCADE CONSTRAINTS PURGE");

            foreach (var n in new[] { 30, 31, 128, 129 })
            {
                var name = TableEditProbe.Prefix + "T" + TableEditProbe.Repeat("X", n - TableEditProbe.Prefix.Length - 1);
                var created = await p.ExpectAsync($"6 table {n}", n <= 128 ? "OK" : "TBR-23007", $"CREATE TABLE {name} (ID NUMBER)");
                if (created.Ok)
                {
                    await p.DropTableAsync(name);
                }
            }
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>7. 서버가 돌려주는 가상 식·기본값·CHECK 조건·NUMBER·CHAR_USED 표기(메타데이터 공급자의 ColumnInfo 그대로).</summary>
    [TiberoWriteFact]
    public async Task P07_server_spelling_of_expressions_defaults_and_types()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("SP");
        await p.DropTableAsync(t);
        try
        {
            await p.DumpAsync("7", "SELECT PARAMETER, VALUE FROM NLS_SESSION_PARAMETERS WHERE PARAMETER = 'NLS_LENGTH_SEMANTICS'");
            await p.MustAsync($"""
                CREATE TABLE {t} (
                  id number primary key,
                  n0 NUMBER,
                  n1 NUMBER(*,0),
                  n2 NUMBER(10),
                  n3 NUMBER(10,2),
                  n4 NUMBER(*,2),
                  f1 FLOAT,
                  f2 FLOAT(10),
                  i1 INTEGER,
                  s0 VARCHAR2(10),
                  s1 VARCHAR2(10 BYTE),
                  s2 VARCHAR2(10 CHAR),
                  c0 CHAR(5),
                  c1 CHAR(5 CHAR),
                  ns NVARCHAR2(10),
                  d1 VARCHAR2(10) default 'x',
                  d2 NUMBER DEFAULT 0,
                  d3 DATE default sysdate,
                  d4 VARCHAR2(10) DEFAULT
                     'abc'  ,
                  d5 TIMESTAMP DEFAULT SYSTIMESTAMP,
                  d6 VARCHAR2(10) DEFAULT 'It''s',
                  d7 NUMBER DEFAULT (1 + 2),
                  v1 AS (n2*2 + nvl(n3,0)) virtual,
                  v2 VARCHAR2(20) AS (upper(s0) || 'x') VIRTUAL,
                  constraint {T("SP_CK1")} check (n2 > 0 and s0 in ('a','b')),
                  CONSTRAINT {T("SP_CK2")} CHECK ( "N3" IS NOT NULL ),
                  CONSTRAINT {T("SP_CK3")} CHECK (s1 like 'A%'
                     OR s1 IS NULL)
                )
                """);
            await p.MustAsync($"ALTER TABLE {t} MODIFY (n0 NOT NULL)");
            var raw = await p.DumpAsync("7", $"SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, CHAR_LENGTH, CHAR_USED, NULLABLE, VIRTUAL_COLUMN, DATA_DEFAULT FROM USER_TAB_COLS WHERE TABLE_NAME = '{t}' ORDER BY COLUMN_ID");
            var checks = await p.DumpAsync("7", $"SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE, SEARCH_CONDITION FROM USER_CONSTRAINTS WHERE TABLE_NAME = '{t}' ORDER BY CONSTRAINT_NAME");
            var info = await p.DumpTableInfoAsync("7", t);
            var c = info.Columns.ToDictionary(x => x.Name);
            var used = raw.ToDictionary(r => r[0]!, r => r[6]);

            // 형식 이름: VARCHAR2 → VARCHAR, NVARCHAR2 → NVARCHAR, TIMESTAMP → TIMESTAMP(6), FLOAT → NUMBER.
            Assert.Equal(["VARCHAR", "NVARCHAR", "CHAR", "TIMESTAMP(6)", "NUMBER", "NUMBER"], new[] { "S0", "NS", "C0", "D5", "F1", "F2" }.Select(n => c[n].DataType));

            // NUMBER(*,0)·INTEGER = 정밀도 38 눈금 0 (Oracle 은 정밀도 NULL), FLOAT(10) = NUMBER 정밀도 3 눈금 NULL.
            Assert.Equal([(38, 0), (38, 0), (38, 2), (3, null)], new[] { "N1", "I1", "N4", "F2" }.Select(n => (c[n].Precision, c[n].Scale)));
            Assert.Equal((null, null), (c["N0"].Precision, c["N0"].Scale));

            // 문자 길이 기준: 10 BYTE 와 10 CHAR 가 공급자에서 똑같이 보인다(Tibero 는 CHAR 열도 DATA_LENGTH = 글자 수). 구분은 CHAR_USED 뿐.
            Assert.Equal((c["S1"].Length, c["S1"].ByteLength), (c["S2"].Length, c["S2"].ByteLength));
            Assert.Equal(["B", "B", "C", "B", "C", "C"], new[] { "S0", "S1", "S2", "C0", "C1", "NS" }.Select(n => used[n]));

            // 기본값: 쓴 대로(대소문자·따옴표 그대로), 앞뒤 공백·줄바꿈은 잘린다.
            Assert.Equal(["'x'", "0", "sysdate", "'abc'", "SYSTIMESTAMP", "'It''s'", "(1 + 2)"], new[] { "D1", "D2", "D3", "D4", "D5", "D6", "D7" }.Select(n => c[n].DefaultValue));

            // 가상 식: 서버가 다시 쓴다(따옴표·대문자·괄호, || → CONCAT).
            Assert.Equal("((\"N2\" * 2) + NVL(\"N3\",0))", c["V1"].DefaultValue);
            Assert.Equal("CONCAT(UPPER(\"S0\"),'x')", c["V2"].DefaultValue);

            // CHECK 조건: 쓴 대로(대소문자·앞뒤 공백·줄바꿈까지).
            var cond = info.Constraints.Where(k => k.Kind == ConstraintKind.Check).ToDictionary(k => k.Name, k => k.Condition);
            Assert.Equal("n2 > 0 and s0 in ('a','b')", cond[T("SP_CK1")]);
            Assert.Equal(" \"N3\" IS NOT NULL ", cond[T("SP_CK2")]);
            Assert.Matches(@"^s1 like 'A%'\r?\n\s+OR s1 IS NULL$", cond[T("SP_CK3")]);

            // 서버 이름 제약 = <계정>_CON<숫자> (Oracle 의 SYS_C…). NOT NULL 도 이 이름의 CHECK 로 사전에 있지만 공급자는 빼고 준다.
            var serverNamed = checks.Where(r => !r[0]!.StartsWith(TableEditProbe.Prefix, StringComparison.Ordinal)).ToList();
            Assert.Equal(2, serverNamed.Count);
            var pattern = "^" + Regex.Escape(p.Owner) + "_CON[0-9]+$";
            Assert.True(serverNamed.All(r => Regex.IsMatch(r[0]!, pattern)), "server-named constraints do not look like <owner>_CON<n>");
            Assert.Contains(serverNamed, r => r[2] == "\"N0\" IS NOT NULL");
            Assert.DoesNotContain(info.Constraints, k => k.Condition == "\"N0\" IS NOT NULL");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>8. 카탈로그 DATA_TYPE 표기(쓴 이름과 다른 것만).</summary>
    private static readonly Dictionary<string, string> Spelled = new(StringComparer.Ordinal)
    {
        ["VARCHAR2(20)"] = "VARCHAR",
        ["NVARCHAR2(20)"] = "NVARCHAR",
        ["VARCHAR(20)"] = "VARCHAR",
        ["TIMESTAMP"] = "TIMESTAMP(6)",
        ["TIMESTAMP(3)"] = "TIMESTAMP(3)",
        ["TIMESTAMP WITH TIME ZONE"] = "TIMESTAMP(6) WITH TIME ZONE",
        ["TIMESTAMP WITH LOCAL TIME ZONE"] = "TIMESTAMP(6) WITH LOCAL TIME ZONE",
        ["INTERVAL YEAR TO MONTH"] = "INTERVAL YEAR(2) TO MONTH",
        ["INTERVAL DAY TO SECOND"] = "INTERVAL DAY(2) TO SECOND(6)",
        ["RAW(16)"] = "RAW",
        ["LONG RAW"] = "LONG RAW",
    };

    /// <summary>8. 형식 목록: CREATE 가 되는지(BFILE 만 안 됨), 카탈로그 DATA_TYPE 표기.</summary>
    [TiberoWriteFact]
    public async Task P08_type_list_creates()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var types = new[]
        {
            "VARCHAR2(20)", "NVARCHAR2(20)", "CHAR(5)", "NCHAR(5)", "NUMBER", "NUMBER(12,3)", "DATE", "TIMESTAMP", "TIMESTAMP(3)",
            "TIMESTAMP WITH TIME ZONE", "TIMESTAMP WITH LOCAL TIME ZONE", "CLOB", "NCLOB", "BLOB", "RAW(16)", "LONG", "LONG RAW",
            "VARCHAR(20)", "INTERVAL YEAR TO MONTH", "INTERVAL DAY TO SECOND", "BINARY_FLOAT", "BINARY_DOUBLE", "ROWID", "XMLTYPE", "BFILE",
        };
        try
        {
            for (var i = 0; i < types.Length; i++)
            {
                var t = T("TY" + i);
                await p.DropTableAsync(t);
                var created = await p.ExpectAsync($"8 {types[i]}", types[i] == "BFILE" ? "TBR-7001" : "OK", $"CREATE TABLE {t} (ID NUMBER, C {types[i]})");
                if (created.Ok)
                {
                    var row = await p.DumpAsync($"8 {types[i]}", $"SELECT DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, CHAR_USED FROM USER_TAB_COLS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'C'");
                    if (Spelled.TryGetValue(types[i], out var spelled))
                    {
                        Assert.Equal(spelled, Assert.Single(row)[0]);
                    }
                }
            }

            var two = T("TY_2L");
            await p.DropTableAsync(two);
            await p.ExpectAsync("8 two LONG columns", "TBR-7041", $"CREATE TABLE {two} (A LONG, B LONG)");
            await p.DropTableAsync(two);
        }
        finally
        {
            for (var i = 0; i < types.Length; i++)
            {
                await p.DropTableAsync(T("TY" + i));
            }
        }
    }

    /// <summary>
    /// 9. 의뢰 스크립트 꼴(<c>;</c> 와 단독 줄 <c>/</c>): tbSQL 이 win30 에 없어 Oranda 문장 분할기 결과를 서버에 보내 갈음한다.
    /// 분할 결과 문장 수·종류를 단언하고, 각 문장이 서버에서 도는지 본다.
    /// </summary>
    [TiberoWriteFact]
    public async Task P09_request_script_shape_splits_and_runs()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var p = await OpenAsync(it);
        var t = T("SC");
        var trg = T("SC_TRG");
        await p.DropTableAsync(t);
        var script = $"""
            -- bundle: sample, 2026-11-17, Tibero, 5 statements
            CREATE TABLE {p.Owner}.{t} (ID NUMBER, NOTE VARCHAR2(20));
            ALTER TABLE {p.Owner}.{t} ADD (V AS (ID * 2) VIRTUAL);
            COMMENT ON TABLE {p.Owner}.{t} IS 'semi;colon';
            CREATE OR REPLACE TRIGGER {p.Owner}.{trg}
            BEFORE INSERT ON {p.Owner}.{t}
            FOR EACH ROW
            BEGIN
              :NEW.NOTE := NVL(:NEW.NOTE, 'auto;');
            END;
            /
            CREATE INDEX {p.Owner}.{T("SC_I")} ON {p.Owner}.{t} (NOTE);
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        try
        {
            var statements = StatementSplitter.Split(script, SplitOptions.Default);
            p.Log("9", $"split count = {statements.Count}");
            foreach (var s in statements)
            {
                var outcome = await p.RunAsync($"9 {s.Kind}", s.Text);
                Assert.True(outcome.Ok, outcome.ToString());
            }

            await p.MustAsync($"INSERT INTO {t} (ID) VALUES (1)");
            var note = await p.ScalarAsync($"SELECT NOTE FROM {t}");
            await p.SilentAsync("ROLLBACK");
            p.Log("9", $"trigger result = {TableEditProbe.Show(note)}");

            Assert.Equal(5, statements.Count);
            Assert.Equal("auto;", note);
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>10. DDL 암묵 커밋: 미커밋 INSERT 뒤 DDL(성공·실패) → 다른 세션에서 그 행이 보이는지.</summary>
    [TiberoWriteFact]
    public async Task P10_ddl_commits_an_open_transaction()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var a = await OpenAsync(it);
        var b = await OpenAsync(it);
        var t = T("TX");
        var t2 = T("TX2");
        await a.DropTableAsync(t);
        await a.DropTableAsync(t2);
        try
        {
            await a.MustAsync($"CREATE TABLE {t} (ID NUMBER, N NUMBER)");

            await a.MustAsync($"INSERT INTO {t} VALUES (1, 1)");
            var before = await b.ScalarAsync($"SELECT COUNT(*) FROM {t}");
            await a.ExpectAsync("10 ddl-ok", "OK", $"CREATE TABLE {t2} (ID NUMBER)");
            var afterOk = await b.ScalarAsync($"SELECT COUNT(*) FROM {t}");
            a.Log("10", $"other session sees: before DDL={before}, after successful DDL={afterOk}");

            await a.MustAsync($"INSERT INTO {t} VALUES (2, 2)");
            await a.ExpectAsync("10 ddl-exec-fail", "TBR-7072", $"ALTER TABLE {t} DROP COLUMN NO_SUCH_COL");
            var afterExecFail = await b.ScalarAsync($"SELECT COUNT(*) FROM {t}");
            a.Log("10", $"other session sees after failed DDL (missing column) = {afterExecFail}");

            await a.MustAsync($"INSERT INTO {t} VALUES (3, 3)");
            await a.ExpectAsync("10 ddl-parse-fail", "TBR-7001", $"ALTER TABLE {t} ADD (");
            var afterParseFail = await b.ScalarAsync($"SELECT COUNT(*) FROM {t}");
            a.Log("10", $"other session sees after syntax-error DDL = {afterParseFail}");

            await a.MustAsync($"INSERT INTO {t} VALUES (4, 4)");
            await a.ExpectAsync("10 comment", "OK", $"COMMENT ON TABLE {t} IS 'x'");
            var afterComment = await b.ScalarAsync($"SELECT COUNT(*) FROM {t}");
            a.Log("10", $"other session sees after COMMENT = {afterComment}");
            await a.SilentAsync("ROLLBACK");

            // Oracle 과 같다: 성공한 DDL·실행 단계에서 실패한 DDL·COMMENT 는 앞 트랜잭션을 커밋하고, 구문 오류 DDL 은 커밋하지 않는다.
            Assert.Equal(["0", "1", "2", "2", "4"], new[] { before, afterOk, afterExecFail, afterParseFail, afterComment });
        }
        finally
        {
            await a.SilentAsync("ROLLBACK");
            await a.DropTableAsync(t);
            await a.DropTableAsync(t2);
        }
    }
}
