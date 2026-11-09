using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: TP0b-1 커밋 B, TP0b-2 커밋 B(실행기 문장·스칼라 판정). 사전 점검·실행기를 Core 로 옮기기 전, Oracle 이 넘기는 값 해석·문장·오류 범위가 옮기기 전과 같은지(DB 없음).
public sealed class OracleCopyDialectTests
{
    private static readonly OracleBulkValueCodec Codec = OracleBulkValueCodec.Instance;

    // ---- 값 해석: 예전 CopyCatalogQueries.Text·ToLong 과 같은 글자·값 ----

    [Fact]
    public void Text_keeps_odp_number_text_and_plain_values()
    {
        foreach (var number in new[] { "12345678901234567890.5", "-1234.5", "0", "12345678901234567890123456789012345678", "0.00000000000000000000000000000000012345" })
        {
            var d = OracleDecimal.Parse(number);
            Assert.Equal(d.ToString(), Codec.ToText(d)); // 예전 분기 그대로: ODP.NET 글자(지수·문화권 구분자도 손대지 않음)
        }

        Assert.Null(Codec.ToText(OracleDecimal.Null));
        Assert.Null(Codec.ToText(null));
        Assert.Equal("SCOTT", Codec.ToText("SCOTT"));
        Assert.Equal("42", Codec.ToText(42m));
        Assert.Equal("1.5", Codec.ToText(1.5d)); // invariant
    }

    [Fact]
    public void ToInt64_truncates_and_clamps_like_before()
    {
        Assert.Null(Codec.ToInt64(null));
        Assert.Null(Codec.ToInt64(OracleDecimal.Null));
        Assert.Equal(12, Codec.ToInt64(OracleDecimal.Parse("12.9")));
        Assert.Equal(-12, Codec.ToInt64(OracleDecimal.Parse("-12.9")));
        Assert.Equal(long.MaxValue, Codec.ToInt64(OracleDecimal.Parse("123456789012345678901234567890")));
        Assert.Equal(long.MinValue, Codec.ToInt64(OracleDecimal.Parse("-123456789012345678901234567890")));
        Assert.Equal(900, Codec.ToInt64("900")); // V$PARAMETER.VALUE 는 글자
        Assert.Equal(12, Codec.ToInt64("12.7"));
        Assert.Equal(5, Codec.ToInt64(5));
        Assert.Null(Codec.ToInt64(new object()));
    }

    [Theory]
    [InlineData("VARCHAR2(10 CHAR)", BulkKind.Text)]
    [InlineData("NCHAR", BulkKind.Text)]
    [InlineData("CLOB", BulkKind.Clob)]
    [InlineData("NCLOB", BulkKind.Clob)]
    [InlineData("BLOB", BulkKind.Blob)]
    [InlineData("NUMBER(10,2)", BulkKind.Other)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", BulkKind.Other)]
    public void Kind_follows_bulk_bind_types(string serverType, BulkKind kind)
    {
        Assert.True(Codec.IsSupported(serverType));
        Assert.Equal(kind, Codec.KindOf(serverType));
    }

    [Fact]
    public void Unsupported_types_match_bulk_binds_and_key_text_is_failed_row_keys()
    {
        Assert.False(Codec.IsSupported("XMLTYPE"));
        Assert.False(Codec.IsSupported("LONG"));
        Assert.Throws<NotSupportedException>(() => BulkBinds.ToDbType("XMLTYPE"));
        Assert.Throws<NotSupportedException>(() => Codec.KindOf("XMLTYPE"));

        var number = OracleDecimal.Parse("12345678901234567890.5");
        Assert.Equal(FailedRowKeys.ToText(number, "NUMBER"), Codec.ToKeyText(number, "NUMBER"));
        Assert.Equal(FailedRowKeys.FromText("2026-03-05T14:30:15", "DATE"), Codec.FromKeyText("2026-03-05T14:30:15", "DATE"));
        Assert.Equal(FailedRowKeys.BindTypeOf("TIMESTAMP(6)"), Codec.BindTypeOf("TIMESTAMP(6)"));
        Assert.Throws<NotSupportedException>(() => Codec.BindTypeOf("CLOB")); // 키로 쓸 수 없는 형식
    }

    // ---- 문장: 미리 보기(ICopyDialect)와 실행기(CopyStatements)가 같은 글 ----

    [Fact]
    public void Dialect_statements_equal_copy_statements()
    {
        var dialect = OracleCopyDialect.Instance;
        var fk = new DisabledObject(DisabledKind.ForeignKey, "APP", "ORDER_ITEMS", "FK_ITEMS_ORDERS");

        SameStatement(CopyStatements.DisableTrigger("APP", "TRG_ORDERS"), dialect.DisableTrigger("APP", "TRG_ORDERS"));
        SameStatement(CopyStatements.DisableConstraint(fk), dialect.DisableConstraint(fk));
        SameStatement(CopyStatements.DropTable("APP", "ORDERS"), dialect.DropTable("APP", "ORDERS"));
        SameStatement(CopyStatements.NextValues("APP", "SEQ_ORDERS"), dialect.NextValues("APP", "SEQ_ORDERS"));
        Assert.Equal("SELECT \"APP\".\"SEQ_ORDERS\".NEXTVAL FROM DUAL CONNECT BY LEVEL <= :N", dialect.NextValues("APP", "SEQ_ORDERS").Text);

        var delete = new SqlWithBinds("DELETE FROM \"APP\".\"ORDERS\"", new Dictionary<string, BindValue>());
        SameStatement(CopyStatements.Delete(delete), CopyCommonStatements.Delete(delete));
        SameStatement(CopyStatements.Query("SELECT 1 FROM DUAL"), CopyCommonStatements.Query("SELECT 1 FROM DUAL"));
    }

    [Fact]
    public void Created_table_is_dropped_with_purge_as_a_dangerous_command_retyped_by_table_name()
    {
        // 「없으면 만들기」로 방금 만든 표를 적재 실패 뒤 지운다: 휴지통 없이(PURGE), 위험 명령이라 표 이름 재입력, 변경 일지에 DROP 으로 남는다.
        var drop = ((ICopyDialect)OracleCopyDialect.Instance).DropCreatedTable("APP", "ORDERS");

        Assert.Equal("DROP TABLE \"APP\".\"ORDERS\" PURGE", drop.Text);
        Assert.Equal("DROP", drop.Verb);
        Assert.Equal(StatementKind.Ddl, drop.Kind);
        Assert.Equal("ORDERS", drop.DangerousTarget);
        Assert.Equal(new ChangeTarget("APP", "ORDERS", "TABLE", ChangeOperation.Drop), drop.ChangeTarget);
        // 다시 만들기의 DROP(휴지통에 남김)과 글이 다르다: 미리 받은 확인이 서로 섞이지 않는다.
        Assert.NotEqual(OracleCopyDialect.Instance.DropTable("APP", "ORDERS").Text, drop.Text);
    }

    // ---- TP0b-2 커밋 B: 실행기가 쓰는 문장. 옮기기 전 CopyStatements·CopyRunner 의 글을 글자 그대로 옮겨 적었다 ----

    [Fact]
    public void Runner_dialect_statements_keep_previous_text()
    {
        var dialect = OracleCopyDialect.Instance;
        var fk = new DisabledObject(DisabledKind.ForeignKey, "APP", "ORDER_ITEMS", "FK_ITEMS_ORDERS");

        Same("SET TRANSACTION READ ONLY", StatementKind.TransactionControl, "SET TRANSACTION", dialect.SetTransactionReadOnly());
        Same("SAVEPOINT ORANDA_COPY_BATCH", StatementKind.TransactionControl, "SAVEPOINT", dialect.Savepoint(CopyCommonStatements.BatchSavepoint));
        Same("ROLLBACK TO SAVEPOINT ORANDA_COPY_TABLE", StatementKind.TransactionControl, "ROLLBACK",
            dialect.RollbackToSavepoint(CopyCommonStatements.TableSavepoint));
        Same("ALTER TRIGGER \"APP\".\"TRG_ORDERS\" ENABLE", StatementKind.Ddl, "ALTER", dialect.EnableTrigger("APP", "TRG_ORDERS"));
        Same("ALTER TABLE \"APP\".\"ORDER_ITEMS\" DROP CONSTRAINT \"FK_ITEMS_ORDERS\"", StatementKind.Ddl, "ALTER", dialect.DropConstraint(fk));
        Same("ALTER TABLE \"APP\".\"ORDER_ITEMS\" ENABLE VALIDATE CONSTRAINT \"FK_ITEMS_ORDERS\"", StatementKind.Ddl, "ALTER",
            dialect.EnableConstraint(fk, validate: true));
        Same("ALTER TABLE \"APP\".\"ORDER_ITEMS\" ENABLE NOVALIDATE CONSTRAINT \"FK_ITEMS_ORDERS\"", StatementKind.Ddl, "ALTER",
            dialect.EnableConstraint(fk, validate: false));
        Same("ALTER SESSION SET RECYCLEBIN = ON", StatementKind.Other, "ALTER SESSION", dialect.RecycleBinOn());

        SameStatement(CopyStatements.SetTransactionReadOnly(), dialect.SetTransactionReadOnly());
        SameStatement(CopyStatements.Savepoint("X"), dialect.Savepoint("X"));
        SameStatement(CopyStatements.RollbackTo("X"), dialect.RollbackToSavepoint("X"));
        SameStatement(CopyStatements.EnableTrigger("APP", "T"), dialect.EnableTrigger("APP", "T"));
        SameStatement(CopyStatements.DropConstraint(fk), dialect.DropConstraint(fk));
        SameStatement(CopyStatements.EnableConstraint(fk, true), dialect.EnableConstraint(fk, true));
        SameStatement(CopyStatements.EnableConstraint(fk, false), dialect.EnableConstraint(fk, false));
        SameStatement(CopyStatements.RecycleBinOn(), dialect.RecycleBinOn());

        // 예전 CopyRunner 안에서 만들던 글
        Assert.Equal("ALTER TABLE \"APP\".\"ORDERS\" MODIFY (\"ID\" GENERATED BY DEFAULT AS IDENTITY (START WITH LIMIT VALUE))",
            dialect.RestartIdentity("APP", "ORDERS", "ID"));
        Assert.Equal("ALTER TABLE \"APP\".\"C\" ADD FOREIGN KEY (\"P_ID\") REFERENCES \"APP\".\"P\" (\"ID\") ENABLE NOVALIDATE",
            dialect.AddForeignKeyWithoutValidation("ALTER TABLE \"APP\".\"C\" ADD FOREIGN KEY (\"P_ID\") REFERENCES \"APP\".\"P\" (\"ID\")"));
    }

    // ---- TB1 커밋 1: 배열 DML 값 자리 고리(ICopyDialect.BindPlaceholder). Oracle 은 :Cn 그대로라 INSERT·MERGE 글이 고리 전과 바이트 같다 ----

    [Fact]
    public void Bind_placeholder_keeps_array_dml_text_byte_identical()
    {
        var dialect = OracleCopyDialect.Instance;
        Assert.Equal(":C1", dialect.BindPlaceholder("C1", "NUMBER"));
        Assert.Equal(":C12", dialect.BindPlaceholder("C12", "TIMESTAMP(6)"));
        Assert.Equal(":C3", dialect.BindPlaceholder("C3", "DATE")); // 형식과 무관(감싸지 않음)

        var job = new CopyJob
        {
            Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "TGT"),
            SourceOwner = "SRC", TargetOwner = "TGT", MaskSalt = "AA==",
            Tables = [new TableRule("M", RuleKind.All, TargetAction.MergeByPk)],
        };
        var types = new[] { "NUMBER(10)", "DATE", "TIMESTAMP(9)", "RAW(16)", "VARCHAR2(10 CHAR)", "CLOB", "BLOB" };
        var columns = types.Select((_, i) => "COL" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var info = new TableInfo(new DbObjectRef("SRC", "M", "TABLE"), [.. types.Select((t, i) => new ColumnInfo(columns[i], i + 1, t))]);
        var tables = new Dictionary<string, TableInfo>(StringComparer.Ordinal) { ["M"] = info };
        var builder = new CopySqlBuilder(job, new ResolvedPeriod(null, null), tables, tables);
        var names = CopyCommonStatements.BindNames(columns.Count);
        string ValueAt(int i) => dialect.BindPlaceholder(names[i], types[i]);

        // 실행기(CopyRunner.DmlFor)가 부르는 모양 = 고리 없는 글.
        Assert.Equal(builder.Insert("M", columns), builder.Insert("M", columns, ValueAt));
        Assert.Equal(builder.Merge("M", columns, ["COL1"]), builder.Merge("M", columns, ["COL1"], ValueAt));
        Assert.Equal(builder.Merge("M", ["COL1"], ["COL1"]), builder.Merge("M", ["COL1"], ["COL1"], ValueAt));
        Assert.Equal("INSERT INTO \"TGT\".\"M\" (\"COL1\", \"COL2\") VALUES (:C1, :C2)", builder.Insert("M", ["COL1", "COL2"], ValueAt));
    }

    [Fact]
    public void Common_runner_statements_keep_previous_text()
    {
        Assert.Equal("ORANDA_COPY_BATCH", CopyStatements.BatchSavepoint);
        Assert.Equal("ORANDA_COPY_TABLE", CopyStatements.TableSavepoint);
        Assert.Equal("N", CopyStatements.NextValuesBind);
        Assert.Equal("\"APP\".\"ORDERS\"", CopyCommonStatements.Qualified("APP", "ORDERS"));
        Assert.Equal("\"a b\".\"x\"", CopyStatements.Qualified("a b", "x"));
        Same("COMMIT", StatementKind.TransactionControl, "COMMIT", CopyCommonStatements.Commit());
        Same("ROLLBACK", StatementKind.TransactionControl, "ROLLBACK", CopyCommonStatements.Rollback());

        var merge = CopyCommonStatements.ArrayDml("MERGE INTO \"APP\".\"T\" t USING (SELECT :C1 C1 FROM DUAL) s ON (1=1)", 3);
        Same(merge.Text, StatementKind.Dml, "MERGE", merge);
        Assert.Equal(["C1", "C2", "C3"], merge.BindNames);
        Assert.Equal("INSERT", CopyCommonStatements.ArrayDml("INSERT INTO \"APP\".\"T\" VALUES (:C1)", 1).Verb);
        Assert.Equal(["C1", "C2"], CopyCommonStatements.BindNames(2));

        Same("CREATE TABLE \"APP\".\"T\" (ID NUMBER)", StatementKind.Ddl, "CREATE", CopyCommonStatements.RecreateDdl("CREATE TABLE \"APP\".\"T\" (ID NUMBER)"));
        Same("GRANT", StatementKind.Ddl, "GRANT", CopyCommonStatements.RecreateDdl("GRANT"));
    }

    [Fact]
    public void Runner_error_numbers_are_the_previous_ora_numbers()
    {
        var dialect = OracleCopyDialect.Instance;

        Assert.True(dialect.IsParentKeysNotFound(new DbError("ORA-02298", "m") { Number = 2298 }));
        Assert.False(dialect.IsParentKeysNotFound(new DbError("ORA-02449", "m") { Number = 2449 }));
        Assert.False(dialect.IsParentKeysNotFound(null)); // 예전 r?.Error?.Number == 2298 → null 이면 거짓
        Assert.True(dialect.IsReferencedByForeignKeys(new DbError("ORA-02449", "m") { Number = 2449 }));
        Assert.False(dialect.IsReferencedByForeignKeys(new DbError("ORA-02298", "m") { Number = 2298 }));
        Assert.False(dialect.IsReferencedByForeignKeys(null));
    }

    // ---- TP0b-2 커밋 B: 실행기가 보던 OracleDecimal 판정 = 코덱 ----

    private static readonly object?[] Scalars =
    [
        null, OracleDecimal.Null, OracleDecimal.Parse("0"), OracleDecimal.Parse("0.5"), OracleDecimal.Parse("-0.5"),
        OracleDecimal.Parse("12"), OracleDecimal.Parse("-7"), OracleDecimal.Parse("12.9"),
        OracleDecimal.Parse("9223372036854775807"), OracleDecimal.Parse("9223372036854775808"),
        OracleDecimal.Parse("-9223372036854775809"), OracleDecimal.Parse("123456789012345678901234567890"),
        0m, 5, "0", "12",
    ];

    [Fact]
    public void Runner_scalar_checks_equal_previous_oracle_decimal_logic()
    {
        foreach (var value in Scalars)
        {
            SameScalar(value);
        }
    }

    private static void SameScalar(object? value)
    {
        // 예전 CopyRunner: TableGoneAsync / 시퀀스·건수의 「is OracleDecimal { IsNull: false }」 / ToLong(OracleDecimal)(넘치면 OverflowException)
        var oldZero = value is OracleDecimal { IsNull: false } z && z.IsZero;
        var oldText = value is OracleDecimal { IsNull: false } t ? t.ToString() : null;

        Assert.Equal(oldZero, Codec.IsZero(value));
        Assert.Equal(oldText, Codec.NumberText(value));
        if (value is OracleDecimal { IsNull: false } d)
        {
            Assert.Equal(Outcome(() => OldToLong(d)), Outcome(() => Codec.ToInt64Exact(value)));
        }
        else
        {
            Assert.Throws<InvalidCastException>(() => Codec.ToInt64Exact(value)); // 실행기는 NumberText 로 먼저 거른다
        }
    }

    [Theory]
    [InlineData("VARCHAR2(10 CHAR)")]
    [InlineData("NVARCHAR2(20)")]
    [InlineData("CHAR(1)")]
    [InlineData("NCHAR(2)")]
    [InlineData("CLOB")]
    [InlineData("NCLOB")]
    [InlineData("BLOB")]
    [InlineData("RAW(16)")]
    [InlineData("NUMBER")]
    [InlineData("DATE")]
    [InlineData("TIMESTAMP(6)")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)")]
    [InlineData("BINARY_DOUBLE")]
    public void Runner_mask_kind_equals_previous_db_type_switch(string serverType)
    {
        // 예전 RowShape.Create: 문자 판정과 고정값 변환 갈래(CLOB·NCLOB = 글자, BLOB = 16진, 그 밖 = FailedRowKeys.FromText)
        var dbType = BulkBinds.ToDbType(serverType);
        var oldText = dbType is OracleDbType.Varchar2 or OracleDbType.NVarchar2 or OracleDbType.Char or OracleDbType.NChar;
        var oldBranch = dbType switch
        {
            OracleDbType.Clob or OracleDbType.NClob => BulkKind.Clob,
            OracleDbType.Blob => BulkKind.Blob,
            _ => BulkKind.Other,
        };

        var kind = Codec.KindOf(serverType);
        Assert.Equal(oldText, kind == BulkKind.Text);
        if (!oldText)
        {
            Assert.Equal(oldBranch, kind);
        }
    }

    [Fact]
    public async Task Snapshot_kind_comes_from_the_same_classification_as_before()
    {
        // 예전: catch (OracleException ex) → OracleErrorClassifier.KindOf(ex). 지금: CopyErrors.Server(분류기, ex)?.Kind (바깥 예외만).
        var errors = new OracleErrorClassifier();
        var direct = await RealOracleExceptionAsync();

        Assert.Equal(OracleErrorClassifier.KindOf(direct), CopyErrors.Server(errors, direct)!.Kind);
        Assert.Null(CopyErrors.Server(errors, new InvalidOperationException("wrapped", direct))); // 예전에도 잡지 않던 감싼 예외
        Assert.Equal(DbErrorKind.SnapshotTooOld, OracleErrorClassifier.ClassifyNumber(1555));
    }

    private static long OldToLong(OracleDecimal value)
    {
        try
        {
            return (long)value;
        }
        catch (InvalidCastException ex)
        {
            throw new OverflowException("NUMBER 값이 long 범위를 넘습니다.", ex);
        }
    }

    private static string Outcome(Func<long> f)
    {
        try
        {
            return f().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static void Same(string text, StatementKind kind, string verb, SqlStatement actual)
    {
        Assert.Equal(text, actual.Text);
        Assert.Equal(kind, actual.Kind);
        Assert.Equal(verb, actual.Verb);
    }

    private static void SameStatement(SqlStatement expected, SqlStatement actual)
    {
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Verb, actual.Verb);
        Assert.Equal(expected.IsUnfilteredDml, actual.IsUnfilteredDml);
        Assert.Equal(expected.DangerousTarget, actual.DangerousTarget);
        Assert.Equal(expected.ChangeTarget, actual.ChangeTarget);
        Assert.Equal(expected.BindNames, actual.BindNames);
    }

    // ---- 안내 글의 오류 번호·휴지통·다시 만들기 문장: Core 가 방언으로 옮기기 전(Core 에 박혀 있던 글)과 같은 글자 ----

    [Fact]
    public void Notice_error_names_and_flashback_note_keep_the_oracle_numbers_and_bin_wording()
    {
        ICopyDialect dialect = OracleCopyDialect.Instance;

        Assert.Equal("ORA-02449", dialect.ReferencedByForeignKeysErrorName);
        Assert.Equal("ORA-24344", dialect.TriggerCompileErrorName);
        Assert.Equal("ORA-01555", dialect.SnapshotTooOldErrorName);
        Assert.Equal("(트리거는 BIN$ 이름으로, 인덱스·권한과 함께 돌아옵니다. 이 표를 참조하던 FK 는 돌아오지 않습니다)", dialect.FlashbackRestoreNote);
    }

    [Fact]
    public void Server_name_trigger_status_and_recycled_child_hooks_keep_the_oracle_behavior()
    {
        ICopyDialect dialect = OracleCopyDialect.Instance;
        ICopyPrecheckDialect precheck = OracleCopyPrecheckDialect.Instance;

        // Oracle 은 SYS_ / BIN$ 만 서버 이름으로 본다(소유자와 무관). Tibero 모양 이름 SCOTT_CON1 은 사용자가 지은 이름이다.
        foreach (var name in new[] { "SYS_C001234", "SYS_IL0000012345C00002$$", "BIN$abc==$0" })
        {
            Assert.True(dialect.IsServerName(name, "SCOTT"));
            Assert.True(precheck.IsServerName(name, "SCOTT"));
        }

        Assert.False(dialect.IsServerName("SCOTT_CON1", "SCOTT"));
        Assert.False(precheck.IsServerName("SCOTT_CON1", "SCOTT"));
        Assert.False(dialect.IsServerName("ORDERS_PK", "SCOTT"));
        // 컴파일 오류 트리거는 ORA-24344 오류로 알려 주므로 상태 조회를 하지 않는다. 지운 자식 표는 FK 를 쥔 채 휴지통에 남지 않는다.
        Assert.Null(dialect.TriggerStatusSql);
        Assert.Null(dialect.RecycledChildForeignKeyNote(["SCOTT.CHILD"]));
    }

    [Theory]
    [InlineData(false, " 원본과 다르게 만들어지는 것: 문자 길이는 글자 단위(CHAR), DEFAULT ON NULL·IDENTITY 옵션(ALWAYS·시작·증가)·DEFERRABLE PK/UK·COLLATE 은 빠지고, 파티션·IOT·임시 표는 일반 표가 됩니다.")]
    [InlineData(true, " 원본과 다르게 만들어지는 것: 문자 길이는 글자 단위(CHAR), DEFAULT ON NULL·IDENTITY 옵션(ALWAYS·시작·증가)·DEFERRABLE PK/UK·COLLATE·도메인 은 빠지고, 파티션·IOT·임시 표는 일반 표가 됩니다.")]
    public void Recreate_difference_note_is_the_text_the_precheck_wrote_before(bool hasDomain, string expected)
    {
        ICopyPrecheckDialect dialect = OracleCopyPrecheckDialect.Instance;

        Assert.Equal(expected, dialect.RecreateDifferenceNote(hasDomain));
    }

    [Fact]
    public void Oracle_error_classifier_has_no_driver_exceptions_outside_dbexception()
    {
        IDbErrorClassifier errors = new OracleErrorClassifier();

        Assert.False(errors.IsOwnDriverException(new InvalidOperationException("x")));
        Assert.False(errors.IsOwnDriverException(new OperationCanceledException()));
    }

    // ---- 오류 범위: 예전 catch (OracleException) 과 같게 바깥 예외만 ----

    [Fact]
    public async Task Error_filter_catches_only_an_outermost_oracle_exception()
    {
        var errors = new OracleErrorClassifier();
        var direct = await RealOracleExceptionAsync();
        var wrapped = new InvalidOperationException("wrapped", direct);

        Assert.True(CopyErrors.IsServer(errors, direct));
        Assert.NotNull(CopyErrors.Server(errors, direct));

        // 분류기 자체는 안쪽까지 찾지만(FromException), 사전 점검은 예전처럼 감싼 예외를 삼키지 않는다.
        Assert.NotNull(errors.FromException(wrapped));
        Assert.False(CopyErrors.IsServer(errors, wrapped));
        Assert.False(CopyErrors.IsServer(errors, new AggregateException(direct)));

        Assert.False(CopyErrors.IsServer(errors, new OperationCanceledException()));
        Assert.False(CopyErrors.IsNoAccess(errors, new OperationCanceledException()));
    }

    /// <summary>닫힌 포트(127.0.0.1:1)로 여는 실제 ODP.NET 예외(OracleException 은 공개 생성자가 없다).</summary>
    private static async Task<OracleException> RealOracleExceptionAsync()
    {
        await using var conn = new OracleConnection(
            "User Id=u;Password=p;Pooling=false;Connection Timeout=3;"
            + "Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=1))(CONNECT_DATA=(SID=X)))");
        return await Assert.ThrowsAsync<OracleException>(() => conn.OpenAsync());
    }
}
