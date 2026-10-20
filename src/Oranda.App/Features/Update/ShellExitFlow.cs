using Oranda.App.Shell;
using Oranda.App.Views;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Update;

/// <summary><see cref="IExitFlow"/> 의 실제 구현: 셸의 종료 작업과 메인 창의 기존 닫기 흐름에 그대로 붙는다.</summary>
[OrandaService(typeof(IExitFlow))]
public sealed class ShellExitFlow(ShellController shell) : IExitFlow
{
    public void OnExit(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        shell.RegisterShutdown(_ =>
        {
            handler();
            return Task.CompletedTask;
        });
    }

    public Task<bool> RequestExitAsync() =>
        shell.MainWindow is MainWindow window ? window.CloseWithFlowAsync() : Task.FromResult(false);
}
