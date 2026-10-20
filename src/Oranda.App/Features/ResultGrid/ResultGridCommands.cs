using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// TSK-06-03 가상 결과 그리드 — 활성 그리드 추적과 명령 처리기(commands.md 「결과」). 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 셸이 자동 수집한다.
/// </summary>
public static class ResultGridCommands
{
    /// <summary>포커스가 있는 결과 그리드. 그리드 컨트롤이 포커스를 받을 때 채운다.</summary>
    public static ResultGridModel? Active { get; set; }

    /// <summary>마지막 「행 복사」의 앱 안 사본(그리드 사이에 함께 쓴다). 「행 붙여넣기」가 읽는다.</summary>
    public static GridRowsCopy? CopiedRows { get; set; }

    /// <summary>
    /// 「전체 행 수 세기」(<c>oranda.grid.countRows</c>): 결과를 만든 질의를 <c>SELECT COUNT(*) FROM (…)</c> 로 감싸 같은 바인드로 실행하고
    /// 행 수를 돌려준다(못 읽으면 null). 실패는 이유를 담은 예외, 취소는 <see cref="OperationCanceledException"/>.
    /// 실행 화면(<c>ExecutionCommands.Install</c>)이 채운다. 비어 있으면 셀 수 없다고 안내한다.
    /// </summary>
    public static Func<SqlStatement, CancellationToken, Task<long?>>? CountRows { get; set; }

    /// <summary>
    /// 결과 거르기(GRID-10): 요청의 질의를 <c>SELECT * FROM (…) WHERE … ORDER BY …</c> 로 감싸 같은 바인드로 실행하고 새 결과를 돌려준다
    /// (결과 집합의 수명은 받는 쪽이 가진다). 실패는 이유를 담은 예외, 취소는 <see cref="OperationCanceledException"/>.
    /// 실행 화면(<c>ExecutionCommands.Install</c>)이 채운다. 비어 있으면 거를 수 없다고 안내한다.
    /// </summary>
    public static Func<ResultFilterRequest, CancellationToken, Task<IResultSet>>? Filter { get; set; }

    /// <summary>거른 결과의 전체 행 수(「전체 행 수 세기」를 거른 결과에서 할 때). 셀 값 바인드를 함께 넘긴다.</summary>
    public static Func<ResultFilterRequest, CancellationToken, Task<long?>>? CountFiltered { get; set; }

    /// <summary>
    /// 외래키 따라가기(GRID-11, <c>oranda.grid.followForeignKey</c>·<c>oranda.grid.showReferencingRows</c>): 요청한 행의 키 값을 바인드로 묶어
    /// 부모 또는 자식 테이블을 조회하고 새 결과 탭에 열 결과를 돌려준다(결과 집합의 수명은 받는 쪽이 가진다). 외래키 고르기를 취소하면 null.
    /// 실패는 이유를 담은 예외, 취소는 <see cref="OperationCanceledException"/>. 실행 화면(<c>ExecutionCommands.Install</c>)이 채운다.
    /// </summary>
    public static Func<RelatedRowsRequest, CancellationToken, Task<RelatedRowsResult?>>? OpenRelatedRows { get; set; }
}

/// <summary>결과 영역 명령 처리기. 활성 그리드가 없으면 아무것도 하지 않는다.</summary>
[OrandaCommand("oranda.grid.copy", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@10"])]
[OrandaCommand("oranda.grid.copyWithHeaders", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@20"])]
[OrandaCommand("oranda.grid.copyRows", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@22"])]
[OrandaCommand("oranda.grid.copyRowsWithHeaders", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@24"])]
[OrandaCommand("oranda.grid.copyAsCsv", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@30"])]
[OrandaCommand("oranda.grid.copyAsInsert", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@40"])]
[OrandaCommand("oranda.grid.copyAsInList", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@50"])]
[OrandaCommand("oranda.grid.find", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
[OrandaCommand("oranda.grid.fetchAll", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasMoreRows", Placements = ["context:grid/rows@20"])]
[OrandaCommand("oranda.grid.toggleTextMode", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
[OrandaCommand("oranda.grid.freezeColumns", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult", Placements = ["context:grid/columns@10"])]
[OrandaCommand("oranda.grid.hideColumn", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult", Placements = ["context:grid/columns@20"])]
[OrandaCommand("oranda.grid.showAllColumns", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult", Placements = ["context:grid/columns@30"])]
[OrandaCommand("oranda.grid.toggleRepeatHide", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
[OrandaCommand("oranda.grid.closeResultTab", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
[OrandaCommand("oranda.grid.countRows", CommandCategory.Result, When = "gridFocus", Enablement = "gridQueryResult && hasSession && !sessionBusy",
    Placements = ["context:grid/rows@10"])]
[OrandaCommand("oranda.grid.toggleFilterBar", CommandCategory.Result, When = "gridFocus", Enablement = "gridQueryResult",
    Icon = "filter", Placements = ["context:grid/filter@10"])]
[OrandaCommand("oranda.grid.filterByValue", CommandCategory.Result, When = "gridFocus",
    Enablement = "gridQueryResult && gridHasSelection && hasSession && !sessionBusy", Placements = ["context:grid/filter@20"])]
[OrandaCommand("oranda.grid.filterExcludeValue", CommandCategory.Result, When = "gridFocus",
    Enablement = "gridQueryResult && gridHasSelection && hasSession && !sessionBusy", Placements = ["context:grid/filter@30"])]
[OrandaCommand("oranda.grid.filterNullRows", CommandCategory.Result, When = "gridFocus",
    Enablement = "gridQueryResult && gridHasSelection && hasSession && !sessionBusy", Placements = ["context:grid/filter@40"])]
[OrandaCommand("oranda.grid.pinResultTab", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult",
    Icon = "pin", Placements = ["context:grid/tab@10"])]
[OrandaCommand("oranda.grid.followForeignKey", CommandCategory.Result, When = "gridFocus",
    Enablement = "gridQueryResult && gridHasSelection && hasSession && !sessionBusy", Placements = ["context:grid/navigate@10"])]
[OrandaCommand("oranda.grid.showReferencingRows", CommandCategory.Result, When = "gridFocus",
    Enablement = "gridQueryResult && gridHasSelection && hasSession && !sessionBusy", Placements = ["context:grid/navigate@20"])]
public sealed class ResultGridCommandHandler : ICommandHandler
{
    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var g = ResultGridCommands.Active;
        if (g is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.grid.copy":
                await g.CopyAsync(CopyFormat.Tsv, false, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.copyWithHeaders":
                await g.CopyAsync(CopyFormat.Tsv, true, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.copyRows":
                await g.CopyRowsAsync(includeHeaders: false, cancellationToken).ConfigureAwait(true); // 고른 칸이 걸친 행 전체 + 앱 안 사본
                break;
            case "oranda.grid.copyRowsWithHeaders":
                await g.CopyRowsAsync(includeHeaders: true, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.copyAsCsv":
                await g.CopyAsync(CopyFormat.Csv, true, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.copyAsInsert":
                await g.CopyAsync(CopyFormat.Insert, false, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.copyAsInList":
                await g.CopyAsync(CopyFormat.InList, false, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.find":
                g.RequestFind();
                break;
            case "oranda.grid.fetchAll":
                await g.FetchAllAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.toggleTextMode":
                g.ToggleTextMode();
                break;
            case "oranda.grid.freezeColumns":
                g.FreezeUpToActive();
                break;
            case "oranda.grid.hideColumn":
                HideSelectedColumns(g);
                break;
            case "oranda.grid.showAllColumns":
                g.ShowAllColumns();
                break;
            case "oranda.grid.toggleRepeatHide":
                g.ToggleRepeatHide();
                break;
            case "oranda.grid.closeResultTab":
                g.RequestClose();
                break;
            case "oranda.grid.countRows":
                g.RequestCountRows(); // 결과 영역이 문장을 알고 세는 동안 상태 줄에 보인다(실패도 상태 줄에, 명령 밖으로 던지지 않는다)
                break;
            case "oranda.grid.toggleFilterBar":
                g.RequestFilterBar();
                break;
            case "oranda.grid.filterByValue":
                g.RequestCellFilter(CellFilterKind.Equal); // 결과 영역이 활성 칸 값을 바인드로 묶어 다시 실행한다(실패는 상태 줄에)
                break;
            case "oranda.grid.filterExcludeValue":
                g.RequestCellFilter(CellFilterKind.NotEqual);
                break;
            case "oranda.grid.filterNullRows":
                g.RequestCellFilter(CellFilterKind.IsNull);
                break;
            case "oranda.grid.pinResultTab":
                g.RequestPin(); // 결과 탭 묶음이 그 탭을 고정하거나 푼다
                break;
            case "oranda.grid.followForeignKey":
                g.RequestRelatedRows(RelatedRowsKind.Parent); // 결과 영역이 활성 행의 외래키 값을 바인드로 묶어 부모 행을 새 결과 탭에 연다(실패는 상태 줄에)
                break;
            case "oranda.grid.showReferencingRows":
                g.RequestRelatedRows(RelatedRowsKind.Children); // 이 행의 키를 참조하는 자식 행을 새 결과 탭에(외래키가 여럿이면 고른다)
                break;
            default:
                break;
        }
    }

    private static void HideSelectedColumns(ResultGridModel g)
    {
        if (!g.HasSelection)
        {
            return;
        }

        var (from, to) = g.SelectionRect();
        foreach (var ord in g.VisibleColumns.Skip(from.Col).Take(to.Col - from.Col + 1).ToList())
        {
            g.HideColumn(ord);
        }
    }
}
