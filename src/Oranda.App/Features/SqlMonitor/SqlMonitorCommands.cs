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
}

/// <summary>모니터 메뉴의 SQL 모니터 명령(SQLM-01·02).</summary>
[OrandaCommand("oranda.monitor.sql", CommandCategory.Monitor, Enablement = "hasSession && !noMonitorPrivilege",
    Placements = ["menu:monitor/sql@20"])]
[OrandaCommand("oranda.monitor.sqlSearch", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSqlOpen",
    Placements = ["menu:monitor/sqlSearch@300"])]
[OrandaCommand("oranda.monitor.sqlShowPlan", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSqlSelected",
    Placements = ["menu:monitor/sqlShowPlan@310", "context:monitor/sqlShowPlan@50"])]
[OrandaCommand("oranda.monitor.sqlShowBinds", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSqlSelected",
    Placements = ["menu:monitor/sqlShowBinds@320", "context:monitor/sqlShowBinds@51"])]
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

        if ((context.Parameter as SqlMonitorViewModel ?? shell.Docking.ActiveDocument?.Panel as SqlMonitorViewModel) is not { } vm)
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
        }
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
            vm.OpenSession = c => SessionMonitorCommands.OpenMonitorSessionAsync(container, c);

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
