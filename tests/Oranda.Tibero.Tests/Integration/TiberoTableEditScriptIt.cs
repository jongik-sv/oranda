#pragma warning disable CA1861 // 시험 입력용 상수 배열
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 테이블 편집기 Core(차이·문장·검사 계획, T1b)를 Tibero 방언으로 실제 서버에 돌린다(설계 14장 Tibero 줄, 계획 Task 4 뒷부분).
/// <list type="bullet">
/// <item>실측 테이블 여러 개: 편집을 켜고 안 고치면 문장 0개(거짓 차이 없음, 설계 8.4).</item>
/// <item>8.2 문장 꼴 각각(열 추가·지우기·이름·MODIFY·기본값 없애기·가상 열·가상 식 변경 = DROP+ADD·주석·제약 추가·지우기·사용·인덱스 만들기·지우기·이름)이 서버에서 되고,
/// 다시 읽으면 0문장.</item>
/// <item>검사 계획 ✖(넘침·NULL 행·데이터 있는 형식 변경·중복 키·짝 없는 행·어기는 행)이 Tibero 검사 읽기로 건수와 함께 나온다.</item>
/// </list>
/// 공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTableEditScriptIt(ITestOutputHelper output)
{
    private static readonly TiberoTableEditDialect Dialect = new();

    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    /// <summary>실측 2·5·7장의 꼴을 담은 테이블들: 안 고치면 0문장.</summary>
    [TiberoWriteFact]
    public async Task Untouched_real_tables_produce_no_statements()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var parent = T("U0");
        string[] tables = [T("U1"), T("U2"), T("U3"), T("U4")];
        foreach (var t in tables.Append(parent))
        {
            await p.DropTableAsync(t);
        }

        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (A NUMBER, B NUMBER, CONSTRAINT {T("U0_PK")} PRIMARY KEY (A, B))");
            await p.MustAsync($"""
                CREATE TABLE {tables[0]} (
                  id number primary key,
                  n1 NUMBER(*,0), n2 integer, n3 NUMBER(10), n4 NUMBER(10,2), n5 NUMBER, f1 FLOAT, f2 FLOAT(10),
                  s1 varchar2(10), s2 VARCHAR2(10 CHAR), c1 CHAR(3 CHAR), ns NVARCHAR2(5), nc NCHAR(2),
                  t1 TIMESTAMP, t2 TIMESTAMP(3) WITH TIME ZONE, t3 TIMESTAMP WITH LOCAL TIME ZONE,
                  i1 INTERVAL YEAR TO MONTH, i2 INTERVAL DAY TO SECOND, r1 RAW(16), b1 BLOB, k1 CLOB,
                  d1 VARCHAR2(10) DEFAULT
                     'abc'  ,
                  d2 DATE default sysdate, d3 NUMBER DEFAULT (1 + 2), d4 VARCHAR2(10) DEFAULT 'It''s',
                  v1 AS (n3*2 + nvl(n4,0)) virtual,
                  v2 VARCHAR2(20) AS (upper(s1) || 'x') VIRTUAL,
                  CONSTRAINT {T("U1_CK")} check (n3 > 0 and s1 in ('a','b')),
                  CONSTRAINT {T("U1_CK2")} CHECK (s2 like 'A%'
                     OR s2 IS NULL)
                )
                """);
            await p.MustAsync($"ALTER TABLE {tables[0]} MODIFY (n5 DEFAULT NULL)");
            await p.MustAsync($"ALTER TABLE {tables[0]} MODIFY (s1 NOT NULL)");
            await p.MustAsync($"COMMENT ON TABLE {tables[0]} IS '주문 실측'");
            await p.MustAsync($"COMMENT ON COLUMN {tables[0]}.s1 IS 'it''s'");

            await p.MustAsync($"CREATE TABLE {tables[1]} (ID NUMBER, PA NUMBER, PB NUMBER, U VARCHAR2(5), Q NUMBER, " +
                              $"CONSTRAINT {T("U2_FK")} FOREIGN KEY (PA, PB) REFERENCES {parent} (A, B) ON DELETE CASCADE, " +
                              $"UNIQUE (U), CHECK (Q > 0) DISABLE)");
            await p.MustAsync($"ALTER TABLE {tables[1]} ADD CONSTRAINT {T("U2_FK2")} FOREIGN KEY (Q, ID) REFERENCES {parent} (A, B) ON DELETE SET NULL ENABLE NOVALIDATE");
            await p.MustAsync($"CREATE INDEX {T("U2_D")} ON {tables[1]} (U DESC, ID)");
            await p.MustAsync($"CREATE INDEX {T("U2_F")} ON {tables[1]} (UPPER(U))");
            await p.MustAsync($"CREATE UNIQUE INDEX {T("U2_X")} ON {tables[1]} (Q, ID)");
            await p.MustAsync($"CREATE BITMAP INDEX {T("U2_B")} ON {tables[1]} (PA)");

            await p.MustAsync($"CREATE GLOBAL TEMPORARY TABLE {tables[2]} (ID NUMBER, S VARCHAR2(10 CHAR) DEFAULT 'x') ON COMMIT PRESERVE ROWS");
            await p.MustAsync($"CREATE TABLE {tables[3]} (ID NUMBER CONSTRAINT {T("U4_PK")} PRIMARY KEY, N NUMBER NOT NULL) ORGANIZATION INDEX");

            foreach (var t in tables)
            {
                var (_, draft) = await ReadAsync(session, p.Owner, t);
                var script = TableEditScriptBuilder.Build(draft, draft, Dialect);
                var changes = TableDraftDiff.Compute(draft, draft, Dialect);
                foreach (var s in script.Statements)
                {
                    p.Log(t, "unexpected: " + s.Statement.Text);
                }

                Assert.True(script.IsEmpty, t + " produced statements without an edit");
                Assert.True(changes.IsEmpty, t + " has changes without an edit");

                // 복사한 원본(같은 값, 다른 객체)과도 0 — 참조 같음에 기대지 않는다.
                var copy = draft with
                {
                    Columns = [.. draft.Columns.Select(c => c with { })],
                    Constraints = [.. draft.Constraints.Select(k => k with { ColumnKeys = [.. k.ColumnKeys], ReferencedColumns = [.. k.ReferencedColumns] })],
                    Indexes = [.. draft.Indexes.Select(i => i with { Columns = [.. i.Columns.Select(c => c with { })] })],
                };
                Assert.True(TableEditScriptBuilder.Build(draft, copy, Dialect).IsEmpty, t + " copy produced statements");
            }
        }
        finally
        {
            foreach (var t in tables.Append(parent))
            {
                await p.DropTableAsync(t);
            }
        }
    }

    /// <summary>설계 8.2 표의 문장 꼴을 단계마다 만들어 보내고(모두 성공), 다시 읽은 원본에서 그 결과를 확인한다.</summary>
    [TiberoWriteFact]
    public async Task Section_8_2_statements_run_on_tibero_step_by_step()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var parent = T("S0");
        var t = T("S1");
        await p.DropTableAsync(t);
        await p.DropTableAsync(parent);
        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (A NUMBER CONSTRAINT {T("S0_PK")} PRIMARY KEY)");
            await p.MustAsync($"INSERT INTO {parent} VALUES (1)");
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, A NUMBER, S VARCHAR2(10), OLDC NUMBER)");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 1, 'ab', 5)");
            await p.MustAsync("COMMIT");

            // 1. 열 추가(기본값·NOT NULL·가상 열), 열 지우기, 열 이름.
            var d = await StepAsync(session, p, t, "1", x => x with
            {
                Columns =
                [
                    .. x.Columns.Select(c => c.Name switch
                    {
                        "OLDC" => c with { IsDropped = true },
                        "A" => c with { Name = "PARENT_A" },
                        _ => c,
                    }),
                    new ColumnDraft(DraftKeys.New(), "NOTE") { DataType = "VARCHAR2", Length = 20, CharSemantics = CharSemantics.Characters, DefaultValue = "'x'", IsNullable = false },
                    new ColumnDraft(DraftKeys.New(), "TWICE") { VirtualExpression = "ID * 2" },
                ],
            });
            Assert.Equal(["ID", "PARENT_A", "S", "NOTE", "TWICE"], d.Columns.Select(c => c.Name));
            Assert.Equal((CharSemantics.Characters, false, "'x'"), (Col(d, "NOTE").CharSemantics, Col(d, "NOTE").IsNullable, Col(d, "NOTE").DefaultValue));
            Assert.True(Col(d, "TWICE").IsVirtual);

            // 2. MODIFY: 길이 늘리기 + NULL 허용 + 기본값 없애기(한 열 = MODIFY 하나), 다른 열 형식(빈 열 아님, 넓히기).
            d = await StepAsync(session, p, t, "2", x => x with
            {
                Columns = [.. x.Columns.Select(c => c.Name switch
                {
                    "NOTE" => c with { Length = 40, IsNullable = true, DefaultValue = null },
                    "S" => c with { Length = 30 },
                    _ => c,
                })],
            });
            Assert.Equal((40, true, (string?)null), (Col(d, "NOTE").Length, Col(d, "NOTE").IsNullable, Dialect.NormalizeDefault(Col(d, "NOTE").DefaultValue)));
            Assert.Equal((30, CharSemantics.Bytes), (Col(d, "S").Length!.Value, Col(d, "S").CharSemantics!.Value)); // 원래 기준(BYTE)을 지킴(설계 12.1)

            // 3. 가상 식 변경 = DROP + ADD(Tibero 는 MODIFY 를 못 함, 실측 1장).
            var virtualScript = Script(d, d with { Columns = [.. d.Columns.Select(c => c.Name == "TWICE" ? c with { VirtualExpression = "ID * 3" } : c)] });
            Assert.Contains(virtualScript, s => s.Contains("DROP COLUMN", StringComparison.Ordinal));
            d = await StepAsync(session, p, t, "3", x => x with { Columns = [.. x.Columns.Select(c => c.Name == "TWICE" ? c with { VirtualExpression = "ID * 3" } : c)] });
            Assert.Equal(Dialect.NormalizeExpression("(\"ID\" * 3)"), Dialect.NormalizeExpression(Col(d, "TWICE").VirtualExpression));

            // 4. 주석(테이블·열), 지우기는 빈 글.
            d = await StepAsync(session, p, t, "4", x => x with
            {
                Comment = "시험 테이블",
                Columns = [.. x.Columns.Select(c => c.Name == "S" ? c with { Comment = "글 'q'" } : c)],
            });
            Assert.Equal(("시험 테이블", "글 'q'"), (d.Comment, Col(d, "S").Comment));

            // 5. 제약 추가: PK·UK·CHECK·FK(ON DELETE SET NULL), 사용 끄기.
            var key = (string name) => Col(d, name).RowKey;
            d = await StepAsync(session, p, t, "5", x => x with
            {
                Constraints =
                [
                    .. x.Constraints,
                    new ConstraintDraft(DraftKeys.New(), T("S1_PK"), ConstraintKind.PrimaryKey) { ColumnKeys = [key("ID")] },
                    new ConstraintDraft(DraftKeys.New(), T("S1_UK"), ConstraintKind.Unique) { ColumnKeys = [key("S")] },
                    new ConstraintDraft(DraftKeys.New(), T("S1_CK"), ConstraintKind.Check) { Condition = "ID > 0", IsEnabled = false },
                    new ConstraintDraft(DraftKeys.New(), T("S1_FK"), ConstraintKind.ForeignKey)
                    {
                        ColumnKeys = [key("PARENT_A")],
                        ReferencedTable = new DbObjectRef(p.Owner, parent, "TABLE"),
                        ReferencedColumns = ["A"],
                        DeleteRule = DeleteRule.SetNull,
                    },
                ],
            });
            var byName = d.Constraints.ToDictionary(c => c.Name);
            Assert.Equal(DeleteRule.SetNull, byName[T("S1_FK")].DeleteRule);
            Assert.False(byName[T("S1_CK")].IsEnabled);

            // 6. 제약 사용 켜기·지우기.
            d = await StepAsync(session, p, t, "6", x => x with
            {
                Constraints = [.. x.Constraints.Select(c => c.Name == T("S1_CK") ? c with { IsEnabled = true } : c.Name == T("S1_UK") ? c with { IsDropped = true } : c)],
            });
            Assert.True(d.Constraints.Single(c => c.Name == T("S1_CK")).IsEnabled);
            Assert.DoesNotContain(d.Constraints, c => c.Name == T("S1_UK"));

            // 7. 인덱스 만들기(DESC 열 포함)·UNIQUE.
            d = await StepAsync(session, p, t, "7", x => x with
            {
                Indexes =
                [
                    .. x.Indexes,
                    new IndexDraft(DraftKeys.New(), T("S1_IX")) { Columns = [new IndexColumnDraft(Col(x, "S").RowKey, Descending: true), new IndexColumnDraft(Col(x, "NOTE").RowKey)] },
                    new IndexDraft(DraftKeys.New(), T("S1_UX")) { IsUnique = true, Columns = [new IndexColumnDraft(Col(x, "NOTE").RowKey), new IndexColumnDraft(Col(x, "ID").RowKey)] },
                ],
            });
            var ix = d.Indexes.Single(i => i.Name == T("S1_IX"));
            Assert.Equal([true, false], ix.Columns.Select(c => c.Descending));

            // 8. 인덱스 이름 바꾸기·지우기, 열 지우기(가상 열).
            d = await StepAsync(session, p, t, "8", x => x with
            {
                Indexes = [.. x.Indexes.Select(i => i.Name == T("S1_IX") ? i with { Name = T("S1_IX2") } : i.Name == T("S1_UX") ? i with { IsDropped = true } : i)],
                Columns = [.. x.Columns.Select(c => c.Name == "TWICE" ? c with { IsDropped = true } : c)],
            });
            Assert.Equal([T("S1_IX2"), T("S1_PK")], d.Indexes.Select(i => i.Name).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(d.Columns, c => c.Name == "TWICE");
        }
        finally
        {
            await p.DropTableAsync(t);
            await p.DropTableAsync(parent);
        }
    }

    /// <summary>검사 계획(설계 10장 ✖) + Tibero 검사 읽기: 넘침·NULL 행·데이터 있는 형식/정밀도 변경·중복 키·짝 없는 행·어기는 행이 건수와 함께 막힌다.</summary>
    [TiberoWriteFact]
    public async Task Check_plan_blocks_with_counts_from_tibero_reads()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var parent = T("K0");
        var t = T("K1");
        await p.DropTableAsync(t);
        await p.DropTableAsync(parent);
        try
        {
            await p.MustAsync($"CREATE TABLE {parent} (A NUMBER CONSTRAINT {T("K0_PK")} PRIMARY KEY)");
            await p.MustAsync($"INSERT INTO {parent} VALUES (1)");
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, S VARCHAR2(20), NL VARCHAR2(5), N NUMBER(10,2), PA NUMBER, Q NUMBER, K VARCHAR2(5))");
            foreach (var row in new[] { "1, 'abcdefgh', NULL, 1.5, 1, 5, 'a'", "2, 'ab', 'x', 2.5, 9, -1, 'a'", "3, NULL, NULL, NULL, NULL, 2, 'b'" })
            {
                await p.MustAsync($"INSERT INTO {t} VALUES ({row})");
            }

            await p.MustAsync("COMMIT");
            var (_, original) = await ReadAsync(session, p.Owner, t);
            var c = original.Columns.ToDictionary(x => x.Name);
            var edited = original with
            {
                Columns = [.. original.Columns.Select(x => x.Name switch
                {
                    "S" => x with { Length = 5 },
                    "NL" => x with { IsNullable = false },
                    "N" => x with { Precision = 10, Scale = 0 },
                    _ => x,
                })],
                Constraints =
                [
                    new ConstraintDraft(DraftKeys.New(), T("K1_UK"), ConstraintKind.Unique) { ColumnKeys = [c["K"].RowKey] },
                    new ConstraintDraft(DraftKeys.New(), T("K1_CK"), ConstraintKind.Check) { Condition = "Q > 0" },
                    new ConstraintDraft(DraftKeys.New(), T("K1_FK"), ConstraintKind.ForeignKey)
                    {
                        ColumnKeys = [c["PA"].RowKey], ReferencedTable = new DbObjectRef(p.Owner, parent, "TABLE"), ReferencedColumns = ["A"],
                    },
                ],
            };

            var options = new TableEditCheckOptions { Server = session.Server };
            var plan = TableEditCheckPlanner.Plan(original, edited, Dialect, options);
            var findings = await TableEditCheckRunner.RunAsync(plan, new DbObjectRef(p.Owner, t, "TABLE"), new TiberoTableEditChecks(), session, options, CancellationToken.None);
            foreach (var f in findings)
            {
                p.Log("check", $"{f.Severity} {f.Code} {f.Subject} {f.Count} {f.Detail}");
            }

            long? Count(CheckCode code) => findings.Single(f => f.Severity == CheckSeverity.Block && f.Code == code).Count;
            Assert.Equal(1, Count(CheckCode.Overflow)); // 'abcdefgh' > 5
            Assert.Equal(2, Count(CheckCode.NullRows));
            Assert.Equal(2, Count(CheckCode.IncompatibleTypeChange)); // 정밀도·소수 줄이기 = 데이터 있음(실측 3장 7238)
            Assert.Equal(2, Count(CheckCode.DuplicateKeys));
            Assert.Equal(1, Count(CheckCode.CheckViolations));
            Assert.Equal(1, Count(CheckCode.OrphanRows));

            // 건수 = 「그 행 보기」 SELECT 의 행 수(검사 읽기와 Core 조건이 같은 행을 본다).
            foreach (var f in findings.Where(f => f.Severity == CheckSeverity.Block))
            {
                Assert.False(string.IsNullOrEmpty(f.ViewRowsSql), f.Code + " has no view-rows SQL");
                Assert.Equal(f.Count?.ToString(System.Globalization.CultureInfo.InvariantCulture), await p.ScalarAsync($"SELECT COUNT(*) FROM ({f.ViewRowsSql})"));
            }

            // ✖ 판정이 실제 서버 실패와 맞는지(실측 번호).
            foreach (var (sql, code) in new[]
            {
                ($"ALTER TABLE {t} MODIFY (S VARCHAR2(5))", "TBR-7413"),
                ($"ALTER TABLE {t} MODIFY (NL NOT NULL)", "TBR-7045"),
                ($"ALTER TABLE {t} MODIFY (N NUMBER(10,0))", "TBR-7238"),
                ($"ALTER TABLE {t} ADD CONSTRAINT {T("K1_UK")} UNIQUE (K)", "TBR-7161"),
                ($"ALTER TABLE {t} ADD CONSTRAINT {T("K1_CK")} CHECK (Q > 0)", "TBR-7161"),
                ($"ALTER TABLE {t} ADD CONSTRAINT {T("K1_FK")} FOREIGN KEY (PA) REFERENCES {parent} (A)", "TBR-7161"),
            })
            {
                await p.ExpectAsync("check vs server", code, sql);
            }
        }
        finally
        {
            await p.DropTableAsync(t);
            await p.DropTableAsync(parent);
        }
    }

    private static ColumnDraft Col(TableDraft d, string name) => d.Columns.Single(c => c.Name == name);

    private static List<string> Script(TableDraft original, TableDraft edited) =>
        [.. TableEditScriptBuilder.Build(original, edited, Dialect).Statements.Select(s => s.Statement.Text)];

    /// <summary>원본을 읽고 편집해 문장을 보낸 뒤(모두 성공해야 함) 다시 읽은 원본을 돌려준다. 다시 읽은 원본은 안 고치면 0문장.</summary>
    private static async Task<TableDraft> StepAsync(TiberoSession session, TableEditProbe p, string table, string label, Func<TableDraft, TableDraft> edit)
    {
        var (_, original) = await ReadAsync(session, p.Owner, table);
        var statements = Script(original, edit(original));
        Assert.NotEmpty(statements);
        foreach (var sql in statements)
        {
            await p.ExpectAsync("8.2 step " + label, "OK", sql);
        }

        var (_, after) = await ReadAsync(session, p.Owner, table);
        Assert.True(TableEditScriptBuilder.Build(after, after, Dialect).IsEmpty, "step " + label + ": re-read table is not clean");
        return after;
    }

    private static async Task<(TableInfo Info, TableDraft Draft)> ReadAsync(TiberoSession session, string owner, string table)
    {
        var info = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(owner, table, "TABLE"), CancellationToken.None);
        return (info, TableDraftFactory.FromTable(info, Dialect));
    }

    private async Task<(TiberoSession Session, TableEditProbe Probe)> OpenAsync(TiberoItSessions it)
    {
        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(owner), "the session user of the connection is unknown");
        return (session, new TableEditProbe(session, output, owner!.ToUpperInvariant(), it.Env.DbName));
    }
}
