using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Extensibility;
using Oranda.Core.Plans;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>TSK-07-01 실행 계획 — 명령 ID(commands.md 7장). 처리기는 <see cref="ExecutionPlanCommandHandler"/>.</summary>
public static class ExecutionPlanCommands
{
    public const string PanelId = "oranda.panel.plan";

    /// <summary>이 Task 가 등록하는 명령 ID 전체.</summary>
    public static readonly string[] Ids =
    [
        "oranda.plan.explain",
        "oranda.plan.actual",
        "oranda.plan.displayCursorOptions",
        "oranda.view.plan",
    ];
}

/// <summary>
/// 예상 계획(Ctrl+E)·실제 계획(Ctrl+Shift+E)·형식 옵션·창 열기.
/// 편집기 현재 문장은 <see cref="IEditorContext"/> 로 얻고(소비만), 마지막 실행의 sql_id 는 <c>StatementExecuted</c> 이벤트의 커서를 기억한다.
/// </summary>
[OrandaCommand("oranda.plan.explain", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "hasSession && !sessionBusy",
    Placements = ["menu:sql/explain@50"])]
[OrandaCommand("oranda.plan.actual", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "hasSession",
    Placements = ["menu:sql/actualPlan@60"])]
[OrandaCommand("oranda.plan.displayCursorOptions", CommandCategory.Sql, Enablement = "hasSession",
    Placements = ["menu:sql/displayCursorOptions@70"])]
[OrandaCommand("oranda.view.plan", CommandCategory.View, Placements = ["menu:view/plan@40"])]
public sealed class ExecutionPlanCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;
    private CursorRef? _lastCursor;

    public ExecutionPlanCommandHandler()
    {
    }

    public ExecutionPlanCommandHandler(ServiceContainer container)
    {
        _container = container;
        _container.GetService<IEventBus>()?.Subscribe<StatementExecuted>(this, e =>
        {
            if (e.Result.Cursor is { } cursor)
            {
                _lastCursor = cursor;
            }
        });
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        shell.Docking.ShowTool(ExecutionPlanCommands.PanelId);
        if (context.CommandId is "oranda.view.plan" or "oranda.plan.displayCursorOptions")
        {
            return;
        }

        var vm = await FindPanelAsync().ConfigureAwait(true);
        var session = ConnectionManagerCommands.Resolve(_container).ActiveSession;
        if (vm is null || session is null)
        {
            return;
        }

        if (context.CommandId == "oranda.plan.explain")
        {
            if (_container.GetService<IEditorContext>()?.GetCurrentStatement() is not { } statement)
            {
                vm.StatusText = "예상 계획을 볼 문장이 없습니다(커서를 문장 안에 두세요).";
                return;
            }

            await vm.ExplainAsync(session, statement, cancellationToken).ConfigureAwait(true);
        }
        else if (!string.IsNullOrWhiteSpace(vm.SqlIdText))
        {
            await vm.ShowTypedCursorAsync(session, cancellationToken).ConfigureAwait(true);
        }
        else if (_lastCursor is { } cursor)
        {
            await vm.ShowCursorAsync(session, cursor, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            vm.StatusText = "마지막 실행의 sql_id 를 알 수 없습니다. 창의 sql_id 칸에 직접 넣으세요.";
        }
    }

    private static async Task<PlanViewModel?> FindPanelAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            if (PlanViewModel.Current is { } vm)
            {
                return vm;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        return PlanViewModel.Current;
    }
}
