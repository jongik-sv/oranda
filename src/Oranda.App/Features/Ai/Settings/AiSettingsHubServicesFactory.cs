using System.Threading;
using Avalonia.Controls;
using Oranda.App.Features.Ai.External.Connect;
using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Settings;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>설정 「AI」 탭의 부품을 앱 서비스에서 모은다. 앱 밖(시험)이면 모두 null 이라 단추가 꺼진다.</summary>
internal static class AiSettingsHubServicesFactory
{
    public static AiSettingsHubServices FromApp(SettingsService settings)
    {
        if ((Avalonia.Application.Current as App)?.Services is not { } services)
        {
            return new AiSettingsHubServices();
        }

        var launcher = services.GetRegisteredOrDefault<IAiSetupLauncher>() as AiSetupLauncher;
        var connections = services.GetRegisteredOrDefault<IConnectionService>();
        var skills = services.GetRegisteredOrDefault<IAgentSkillInstaller>();
        return new AiSettingsHubServices(
            OpenWizard: launcher is null ? null : (owner, entry, reason) => launcher.OpenOwnedAsync(owner, entry, reason),
            Access: connections is null ? null : AccessStepServices.FromShared(SharedAiAccessService.FromConnections(connections, bus: null)), // 공유 허용 수준 한 길(도우미·외부 연결 창과 같은 값)
            AgentLink: services.GetRegisteredOrDefault<IExternalAgentLink>(),
            OpenAgentConnect: ExternalAgentConnectCommands.ServicesFrom(services) is null ? null : owner => ExternalAgentConnectCommands.ShowAsync(services, CancellationToken.None, owner),
            OpenSkillInstall: skills is null ? null : owner => owner is null
                ? System.Threading.Tasks.Task.CompletedTask
                : new AgentSkillInstallWindow(new AgentSkillInstallViewModel(skills)).ShowDialog(owner),
            Test: () => AiSetupLauncher.CreateContext(services, settings, AiSetupEntry.Menu, reason: null, owner: null));
    }
}
