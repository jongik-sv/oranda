using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Tuning.Cursors;

/// <summary>TSK-06-03 자식 커서 — 이 Task 가 등록하는 명령 ID.</summary>
public static class ChildCursorsCommands
{
    public static readonly string[] Ids = ["oranda.tuning.childCursors"];
}

/// <summary>
/// 「자식 커서」: sql_id 는 다른 창이 맡긴 것(<see cref="TuningNavigation.OpenChildCursorsAsync"/>) → 매개변수(<see cref="CursorRef"/>·sql_id 글)
/// → 앞에 있는 SQL 모니터의 고른 SQL(그 모니터 세션으로) → 계획 창의 마지막 커서 순으로 찾고, 없으면 빈 탭(sql_id 칸)을 연다.
/// </summary>
[OrandaCommand("oranda.tuning.childCursors", CommandCategory.Monitor, Enablement = "hasSession", Icon = "diff",
    Placements = ["menu:tuning/childCursors@210"])]
public sealed class ChildCursorsCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;

    public ChildCursorsCommandHandler()
    {
    }

    public ChildCursorsCommandHandler(ServiceContainer container) => _container = container;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return Task.CompletedTask;
        }

        var session = ExecutionCommands.ActiveTabSession(_container) ?? SessionRouter.CurrentOf(_container);
        var cursor = TuningNavigation.TakeCursor() ?? FromParameter(context.Parameter);
        if (cursor is null && shell.Docking.ActiveDocument?.Panel is SqlMonitorViewModel { SelectedSql: { } picked } monitor)
        {
            cursor = picked.Row.Cursor;
            session = monitor.MonitorSession ?? session; // SQL 모니터의 전용 세션(V$ 권한이 있는 접속)
        }

        cursor ??= PlanViewModel.Current?.LastCursor;
        return Open(_container, shell.Docking, session, cursor, cancellationToken);
    }

    /// <summary>매개변수를 커서로(<see cref="CursorRef"/> 또는 sql_id 글).</summary>
    internal static CursorRef? FromParameter(object? parameter) => parameter switch
    {
        CursorRef c => c,
        string s when ChildCursorsViewModel.IsSqlId(s.Trim()) => new CursorRef(s.Trim()),
        _ => null,
    };

    private static Task Open(ServiceContainer container, DockWorkspace docking, ISession? session, CursorRef? cursor, CancellationToken ct)
    {
        if (cursor is not null && docking.Documents.FirstOrDefault(d => d.Panel is ChildCursorsViewModel { SqlId: { } id } && id == cursor.SqlId) is { Panel: ChildCursorsViewModel existing } doc)
        {
            docking.Activate(doc);
            return existing.LoadAsync(ct);
        }

        var vm = new ChildCursorsViewModel(container) { PreferredInstance = cursor?.InstanceId };
        vm.Attach(session, cursor?.SqlId);
        Wire(container, vm);
        if (docking.OpenDocument(TuningCommands.ChildCursorsPanelId, vm, vm.Title) is not { } opened)
        {
            vm.Dispose();
            return Task.CompletedTask;
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChildCursorsViewModel.Title))
            {
                opened.Title = vm.Title;
            }
        }

        void OnClosed(object? sender, PanelDocument closed)
        {
            if (ReferenceEquals(closed, opened))
            {
                docking.DocumentClosed -= OnClosed;
                vm.PropertyChanged -= OnChanged;
                vm.Dispose(); // 「다른 접속으로 열기」로 연 세션도 닫는다.
            }
        }

        vm.PropertyChanged += OnChanged;
        docking.DocumentClosed += OnClosed;
        return cursor is null ? Task.CompletedTask : vm.LoadAsync(ct);
    }

    private static void Wire(ServiceContainer container, ChildCursorsViewModel vm)
    {
        vm.SqlTextLoader = async (s, c, ct) => container.GetService<IMonitorService>() is { } monitor
            ? (await monitor.GetSqlDetailAsync(s, c, ct).ConfigureAwait(true)).FullText
            : null;
        vm.OpenStats = (s, table) => _ = TableStatsCommandHandler.Open(container, s, table, CancellationToken.None);
        vm.OpenPlan = async (s, c) =>
        {
            if (container.GetService<IShellUi>() is not { } shell)
            {
                return;
            }

            shell.Docking.ShowTool(ExecutionPlanCommands.PanelId);
            for (var i = 0; i < 5 && PlanViewModel.Current is null; i++)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            if (PlanViewModel.Current is { } plan)
            {
                await plan.ShowCursorAsync(s, c).ConfigureAwait(true);
            }
        };
        if (container.GetService<ISessionManager>() is not null)
        {
            var opener = SessionMonitorCommands.CreateOpener(container, "childCursors");
            vm.ChooseSession = opener.OpenChosenAsync;
        }
    }
}
