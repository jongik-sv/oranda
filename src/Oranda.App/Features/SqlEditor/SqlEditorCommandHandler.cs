using System.Globalization;
using Oranda.Core.Shell;
using static Oranda.App.Features.SqlEditor.EditorTextOps;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// TSK-04-02 이 소유한 명령 52개의 처리기(commands.md 7장). 한 클래스가 <see cref="CommandContext.CommandId"/> 로 나눠 처리한다.
/// 활성 탭은 <see cref="SqlEditorHost.Active"/>. 탭이 없을 때 편집 명령은 아무것도 하지 않는다(활성 조건은 셸이 막는다).
/// </summary>
[OrandaCommand("oranda.file.newSqlEditor", CommandCategory.File, Icon = "newSql", Placements = ["menu:file/newSqlEditor@10", "toolbar:main/newSql@10"])]
[OrandaCommand("oranda.file.open", CommandCategory.File, Icon = "open", Placements = ["menu:file/open@20", "toolbar:main/open@20"])]
[OrandaCommand("oranda.file.save", CommandCategory.File, Enablement = "hasDocument", Icon = "save", Placements = ["menu:file/save@30", "toolbar:main/save@30"])]
[OrandaCommand("oranda.file.saveAs", CommandCategory.File, Enablement = "hasDocument && !mybatisDocActive", Placements = ["menu:file/saveAs@40"])]
[OrandaCommand("oranda.file.saveAll", CommandCategory.File, Enablement = "hasDocument", Placements = ["menu:file/saveAll@50"])]
[OrandaCommand("oranda.file.reopenClosed", CommandCategory.File, Placements = ["menu:file/reopenClosed@60"])]
[OrandaCommand("oranda.file.duplicateTab", CommandCategory.File, Enablement = "sqlDocActive", Placements = ["menu:file/duplicateTab@70"])]
[OrandaCommand("oranda.file.saveWorkspace", CommandCategory.File, Enablement = "hasDocument")]
[OrandaCommand("oranda.file.openWorkspace", CommandCategory.File)]
[OrandaCommand("oranda.file.openWorkspaceFile", CommandCategory.File, Icon = "workspaceOpen", Placements = ["menu:file/openWorkspaceFile@90"])]
[OrandaCommand("oranda.file.saveWorkspaceFile", CommandCategory.File, Enablement = "hasDocument && !mybatisDocActive", Icon = "workspaceSave", Placements = ["menu:file/saveWorkspaceFile@92"])]
[OrandaCommand("oranda.file.saveWorkspaceFileAs", CommandCategory.File, Enablement = "hasDocument && !mybatisDocActive", Placements = ["menu:file/saveWorkspaceFileAs@94"])]
[OrandaCommand("oranda.file.newWorkspace", CommandCategory.File, Icon = "folderAdd", Placements = ["menu:file/newWorkspace@12", "toolbar:main/newWorkspace@12"])]
[OrandaCommand("oranda.file.renameWorkspace", CommandCategory.File, Enablement = "sqlWorkspaceActive", Placements = ["menu:file/renameWorkspace@96"])]
[OrandaCommand("oranda.file.closeWorkspace", CommandCategory.File, Enablement = "sqlWorkspaceActive", Placements = ["menu:file/closeWorkspace@805"])]
[OrandaCommand("oranda.file.renameTab", CommandCategory.File, Enablement = "sqlDocActive && !mybatisDocActive", Icon = "rename", Placements = ["menu:file/renameTab@75", "context:editor/renameTab@900"])]
[OrandaCommand("oranda.edit.undo", CommandCategory.Edit, When = "editorFocus", Enablement = "canUndo", Placements = ["menu:edit/undo@10", "context:editor/undo@10"])]
[OrandaCommand("oranda.edit.redo", CommandCategory.Edit, When = "editorFocus", Enablement = "canRedo", Placements = ["menu:edit/redo@20", "context:editor/redo@20"])]
[OrandaCommand("oranda.edit.cut", CommandCategory.Edit, When = "editorFocus", Enablement = "hasSelection", Placements = ["menu:edit/cut@30", "context:editor/cut@30"])]
[OrandaCommand("oranda.edit.copy", CommandCategory.Edit, When = "editorFocus", Enablement = "hasSelection", Placements = ["menu:edit/copy@40", "context:editor/copy@40"])]
[OrandaCommand("oranda.edit.paste", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/paste@50", "context:editor/paste@50"])]
[OrandaCommand("oranda.edit.selectAll", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/selectAll@60", "context:editor/selectAll@60"])]
[OrandaCommand("oranda.edit.find", CommandCategory.Edit, When = "editorFocus", Icon = "find", Placements = ["menu:edit/find@70", "context:editor/find@70"])]
[OrandaCommand("oranda.edit.replace", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/replace@80", "context:editor/replace@80"])]
[OrandaCommand("oranda.edit.findNext", CommandCategory.Edit, When = "editorFocus", Enablement = "hasFindText", Placements = ["menu:edit/findNext@90"])]
[OrandaCommand("oranda.edit.findPrevious", CommandCategory.Edit, When = "editorFocus", Enablement = "hasFindText", Placements = ["menu:edit/findPrevious@100"])]
[OrandaCommand("oranda.edit.goToLine", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/goToLine@110"])]
[OrandaCommand("oranda.edit.toggleLineComment", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/toggleLineComment@120", "context:editor/toggleLineComment@120"])]
[OrandaCommand("oranda.edit.toggleBlockComment", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/toggleBlockComment@130"])]
[OrandaCommand("oranda.edit.commentLines", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/commentLines@140"])]
[OrandaCommand("oranda.edit.uncommentLines", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/uncommentLines@150"])]
[OrandaCommand("oranda.edit.indent", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/indent@160"])]
[OrandaCommand("oranda.edit.outdent", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/outdent@170"])]
[OrandaCommand("oranda.edit.upperCase", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/upperCase@180", "context:editor/upperCase@180"])]
[OrandaCommand("oranda.edit.lowerCase", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/lowerCase@190", "context:editor/lowerCase@190"])]
[OrandaCommand("oranda.edit.titleCase", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/titleCase@200"])]
[OrandaCommand("oranda.edit.sentenceCase", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/sentenceCase@210"])]
[OrandaCommand("oranda.edit.invertCase", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/invertCase@220"])]
[OrandaCommand("oranda.edit.cycleCase", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/cycleCase@230"])]
[OrandaCommand("oranda.edit.duplicateLine", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/duplicateLine@240", "context:editor/duplicateLine@240"])]
[OrandaCommand("oranda.edit.deleteLine", CommandCategory.Edit, When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/deleteLine@250", "context:editor/deleteLine@250"])]
[OrandaCommand("oranda.edit.toggleBookmark", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/toggleBookmark@260", "context:editor/toggleBookmark@260"])]
[OrandaCommand("oranda.edit.nextBookmark", CommandCategory.Edit, When = "editorFocus", Enablement = "hasBookmark", Placements = ["menu:edit/nextBookmark@270"])]
[OrandaCommand("oranda.edit.previousBookmark", CommandCategory.Edit, When = "editorFocus", Enablement = "hasBookmark", Placements = ["menu:edit/previousBookmark@280"])]
[OrandaCommand("oranda.edit.fold", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/fold@290"])]
[OrandaCommand("oranda.edit.unfold", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/unfold@300"])]
[OrandaCommand("oranda.edit.foldAll", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/foldAll@310"])]
[OrandaCommand("oranda.edit.unfoldAll", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/unfoldAll@320"])]
[OrandaCommand("oranda.edit.goToMatchingBracket", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/goToMatchingBracket@330"])]
[OrandaCommand("oranda.edit.addCursorAbove", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/addCursorAbove@340"])]
[OrandaCommand("oranda.edit.addCursorBelow", CommandCategory.Edit, When = "editorFocus", Placements = ["menu:edit/addCursorBelow@350"])]
[OrandaCommand("oranda.nav.documentOutline", CommandCategory.Navigate, When = "editorFocus", Placements = ["menu:navigate/documentOutline@10"])]
[OrandaCommand("oranda.view.outline", CommandCategory.View, Placements = ["menu:view/outline@10"])]
public sealed class SqlEditorCommandHandler : ICommandHandler
{
    private SqlEditorHost? _host;

    // 매개변수 없는 생성자만 둔다(이유는 EditorContext 참고). 시험은 Host 를 초기화 구문으로 준다.

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = Host;
        var tab = host.Active;

        // 저장은 포커스가 PL/SQL 편집기에 있으면 그 편집기를 로컬 파일로 저장한다(숨은 SQL 탭이 아니라). 서버 반영은 컴파일 몫이다.
        if (context.CommandId is "oranda.file.save" or "oranda.file.saveAs" && context.Keys.Evaluate("plsqlEditorFocus")
            && PlsqlEditor.PlsqlEditorViewModel.Active is { } focused)
        {
            tab = focused.Editor;
        }

        switch (context.CommandId)
        {
            // ---- 파일 ----
            case "oranda.file.newSqlEditor":
                host.NewDocument();
                return;
            case "oranda.file.open":
                {
                    // 파일 열기로 고른 Orange .sqd·Oranda .workspace.md 는 글자 그대로가 아니라 작업 공간으로 연다.
                    var path = context.Parameter as string ?? await host.Dialogs.PickOpenFileAsync(cancellationToken).ConfigureAwait(true);
                    if (path is null)
                    {
                        return;
                    }

                    if (WorkspaceFiles.OpensAsWorkspace(path))
                    {
                        await OpenWorkspaceFileAsync(host, path, cancellationToken).ConfigureAwait(true);
                    }
                    else
                    {
                        await host.OpenFileAsync(path, cancellationToken).ConfigureAwait(true);
                    }

                    return;
                }
            case "oranda.file.save" when tab is null && host.ActiveWorkspace is { MyBatis: not null } mapper && host.MyBatisHooks.SaveHandler is { } mapperSave:
                // MyBatis 작업 공간은 문장 탭을 모두 닫아도 세션 기준으로 저장한다(docs/oranda1/design.md 2.2·4.4, 구현 TSK-03-03).
                await mapperSave.SaveAsync(mapper, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.save" when tab is not null:
                await host.SaveAsync(tab, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.saveAs" when tab is not null:
                await host.SaveAsAsync(tab, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.saveAll":
                await host.SaveAllAsync(cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.reopenClosed":
                host.ReopenClosed();
                return;
            case "oranda.file.duplicateTab" when tab is not null:
                host.Duplicate(tab);
                return;
            case "oranda.file.saveWorkspace":
                await host.SaveWorkspaceAsync(cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.openWorkspace":
                await host.OpenWorkspaceAsync(cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.saveWorkspaceFile":
                await host.SaveWorkspaceFileAsync(askPath: context.Parameter is true, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.saveWorkspaceFileAs":
                await host.SaveWorkspaceFileAsync(askPath: true, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.openWorkspaceFile":
                await OpenWorkspaceFileAsync(host, context.Parameter as string, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.newWorkspace":
                host.NewWorkspace();
                return;
            case "oranda.file.renameWorkspace" when host.ActiveWorkspace is { } renamed:
                await host.RenameWorkspaceAsync(renamed, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.closeWorkspace" when host.ActiveWorkspace is { } closing:
                await host.TryCloseWorkspaceAsync(closing, cancellationToken).ConfigureAwait(true);
                return;
            case "oranda.file.renameTab" when tab is not null:
                await host.RenameTabAsync(tab, cancellationToken).ConfigureAwait(true);
                return;

            // ---- 보기·이동 ----
            case "oranda.view.outline":
            case "oranda.nav.documentOutline":
                host.RequestOutline();
                return;
        }

        // 글자 편집 명령(모두 선택·복사·찾기 등)은 포커스가 PL/SQL 편집기에 있으면 그 편집기에서 한다(숨은 SQL 탭이 아니라).
        if (context.CommandId.StartsWith("oranda.edit.", StringComparison.Ordinal) && context.Keys.Evaluate("plsqlEditorFocus")
            && PlsqlEditor.PlsqlEditorViewModel.Active is { } plsql)
        {
            tab = plsql.Editor;
        }

        if (tab is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.edit.undo":
                tab.Document.UndoStack.Undo();
                break;
            case "oranda.edit.redo":
                tab.Document.UndoStack.Redo();
                break;
            case "oranda.edit.cut":
                tab.Surface?.Cut();
                break;
            case "oranda.edit.copy":
                tab.Surface?.Copy();
                break;
            case "oranda.edit.paste":
                tab.Surface?.Paste();
                break;
            case "oranda.edit.selectAll":
                if (tab.Surface is { } s)
                {
                    s.SelectAll();
                }
                else
                {
                    tab.Select(0, tab.Document.TextLength);
                }

                break;

            case "oranda.edit.find":
                tab.OpenFind(replace: false);
                break;
            case "oranda.edit.replace":
                tab.OpenFind(replace: true);
                break;
            case "oranda.edit.findNext":
                tab.FindNext();
                break;
            case "oranda.edit.findPrevious":
                tab.FindPrevious();
                break;
            case "oranda.edit.goToLine":
                await GoToLineAsync(host, tab, context.Parameter, cancellationToken).ConfigureAwait(true);
                break;

            case "oranda.edit.toggleLineComment":
                tab.Apply(ToggleLineComment);
                break;
            case "oranda.edit.toggleBlockComment":
                tab.Apply(ToggleBlockComment);
                break;
            case "oranda.edit.commentLines":
                tab.Apply(CommentLines);
                break;
            case "oranda.edit.uncommentLines":
                tab.Apply(UncommentLines);
                break;
            case "oranda.edit.indent":
                tab.Apply((t, a, l) => Indent(t, a, l));
                break;
            case "oranda.edit.outdent":
                tab.Apply((t, a, l) => Outdent(t, a, l));
                break;

            case "oranda.edit.upperCase":
                tab.Apply((t, a, l) => ChangeCase(t, a, l, CaseMode.Upper));
                break;
            case "oranda.edit.lowerCase":
                tab.Apply((t, a, l) => ChangeCase(t, a, l, CaseMode.Lower));
                break;
            case "oranda.edit.titleCase":
                tab.Apply((t, a, l) => ChangeCase(t, a, l, CaseMode.Title));
                break;
            case "oranda.edit.sentenceCase":
                tab.Apply((t, a, l) => ChangeCase(t, a, l, CaseMode.Sentence));
                break;
            case "oranda.edit.invertCase":
                tab.Apply((t, a, l) => ChangeCase(t, a, l, CaseMode.Invert));
                break;
            case "oranda.edit.cycleCase":
                tab.Apply((t, a, l) => ChangeCase(t, a, l, CaseMode.Cycle));
                break;

            case "oranda.edit.duplicateLine":
                tab.Apply((t, a, l) => DuplicateLine(t, a, l));
                break;
            case "oranda.edit.deleteLine":
                tab.Apply((t, a, l) => DeleteLine(t, a, l));
                break;

            case "oranda.edit.toggleBookmark":
                tab.ToggleBookmark();
                break;
            case "oranda.edit.nextBookmark":
                tab.GoToBookmark(+1);
                break;
            case "oranda.edit.previousBookmark":
                tab.GoToBookmark(-1);
                break;

            case "oranda.edit.fold":
                tab.Surface?.Fold(FoldAction.Fold);
                break;
            case "oranda.edit.unfold":
                tab.Surface?.Fold(FoldAction.Unfold);
                break;
            case "oranda.edit.foldAll":
                tab.Surface?.Fold(FoldAction.FoldAll);
                break;
            case "oranda.edit.unfoldAll":
                tab.Surface?.Fold(FoldAction.UnfoldAll);
                break;
            case "oranda.edit.goToMatchingBracket":
                tab.GoToMatchingBracket();
                break;
            case "oranda.edit.addCursorAbove":
                tab.Surface?.AddCursor(-1);
                break;
            case "oranda.edit.addCursorBelow":
                tab.Surface?.AddCursor(+1);
                break;

            default:
                break;
        }
    }

    private static async Task GoToLineAsync(SqlEditorHost host, SqlEditorViewModel tab, object? parameter, CancellationToken ct)
    {
        var text = parameter switch
        {
            int n => n.ToString(CultureInfo.InvariantCulture),
            string s => s,
            _ => await host.Dialogs.PromptAsync("줄로 이동", string.Empty, ct).ConfigureAwait(true),
        };
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        {
            tab.GoToLine(line);
        }
    }

    internal static async Task OpenWorkspaceFileAsync(SqlEditorHost host, string? path, CancellationToken ct)
    {
        try
        {
            var file = await host.OpenWorkspaceFileAsync(path, ct).ConfigureAwait(true);
            if (file is { Notes.Count: > 0 })
            {
                await host.Dialogs.ShowMessageAsync("작업 공간 열기", string.Join(Environment.NewLine, file.Notes), ct).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException)
        {
            await host.Dialogs.ShowMessageAsync("작업 공간 열기", "파일을 열지 못했다: " + ex.Message, ct).ConfigureAwait(true);
        }
    }
}
