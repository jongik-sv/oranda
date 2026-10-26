using Oranda.App.Features.Update;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Help;

/// <summary>도움말 &gt; 새 기능(릴리스 노트): 앱에 포함된 릴리스 노트 창을 연다(이미 열려 있으면 앞으로 가져온다).</summary>
[OrandaCommand("oranda.help.releaseNotes", CommandCategory.Help, Placements = ["menu:help/releaseNotes@880"])]
public sealed class ReleaseNotesCommandHandler(UpdateService service, ShellController shell) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ReleaseNotesWindow.ShowFor(service, shell);
        return Task.CompletedTask;
    }
}
