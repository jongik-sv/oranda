using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Core;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>
/// TSK-08-01 PL/SQL 소스 편집·컴파일 명령 처리기(commands.md 7장). 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand("oranda.file.newPlsqlEditor", CommandCategory.File, Title = "새 PL/SQL 편집기", Icon = "newPlsql", Placements = ["menu:file/newPlsqlEditor@15", "toolbar:main/newPlsql@15"])]
[OrandaCommand("oranda.view.compileErrors", CommandCategory.View, Title = "컴파일 오류", Placements = ["menu:view/compileErrors@50"])]
[OrandaCommand("oranda.plsql.openSource", CommandCategory.Database, Title = "PL/SQL 소스 열기", When = "schemaFocus", Enablement = "plsqlNodeSelected",
    Placements = ["context:schema/plsql@50"])]
[OrandaCommand("oranda.plsql.addToWorkspace", CommandCategory.Database, Title = "PL/SQL 작업 공간에 추가", When = "schemaFocus", Enablement = "plsqlNodeSelected",
    Placements = ["context:schema/plsql@51"])]
[OrandaCommand("oranda.plsql.saveWorkspaceFile", CommandCategory.File, Title = "PL/SQL 작업 공간 파일로 저장", Enablement = "plsqlDocActive",
    Placements = ["menu:file/savePlsqlWorkspace@95"])]
[OrandaCommand("oranda.plsql.compile", CommandCategory.Database, Title = "컴파일(저장)", When = "plsqlEditorFocus", Enablement = "hasSession && !sessionBusy && !readOnlySession")]
[OrandaCommand("oranda.plsql.toggleSpecBody", CommandCategory.Database, Title = "명세/본문 전환", When = "plsqlEditorFocus", Enablement = "isPackageDoc")]
[OrandaCommand("oranda.plsql.showServerDiff", CommandCategory.Database, Title = "서버 소스와 차이 보기", When = "plsqlEditorFocus", Enablement = "serverSourceChanged")]
public sealed class PlsqlEditorCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public PlsqlEditorCommandHandler()
    {
    }

    public PlsqlEditorCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 지금 활성 PL/SQL 탭.</summary>
    internal Func<PlsqlEditorViewModel?>? ActiveProvider { get; set; }

    /// <summary>시험용: 문서 탭을 여는 방법(기본 = 셸 도킹).</summary>
    internal Func<PlsqlEditorViewModel, string, bool>? OpenDocument { get; set; }

    /// <summary>시험용: 지금 세션.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    /// <summary>시험용: 컴파일 오류 창을 보이는 방법.</summary>
    internal Action? ShowErrorsPanel { get; set; }

    /// <summary>시험용: 서비스 없이 쓸 제공자·실행 서비스(새 탭에 넣는다).</summary>
    internal Func<PlsqlEditorViewModel>? ViewModelFactory { get; set; }

    /// <summary>PL/SQL 편집기 끔 읽기 전용 열기의 안내 받는 곳(기본 = 상태 표시줄 알림·스키마 브라우저 상태 줄). 객체 검색은 자기 상태 줄을 넣는다.</summary>
    internal Action<string>? Notify { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null && ViewModelFactory is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.file.newPlsqlEditor":
                {
                    var vm = NewViewModel();
                    vm.OpenDocument = OpenDocument;
                    vm.StartNew(NewTemplate, ResolveSession());
                    Dock(_services, OpenDocument, vm, context.Parameter as PlsqlWorkspaceViewModel, focus: true); // 탭 줄의 + 는 그 작업 공간에
                    break;
                }

            case "oranda.view.compileErrors":
                ShowErrors();
                break;
            case "oranda.plsql.openSource":
                await OpenSourceAsync(context.Parameter, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.plsql.addToWorkspace":
                await AddToWorkspaceAsync(context.Parameter, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.plsql.saveWorkspaceFile":
                if (VisibleWorkspace() is { } shown)
                {
                    await PlsqlWorkspaceFiles.SaveAsync(_services, SqlEditorHost.Current, shown, cancellationToken, saveAs: context.Parameter is true).ConfigureAwait(true);
                }

                break;
            case "oranda.plsql.compile":
                if (CurrentActive() is { } active)
                {
                    var outcome = await active.CompileAsync(force: false, cancellationToken).ConfigureAwait(true);
                    if (outcome.Status is PlsqlCompileStatus.Compiled or PlsqlCompileStatus.CompiledWithErrors)
                    {
                        ShowErrors();
                    }
                }

                break;
            case "oranda.plsql.toggleSpecBody":
                if (CurrentActive() is { } doc)
                {
                    await doc.OpenSiblingAsync(cancellationToken).ConfigureAwait(true);
                }

                break;
            case "oranda.plsql.showServerDiff":
                if (CurrentActive() is { } diffDoc)
                {
                    await diffDoc.ShowServerDiffAsync(cancellationToken).ConfigureAwait(true);
                }

                break;
        }
    }

    /// <summary>새 문서에 채우는 뼈대.</summary>
    public const string NewTemplate = "CREATE OR REPLACE PROCEDURE new_procedure\nIS\nBEGIN\n    NULL;\nEND new_procedure;\n";

    private DbObjectRef? TargetOf(object? parameter) => parameter switch
    {
        PlsqlSourceLocation at => at.Ref,
        SchemaNode n => n.Ref,
        DbObjectRef r => r,
        _ => FindSchemaBrowser()?.SelectedNode?.Ref,
    };

    /// <summary>
    /// 「PL/SQL 작업 공간에 추가」: 스키마 브라우저에서 고른 객체를 고정 탭으로 넣는다(활성 → 최근 → 새 작업 공간). 포커스는 스키마 브라우저에 남는다.
    /// 이미 어느 작업 공간에 열려 있으면 새로 넣지 않고 그 탭을 앞으로 낸다. 패키지(명세·본문)는 둘 다 같은 작업 공간에 넣고, 짝을 읽지 못하면 그 탭만 뺀다.
    /// </summary>
    private async Task AddToWorkspaceAsync(object? parameter, CancellationToken ct)
    {
        var target = TargetOf(parameter);
        var session = ResolveSession();
        if (target is not null && session is not null && PlsqlSourceText.SourceTypes.Contains(target.ObjectType) && SchemaObjectOpener.IsPlsqlEditorOff(_services))
        {
            // 메뉴에서는 숨지만(plsqlWorkspace 는 plsqlEditor 가 있어야 켜진다) 직접 불러도 PL/SQL 탭을 만들지 않는다(D-007).
            await SchemaObjectOpener.OpenReadOnlySourceAsync(_services, target, session, 0, preview: false, Notify, ct).ConfigureAwait(true);
            return;
        }

        if (target is null || session is null || !PlsqlSourceText.SourceTypes.Contains(target.ObjectType) || PlsqlWorkspaceRouter.Of(_services) is not { } router)
        {
            return;
        }

        var types = target.ObjectType is "PACKAGE" or "PACKAGE BODY" ? new[] { "PACKAGE", "PACKAGE BODY" } : [target.ObjectType];
        // 짝(명세·본문) 중 이미 열린 쪽이 있으면 그 작업 공간에 같이 넣는다(아직 안 열린 쪽이 먼저 와도).
        PlsqlWorkspaceViewModel? near = types.Select(t => router.FindOpen(target with { ObjectType = t }, session)?.Workspace).FirstOrDefault(w => w is not null);
        (PlsqlWorkspaceViewModel Workspace, PlsqlEditorViewModel Tab)? clicked = null;
        foreach (var type in types)
        {
            var isClicked = type == target.ObjectType;
            var item = target with { ObjectType = type };
            if (router.FindOpen(item, session) is { } open)
            {
                open.Workspace.Pin(open.Tab); // 미리보기로 열려 있으면 고정한다
                router.Show(open.Workspace, open.Tab, focus: false);
                near ??= open.Workspace;
                clicked = isClicked ? open : clicked;
                continue;
            }

            var vm = NewViewModel();
            if (router.Place(vm, near, preview: false, focus: false) is null)
            {
                continue;
            }

            var workspace = router.WorkspaceOf(vm);
            var loaded = await router.LoadAsync(vm, token => vm.OpenAsync(session, item, token), ct).ConfigureAwait(true);
            if (!loaded && !isClicked && workspace is not null)
            {
                workspace.Remove(vm); // 짝을 읽지 못하면 그 탭은 뺀다
                continue;
            }

            if (workspace is not null)
            {
                near ??= workspace;
                clicked = isClicked ? (workspace, vm) : clicked;
            }
        }

        if (clicked is { } shown && shown.Workspace.Tabs.Contains(shown.Tab))
        {
            router.Show(shown.Workspace, shown.Tab, focus: false); // 누른 객체의 탭이 보이게
        }
    }

    /// <summary>보이는 PL/SQL 작업 공간(활성 문서). 활성 문서가 PL/SQL 작업 공간이 아니면 활성 PL/SQL 탭이 든 작업 공간.</summary>
    private PlsqlWorkspaceViewModel? VisibleWorkspace()
    {
        if ((_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ActiveDocument?.Panel is PlsqlWorkspaceViewModel active)
        {
            return active;
        }

        return CurrentActive() is { } tab ? PlsqlWorkspaceRouter.Of(_services)?.WorkspaceOf(tab) : null;
    }

    private async Task OpenSourceAsync(object? parameter, CancellationToken ct)
    {
        var target = TargetOf(parameter);
        var session = ResolveSession();
        if (target is null || session is null || !PlsqlSourceText.SourceTypes.Contains(target.ObjectType))
        {
            return;
        }

        // D-007: PL/SQL 편집기 기능이 꺼져 있으면 서버 소스를 읽기 전용 SQL 탭으로 연다(분기는 SchemaObjectOpener 한 곳).
        if (SchemaObjectOpener.IsPlsqlEditorOff(_services))
        {
            var line = parameter is PlsqlSourceLocation { Line: > 0 } at ? at.Line : 0;
            await SchemaObjectOpener.OpenReadOnlySourceAsync(_services, target, session, line, preview: false, Notify, ct).ConfigureAwait(true);
            return;
        }

        var vm = NewViewModel();
        vm.OpenDocument = OpenDocument;
        if (!Dock(_services, OpenDocument, vm))
        {
            return;
        }

        // 작업 공간에 넣었으면 읽는 중에 탭·작업 공간이 닫힐 때 읽기를 멈춘다(시험의 OpenDocument 는 그대로 읽는다).
        var router = OpenDocument is null ? PlsqlWorkspaceRouter.Of(_services) : null;
        var loaded = router is not null
            ? await router.LoadAsync(vm, token => vm.OpenAsync(session, target, token), ct).ConfigureAwait(true)
            : await vm.OpenAsync(session, target, ct).ConfigureAwait(true);
        if (loaded && parameter is PlsqlSourceLocation { Line: > 0 } location)
        {
            vm.GoToServerLine(location.Line);
        }
    }

    private void ShowErrors()
    {
        if (ShowErrorsPanel is not null)
        {
            ShowErrorsPanel();
            return;
        }

        (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.compileErrors");
    }

    private PlsqlEditorViewModel? CurrentActive() => ActiveProvider is not null ? ActiveProvider() : PlsqlEditorViewModel.Active;

    private PlsqlEditorViewModel NewViewModel()
        => ViewModelFactory?.Invoke() ?? (_services is null ? new PlsqlEditorViewModel() : new PlsqlEditorViewModel(_services));

    private ISession? ResolveSession() => SessionProvider?.Invoke() ?? ResolveSession(_services);

    /// <summary>새 PL/SQL 탭이 쓸 지금 세션: 스키마 브라우저가 보는 세션 → 지금 접속. 파일 열기(<see cref="PlsqlFiles"/>)도 같은 길을 쓴다.</summary>
    internal static ISession? ResolveSession(IServiceProvider? services)
        => FindSchemaBrowser(services)?.CurrentSession ?? ConnectionManager.SessionRouter.CurrentOf(services);

    private SchemaBrowserViewModel? FindSchemaBrowser() => FindSchemaBrowser(_services);

    private static SchemaBrowserViewModel? FindSchemaBrowser(IServiceProvider? services)
    {
        if (services?.GetService(typeof(IShellUi)) is not IShellUi { Docking.Layout: { } root })
        {
            return null;
        }

        var stack = new Stack<IDockable>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is PanelTool { Definition.Id: "oranda.panel.schemaBrowser", IsPanelCreated: true, Panel: SchemaBrowserViewModel vm })
            {
                return vm;
            }

            if (d is IDock dock && dock.VisibleDockables is { } kids)
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 탭을 PL/SQL 작업 공간에 넣는다(<see cref="PlsqlWorkspaceRouter"/>: <paramref name="near"/> → 활성 → 최근 → 새로). 시험이 <paramref name="open"/> 을 주면 그것으로 연다.
    /// </summary>
    internal static bool Dock(IServiceProvider? services, Func<PlsqlEditorViewModel, string, bool>? open, PlsqlEditorViewModel vm,
        PlsqlWorkspaceViewModel? near = null, bool focus = false)
    {
        if (open is not null)
        {
            return open(vm, vm.Title);
        }

        return PlsqlWorkspaceRouter.Of(services)?.Place(vm, near, preview: false, focus) is not null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DockWorkspace, object> CloseConfirmInstalled = new();

    /// <summary>
    /// 고친 PL/SQL 탭을 문서 탭 ×·닫기 명령으로 닫기 전에 저장 확인을 건다(TSK-13-27). PL/SQL 작업 공간 문서는 고친 탭마다 차례로 묻는다.
    /// 이미 걸려 있던 확인은 PL/SQL 이 아닌 문서에 그대로 넘긴다. 앱 종료는 이 경로를 지나지 않으므로 SQL 탭처럼 묻지 않는다.
    /// </summary>
    internal static void InstallCloseConfirm(DockWorkspace docking, Func<SqlEditorHost>? host = null)
    {
        if (!CloseConfirmInstalled.TryAdd(docking, new object()))
        {
            return;
        }

        var previous = docking.CloseConfirm;
        docking.CloseConfirm = document => document.Panel switch
        {
            PlsqlEditorViewModel vm => vm.NeedsSaveConfirm ? ct => vm.ConfirmCloseAsync((host ?? (() => SqlEditorHost.Current))(), ct) : null,
            PlsqlWorkspaceViewModel ws => ws.NeedsSaveConfirm ? ct => ws.ConfirmCloseAllAsync(host?.Invoke(), ct) : null,
            _ => previous?.Invoke(document),
        };
    }

}

/// <summary><c>oranda.plsql.openSource</c> 인자: 객체와 서버 소스 줄 번호(1부터, <c>all_source.line</c>). 열고 나서 그 줄로 커서를 옮긴다.</summary>
public sealed record PlsqlSourceLocation(DbObjectRef Ref, int Line);

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="PlsqlEditorCommandHandler"/>.</summary>
public static class PlsqlEditorCommands
{
    public const string PanelId = "oranda.panel.plsqlEditor";

    internal static bool Dock(IServiceProvider? services, Func<PlsqlEditorViewModel, string, bool>? open, PlsqlEditorViewModel vm,
        PlsqlWorkspaceViewModel? near = null)
        => PlsqlEditorCommandHandler.Dock(services, open, vm, near);
}
