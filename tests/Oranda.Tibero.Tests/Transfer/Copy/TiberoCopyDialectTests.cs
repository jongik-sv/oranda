using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Transfer.Copy;

/// <summary>Tibero 복사 실행 방언(TB3b, 서버 없음). 서버로 도는 확인은 <c>TiberoCopyDialectIt</c>.</summary>
public sealed class TiberoCopyDialectTests
{
    private static readonly TiberoCopyDialect Dialect = TiberoCopyDialect.Instance;

    private static readonly DisabledObject Fk = new(DisabledKind.ForeignKey, "SCOTT", "EMP", "FK_DEPT");

    [Fact]
    public void Ddl_and_savepoint_statements_are_the_oracle_text_with_quoted_names()
    {
        Assert.Equal("ALTER TRIGGER \"SCOTT\".\"TRG\" DISABLE", Dialect.DisableTrigger("SCOTT", "TRG").Text);
        Assert.Equal("ALTER TRIGGER \"SCOTT\".\"TRG\" ENABLE", Dialect.EnableTrigger("SCOTT", "TRG").Text);
        Assert.Equal("ALTER TABLE \"SCOTT\".\"EMP\" DISABLE CONSTRAINT \"FK_DEPT\"", Dialect.DisableConstraint(Fk).Text);
        Assert.Equal("ALTER TABLE \"SCOTT\".\"EMP\" DROP CONSTRAINT \"FK_DEPT\"", Dialect.DropConstraint(Fk).Text);
        Assert.Equal("ALTER TABLE \"SCOTT\".\"EMP\" ENABLE VALIDATE CONSTRAINT \"FK_DEPT\"", Dialect.EnableConstraint(Fk, validate: true).Text);
        Assert.Equal("ALTER TABLE \"SCOTT\".\"EMP\" ENABLE NOVALIDATE CONSTRAINT \"FK_DEPT\"", Dialect.EnableConstraint(Fk, validate: false).Text);
        Assert.Equal("SELECT \"SCOTT\".\"SEQ\".NEXTVAL FROM DUAL CONNECT BY LEVEL <= :" + CopyCommonStatements.NextValuesBind,
            Dialect.NextValues("SCOTT", "SEQ").Text);
        Assert.Equal(StatementKind.Query, Dialect.NextValues("SCOTT", "SEQ").Kind);
        Assert.Equal("SAVEPOINT " + CopyCommonStatements.BatchSavepoint, Dialect.Savepoint(CopyCommonStatements.BatchSavepoint).Text);
        Assert.Equal("ROLLBACK TO SAVEPOINT " + CopyCommonStatements.BatchSavepoint, Dialect.RollbackToSavepoint(CopyCommonStatements.BatchSavepoint).Text);
        Assert.Equal("ALTER x ENABLE NOVALIDATE", Dialect.AddForeignKeyWithoutValidation("ALTER x"));
    }

    [Fact]
    public void Drop_table_is_a_dangerous_drop_without_purge_or_cascade_and_journals_the_table()
    {
        var drop = Dialect.DropTable("SCOTT", "EMP");

        Assert.Equal("DROP TABLE \"SCOTT\".\"EMP\"", drop.Text);
        Assert.Equal(StatementKind.Ddl, drop.Kind);
        Assert.Equal("EMP", drop.DangerousTarget);
        Assert.Equal(new ChangeTarget("SCOTT", "EMP", "TABLE", ChangeOperation.Drop), drop.ChangeTarget);
    }

    [Fact]
    public void Date_value_slot_is_wrapped_in_to_date_so_the_time_survives_the_nls_format() =>
        Assert.Equal("TO_DATE(:C1, 'YYYY-MM-DD HH24:MI:SS')", Dialect.BindPlaceholder("C1", "DATE"));

    [Fact]
    public void Text_value_slot_is_the_plain_bind() =>
        Assert.Equal(":C1", Dialect.BindPlaceholder("C1", "VARCHAR2(10 CHAR)"));

    [Fact]
    public void Read_only_transaction_is_the_psm_block_and_recycle_bin_uses_use_recyclebin()
    {
        var readOnly = Dialect.SetTransactionReadOnly();
        var bin = Dialect.RecycleBinOn();

        Assert.Equal(TiberoExecutionService.ReadOnlyBlock, readOnly.Text); // 최상위 SET TRANSACTION READ ONLY 는 TBR-7001
        Assert.Equal(StatementKind.TransactionControl, readOnly.Kind);
        Assert.Equal("ALTER SESSION SET USE_RECYCLEBIN = Y", bin.Text); // RECYCLEBIN = ON 은 TBR-7001
        Assert.Equal(StatementKind.Other, bin.Kind);
    }

    [Fact]
    public void Restart_identity_is_only_a_comment_because_tibero_has_no_identity_columns() =>
        Assert.Equal("-- " + TiberoCopyDialect.IdentityNotSupported, Dialect.RestartIdentity("SCOTT", "EMP", "ID")); // 예외면 되살리기 전체가 멈춘다

    [Theory]
    [InlineData(-7161, true, false)]
    [InlineData(7161, true, false)]
    [InlineData(-7156, false, true)]
    [InlineData(7156, false, true)]
    [InlineData(2298, false, false)] // Oracle 번호는 Tibero 오류가 아니다
    [InlineData(2449, false, false)]
    [InlineData(-10008, false, false)]
    [InlineData(int.MinValue, false, false)]
    public void Error_checks_follow_the_tibero_numbers_ignoring_the_sign(int number, bool parentKeysNotFound, bool referenced)
    {
        var error = TiberoErrorClassifier.Create(number, "TBR-x");

        Assert.Equal(parentKeysNotFound, Dialect.IsParentKeysNotFound(error));
        Assert.Equal(referenced, Dialect.IsReferencedByForeignKeys(error));
    }

    [Fact]
    public void No_error_is_neither()
    {
        Assert.False(Dialect.IsParentKeysNotFound(null));
        Assert.False(Dialect.IsReferencedByForeignKeys(null));
    }

    [Fact]
    public void Notice_error_names_are_only_the_measured_tibero_numbers_and_never_oracle_numbers()
    {
        ICopyDialect dialect = Dialect;

        Assert.Equal("TBR-7156", dialect.ReferencedByForeignKeysErrorName); // 3.5 실측: 참조되는 부모 DROP
        Assert.Null(dialect.TriggerCompileErrorName); // 미실측: 컴파일 오류 트리거는 오류 없이 INVALID+ENABLED 로 남는다
        Assert.Null(dialect.SnapshotTooOldErrorName); // 미실측
    }

    [Fact]
    public void Flashback_note_does_not_claim_oracle_bin_names_or_what_comes_back_with_the_table()
    {
        ICopyDialect dialect = Dialect;

        Assert.DoesNotContain("BIN$", dialect.FlashbackRestoreNote, StringComparison.Ordinal);
        Assert.DoesNotContain("ORA-", dialect.FlashbackRestoreNote, StringComparison.Ordinal);
        Assert.Contains("확인되지 않았습니다", dialect.FlashbackRestoreNote, StringComparison.Ordinal);
        Assert.StartsWith("(", dialect.FlashbackRestoreNote, StringComparison.Ordinal);
        Assert.EndsWith(")", dialect.FlashbackRestoreNote, StringComparison.Ordinal);
    }
}
