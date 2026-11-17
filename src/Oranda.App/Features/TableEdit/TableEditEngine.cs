using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 화면이 쓰는 Core 테이블 편집 로직(편집 모델 만들기·차이·문장·검사 계획·검사 실행)의 한 자리. 기본은 <see cref="CoreTableEditEngine"/>(Core 정적 함수),
/// 시험은 가짜를 넣어 화면만 본다(Core 몸 T1b 와 따로 시험하려는 것). 같은 원본·편집이면 같은 결과를 내야 한다(미리보기 = 실행, 설계 4장).
/// </summary>
public interface ITableEditEngine
{
    /// <summary>기존 테이블의 원본 모델(<see cref="TableDraftFactory.FromTable"/>).</summary>
    TableDraft FromTable(TableInfo table, ITableEditDialect dialect);

    /// <summary>새 테이블의 빈 원본(<see cref="TableDraftFactory.Empty"/>).</summary>
    TableDraft Empty(string owner);

    /// <summary>원본과 편집의 차이(<see cref="TableDraftDiff.Compute"/>).</summary>
    TableDraftChanges Diff(TableDraft original, TableDraft edited, ITableEditDialect dialect);

    /// <summary>문장 목록(<see cref="TableEditScriptBuilder.Build"/>).</summary>
    TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect);

    /// <summary>검사 계획(<see cref="TableEditCheckPlanner.Plan"/>).</summary>
    TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options);

    /// <summary>검사 실행(<see cref="TableEditCheckRunner.RunAsync"/>).</summary>
    Task<IReadOnlyList<CheckFinding>> RunChecksAsync(
        TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, ISession session, TableEditCheckOptions options, CancellationToken ct);
}

/// <summary><see cref="ITableEditEngine"/> 기본 구현: Core <c>Oranda.Core.Schema.TableEdit</c> 정적 함수를 그대로 부른다.</summary>
public sealed class CoreTableEditEngine : ITableEditEngine
{
    public static CoreTableEditEngine Instance { get; } = new();

    public TableDraft FromTable(TableInfo table, ITableEditDialect dialect) => TableDraftFactory.FromTable(table, dialect);

    public TableDraft Empty(string owner) => TableDraftFactory.Empty(owner);

    public TableDraftChanges Diff(TableDraft original, TableDraft edited, ITableEditDialect dialect) => TableDraftDiff.Compute(original, edited, dialect);

    public TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect) => TableEditScriptBuilder.Build(original, edited, dialect);

    public TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options) =>
        TableEditCheckPlanner.Plan(original, edited, dialect, options);

    public Task<IReadOnlyList<CheckFinding>> RunChecksAsync(
        TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, ISession session, TableEditCheckOptions options, CancellationToken ct) =>
        TableEditCheckRunner.RunAsync(plan, table, checks, session, options, ct);
}
