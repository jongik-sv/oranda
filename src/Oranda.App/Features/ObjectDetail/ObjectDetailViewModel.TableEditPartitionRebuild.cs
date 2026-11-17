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

        var pick = PickScriptFile ?? ((suggested, token) => SqlEditor.SqlEditorHost.Current.Dialogs.PickSaveFileAsync(suggested, token));
        var path = await pick(edit.Original.Name + "_partition.sql", ct);
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

    /// <summary>
    /// 서버에서 열과 종속 객체 DDL(제약·인덱스·트리거·권한·주석)을 새로 읽어 스크립트를 만든다. 종속 DDL 을 못 읽으면 null 로 넘긴다
    /// (Tibero 는 그때 원본을 지우지 않는 스크립트를 낸다). 만들지 못하면 null 과 상태 줄 이유.
    /// </summary>
    internal async Task<string?> BuildPartitionRebuildScriptAsync(CancellationToken ct = default)
    {
        TableEdit?.FlushPendingInput?.Invoke();
        if (TableEdit is not { CanUsePartitionRebuild: true, PartitionRebuildClause: { } clause } edit || _session is not { } basis || Target is not { } target
            || ResolveMeta() is not { } meta)
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
        var usePrimaryKey = table.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey && c.IsEnabled);
        try
        {
            return scripts.RebuildPartitioned(target, ColumnOrdering.ByColumnId(table.Columns), enterprise, usePrimaryKey, dependentDdl, clause);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            StatusText = TableEditText.T("다시 만들기 스크립트를 만들지 못했다: ", "Could not build the rebuild script: ") + ex.Message;
            return null;
        }
    }
}
