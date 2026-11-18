using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 일반 → 파티션 테이블 바꾸기의 「다시 만들기 스크립트」(테이블 편집기 설계 TE-41): 한 문장 <c>MODIFY PARTITION BY … ONLINE</c> 이 없는 서버
/// (Oracle 12.2 미만·Tibero)에서 파티션 페이지의 단추가 부른다. 열 순서 바꾸기의 다시 만들기(<see cref="IColumnReorderScripts"/>)에 <c>PARTITION BY</c> 절을 붙인 글을
/// 새 SQL 탭에 열거나 파일로 저장한다. 실행하지 않는다(행이 많으면 오래 걸리고 쓰기를 멈춰야 해서 사람이 검토한 뒤 실행).
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    /// <summary>시험용: 다시 만들기 스크립트 만들기(기본 = 세션 DBMS 의 <see cref="IColumnReorderScripts"/>).</summary>
    internal IColumnReorderScripts? RebuildScripts { get; set; }

    /// <summary>편집 세션에 단추 동작을 넣는다(편집을 켤 때).</summary>
    private void WirePartitionRebuild(TableEditSession edit)
    {
        edit.OpenPartitionRebuild = () => OpenPartitionRebuildScriptAsync();
        edit.SavePartitionRebuild = () => SavePartitionRebuildScriptAsync();
        edit.SaveMonthAddScript = () => SaveMonthAddScriptAsync();
    }

    /// <summary>
    /// 「월 추가 스크립트 저장…」(파티션 추천 설계 PA-5): INTERVAL 을 못 쓰는 범위 키의 다음 달 SPLIT 스크립트를 <c>.sql</c> 로(다시 만들기 스크립트와 같은 저장 방식).
    /// 저장한 경로. 실행하지 않는다.
    /// </summary>
    public async Task<string?> SaveMonthAddScriptAsync(CancellationToken ct = default)
    {
        if (TableEdit is not { } edit || edit.BuildMonthAddScript() is not { } script)
        {
            return null;
        }

        return await SaveScriptAsync(edit.Original.Name + "_add_months.sql", script, ct);
    }

    /// <summary>「다시 만들기 스크립트 열기」: 새 SQL 탭에(이 접속, 실행하지 않음). 열었으면 true.</summary>
    public async Task<bool> OpenPartitionRebuildScriptAsync(CancellationToken ct = default)
    {
        if (await BuildPartitionRebuildScriptAsync(ct) is not { } script)
        {
            return false;
        }

        var ok = (DdlEditorOpener ?? OpenInNewEditor)(script, _session?.Profile.Id);
        StatusText = ok
            ? TableEditText.T("다시 만들기 스크립트를 새 SQL 탭에 열었다(검토한 뒤 직접 실행한다)", "Opened the rebuild script in a new SQL tab (review it, then run it yourself)")
            : TableEditText.T("편집기를 열지 못했다", "Could not open the editor");
        return ok;
    }

    /// <summary>「스크립트 저장…」: 다시 만들기 스크립트를 <c>.sql</c> 로. 저장한 경로.</summary>
    public async Task<string?> SavePartitionRebuildScriptAsync(CancellationToken ct = default)
    {
        if (TableEdit is not { } edit || await BuildPartitionRebuildScriptAsync(ct) is not { } script)
        {
            return null;
        }

        return await SaveScriptAsync(edit.Original.Name + "_partition.sql", script, ct);
    }

    /// <summary>스크립트 글을 고른 파일에 쓴다(저장 대화 → UTF-8). 저장한 경로, 취소·실패 = null(상태 줄에 이유).</summary>
    private async Task<string?> SaveScriptAsync(string suggested, string script, CancellationToken ct)
    {
        var pick = PickScriptFile ?? ((name, token) => SqlEditor.SqlEditorHost.Current.Dialogs.PickSaveFileAsync(name, token));
        var path = await pick(suggested, ct);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(path, script, RequestScriptWriter.Encoding, ct);
            StatusText = TableEditText.T("스크립트를 저장했다: ", "Saved the script: ") + path;
            return path;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText = TableEditText.T("스크립트를 저장하지 못했다: ", "Could not save the script: ") + ex.Message;
            return null;
        }
    }

    /// <summary>다시 만들기 스크립트에 넣는 서버 값(열·종속 DDL·에디션). 미리보기는 편집 세션마다 한 번 읽어 둔다.</summary>
    private sealed record RebuildInputs(TableEditSession Edit, IColumnReorderScripts Scripts, DbObjectRef Target, TableInfo Table, string? DependentDdl, bool Enterprise);

    private RebuildInputs? _rebuildPreviewInputs;
    private string? _rebuildPreviewPending;

    /// <summary>시험용: 마지막으로 시작한 미리보기 스크립트 만들기.</summary>
    internal Task PartitionRebuildPreviewTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// 서버에서 열과 종속 객체 DDL(제약·인덱스·트리거·권한·주석)을 새로 읽어 스크립트를 만든다. 종속 DDL 을 못 읽으면 null 로 넘긴다
    /// (Tibero 는 그때 원본을 지우지 않는 스크립트를 낸다). 만들지 못하면 null 과 상태 줄 이유.
    /// </summary>
    internal async Task<string?> BuildPartitionRebuildScriptAsync(CancellationToken ct = default)
    {
        TableEdit?.FlushPendingInput?.Invoke();
        return await ReadRebuildInputsAsync(ct) is { } inputs ? ComposeRebuildScript(inputs) : null;
    }

    /// <summary>
    /// 편집이 다시 만들기 상태로 바뀌었거나 그 절이 바뀌면 DDL 미리보기에 보일 스크립트를 만든다(<see cref="OnTableEditRecomputed"/> 에서).
    /// 서버 값은 편집 세션마다 한 번 읽고, 절·LOCAL 인덱스만 바뀌면 다시 읽지 않는다. 입력 칸을 비우지 않는다(편집 중 다시 계산이 돌지 않게).
    /// </summary>
    private void RefreshPartitionRebuildPreview()
    {
        if (TableEdit is not { } edit || edit.PartitionRebuildPreviewKey is not { } key || edit.PartitionRebuildPreview is not null
            || key == _rebuildPreviewPending)
        {
            return;
        }

        if (_rebuildPreviewInputs is { } cached && ReferenceEquals(cached.Edit, edit))
        {
            if (ComposeRebuildScript(cached) is { } script)
            {
                edit.SetPartitionRebuildPreview(key, script);
            }

            return;
        }

        _rebuildPreviewPending = key;
        PartitionRebuildPreviewTask = BuildPreviewAsync(edit, key);
    }

    private async Task BuildPreviewAsync(TableEditSession edit, string key)
    {
        try
        {
            if (await ReadRebuildInputsAsync(CancellationToken.None) is { } inputs && ReferenceEquals(TableEdit, edit))
            {
                _rebuildPreviewInputs = inputs;
                if (edit.PartitionRebuildPreviewKey is { } now && ComposeRebuildScript(inputs) is { } script)
                {
                    edit.SetPartitionRebuildPreview(now, script);
                }
            }
        }
        finally
        {
            if (_rebuildPreviewPending == key)
            {
                _rebuildPreviewPending = null;
            }
        }
    }

    /// <summary>서버 값을 읽는다. 못 읽으면 null 과 상태 줄 이유.</summary>
    private async Task<RebuildInputs?> ReadRebuildInputsAsync(CancellationToken ct)
    {
        if (TableEdit is not { IsPartitionRebuildReady: true } edit || _session is not { } basis || Target is not { } target || ResolveMeta() is not { } meta)
        {
            return null;
        }

        var session = ConnectionManager.SessionRouter.ForTool(_services, basis) ?? basis;
        if ((RebuildScripts ?? DbmsServices.For<IColumnReorderScripts>(_services, session)) is not { } scripts)
        {
            StatusText = TableEditText.T($"이 DBMS({session.Profile.Dbms}) 용 다시 만들기 스크립트 구현이 없다", $"No rebuild script for this DBMS ({session.Profile.Dbms})");
            return null;
        }

        TableInfo table;
        try
        {
            table = await ReadTableFreshAsync(meta, session, target, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            StatusText = TableEditText.T("테이블을 읽지 못했다: ", "Could not read the table: ") + ex.Message;
            return null;
        }

        string? dependentDdl;
        try
        {
            var plain = await meta.GetDdlAsync(session, target, new DdlOptions { SqlTerminator = true }, ct);
            var full = await meta.GetDdlAsync(session, target, new DdlOptions { SqlTerminator = true, IncludeDependentObjects = true }, ct);
            dependentDdl = scripts.DependentPart(plain, full);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            dependentDdl = null; // 읽지 못함(빈 글 = 읽었는데 없음과 다르다)
        }

        var enterprise = session.Server?.Edition?.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) == true;
        return new RebuildInputs(edit, scripts, target, table, dependentDdl, enterprise);
    }

    /// <summary>지금 편집의 절·LOCAL 인덱스로 스크립트 글을 만든다. 다시 만들기 상태가 아니거나 만들지 못하면 null(못 만든 이유는 상태 줄).</summary>
    private string? ComposeRebuildScript(RebuildInputs inputs)
    {
        if (inputs.Edit is not { IsPartitionRebuildReady: true, PartitionRebuildClause: { } clause } edit)
        {
            return null;
        }

        // LOCAL 인덱스(PA-12): 종속 DDL 의 그 인덱스 CREATE·제약에 LOCAL 을 붙인다. 온라인 재정의(DBMS_REDEFINITION)는 인덱스를 원본 모양 그대로
        // 복사하므로(COPY_TABLE_DEPENDENTS) LOCAL 을 고르면 CTAS + RENAME 으로 만든다.
        var dependentDdl = inputs.DependentDdl;
        var local = edit.PartitionDraft?.LocalIndexes ?? [];
        if (local.Count > 0 && dependentDdl is not null)
        {
            dependentDdl = ColumnReorderBuilder.WithLocalIndexes(dependentDdl, local);
        }

        var enterprise = local.Count == 0 && inputs.Enterprise;
        var usePrimaryKey = inputs.Table.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey && c.IsEnabled);
        try
        {
            return inputs.Scripts.RebuildPartitioned(inputs.Target, ColumnOrdering.ByColumnId(inputs.Table.Columns), enterprise, usePrimaryKey, dependentDdl, clause);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            StatusText = TableEditText.T("다시 만들기 스크립트를 만들지 못했다: ", "Could not build the rebuild script: ") + ex.Message;
            return null;
        }
    }
}
