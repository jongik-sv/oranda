using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Features.GridEdit;

/// <summary>
/// TSK-06-04 그리드 편집 — 편집 모드 상태와 화면 연결 지점(commands.md 「결과 편집」).
/// 결과 영역·편집기를 가진 쪽(TSK-04-02·06-02·06-03)이 <see cref="Active"/>·<see cref="ConfirmPreview"/>·<see cref="Rerun"/> 을 채운다.
/// </summary>
public static class GridEditCommands
{
    /// <summary>포커스가 있는 결과 그리드의 편집 모드. 편집 모드가 아니면 null.</summary>
    public static GridEditSession? Active { get; set; }

    /// <summary>
    /// DML 미리보기 대화상자(GRID-05). 글을 보여 주고 사용자가 확인하면 true.
    /// 비어 있으면 반영하지 않는다(미리보기 없이 반영하는 경로를 두지 않는다).
    /// </summary>
    public static Func<string, CancellationToken, Task<bool>>? ConfirmPreview { get; set; }

    /// <summary>
    /// 편집 모드로 다시 실행. 인자 null = 편집기 현재 문장(F9), 아니면 그 문장(「ROWID 추가 후 다시 실행」).
    /// 실행 화면(TSK-06-02)이 채운다.
    /// </summary>
    public static Func<SqlStatement?, CancellationToken, Task>? Rerun { get; set; }
}

/// <summary>편집 모드 명령 처리기. 편집 모드가 아니면(활성 세션 없음) 아무것도 하지 않는다.</summary>
[OrandaCommand("oranda.exec.runForEdit", CommandCategory.Sql, When = "sqlDocFocus",
    Enablement = "hasSession && !sessionBusy && !readOnlySession", Placements = ["menu:sql/runForEdit@40"])]
[OrandaCommand("oranda.grid.edit.addRow", CommandCategory.Result, When = "gridFocus", Enablement = "gridEditing",
    Placements = ["context:grid/edit@10"])]
[OrandaCommand("oranda.grid.edit.deleteRow", CommandCategory.Result, When = "gridFocus", Enablement = "gridEditing",
    Placements = ["context:grid/edit@20"])]
[OrandaCommand("oranda.grid.edit.previewDml", CommandCategory.Result, When = "gridFocus", Enablement = "gridDirty",
    Placements = ["context:grid/edit@30"])]
[OrandaCommand("oranda.grid.edit.applyAndCommit", CommandCategory.Result, When = "gridFocus", Enablement = "gridDirty && !sessionBusy",
    Placements = ["context:grid/edit@40"])]
[OrandaCommand("oranda.grid.edit.apply", CommandCategory.Result, When = "gridFocus", Enablement = "gridDirty && !sessionBusy",
    Placements = ["context:grid/edit@50"])]
[OrandaCommand("oranda.grid.edit.resetRow", CommandCategory.Result, When = "gridFocus", Enablement = "gridDirty",
    Placements = ["context:grid/edit@60"])]
[OrandaCommand("oranda.grid.edit.resetAll", CommandCategory.Result, When = "gridFocus", Enablement = "gridDirty",
    Placements = ["context:grid/edit@70"])]
[OrandaCommand("oranda.grid.edit.addRowidAndRerun", CommandCategory.Result, When = "gridFocus", Enablement = "gridNotEditable && singleTable")]
[OrandaCommand("oranda.grid.edit.chooseTargetTable", CommandCategory.Result, When = "gridFocus", Enablement = "gridEditing && joinResult")]
[OrandaCommand("oranda.grid.edit.toggleOptimisticLock", CommandCategory.Result, When = "gridFocus", Enablement = "gridEditing")]
public sealed class GridEditCommandHandler : ICommandHandler
{
    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CommandId == "oranda.exec.runForEdit")
        {
            if (GridEditCommands.Rerun is { } run)
            {
                await run(null, cancellationToken).ConfigureAwait(true);
            }

            return;
        }

        var s = GridEditCommands.Active;
        if (s is null)
        {
            return;
        }

        // 행 대상: Parameter 가 행 번호(long)면 그 행.
        var row = context.Parameter is long r ? r : (long?)null;
        switch (context.CommandId)
        {
            case "oranda.grid.edit.addRow":
                s.AddRow();
                break;
            case "oranda.grid.edit.deleteRow" when row is { } d:
                s.DeleteRow(d);
                break;
            case "oranda.grid.edit.resetRow" when row is { } rr:
                s.ResetRow(rr);
                break;
            case "oranda.grid.edit.resetAll":
                s.ResetAll();
                break;
            case "oranda.grid.edit.previewDml":
                if (GridEditCommands.ConfirmPreview is { } show)
                {
                    await show(s.BuildPreview(), cancellationToken).ConfigureAwait(true);
                }

                break;
            case "oranda.grid.edit.applyAndCommit":
                await ApplyAsync(s, commit: true, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.edit.apply":
                await ApplyAsync(s, commit: false, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.edit.toggleOptimisticLock":
                s.OptimisticLock = !s.OptimisticLock;
                break;
            case "oranda.grid.edit.chooseTargetTable" when context.Parameter is DbObjectRef target:
                await s.ChooseTargetAsync(target, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.grid.edit.addRowidAndRerun":
                if (s.Capability is { CanAddRowId: true, QueryWithRowId: { } q } && GridEditCommands.Rerun is { } rerun)
                {
                    await rerun(q, cancellationToken).ConfigureAwait(true);
                }

                break;
        }
    }

    /// <summary>미리보기 확인을 받은 뒤에만 반영한다(GRID-05).</summary>
    internal static async Task<bool> ApplyAsync(GridEditSession s, bool commit, CancellationToken ct)
    {
        if (!s.IsDirty || GridEditCommands.ConfirmPreview is not { } confirm)
        {
            return false;
        }

        if (!await confirm(s.BuildPreview(), ct).ConfigureAwait(true))
        {
            return false;
        }

        await s.ApplyAsync(commit, ct).ConfigureAwait(true);
        return true;
    }
}
