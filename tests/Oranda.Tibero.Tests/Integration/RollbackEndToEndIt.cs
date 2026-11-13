using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Journal;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Rollback.Ledger;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Testing.Rollback;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 되돌리기 지점 Tibero 끝까지 시험(RB-11·RB-13·RB-14, 설계 16.3): 실제 엔진(<see cref="RollbackCaptureEngine"/>)·원장(<see cref="RollbackLedger"/>)·
/// Tibero 메타데이터·방언·통로로 운영 프로필에서 문장을 실행하고 커밋한 뒤, 저장된 원복 문장을 역순으로 실행해 원래 값으로 돌아오는지 본다.
/// 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c>), <c>ORB_RB</c> 표만 만들고 처음과 끝에 지운다(백업 테이블 <c>ORB_ORB_RB…</c> 포함).
/// </summary>
[Trait("Category", "Integration")]
public class RollbackEndToEndIt(ITestOutputHelper output)
{
    private const string E = "ORB_RB_E";
    private const string B = "ORB_RB_B";

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    [TiberoWriteFact]
    public async Task Update_delete_and_single_row_insert_commit_into_a_script_that_restores_the_rows_when_run_in_reverse()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var store = new InMemoryRollbackStore();
        var ledger = new RollbackLedger(store, it.Events);
        var metadata = new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync);
        var engine = new RollbackCaptureEngine(ledger, static () => new RollbackSettings(), metadata, store);
        var manager = new TiberoSessionManager(it.Events, null, ledger, engine);
        var profile = it.Profile() with { Environment = ConnectionEnvironment.Production };
        TiberoSession session;
        using (var credential = new SecureCredential(it.Env.Password))
        {
            session = (TiberoSession)await manager.OpenAsync(profile, credential, SessionPurpose.Work, CancellationToken.None);
        }

        await using var _ = session;
        var service = new TiberoExecutionService(new AllowAll(), new RecordingRollbackInteraction(), it.Events, null, dbmsOutput: false, capture: engine);
        await CleanupAsync(service, session);
        try
        {
            await RunAsync(service, session, $"CREATE TABLE {E} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30), PRICE NUMBER(10,2), TS TIMESTAMP(6))", StatementKind.Ddl);
            await RunAsync(service, session,
                $"INSERT INTO {E} SELECT LEVEL, 'n' || LEVEL, LEVEL * 10.5, TIMESTAMP '2026-11-13 10:00:00.123456' + NUMTODSINTERVAL(LEVEL, 'SECOND') FROM DUAL CONNECT BY LEVEL <= 3",
                StatementKind.Dml);
            await RunAsync(service, session, "COMMIT", StatementKind.TransactionControl);
            var before = await SnapshotAsync(service, session);

            var update = await RunAsync(service, session, $"UPDATE {E} SET PRICE = PRICE * 2 WHERE ID <= 2", StatementKind.Dml,
                new DmlShape(DmlVerb.Update) { Name = E, TableText = E, WhereText = "ID <= 2", SetColumns = ["PRICE"] });
            var delete = await RunAsync(service, session, $"DELETE FROM {E} WHERE ID = 3", StatementKind.Dml,
                new DmlShape(DmlVerb.Delete) { Name = E, TableText = E, WhereText = "ID = 3" });
            var insert = await RunAsync(service, session, $"INSERT INTO {E} (ID, NAME, PRICE) VALUES (4, 'it''s', 1.25)", StatementKind.Dml,
                new DmlShape(DmlVerb.InsertValues) { Name = E, TableText = E, InsertColumns = ["ID", "NAME", "PRICE"] });
            foreach (var (label, r) in new[] { ("update", update), ("delete", delete), ("insert", insert) })
            {
                output.WriteLine($"RB13 e2e {label}: status={r.Status} rows={r.RowsAffected} rollback={r.Rollback?.State}/{r.Rollback?.Reason} steps={r.Rollback?.StepCount} "
                                 + $"returning={r.Rollback?.ReturningAppended} log={r.Rollback?.LogLine} warn={r.Rollback?.Warning}");
                Assert.Equal(ExecutionStatus.Succeeded, r.Status);
                Assert.Equal(RollbackScriptState.Open, r.Rollback?.State);
            }

            Assert.False(insert.OutValues.ContainsKey(RollbackCaptureEngine.ReturningBind));
            await RunAsync(service, session, "COMMIT", StatementKind.TransactionControl);

            var group = Assert.Single(store.Groups, g => g.StepCount > 0);
            output.WriteLine($"RB13 e2e group: state={group.State} end={group.EndKind}/{group.EndSource} steps={group.StepCount} rows={group.RowsTotal} "
                             + $"base={group.BasePoint?.Text} commit={group.CommitPoint?.Text} tx={(group.TransactionId is null ? "null" : "set")}");
            Assert.Equal(RollbackScriptState.Confirmed, group.State);
            Assert.Equal("TSN", group.CommitPoint?.Name);
            var document = store.Document(group.Id)!;

            // 원복: 문장 역순, 문장 안 차례대로. 원복 실행처럼 Recording = None 으로 보낸다.
            var none = new ExecutionOptions { Recording = ChangeRecording.None };
            foreach (var statement in document.Statements.OrderByDescending(s => s.Seq))
            {
                foreach (var step in statement.Steps)
                {
                    var r = await service.ExecuteAsync(session, new SqlStatement(step.Sql, default, StatementKind.Dml), NoBinds, none, CancellationToken.None);
                    output.WriteLine($"RB13 e2e step #{statement.Seq}: rows={r.RowsAffected} status={r.Status} {r.Error?.Code}");
                    Assert.Equal(1L, r.RowsAffected);
                }
            }

            await RunAsync(service, session, "COMMIT", StatementKind.TransactionControl);
            Assert.Equal(before, await SnapshotAsync(service, session));
        }
        finally
        {
            await CleanupAsync(service, session);
        }
    }

    [TiberoWriteFact]
    public async Task Truncate_on_a_production_profile_creates_the_offered_backup_table_first()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(profile: it.Profile() with { Environment = ConnectionEnvironment.Production });
        var interaction = new RecordingRollbackInteraction();
        var service = new TiberoExecutionService(new ConfirmAll(), interaction, it.Events, null, dbmsOutput: false);
        await CleanupAsync(service, session);
        try
        {
            await RunAsync(service, session, $"CREATE TABLE {B} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30))", StatementKind.Ddl);
            await RunAsync(service, session, $"INSERT INTO {B} SELECT LEVEL, 'n' || LEVEL FROM DUAL CONNECT BY LEVEL <= 5", StatementKind.Dml);
            await RunAsync(service, session, "COMMIT", StatementKind.TransactionControl);

            foreach (var (text, target) in new[]
                     {
                         ($"ALTER TABLE {B} DROP COLUMN NAME", new ChangeTarget(null, B, "TABLE", ChangeOperation.Alter) { DataLoss = DataLossKind.DropColumns, DroppedColumns = ["NAME"] }),
                         ($"TRUNCATE TABLE {B}", new ChangeTarget(null, B, "TABLE", ChangeOperation.Truncate) { DataLoss = DataLossKind.Truncate }),
                     })
            {
                var stmt = new SqlStatement(text, default, StatementKind.Ddl) { ChangeTarget = target };
                var offer = await TiberoExecutionService.OfferBackupAsync(session, stmt, CancellationToken.None);
                Assert.NotNull(offer);
                output.WriteLine($"RB14 offer: name={offer.BackupName} select={offer.CreateSql[offer.CreateSql.IndexOf(" AS SELECT ", StringComparison.Ordinal)..offer.CreateSql.IndexOf(" FROM ", StringComparison.Ordinal)]}"); // 스키마(계정) 이름은 싣지 않는다
                interaction.Answer = new ConfirmAnswer(true, false, offer.CreateSql);
                var result = await service.ExecuteAsync(session, stmt, NoBinds, null, CancellationToken.None);
                output.WriteLine($"RB14 {target.DataLoss}: status={result.Status} {result.Error?.Code} logNamesBackup={result.Rollback?.LogLine?.Contains(offer.BackupName, StringComparison.Ordinal)}");
                Assert.Equal(ExecutionStatus.Succeeded, result.Status);
                var backupRows = await ScalarAsync(service, session, $"SELECT COUNT(*) FROM {offer.BackupName}");
                output.WriteLine($"RB14 backup rows={backupRows}");
                Assert.Equal("5", backupRows);
                await RunAsync(service, session, $"DROP TABLE {offer.BackupName} PURGE", StatementKind.Ddl);
            }

            Assert.Equal("0", await ScalarAsync(service, session, $"SELECT COUNT(*) FROM {B}"));
        }
        finally
        {
            await CleanupAsync(service, session);
        }
    }

    private static async Task<ExecutionResult> RunAsync(TiberoExecutionService service, TiberoSession session, string text, StatementKind kind, DmlShape? shape = null)
    {
        var stmt = new SqlStatement(text, default, kind) { Dml = shape, Verb = text.Split(' ')[0].ToUpperInvariant() };
        return await service.ExecuteAsync(session, stmt, NoBinds, null, CancellationToken.None);
    }

    private static async Task<string?> ScalarAsync(TiberoExecutionService service, TiberoSession session, string sql)
    {
        var r = await service.ExecuteAsync(session, new SqlStatement(sql, default, StatementKind.Query), NoBinds, null, CancellationToken.None);
        if (r.ResultSet is not { } rs)
        {
            return r.Error?.Code;
        }

        await using (rs)
        {
            var page = await rs.GetPageAsync(0, CancellationToken.None);
            return page.RowCount == 0 ? null : Convert.ToString(page.Columns[0][0], CultureInfo.InvariantCulture);
        }
    }

    private static async Task<string> SnapshotAsync(TiberoExecutionService service, TiberoSession session) =>
        await ScalarAsync(service, session,
            $"SELECT LISTAGG(ID || ':' || NAME || ':' || TO_CHAR(PRICE, 'TM9') || ':' || TO_CHAR(TS, 'YYYY-MM-DD HH24:MI:SS.FF6'), '|') WITHIN GROUP (ORDER BY ID) FROM {E}") ?? "";

    private static async Task CleanupAsync(TiberoExecutionService service, TiberoSession session)
    {
        await service.ExecuteAsync(session, new SqlStatement("ROLLBACK", default, StatementKind.TransactionControl), NoBinds, null, CancellationToken.None);
        foreach (var table in new[] { E, B })
        {
            await service.ExecuteAsync(session, new SqlStatement($"DROP TABLE {table} PURGE", default, StatementKind.Ddl), NoBinds, null, CancellationToken.None);
        }

        var leftovers = await service.ExecuteAsync(session,
            new SqlStatement("SELECT TABLE_NAME FROM USER_TABLES WHERE TABLE_NAME LIKE 'ORB\\_ORB\\_RB%' ESCAPE '\\'", default, StatementKind.Query), NoBinds, null,
            CancellationToken.None);
        if (leftovers.ResultSet is { } rs)
        {
            List<string> names = [];
            await using (rs)
            {
                var page = await rs.GetPageAsync(0, CancellationToken.None);
                names.AddRange(page.Columns[0].Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)!));
            }

            foreach (var name in names)
            {
                await service.ExecuteAsync(session, new SqlStatement($"DROP TABLE {name} PURGE", default, StatementKind.Ddl), NoBinds, null, CancellationToken.None);
            }
        }
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class ConfirmAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(new PolicyDecision(PolicyDecisionKind.Confirm));
    }
}
