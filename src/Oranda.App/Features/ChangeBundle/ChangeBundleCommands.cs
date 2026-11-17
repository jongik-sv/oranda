using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>
/// 반영 묶음 창 열기(<c>oranda.view.changeBundle</c>: 도구 메뉴 「반영 묶음」·팔레트, 설계 9.2). 변경 일지(@50) 바로 아래.
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand("oranda.view.changeBundle", CommandCategory.Tools, Title = "반영 묶음", Placements = ["menu:tools/changeBundle@51"])]
public sealed class ChangeBundleCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public ChangeBundleCommandHandler()
    {
    }

    public ChangeBundleCommandHandler(IServiceProvider services) => _services = services;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool(ChangeBundleNotices.PanelId);
        return Task.CompletedTask;
    }
}
