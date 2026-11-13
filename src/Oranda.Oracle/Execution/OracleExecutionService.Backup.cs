using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Journal;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Backup;
using Oranda.Core.Rollback.Capture;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Rollback;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Execution;

// 데이터가 사라지는 DDL 앞 백업 테이블(CTAS) 권유(되돌리기 지점 설계 9장, RD-17, RB-44).
internal sealed partial class OracleExecutionService
{
    /// <summary>백업 테이블을 만들지 못해 원래 DDL 을 보내지 않았을 때의 오류 코드.</summary>
    internal const string BackupFailedCode = "ORANDA-BACKUP";

    /// <summary>권유를 만들 때 읽는 사전(현재 스키마·PK 열)의 제한 시간. 넘으면 권하지 않는다(실행은 계속).</summary>
    internal static readonly TimeSpan BackupOfferTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 정책 관문 앞, 명령 잠금 밖: 데이터가 사라지는 DDL 이면 백업 테이블 제안을 만든다. 확인 창이 나오는 접속(시험·운영)에서만 권한다(RD-17:
    /// 개발 접속은 D-065 로 묻지 않는다). 읽기 전용 프로필은 정책이 DDL 을 거부하므로 권하지 않는다. 설정 「백업 테이블 만들기 권하기」를 끈 경우는
    /// 확인 창이 체크를 보이지 않는다(설계 10.4). 사전 읽기가 실패하면 권하지 않는다. 사용자 취소만 전파한다.
    /// </summary>
    internal static async Task<BackupOffer?> OfferBackupAsync(OracleSession session, SqlStatement stmt, CancellationToken ct)
    {
        if (stmt.Kind != StatementKind.Ddl || stmt.ChangeTarget is not { DataLoss: not DataLossKind.None } target
            || session.Profile.Environment == ConnectionEnvironment.Development || session.Profile.ReadOnly)
        {
            return null;
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(BackupOfferTimeout);
            var channel = OracleRollbackChannel.Gated(session);
            var none = new Dictionary<string, BindValue>();
            var schema = await channel.QueryAsync(EmbeddedCatalog.Text("rollback.session_schema"), none, 1, limit.Token);
            if (schema.Rows is not [var row] || row[0] is not string current || row[1] is not string user)
            {
                return null;
            }

            IReadOnlyList<string> keys = [];
            if (BackupPlanner.NeedsKeyColumns(target))
            {
                var binds = new Dictionary<string, BindValue>
                {
                    ["owner"] = new(BindDataType.Varchar2, target.Owner ?? current),
                    ["name"] = new(BindDataType.Varchar2, target.Name),
                };
                var pk = await channel.QueryAsync(EmbeddedCatalog.Text("rollback.primary_key_columns"), binds, 1000, limit.Token);
                keys = [.. pk.Rows.Select(r => r[0] as string).OfType<string>()];
            }

            var context = new BackupContext(current, user, session.Rollback.Dialect.MaxIdentifierBytes(session.Server), DateTime.Now) { KeyColumns = keys };
            return BackupPlanner.Offer(target, context);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RollbackChannelException or OperationCanceledException)
        {
            return null; // 권한·시간 초과: 권하지 않고 실행은 계속한다.
        }
    }

    /// <summary>
    /// 명령 잠금 안, 원래 DDL 앞: 사용자가 고른 백업 테이블을 만든다. CTAS 는 DDL 이라 열린 트랜잭션을 커밋한다(뒤이은 DDL 도 어차피 커밋한다).
    /// 그래서 앞에서 열린 묶음을 저장하고(7.4), 뒤에서 미커밋 표시를 다시 읽어 끝이면 원장에 알린다(7.3, <see cref="RollbackFlow.EndAfter"/>).
    /// 그동안 끊겨도 끝 알림은 여기서 한다(<see cref="OracleSession.BeginEndScope"/>). 실패·취소면 원래 DDL 을 보내지 않는다.
    /// </summary>
    /// <returns>실패·취소면 그 결과, 성공이면 null</returns>
    private async Task<ExecutionResult?> CreateBackupAsync(OracleSession session, SqlStatement stmt, string backupSql,
                                                           PolicyDecision? applied, CancellationToken ct)
    {
        var ctas = new SqlStatement(backupSql, default, StatementKind.Ddl) { Verb = "CREATE" };
        var pendingBefore = session.HasPendingTransaction;
        using var scope = session.BeginEndScope();
        ExecutionResult? failed = null;
        try
        {
            await session.Rollback.BeforeEndAsync(session, ct);
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = backupSql;
            using (cmd.CancelOnToken(ct))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            failed = new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = applied };
        }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException or ObjectDisposedException)
        {
            var reason = ex is OracleException oe ? OracleErrorClassifier.ToDbError(oe).Message : ex.Message;
            if (ex is OracleException broken)
            {
                session.ReportFailure(broken);
            }

            failed = new ExecutionResult(stmt, ExecutionStatus.Failed)
            {
                Policy = applied,
                Error = new DbError(BackupFailedCode, $"백업 테이블을 만들지 못했습니다: {reason}. 백업 없이 실행하려면 체크를 끄고 다시 실행하세요.")
                {
                    Number = ex is OracleException n ? n.Number : 0,
                    Kind = ex is OracleException k ? OracleErrorClassifier.KindOf(k) : DbErrorKind.Other,
                },
            };
        }

        // 실패한 CTAS 도 앞에서 커밋했을 수 있다: 미커밋 표시를 다시 읽고(화면의 「미커밋」도 맞춘다) 끝이면 알린다.
        var pendingAfter = await SafeTrackAfterFailureAsync(session, ctas, StatementKind.Ddl);
        var hasOpenGroup = session.Rollback.Ledger.Open(session.Id) is not null;
        if (RollbackFlow.EndAfter(session.Id, ctas, failed is null, pendingBefore, pendingAfter, hasOpenGroup) is { } ended)
        {
            await session.Rollback.EndAsync(session, ended.Kind, ended.Source, ended.Savepoint);
        }

        scope.Reported(); // 끝이 아니면(미커밋이 남음) 알릴 것이 없다
        if (failed is not null)
        {
            return failed with { TransactionPending = pendingAfter };
        }

        _events.Publish(new MetadataInvalidated(session.DbKey, []));
        return null;
    }

    /// <summary>
    /// 확인 창이 돌려준 CTAS 글이 이 실행의 제안(<paramref name="offer"/>)에서 나온 것인가: 같은 원본 SELECT 이고 이름만 다를 수 있다.
    /// 정책 관문을 지나지 않는 글이므로 다른 글은 보내지 않는다.
    /// </summary>
    internal static bool IsOfferedBackup(BackupOffer? offer, string? backupSql) =>
        offer is not null && backupSql is not null && BackupPlanner.IsSameBackup(offer, backupSql);

    /// <summary>백업 테이블을 만든 실행의 로그 한 줄(<see cref="ExecutionResult.Rollback"/>).</summary>
    private static RollbackStatementSummary BackupSummary(string backupSql) =>
        RollbackStatementSummary.None with { LogLine = $"백업 테이블 {BackupPlanner.BackupNameOf(backupSql) ?? "(이름 모름)"} 을 만든 뒤 실행했습니다." };
}
