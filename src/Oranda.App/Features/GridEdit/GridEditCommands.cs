using Oranda.Core.Execution;
using Oranda.Core.Grid;
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
    /// DML 미리보기 대화상자(GRID-05). 글을 보여 주고 사용자가 확인하면 true. 둘째 인자 = 반영 뒤 바로 커밋하는 동작인지(창이 동작을 밝힌다).
    /// 비어 있으면 반영하지 않는다(미리보기 없이 반영하는 경로를 두지 않는다).
    /// </summary>
    public static Func<string, bool, CancellationToken, Task<bool>>? ConfirmPreview { get; set; }

    /// <summary>
    /// 반영 결과 안내(「n 행 반영(커밋 안 함)…」)를 눈에 띄는 곳(메시지 창)에 알린다. 반영 뒤 다시 읽으면 결과 창이 새로 만들어져
    /// 편집 세션의 <see cref="GridEditSession.Notice"/> 가 사라지므로 이 안내가 남는 자리다.
    /// </summary>
    public static Action<string>? Report { get; set; }

    /// <summary>
    /// 편집 모드로 다시 실행. 인자 null = 편집기 현재 문장(F9), 아니면 그 문장(「ROWID 추가 후 다시 실행」).
    /// 실행 화면(TSK-06-02)이 채운다.
    /// </summary>
    public static Func<SqlStatement?, CancellationToken, Task>? Rerun { get; set; }
}

/// <summary>
/// 편집 모드 명령 처리기. 편집 모드가 아니면(활성 세션 없음) 아무것도 하지 않는다.
/// 행 붙여넣기·행 복제는 읽기 전용 결과에서도 불러 「편집 모드로 다시 실행」 안내 막대를 띄운다(활성 조건이 <c>gridEditing</c> 이 아니다).
/// 두 명령의 대상은 Parameter 가 결과 그리드면 그 그리드, 아니면 활성(포커스) 그리드다.
/// </summary>
[OrandaCommand("oranda.exec.runForEdit", CommandCategory.Sql, When = "sqlDocFocus",
    Enablement = "(hasSession || tabConnectionMissing) && !sessionBusy && !readOnlySession", Placements = ["menu:sql/runForEdit@40"])]
[OrandaCommand("oranda.grid.edit.addRow", CommandCategory.Result, When = "gridFocus", Enablement = "gridEditing",
    Placements = ["context:grid/edit@10"])]
[OrandaCommand("oranda.grid.edit.duplicateRow", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/edit@12"])]
[OrandaCommand("oranda.grid.edit.pasteRows", CommandCategory.Result, When = "gridFocus", Enablement = "gridHasResult",
    Placements = ["context:grid/edit@14"])]
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

        if (context.CommandId is "oranda.grid.edit.pasteRows" or "oranda.grid.edit.duplicateRow")
        {
            if ((context.Parameter as ResultGrid.ResultGridModel ?? ResultGrid.ResultGridCommands.Active) is { } focused)
            {
                await (context.CommandId == "oranda.grid.edit.pasteRows"
                    ? GridRowTransfer.PasteRowsAsync(focused, cancellationToken)
                    : GridRowTransfer.DuplicateRowsAsync(focused, cancellationToken)).ConfigureAwait(true);
            }

            return;
        }

        var s = GridEditCommands.Active;
        if (s is null)
        {
            return;
        }

        // 행 대상: Parameter 가 행 번호(long)면 그 행, 없으면 지금 그리드에서 고른 행(메뉴·오른쪽 클릭에서 부를 때).
        var row = context.Parameter is long r ? r
            : ResultGrid.ResultGridCommands.Active is { HasSelection: true } grid && GridEditBinding.Of(grid) == s
                ? GridEditBinding.ToEditRow(grid, s, grid.Active.Row)
                : (long?)null;
        switch (context.CommandId)
        {
            case "oranda.grid.edit.addRow":
                s.AddRow();
                if (ResultGrid.ResultGridCommands.Active is { } shown && GridEditBinding.Of(shown) == s)
                {
                    shown.SelectCell(new ResultGrid.GridCell(shown.RowCount - 1, 0)); // 새 행으로 가서 바로 입력
                }

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
                // 미리보기 창의 「반영」은 커밋 없이 반영한다(취소면 아무것도 하지 않는다).
                if (GridEditCommands.ConfirmPreview is { } show && await show(s.BuildPreview(), false, cancellationToken).ConfigureAwait(true))
                {
                    await ApplyConfirmedAsync(s, commit: false, cancellationToken).ConfigureAwait(true);
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
                if (s.Capability is { CanAddRowId: true, QueryWithRowId: { } q } && (s.Rerun ?? GridEditCommands.Rerun) is { } rerun)
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

        if (!await confirm(s.BuildPreview(), commit, ct).ConfigureAwait(true))
        {
            return false;
        }

        await ApplyConfirmedAsync(s, commit, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>반영한 뒤 그 행을 찾는 키: 원래 키에 같은 열의 새 값을 덮는다.</summary>
    private static Dictionary<string, object?> KeyAfterApply(RowChange c)
    {
        var key = new Dictionary<string, object?>(c.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.Keys.ToList())
        {
            if (c.NewValues.TryGetValue(name, out var v))
            {
                key[name] = v;
            }
        }

        return key;
    }

    /// <summary>확인을 받은 변경을 반영한다.</summary>
    private static async Task ApplyConfirmedAsync(GridEditSession s, bool commit, CancellationToken ct)
    {
        var updatedChanges = s.BuildChanges().Where(c => c.Kind == RowChangeKind.Update).ToList();
        var updated = updatedChanges.Select(c => c.RowIndex).ToList();
        ApplyResult result;
        try
        {
            result = await s.ApplyAsync(commit, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SaveFeedback.ShowApplyFailed();
            throw;
        }

        // 저장 효과(TSK-13-18): 상태 표시줄 알림 + 반영된 행 잠깐 강조(다시 읽으면 새 그리드에서).
        SaveFeedback.ShowApplied(result.AppliedCount, result.Failures.Count, result.Committed);
        var savedRows = updated.Where(r => s.GetRowState(r) != GridEditRowState.Failed).ToList();
        var notice = s.Notice;
        if (notice is not null)
        {
            GridEditCommands.Report?.Invoke(notice);
        }

        // 모두 반영됐으면 같은 질의를 편집 모드로 다시 읽어 그리드가 반영된 값을 보인다(실패 행이 남으면 그대로 둔다).
        // 편집기 밖 결과(객체 상세 「데이터」 탭)는 그 결과가 정한 방법으로 다시 읽는다.
        if (!s.IsDirty && (s.Rerun ?? GridEditCommands.Rerun) is { } rerun)
        {
            await rerun(s.Query, ct).ConfigureAwait(true);

            // 새로 열린 결과 창의 요약 줄에도 안내를 남긴다(옛 세션의 안내는 창과 함께 사라졌다).
            if (notice is not null)
            {
                GridEditCommands.Active?.Announce(notice);
            }

            // 다시 읽은 결과는 정렬이 풀려 행 번호가 달라지므로, 반영한 행의 키(키 열을 고쳤으면 고친 값)로 새 그리드에서 찾는다.
            var savedKeys = updatedChanges.Where(c => savedRows.Contains(c.RowIndex)).Select(KeyAfterApply).ToList();
            GridEditBinding.ModelOf(GridEditCommands.Active)?.FlashRowsByKey(savedKeys);
        }
        else
        {
            GridEditBinding.ModelOf(s)?.FlashSavedRows(savedRows);
        }
    }
}
