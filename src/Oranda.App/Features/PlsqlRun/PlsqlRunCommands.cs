using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Features.PlsqlRun;

/// <summary>
/// PL/SQL 편집기 문서(TSK-08-01)가 구현하면 <c>oranda.plsql.run</c> 이 대상을 알아낸다.
/// 편집기가 아직 구현하지 않았거나 실행 대상이 아니면(<see cref="RunTarget"/> 가 null) 명령은 아무것도 하지 않는다.
/// </summary>
public interface IPlsqlRunSource
{
    /// <summary>실행할 프로시저·함수 또는 패키지. 문서 종류가 실행 대상이 아니면 null.</summary>
    DbObjectRef? RunTarget { get; }

    /// <summary>패키지 안에서 커서가 놓인 멤버 이름(없으면 null — 대화상자에서 고른다).</summary>
    string? RunMember { get; }
}

/// <summary>명령 인자로 직접 대상을 넘길 때(다른 기능이 부른다).</summary>
public sealed record PlsqlRunRequest(DbObjectRef Target, string? Member = null);

/// <summary>
/// TSK-08-02 PL/SQL 실행 대화상자 — 명령 처리기와 대화상자 조립(commands.md 7장). 서비스는 실행할 때 꺼낸다
/// (시작 경로에서 Oracle·Language 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaCommand("oranda.plsql.run", CommandCategory.Database, Title = "PL/SQL 실행", When = "plsqlEditorFocus",
    Enablement = "hasSession && !sessionBusy && plsqlRunnable", Placements = ["menu:sql/plsql/run@31"])]
[OrandaCommand("oranda.plsql.runFromBrowser", CommandCategory.Database, Title = "프로시저·함수 실행", When = "schemaFocus",
    Enablement = "plsqlNodeSelected && hasSession", Placements = ["context:schema/plsqlRun@45"])]
public sealed class PlsqlRunCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;

    public PlsqlRunCommandHandler()
    {
    }

    public PlsqlRunCommandHandler(ServiceContainer container) => _container = container;

    /// <summary>시험용: 대화상자를 여는 방법(기본 = 창을 띄운다).</summary>
    internal Action<PlsqlRunViewModel>? Presenter { get; set; }

    /// <summary>시험용: 서비스 묶음을 바꾼다.</summary>
    internal PlsqlRunDeps? Deps { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (target, member) = ResolveTarget(context);
        if (target is null || !PlsqlRunCommands.IsRunnableType(target.ObjectType))
        {
            return;
        }

        var deps = Deps ?? (_container is null ? null : PlsqlRunCommands.CreateDeps(_container));
        if (deps is null)
        {
            return;
        }

        var vm = new PlsqlRunViewModel(deps);
        if (Presenter is not null)
        {
            Presenter(vm);
        }
        else
        {
            PlsqlRunWindow.ShowFor(vm, _container?.GetService<ShellController>()?.MainWindow);
        }

        await vm.OpenAsync(target, member, cancellationToken).ConfigureAwait(true);
    }

    private (DbObjectRef? Target, string? Member) ResolveTarget(CommandContext context)
    {
        switch (context.Parameter)
        {
            case PlsqlRunRequest r:
                return (r.Target, r.Member);
            case DbObjectRef o:
                return (o, null);
            case SchemaNode { Ref: { } n }:
                return (n, null);
        }

        if (context.CommandId == "oranda.plsql.runFromBrowser")
        {
            return (PlsqlRunCommands.FindSchemaBrowser(_container)?.SelectedNode?.Ref, null);
        }

        // 편집기에서: 활성 문서가 실행 대상을 알려 준다(TSK-08-01).
        if (_container?.GetService<IShellUi>() is ShellController { Docking.ActiveDocument.Panel: IPlsqlRunSource source })
        {
            return (source.RunTarget, source.RunMember);
        }

        return (null, null);
    }
}

/// <summary>PL/SQL 실행 대화상자의 공용 도우미(서비스 묶음·대상 판별).</summary>
public static class PlsqlRunCommands
{
    /// <summary>이 Task 가 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids = ["oranda.plsql.run", "oranda.plsql.runFromBrowser"];

    /// <summary>실행 대화상자로 열 수 있는 객체 종류.</summary>
    public static bool IsRunnableType(string? objectType)
        => objectType is not null && objectType.ToUpperInvariant() is "PROCEDURE" or "FUNCTION" or "PACKAGE" or "PACKAGE BODY";

    /// <summary>컨테이너에서 실행 서비스 묶음을 만든다. 서비스는 쓸 때 해석한다.</summary>
    public static PlsqlRunDeps CreateDeps(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var bus = container.GetService<IEventBus>();
        var state = ExecutionState.For(bus);
        state.UseKeys(container.GetService<ContextKeyService>());
        return new PlsqlRunDeps
        {
            // 활성 문서가 PL/SQL 편집기(작업 공간이면 보이는 탭)면 그 편집기의 세션(연 접속), 아니면(스키마 브라우저에서 실행) 지금 접속 —
            // 활성 SQL 작업 공간이 같은 접속을 쓰면 그 작업 공간의 세션(블록의 변경·커밋이 다른 작업 공간의 트랜잭션에 섞이지 않게, TSK-14-08).
            Session = () => (container.GetService<IShellUi>() as ShellController)?.Docking.ActiveDocument?.Panel switch
            {
                PlsqlEditor.PlsqlEditorViewModel { Session: { } own } => own,
                PlsqlWorkspace.PlsqlWorkspaceViewModel { Session: { } tab } => tab,
                _ => SessionRouter.ForTool(container),
            },
            Metadata = () => container.GetService<IMetadataProvider>(),
            Execution = () =>
            {
                ExecutionCommands.EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            },
            State = state,
            Log = ExecutionLog.For(bus),
            SendToEditor = text => SqlEditorHost.Current.NewDocument(text),
        };
    }

    /// <summary>도킹 트리에서 스키마 브라우저 도구 창의 ViewModel 을 찾는다(없으면 null).</summary>
    internal static SchemaBrowserViewModel? FindSchemaBrowser(ServiceContainer? container)
    {
        if (container?.GetService<IShellUi>() is not IShellUi { Docking.Layout: { } root })
        {
            return null;
        }

        var stack = new Stack<Dock.Model.Core.IDockable>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is PanelTool { Definition.Id: "oranda.panel.schemaBrowser", IsPanelCreated: true, Panel: SchemaBrowserViewModel vm })
            {
                return vm;
            }

            if (d is Dock.Model.Core.IDock dock && dock.VisibleDockables is { } kids)
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }

        return null;
    }
}
