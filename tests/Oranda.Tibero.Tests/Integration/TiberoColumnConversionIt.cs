using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 열 형식 변경 자동 변환 Tibero 통합 시험(설계 18.9, T17b): 행 단위 시험 질의(<see cref="TiberoColumnConversionChecks"/>)의 건수·「그 행 보기」·서버 오류·취소,
/// 변환한 값으로 센 CHECK·UK, 참조 FK, 바꿔치기 단계 실행 뒤 정의·데이터. 공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다
/// (시험 계정은 CREATE VIEW 권한이 없어 뷰는 쓰지 않음).
/// 환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoColumnConversionIt(ITestOutputHelper output)
{
    private static readonly TiberoColumnConversionChecks Conversions = new();

    private static readonly TiberoTableEditChecks Checks = new();

    private static readonly TiberoTableEditDialect Dialect = new();

    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    /// <summary>실측(probe 「변환 검사」 장): 행 단위 시험 질의가 쓰는 조각과 바꿔치기 문장 꼴이 Tibero 에서 되는지. 실패 번호를 회귀로 묶는다.</summary>
    [TiberoWriteFact]
    public async Task Probe_conversion_building_blocks()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (_, p) = await OpenAsync(it);
        var t = T("CVP");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, S VARCHAR2(20), N NUMBER(10,2), D VARCHAR2(20))");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 'abcd', 12345.67, '2026-01-31')");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, '12', 1.5, 'x')");
            await p.MustAsync($"INSERT INTO {t} VALUES (3, NULL, NULL, NULL)");
            await p.MustAsync("COMMIT");

            // VALIDATE_CONVERSION 없음 → 늘 행 단위 시험. CAST 는 문자를 자르므로(Oracle 과 같음) 검사는 변수 담기로 본다.
            await p.ExpectAsync("validate_conversion", "TBR-8007", $"SELECT COUNT(*) FROM {t} WHERE VALIDATE_CONVERSION(S AS NUMBER) = 0");
            Assert.Equal("abc", await p.ScalarAsync($"SELECT CAST(S AS VARCHAR2(3)) FROM {t} WHERE ID = 1"));
            await p.ExpectAsync("cast number overflow", "TBR-5111", $"SELECT CAST(N AS NUMBER(3,0)) FROM {t} WHERE ID = 1");
            await p.ExpectAsync("cast bad number", "TBR-5074", $"SELECT CAST(S AS NUMBER) FROM {t}");

            // 변수 담기: 넘침·자름·RAISE VALUE_ERROR 모두 TBR-15115, 반올림은 됨, CHAR 기준은 글자.
            await p.ExpectAsync("var truncate", "TBR-15115", "DECLARE v VARCHAR2(3); BEGIN v := 'abcd'; END;");
            await p.ExpectAsync("var number overflow", "TBR-15115", "DECLARE v NUMBER(3,0); BEGIN v := 12345.67; END;");
            await p.ExpectAsync("var number round", "OK", "DECLARE v NUMBER(5,0); BEGIN v := 12345.4; END;");
            await p.ExpectAsync("var byte truncate", "TBR-15115", "DECLARE v VARCHAR2(4 BYTE); BEGIN v := '가나'; END;");
            await p.ExpectAsync("var char sem", "OK", "DECLARE v VARCHAR2(2 CHAR); BEGIN v := '가나'; END;");
            await p.ExpectAsync("raise value_error", "TBR-15115", "BEGIN RAISE VALUE_ERROR; END;");

            // 동적 SQL: 글 변수로 OPEN FOR + BULK COLLECT LIMIT, EXECUTE IMMEDIATE INTO USING ROWID, 문법 오류는 OPEN 에서.
            await p.ExpectAsync("bulk collect truncate", "TBR-15115",
                $"DECLARE TYPE vl IS TABLE OF VARCHAR2(3); vs vl; c SYS_REFCURSOR; q VARCHAR2(32767) := 'SELECT (S) FROM {t} t'; BEGIN " +
                "OPEN c FOR q; LOOP FETCH c BULK COLLECT INTO vs LIMIT 500; EXIT WHEN vs.COUNT < 500; END LOOP; CLOSE c; END;");
            await p.ExpectAsync("bulk collect bad number", "TBR-15115",
                $"DECLARE TYPE vl IS TABLE OF NUMBER; vs vl; c SYS_REFCURSOR; q VARCHAR2(32767) := 'SELECT (S) FROM {t} t'; BEGIN " +
                "OPEN c FOR q; LOOP FETCH c BULK COLLECT INTO vs LIMIT 500; EXIT WHEN vs.COUNT < 500; END LOOP; CLOSE c; END;");
            await p.ExpectAsync("execute immediate into truncate", "TBR-15115",
                $"DECLARE v VARCHAR2(3); q VARCHAR2(32767) := 'SELECT (S) FROM {t} t WHERE t.ROWID = :rid'; BEGIN " +
                $"FOR r IN (SELECT ROWID AS rid FROM {t} WHERE ID = 1) LOOP EXECUTE IMMEDIATE q INTO v USING r.rid; END LOOP; END;");
            await p.ExpectAsync("open for syntax error", "TBR-8015",
                $"DECLARE c SYS_REFCURSOR; q VARCHAR2(32767) := 'SELECT (S +) FROM {t} t'; BEGIN OPEN c FOR q; CLOSE c; END;");
            Assert.Equal("2", await p.ScalarAsync($"SELECT ID FROM {t} WHERE ROWID IN (SELECT CHARTOROWID(ROWIDTOCHAR(ROWID)) FROM {t} WHERE ID = 2)"));

            // 바꿔치기 단계 꼴: 임시 열 이름 <열>$N, DROP COLUMN … CASCADE CONSTRAINTS, 확인 블록의 RAISE_APPLICATION_ERROR.
            await p.ExpectAsync("add temp column", "OK", $"ALTER TABLE {t} ADD (\"S$1\" NUMBER)");
            await p.ExpectAsync("update temp column bare", "OK", $"UPDATE {t} SET S$1 = 1");
            await p.ExpectAsync("rename temp column bare", "OK", $"ALTER TABLE {t} RENAME COLUMN S$1 TO S2");
            await p.ExpectAsync("rename back quoted", "OK", $"ALTER TABLE {t} RENAME COLUMN \"S2\" TO \"S$1\"");
            await p.ExpectAsync("drop column cascade constraints", "OK", $"ALTER TABLE {t} DROP COLUMN \"S$1\" CASCADE CONSTRAINTS");
            await p.ExpectAsync("raise_application_error", "TBR-20990", "BEGIN RAISE_APPLICATION_ERROR(-20990, 'stop'); END;");

            // 서버가 만드는 NOT NULL 조건 꼴(meta.constraints 는 SQL 에서 거르지 않고 MetadataRows.IsNotNullCheck 가 뺀다): 이름 붙인 NOT NULL·ALTER MODIFY NOT NULL 도 같은 꼴.
            var nn = T("CVN");
            await p.DropTableAsync(nn);
            try
            {
                await p.MustAsync($"CREATE TABLE {nn} (A NUMBER CONSTRAINT {T("CVN_NN")} NOT NULL, B NUMBER, C NUMBER NOT NULL)");
                await p.MustAsync($"ALTER TABLE {nn} MODIFY (B NOT NULL)");
                var conditions = await p.RowsAsync($"SELECT constraint_name, search_condition FROM user_constraints WHERE table_name = '{nn}' AND constraint_type = 'C'");
                Assert.Equal(3, conditions.Count);
                Assert.All(conditions, r => Assert.True(Oranda.Core.Metadata.MetadataRows.IsNotNullCheck(r[1]), $"{r[0]}: [{r[1]}]"));
            }
            finally
            {
                await p.DropTableAsync(nn);
            }
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>「변환 안 되는 행」: 숫자가 아님·NUMBER(1) 넘침을 행마다 세고, NOT NULL 대상이면 NULL 행도, 「그 행 보기」는 실패한 행만.</summary>
    [TiberoWriteFact]
    public async Task Failing_rows_are_counted_with_view_rows_sql_and_not_null_rows()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("CVB");
        var table = new DbObjectRef(p.Owner, t, "TABLE");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, V VARCHAR2(20))");
            foreach (var row in new[] { "1, '1'", "2, 'x2'", "3, '3'", "4, '4.5x'", "5, '12'", "6, NULL" })
            {
                await p.MustAsync($"INSERT INTO {t} VALUES ({row})");
            }

            await p.MustAsync("COMMIT");
            var source = new ColumnDraft("c2", "V") { DataType = "VARCHAR2", Length = 20 };
            var target = new ColumnDraft("c2", "V") { DataType = "NUMBER", Precision = 1 };
            var ct = CancellationToken.None;

            var none = await Conversions.FailingRowsAsync(session, table, source, new ColumnDraft("c2", "V") { DataType = "VARCHAR2", Length = 10 }, "v", ct);
            var failures = await Conversions.FailingRowsAsync(session, table, source, target, ColumnConversionDefaults.Expression(source, target), ct);
            var notNull = await Conversions.FailingRowsAsync(session, table, source, target with { IsNullable = false }, "TO_NUMBER(v) * 1", ct);
            var shrink = await Conversions.FailingRowsAsync(session, table, source, new ColumnDraft("c2", "V") { DataType = "VARCHAR2", Length = 1 }, "v", ct);

            Assert.Equal((0L, false, null), (none.Count, none.IsLowerBound, none.ViewRowsSql));
            Assert.Equal((3L, false), (failures.Count, failures.IsLowerBound)); // 'x2'·'4.5x' 는 숫자가 아니고 '12' 는 NUMBER(1) 를 넘친다
            Assert.Matches("(TBR|ODP)-5074", failures.FirstError);
            Assert.Equal("2,4,5", Ids(await p.RowsAsync($"SELECT id FROM ({failures.ViewRowsSql}) ORDER BY id")));
            Assert.Equal(4, notNull.Count); // NULL 행도 NOT NULL 에 못 들어간다
            Assert.Equal("2,4,5,6", Ids(await p.RowsAsync($"SELECT id FROM ({notNull.ViewRowsSql}) ORDER BY id")));
            Assert.Equal(3, shrink.Count); // 'x2'·'4.5x'·'12' 는 VARCHAR2(1) 를 넘친다(CAST 처럼 자르지 않음)
            output.WriteLine($"{failures.Count}건, {failures.FirstError}\n{failures.ViewRowsSql}");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>틀린 변환식 = 동적 SQL 이 행마다 실패 → 모든 행 + 서버 오류, 새 형식이 틀리면 블록 자체가 실패 → 「1건 이상」. 세션은 그대로 쓸 수 있다.</summary>
    [TiberoWriteFact]
    public async Task Wrong_expression_or_type_fails_with_the_server_error()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("CVW");
        var table = new DbObjectRef(p.Owner, t, "TABLE");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, V VARCHAR2(20))");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, '1')");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, '2')");
            await p.MustAsync("COMMIT");
            var source = new ColumnDraft("c2", "V") { DataType = "VARCHAR2", Length = 20 };

            var wrong = await Conversions.FailingRowsAsync(session, table, source, new ColumnDraft("c2", "V") { DataType = "NUMBER" }, "TO_NUMBER(v", default);
            var badType = await Conversions.FailingRowsAsync(session, table, source, new ColumnDraft("c2", "V") { DataType = "NO_SUCH_TYPE" }, "v", default);

            Assert.Equal((2L, false), (wrong.Count, wrong.IsLowerBound));
            Assert.Matches("(TBR|ODP)-8015", wrong.FirstError);
            Assert.True(badType.IsLowerBound);
            Assert.False(string.IsNullOrEmpty(badType.FirstError));
            Assert.Equal(2, await Checks.CountRowsAsync(session, table, default));
            output.WriteLine($"wrong: {wrong.FirstError}\nbad type: {badType.FirstError}");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>다시 만들 제약을 변환한 값으로 센다: '1'·'01' 은 숫자로 같은 값 → UK 중복 2행, 숫자 열에 <c>code &lt;&gt; 'x'</c> = 계산 오류 → 「1건 이상」.</summary>
    [TiberoWriteFact]
    public async Task Restored_constraints_are_checked_with_the_converted_values()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("CVC");
        var table = new DbObjectRef(p.Owner, t, "TABLE");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, CODE VARCHAR2(10) CONSTRAINT {T("CVC_UK")} UNIQUE)");
            foreach (var row in new[] { "1, '1'", "2, '01'", "3, '2'", "4, NULL", "5, NULL" })
            {
                await p.MustAsync($"INSERT INTO {t} VALUES ({row})");
            }

            await p.MustAsync("COMMIT");
            string[] others = ["ID"];
            var ct = CancellationToken.None;

            var duplicates = await Conversions.ConvertedDuplicateKeysAsync(session, table, others, "CODE", "TO_NUMBER(code)", ["CODE"], ct);
            var noDuplicates = await Conversions.ConvertedDuplicateKeysAsync(session, table, others, "CODE", "code", ["CODE"], ct);
            var check = await Conversions.ConvertedCheckViolationsAsync(session, table, others, "CODE", "TO_NUMBER(code)", "code <> 'x'", ct);
            var checkOk = await Conversions.ConvertedCheckViolationsAsync(session, table, others, "CODE", "TO_NUMBER(code)", "code > 0 AND id > 0 -- 양수", ct);
            var violated = await Conversions.ConvertedCheckViolationsAsync(session, table, others, "CODE", "TO_NUMBER(code)", "code > 1", ct);

            Assert.Equal(2, duplicates.Count); // NULL 두 행은 빼고
            Assert.Equal(0, noDuplicates.Count);
            Assert.True(check.IsLowerBound);
            Assert.Matches("(TBR|ODP)-5074", check.FirstError); // 숫자 열에 'x' 비교
            Assert.Equal(0, checkOk.Count); // 끝 주석이 닫는 괄호를 먹지 않는다, NULL 은 UNKNOWN
            Assert.Equal(2, violated.Count); // 1, 01
            output.WriteLine($"check: {check.FirstError}");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>참조 FK 는 그 PK·UK 열에서만 잡힌다(바꿔치기 3단계 CASCADE CONSTRAINTS 가 지워 버림).</summary>
    [TiberoWriteFact]
    public async Task Referencing_foreign_keys_block_only_the_referenced_column()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var parent = T("CVFP");
        var child = T("CVFC");
        await p.DropTableAsync(child);
        await p.DropTableAsync(parent);
        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (ID NUMBER PRIMARY KEY, CODE VARCHAR2(10) CONSTRAINT {T("CVFP_UK")} UNIQUE)");
            await p.MustAsync($"CREATE TABLE {child} (ID NUMBER, PCODE VARCHAR2(10) CONSTRAINT {T("CVFC_FK")} REFERENCES {parent} (CODE))");
            var parentRef = new DbObjectRef(p.Owner, parent, "TABLE");

            Assert.Equal([new ForeignKeyRef(p.Owner, child, T("CVFC_FK"))], await Conversions.ReferencingForeignKeysAsync(session, parentRef, "CODE", default));
            Assert.Empty(await Conversions.ReferencingForeignKeysAsync(session, parentRef, "ID", default));
        }
        finally
        {
            await p.DropTableAsync(child);
            await p.DropTableAsync(parent);
        }
    }

    /// <summary>취소: 1차 블록이 도는 중 토큰이 오면 서버 명령이 멈추고 <see cref="OperationCanceledException"/>(블록이 취소 오류를 삼키지 않음). 세션은 그대로 쓸 수 있다.</summary>
    [TiberoWriteFact]
    public async Task Cancelling_the_row_test_cancels_the_server_command()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("CVS");
        var table = new DbObjectRef(p.Owner, t, "TABLE");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} AS SELECT ROWNUM AS ID, TO_CHAR(ROWNUM) AS V FROM all_objects a, all_objects b WHERE ROWNUM <= 20000");
            var source = new ColumnDraft("c2", "V") { DataType = "VARCHAR2", Length = 40 };
            const string slow = "TO_CHAR((SELECT COUNT(*) FROM all_objects a WHERE a.object_id + t.id > 0))"; // 행마다 사전 한 번 훑기(행 하나는 짧게)
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var run = Conversions.FailingRowsAsync(session, table, source, source with { Length = 40 }, slow, cts.Token);
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(180))); // Tibero 는 블록 취소를 늦게 듣는다(실측 29~56초)

            Assert.Same(run, finished);
            var ex = await Record.ExceptionAsync(() => run);
            output.WriteLine($"cancel after {clock.ElapsedMilliseconds} ms: {ex?.GetType().Name} {ex?.InnerException?.Message}");
            Assert.IsAssignableFrom<OperationCanceledException>(ex);

            // 비교: 같은 식의 일반 SQL 건수 질의(블록 아님) 취소.
            using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            clock.Restart();
            var ex2 = await Record.ExceptionAsync(() => Conversions.ConvertedCheckViolationsAsync(session, table, ["ID"], "V", slow, "v IS NULL", cts2.Token));
            output.WriteLine($"sql cancel after {clock.ElapsedMilliseconds} ms: {ex2?.GetType().Name} {ex2?.InnerException?.Message}");
            Assert.IsAssignableFrom<OperationCanceledException>(ex2);
            Assert.Equal(20000, await Checks.CountRowsAsync(session, table, default));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>
    /// 바꿔치기 단계 1~6 을 Tibero 에서 차례로 실행: 형식·NOT NULL·기본값·주석·UK·CHECK·인덱스 3개(일반·함수 기반·DESC)가 되살아나고 값이 바뀐다(열은 맨 뒤로).
    /// Tibero 는 DROP COLUMN 뒤에도 함수 기반 인덱스를 남기므로(그대로면 4단계 RENAME 이 TBR-8026) 3단계가 먼저 지운다.
    /// </summary>
    [TiberoWriteFact]
    public async Task Swap_converts_text_to_number_and_restores_definition_constraints_and_indexes()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("CVX");
        var table = new DbObjectRef(p.Owner, t, "TABLE");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"""
                CREATE TABLE {t} (
                  ID NUMBER CONSTRAINT {T("CVX_PK")} PRIMARY KEY,
                  ORDER_NO VARCHAR2(20 CHAR) DEFAULT '0' NOT NULL,
                  CODE VARCHAR2(10),
                  AMOUNT NUMBER(10,2),
                  CONSTRAINT {T("CVX_UK")} UNIQUE (ORDER_NO, CODE),
                  CONSTRAINT {T("CVX_CK")} CHECK (ID > 0 OR ORDER_NO IS NOT NULL)
                )
                """);
            await p.MustAsync($"COMMENT ON COLUMN {t}.ORDER_NO IS '주문 번호'");
            await p.MustAsync($"CREATE INDEX {T("CVX_IX1")} ON {t} (ORDER_NO, ID)");
            await p.MustAsync($"CREATE INDEX {T("CVX_IX2")} ON {t} (UPPER(ORDER_NO))");
            await p.MustAsync($"CREATE INDEX {T("CVX_IX3")} ON {t} (CODE, ORDER_NO DESC)");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, '10', 'a', 1.5)");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, ' 20 ', 'b', 12345678.25)");
            await p.MustAsync($"INSERT INTO {t} VALUES (3, '30', NULL, NULL)");
            await p.MustAsync("COMMIT");

            var provider = TableEditProbe.NewProvider();
            var original = TableDraftFactory.FromTable(await provider.GetTableAsync(session, table, default), Dialect);
            Assert.Contains(original.Constraints, k => k.Name == T("CVX_CK")); // 끝이 IS NOT NULL 인 사용자 CHECK 도 읽는다
            var source = original.Columns.Single(c => c.Name == "ORDER_NO");
            var target = source with { DataType = "NUMBER", Length = null, CharSemantics = null, Precision = 12, Scale = null, DefaultValue = "0" };
            var request = new ColumnConversionRequest(original, source.RowKey, target, ColumnConversionDefaults.Expression(source, target))
            {
                Dependents = await Checks.DependentsAsync(session, table, default),
            };

            var result = await ColumnConversionCheckRunner.RunAsync(request, ColumnConversionMethod.ColumnSwap, Dialect, Conversions, Checks, session,
                new TableEditCheckOptions { Server = session.Server }, default);
            Assert.False(result.IsBlocked, string.Join("; ", result.Findings.Select(f => $"{f.Severity} {f.Code} {f.Subject} {f.Detail}")));

            var plan = ColumnConversionBuilder.BuildSwap(request, Dialect, session.Server);
            output.WriteLine(plan.RemainingScript(0));
            foreach (var statement in plan.Statements)
            {
                var outcome = await p.RunAsync("swap", statement.Text);
                if (!outcome.Ok)
                {
                    await p.TryDumpAsync("cols", $"SELECT column_name, hidden_column, virtual_column, internal_column_id FROM user_tab_cols WHERE table_name = '{t}' ORDER BY internal_column_id");
                    await p.TryDumpAsync("idx", $"SELECT index_name, column_name FROM user_ind_columns WHERE table_name = '{t}'");
                    await p.TryDumpAsync("expr", $"SELECT index_name, column_expression FROM user_ind_expressions WHERE table_name = '{t}'");
                    await p.TryDumpAsync("cols2", $"SELECT * FROM user_tab_columns WHERE table_name = '{t}'");
                    Assert.Fail(outcome.ToString());
                }
            }

            var after = await TableEditProbe.NewProvider().GetTableAsync(session, table, default);
            var column = after.Columns.Single(c => c.Name == "ORDER_NO");
            Assert.Equal(("NUMBER", 12, false, "0", "주문 번호"), (column.DataType, column.Precision, column.IsNullable, column.DefaultValue?.Trim(), column.Comment));
            Assert.Equal(after.Columns.Count, column.Position); // 바꿔치기는 열이 맨 뒤로 간다
            Assert.Contains(after.Constraints, c => c.Name == T("CVX_UK") && c.Columns.SequenceEqual(["ORDER_NO", "CODE"]));
            Assert.Contains(after.Constraints, c => c.Name == T("CVX_CK"));
            Assert.Contains(after.Indexes, i => i.Name == T("CVX_IX1"));
            Assert.Contains(after.Indexes, i => i.Name == T("CVX_IX2"));
            Assert.Contains(after.Indexes, i => i.Name == T("CVX_IX3") && i.Columns.Count == 2);
            Assert.Equal("10,20,30", Ids(await p.RowsAsync($"SELECT TO_CHAR(ORDER_NO) FROM {t} ORDER BY ID")));
            Assert.Empty(ColumnConversionBuilder.Findings(request, ColumnConversionMethod.ColumnSwap, Dialect, session.Server)
                .Where(f => f.Severity == CheckSeverity.Block));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    private static string Ids(List<string?[]> rows) => string.Join(",", rows.Select(r => r[0]));

    private async Task<(TiberoSession Session, TableEditProbe Probe)> OpenAsync(TiberoItSessions it)
    {
        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(owner), "the session user of the connection is unknown");
        return (session, new TableEditProbe(session, output, owner!.ToUpperInvariant(), it.Env.DbName));
    }
}
