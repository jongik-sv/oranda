using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using System.Linq;
using Oranda.App.Features.Binds;
using Oranda.App.Features.ConnectionManager;
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
[OrandaCommand("oranda.view.plan", CommandCategory.View)]
[OrandaCommand(ExecutionPlanCommands.RerunWithHintId, CommandCategory.Sql, Enablement = "(hasSession || dedicatedSession) && !sessionBusy && !mybatisDocActive",
    Icon = "run", Placements = ["menu:tuning/rerunStats/hint@300"])]
[OrandaCommand(ExecutionPlanCommands.RerunWithStatisticsLevelId, CommandCategory.Sql, Enablement = "(hasSession || dedicatedSession) && !sessionBusy && !mybatisDocActive",
    Placements = ["menu:tuning/rerunStats/statisticsLevel@301"])]
public sealed class ExecutionPlanCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;
    private readonly ConditionalWeakTable<PlanViewModel, object> _wired = [];

    public ExecutionPlanCommandHandler()
    {
    }

    public ExecutionPlanCommandHandler(ServiceContainer container)
    {
        _container = container;
        PlanPanelViewModel.ModelCreated = Wire; // 계획 명령을 거치지 않는 길(잠금 모니터·자식 커서)로 만들어진 탭 모델도 연결한다
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        // 명령을 시작한 탭을 먼저 잡는다: 아래에서 기다리는 사이 활성 탭이 바뀌어도 계획은 이 탭의 것이다.
        var tab = SqlEditorHost.Current.Active;
        shell.Docking.ShowTool(ExecutionPlanCommands.PanelId);
        if (context.CommandId is "oranda.view.plan" or "oranda.plan.displayCursorOptions")
        {
            return;
        }

        var vm = await FindPanelAsync(tab).ConfigureAwait(true);
        if (vm is not null)
        {
            Wire(vm, tab);
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

        var session = SessionRouter.Resolve(_container).SessionFor(tab); // 탭의 세션(전용 세션 탭이면 그 세션, D-017)
        if (vm is null || session is null)
        {
            return;
        }

        if (context.CommandId == "oranda.plan.explain")
        {
            if ((tab?.GetStatementAtCaret() ?? _container.GetService<IEditorContext>()?.GetCurrentStatement()) is not { } statement)
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
        else
        {
            var state = ExecutionState.For(_container.GetService<IEventBus>());
            await ShowActualAsync(vm, tab, session, state, LastCursorAsync, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 입력칸의 sql_id 가 없을 때 실제 계획. 세션의 직전 문장(DISPLAY_CURSOR(NULL))을 읽기 전에 그 문장이 이 탭 것인지 세션 실행 기록으로 확인한다:
    /// 세션이 실행 중이면 안내, 이 앱에서 이 세션에 보낸 문장이 없으면 옛 경로, 이 탭의 마지막 기록이 세션의 현재 순번과 같으면 읽고,
    /// 그 밖(다른 탭·다른 기능이 더 나중에 실행, 기록 없는 탭, 다른 세션 기록, 닫힌 탭 …)에는 남의 계획이 들어오지 않게 안내한다. 이미 보관한 계획은 지우지 않는다.
    /// </summary>
    internal static async Task ShowActualAsync(
        PlanViewModel vm, SqlEditorViewModel? tab, Oranda.Core.Sessions.ISession session, ExecutionState state,
        Func<Oranda.Core.Sessions.ISession, CancellationToken, Task<CursorRef?>> lastCursorOf, CancellationToken ct)
    {
        if (state.IsBusy(session.Id))
        {
            vm.StatusText = "이 세션에서 다른 문장이 실행 중입니다. 끝난 뒤 실제 계획을 보세요.";
            return;
        }

        if (tab is not null && state.RunLog.CurrentSequence(session.Id) is { } current
            && !(tab.LastRun.SessionId == session.Id && tab.LastRun.Sequence == current))
        {
            vm.StatusText = "이 탭의 실행이 세션의 마지막 실행이 아닙니다. sql_id 를 직접 넣거나 문장을 다시 실행하세요.";
            return;
        }

        if (await lastCursorOf(session, ct).ConfigureAwait(true) is { } last)
        {
            // 실행 엔진이 커서를 알려 주지 않으므로 세션의 직전 문장으로 찾는다.
            await vm.ShowCursorAsync(session, last, ct).ConfigureAwait(true);
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

    /// <summary>탭의 계획 모델에 다시 실행·설정·문장 얻기를 한 번 잇는다(모델마다 한 번).</summary>
    private void Wire(PlanViewModel vm, SqlEditorViewModel? tab)
    {
        if (_container is not { } container)
        {
            return;
        }

        if (container.GetService<SettingsService>() is { } current)
        {
            // 설정 창에서 바꾼 값을 따른다(저장 콜백은 아래에서 한 번만 잇는다 — 값을 읽어 오는 것만으로 설정을 다시 쓰지 않는다).
            vm.SetAlwaysHintFromSettings(current.Current.Execution.GatherPlanStatisticsHint);
        }

        if (_wired.TryGetValue(vm, out _))
        {
            return;
        }

        _wired.Add(vm, new object());
        vm.LastStatementSource = () => tab?.LastRun.Statement; // 탭이 시작한 실행만: 다른 탭의 문장이 섞이지 않는다
        vm.CurrentStatement = () => tab is not null ? tab.GetStatementAtCaret() : container.GetService<IEditorContext>()?.GetCurrentStatement();
        var bus = container.GetService<IEventBus>();
        var state = ExecutionState.For(bus);
        var rerunner = new PlanStatsRerunner(
            state,
            () => BindVariableStore.ForTab(tab ?? SqlEditorHost.Current.Active, bus),
            () => SessionRouter.Resolve(container).SessionFor(tab ?? SqlEditorHost.Current.Active),
            () =>
            {
                ExecutionCommands.EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            },
            () => container.GetRequiredService<IExecutionPlanService>());
        vm.Rerunner = (mode, stmt, ct) => rerunner.RunAsync(stmt, mode, ct);
        if (container.GetService<SettingsService>() is { } settings)
        {
            vm.SaveAlwaysHint = on => _ = settings.UpdateAsync(s => s with { Execution = s.Execution with { GatherPlanStatisticsHint = on } });
        }
    }

    /// <summary>탭의 계획 모델. 창이 막 열렸으면 아직 없을 수 있어 몇 번 기다린다(창 없는 시험은 가장 최근 모델).</summary>
    private static async Task<PlanViewModel?> FindPanelAsync(SqlEditorViewModel? tab)
    {
        for (var i = 0; i < 5; i++)
        {
            if (PlanPanelViewModel.Instance is { } panel)
            {
                // 닫힌 탭의 모델을 되살리지 않는다.
                return panel.For(tab is not null && SqlEditorHost.Current.Tabs.Contains(tab) ? tab : null);
            }

            if (PlanViewModel.Current is { } vm)
            {
                return vm;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        return PlanPanelViewModel.Instance?.For(tab) ?? PlanViewModel.Current;
    }
}
