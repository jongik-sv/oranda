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

    private async Task OpenSourceAsync(object? parameter, CancellationToken ct)
    {
        var target = parameter switch
        {
            PlsqlSourceLocation at => at.Ref,
            SchemaNode n => n.Ref,
            DbObjectRef r => r,
            _ => FindSchemaBrowser()?.SelectedNode?.Ref,
        };
        var session = ResolveSession();
        if (target is null || session is null || !PlsqlSourceText.SourceTypes.Contains(target.ObjectType))
        {
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

    private ISession? ResolveSession()
        => SessionProvider?.Invoke()
            ?? FindSchemaBrowser()?.CurrentSession
            ?? ConnectionManager.SessionRouter.CurrentOf(_services);

    private SchemaBrowserViewModel? FindSchemaBrowser()
    {
        if (_services?.GetService(typeof(IShellUi)) is not IShellUi { Docking.Layout: { } root })
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
