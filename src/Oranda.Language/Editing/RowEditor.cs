using System.Runtime.CompilerServices;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Sessions;

[assembly: InternalsVisibleTo("Oranda.Language.Tests")]
[assembly: InternalsVisibleTo("Oranda.Oracle.IntegrationTests")]
[assembly: InternalsVisibleTo("Oranda.App.Tests")]

namespace Oranda.Language.Editing;

/// <summary>
/// 그리드 편집(GRID-04·05, PRD 6.5.1, TRD 4.4). 분석은 <see cref="RowEditPlanner"/>, DML 은 <see cref="RowDmlBuilder"/>.
/// 반영: 일괄 세이브포인트 → 행마다 (세이브포인트 → DML → 영향 행 수 ≠ 1 이거나 오류면 그 행만 롤백).
/// 모든 문장(세이브포인트 포함)은 <see cref="IExecutionService"/>(정책 관문)를 지난다. 소유: TSK-06-04.
/// </summary>
[OrandaService(typeof(IRowEditor))]
internal sealed class RowEditor(IExecutionService execution) : IRowEditor
{
    internal const string BatchSavepoint = "ORANDA_EDIT";
    internal const string RowSavepoint = "ORANDA_EDIT_ROW";

    private static readonly ExecutionOptions DmlOptions = new() { Source = HistorySource.GridEdit };
    private static readonly ExecutionOptions ControlOptions = new() { Source = HistorySource.GridEdit, SkipHistory = true };
    private static readonly Dictionary<string, BindValue> NoBinds = [];

    public EditCapability Analyze(SqlStatement query, IReadOnlyList<ResultColumn> cols, TableKeyInfo keys)
        => RowEditPlanner.Plan(query, cols, keys).Capability;

    public IReadOnlyList<SqlStatement> BuildDml(IReadOnlyList<RowChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return [.. BuildAll(changes).Select(d => d.Statement)];
    }

    public async Task<ApplyResult> ApplyAsync(ISession s, IReadOnlyList<RowChange> changes, bool commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(changes);

        // 서버에 아무것도 보내기 전에 모든 DML 을 만든다(키 없는 변경은 여기서 예외).
        var dml = BuildAll(changes);
        if (dml.Count == 0)
        {
            return new ApplyResult(0, false);
        }

        if (s.Profile.ReadOnly)
        {
            var err = new DbError("ORANDA-READONLY", $"읽기 전용 접속 '{s.Profile.Name}' 에서는 편집 내용을 반영할 수 없습니다.");
            return new ApplyResult(0, false) { Failures = [.. dml.Select(d => new RowApplyFailure(d.Change.RowIndex, 0, err))] };
        }

        var batch = await ControlAsync(s, "SAVEPOINT " + BatchSavepoint, ct);
        if (batch.Status != ExecutionStatus.Succeeded)
        {
            return Aborted(dml, 0, ErrorOf(batch));
        }

        var failures = new List<RowApplyFailure>();
        var applied = 0;
        for (var i = 0; i < dml.Count; i++)
        {
            var d = dml[i];
            var sp = await ControlAsync(s, "SAVEPOINT " + RowSavepoint, ct);
            if (sp.Status != ExecutionStatus.Succeeded)
            {
                await RollbackToAsync(s, BatchSavepoint);
                return Aborted(dml, i, ErrorOf(sp));
            }

            var r = await execution.ExecuteAsync(s, d.Statement, d.Binds, DmlOptions, ct);
            switch (r.Status)
            {
                case ExecutionStatus.Succeeded when r.RowsAffected == 1:
                    applied++;
                    break;
                case ExecutionStatus.Succeeded:
                    // 0 행(동시 삭제·낙관적 잠금 불일치) 또는 2 행 이상: 그 행만 되돌린다.
                    await RollbackToAsync(s, RowSavepoint);
                    failures.Add(new RowApplyFailure(d.Change.RowIndex, r.RowsAffected ?? -1,
                        new DbError("ORANDA-ROWCOUNT", AffectedMessage(r.RowsAffected ?? -1))));
                    break;
                case ExecutionStatus.Failed:
                    await RollbackToAsync(s, RowSavepoint);
                    failures.Add(new RowApplyFailure(d.Change.RowIndex, 0, ErrorOf(r)));
                    break;
                default:
                    // 정책 관문 거부·사용자 취소: 이번 반영 전체를 되돌린다.
                    await RollbackToAsync(s, BatchSavepoint);
                    return Aborted(dml, i, ErrorOf(r));
            }
        }

        var committed = false;
        if (commit && failures.Count == 0 && applied > 0)
        {
            await s.CommitAsync(ct);
            committed = true;
        }

        return new ApplyResult(applied, committed) { Failures = failures };
    }

    internal static string AffectedMessage(long rows) => rows switch
    {
        0 => "바뀐 행이 없습니다. 다른 세션이 행을 지웠거나 값을 바꿨습니다. 반영하지 않았습니다.",
        < 0 => "영향 행 수를 알 수 없어 반영하지 않았습니다.",
        _ => $"{rows} 행이 바뀌어 반영하지 않았습니다(1 행이어야 함).",
    };

    private static List<RowDml> BuildAll(IReadOnlyList<RowChange> changes)
    {
        var list = new List<RowDml>(changes.Count);
        foreach (var c in changes)
        {
            if (RowDmlBuilder.Build(c) is { } d)
            {
                list.Add(d);
            }
        }

        return list;
    }

    private Task<ExecutionResult> ControlAsync(ISession s, string text, CancellationToken ct)
    {
        var verb = text.StartsWith("SAVEPOINT", StringComparison.Ordinal) ? "SAVEPOINT" : "ROLLBACK";
        var stmt = new SqlStatement(text, default, StatementKind.TransactionControl) { Verb = verb };
        return execution.ExecuteAsync(s, stmt, NoBinds, ControlOptions, ct);
    }

    /// <summary>되돌리기는 취소와 무관하게 끝까지 한다(반쯤 반영된 상태를 남기지 않는다).</summary>
    private Task<ExecutionResult> RollbackToAsync(ISession s, string savepoint)
        => ControlAsync(s, "ROLLBACK TO SAVEPOINT " + savepoint, CancellationToken.None);

    private static DbError ErrorOf(ExecutionResult r) => r.Error ?? r.Status switch
    {
        ExecutionStatus.Denied => new DbError("ORANDA-DENIED", r.Policy?.Message ?? "정책 관문이 거부했습니다."),
        ExecutionStatus.Cancelled => new DbError("ORANDA-CANCELLED", "취소되어 반영하지 않았습니다."),
        _ => new DbError("ORANDA-EDIT", "반영하지 못했습니다."),
    };

    /// <summary>일괄 중단: <paramref name="at"/> 행은 원인 오류, 나머지는 「반영 안 함」.</summary>
    private static ApplyResult Aborted(List<RowDml> dml, int at, DbError cause)
    {
        var skipped = new DbError("ORANDA-ABORTED", "반영이 중단되어 되돌렸습니다.");
        return new ApplyResult(0, false)
        {
            Failures = [.. dml.Select((d, i) => new RowApplyFailure(d.Change.RowIndex, 0, i == at ? cause : skipped))],
        };
    }
}
