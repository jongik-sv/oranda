using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Features.LobRowView;

/// <summary>LOB 를 저장할 곳: 대상 테이블·열과 행 키(PK 열 또는 <c>ROWID</c>). 그리드 편집(TSK-06-04)의 키 규칙을 그대로 쓴다.</summary>
public sealed record LobSaveTarget(DbObjectRef Table, string Column, IReadOnlyDictionary<string, object?> Key);

/// <summary>저장 결과. <see cref="Message"/> 는 사용자에게 보일 글.</summary>
public sealed record LobWriteResult(bool Success, string Message, bool Committed = false);

/// <summary>
/// LOB 열 하나 갱신(<c>UPDATE t SET c = :v1 WHERE 키</c>). DML 은 <see cref="RowDmlBuilder"/>(값은 바인드, 키 없으면 만들지 않음)로 만들고
/// 모든 문장은 <see cref="IExecutionService"/>(정책 관문)를 지난다. 영향 행 수가 1 이 아니면 그 문장만 롤백한다.
/// </summary>
public sealed class LobWriter(IExecutionService execution)
{
    internal const string Savepoint = "ORANDA_LOB";

    private static readonly ExecutionOptions DmlOptions = new() { Source = HistorySource.GridEdit };
    private static readonly ExecutionOptions ControlOptions = new() { Source = HistorySource.GridEdit, SkipHistory = true };
    private static readonly Dictionary<string, BindValue> NoBinds = [];

    /// <param name="value">CLOB 이면 <c>BindDataType.Clob</c> + string, BLOB 이면 <c>BindDataType.Blob</c> + byte[]</param>
    public async Task<LobWriteResult> WriteAsync(ISession session, LobSaveTarget target, BindValue value, bool commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(value);

        if (session.Profile.ReadOnly)
        {
            return new LobWriteResult(false, $"읽기 전용 접속 '{session.Profile.Name}' 에서는 LOB 를 저장할 수 없습니다.");
        }

        // 값 자리(:v1)는 VARCHAR2 로 만들어지므로 LOB 바인드로 바꿔 끼운다.
        var change = new RowChange(RowChangeKind.Update, 0, target.Table)
        {
            Key = target.Key,
            NewValues = new Dictionary<string, object?> { [target.Column] = string.Empty },
        };
        RowDml? dml;
        try
        {
            dml = RowDmlBuilder.Build(change);
        }
        catch (ArgumentException ex)
        {
            return new LobWriteResult(false, ex.Message);
        }

        if (dml is null)
        {
            return new LobWriteResult(false, "저장할 내용이 없습니다.");
        }

        var binds = new Dictionary<string, BindValue>(dml.Binds, StringComparer.OrdinalIgnoreCase) { ["v1"] = value };

        var sp = await ControlAsync(session, "SAVEPOINT " + Savepoint, ct).ConfigureAwait(false);
        if (sp.Status != ExecutionStatus.Succeeded)
        {
            return new LobWriteResult(false, MessageOf(sp));
        }

        var r = await execution.ExecuteAsync(session, dml.Statement, binds, DmlOptions, ct).ConfigureAwait(false);
        if (r.Status == ExecutionStatus.Succeeded && r.RowsAffected == 1)
        {
            if (commit)
            {
                await session.CommitAsync(ct).ConfigureAwait(false);
            }

            return new LobWriteResult(true, commit ? "LOB 를 저장하고 커밋했습니다." : "LOB 를 저장했습니다(커밋 안 함).", commit);
        }

        await ControlAsync(session, "ROLLBACK TO SAVEPOINT " + Savepoint, CancellationToken.None).ConfigureAwait(false);
        return r.Status == ExecutionStatus.Succeeded
            ? new LobWriteResult(false, AffectedMessage(r.RowsAffected ?? -1))
            : new LobWriteResult(false, MessageOf(r));
    }

    private static string AffectedMessage(long rows) => rows == 0
        ? "대상 행을 찾지 못했습니다(다른 곳에서 지워졌거나 키가 바뀜). 저장하지 않았습니다."
        : $"{rows} 행이 걸려 저장하지 않았습니다(키가 유일하지 않음).";

    private Task<ExecutionResult> ControlAsync(ISession s, string text, CancellationToken ct)
    {
        var verb = text.StartsWith("SAVEPOINT", StringComparison.Ordinal) ? "SAVEPOINT" : "ROLLBACK";
        var stmt = new SqlStatement(text, default, StatementKind.TransactionControl) { Verb = verb };
        return execution.ExecuteAsync(s, stmt, NoBinds, ControlOptions, ct);
    }

    private static string MessageOf(ExecutionResult r) => r.Error?.Message ?? r.Status switch
    {
        ExecutionStatus.Denied => r.Policy?.Message ?? "정책 관문이 거부했습니다.",
        ExecutionStatus.Cancelled => "취소되어 저장하지 않았습니다.",
        _ => "저장하지 못했습니다.",
    };
}
