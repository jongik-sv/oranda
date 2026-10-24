namespace Oranda.App.Features.LockMonitor;

/// <summary>Lock 모니터 패널·명령 ID(TSK-05-01 계약). 처리기·특성은 화면 Task(TSK-05-03·04)가 더한다.</summary>
public static class LockMonitorCommands
{
    public const string PanelId = "oranda.panel.lockMonitor";

    public const string Open = "oranda.monitor.locks";
    public const string Refresh = "oranda.monitor.locks.refresh";
    public const string ToggleAutoRefresh = "oranda.monitor.locks.toggleAutoRefresh";
    public const string KillHolder = "oranda.monitor.locks.killHolder";
    public const string ShowSql = "oranda.monitor.locks.showSql";
    public const string ShowLockedRow = "oranda.monitor.locks.showLockedRow";
    public const string ShowDdlLocks = "oranda.monitor.locks.showDdlLocks";
    public const string GoToSession = "oranda.monitor.locks.goToSession";
    public const string ExportCsv = "oranda.monitor.locks.exportCsv";
    public const string ShowHistory = "oranda.monitor.locks.showHistory";

    public static readonly string[] Ids =
    [
        Open, Refresh, ToggleAutoRefresh, KillHolder, ShowSql, ShowLockedRow,
        ShowDdlLocks, GoToSession, ExportCsv, ShowHistory,
    ];
}
