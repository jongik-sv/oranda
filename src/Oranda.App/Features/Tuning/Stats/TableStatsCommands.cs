using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Core;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.TableSpec;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.Core.Extensibility;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>TSK-06-03 통계 보기 — 이 Task 가 등록하는 명령 ID(commands.md 「튜닝」 절 통계·커서 200~).</summary>
public static class TableStatsCommands
{
    /// <summary>통계 보기 탭이 앞에 있고 표가 정해져 있으면 참(「통계 수집 스크립트 만들기」 활성).</summary>
    public const string ActiveKey = "tuningStatsActive";

    public static readonly string[] Ids =
    [
        "oranda.tuning.stats",
        "oranda.tuning.gatherStatsScript",
        "oranda.schema.tableStats",
    ];
}

/// <summary>
/// 「통계 보기」(편집기 커서 아래 표, 다른 창이 넘긴 표), 스키마 브라우저 표 문맥 메뉴의 「통계 보기」, 「통계 수집 스크립트 만들기」(새 편집기 탭에 넣기만).
/// 다른 창이 표를 넘기는 방법은 <see cref="TuningNavigation.OpenStatsAsync"/>(또는 매개변수 <see cref="TableStatsTarget"/>·<c>"OWNER.TABLE"</c>).
/// </summary>
[OrandaCommand("oranda.tuning.stats", CommandCategory.Database, Enablement = "hasSession", Icon = "table",
    Placements = ["menu:tuning/stats@200", "context:editor/tableStats@1030"])]
[OrandaCommand("oranda.tuning.gatherStatsScript", CommandCategory.Database, Enablement = TableStatsCommands.ActiveKey, Icon = "docEdit",
    Placements = ["menu:tuning/scripts/gatherStats@590"])]
[OrandaCommand("oranda.schema.tableStats", CommandCategory.Database, When = "schemaFocus", Enablement = "tableNodeSelected && hasSession", Icon = "table",
    Placements = ["context:schema/tableStats@72"])]
public sealed class TableStatsCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;
    private bool _watching;

    public TableStatsCommandHandler()
    {
    }

    public TableStatsCommandHandler(ServiceContainer container) => _container = container;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return Task.CompletedTask;
        }

        Watch(shell.Docking);
        switch (context.CommandId)
        {
            case "oranda.tuning.gatherStatsScript":
                (context.Parameter as TableStatsViewModel ?? shell.Docking.ActiveDocument?.Panel as TableStatsViewModel)?.OpenGatherScript();
                return Task.CompletedTask;
            case "oranda.schema.tableStats":
            {
                var browser = FindSchemaBrowser(shell.Docking);
                var table = TableSpecCommandHandler.TableOf(context.Parameter as SchemaNode ?? browser?.SelectedNode);
                var session = browser?.CurrentSession ?? SessionRouter.CurrentOf(_container);
                if (table is not null)
                {
                    return Open(_container, session, new TableStatsTarget(table.Owner, table.Name), cancellationToken);
                }

                return Task.CompletedTask;
            }

            default:
            {
                var session = ExecutionCommands.ActiveTabSession(_container) ?? SessionRouter.CurrentOf(_container);
                var target = WithOwner(TuningNavigation.TakeStats(), session) ?? FromParameter(context.Parameter, session) ?? EditorTable(_container, session);
                return Open(_container, session, target, cancellationToken);
            }
        }
    }

    /// <summary>통계 보기 탭을 연다(같은 표의 탭이 있으면 그 탭으로 가서 다시 읽는다).</summary>
    internal static Task Open(ServiceContainer container, ISession? session, TableStatsTarget? target, CancellationToken ct)
    {
        if (container.GetService<IShellUi>() is not { } shell)
        {
            return Task.CompletedTask;
        }

        if (target is not null && shell.Docking.Documents.FirstOrDefault(d => d.Panel is TableStatsViewModel { Target: { } t } && t == target) is { Panel: TableStatsViewModel existing } doc)
        {
            shell.Docking.Activate(doc);
            return existing.LoadAsync(ct);
        }

        var vm = new TableStatsViewModel(container);
        vm.Attach(session, target);
        vm.EditorTable = () => EditorTable(container, vm.Session);
        if (shell.Docking.OpenDocument(TuningCommands.StatsPanelId, vm, vm.Title) is not { } opened)
        {
            return Task.CompletedTask;
        }

        // 표가 바뀌면 탭 제목을 맞추고, 탭이 닫히면 읽기를 멈춘다.
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TableStatsViewModel.Title))
            {
                opened.Title = vm.Title;
            }

            if (e.PropertyName == nameof(TableStatsViewModel.Target))
            {
                UpdateKey(container, shell.Docking);
            }
        }

        void OnClosed(object? sender, PanelDocument closed)
        {
            if (ReferenceEquals(closed, opened))
            {
                shell.Docking.DocumentClosed -= OnClosed;
                vm.PropertyChanged -= OnChanged;
                vm.Dispose();
            }
        }

        vm.PropertyChanged += OnChanged;
        shell.Docking.DocumentClosed += OnClosed;
        UpdateKey(container, shell.Docking);
        return target is null ? Task.CompletedTask : vm.LoadAsync(ct);
    }

    /// <summary>매개변수(<see cref="TableStatsTarget"/>·<c>"OWNER.TABLE"</c>)를 표로.</summary>
    internal static TableStatsTarget? FromParameter(object? parameter, ISession? session) => parameter switch
    {
        TableStatsTarget t => t,
        string s => TableStatsTarget.Parse(s, session?.Server.SessionUser ?? session?.Profile.User),
        _ => null,
    };

    /// <summary>소유자가 빈 표면 접속 계정으로 채운다(계정도 모르면 null).</summary>
    internal static TableStatsTarget? WithOwner(TableStatsTarget? target, ISession? session)
    {
        if (target is not { Owner.Length: 0 })
        {
            return target;
        }

        var owner = session?.Server.SessionUser ?? session?.Profile.User;
        return string.IsNullOrWhiteSpace(owner) ? null : target with { Owner = owner.Trim().ToUpperInvariant() };
    }

    /// <summary>활성 편집기 커서 아래 표. 없으면 null.</summary>
    internal static TableStatsTarget? EditorTable(ServiceContainer container, ISession? session)
    {
        if (container.GetService<IEditorContext>()?.GetActiveDocument() is not { } doc)
        {
            return null;
        }

        var statement = StatementSplitter.GetStatementAt(doc.Text, doc.CaretOffset, SplitOptions.Default);
        return EditorTableLocator.Find(doc.Text, doc.CaretOffset, statement?.Text, session?.Server.SessionUser ?? session?.Profile.User);
    }

    private void Watch(DockWorkspace docking)
    {
        if (_watching || _container is null)
        {
            return;
        }

        _watching = true;
        var container = _container;
        docking.ActiveDocumentChanged += (_, _) => UpdateKey(container, docking);
    }

    private static void UpdateKey(ServiceContainer container, DockWorkspace docking) =>
        container.GetService<ContextKeyService>()?.Set(TableStatsCommands.ActiveKey, docking.ActiveDocument?.Panel is TableStatsViewModel { Target: not null });

    private static SchemaBrowserViewModel? FindSchemaBrowser(DockWorkspace docking)
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
