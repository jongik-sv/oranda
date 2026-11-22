using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dock.Model.Core;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.TableSpec;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>SG-1 통계 수집 실행 — 명령 ID.</summary>
public static class GatherStatsCommands
{
    /// <summary>스키마 브라우저 테이블 노드 「통계 수집…」.</summary>
    public const string SchemaId = "oranda.schema.gatherStats";

    /// <summary>「통계 보기」 탭의 「통계 수집 실행…」(명령 팔레트).</summary>
    public const string PanelId = "oranda.tuning.gatherStats";

    public static readonly string[] Ids = [SchemaId, PanelId];
}

/// <summary>
/// 「통계 수집…」: 확인 창(<see cref="GatherStatsDialog"/>)을 띄워 같은 접속의 전용 세션에서 <c>DBMS_STATS.GATHER_TABLE_STATS</c> 를 실행한다.
/// 스키마 브라우저 트리는 하나만 고를 수 있어 고른 테이블 하나가 대상이다. 「테이블」 목록 폴더(소유자 전체)에서는 켜지지 않는다(실수 방지).
/// 끝나면 그 테이블의 상세 캐시를 비우고 열 통계 요약·열린 「통계 보기」·테이블 창을 새로 읽는다(<see cref="GatherStatsRefresh"/>).
/// </summary>
[OrandaCommand(GatherStatsCommands.SchemaId, CommandCategory.Database, When = "schemaFocus",
    Enablement = "tableObjectSelected && hasSession && !noImplTuning && !readOnlySession", Icon = "refresh",
    Placements = ["context:schema/tableStats@73"])]
[OrandaCommand(GatherStatsCommands.PanelId, CommandCategory.Database, Enablement = TableStatsCommands.ActiveKey + " && !readOnlySession", Icon = "refresh")]
public sealed class GatherStatsCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;

    public GatherStatsCommandHandler()
    {
    }

    public GatherStatsCommandHandler(ServiceContainer container) => _container = container;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return Task.CompletedTask;
        }

        if (context.CommandId == GatherStatsCommands.PanelId)
        {
            var panel = context.Parameter as TableStatsViewModel ?? shell.Docking.ActiveDocument?.Panel as TableStatsViewModel;
            return panel is { Target: { } t, Session: { } s } ? ShowAsync(_container, s, [t]) : Task.CompletedTask;
        }

        var browser = FindSchemaBrowser(shell.Docking);
        var table = TableSpecCommandHandler.TableOf(context.Parameter as SchemaNode ?? browser?.SelectedNode);
        var session = browser?.CurrentSession ?? SessionRouter.CurrentOf(_container);
        if (table is not { ObjectType: "TABLE" } || session is null)
        {
            return Task.CompletedTask;
        }

        return ShowAsync(_container, session, [new TableStatsTarget(table.Owner, table.Name)]);
    }

    /// <summary>확인 창을 띄운다. 실행 세션 = <paramref name="session"/> 과 같은 접속의 전용 세션(비밀번호는 저장소, 없으면 묻는다).</summary>
    internal static async Task ShowAsync(ServiceContainer container, ISession session, IReadOnlyList<TableStatsTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(session);
        var state = ExecutionState.For(container.GetService<IEventBus>());
        ExecutionCommands.EnsureEngineServices(container, state);
        if (container.GetService<IExecutionService>() is not { } exec)
        {
            return;
        }

        var profile = session.Profile;
        var vm = new GatherStatsDialogViewModel(profile, targets, new GatherStatsRunner(exec),
            ct => DedicatedSessionCommands.OpenDedicatedAsync(container, profile, ct));
        vm.Completed += (_, done) => Dispatcher.UIThread.Post(() => _ = RefreshAsync(container, session, done));
        vm.BackgroundFinished += (_, summary) => Dispatcher.UIThread.Post(() => NotifyBackground(container, summary));
        var owner = container.GetService<ShellController>()?.MainWindow;
        await GatherStatsDialog.ShowAsync(owner, vm, (text, title) => SqlEditorHost.Current.NewDocument(text).Title = title).ConfigureAwait(true);
    }

    /// <summary>
    /// 통계를 새로 모은 뒤(확인 창·편집기 DBMS_STATS) 그 테이블만 새로 읽는다(<see cref="GatherStatsRefresh"/>): 상세 캐시, 열 통계 요약·속성 창,
    /// 열린 「통계 보기」와 테이블 창. <paramref name="session"/> = 그 접속의 세션(상세 캐시 키, 같은 접속 창만).
    /// </summary>
    internal static Task RefreshAsync(IServiceProvider services, ISession? session, IReadOnlyList<TableStatsTarget> done)
    {
        if (services.GetService(typeof(IShellUi)) is not IShellUi shell)
        {
            return Task.CompletedTask;
        }

        var panels = shell.Docking.Documents.Select(d => d.Panel).ToList();
        var browser = FindSchemaBrowser(shell.Docking);
        return GatherStatsRefresh.ApplyAsync(done, new GatherStatsViews
        {
            Session = session,
            Meta = services.GetService(typeof(IMetadataProvider)) as IMetadataProvider,
            ColumnStats = browser?.Properties.ColumnStats,
            Properties = browser?.Properties,
            StatsTabs = panels.OfType<TableStatsViewModel>(),
            Details = panels.OfType<ObjectDetailViewModel>(),
        }, CancellationToken.None);
    }

    /// <summary>백그라운드 수집 끝(S4-2): 스키마 브라우저 상태 줄에 요약을 남긴다(실행 이력에는 문장마다 이미 남는다).</summary>
    private static void NotifyBackground(ServiceContainer container, string summary)
    {
        if (container.GetService<IShellUi>() is { } shell && FindSchemaBrowser(shell.Docking) is { } browser)
        {
            browser.StatusText = TuningText.T("통계 수집(백그라운드) 끝: ", "Gather statistics (background) done: ") + summary;
        }
    }

    internal static SchemaBrowserViewModel? FindSchemaBrowser(DockWorkspace docking)
    {
        if (docking.Layout is not { } root)
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
}
