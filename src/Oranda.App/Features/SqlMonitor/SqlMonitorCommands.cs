using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Shell;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>TSK-09-02 SQL 모니터 — 명령 ID(commands.md 7장 「모니터」)와 문서 종류 ID. 처리기는 <see cref="SqlMonitorCommandHandler"/>.</summary>
public static class SqlMonitorCommands
{
    /// <summary>문서 종류 ID.</summary>
    public const string PanelId = "oranda.panel.sqlMonitor";

    /// <summary>이 Task 가 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids =
    [
        "oranda.monitor.sql",
        "oranda.monitor.sqlSearch",
        "oranda.monitor.sqlShowPlan",
        "oranda.monitor.sqlShowBinds",
    ];

    /// <summary>TSK-06-06 이 등록하는 튜닝 메뉴 「대기·이력」 명령(<c>TuningCommands.Ids</c> 에도 있다).</summary>
    public static readonly string[] TuningIds =
    [
        "oranda.tuning.ashWaits",
        "oranda.tuning.awrHistory",
        "oranda.tuning.planControls",
        "oranda.tuning.tuningAdvisorScript",
        "oranda.tuning.baselineScript",
    ];
}

/// <summary>튜닝 메뉴의 SQL 모니터 명령(SQLM-01·02).</summary>
[OrandaCommand("oranda.monitor.sql", CommandCategory.Monitor, Icon = "sqlMonitor", Placements = ["menu:tuning/sql@100", "toolbar:main/sqlMonitor@520"])]
[OrandaCommand("oranda.monitor.sqlSearch", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSqlOpen",
    Placements = ["menu:tuning/sqlSearch@110"])]
[OrandaCommand("oranda.monitor.sqlShowPlan", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSqlSelected",
    Placements = ["menu:tuning/sqlShowPlan@120", "context:monitor/sqlShowPlan@50"])]
[OrandaCommand("oranda.monitor.sqlShowBinds", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSqlSelected",
    Placements = ["menu:tuning/sqlShowBinds@130", "context:monitor/sqlShowBinds@51"])]
// TSK-06-06 튜닝 메뉴 「대기·이력」(500~): SQL 모니터를 열고 그 탭으로 / 스크립트 만들기 하위 메뉴(새 편집기 탭에 글만, 실행 안 함).
[OrandaCommand("oranda.tuning.ashWaits", CommandCategory.Monitor, Icon = "aggregate", Placements = ["menu:tuning/sqlWaits@500"])]
[OrandaCommand("oranda.tuning.awrHistory", CommandCategory.Monitor, Icon = "history", Placements = ["menu:tuning/awrHistory@510"])]
[OrandaCommand("oranda.tuning.planControls", CommandCategory.Monitor, Icon = "key", Placements = ["menu:tuning/planControls@520"])]
[OrandaCommand("oranda.tuning.tuningAdvisorScript", CommandCategory.Monitor, Icon = "docEdit", Enablement = "monitorSqlSelected",
    Placements = ["menu:tuning/scripts/tuningAdvisor@540"])]
[OrandaCommand("oranda.tuning.baselineScript", CommandCategory.Monitor, Icon = "docEdit", Enablement = "monitorSqlSelected",
    Placements = ["menu:tuning/scripts/baseline@550"])]
public sealed class SqlMonitorCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;

    public SqlMonitorCommandHandler()
    {
    }

    public SqlMonitorCommandHandler(ServiceContainer container)
    {
        _container = container;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        if (context.CommandId == "oranda.monitor.sql")
        {
            await OpenAsync(shell.Docking, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (TabFor(context.CommandId) is { } tab)
        {
            // 열린 SQL 모니터가 있으면 그 탭으로, 없으면 연다.
            if (FindOpen(shell.Docking) is { } open)
            {
                shell.Docking.Activate(open.Document);
                open.Vm.DetailTabIndex = tab;
                return;
            }

            await OpenAsync(shell.Docking, cancellationToken).ConfigureAwait(true);
            if (FindOpen(shell.Docking) is { } opened)
            {
                opened.Vm.DetailTabIndex = tab;
            }

            return;
        }

        if ((context.Parameter as SqlMonitorViewModel ?? shell.Docking.ActiveDocument?.Panel as SqlMonitorViewModel ?? FindOpen(shell.Docking)?.Vm) is not { } vm)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.monitor.sqlSearch":
                await vm.SearchAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.monitor.sqlShowPlan":
                await vm.ShowPlanAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.monitor.sqlShowBinds":
                await vm.ShowBindsAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.tuning.tuningAdvisorScript":
                vm.CreateTuningAdvisorScript();
                break;
            case "oranda.tuning.baselineScript":
                await vm.CreateBaselineScriptAsync(cancellationToken).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>튜닝 메뉴 명령이 여는 아래 탭(해당 없으면 null).</summary>
    public static int? TabFor(string commandId) => commandId switch
    {
        "oranda.tuning.ashWaits" => SqlMonitorViewModel.WaitsTab,
        "oranda.tuning.awrHistory" => SqlMonitorViewModel.AwrTab,
        "oranda.tuning.planControls" => SqlMonitorViewModel.ControlsTab,
        _ => null,
    };

    private static (Shell.Docking.PanelDocument Document, SqlMonitorViewModel Vm)? FindOpen(Shell.Docking.DockWorkspace docking)
    {
        if (docking.ActiveDocument is { Panel: SqlMonitorViewModel active } current)
        {
            return (current, active);
        }

        foreach (var d in docking.Documents)
        {
            if (d.Panel is SqlMonitorViewModel vm)
            {
                return (d, vm);
            }
        }

        return null;
    }

    private async Task OpenAsync(Shell.Docking.DockWorkspace docking, CancellationToken ct)
    {
        var container = _container!;
        if (docking.OpenDocument(SqlMonitorCommands.PanelId) is not { Panel: SqlMonitorViewModel vm } doc)
        {
            return;
        }

        if (vm.OpenSession is null)
        {
            var opener = SessionMonitorCommands.CreateOpener(container, "sqlMonitor");
            vm.OpenSession = opener.OpenDefaultAsync;
            vm.ChooseSession = opener.OpenChosenAsync;
            vm.Remember = opener.Remember;

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
}
