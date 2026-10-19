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
}

/// <summary>결과 영역 명령 처리기. 활성 그리드가 없으면 아무것도 하지 않는다.</summary>
[OrandaCommand("oranda.grid.copy", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@10"])]
[OrandaCommand("oranda.grid.copyWithHeaders", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@20"])]
[OrandaCommand("oranda.grid.copyAsCsv", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@30"])]
[OrandaCommand("oranda.grid.copyAsInsert", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@40"])]
[OrandaCommand("oranda.grid.copyAsInList", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection", Placements = ["context:grid/copy@50"])]
[OrandaCommand("oranda.grid.find", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
[OrandaCommand("oranda.grid.fetchAll", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasMoreRows")]
[OrandaCommand("oranda.grid.toggleTextMode", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
[OrandaCommand("oranda.grid.freezeColumns", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult", Placements = ["context:grid/columns@10"])]
[OrandaCommand("oranda.grid.hideColumn", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult", Placements = ["context:grid/columns@20"])]
[OrandaCommand("oranda.grid.showAllColumns", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult", Placements = ["context:grid/columns@30"])]
[OrandaCommand("oranda.grid.toggleRepeatHide", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
[OrandaCommand("oranda.grid.closeResultTab", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult")]
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
