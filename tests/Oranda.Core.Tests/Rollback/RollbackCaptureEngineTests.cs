using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Text;
using Oranda.Testing;
using Oranda.Testing.Rollback;

namespace Oranda.Core.Tests.Rollback;

/// <summary>원복 캡처 엔진(되돌리기 지점 설계 6장·7.1, 16.1 「Core 엔진」).</summary>
public class RollbackCaptureEngineTests
{
    private static readonly DbObjectRef Products = new("SAMPLE", "PRODUCTS", "TABLE");
    private static readonly Dictionary<string, BindValue> NoBinds = [];

    private sealed class Rig
    {
        public Rig(ConnectionEnvironment env = ConnectionEnvironment.Production, bool withPk = true, RollbackSettings? settings = null,
                   bool store = true, bool returning = true)
        {
            Metadata.Tables[Products] = new TableInfo(Products,
            [
                new ColumnInfo("PRODUCT_ID", 1, "NUMBER"),
                new ColumnInfo("PRODUCT_NAME", 2, "VARCHAR2(50)"),
                new ColumnInfo("UNIT_PRICE", 3, "NUMBER(10,2)"),
                new ColumnInfo("MEMO", 4, "CLOB"),
                new ColumnInfo("NAME_UPPER", 5, "VARCHAR2(50)") { IsVirtual = true },
            ])
            {
                Constraints = withPk ? [new ConstraintInfo("PRODUCTS_PK", ConstraintKind.PrimaryKey, ["PRODUCT_ID"])] : [],
            };
            Session = new FakeSession(TestData.Profile(env));
            Engine = new RollbackCaptureEngine(Ledger, () => settings ?? new RollbackSettings(), Metadata, store ? new InMemoryRollbackStore() : null);
            Dialect = new FakeRollbackDialect { SupportsReturningRowId = returning };
            Channel.When("GET_SYSTEM_CHANGE_NUMBER", [48213907m]);
        }

        public FakeMetadataProvider Metadata { get; } = new();

        public RecordingRollbackLedger Ledger { get; } = new();

        public FakeRollbackChannel Channel { get; } = new();

        public FakeRollbackDialect Dialect { get; }

        public FakeSession Session { get; }

        public RollbackCaptureEngine Engine { get; }

        public RollbackRequest Request(string text, DmlShape? shape, ExecutionOptions? options = null, StatementKind kind = StatementKind.Dml,
                                       IReadOnlyDictionary<string, BindValue>? binds = null) =>
            new(Session, new SqlStatement(text, default(TextRange), kind), binds ?? NoBinds, options ?? ExecutionOptions.Default) { Shape = shape };

        public async Task<(IStatementCapture Capture, ConfirmExtras Extras)> BeginAsync(RollbackRequest request, ConfirmAnswer? answer = null)
        {
            var extras = await Engine.PrepareAsync(request, Channel, Dialect, CancellationToken.None);
            var capture = await Engine.BeginAsync(request, extras, answer ?? ConfirmAnswer.Default(extras), Channel, Dialect, CancellationToken.None);
            return (capture, extras);
        }
    }

    private static DmlShape UpdatePrice => new(DmlVerb.Update)
    {
        Owner = "SAMPLE", Name = "PRODUCTS", TableText = "SAMPLE.PRODUCTS", WhereText = "CATEGORY_ID = :c", SetColumns = ["UNIT_PRICE"],
    };

    [Fact]
    public async Task Update_sends_savepoint_lock_read_then_after_read_and_records_reverse_update()
    {
        var rig = new Rig();
        rig.Channel.When("SELECT COUNT(*)", [12m])
            .When("FOR UPDATE", ["RID1", "1040", "4300"], ["RID2", "1041", "5000"])
            .When("WHERE ROWID IN", ["RID1", "1040", "4730"], ["RID2", "1041", "5000"]);
        var binds = new Dictionary<string, BindValue> { ["c"] = new(BindDataType.Number, 3m), ["unused"] = new(BindDataType.Number, 1m) };
        var request = rig.Request("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = :c", UpdatePrice, binds: binds);

        var (capture, extras) = await rig.BeginAsync(request);
        var summary = await capture.CompleteAsync(2, new Dictionary<string, object?>(), "7.12.3", CancellationToken.None);

        Assert.Equal(12, extras.Rollback!.EstimatedRows);
        Assert.True(extras.Rollback.DefaultOn);
        Assert.Collection(rig.Channel.SentSql,
            s => Assert.Contains("ALL_TRIGGERS", s, StringComparison.Ordinal),
            s => Assert.StartsWith("SELECT COUNT(*) FROM SAMPLE.PRODUCTS WHERE CATEGORY_ID = :c", s, StringComparison.Ordinal),
            s => Assert.Contains("GET_SYSTEM_CHANGE_NUMBER", s, StringComparison.Ordinal),
            s => Assert.Equal("SAVEPOINT ORANDA$RB", s),
            s => Assert.Equal("SELECT ROWIDTOCHAR(ROWID), TO_CHAR(PRODUCT_ID, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,'''), "
                              + "TO_CHAR(UNIT_PRICE, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''') FROM SAMPLE.PRODUCTS WHERE CATEGORY_ID = :c FOR UPDATE", s),
            s => Assert.StartsWith("SELECT ROWIDTOCHAR(ROWID), ", s, StringComparison.Ordinal));
        Assert.Equal(["c"], rig.Channel.SentItems[4].Binds.Keys);
        Assert.Equal(["c"], rig.Channel.SentItems[1].Binds.Keys);

        var record = Assert.Single(rig.Ledger.Records);
        Assert.Equal(RollbackScriptState.Open, record.State);
        Assert.Equal("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730", Assert.Single(record.Steps).Sql);
        Assert.Equal("SELECT COUNT(*) FROM SAMPLE.PRODUCTS WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4300", Assert.Single(record.Probes).RolledBackIfOne);
        Assert.Equal(2, record.LockedRows);
        Assert.Equal(48213907, record.BeforePoint!.Number);
        Assert.Equal("7.12.3", record.TransactionId);
        Assert.Equal(1, summary.StepCount);
        Assert.Contains("1문장", summary.LogLine, StringComparison.Ordinal);
        Assert.Empty(record.TouchedRowIds);
    }

    [Fact]
    public async Task Lock_read_over_limit_runs_without_script()
    {
        var rig = new Rig(settings: new RollbackSettings { MaxRowsPerStatement = 1 });
        rig.Channel.When("FOR UPDATE", ["RID1", "1", "1"], ["RID2", "2", "2"], ["RID3", "3", "3"]);

        var (capture, _) = await rig.BeginAsync(rig.Request("UPDATE …", UpdatePrice));
        var summary = await capture.CompleteAsync(3, new Dictionary<string, object?>(), null, CancellationToken.None);

        Assert.Equal(2, rig.Channel.SentItems.Single(s => s.Sql.EndsWith("FOR UPDATE", StringComparison.Ordinal)).MaxRows);
        Assert.Equal(NotCreatedReason.OverLimit, Assert.Single(rig.Ledger.Records).Reason);
        Assert.Empty(rig.Ledger.Records[0].Steps);
        Assert.Contains("상한", summary.LogLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row_count_mismatch_is_incomplete_with_warning()
    {
        var rig = new Rig();
        rig.Channel.When("FOR UPDATE", ["RID1", "1040", "4300"]).When("WHERE ROWID IN", ["RID1", "1040", "4730"]);

        var (capture, _) = await rig.BeginAsync(rig.Request("UPDATE …", UpdatePrice));
        var summary = await capture.CompleteAsync(2, new Dictionary<string, object?>(), null, CancellationToken.None);

        Assert.Equal(RollbackScriptState.Incomplete, summary.State);
        Assert.Contains("행 수 다름(잠금 1, 실행 2)", rig.Ledger.Records[0].IncompleteReason, StringComparison.Ordinal);
        Assert.NotNull(summary.Warning);
    }

    [Fact]
    public async Task Abandon_rolls_back_to_internal_savepoint_and_records_nothing()
    {
        var rig = new Rig();
        rig.Channel.When("FOR UPDATE", ["RID1", "1040", "4300"]);

        var (capture, _) = await rig.BeginAsync(rig.Request("UPDATE …", UpdatePrice));
        var summary = await capture.AbandonAsync(new DbError("ORA-00001", "unique"), CancellationToken.None);

        Assert.Equal("ROLLBACK TO SAVEPOINT ORANDA$RB", rig.Channel.SentSql[^1]);
        Assert.Empty(rig.Ledger.Records);
        Assert.Equal("원복 스크립트용 잠금은 풀었습니다", summary.LogLine);
    }

    [Fact]
    public async Task Pk_change_uses_rowid_and_marks_touched_rows()
    {
        var rig = new Rig();
        var shape = UpdatePrice with { SetColumns = ["PRODUCT_ID"] };
        rig.Channel.When("FOR UPDATE", ["RID1", "1040"]).When("WHERE ROWID IN", ["RID1", "9040"]);

        var (capture, _) = await rig.BeginAsync(rig.Request("UPDATE …", shape));
        await capture.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);

        var record = Assert.Single(rig.Ledger.Records);
        Assert.Equal("UPDATE SAMPLE.PRODUCTS SET PRODUCT_ID = 1040 WHERE ROWID = CHARTOROWID('RID1') AND PRODUCT_ID = 9040", record.Steps[0].Sql);
        Assert.Equal(["RID1"], record.TouchedRowIds);
    }

    [Fact]
    public async Task Lock_read_privilege_failure_releases_savepoint_and_runs_without_script()
    {
        var rig = new Rig();
        rig.Channel.Fail("FOR UPDATE", FakeRollbackChannel.PrivilegeError());

        var (capture, _) = await rig.BeginAsync(rig.Request("UPDATE …", UpdatePrice));
        var summary = await capture.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);

        Assert.Equal("ROLLBACK TO SAVEPOINT ORANDA$RB", rig.Channel.SentSql[^1]);
        Assert.Equal((RollbackScriptState.NotCreated, NotCreatedReason.Failed), (summary.State, summary.Reason));
        Assert.Contains("권한 없음", summary.LogLine, StringComparison.Ordinal);
        Assert.Same(capture.StatementToSend.Text, rig.Request("UPDATE …", UpdatePrice).Statement.Text);
    }

    [Fact]
    public async Task Cancel_during_lock_read_releases_savepoint_and_propagates()
    {
        var rig = new Rig();
        rig.Channel.Hang("FOR UPDATE");
        var request = rig.Request("UPDATE …", UpdatePrice);
        var extras = await rig.Engine.PrepareAsync(request, rig.Channel, rig.Dialect, default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Engine.BeginAsync(request, extras, ConfirmAnswer.Default(extras), rig.Channel, rig.Dialect, cts.Token));
        Assert.Equal("ROLLBACK TO SAVEPOINT ORANDA$RB", rig.Channel.SentSql[^1]);
    }

    [Fact]
    public async Task Large_set_column_is_not_reverted_and_row_movement_is_incomplete()
    {
        var rig = new Rig();
        var shape = UpdatePrice with { SetColumns = ["UNIT_PRICE", "MEMO"] };
        rig.Channel.When("FOR UPDATE", ["RID1", "1040", "4300", 0m], ["RID2", "1041", "1", 0m])
            .When("WHERE ROWID IN", ["RID1", "1040", "4730", 0m]);

        var (capture, extras) = await rig.BeginAsync(rig.Request("UPDATE …", shape));
        await capture.CompleteAsync(2, new Dictionary<string, object?>(), null, CancellationToken.None);

        var record = Assert.Single(rig.Ledger.Records);
        Assert.Equal("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730", Assert.Single(record.Steps).Sql);
        Assert.Contains("큰 열(MEMO)은 되돌리지 않음", record.IncompleteReason, StringComparison.Ordinal);
        Assert.Contains("행 이동", record.IncompleteReason, StringComparison.Ordinal);
        Assert.Contains(extras.Rollback!.Warnings, w => w.Contains("MEMO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identity_column_restore_is_marked_incomplete_with_note()
    {
        var rig = new Rig();
        rig.Metadata.Tables[Products] = new TableInfo(Products, [new ColumnInfo("ID", 1, "NUMBER") { IsIdentity = true }, new ColumnInfo("NAME", 2, "VARCHAR2(9)")])
        {
            Constraints = [new ConstraintInfo("PK", ConstraintKind.PrimaryKey, ["ID"])],
        };
        var shape = new DmlShape(DmlVerb.Delete) { Owner = "SAMPLE", Name = "PRODUCTS" };
        rig.Channel.When("FOR UPDATE", ["RID1", "7", "a"]);

        var (capture, _) = await rig.BeginAsync(rig.Request("DELETE FROM SAMPLE.PRODUCTS", shape));
        await capture.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);

        var step = Assert.Single(rig.Ledger.Records[0].Steps);
        Assert.Equal("INSERT INTO SAMPLE.PRODUCTS (ID, NAME) VALUES (7, 'a')", step.Sql);
        Assert.Contains("ID 열", step.Note, StringComparison.Ordinal);
        Assert.Equal(RollbackScriptState.Incomplete, rig.Ledger.Records[0].State);
    }

    [Fact]
    public async Task Production_statement_without_point_is_still_recorded()
    {
        var rig = new Rig(settings: new RollbackSettings { RecordPoint = false });
        var (capture, extras) = await rig.BeginAsync(rig.Request("UPDATE …", UpdatePrice), ConfirmAnswer.Default(ConfirmExtras.None));
        await capture.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);

        Assert.True(extras.Rollback!.Applicable);
        var record = Assert.Single(rig.Ledger.Records);
        Assert.Equal((NotCreatedReason.Declined, (ChangePoint?)null), (record.Reason, record.BeforePoint));
    }

    [Fact]
    public async Task No_pk_update_uses_rowid()
    {
        var rig = new Rig(withPk: false);
        rig.Channel.When("FOR UPDATE", ["RID1", "4300"]).When("WHERE ROWID IN", ["RID1", "4730"]);

        var (capture, _) = await rig.BeginAsync(rig.Request("UPDATE …", UpdatePrice));
        await capture.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);

        var record = Assert.Single(rig.Ledger.Records);
        Assert.Equal("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300 WHERE ROWID = CHARTOROWID('RID1') AND UNIT_PRICE = 4730", record.Steps[0].Sql);
        Assert.Equal(["RID1"], record.TouchedRowIds);
    }

    [Fact]
    public async Task Delete_reinserts_rows_without_virtual_column_and_skips_rows_with_large_values()
    {
        var rig = new Rig();
        var shape = new DmlShape(DmlVerb.Delete) { Owner = "SAMPLE", Name = "PRODUCTS", TableText = "SAMPLE.PRODUCTS", WhereText = "DISCONTINUED = 'Y'" };
        // 읽는 열 = PRODUCT_ID, PRODUCT_NAME, UNIT_PRICE, MEMO(NULL 이면 1)
        rig.Channel.When("FOR UPDATE", ["RID1", "1040", "보리차 티백", "4730", 1m], ["RID2", "1041", "녹차", "100", 0m]);

        var (capture, _) = await rig.BeginAsync(rig.Request("DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y'", shape));
        await capture.CompleteAsync(2, new Dictionary<string, object?>(), null, CancellationToken.None);

        var record = Assert.Single(rig.Ledger.Records);
        Assert.Equal("INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, UNIT_PRICE, MEMO) VALUES (1040, '보리차 티백', 4730, NULL)",
                     Assert.Single(record.Steps).Sql);
        Assert.Equal(RollbackScriptState.Incomplete, record.State);
        Assert.Contains("큰 열", record.IncompleteReason, StringComparison.Ordinal);
        Assert.Equal("SELECT COUNT(*) FROM SAMPLE.PRODUCTS WHERE PRODUCT_ID = 1040", record.Probes[0].RolledBackIfOne);
    }

    [Fact]
    public async Task Insert_values_appends_returning_and_deletes_inserted_row()
    {
        var rig = new Rig();
        var shape = new DmlShape(DmlVerb.InsertValues) { Owner = "SAMPLE", Name = "PRODUCTS", TableText = "SAMPLE.PRODUCTS" };
        rig.Channel.When("WHERE ROWID IN", ["RIDN", "2001", "유자차 스틱", "6200", 1m]);

        var (capture, _) = await rig.BeginAsync(rig.Request("INSERT INTO SAMPLE.PRODUCTS VALUES (2001, '유자차 스틱', 6200, NULL)", shape));
        Assert.EndsWith("\nRETURNING ROWID INTO :ORANDA_RID", capture.StatementToSend.Text, StringComparison.Ordinal);
        Assert.Equal(BindDirection.Out, capture.BindsToSend["ORANDA_RID"].Direction);
        var summary = await capture.CompleteAsync(1, new Dictionary<string, object?> { ["ORANDA_RID"] = "RIDN" }, null, CancellationToken.None);

        Assert.True(summary.ReturningAppended);
        Assert.Equal("DELETE FROM SAMPLE.PRODUCTS WHERE PRODUCT_ID = 2001 AND PRODUCT_NAME = '유자차 스틱' AND UNIT_PRICE = 6200 AND MEMO IS NULL",
                     Assert.Single(rig.Ledger.Records[0].Steps).Sql);
        Assert.DoesNotContain(rig.Channel.SentSql, s => s.StartsWith("SAVEPOINT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Insert_without_returning_support_or_with_bind_clash_is_incomplete_and_unchanged()
    {
        var shape = new DmlShape(DmlVerb.InsertValues) { Owner = "SAMPLE", Name = "PRODUCTS" };
        var noReturning = new Rig(returning: false);
        var (a, _) = await noReturning.BeginAsync(noReturning.Request("INSERT INTO SAMPLE.PRODUCTS VALUES (1, 'a', 1, NULL)", shape));
        await a.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);
        Assert.Equal("INSERT INTO SAMPLE.PRODUCTS VALUES (1, 'a', 1, NULL)", a.StatementToSend.Text);
        Assert.Contains("RETURNING 미지원", noReturning.Ledger.Records[0].IncompleteReason, StringComparison.Ordinal);

        var clash = new Rig();
        var (b, _) = await clash.BeginAsync(clash.Request("INSERT INTO SAMPLE.PRODUCTS VALUES (:ORANDA_RID, 'a', 1, NULL)", shape));
        Assert.DoesNotContain("RETURNING", b.StatementToSend.Text, StringComparison.Ordinal);
        Assert.False(b.BindsToSend.ContainsKey("ORANDA_RID"));
        await b.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);
        Assert.Contains("ORANDA_RID", clash.Ledger.Records[0].IncompleteReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_records_point_only()
    {
        var rig = new Rig(ConnectionEnvironment.Development);

        var (capture, extras) = await rig.BeginAsync(rig.Request("UPDATE …", UpdatePrice));
        var summary = await capture.CompleteAsync(5, new Dictionary<string, object?>(), "1.2.3", CancellationToken.None);

        Assert.Same(ConfirmExtras.None, extras);
        Assert.Single(rig.Channel.SentSql); // 시점 읽기 하나
        var record = Assert.Single(rig.Ledger.Records);
        Assert.Equal((RollbackScriptState.NotCreated, NotCreatedReason.NotProduction), (record.State, record.Reason));
        Assert.Equal(48213907, record.BeforePoint!.Number);
        Assert.Null(summary.LogLine);
    }

    [Fact]
    public async Task Recording_modes_none_and_point_only()
    {
        var none = new Rig();
        var (passThrough, _) = await none.BeginAsync(none.Request("UPDATE …", UpdatePrice, new ExecutionOptions { Recording = ChangeRecording.None }));
        await passThrough.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);
        Assert.Empty(none.Channel.SentSql);
        Assert.Empty(none.Ledger.Records);

        var pointOnly = new Rig();
        var (capture, extras) = await pointOnly.BeginAsync(pointOnly.Request("UPDATE …", UpdatePrice, new ExecutionOptions { Recording = ChangeRecording.PointOnly }));
        await capture.CompleteAsync(1, new Dictionary<string, object?>(), null, CancellationToken.None);
        Assert.Same(ConfirmExtras.None, extras);
        Assert.Equal(NotCreatedReason.Suppressed, Assert.Single(pointOnly.Ledger.Records).Reason);
        Assert.DoesNotContain(pointOnly.Channel.SentSql, s => s.StartsWith("SAVEPOINT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_offers_not_target_no_store_and_unknown_count()
    {
        var merge = new Rig();
        var extras = await merge.Engine.PrepareAsync(merge.Request("MERGE …", new DmlShape(DmlVerb.Merge) { Name = "PRODUCTS" }), merge.Channel, merge.Dialect, default);
        Assert.Equal((false, NotCreatedReason.NotTarget, "MERGE"), (extras.Rollback!.Applicable, extras.Rollback.Reason, extras.Rollback.Message));

        var noStore = new Rig(store: false);
        extras = await noStore.Engine.PrepareAsync(noStore.Request("UPDATE …", UpdatePrice), noStore.Channel, noStore.Dialect, default);
        Assert.Equal(NotCreatedReason.NoSecureStore, extras.Rollback!.Reason);

        var slow = new Rig();
        slow.Channel.Hang("SELECT COUNT(*)").When("ALL_TRIGGERS", ["TRIGGER", "PRODUCTS_TRG"]);
        var engine = new RollbackCaptureEngine(slow.Ledger, () => new RollbackSettings(), slow.Metadata, new InMemoryRollbackStore())
        {
            CountTimeout = TimeSpan.FromMilliseconds(50),
        };
        extras = await engine.PrepareAsync(slow.Request("UPDATE …", UpdatePrice), slow.Channel, slow.Dialect, default);
        Assert.True(extras.Rollback!.Applicable);
        Assert.Null(extras.Rollback.EstimatedRows);
        Assert.Contains(extras.Rollback.Warnings, w => w.Contains("트리거 1개", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Probe_end_decides_commit_or_unknown()
    {
        var rig = new Rig();
        rig.Ledger.ProbeList = [new RollbackProbe("SELECT 1 /*c*/ FROM DUAL", "SELECT 0 /*r*/ FROM DUAL")];
        rig.Channel.When("/*c*/", [1m]).When("/*r*/", [0m]);
        Assert.Equal(TransactionEndKind.Commit, await rig.Engine.ProbeEndAsync(rig.Session.Id, rig.Channel, default));

        rig.Ledger.ProbeList = [new RollbackProbe("SELECT 1 /*c*/ FROM DUAL", "SELECT 0 /*r*/ FROM DUAL"), new RollbackProbe("/*x*/", "/*y*/")];
        rig.Channel.When("/*x*/", [0m]).When("/*y*/", [1m]);
        Assert.Equal(TransactionEndKind.Unknown, await rig.Engine.ProbeEndAsync(rig.Session.Id, rig.Channel, default));
    }

    [Fact]
    public async Task Point_reading_remembers_the_method_that_worked()
    {
        var rig = new Rig();
        var channel = new FakeRollbackChannel()
            .Fail("GET_SYSTEM_CHANGE_NUMBER", new DbError("ORA-00904", "invalid identifier"))
            .When("TIMESTAMP_TO_SCN", [77m]);

        Assert.Equal(77, (await rig.Engine.ReadPointAsync(rig.Session.Id, channel, rig.Dialect, default))!.Number);
        Assert.Equal(77, (await rig.Engine.ReadPointAsync(rig.Session.Id, channel, rig.Dialect, default))!.Number);
        Assert.Single(channel.SentSql, s => s.Contains("GET_SYSTEM_CHANGE_NUMBER", StringComparison.Ordinal));
    }
}
