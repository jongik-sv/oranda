using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Monitoring;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LockMonitor;

/// <summary>Lock 모니터 패널·명령 ID(TSK-05-01 계약, 처리기 TSK-05-03). <see cref="ShowHistory"/> 는 TSK-05-04 가 등록한다.</summary>
public static class LockMonitorCommands
{
    public const string PanelId = "oranda.panel.lockMonitor";

    public const string Open = "oranda.monitor.locks";
    public const string Refresh = "oranda.monitor.locks.refresh";
    public const string ToggleAutoRefresh = "oranda.monitor.locks.toggleAutoRefresh";
    public const string KillHolder = "oranda.monitor.locks.killHolder";
    public const string DisconnectHolder = "oranda.monitor.locks.disconnectHolder";
    public const string ShowSql = "oranda.monitor.locks.showSql";
    public const string ShowLockedRow = "oranda.monitor.locks.showLockedRow";
    public const string ShowDdlLocks = "oranda.monitor.locks.showDdlLocks";
    public const string GoToSession = "oranda.monitor.locks.goToSession";
    public const string ExportCsv = "oranda.monitor.locks.exportCsv";
    public const string ShowHistory = "oranda.monitor.locks.showHistory";

    public static readonly string[] Ids =
    [
        Open, Refresh, ToggleAutoRefresh, KillHolder, DisconnectHolder, ShowSql, ShowLockedRow,
        ShowDdlLocks, GoToSession, ExportCsv, ShowHistory,
    ];

    /// <summary>이 Task(05-03)가 처리기를 단 명령(<see cref="ShowHistory"/> 는 05-04 몫).</summary>
    public static readonly string[] HandledIds = [.. Ids.Where(i => i != ShowHistory)];
}

/// <summary>「모니터」 메뉴의 Lock 모니터 명령(LK-01~09). 세션 모니터 다음 자리(@15), 창 명령은 Lock 모니터가 열려 있을 때만 보인다.</summary>
[OrandaCommand(LockMonitorCommands.Open, CommandCategory.Monitor, Icon = "lock", Placements = ["menu:monitor/locks@15"])]
[OrandaCommand(LockMonitorCommands.Refresh, CommandCategory.Monitor, Icon = "refresh", When = "lockMonitorFocus", Enablement = "lockMonitorOpen",
    Placements = ["menu:monitor/locks.refresh@300", "context:lockMonitor/refresh@10"])]
[OrandaCommand(LockMonitorCommands.ToggleAutoRefresh, CommandCategory.Monitor, Icon = "timer", When = "lockMonitorFocus", Enablement = "lockMonitorOpen",
    Placements = ["menu:monitor/locks.autoRefresh@310"])]
[OrandaCommand(LockMonitorCommands.ShowSql, CommandCategory.Monitor, When = "lockMonitorFocus", Enablement = "lockSessionSelected",
    Placements = ["menu:monitor/locks.showSql@400", "context:lockMonitor/showSql@20"])]
[OrandaCommand(LockMonitorCommands.ShowLockedRow, CommandCategory.Monitor, When = "lockMonitorFocus", Enablement = "lockWaiterSelected",
    Placements = ["menu:monitor/locks.showLockedRow@410", "context:lockMonitor/showLockedRow@21"])]
[OrandaCommand(LockMonitorCommands.ShowDdlLocks, CommandCategory.Monitor, When = "lockMonitorFocus", Enablement = "lockMonitorOpen && canQueryDdlLocks",
    Placements = ["menu:monitor/locks.showDdlLocks@420"])]
[OrandaCommand(LockMonitorCommands.KillHolder, CommandCategory.Monitor, Icon = "kill", When = "lockMonitorFocus", Enablement = "lockSessionSelected && lockCanKillSession",
    Placements = ["menu:monitor/locks.kill@500", "context:lockMonitor/kill@30"])]
[OrandaCommand(LockMonitorCommands.DisconnectHolder, CommandCategory.Monitor, Icon = "disconnect", When = "lockMonitorFocus", Enablement = "lockSessionSelected && lockCanKillSession",
    Placements = ["menu:monitor/locks.disconnect@510", "context:lockMonitor/disconnect@31"])]
[OrandaCommand(LockMonitorCommands.GoToSession, CommandCategory.Monitor, Icon = "sessions", When = "lockMonitorFocus", Enablement = "lockSessionSelected",
    Placements = ["menu:monitor/locks.goToSession@520", "context:lockMonitor/goToSession@40"])]
[OrandaCommand(LockMonitorCommands.ExportCsv, CommandCategory.Monitor, Icon = "exportFile", When = "lockMonitorFocus", Enablement = "lockMonitorOpen",
    Placements = ["menu:monitor/locks.exportCsv@600"])]
public sealed class LockMonitorCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;

    public LockMonitorCommandHandler()
    {
    }

    public LockMonitorCommandHandler(ServiceContainer container)
    {
        _container = container;
    }

    /// <summary>CSV 저장(창이 붙어 있을 때 View 가 넣는다). 명령 팔레트·메뉴에서 부른다.</summary>
    internal static Func<LockMonitorViewModel, Task>? SaveCsv { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        if (context.CommandId == LockMonitorCommands.Open)
        {
            await OpenAsync(shell.Docking, cancellationToken).ConfigureAwait(true);
            return;
        }

        if ((context.Parameter as LockMonitorViewModel ?? shell.Docking.ActiveDocument?.Panel as LockMonitorViewModel
                ?? shell.Docking.Documents.Select(d => d.Panel).OfType<LockMonitorViewModel>().FirstOrDefault()) is not { } vm)
        {
            return;
        }

        switch (context.CommandId)
        {
            case LockMonitorCommands.Refresh:
                await vm.RefreshAsync(cancellationToken).ConfigureAwait(true);
                break;
            case LockMonitorCommands.ToggleAutoRefresh:
                vm.AutoRefresh = !vm.AutoRefresh;
                break;
            case LockMonitorCommands.ShowSql:
                await vm.ShowDetailAsync(LockMonitorViewModel.SqlTab, cancellationToken).ConfigureAwait(true);
                break;
            case LockMonitorCommands.ShowLockedRow:
                await vm.ShowDetailAsync(LockMonitorViewModel.RowTab, cancellationToken).ConfigureAwait(true);
                break;
            case LockMonitorCommands.ShowDdlLocks:
                vm.TopTabIndex = LockMonitorViewModel.DdlTab;
                await vm.LoadDdlLocksAsync(cancellationToken).ConfigureAwait(true);
                break;
            case LockMonitorCommands.KillHolder:
                await vm.KillAsync(cancellationToken).ConfigureAwait(true);
                break;
            case LockMonitorCommands.DisconnectHolder:
                await vm.DisconnectAsync(cancellationToken).ConfigureAwait(true);
                break;
            case LockMonitorCommands.GoToSession:
                await vm.GoToSessionAsync().ConfigureAwait(true);
                break;
            case LockMonitorCommands.ExportCsv:
                if (SaveCsv is { } save)
                {
                    await save(vm).ConfigureAwait(true);
                }

                break;
        }
    }

    private async Task OpenAsync(Shell.Docking.DockWorkspace docking, CancellationToken ct)
    {
        var container = _container!;
        if (docking.OpenDocument(LockMonitorCommands.PanelId) is not { Panel: LockMonitorViewModel vm } doc)
        {
            return;
        }

        if (vm.OpenSession is null)
        {
            Wire(container, vm);

            // 문서 탭이 닫히면 모니터 세션도 닫는다.
            void OnLayout(object? sender, EventArgs e)
            {
                if (!docking.Documents.Contains(doc))
                {
                    docking.LayoutChanged -= OnLayout;
                    docking.ActiveDocumentChanged -= OnLayout;
                    _ = vm.StopAsync();
                }
            }

            docking.LayoutChanged += OnLayout;
            docking.ActiveDocumentChanged += OnLayout;
        }

        await vm.StartAsync(ct).ConfigureAwait(true);
    }

    /// <summary>세션 열기·확인 창·이동·계획 보기를 셸에 잇는다.</summary>
    internal static void Wire(ServiceContainer container, LockMonitorViewModel vm)
    {
        var opener = SessionMonitorCommands.CreateOpener(container, "lockMonitor");
        vm.OpenSession = opener.OpenDefaultAsync;
        vm.ChooseSession = opener.OpenChosenAsync;
        vm.Remember = opener.Remember;
        vm.ConfirmKill ??= request => container.GetService<ShellController>()?.MainWindow is { } owner
            ? LockKillDialog.ShowAsync(owner, request)
            : Task.FromResult(false);
        vm.OpenConnectionManager ??= () => ShellCommandRunner.RunAsync("oranda.conn.manage");
        vm.GoToSessionMonitor ??= async key =>
        {
            await ShellCommandRunner.RunAsync("oranda.monitor.sessions").ConfigureAwait(true);
            if (container.GetService<IShellUi>()?.Docking.Documents.Select(d => d.Panel).OfType<SessionMonitorViewModel>().FirstOrDefault() is { } sessions)
            {
                sessions.SelectWhenListed(key);
            }
        };
        vm.ShowPlan ??= async (session, cursor, ct) =>
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShellCommandRunner.RunAsync("oranda.view.plan"));
            for (var i = 0; i < 5 && PlanViewModel.Current is null; i++)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            }

            if (PlanViewModel.Current is { } plan)
            {
                await plan.ShowCursorAsync(session, cursor, ct).ConfigureAwait(true);
            }
        };
    }
}
