using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Bridge;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Shell;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 「외부 에이전트 연결…」(설계서 §3.1, 명령 <c>oranda.agent.external.connect</c>). 연결 창을 연다.
/// 메뉴에서는 뺐다(ai-settings-one-place 2026-11-24): 설정 「AI」 탭의 「외부 에이전트」 묶음 「연결…」 과 명령 팔레트로 닿는다.
/// 이 형은 <c>Features.Ai</c> 네임스페이스라 aiTuning 기능이 꺼져 있으면 메뉴에 나오지 않는다(E-21: 다리도 그 기능에서만 열린다).
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand(ExternalAgentConnectCommands.Connect, CommandCategory.Tools, Title = "외부 에이전트 연결…")]
public sealed class ExternalAgentConnectCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public ExternalAgentConnectCommandHandler()
    {
    }

    public ExternalAgentConnectCommandHandler(IServiceProvider services) => _services = services;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        _services is null ? Task.CompletedTask : ExternalAgentConnectCommands.ShowAsync(_services, cancellationToken);
}

/// <summary>연결 창 명령 id 와 여는 길.</summary>
public static class ExternalAgentConnectCommands
{
    public const string Connect = "oranda.agent.external.connect";

    /// <summary>창이 쓸 부품을 컨테이너에서 모은다. 허가 저장소가 없으면 null(조립이 덜 된 시험 등).</summary>
    public static ExternalAgentConnectServices? ServicesFrom(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService(typeof(IAgentGrants)) is not IAgentGrants grants)
        {
            return null;
        }

        var connections = services.GetService(typeof(IConnectionService)) as IConnectionService;
        var link = services.GetRegisteredOrDefault<IExternalAgentLink>() as ExternalAgentLink;
        // 접속 목록은 공유 허용 수준 한 길에서 읽는다. 이 창은 수준을 바꾸지 않는다(설정 「AI」 탭 ① 한곳, ai-settings-one-place).
        return new ExternalAgentConnectServices(
            ProfileAccess(connections is null ? null : SharedAiAccessService.FromConnections(connections, bus: null)),
            grants,
            StartBridge: () => (services.GetService(typeof(IAgentBridgeHost)) as IAgentBridgeHost)?.Start(),
            Bind: () => link?.Bind(),
            Label: () => SqlEditorHost.Current.ActiveWorkspace?.Name ?? string.Empty);
    }

    /// <param name="ownerWindow">창을 띄울 주인(설정 「AI」 탭이 설정 창을 넘긴다). null 이면 주 창.</param>
    /// <summary>연결 창의 접속 읽기 길. 공유 서비스가 있으면 그 한 길, 없으면 빈 목록.</summary>
    internal static Func<CancellationToken, Task<IReadOnlyList<ConnectionProfile>>> ProfileAccess(SharedAiAccessService? shared) =>
        shared is null ? _ => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]) : shared.LoadProfilesAsync;

    public static async Task ShowAsync(IServiceProvider services, CancellationToken ct, Window? ownerWindow = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var owner = ownerWindow ?? (services.GetService(typeof(ShellController)) as ShellController)?.MainWindow;
        if (owner is null || ServicesFrom(services) is not { } parts)
        {
            return;
        }

        using var vm = new ExternalAgentConnectViewModel(parts);
        await vm.LoadAsync(ct).ConfigureAwait(true);
        var skills = services.GetRegisteredOrDefault<IAgentSkillInstaller>();
        // 「수준 바꾸기」: 설정 창 위에서 열었으면 창만 닫고(① 표가 밑에 있다), 주 창 위(팔레트)에서 열었으면 설정 「AI」 탭을 연다.
        Func<Task> openLevels = ownerWindow is Oranda.App.Shell.Settings.SettingsWindow
            ? () => Task.CompletedTask
            : () =>
            {
                (services.GetService(typeof(Oranda.App.Shell.ShellController)) as Oranda.App.Shell.IShellUi)?.ShowSettings(vm => vm.SelectedTab = Oranda.App.Shell.Settings.SettingsViewModel.AiTabIndex);
                return Task.CompletedTask;
            };
        var window = new ExternalAgentConnectWindow(vm, skills, openLevels);
        await window.ShowDialog(owner).ConfigureAwait(true);
    }
}
