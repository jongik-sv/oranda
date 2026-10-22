using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
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
            container.AddInstance<IConnectionService>(service);

            // 탭 → 세션 고르기·창 테두리(환경 색)는 라우터가 맡는다. 접속 서비스와 함께 만들어 첫 접속부터 이벤트를 받는다
            // (시작 때 만들면 Oracle 어셈블리를 깨운다).
            SessionRouter.Resolve(container);
            if (container.GetService<ContextKeyService>() is { } keys)
            {
                // 세션 모니터 권한 키(kill·trace)는 마지막에 연 접속이 아니라 지금 접속의 권한을 따른다.
                service.ActiveSessionChanged += (_, _) =>
                {
                    if (service.ActiveSession is { } active)
                    {
                        SessionMonitor.SessionMonitorCommands.ApplyPrivilegeKeys(keys, active.Server.Capabilities);
                    }
                    else
                    {
                        keys.Set("canKillSession", false);
                        keys.Set("canTraceSessions", false);
                    }
                };
            }

            return service;
        }
    }

    /// <summary>
    /// 저장된 프로필로 접속한다(비밀번호가 없으면 묻는다, 접속 관리자와 같은 순서). 접속 안 됨 탭의 「다시 접속」 이 쓴다.
    /// 연 작업 세션, 프로필이 없거나 못 열었거나 사용자가 취소하면 null.
    /// </summary>
    public static async Task<ISession?> ConnectProfileAsync(ServiceContainer container, Guid profileId)
    {
        ArgumentNullException.ThrowIfNull(container);
        var service = Resolve(container);
        var doc = await service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        if (doc.Value.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile)
        {
            return null;
        }

        var owner = container.GetService<ShellController>()?.MainWindow;
        var vm = new ConnectionManagerViewModel(service)
        {
            AskPassword = p => owner is null ? Task.FromResult<string?>(null) : ConnectionDialogs.PromptPasswordAsync(owner, p),
            AskChangePassword = (p, old) => owner is null
                ? Task.FromResult<(string? Old, string New)?>(null)
                : ConnectionDialogs.ChangePasswordAsync(owner, p, old),
        };
        var outcome = await vm.ConnectWithPromptsAsync(profile).ConfigureAwait(true);
        if (outcome is { IsConnected: false, Message: { Length: > 0 } message })
        {
            ExecutionLog.For(container.GetService<IEventBus>()).AddMessage(MessageKind.Error, $"「{profile.Name}」 접속 실패: {message}");
        }

        return outcome?.IsConnected == true ? service.SessionOf(profileId) : null;
    }
}

/// <summary>접속 메뉴·명령 팔레트의 접속 명령(CONN-01~04).</summary>
[OrandaCommand("oranda.conn.manage", CommandCategory.Connection, Icon = "connect", Placements = ["menu:file/connManage@400", "toolbar:main/connManage@110"])]
[OrandaCommand("oranda.conn.switchSession", CommandCategory.Connection, Enablement = "sqlDocActive || plsqlDocActive", Placements = ["menu:file/connSwitch@410"])]
[OrandaCommand("oranda.conn.disconnect", CommandCategory.Connection, Enablement = "hasSession", Icon = "disconnect", Placements = ["menu:file/connDisconnect@420", "toolbar:main/connDisconnect@120"])]
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
                await ShowManagerAsync(service).ConfigureAwait(true);
                break;
            case "oranda.conn.switchSession":
                // 오렌지 F7: 툴바 접속 콤보를 펼친다. 열린 접속이 없으면 접속 관리자.
                if (ConnectionComboViewModel.For(container)?.OpenList() != true)
                {
                    await ShowManagerAsync(service).ConfigureAwait(true);
                }

                break;
            case "oranda.conn.disconnect":
                await DisconnectAsync(service, cancellationToken).ConfigureAwait(true);
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

    /// <summary>
    /// 보이는 접속(활성 SQL 탭의 접속, 그 밖의 문서·전용 세션 탭이면 지금 접속)을 끊는다. 미커밋이면 접속 이름을 밝혀 먼저 묻고,
    /// 취소하면 끊지 않는다(CONN-06). 활성 탭의 접속이 이미 닫혔으면 할 일이 없다.
    /// </summary>
    private async Task DisconnectAsync(IConnectionService service, CancellationToken ct)
    {
        var router = SessionRouter.Resolve(container);
        var target = router.ShownSession is { } shown && service.Sessions.Contains(shown)
            ? shown
            : router.ShownMissing is null ? service.ActiveSession : null;
        var log = ExecutionLog.For(container.GetService<IEventBus>());
        if (target is null)
        {
            log.AddMessage(MessageKind.Info, "이 탭의 접속은 이미 닫혀 있습니다.");
            return;
        }

        var execution = ExecutionCommands.Resolve(container);
        if (execution.State.IsRunning(target.Id))
        {
            log.AddMessage(MessageKind.Warning, $"「{router.LabelOf(target)}」 에서 실행 중입니다. 실행을 취소하거나 끝난 뒤 접속을 끊으세요.");
            return;
        }

        if (!await execution.ConfirmConnectionUncommittedAsync(target, ct).ConfigureAwait(true))
        {
            return;
        }

        await service.DisconnectAsync(target, ct).ConfigureAwait(true);
    }

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
        vm.ImportConnections = () => ConnectionImport.ConnectionImportHandler.RunAsync(container, window, CancellationToken.None);
        vm.ExportConnections = () => ConnectionImport.ConnectionExportHandler.RunAsync(container, window, CancellationToken.None);
        window.Closed += (_, _) => _window = null;
        _window = window;
        if (Owner is { IsVisible: true } owner)
        {
            OwnerPlacementGuard.FitInside(owner, window);
            OwnerPlacementGuard.Hold(owner, window);
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
