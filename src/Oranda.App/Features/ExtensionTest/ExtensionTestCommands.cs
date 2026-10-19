using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ExtensionTest;

/// <summary>TSK-10-03 AI 확장 지점 시험 패널 — 명령 ID 모음(commands.md 7장).</summary>
public static class ExtensionTestCommands
{
    public const string ViewCommandId = "oranda.view.extensionTest";

    public const string PanelId = "oranda.panel.extensionTest";
}

/// <summary>
/// 확장 시험 패널 열기(<c>oranda.view.extensionTest</c>, 개발 빌드만 — 활성 조건 <c>devBuild</c>).
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand(ExtensionTestCommands.ViewCommandId, CommandCategory.View, Title = "확장 시험 패널", Enablement = "devBuild")]
public sealed class ExtensionTestCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public ExtensionTestCommandHandler()
    {
    }

    public ExtensionTestCommandHandler(IServiceProvider services) => _services = services;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool(ExtensionTestCommands.PanelId);
        return Task.CompletedTask;
    }
}
