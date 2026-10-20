using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Core;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.TableSpec;

/// <summary>
/// TSK-13-06 테이블 정의서 명령 처리기. 표 문맥 메뉴는 고른 표 하나로, 도구 메뉴는 소유자·이름 패턴으로 정의서 화면을 연다.
/// 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaCommand("oranda.schema.tableSpec", CommandCategory.Database, When = "schemaFocus", Enablement = "tableNodeSelected && hasSession",
    Placements = ["context:schema/tableSpec@70"])]
[OrandaCommand("oranda.tools.tableSpec", CommandCategory.Tools, Enablement = "hasSession",
    Placements = ["menu:tools/tableSpec@50"])]
public sealed class TableSpecCommandHandler : ICommandHandler
{
    public const string PanelId = "oranda.panel.tableSpec";

    private readonly IServiceProvider? _services;

    public TableSpecCommandHandler()
    {
    }

    public TableSpecCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 문서 탭을 여는 방법(새 ViewModel, 제목 → 열렸는지)을 바꾼다. 기본 = 셸 도킹의 <c>OpenDocument</c>.</summary>
    internal Func<TableSpecViewModel, string, bool>? OpenDocument { get; set; }

    /// <summary>시험용: 지금 세션을 바꾼다.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null && OpenDocument is null)
        {
            return Task.CompletedTask;
        }

        var schemaVm = FindSchemaBrowser();
        var session = SessionProvider?.Invoke() ?? schemaVm?.CurrentSession
            ?? ConnectionManager.SessionRouter.CurrentOf(_services);
        if (session is null)
        {
            return Task.CompletedTask;
        }

        DbObjectRef? table = null;
        if (context.CommandId == "oranda.schema.tableSpec")
        {
            table = TableOf(context.Parameter as SchemaNode ?? schemaVm?.SelectedNode);
            if (table is null)
            {
                return Task.CompletedTask;
            }
        }

        var vm = _services is null ? new TableSpecViewModel() : new TableSpecViewModel(_services);
        vm.Attach(session, table);
        var title = table is null ? "테이블 정의서" : $"테이블 정의서 {table.Name}";
        _ = OpenDocument?.Invoke(vm, title)
            ?? (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.OpenDocument(PanelId, vm, title) is not null;
        return Task.CompletedTask;
    }

    /// <summary>표·뷰 노드이거나 그 하위 노드(<c>tableNodeSelected</c> 와 같은 범위)면 그 객체.</summary>
    internal static DbObjectRef? TableOf(SchemaNode? node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is { Kind: SchemaNodeKind.Item, Ref: { } r } && SchemaNode.IsTableLike(r.ObjectType))
            {
                return r;
            }
        }

        return null;
    }

    private SchemaBrowserViewModel? FindSchemaBrowser()
    {
        if (_services?.GetService(typeof(IShellUi)) is not IShellUi { Docking.Layout: { } root })
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

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="TableSpecCommandHandler"/>.</summary>
public static class TableSpecCommands
{
}
