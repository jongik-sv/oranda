using Oranda.App.Features.Update;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Help;

/// <summary>도움말 &gt; 업데이트 확인…: 업데이트 대화상자를 연다(이미 열려 있으면 앞으로 가져오고 다시 확인한다).</summary>
[OrandaCommand("oranda.help.checkUpdate", CommandCategory.Help, Placements = ["menu:help/checkUpdate@890"])]
public sealed class CheckUpdateCommandHandler(UpdateService service, ShellController shell) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        UpdateWindow.ShowFor(service, shell);
        return Task.CompletedTask;
    }
}
