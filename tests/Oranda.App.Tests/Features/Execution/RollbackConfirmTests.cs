using Oranda.App.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Text;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// 되돌리기 지점 설계 10장: 운영 확인 창의 원복 줄·백업 테이블 확인란(10.1, 9장), 실행 로그 줄(10.2), 커밋 전 경고(10.3).
/// </summary>
[Collection("GridEditStatics")]
public sealed class RollbackConfirmTests
{
    private static readonly ConnectionProfile Prod = new("prod-example", "app", "db:1521/ORCL", Environment: ConnectionEnvironment.Production);

    private static ConfirmDialogModel Model(ConfirmExtras extras, RollbackSettings? settings = null) =>
        new(new ConfirmRequest(
            new SqlStatement("UPDATE SAMPLE.ORDERS SET STATUS = 'CLOSED' WHERE ORDER_DATE < DATE '2026-10-01'", new TextRange(0, 10), StatementKind.Dml),
            new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "운영"),
            Prod)
        {
            Extras = extras,
            Rollback = settings ?? new RollbackSettings(),
        });

    [Fact]
    public void Confirm_window_shows_the_script_option_with_estimate_hint_warnings_and_point_line()
    {
        var model = Model(new ConfirmExtras(
            new RollbackOffer(true, NotCreatedReason.None) { EstimatedRows = 1240, DefaultOn = true, Warnings = ["대상 테이블에 트리거 2개가 있습니다."] },
            null), new RollbackSettings { RetentionDays = 14 });

        Assert.True(model.ShowsScriptOption);
        Assert.Contains("1,240", model.ScriptOptionText, StringComparison.Ordinal);
        Assert.True(model.ScriptOptionEnabled);
        Assert.True(model.ScriptOptionDefault);
        Assert.Contains("14일", model.ScriptHintText, StringComparison.Ordinal);
        Assert.Single(model.ScriptWarnings);
        Assert.Equal("실행 직전 SCN 은 원복 스크립트와 상관없이 기록합니다.", model.PointText);
        Assert.Null(model.NotTargetText);

        // 확인란을 끄고 실행하면 원복 스크립트를 만들지 않는다. 취소는 Declined.
        Assert.Equal(new ConfirmAnswer(true, false, null), model.Answer(true, createScript: false, backup: false, null));
        Assert.Equal(new ConfirmAnswer(true, true, null), model.Answer(true, createScript: true, backup: false, null));
        Assert.Same(ConfirmAnswer.Declined, model.Answer(false, true, false, null));

        // 예상 행 수를 모르면 그렇게 적는다.
        var unknown = Model(new ConfirmExtras(new RollbackOffer(true, NotCreatedReason.None) { DefaultOn = true }, null));
        Assert.Contains("알 수 없음", unknown.ScriptOptionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Over_limit_dims_the_option_and_not_target_statements_show_one_line_instead()
    {
        var over = Model(new ConfirmExtras(new RollbackOffer(true, NotCreatedReason.OverLimit) { EstimatedRows = 123_456, OverLimit = true, DefaultOn = true }, null));
        Assert.False(over.ScriptOptionEnabled);
        Assert.False(over.ScriptOptionDefault);
        Assert.Contains("123,456", over.OverLimitText, StringComparison.Ordinal);
        Assert.Contains("100,000", over.OverLimitText, StringComparison.Ordinal);
        Assert.False(over.Answer(true, createScript: true, backup: false, null).CreateScript); // 흐린 확인란은 만들지 않는다

        var merge = Model(new ConfirmExtras(RollbackOffer.NotApplicable(NotCreatedReason.NotTarget, "MERGE"), null));
        Assert.False(merge.ShowsScriptOption);
        Assert.Equal("이 문장은 원복 스크립트를 만들지 않습니다(MERGE). 실행 직전 SCN 만 기록합니다.", merge.NotTargetText);
        Assert.Null(merge.PointText);

        // 원복 제안이 없는 확인(예: 시험 접속에서 SCN 만)은 줄을 더하지 않는다.
        var none = Model(ConfirmExtras.None);
        Assert.False(none.ShowsScriptOption);
        Assert.Null(none.NotTargetText);
        Assert.False(none.ShowsBackupOption);
    }

    [Fact]
    public void Backup_option_answers_the_create_sql_with_the_edited_name()
    {
        var table = new DbObjectRef("SAMPLE", "PRODUCTS", "TABLE");
        var model = Model(new ConfirmExtras(null, new BackupOffer(table, "ORB_PRODUCTS_11131405", "CREATE TABLE ORB_PRODUCTS_11131405 AS SELECT * FROM SAMPLE.PRODUCTS", "TRUNCATE 뒤에는 이전 데이터를 읽지 못합니다.")));

        Assert.True(model.ShowsBackupOption);
        Assert.Contains("열린 트랜잭션을 먼저 커밋", model.BackupHintText, StringComparison.Ordinal);
        Assert.Null(model.Answer(true, false, backup: false, "ORB_PRODUCTS_11131405").BackupSql);
        Assert.Equal("CREATE TABLE ORB_PRODUCTS_11131405 AS SELECT * FROM SAMPLE.PRODUCTS", model.Answer(true, false, true, null).BackupSql);
        Assert.Equal("CREATE TABLE BK_PRODUCTS AS SELECT * FROM SAMPLE.PRODUCTS", model.Answer(true, false, true, " BK_PRODUCTS ").BackupSql);
        Assert.Equal("CREATE TABLE ORB_PRODUCTS_11131405 AS SELECT * FROM SAMPLE.PRODUCTS", model.Answer(true, false, true, "BK; DROP").BackupSql); // 쓸 수 없는 이름은 제안 그대로
        // 이름 자리만 바꾼다(스키마·원본 표 이름에 같은 글이 있어도).
        Assert.Equal("CREATE TABLE APP.NEWNAME AS SELECT * FROM APP.ORB_X", ConfirmDialogModel.RenameTarget("CREATE TABLE APP.ORB_X AS SELECT * FROM APP.ORB_X", "ORB_X", "NEWNAME"));
        Assert.True(ConfirmDialogModel.IsValidBackupName("BK_PRODUCTS"));
        Assert.False(ConfirmDialogModel.IsValidBackupName("1BK"));
        Assert.False(ConfirmDialogModel.IsValidBackupName("BK PRODUCTS"));
    }

    [Fact]
    public void Run_log_lines_follow_the_summary_and_point_only_statements_add_none()
    {
        Assert.Empty(ExecutionController.RollbackLogLines(null));
        Assert.Empty(ExecutionController.RollbackLogLines(new RollbackStatementSummary(RollbackScriptState.NotCreated, NotCreatedReason.NotProduction, 0)));

        var open = ExecutionController.RollbackLogLines(new RollbackStatementSummary(RollbackScriptState.Open, NotCreatedReason.None, 12));
        Assert.Equal([(MessageKind.Info, "원복 스크립트 12문장 (커밋하면 확정)")], open);

        var over = ExecutionController.RollbackLogLines(new RollbackStatementSummary(RollbackScriptState.NotCreated, NotCreatedReason.OverLimit, 0));
        Assert.Contains("상한 넘음", Assert.Single(over).Text, StringComparison.Ordinal);

        var mismatch = ExecutionController.RollbackLogLines(new RollbackStatementSummary(RollbackScriptState.Incomplete, NotCreatedReason.None, 12)
        {
            Warning = "원복 스크립트 불완전: 행 수 다름(잠금 12, 실행 13) — 커밋 전에 확인하세요",
        });
        Assert.Equal(MessageKind.Warning, Assert.Single(mismatch).Kind);

        // 엔진이 같은 글을 LogLine 과 Warning 에 함께 채우면 경고 한 줄만 남는다.
        const string Same = "원복 스크립트 불완전: 행 수 다름(잠금 12, 실행 13) — 커밋 전에 확인하세요";
        var once = ExecutionController.RollbackLogLines(new RollbackStatementSummary(RollbackScriptState.Incomplete, NotCreatedReason.None, 12) { LogLine = Same, Warning = Same });
        Assert.Equal([(MessageKind.Warning, Same)], once);

        var returning = ExecutionController.RollbackLogLines(new RollbackStatementSummary(RollbackScriptState.Open, NotCreatedReason.None, 1) { ReturningAppended = true });
        Assert.Equal(2, returning.Count);
        Assert.Contains("RETURNING ROWID INTO", returning[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public Task Commit_asks_first_when_the_open_group_has_a_row_count_mismatch() => Ui.Run(async () =>
    {
        var ledger = new RecordingRollbackLedger { OpenState = new RollbackOpenState(2, 24, HasMismatch: true, HasIncomplete: true) };
        using var rig = new Rig(rollbackLedger: ledger);

        // 툴바 커밋: 아니오면 커밋하지 않는다.
        rig.Dialogs.IncompleteCommitAnswer = false;
        await rig.Controller.ConfirmedCommitAsync(rig.Session);
        Assert.Equal(0, rig.Session.Commits);
        Assert.Contains("불완전", Assert.Single(rig.Dialogs.IncompleteCommitAsked), StringComparison.Ordinal);

        // 편집기의 COMMIT 문장: 아니오면 보내지 않는다.
        rig.Host.NewDocument("commit");
        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Empty(rig.Exec.Statements);
        Assert.Equal(2, rig.Dialogs.IncompleteCommitAsked.Count);

        rig.Dialogs.IncompleteCommitAnswer = true;
        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Single(rig.Exec.Statements);

        // 행 수가 다른 문장이 없으면 묻지 않는다.
        ledger.OpenState = new RollbackOpenState(1, 3, HasMismatch: false, HasIncomplete: false);
        await rig.Controller.ConfirmedCommitAsync(rig.Session);
        Assert.Equal(1, rig.Session.Commits);
        Assert.Equal(3, rig.Dialogs.IncompleteCommitAsked.Count);
    });

    [Fact]
    public void Commit_statement_detection_needs_the_whole_word()
    {
        static SqlStatement Tx(string text) => new(text, new TextRange(0, text.Length), StatementKind.TransactionControl);
        Assert.True(ExecutionController.IsCommitStatement(Tx("COMMIT")));
        Assert.True(ExecutionController.IsCommitStatement(Tx("  commit work")));
        Assert.False(ExecutionController.IsCommitStatement(Tx("ROLLBACK")));
        Assert.False(ExecutionController.IsCommitStatement(new SqlStatement("COMMIT", new TextRange(0, 6), StatementKind.Dml)));
        Assert.False(ExecutionController.IsCommitStatement(Tx("COMMITTED")));
        Assert.True(ExecutionController.IsCommitStatement(Tx("-- 끝\n/* 확정 */ COMMIT")));
    }
}
