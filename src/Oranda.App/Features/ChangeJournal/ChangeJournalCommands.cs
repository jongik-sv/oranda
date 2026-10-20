using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Core;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ChangeJournal;

/// <summary>
/// TSK-13-07 변경 일지 — 도구 창 열기(<c>oranda.view.changeJournal</c>: 도구 메뉴·팔레트)와 스키마 브라우저 객체 문맥 메뉴
/// 「변경 일지 보기」(<c>oranda.journal.showForObject</c>: 그 객체의 버전만 보인다).
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand("oranda.view.changeJournal", CommandCategory.Tools, Title = "변경 일지", Placements = ["menu:tools/changeJournal@50"])]
[OrandaCommand("oranda.journal.showForObject", CommandCategory.Database, Title = "변경 일지 보기", When = "schemaFocus", Enablement = "schemaNodeSelected",
    Placements = ["context:schema/changeJournal@60"])]
public sealed class ChangeJournalCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public ChangeJournalCommandHandler()
    {
    }

    public ChangeJournalCommandHandler(IServiceProvider services) => _services = services;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null)
        {
            return Task.CompletedTask;
        }

        switch (context.CommandId)
        {
            case "oranda.view.changeJournal":
                Show(null, clear: true);
                break;
            case "oranda.journal.showForObject":
                var target = context.Parameter switch
                {
                    SchemaNode n => n.Ref,
                    DbObjectRef r => r,
                    _ => FindSchemaBrowser()?.SelectedNode?.Ref,
                };
                if (target is not null)
                {
                    Show(target, clear: false);
                }

                break;
        }

        return Task.CompletedTask;
    }

    private void Show(DbObjectRef? target, bool clear)
    {
        // 패널이 아직 없으면 만들어질 때 가져가고, 이미 있으면 바로 알린다.
        ChangeJournalViewModel.PendingObject = target;
        (_services!.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.changeJournal");
        if (ChangeJournalViewModel.Active is { } active)
        {
            ChangeJournalViewModel.PendingObject = null;
            if (target is not null || clear)
            {
                active.ShowObject(target);
            }
        }
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
