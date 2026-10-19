using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.Language.Editing;

/// <summary>그리드 편집(SELECT 분석·키 규칙·DML 생성·세이브포인트 반영). 소유: TSK-06-04. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IRowEditor))]
internal sealed class RowEditor : IRowEditor
{
    public EditCapability Analyze(SqlStatement query, IReadOnlyList<ResultColumn> cols, TableKeyInfo keys)
        => throw new NotImplementedException("TSK-06-04");

    public IReadOnlyList<SqlStatement> BuildDml(IReadOnlyList<RowChange> changes)
        => throw new NotImplementedException("TSK-06-04");

    public Task<ApplyResult> ApplyAsync(ISession s, IReadOnlyList<RowChange> changes, bool commit, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-04");
}
