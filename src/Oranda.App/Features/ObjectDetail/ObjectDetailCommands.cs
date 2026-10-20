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

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// TSK-07-03 객체 상세 열기·DDL 보기 명령 처리기 (commands.md 7장 「데이터베이스」).
/// 대상 객체는 명령 인자(<see cref="SchemaNode"/> 또는 <see cref="DbObjectRef"/>), 없으면 스키마 브라우저의 선택 노드.
/// 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaCommand("oranda.schema.openDetail", CommandCategory.Database, When = "schemaFocus", Enablement = "schemaNodeSelected",
    Placements = ["context:schema/detail@40"])]
[OrandaCommand("oranda.schema.showDdl", CommandCategory.Database, When = "schemaFocus", Enablement = "schemaNodeSelected",
    Placements = ["context:schema/detail@41"])]
public sealed class ObjectDetailCommandHandler : ICommandHandler
{
    public const string PanelId = "oranda.panel.objectDetail";

    private readonly IServiceProvider? _services;

    public ObjectDetailCommandHandler()
    {
    }

    public ObjectDetailCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 문서 탭을 여는 방법을 바꾼다(기본 = 셸 도킹의 <c>OpenDocument</c>).</summary>
    internal Func<ObjectDetailViewModel, string, bool>? OpenDocument { get; set; }

    /// <summary>시험용: 지금 세션을 바꾼다.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (_services is null && OpenDocument is null)
        {
            return;
        }

        var schemaVm = FindSchemaBrowser();
        var target = context.Parameter switch
        {
            SchemaNode n => n.Ref,
            DbObjectRef r => r,
            _ => schemaVm?.SelectedNode?.Ref,
        };
        var session = SessionProvider?.Invoke() ?? schemaVm?.CurrentSession
            ?? (_services?.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions.LastOrDefault(s => s.Purpose == SessionPurpose.Work);
        if (target is null || session is null)
        {
            return;
        }

        var vm = _services is null ? new ObjectDetailViewModel() : new ObjectDetailViewModel(_services);
        var title = $"{target.Owner}.{target.Name}";
        var opened = OpenDocument?.Invoke(vm, title)
            ?? (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.OpenDocument(PanelId, vm, title) is not null;
        if (!opened)
        {
            return;
        }

        CloseDataWithDocument(vm);
        await vm.OpenAsync(session, target, context.CommandId == "oranda.schema.showDdl", cancellationToken);
    }

    /// <summary>문서 탭을 닫으면 「데이터」 탭의 결과(열린 커서)도 닫는다.</summary>
    private void CloseDataWithDocument(ObjectDetailViewModel vm)
    {
        if (OpenDocument is not null || _services?.GetService(typeof(IShellUi)) is not IShellUi { Docking: { } docking })
        {
            return;
        }

        void OnClosed(object? sender, PanelDocument doc)
        {
            if (ReferenceEquals(doc.Panel, vm))
            {
                docking.DocumentClosed -= OnClosed;
                vm.Data.Close();
            }
        }

        docking.DocumentClosed += OnClosed;
    }

    /// <summary>도킹 트리에서 스키마 브라우저 도구 창의 ViewModel 을 찾는다(없으면 null).</summary>
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

/// <summary>객체 상세의 바깥 연결 지점(ui-contract 소유 파일 규약). 명령 처리기는 <see cref="ObjectDetailCommandHandler"/>.</summary>
public static class ObjectDetailCommands
{
    /// <summary>
    /// 「데이터」 탭의 실행 쪽: 상세 문서의 세션으로 데이터 질의·거르기·행 수 세기를 돌린다.
    /// 실행 화면(<c>ExecutionCommands.Install</c>)이 채운다(실행 엔진은 부를 때 해석한다). 비어 있으면 데이터를 읽을 수 없다고 안내한다.
    /// </summary>
    public static Func<ISession, ObjectDataRunner>? DataRunner { get; set; }
}
