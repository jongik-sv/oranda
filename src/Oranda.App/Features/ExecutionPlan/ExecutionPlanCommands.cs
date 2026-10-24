using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Settings;
using Oranda.Core.Execution;
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
        "oranda.plan.pinForCompare",
        "oranda.plan.compare",
        "oranda.view.plan",
        RerunWithHintId,
        RerunWithStatisticsLevelId,
    ];

    /// <summary>「실제 행 수 받으며 다시 실행 › 힌트 넣은 사본」(TSK-06-04).</summary>
    public const string RerunWithHintId = "oranda.tuning.rerunWithHint";

    /// <summary>「실제 행 수 받으며 다시 실행 › STATISTICS_LEVEL=ALL」(TSK-06-04).</summary>
    public const string RerunWithStatisticsLevelId = "oranda.tuning.rerunWithStatisticsLevel";
}

/// <summary>
/// 예상 계획(Ctrl+E)·실제 계획(Ctrl+Shift+E)·형식 옵션·창 열기·비교 기준 고정·고정 계획과 비교(PLAN-04).
/// 편집기 현재 문장은 <see cref="IEditorContext"/> 로 얻고(소비만), 마지막 실행의 sql_id 는 <c>StatementExecuted</c> 이벤트의 커서를 기억한다.
/// </summary>
[OrandaCommand("oranda.plan.explain", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession) && !sessionBusy && !mybatisDocActive",
    Icon = "explainPlan", Placements = ["menu:sql/explain@50", "menu:tuning/explain@10", "toolbar:main/explain@230", "context:editor/explain@1020"])]
[OrandaCommand("oranda.plan.actual", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession) && !mybatisDocActive",
    Placements = ["menu:sql/actualPlan@60", "menu:tuning/actualPlan@20"])]
[OrandaCommand("oranda.plan.displayCursorOptions", CommandCategory.Sql, Enablement = "hasSession",
    Placements = ["menu:tuning/displayCursorOptions@30"])]
[OrandaCommand("oranda.plan.pinForCompare", CommandCategory.Sql, Icon = "lock",
    Placements = ["menu:tuning/pinPlan@40", "context:plan/pin@10"])]
[OrandaCommand("oranda.plan.compare", CommandCategory.Sql, Icon = "diff",
    Placements = ["menu:tuning/comparePlan@50", "context:plan/compare@11"])]
[OrandaCommand("oranda.view.plan", CommandCategory.View, Placements = ["menu:view/plan@40"])]
[OrandaCommand(ExecutionPlanCommands.RerunWithHintId, CommandCategory.Sql, Enablement = "(hasSession || dedicatedSession) && !sessionBusy && !mybatisDocActive",
    Icon = "run", Placements = ["menu:tuning/rerunStats/hint@300"])]
[OrandaCommand(ExecutionPlanCommands.RerunWithStatisticsLevelId, CommandCategory.Sql, Enablement = "(hasSession || dedicatedSession) && !sessionBusy && !mybatisDocActive",
    Placements = ["menu:tuning/rerunStats/statisticsLevel@301"])]
public sealed class ExecutionPlanCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;
    private CursorRef? _lastCursor;
    private SqlStatement? _lastStatement;
    private PlanViewModel? _wired;

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

            // 편집기에서 직접 실행한 문장만 「다시 실행」 대상으로 기억한다(앱이 대신 보낸 도우미 문장은 이력을 건너뛴다).
            if (!e.Options.SkipHistory && e.Statement.Kind is StatementKind.Query or StatementKind.Dml)
            {
                _lastStatement = e.Statement;
                Dispatcher.UIThread.Post(() =>
                {
                    if (PlanViewModel.Current is { } vm)
                    {
                        vm.LastStatement = e.Statement;
                    }
                });
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
        if (vm is not null)
        {
            Wire(vm);
        }

        if (context.CommandId is ExecutionPlanCommands.RerunWithHintId or ExecutionPlanCommands.RerunWithStatisticsLevelId)
        {
            if (vm is not null)
            {
                await vm.RerunAsync(context.CommandId == ExecutionPlanCommands.RerunWithHintId ? PlanRerunMode.Hint : PlanRerunMode.StatisticsLevel, cancellationToken).ConfigureAwait(true);
            }

            return;
        }

        // 고정·비교는 접속 없이 창 안의 계획만 쓴다.
        if (context.CommandId == "oranda.plan.pinForCompare")
        {
            vm?.PinCurrent();
            return;
        }

        if (context.CommandId == "oranda.plan.compare")
        {
            vm?.CompareWithPinned();
            return;
        }

        var session = ExecutionCommands.ActiveTabSession(_container); // 활성 탭의 세션(전용 세션 탭이면 그 세션, D-017)
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
        else if (await LastCursorAsync(session, cancellationToken).ConfigureAwait(true) is { } last)
        {
            // 실행 엔진이 커서를 알려 주지 않으면 세션의 직전 문장(DISPLAY_CURSOR(NULL))으로 찾는다.
            await vm.ShowCursorAsync(session, last, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            vm.StatusText = "마지막 실행의 sql_id 를 알 수 없습니다. 창의 sql_id 칸에 직접 넣으세요.";
        }
    }

    private async Task<CursorRef?> LastCursorAsync(Oranda.Core.Sessions.ISession session, CancellationToken ct)
    {
        if (_container?.GetService<IExecutionPlanService>() is not { } plans)
        {
            return null;
        }

        try
        {
            return await Task.Run(() => plans.GetLastCursorAsync(session, ct), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>계획 창에 다시 실행·설정·문장 얻기를 한 번 잇는다.</summary>
    private void Wire(PlanViewModel vm)
    {
        if (ReferenceEquals(_wired, vm) || _container is not { } container)
        {
            return;
        }

        _wired = vm;
        vm.LastStatement ??= _lastStatement;
        vm.CurrentStatement = () => container.GetService<IEditorContext>()?.GetCurrentStatement();
        var bus = container.GetService<IEventBus>();
        var state = ExecutionState.For(bus);
        var rerunner = new PlanStatsRerunner(
            state,
            () => BindVariableStore.ForTab(SqlEditorHost.Current.Active, bus),
            () => ExecutionCommands.ActiveTabSession(container),
            () =>
            {
                ExecutionCommands.EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            },
            () => container.GetRequiredService<IExecutionPlanService>());
        vm.Rerunner = (mode, stmt, ct) => rerunner.RunAsync(stmt, mode, ct);
        if (container.GetService<SettingsService>() is { } settings)
        {
            vm.AlwaysHint = settings.Current.Execution.GatherPlanStatisticsHint;
            vm.SaveAlwaysHint = on => _ = settings.UpdateAsync(s => s with { Execution = s.Execution with { GatherPlanStatisticsHint = on } });
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
