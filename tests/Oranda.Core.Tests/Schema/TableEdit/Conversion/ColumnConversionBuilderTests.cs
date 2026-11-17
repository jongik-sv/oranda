using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Xunit;
using static Oranda.Core.Tests.Schema.TableEdit.Samples;

namespace Oranda.Core.Tests.Schema.TableEdit.Conversion;

/// <summary>열 형식 변경 자동 변환(설계 18.9·시안 ⑧, TE-44·TE-49): 임시 이름·기본 변환식·바꿔치기 단계·남은 단계·다시 만들기.</summary>
public sealed class ColumnConversionBuilderTests
{
    private static ColumnDraft Col(TableDraft d, string name) => d.Columns.Single(c => c.Name == name);

    private static ColumnConversionRequest Request(string column, Func<ColumnDraft, ColumnDraft> target, string? expression = null)
    {
        var (original, _) = Start();
        var source = Col(original, column);
        var t = target(source);
        return new ColumnConversionRequest(original, source.RowKey, t, expression ?? ColumnConversionDefaults.Expression(source, t));
    }

    private static IReadOnlyList<string> Texts(ColumnConversionPlan plan) => [.. plan.Statements.Select(s => s.Text)];

    // ---------- 임시 열 이름(TE-49) ----------

    [Fact]
    public void Temporary_name_takes_the_first_free_number()
    {
        Assert.Equal("ORDER_NO$1", ColumnConversionBuilder.TemporaryName("ORDER_NO", ["ID", "ORDER_NO"], 30));
        Assert.Equal("ORDER_NO$2", ColumnConversionBuilder.TemporaryName("ORDER_NO", ["ORDER_NO", "ORDER_NO$1"], 30));
    }

    [Fact]
    public void Temporary_name_shortens_the_column_to_the_byte_limit()
    {
        var name = new string('A', 30);
        Assert.Equal(new string('A', 28) + "$1", ColumnConversionBuilder.TemporaryName(name, [name], 30));

        // 한글 한 글자 = UTF-8 3바이트: 10글자(30바이트) + "$1" → 9글자(27) + 2 = 29
        Assert.Equal("가나다라마바사아자$1", ColumnConversionBuilder.TemporaryName("가나다라마바사아자차", [], 30));
    }

    // ---------- 기본 변환식 ----------

    [Theory]
    [InlineData("CUSTOMER_NAME", "NUMBER", null, 12, null, "TO_NUMBER(CUSTOMER_NAME)")]
    [InlineData("CUSTOMER_NAME", "DATE", null, null, null, "TO_DATE(CUSTOMER_NAME, 'YYYY-MM-DD HH24:MI:SS')")]
    [InlineData("CUSTOMER_NAME", "TIMESTAMP(6)", null, null, null, "TO_TIMESTAMP(CUSTOMER_NAME, 'YYYY-MM-DD HH24:MI:SS.FF')")]
    [InlineData("CUSTOMER_NAME", "CLOB", null, null, null, "TO_CLOB(CUSTOMER_NAME)")]
    [InlineData("CUSTOMER_NAME", "NVARCHAR2", 50, null, null, "TO_NCHAR(CUSTOMER_NAME)")]
    [InlineData("CUSTOMER_ID", "VARCHAR2", 20, null, null, "TO_CHAR(CUSTOMER_ID)")]
    [InlineData("CREATED", "VARCHAR2", 30, null, null, "TO_CHAR(CREATED, 'YYYY-MM-DD HH24:MI:SS')")]
    [InlineData("AMOUNT", "NUMBER", null, 8, null, "CAST(AMOUNT AS NUMBER(8))")]
    [InlineData("STATUS", "VARCHAR2", 1, null, null, "STATUS")]
    public void Default_expression_fits_the_type_pair(string column, string type, int? length, int? precision, int? scale, string expected)
    {
        var (original, _) = Start();
        var source = Col(original, column);
        var target = source with { DataType = type, Length = length, Precision = precision, Scale = scale };

        Assert.Equal(expected, ColumnConversionDefaults.Expression(source, target));
    }

    [Fact]
    public void Default_expression_quotes_reserved_and_odd_names()
    {
        var source = new ColumnDraft("c1", "DATE") { DataType = "VARCHAR2", Length = 10 };
        var odd = new ColumnDraft("c2", "order no") { DataType = "VARCHAR2", Length = 10 };
        var number = new ColumnDraft("c1", "X") { DataType = "NUMBER" };

        Assert.Equal("TO_NUMBER(\"DATE\")", ColumnConversionDefaults.Expression(source, number));
        Assert.Equal("TO_NUMBER(\"order no\")", ColumnConversionDefaults.Expression(odd, number));
    }

    [Fact]
    public void Validation_only_for_the_default_character_to_number_or_date_expression()
    {
        var (original, _) = Start();
        var source = Col(original, "CUSTOMER_NAME");
        var number = source with { DataType = "NUMBER", Length = null, Precision = 12, Scale = null };
        var date = source with { DataType = "DATE", Length = null };

        Assert.Equal(new ConversionValidation("NUMBER", null, 12, 0), ColumnConversionDefaults.Validation(source, number, "to_number( customer_name )"));
        Assert.Equal(new ConversionValidation("DATE", ColumnConversionDefaults.DateFormat), ColumnConversionDefaults.Validation(source, date, ColumnConversionDefaults.Expression(source, date)));
        Assert.Null(ColumnConversionDefaults.Validation(source, number, "TO_NUMBER(REPLACE(CUSTOMER_NAME, ',', ''))"));
        Assert.Null(ColumnConversionDefaults.Validation(Col(original, "CUSTOMER_ID"), number with { Precision = 5 }, "CAST(CUSTOMER_ID AS NUMBER(5))"));
    }

    // ---------- 열 바꿔치기 ----------

    [Fact]
    public void Swap_steps_add_copy_drop_rename_then_restore_constraints()
    {
        // CUSTOMER_ID 는 FK_ORDERS_01 과 서버 이름 UK(SYS_C0013: CUSTOMER_ID, CREATED)에 들어 있다.
        var request = Request("CUSTOMER_ID", c => c with { DataType = "VARCHAR2", Length = 20, Precision = null, Scale = null });

        var plan = ColumnConversionBuilder.BuildSwap(request, Dialect, null);

        Assert.Equal("CUSTOMER_ID$1", plan.TemporaryColumn);
        Assert.Equal(
            [
                "ALTER TABLE SAMPLE.ORDERS ADD (CUSTOMER_ID$1 VARCHAR2(20))",
                "UPDATE SAMPLE.ORDERS SET CUSTOMER_ID$1 = TO_CHAR(CUSTOMER_ID)",
                "COMMIT",
                "ALTER TABLE SAMPLE.ORDERS DROP COLUMN CUSTOMER_ID CASCADE CONSTRAINTS",
                "ALTER TABLE SAMPLE.ORDERS RENAME COLUMN CUSTOMER_ID$1 TO CUSTOMER_ID",
                "ALTER TABLE SAMPLE.ORDERS ADD UNIQUE (CUSTOMER_ID, CREATED)",
                "ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT FK_ORDERS_01 FOREIGN KEY (CUSTOMER_ID) REFERENCES SAMPLE.CUSTOMERS (ID)",
            ],
            Texts(plan).Where(t => !t.StartsWith("DECLARE", StringComparison.Ordinal)));
        Assert.Equal([1, 2, 3, 4, 5], plan.Steps.Select(s => s.Number));
        Assert.Equal([StatementKind.Ddl, StatementKind.Dml, StatementKind.TransactionControl], plan.Statements.Take(3).Select(s => s.Kind));
        Assert.True(plan.Steps[1].IsDml);
        Assert.Equal(TableEditMarks.Irreversible, plan.Steps[2].Marks);
        Assert.Equal(["CUSTOMER_ID"], plan.Steps[2].Statements[1].ChangeTarget!.DroppedColumns);

        // 부모 열 CUSTOMERS.ID 는 NUMBER 라 이 FK 는 다시 붙지 않는다(ORA-02267) — 서버 없는 판정이 미리 막는다.
        Assert.Contains(ColumnConversionBuilder.Findings(request, ColumnConversionMethod.ColumnSwap, Dialect, null),
            f => (f.Severity, f.Detail) == (CheckSeverity.Block, "foreignKeyTypeChange:FK_ORDERS_01"));
    }

    [Fact]
    public void Step_three_first_checks_rows_changed_after_the_copy()
    {
        var plan = ColumnConversionBuilder.BuildSwap(Request("CUSTOMER_ID", c => c with { DataType = "VARCHAR2", Length = 20, Precision = null, Scale = null }), Dialect, null);
        var lob = ColumnConversionBuilder.BuildSwap(Request("NOTE", c => c with { DataType = "CLOB", Length = null, CharSemantics = null }), Dialect, null);

        var guard = plan.Steps[2].Statements[0];
        Assert.Equal(StatementKind.PlSql, guard.Kind);
        Assert.Contains("SELECT COUNT(*) INTO n FROM SAMPLE.ORDERS t WHERE ROWNUM = 1 AND DECODE(t.CUSTOMER_ID$1, CAST((\nTO_CHAR(CUSTOMER_ID)\n) AS VARCHAR2(20)), 0, 1) = 1;",
            guard.Text, StringComparison.Ordinal);
        Assert.Contains("RAISE_APPLICATION_ERROR(-20990,", guard.Text, StringComparison.Ordinal);
        Assert.Contains("t.NOTE$1 IS NULL AND (\nTO_CLOB(NOTE)\n) IS NOT NULL", lob.Steps[2].Statements[0].Text, StringComparison.Ordinal); // LOB 은 DECODE 로 못 비교
        Assert.Contains("\nEND;\n/\n", plan.RemainingScript(3), StringComparison.Ordinal);
    }

    [Fact]
    public void Invisible_target_adds_the_temporary_column_invisible()
    {
        var plan = ColumnConversionBuilder.BuildSwap(Request("NOTE", c => c with { Length = 100, IsInvisible = true }), Dialect, null);

        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD (NOTE$1 VARCHAR2(100 BYTE) INVISIBLE)", plan.Statements[0].Text);
        Assert.DoesNotContain(plan.Statements, s => s.Text.Contains("VISIBLE)", StringComparison.Ordinal) && s != plan.Statements[0]);
    }

    [Fact]
    public void Swap_restores_default_not_null_comment_check_and_function_index()
    {
        // STATUS: 기본값 'N', CK_ORDERS_01 CHECK (status in ('N','Y')) — 숫자로 바꾸고 기본값 0·NOT NULL·주석
        var status = Request("STATUS",
            c => c with { DataType = "NUMBER", Length = null, CharSemantics = null, Precision = 1, IsNullable = false, DefaultValue = "0", Comment = "상태" },
            "DECODE(STATUS, 'Y', 1, 0)");
        var note = Request("NOTE", c => c with { Length = 100 });

        var statusTexts = Texts(ColumnConversionBuilder.BuildSwap(status, Dialect, null));
        var noteTexts = Texts(ColumnConversionBuilder.BuildSwap(note, Dialect, null));

        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD (STATUS$1 NUMBER(1))", statusTexts[0]);
        Assert.Equal("UPDATE SAMPLE.ORDERS SET STATUS$1 = DECODE(STATUS, 'Y', 1, 0)", statusTexts[1]);
        Assert.Contains("ALTER TABLE SAMPLE.ORDERS MODIFY (STATUS DEFAULT 0 NOT NULL)", statusTexts);
        Assert.Contains("ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT CK_ORDERS_01 CHECK (status in ('N','Y'))", statusTexts);
        Assert.Contains("COMMENT ON COLUMN SAMPLE.ORDERS.STATUS IS '상태'", statusTexts);

        // NOTE 는 길이 기준 BYTE·주석 「메모」·함수 기반 인덱스 IX_ORDERS_02 (UPPER("NOTE"))
        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD (NOTE$1 VARCHAR2(100 BYTE))", noteTexts[0]);
        Assert.Equal("UPDATE SAMPLE.ORDERS SET NOTE$1 = NOTE", noteTexts[1]);
        Assert.Contains("COMMENT ON COLUMN SAMPLE.ORDERS.NOTE IS '메모'", noteTexts);
        Assert.Contains("CREATE INDEX SAMPLE.IX_ORDERS_02 ON SAMPLE.ORDERS (UPPER(\"NOTE\"))", noteTexts);
    }

    [Fact]
    public void Swap_drops_left_indexes_after_the_column_in_a_rerunnable_block()
    {
        // Tibero 는 DROP COLUMN … CASCADE CONSTRAINTS 뒤에도 그 열을 쓰는 함수 기반 인덱스를 남겨 4단계 RENAME 이 TBR-8026 으로 막힌다(T17b 실측) →
        // 옛 열을 지운 다음 사전에 아직 남은 그 열의 인덱스(제약에 딸리지 않은 것)만 실제 소유자로 지운다. 확인 블록과 DROP COLUMN 은 붙어 있다.
        var note = ColumnConversionBuilder.BuildSwap(Request("NOTE", c => c with { Length = 100 }), Dialect, null);
        var name = ColumnConversionBuilder.BuildSwap(Request("CUSTOMER_NAME", c => c with { DataType = "NUMBER", Length = null, CharSemantics = null }), Dialect, null);

        var drop = note.Steps[2].Statements;
        Assert.Equal([StatementKind.PlSql, StatementKind.Ddl, StatementKind.PlSql], drop.Select(s => s.Kind));
        Assert.Equal("ALTER TABLE SAMPLE.ORDERS DROP COLUMN NOTE CASCADE CONSTRAINTS", drop[1].Text);
        Assert.Equal(
            "BEGIN\n  FOR r IN (SELECT owner, index_name FROM all_indexes WHERE table_owner = 'SAMPLE' AND table_name = 'ORDERS'\n"
            + "               AND index_name IN ('IX_ORDERS_02')) LOOP\n"
            + "    EXECUTE IMMEDIATE 'DROP INDEX \"' || r.owner || '\".\"' || r.index_name || '\"';\n  END LOOP;\nEND;",
            drop[2].Text);
        Assert.Contains("IN ('IX_ORDERS_01')", name.Steps[2].Statements[2].Text, StringComparison.Ordinal);
        Assert.Equal(2, ColumnConversionBuilder.BuildSwap(Request("CUSTOMER_ID", c => c with { DataType = "VARCHAR2", Length = 20, Precision = null, Scale = null }), Dialect, null)
            .Steps[2].Statements.Count); // 제약 인덱스만 있으면 블록 없음
        Assert.Contains("CREATE INDEX SAMPLE.IX_ORDERS_02 ON SAMPLE.ORDERS (UPPER(\"NOTE\"))", Texts(note)); // 5단계가 다시 만든다
    }

    [Fact]
    public void Remaining_script_reruns_step_three_from_the_check_until_the_column_is_dropped()
    {
        var plan = ColumnConversionBuilder.BuildSwap(Request("NOTE", c => c with { Length = 100 }), Dialect, null);

        var atCheck = plan.RemainingScript(3);   // ADD·UPDATE·COMMIT 뒤 확인 블록에서 멈춤
        var atDrop = plan.RemainingScript(4);    // 확인 블록 뒤 DROP COLUMN 에서 멈춤 → 확인부터 다시
        var atCleanup = plan.RemainingScript(5); // 옛 열은 지워짐 → 남은 인덱스 블록부터(다시 돌려도 안전)

        Assert.Equal(atCheck, atDrop);
        Assert.StartsWith("-- 형식 변환 남은 단계: SAMPLE.ORDERS.NOTE (3단계부터)\n\nDECLARE", atDrop, StringComparison.Ordinal);
        Assert.StartsWith("-- 형식 변환 남은 단계: SAMPLE.ORDERS.NOTE (3단계부터)\n\nBEGIN\n  FOR r IN", atCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP COLUMN", atCleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void Swap_with_a_new_name_renames_the_temporary_column_and_conditions()
    {
        var request = Request("STATUS", c => c with { Name = "STATE", Length = 2 });

        var plan = ColumnConversionBuilder.BuildSwap(request, Dialect, null);

        Assert.Equal(("STATUS", "STATE"), (plan.Column, plan.FinalColumn));
        Assert.Contains("ALTER TABLE SAMPLE.ORDERS RENAME COLUMN STATUS$1 TO STATE", Texts(plan));
        Assert.Contains("ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT CK_ORDERS_01 CHECK (STATE in ('N','Y'))", Texts(plan));
    }

    [Fact]
    public void Swap_recompiles_dependents_once_per_unit()
    {
        var request = Request("NOTE", c => c with { Length = 100 }) with
        {
            Dependents =
            [
                new DbObjectRef("SAMPLE", "V_ORDERS", "VIEW"),
                new DbObjectRef("SAMPLE", "PKG_ORDERS", "PACKAGE"),
                new DbObjectRef("SAMPLE", "PKG_ORDERS", "PACKAGE BODY"),
                new DbObjectRef("SAMPLE", "PKG_ONLY_BODY", "PACKAGE BODY"),
                new DbObjectRef("PUBLIC", "ORDERS", "SYNONYM"),
                new DbObjectRef("SAMPLE", "ORDERS_JOB", "JOB"),
            ],
        };

        var plan = ColumnConversionBuilder.BuildSwap(request, Dialect, null);

        Assert.Equal(ColumnConversionStepKind.Recompile, plan.Steps[^1].Kind);
        Assert.Equal(
            [
                "ALTER VIEW SAMPLE.V_ORDERS COMPILE",
                "ALTER PACKAGE SAMPLE.PKG_ORDERS COMPILE",
                "ALTER PACKAGE SAMPLE.PKG_ONLY_BODY COMPILE BODY",
                "ALTER PUBLIC SYNONYM ORDERS COMPILE",
            ],
            plan.Steps[^1].Statements.Select(s => s.Text));
    }

    [Fact]
    public void Swap_recreates_an_index_with_a_descending_expression_column()
    {
        // IX_ORDERS_01 = (CUSTOMER_NAME, CREATED DESC) — 3단계가 함께 지우므로 5단계에서 다시 만든다.
        var request = Request("CUSTOMER_NAME", c => c with { DataType = "NUMBER", Length = null, CharSemantics = null });

        var plan = ColumnConversionBuilder.BuildSwap(request, Dialect, null);

        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD (CUSTOMER_NAME$1 NUMBER)", plan.Statements[0].Text);
        Assert.Equal(
            "CREATE INDEX SAMPLE.IX_ORDERS_01 ON SAMPLE.ORDERS (CUSTOMER_NAME, CREATED DESC) TABLESPACE USERS",
            Assert.Single(plan.Steps[^1].Statements).Text);
    }

    [Fact]
    public void Swap_without_constraints_or_dependents_has_four_steps()
    {
        var request = Request("CUSTOMER_NAME", c => c with { DataType = "NUMBER", Length = null, CharSemantics = null });
        request = request with { Original = request.Original with { Constraints = [], Indexes = [] } };

        var plan = ColumnConversionBuilder.BuildSwap(request, Dialect, null);

        Assert.Equal([1, 2, 3, 4], plan.Steps.Select(s => s.Number));
    }

    // ---------- 중간 실패: 남은 단계 ----------

    [Fact]
    public void Remaining_script_starts_at_the_failed_statement()
    {
        var plan = ColumnConversionBuilder.BuildSwap(Request("CUSTOMER_ID", c => c with { DataType = "VARCHAR2", Length = 20, Precision = null, Scale = null }), Dialect, null);

        var afterAdd = plan.RemainingScript(1);
        var afterDrop = plan.RemainingScript(5); // ADD·UPDATE·COMMIT·확인·DROP 뒤

        Assert.StartsWith("-- 형식 변환 남은 단계: SAMPLE.ORDERS.CUSTOMER_ID (2단계부터)\n" + ColumnConversionBuilder.DmlHeader + "\n", afterAdd, StringComparison.Ordinal);
        Assert.Contains("\nUPDATE SAMPLE.ORDERS SET CUSTOMER_ID$1 = TO_CHAR(CUSTOMER_ID);\n\nCOMMIT;\n", afterAdd, StringComparison.Ordinal);
        Assert.StartsWith("-- 형식 변환 남은 단계: SAMPLE.ORDERS.CUSTOMER_ID (4단계부터)\n\nALTER TABLE SAMPLE.ORDERS RENAME COLUMN", afterDrop, StringComparison.Ordinal);
        Assert.DoesNotContain("DML", afterDrop, StringComparison.Ordinal);
        Assert.Equal(string.Empty, plan.RemainingScript(plan.Statements.Count));
        Assert.Equal((1, 2, 3, 4), (plan.CompletedSteps(1), plan.CompletedSteps(4), plan.CompletedSteps(5), plan.CompletedSteps(6)));
        Assert.Equal(1, plan.CompletedSteps(2)); // UPDATE 만 되고 COMMIT 전 — 2단계는 끝나지 않음
    }

    // ---------- 서버 없는 판정 ----------

    [Fact]
    public void Findings_block_virtual_use_identity_and_empty_expression_and_warn_swap()
    {
        var amount = Request("AMOUNT", c => c with { Precision = 8, Scale = 0 }); // TOTAL = AMOUNT*QTY
        var identity = Request("ORDER_ID", c => c with { Precision = 5 }) with { };
        identity = identity with { Original = identity.Original.WithColumn("ORDER_ID", c => c with { Identity = new IdentityInfo(IdentityGeneration.Always) }) };
        var empty = Request("NOTE", c => c with { Length = 100 }, "  ");

        var amountSwap = ColumnConversionBuilder.Findings(amount, ColumnConversionMethod.ColumnSwap, Dialect, null);
        var amountRebuild = ColumnConversionBuilder.Findings(amount, ColumnConversionMethod.Rebuild, Dialect, null);
        var noteSwap = ColumnConversionBuilder.Findings(empty, ColumnConversionMethod.ColumnSwap, Dialect, null);

        Assert.Contains(amountSwap, f => (f.Severity, f.Code, f.Detail) == (CheckSeverity.Block, CheckCode.InvalidDefinition, "usedByVirtualColumn:TOTAL"));
        Assert.Contains(amountSwap, f => (f.Severity, f.Code) == (CheckSeverity.Warning, CheckCode.Irreversible));
        Assert.Contains(amountSwap, f => (f.Severity, f.Code) == (CheckSeverity.Warning, CheckCode.ColumnOrderChanges));
        Assert.Empty(amountRebuild);
        Assert.Contains(ColumnConversionBuilder.Findings(identity, ColumnConversionMethod.Rebuild, Dialect, null), f => f.Detail == "identityColumn");
        Assert.Contains(noteSwap, f => f.Detail == "emptyExpression");
        Assert.DoesNotContain(noteSwap, f => f.Code == CheckCode.ColumnOrderChanges); // NOTE 는 이미 맨 뒤
    }

    [Fact]
    public void Findings_block_special_partitioned_name_clash_domain_and_odd_indexes()
    {
        var note = Request("NOTE", c => c with { Length = 100 });
        var gtt = note with { Original = note.Original with { Kind = TableKind.Temporary } };
        var partitioned = note with { Original = note.Original with { IsPartitioned = true } };
        var clash = Request("NOTE", c => c with { Name = "STATUS", Length = 100 });
        var domain = Request("NOTE", c => c with { Length = 100, Domain = "D_NOTE" });
        var odd = note with { Original = note.Original.WithIndex("IX_ORDERS_02", i => i with { Kind = IndexKind.Other }) };

        static IReadOnlyList<string?> Details(ColumnConversionRequest r, ColumnConversionMethod m = ColumnConversionMethod.ColumnSwap) =>
            [.. ColumnConversionBuilder.Findings(r, m, Dialect, null).Where(f => f.Severity == CheckSeverity.Block).Select(f => f.Detail ?? f.Code.ToString())];

        Assert.Contains("specialTable", Details(gtt, ColumnConversionMethod.Rebuild));
        Assert.Contains("partitionedTable", Details(partitioned, ColumnConversionMethod.Rebuild));
        Assert.Contains(nameof(CheckCode.NameExists), Details(clash));
        Assert.Contains("domainColumn", Details(domain));
        Assert.Contains("unsupportedIndex:IX_ORDERS_02", Details(odd));
        Assert.Empty(Details(note));
    }

    [Fact]
    public void Swap_warns_to_stop_writes_and_lists_triggers()
    {
        var request = Request("NOTE", c => c with { Length = 100 }) with
        {
            Dependents = [new DbObjectRef("SAMPLE", "TRG_ORDERS_AUDIT", "TRIGGER"), new DbObjectRef("SAMPLE", "V_ORDERS", "VIEW")],
        };

        var quiet = Assert.Single(ColumnConversionBuilder.Findings(request, ColumnConversionMethod.ColumnSwap, Dialect, null), f => f.Code == CheckCode.ConversionNeedsQuiet);

        Assert.Equal(CheckSeverity.Warning, quiet.Severity);
        Assert.Equal(["TRG_ORDERS_AUDIT"], quiet.Dependents.Select(d => d.Name));
    }

    [Fact]
    public void Findings_block_when_the_dialect_does_not_convert()
    {
        var dialect = new FakeTableEditDialect();
        dialect.Unsupported[TableEditFeature.ConvertingTypeChange] = "TableEdit.unsupported.x";

        var findings = ColumnConversionBuilder.Findings(Request("NOTE", c => c with { Length = 100 }), ColumnConversionMethod.Rebuild, dialect, null);

        Assert.Equal((CheckSeverity.Block, CheckCode.Unsupported, "TableEdit.unsupported.x"), (findings[0].Severity, findings[0].Code, findings[0].Detail));
    }

    // ---------- 테이블 다시 만들기 ----------

    [Fact]
    public void Rebuild_ctas_copies_the_converted_value_and_restores_not_null()
    {
        var request = Request("CUSTOMER_NAME",
            c => c with { DataType = "NUMBER", Length = null, CharSemantics = null, Precision = 12, IsNullable = false, DefaultValue = "0" });

        var script = ColumnConversionBuilder.BuildRebuild(request, Orders().Columns, enterprise: false, usePrimaryKey: true, dependentDdl: "");

        Assert.Contains("-- 형식 변환: CUSTOMER_NAME = CAST(TO_NUMBER(CUSTOMER_NAME) AS NUMBER(12))", script, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE \"SAMPLE\".\"ORDERS$NEW\" AS SELECT \"ORDER_ID\", \"CUSTOMER_ID\", CAST(TO_NUMBER(CUSTOMER_NAME) AS NUMBER(12)) AS \"CUSTOMER_NAME\", \"STATUS\"",
            script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"SAMPLE\".\"ORDERS$NEW\" MODIFY (\"CUSTOMER_NAME\" DEFAULT 0);", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"SAMPLE\".\"ORDERS$NEW\" MODIFY (\"CUSTOMER_NAME\" NOT NULL);", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_redefinition_maps_the_converted_column_and_lob_keeps_the_expression()
    {
        var request = Request("NOTE", c => c with { Name = "NOTE_TEXT", DataType = "CLOB", Length = null, CharSemantics = null });

        var script = ColumnConversionBuilder.BuildRebuild(request, Orders().Columns, enterprise: true, usePrimaryKey: true, dependentDdl: null);

        Assert.Contains("col_mapping => '\"ORDER_ID\" \"ORDER_ID\", ", script, StringComparison.Ordinal);
        Assert.Contains("TO_CLOB(NOTE) \"NOTE\"'", script, StringComparison.Ordinal);
        Assert.Contains("TO_CLOB(NOTE) AS \"NOTE\"", script, StringComparison.Ordinal);
        Assert.EndsWith("ALTER TABLE \"SAMPLE\".\"ORDERS\" RENAME COLUMN \"NOTE\" TO \"NOTE_TEXT\";" + Environment.NewLine, script, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_to_text_copies_the_expression_then_shrinks_and_sets_the_comment()
    {
        // CAST(… AS VARCHAR2(20)) 는 긴 값을 잘라 버린다 — 식 그대로 복사하고 MODIFY 로 줄여 넘치면 오류가 나게
        var request = Request("CUSTOMER_ID", c => c with { DataType = "VARCHAR2", Length = 20, Precision = null, Scale = null, Comment = "고객" });

        var script = ColumnConversionBuilder.BuildRebuild(request, Orders().Columns, enterprise: false, usePrimaryKey: true, dependentDdl: "");

        Assert.Contains("(TO_CHAR(CUSTOMER_ID)) AS \"CUSTOMER_ID\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(TO_CHAR", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"SAMPLE\".\"ORDERS$NEW\" MODIFY (\"CUSTOMER_ID\" VARCHAR2(20));", script, StringComparison.Ordinal);
        Assert.Contains("COMMENT ON COLUMN \"SAMPLE\".\"ORDERS\".\"CUSTOMER_ID\" IS '고객';", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Reorder_rebuild_without_conversions_is_unchanged()
    {
        var columns = Orders().Columns;
        var desired = columns.Where(c => !c.IsInvisible).Select(c => c.Name).ToList();

        var script = ColumnReorderBuilder.Rebuild(OrdersRef, columns, desired, enterprise: true, usePrimaryKey: true, dependentDdl: "");

        Assert.DoesNotContain("col_mapping", script, StringComparison.Ordinal);
        Assert.DoesNotContain("형식 변환", script, StringComparison.Ordinal);
    }
}
