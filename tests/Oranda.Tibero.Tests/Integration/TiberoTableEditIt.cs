using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 테이블 편집기 Tibero 통합 시험(설계 14장 Tibero 줄, 계획 Task 4). 지금은 실행 전 검사 읽기(<see cref="TiberoTableEditChecks"/>)의 ✖ 경우와
/// 실제 서버 표기에 대한 방언 정규화를 본다. 「편집 켜고 안 고치면 0문장」·8.2 문장 실행은 Core 차이·문장(T1b)이 들어온 뒤 더한다.
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>(<see cref="TiberoWriteFactAttribute"/>).</item>
/// <item>공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTableEditIt(ITestOutputHelper output)
{
    private static readonly TiberoTableEditChecks Checks = new();

    private static readonly TiberoTableEditDialect Dialect = new();

    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    /// <summary>행 수·NULL·넘침(문자 BYTE/CHAR·숫자 정수 자리)·데이터 있음 검사(설계 10장 ✖ 1~3줄, 실측 3장).</summary>
    [TiberoWriteFact]
    public async Task Column_checks_count_rows_nulls_overflow_and_data()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("CK");
        var table = new DbObjectRef(p.Owner, t, "TABLE");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, S VARCHAR2(20), N NUMBER(10,2), NL VARCHAR2(5), E NUMBER, NS NVARCHAR2(10), R RAW(8))");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 'abcdefg', 12345.67, NULL, NULL, N'가나다', HEXTORAW('0102030405'))");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, '가나다', 1.5, 'x', NULL, N'가', HEXTORAW('01'))");
            await p.MustAsync($"INSERT INTO {t} VALUES (3, 'ab', -999.99, NULL, NULL, NULL, NULL)");
            await p.MustAsync($"INSERT INTO {t} VALUES (4, NULL, NULL, 'y', NULL, NULL, NULL)");
            await p.MustAsync("COMMIT");
            var ct = CancellationToken.None;

            Assert.Equal(4, await Checks.CountRowsAsync(session, table, ct));
            Assert.Null(await Checks.NumRowsAsync(session, table, ct)); // 통계 없음(Tibero 사전은 0 을 주지만 LAST_ANALYZED 가 없다)
            var gathered = await p.RunAsync("stats", $"BEGIN DBMS_STATS.GATHER_TABLE_STATS('{p.Owner}', '{t}'); END;");
            if (gathered.Ok)
            {
                Assert.Equal(4, await Checks.NumRowsAsync(session, table, ct));
            }
            Assert.Equal(2, await Checks.NullCountAsync(session, table, "NL", ct));
            Assert.Equal(0, await Checks.NonNullCountAsync(session, table, "E", ct)); // 빈 열 = 형식 바꾸기·정밀도 줄이기 가능(실측 3장)
            Assert.Equal(3, await Checks.NonNullCountAsync(session, table, "N", ct));

            // '가나다' = 3글자 9바이트(실측 6장). 5 BYTE 면 'abcdefg'·'가나다' 둘, 5 CHAR 면 'abcdefg' 하나.
            Assert.Equal(2, await Checks.OverflowCountAsync(session, table, "S", Draft("S", "VARCHAR2", 5, CharSemantics.Bytes), ct));
            Assert.Equal(1, await Checks.OverflowCountAsync(session, table, "S", Draft("S", "VARCHAR2", 5, CharSemantics.Characters), ct));
            Assert.Equal(1, await Checks.OverflowCountAsync(session, table, "S", Draft("S", "VARCHAR2", 5), ct)); // 기준 모름 = 글자(Core 조건, 검사 계획은 원래 기준을 채워 보낸다)
            Assert.Equal(1, await Checks.OverflowCountAsync(session, table, "NS", Draft("NS", "NVARCHAR2", 2), ct)); // N 형식은 글자
            Assert.Equal(1, await Checks.OverflowCountAsync(session, table, "R", Draft("R", "RAW", 4), ct)); // RAW 는 바이트(Core 조건 UTL_RAW.LENGTH)
            Assert.Equal(1, await Checks.OverflowCountAsync(session, table, "N", Draft("N", "NUMBER", precision: 5, scale: 2), ct)); // 정수 3자리: 12345.67
            Assert.Equal(0, await Checks.OverflowCountAsync(session, table, "N", Draft("N", "NUMBER", precision: 10, scale: 0), ct)); // 소수 줄이기는 반올림
            Assert.Equal(2, await Checks.OverflowCountAsync(session, table, "N", Draft("N", "NUMBER", precision: 2, scale: 0), ct)); // 12345.67, -999.99
            Assert.Equal(0, await Checks.OverflowCountAsync(session, table, "ID", Draft("ID", "DATE"), ct)); // 판정 없음

            // 검사가 ✖ 로 본 경우가 실제로 서버에서 실패하는지(실측 3장 번호).
            await p.ExpectAsync("check shrink", "TBR-7413", $"ALTER TABLE {t} MODIFY (S VARCHAR2(5))");
            await p.ExpectAsync("check not null", "TBR-7045", $"ALTER TABLE {t} MODIFY (NL NOT NULL)");
            await p.ExpectAsync("check precision with data", "TBR-7238", $"ALTER TABLE {t} MODIFY (N NUMBER(10,0))");
            await p.ExpectAsync("check empty column type", "OK", $"ALTER TABLE {t} MODIFY (E VARCHAR2(10))");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>키 겹침(모두 NULL 빼고)·짝 없는 FK 행(NULL 키 빼고)·CHECK 어김(UNKNOWN 은 어기지 않음), 그리고 그 제약이 실제로 7161 로 실패.</summary>
    [TiberoWriteFact]
    public async Task Constraint_checks_count_duplicates_orphans_and_violations()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var parent = T("CKP");
        var child = T("CKC");
        await p.DropTableAsync(child);
        await p.DropTableAsync(parent);
        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (A NUMBER, B NUMBER, K VARCHAR2(5), Q NUMBER)");
            await p.MustAsync($"CREATE TABLE {child} (ID NUMBER, PA NUMBER, PB NUMBER)");
            foreach (var row in new[] { "1, 1, 'a', 5", "1, 2, 'a', -1", "2, 1, 'b', NULL", "2, 1, NULL, 3", "3, 3, NULL, 0" })
            {
                await p.MustAsync($"INSERT INTO {parent} VALUES ({row})");
            }

            foreach (var row in new[] { "10, 1, 1", "11, 9, 9", "12, NULL, 9", "13, 1, NULL", "14, 2, 2" })
            {
                await p.MustAsync($"INSERT INTO {child} VALUES ({row})");
            }

            await p.MustAsync("COMMIT");
            var ct = CancellationToken.None;
            var parentRef = new DbObjectRef(p.Owner, parent, "TABLE");
            var childRef = new DbObjectRef(p.Owner, child, "TABLE");

            Assert.Equal(2, await Checks.DuplicateKeyCountAsync(session, parentRef, ["K"], ct)); // 'a' 두 행, NULL 두 행은 뺌
            Assert.Equal(2, await Checks.DuplicateKeyCountAsync(session, parentRef, ["A", "B"], ct)); // (2,1) 두 행
            Assert.Equal(0, await Checks.DuplicateKeyCountAsync(session, parentRef, ["A", "Q"], ct));
            Assert.Equal(2, await Checks.OrphanCountAsync(session, childRef, ["PA", "PB"], parentRef, ["A", "B"], ct)); // (9,9)·(2,2), NULL 키 행은 뺌
            Assert.Equal(2, await Checks.CheckViolationCountAsync(session, parentRef, "Q > 0", ct)); // -1·0, NULL 은 UNKNOWN
            Assert.Equal(0, await Checks.CheckViolationCountAsync(session, parentRef, "Q > -5 OR Q IS NULL", ct));
            Assert.Equal(2, await Checks.CheckViolationCountAsync(session, parentRef, "Q > 0 -- 양수만", ct)); // 끝 주석이 닫는 괄호를 먹지 않는다

            await p.ExpectAsync("check unique", "TBR-7161", $"ALTER TABLE {parent} ADD CONSTRAINT {T("CKP_UK")} UNIQUE (K)");
            await p.ExpectAsync("check check", "TBR-7161", $"ALTER TABLE {parent} ADD CONSTRAINT {T("CKP_CK")} CHECK (Q > 0)");
            await p.MustAsync($"DELETE FROM {parent} WHERE A = 2 AND K IS NULL");
            await p.MustAsync("COMMIT");
            await p.MustAsync($"ALTER TABLE {parent} ADD CONSTRAINT {T("CKP_PK")} PRIMARY KEY (A, B)");
            await p.ExpectAsync("check fk", "TBR-7161", $"ALTER TABLE {child} ADD CONSTRAINT {T("CKC_FK")} FOREIGN KEY (PA, PB) REFERENCES {parent} (A, B)");
            await p.MustAsync($"DELETE FROM {child} WHERE ID IN (11, 14)");
            await p.MustAsync("COMMIT");
            Assert.Equal(0, await Checks.OrphanCountAsync(session, childRef, ["PA", "PB"], parentRef, ["A", "B"], ct));
            await p.ExpectAsync("check fk ok", "OK", $"ALTER TABLE {child} ADD CONSTRAINT {T("CKC_FK")} FOREIGN KEY (PA, PB) REFERENCES {parent} (A, B)");
        }
        finally
        {
            await p.DropTableAsync(child);
            await p.DropTableAsync(parent);
        }
    }

    /// <summary>가상 식 미리보기(설계 7.6)·새 테이블 식 확인·이름 겹침·의존 객체.</summary>
    [TiberoWriteFact]
    public async Task Preview_validate_names_and_dependents()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("PV");
        var trigger = T("PV_TRG");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER CONSTRAINT {T("PV_PK")} PRIMARY KEY, N NUMBER, S VARCHAR2(10))");
            await p.MustAsync($"CREATE INDEX {T("PV_IX")} ON {t} (S)");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 10, 'a')");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, 20, 'b')");
            await p.MustAsync($"INSERT INTO {t} VALUES (3, 30, 'c')");
            await p.MustAsync("COMMIT");
            // 시험 계정은 CREATE VIEW 권한이 없다(TBR-17004) → 의존 객체는 트리거로 만든다.
            await p.MustAsync($"CREATE OR REPLACE TRIGGER {trigger} BEFORE INSERT ON {t} FOR EACH ROW BEGIN :NEW.N := NVL(:NEW.N, 0); END;");
            var ct = CancellationToken.None;
            var table = new DbObjectRef(p.Owner, t, "TABLE");

            var preview = await Checks.PreviewExpressionAsync(session, table, "N * 2 || S", ["N", "S"], 2, ct);
            Assert.Null(preview.Error);
            Assert.Equal(["N * 2 || S", "N", "S"], preview.Columns);
            Assert.Equal(2, preview.Rows.Count);
            Assert.Contains(preview.Rows, r => r[0] == "20a" && r[1] == "10" && r[2] == "a");

            var broken = await Checks.PreviewExpressionAsync(session, table, "NO_SUCH_COL + 1", [], 2, ct);
            Assert.Empty(broken.Rows);
            Assert.Contains("8026", broken.Error, StringComparison.Ordinal); // Invalid identifier(실측 1장)

            ColumnDraft[] fresh =
            [
                new("a", "A") { DataType = "NUMBER", Precision = 10 },
                new("b", "S") { DataType = "VARCHAR2", Length = 10, CharSemantics = CharSemantics.Characters },
                new("c", "NS") { DataType = "NVARCHAR2", Length = 5 },
                new("d", "V") { VirtualExpression = "A * 2" },
            ];
            Assert.Null(await Checks.ValidateExpressionAsync(session, p.Owner, fresh, "A + LENGTH(S) + LENGTH(NS)", ct));
            Assert.NotNull(await Checks.ValidateExpressionAsync(session, p.Owner, fresh, "A + NO_SUCH_COL", ct));
            Assert.Null(await Checks.ValidateExpressionAsync(session, p.Owner, [], "1 + 1", ct));
            Assert.Null(await Checks.ValidateExpressionAsync(session, p.Owner, [new("e", "T") { DataType = "VARCHAR2" }], "UPPER(T) -- 길이 아직 없음", ct));

            Assert.True(await Checks.NameExistsAsync(session, p.Owner, "S", TableEditNameKind.Column, table, ct));
            Assert.False(await Checks.NameExistsAsync(session, p.Owner, "NOPE", TableEditNameKind.Column, table, ct));
            Assert.True(await Checks.NameExistsAsync(session, p.Owner, t, TableEditNameKind.Table, null, ct));
            Assert.False(await Checks.NameExistsAsync(session, p.Owner, trigger, TableEditNameKind.Table, null, ct)); // 트리거는 테이블과 다른 이름 공간
            Assert.False(await Checks.NameExistsAsync(session, p.Owner, T("PV_NOPE"), TableEditNameKind.Table, null, ct));
            Assert.True(await Checks.NameExistsAsync(session, p.Owner, T("PV_IX"), TableEditNameKind.Index, null, ct));
            Assert.True(await Checks.NameExistsAsync(session, p.Owner, T("PV_PK"), TableEditNameKind.Index, null, ct)); // PK 인덱스 = 제약 이름
            Assert.True(await Checks.NameExistsAsync(session, p.Owner, T("PV_PK"), TableEditNameKind.Constraint, null, ct));
            Assert.False(await Checks.NameExistsAsync(session, p.Owner, T("PV_IX"), TableEditNameKind.Constraint, null, ct));

            var dependents = await Checks.DependentsAsync(session, table, ct);
            Assert.Contains(dependents, d => d.Name == trigger && d.ObjectType == "TRIGGER");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>이미 취소된 토큰은 서버에 보내지 않고 <see cref="OperationCanceledException"/>(부르는 쪽 ⚠「확인 못 함」).</summary>
    [TiberoItFact]
    public async Task Cancelled_check_throws_operation_cancelled()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Checks.CountRowsAsync(session, new DbObjectRef("SYS", "DUAL", "TABLE"), cts.Token));
    }

    /// <summary>실제 서버가 돌려준 열·제약·인덱스 표기를, 같은 정의를 사용자가 쓴 꼴과 정규화로 견주면 같다(거짓 차이 없음, 설계 8.4·실측 7장).</summary>
    [TiberoWriteFact]
    public async Task Server_spellings_of_a_real_table_normalize_like_the_written_definition()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("NZ");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"""
                CREATE TABLE {t} (
                  id number primary key,
                  n1 NUMBER(*,0), n2 integer, n3 NUMBER(10), n4 NUMBER(10,2), n5 NUMBER, f1 FLOAT,
                  s1 varchar2(10), s2 VARCHAR2(10 CHAR), c1 CHAR(3 CHAR), ns NVARCHAR2(5), nc NCHAR(2),
                  t1 TIMESTAMP, t2 TIMESTAMP(3) WITH TIME ZONE, t3 TIMESTAMP WITH LOCAL TIME ZONE,
                  i1 INTERVAL YEAR TO MONTH, i2 INTERVAL DAY TO SECOND, r1 RAW(16),
                  d1 VARCHAR2(10) DEFAULT
                     'abc'  ,
                  d2 DATE default sysdate,
                  v1 AS (n3*2 + nvl(n4,0)) virtual,
                  CONSTRAINT {T("NZ_CK")} CHECK (n3 > 0 and s1 in ('a','b'))
                )
                """);
            await p.MustAsync($"ALTER TABLE {t} MODIFY (n5 DEFAULT NULL)");

            var info = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(p.Owner, t, "TABLE"), CancellationToken.None);
            var c = info.Columns.ToDictionary(x => x.Name);
            var written = new Dictionary<string, ColumnDraft>(StringComparer.Ordinal)
            {
                ["N1"] = Draft("N1", "NUMBER(*,0)"),
                ["N2"] = Draft("N2", "INTEGER"),
                ["N3"] = Draft("N3", "NUMBER", precision: 10),
                ["N4"] = Draft("N4", "NUMBER", precision: 10, scale: 2),
                ["N5"] = Draft("N5", "NUMBER"),
                ["F1"] = Draft("F1", "FLOAT"),
                ["S1"] = Draft("S1", "VARCHAR2", 10, CharSemantics.Bytes), // 세션 기준 BYTE(실측)
                ["S2"] = Draft("S2", "VARCHAR2(10 CHAR)"),
                ["C1"] = Draft("C1", "CHAR", 3, CharSemantics.Characters),
                ["NS"] = Draft("NS", "NVARCHAR2", 5),
                ["NC"] = Draft("NC", "NCHAR(2)"),
                ["T1"] = Draft("T1", "TIMESTAMP"),
                ["T2"] = Draft("T2", "TIMESTAMP(3) WITH TIME ZONE"),
                ["T3"] = Draft("T3", "TIMESTAMP WITH LOCAL TIME ZONE"),
                ["I1"] = Draft("I1", "INTERVAL YEAR TO MONTH"),
                ["I2"] = Draft("I2", "INTERVAL DAY TO SECOND"),
                ["R1"] = Draft("R1", "RAW", 16),
            };
            foreach (var (name, user) in written)
            {
                var server = FromServer(c[name]);
                output.WriteLine($"{name}: server {Dialect.NormalizeType(server)} / written {Dialect.NormalizeType(user)}");
                Assert.True(Dialect.NormalizeType(server) == Dialect.NormalizeType(user), name + ": " + Dialect.NormalizeType(server) + " <> " + Dialect.NormalizeType(user));
            }

            Assert.Equal(Dialect.NormalizeDefault("'abc'"), Dialect.NormalizeDefault(c["D1"].DefaultValue));
            Assert.Equal(Dialect.NormalizeDefault("sysdate"), Dialect.NormalizeDefault(c["D2"].DefaultValue));
            Assert.Null(Dialect.NormalizeDefault(c["N5"].DefaultValue)); // DEFAULT NULL 뒤 글자 NULL
            // 서버는 우선순위 괄호를 더해 다시 쓴다: 그 꼴로 쓰면 같고, 괄호 없이 쓰면 다르게 본다(묶음 변경을 놓치지 않으려고 괄호는 지우지 않음).
            Assert.Equal(Dialect.NormalizeExpression("(n3*2) + nvl(n4, 0)"), Dialect.NormalizeExpression(c["V1"].DefaultValue));
            Assert.NotEqual(Dialect.NormalizeExpression("n3*2 + nvl(n4,0)"), Dialect.NormalizeExpression(c["V1"].DefaultValue));
            var check = info.Constraints.Single(k => k.Name == T("NZ_CK"));
            Assert.Equal(Dialect.NormalizeCondition("N3 > 0 AND S1 IN ('a','b')"), Dialect.NormalizeCondition(check.Condition));
            Assert.True(Dialect.IsServerName(info.Constraints.Single(k => k.Kind == ConstraintKind.PrimaryKey).Name), "server-named PK is not recognized");
            Assert.False(Dialect.IsServerName(check.Name));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>공급자 열 → 편집 모델 열(T1b 의 <c>TableDraftFactory.FromTable</c> 이 할 일 중 형식 칸만).</summary>
    private static ColumnDraft FromServer(ColumnInfo c) =>
        new(c.Name, c.Name) { DataType = c.DataType, Length = c.Length, CharSemantics = c.CharSemantics, Precision = c.Precision, Scale = c.Scale };

    private static ColumnDraft Draft(string name, string type, int? length = null, CharSemantics? semantics = null, int? precision = null, int? scale = null) =>
        new(name, name) { DataType = type, Length = length, CharSemantics = semantics, Precision = precision, Scale = scale };

    private async Task<(TiberoSession Session, TableEditProbe Probe)> OpenAsync(TiberoItSessions it)
    {
        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(owner), "the session user of the connection is unknown");
        return (session, new TableEditProbe(session, output, owner!.ToUpperInvariant(), it.Env.DbName));
    }
}
