using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Journal;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Backup;
using Oranda.Core.Rollback.Capture;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Rollback;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Execution;

// 데이터가 사라지는 DDL 앞 백업 테이블(CTAS) 권유(되돌리기 지점 설계 9장, RD-17, RB-14). Oracle OracleExecutionService.Backup.cs(RB-44)와 같은 규칙.
internal sealed partial class TiberoExecutionService
{
    /// <summary>백업 테이블을 만들지 못해 원래 DDL 을 보내지 않았을 때의 오류 코드(Oracle 과 같다).</summary>
    internal const string BackupFailedCode = "ORANDA-BACKUP";

    /// <summary>권유를 만들 때 읽는 사전(현재 스키마·PK 열)의 제한 시간. 넘으면 권하지 않는다(실행은 계속).</summary>
    internal static readonly TimeSpan BackupOfferTimeout = TimeSpan.FromSeconds(10);

    private static readonly Lazy<string> SessionSchemaSql = new(() => CatalogText("rollback.session_schema"));

    private static readonly Lazy<string> PrimaryKeyColumnsSql = new(() => CatalogText("rollback.primary_key_columns"));

    private static string CatalogText(string id) => TiberoEmbeddedCatalog.Load().Get(id, 6, Dbms.Tibero).Sql;

    /// <summary>
    /// 정책 관문 앞, 명령 잠금 밖: 데이터가 사라지는 DDL(<see cref="ChangeTarget.DataLoss"/>: TRUNCATE·DROP COLUMN·DROP … PURGE)이면 백업 테이블 제안을 만든다.
    /// 확인 창이 나오는 접속(시험·운영)에서만 권한다(RD-17: 개발 접속은 D-065 로 묻지 않는다). 읽기 전용 프로필은 정책이 DDL 을 거부하므로 권하지 않는다.
    /// 사전 읽기가 실패하면(권한·시간 초과) 권하지 않는다. 사용자 취소만 전파한다.
    /// </summary>
    internal static async Task<BackupOffer?> OfferBackupAsync(TiberoSession session, SqlStatement stmt, CancellationToken ct)
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
            var channel = TiberoRollbackChannel.Gated(session);
            var none = new Dictionary<string, BindValue>();
            var schema = await channel.QueryAsync(SessionSchemaSql.Value, none, 1, limit.Token).ConfigureAwait(false);
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
                var pk = await channel.QueryAsync(PrimaryKeyColumnsSql.Value, binds, 1000, limit.Token).ConfigureAwait(false);
                keys = [.. pk.Rows.Select(r => r[0] as string).OfType<string>()];
            }

            var context = new BackupContext(current, user, Dialect.MaxIdentifierBytes(session.Server), DateTime.Now) { KeyColumns = keys };
            return BackupPlanner.Offer(target, context);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // 권한·시간 초과·카탈로그 읽기 실패: 권하지 않고 실행은 계속한다(예외 대신 결과).
        }
    }

    /// <summary>
    /// 명령 잠금 안, 원래 DDL 앞: 사용자가 고른 백업 테이블을 만든다. CTAS 는 DDL 이라 열린 트랜잭션을 커밋한다(뒤이은 DDL 도 어차피 커밋한다, RB-13 실측).
    /// 그래서 앞에서 열린 묶음을 저장하고(7.4), 뒤에서 미커밋 표시를 다시 읽어 끝이면 원장에 알린다(7.3, <see cref="RollbackFlow.EndAfter"/>).
    /// 그동안 끊겨도 끝 알림은 여기서 한다(<see cref="TiberoSession.BeginEndScope"/>). 실패·취소면 원래 DDL 을 보내지 않는다.
    /// </summary>
    /// <returns>실패·취소면 그 결과, 성공이면 null</returns>
    private async Task<ExecutionResult?> CreateBackupAsync(TiberoSession session, SqlStatement stmt, string backupSql,
                                                           PolicyDecision? applied, CancellationToken ct)
    {
        var ctas = new SqlStatement(backupSql, default, StatementKind.Ddl) { Verb = "CREATE" };
        var pendingBefore = session.HasPendingTransaction;
        using var scope = session.BeginEndScope();
        ExecutionResult? failed = null;
        var created = false; // 서버가 CTAS 를 끝냈다(그 뒤 취소돼도 백업 테이블은 있다)
        try
        {
            await session.Rollback.BeforeEndAsync(session, ct).ConfigureAwait(false);
            var cmd = session.CreateCommand(backupSql);
            await using (cmd.ConfigureAwait(false))
            {
                using (cmd.CancelOnToken(ct))
                {
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    created = true;
                }
            }

            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            failed = new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = applied };
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex) || ex is InvalidOperationException or ObjectDisposedException)
        {
            var error = TiberoErrorClassifier.ToDbError(ex, backupSql);
            if (error is not null)
            {
                session.ReportFailure(ex);
            }

            failed = new ExecutionResult(stmt, ExecutionStatus.Failed)
            {
                Policy = applied,
                Error = new DbError(BackupFailedCode, $"백업 테이블을 만들지 못했습니다: {error?.Message ?? ex.Message}. 백업 없이 실행하려면 체크를 끄고 다시 실행하세요.")
                {
                    Dbms = Dbms.Tibero,
                    Number = error?.Number ?? 0,
                    Kind = error?.Kind ?? DbErrorKind.Other,
                },
            };
        }

        // 실패한 CTAS 도 앞에서 커밋했을 수 있다(의미 오류 DDL 은 커밋, RB-13 실측): 미커밋 표시를 다시 읽고(화면의 「미커밋」도 맞춘다) 끝이면 알린다.
        var pendingAfter = await SafeTrackAfterFailureAsync(session, ctas, StatementKind.Ddl).ConfigureAwait(false);
        if (await EndTransactionAsync(session, ctas, failed is null, pendingBefore, pendingAfter).ConfigureAwait(false))
        {
            scope.Reported(); // 끝이 아니면(미커밋이 남음) 알릴 것이 없다
        }

        if (created)
        {
            _events.Publish(new MetadataInvalidated(session.DbKey, []));
        }

        if (failed is not null)
        {
            // CTAS 뒤 취소: 원래 DDL 은 보내지 않았지만 백업 테이블이 생겼음을 로그에 남긴다.
            return failed with { TransactionPending = pendingAfter, Rollback = created ? BackupSummary(backupSql) : null };
        }

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
