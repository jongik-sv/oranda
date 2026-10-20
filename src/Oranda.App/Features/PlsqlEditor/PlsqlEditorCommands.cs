using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Core;
using Oranda.App.Features.SchemaBrowser;
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
                    Dock(_services, OpenDocument, vm);
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

        if (await vm.OpenAsync(session, target, ct).ConfigureAwait(true) && parameter is PlsqlSourceLocation { Line: > 0 } location)
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
            ?? (_services?.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions.LastOrDefault(s => s.Purpose == SessionPurpose.Work);

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

    internal static bool Dock(IServiceProvider? services, Func<PlsqlEditorViewModel, string, bool>? open, PlsqlEditorViewModel vm)
    {
        if (open is not null)
        {
            return open(vm, vm.Title);
        }

        if ((services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.OpenDocument(PlsqlEditorCommands.PanelId, vm, vm.Title) is not { } doc)
        {
            return false;
        }

        // 소스를 읽은 뒤 정해지는 제목(「FT.EMP_PKG」)도 탭 머리글에 따라간다(처음엔 「PL/SQL」로 열린다).
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlsqlEditorViewModel.Title))
            {
                doc.Title = vm.Title;
            }
        };
        return true;
    }
}

/// <summary><c>oranda.plsql.openSource</c> 인자: 객체와 서버 소스 줄 번호(1부터, <c>all_source.line</c>). 열고 나서 그 줄로 커서를 옮긴다.</summary>
public sealed record PlsqlSourceLocation(DbObjectRef Ref, int Line);

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="PlsqlEditorCommandHandler"/>.</summary>
public static class PlsqlEditorCommands
{
    public const string PanelId = "oranda.panel.plsqlEditor";

    internal static bool Dock(IServiceProvider? services, Func<PlsqlEditorViewModel, string, bool>? open, PlsqlEditorViewModel vm)
        => PlsqlEditorCommandHandler.Dock(services, open, vm);
}
