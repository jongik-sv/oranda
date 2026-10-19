using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// TSK-03-04 접속 관리자 화면 — 명령 처리기. 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 셸이 자동 수집한다(docs/design/ui-contract.md).
/// 명령 ID 는 commands.md 7장 「소유 Task = TSK-03-04」 행과 같다.
/// </summary>
public static class ConnectionManagerCommands
{
    private static readonly object Gate = new();

    /// <summary>이 파일이 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids =
    [
        "oranda.conn.manage",
        "oranda.conn.switchSession",
        "oranda.conn.disconnect",
        "oranda.conn.reconnect",
        "oranda.conn.changePassword",
    ];

    /// <summary>
    /// 접속 서비스를 컨테이너에서 꺼낸다. 처음이면 만들어 등록한다(같은 서비스를 다른 기능이 <see cref="IConnectionService"/> 로 받는다).
    /// 셸 조립 파일을 고치지 않고 이 Task 안에서 끝내려는 방법이다.
    /// </summary>
    public static IConnectionService Resolve(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        lock (Gate)
        {
            if (container.GetService<IConnectionService>() is { } existing)
            {
                return existing;
            }

            var service = new ConnectionService(
                container.GetRequiredService<IConnectionStore>(),
                container.GetRequiredService<ICredentialStore>(),
                container.GetRequiredService<ISessionManager>(),
                container.GetRequiredService<IAppDataPaths>(),
                container.GetRequiredService<IEventBus>(),
                keys: container.GetService<ContextKeyService>());
            if (container.GetService<ShellController>() is { } shell)
            {
                // 접속은 스레드 풀에서 끝나므로 창(환경 테두리)을 바꾸는 일은 UI 스레드로 넘긴다.
                service.ActiveSessionChanged += (_, _) => Dispatcher.UIThread.Post(() => shell.ActiveEnvironment = service.ActiveSession?.Profile.Environment);
            }

            container.AddInstance<IConnectionService>(service);
            return service;
        }
    }
}

/// <summary>접속 메뉴·명령 팔레트의 접속 명령(CONN-01~04).</summary>
[OrandaCommand("oranda.conn.manage", CommandCategory.Connection, Placements = ["menu:file/connManage@400", "toolbar:main/connManage@10"])]
[OrandaCommand("oranda.conn.switchSession", CommandCategory.Connection, Enablement = "sqlDocActive || plsqlDocActive", Placements = ["menu:file/connSwitch@410"])]
[OrandaCommand("oranda.conn.disconnect", CommandCategory.Connection, Enablement = "hasSession", Placements = ["menu:file/connDisconnect@420"])]
[OrandaCommand("oranda.conn.reconnect", CommandCategory.Connection, Enablement = "sessionBroken", Placements = ["menu:file/connReconnect@430"])]
[OrandaCommand("oranda.conn.changePassword", CommandCategory.Connection, Enablement = "hasSession", Placements = ["menu:file/connChangePassword@440"])]
public sealed class ConnectionCommandHandler(ServiceContainer container) : ICommandHandler
{
    private ConnectionManagerWindow? _window;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var service = ConnectionManagerCommands.Resolve(container);
        switch (context.CommandId)
        {
            case "oranda.conn.manage":
            case "oranda.conn.switchSession":
                await ShowManagerAsync(service).ConfigureAwait(true);
                break;
            case "oranda.conn.disconnect":
                await service.DisconnectAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.conn.reconnect":
                await new ConnectionManagerViewModel(service) { AskPassword = Prompt }.ReconnectAsync().ConfigureAwait(true);
                break;
            case "oranda.conn.changePassword":
                await ChangePasswordAsync(service, cancellationToken).ConfigureAwait(true);
                break;
        }
    }

    private Window? Owner => container.GetService<ShellController>()?.MainWindow;

    private Task<string?> Prompt(Oranda.Core.Connection.ConnectionProfile profile) =>
        Owner is { } owner ? ConnectionDialogs.PromptPasswordAsync(owner, profile) : Task.FromResult<string?>(null);

    private async Task ShowManagerAsync(IConnectionService service)
    {
        if (_window is { } open)
        {
            open.Activate();
            return;
        }

        var vm = new ConnectionManagerViewModel(service);
        await vm.LoadAsync().ConfigureAwait(true);
        var window = new ConnectionManagerWindow { DataContext = vm };
        window.Closed += (_, _) => _window = null;
        _window = window;
        if (Owner is { IsVisible: true } owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    private async Task ChangePasswordAsync(IConnectionService service, CancellationToken ct)
    {
        if (service.LastProfile is not { } profile || Owner is not { } owner)
        {
            return;
        }

        if (await ConnectionDialogs.ChangePasswordAsync(owner, profile, null).ConfigureAwait(true) is not { } request)
        {
            return;
        }

        var result = await service.ChangePasswordAsync(profile, request.Old, request.New, ct).ConfigureAwait(true);
        await ConnectionDialogs.MessageAsync(owner, result.Message).ConfigureAwait(true);
    }
}
