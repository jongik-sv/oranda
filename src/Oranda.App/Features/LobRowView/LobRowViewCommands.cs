using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LobRowView;

/// <summary>
/// TSK-06-05 LOB 편집기·행 보기 — 명령 처리기(commands.md 「결과」·「보기」). 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 셸이 자동 수집한다.
/// 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
public static class LobRowViewCommands
{
    public const string LobEditorPanelId = "oranda.panel.lobEditor";
    public const string RowViewPanelId = "oranda.panel.rowView";

    /// <summary>
    /// 그리드의 (행, 열) 셀로 LOB 편집기 입력을 만든다. 저장 대상은 <paramref name="edit"/>(편집 모드 세션)에서 PK/ROWID 규칙으로 구한다.
    /// 편집 모드가 아니거나 편집할 수 없는 결과면 대상 없이(읽기·파일 저장만) 돌려준다.
    /// </summary>
    public static LobEditorSource? BuildSource(ResultGridModel grid, long row, int ordinal, GridEditSession? edit, ISession? fallbackSession)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (ordinal < 0 || ordinal >= grid.Columns.Count || !grid.TryGetValue(row, ordinal, out var value))
        {
            return null;
        }

        var col = grid.Columns[ordinal];
        var title = $"{col.Name} (행 {row + 1:N0})";
        var session = edit?.Session ?? fallbackSession;
        if (value is not LobValue lob)
        {
            return new LobEditorSource(title, session, null, CellValueFormatter.Format(col, value, grid.Display), null, "LOB 가 아닌 값은 편집하지 않습니다.");
        }

        var (target, reason) = ResolveTarget(grid, edit, row, ordinal);
        return new LobEditorSource(title, session, lob, null, target, reason);
    }

    /// <summary>저장 대상(대상 테이블·열·행 키)과 못 구할 때의 이유.</summary>
    public static (LobSaveTarget? Target, string? Reason) ResolveTarget(ResultGridModel grid, GridEditSession? edit, long row, int ordinal)
    {
        if (edit is null || !ReferenceEquals(edit.Columns, grid.Columns))
        {
            return (null, "LOB 를 저장하려면 편집 모드로 결과를 여세요(「편집 모드로 실행」). 지금은 보기와 파일 저장만 됩니다.");
        }

        var cap = edit.Capability;
        if (!cap.CanEdit || cap.Target is not { } table)
        {
            return (null, "이 결과는 편집할 수 없어 LOB 를 저장할 수 없습니다: " + (cap.Message ?? cap.Reason.ToString()));
        }

        if (ordinal >= edit.Plan.ColumnNames.Count || edit.Plan.ColumnNames[ordinal] is not { } column || column == "ROWID")
        {
            return (null, "이 열은 대상 테이블의 열이 아니어서 저장할 수 없습니다.");
        }

        var key = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var o in cap.KeyColumnOrdinals)
        {
            key[edit.Plan.ColumnNames[o]!] = edit.ReadCell(row, o);
        }

        return (new LobSaveTarget(table, column, key), null);
    }
}

/// <summary>행 보기·값 보기(LOB 편집기) 명령 처리기. 활성 그리드가 없으면 아무것도 하지 않는다.</summary>
[OrandaCommand("oranda.grid.rowView", CommandCategory.Result, Title = "행 보기", When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/view@10"])]
[OrandaCommand("oranda.grid.viewValue", CommandCategory.Result, Title = "값 보기", When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/view@20"])]
[OrandaCommand("oranda.view.rowView", CommandCategory.View, Title = "행 보기 창")]
public sealed class LobRowViewCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;

    public LobRowViewCommandHandler()
    {
    }

    public LobRowViewCommandHandler(ServiceContainer container) => _container = container;

    /// <summary>시험용: 문서 탭을 여는 방법(panelId, 새 ViewModel, 제목 → 실제 ViewModel).</summary>
    internal Func<string, ToolPanelViewModelBase, string, ToolPanelViewModelBase?>? OpenDocument { get; set; }

    /// <summary>시험용: 도구 창을 보이는 방법(panelId).</summary>
    internal Action<string>? ShowTool { get; set; }

    /// <summary>시험용: 지금 편집 모드 세션.</summary>
    internal Func<GridEditSession?>? EditSessionProvider { get; set; }

    /// <summary>시험용: 지금 작업 세션.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.CommandId)
        {
            case "oranda.view.rowView":
                Show(LobRowViewCommands.RowViewPanelId);
                RowViewerViewModel.AttachActive(ResultGridCommands.Active);
                break;
            case "oranda.grid.rowView" when ResultGridCommands.Active is { } grid:
                Show(LobRowViewCommands.RowViewPanelId);
                RowViewerViewModel.AttachActive(grid);
                break;
            case "oranda.grid.viewValue" when ResultGridCommands.Active is { HasSelection: true } g:
                await ViewValueAsync(g, cancellationToken).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }

    private async Task ViewValueAsync(ResultGridModel grid, CancellationToken ct)
    {
        var cell = grid.Active;
        if (cell.Col < 0 || cell.Col >= grid.VisibleColumns.Count)
        {
            return;
        }

        var ordinal = grid.VisibleColumns[cell.Col];
        var edit = EditSessionProvider is not null ? EditSessionProvider() : GridEditCommands.Active;
        var session = SessionProvider is not null ? SessionProvider() : ActiveSession();
        if (LobRowViewCommands.BuildSource(grid, cell.Row, ordinal, edit, session) is not { } source)
        {
            return;
        }

        if (Open(LobRowViewCommands.LobEditorPanelId, (_container is null ? new LobEditorViewModel() : new LobEditorViewModel(_container)), source.Title) is LobEditorViewModel vm)
        {
            await vm.AttachAsync(source, ct).ConfigureAwait(true);
        }
    }

    // 활성 탭의 세션: 전용 세션 탭(D-017)의 결과면 그 세션으로 LOB 를 읽는다.
    private ISession? ActiveSession() => _container is null ? null : ExecutionCommands.ActiveTabSession(_container);

    private void Show(string panelId)
    {
        if (ShowTool is not null)
        {
            ShowTool(panelId);
            return;
        }

        _container?.GetService<IShellUi>()?.Docking.ShowTool(panelId);
    }

    private ToolPanelViewModelBase? Open(string panelId, ToolPanelViewModelBase vm, string title)
    {
        if (OpenDocument is not null)
        {
            return OpenDocument(panelId, vm, title);
        }

        return _container?.GetService<IShellUi>()?.Docking.OpenDocument(panelId, vm, title)?.Panel as ToolPanelViewModelBase;
    }
}
