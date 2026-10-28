using System;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Layout;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Bridge;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Events;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Platform;
using Oranda.Platform.Storage;

namespace Oranda.App.Shell.Composition;

/// <summary>앱을 조립할 때 바꿀 수 있는 값(시험·휴대용).</summary>
public sealed record AppCompositionOptions
{
    /// <summary>데이터 폴더를 직접 지정한다. null 이면 <c>ORANDA_DATA_DIR</c> 또는 OS 기본 위치.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>개발 빌드 전용 도구 창·<c>devBuild</c> 키를 켠다.</summary>
    public bool DevBuild { get; init; }

#if DEBUG
    public static bool DefaultDevBuild => true;
#else
    public static bool DefaultDevBuild => false;
#endif
}

/// <summary>
/// 앱 조립(design.md 3장 「자동 수집」): Platform 어셈블리는 바로, Oracle·Language 어셈블리는 처음 쓸 때까지 미뤄서 등록한다.
/// 시작 경로에서 Oracle·ANTLR 어셈블리를 로드하지 않는다(TRD 8) — 어셈블리 이름만 적어 두고 컨테이너가 필요할 때 로드한다.
/// 명령·도구 창·상태 표시줄 구간은 Oranda.App 어셈블리를 한 번 스캔해 모은다.
/// </summary>
public static class AppComposition
{
    /// <summary>지연 로드할 어셈블리 이름(첫 프레임 전에 열지 않는다).</summary>
    public static readonly string[] DeferredAssemblies = ["Oranda.Oracle", "Oranda.Language"];

    public static ServiceContainer Create(AppCompositionOptions? options = null)
    {
        options ??= new AppCompositionOptions { DevBuild = AppCompositionOptions.DefaultDevBuild };
        var container = new ServiceContainer();

        // 1) 이벤트 버스(Core 구현은 스캔 대상이 아니라 직접 등록)와 구현 어셈블리.
        container.AddInstance<IEventBus>(new EventBus());

        // 실행 정책(Core 구현, 스캔 대상 아님). 첫 SQL 실행 때만 등록하면 그 전에 실행 엔진이 필요한 기능
        // (세션 모니터·실행 계획·DB 정보 등)이 「생성자 인자를 해석할 수 없다」로 실패한다.
        container.AddType(typeof(Oranda.Core.Execution.IExecutionPolicy), typeof(Oranda.Core.Execution.ExecutionPolicy));
        container.AddType(typeof(Oranda.Core.Features.IFeatureGate), typeof(Features.FeatureGate)); // 기능 게이트(WP-07). 설정 읽기 전에는 모두 켬
        container.AddAssembly(typeof(PlatformAssembly).Assembly);
        container.AddAssembly(typeof(AppComposition).Assembly); // 앱 어셈블리의 [OrandaService](편집기 문맥·시작 작업 등)
        foreach (var name in DeferredAssemblies)
        {
            container.AddDeferredAssembly(name);
        }

        if (options.DataDirectory is { } dir)
        {
            container.AddInstance<IAppDataPaths>(new AppDataPaths(dir));
        }

        RegisterAi(container);

        // 2) 셸 기여(명령·도구 창·상태 표시줄 구간) 수집과 레지스트리.
        var contributions = ShellContributions.Scan(typeof(AppComposition).Assembly);
        container.AddInstance(contributions);
        // 레지스트리는 설정보다 먼저 생긴다(게이트는 그때 모두 켬). 설정을 읽은 뒤 게이트가 해석하면 거른 스냅숏을 다시 계산한다(TRD 10.6).
        container.AddFactory(typeof(ICommandRegistry), c => new CommandRegistry(contributions.Commands, c.Create, c.GetService<Oranda.Core.Features.IFeatureGate>()));
        container.AddFactory(typeof(IToolPanelRegistry), c => new ToolPanelRegistry(contributions.Panels, options.DevBuild, c.GetService<Oranda.Core.Features.IFeatureGate>()));
        container.AddFactory(typeof(IStatusBarRegistry), _ => new StatusBarRegistry(contributions.StatusBarItems));

        // 3) 셸 서비스.
        container.AddInstance<IShellText>(new ShellText());
        var keys = new ContextKeyService(options.DevBuild);
        container.AddInstance(keys);
        container.AddInstance<IContextKeys>(keys);
        container.AddType(typeof(SettingsService), typeof(SettingsService));
        container.AddType(typeof(Oranda.Core.Grid.IGridFetchSettings), typeof(Oranda.App.Features.ResultGrid.GridFetchSettings));
        container.AddType(typeof(ThemeService), typeof(ThemeService));
        container.AddType(typeof(KeymapService), typeof(KeymapService));
        container.AddType(typeof(ILayoutStore), typeof(LayoutStore));
        container.AddFactory(
            typeof(DockWorkspace),
            c => new DockWorkspace(
                c.GetRequiredService<IToolPanelRegistry>(),
                def => c.Create(def.ViewModelType),
                c.GetRequiredService<IShellText>()));
        container.AddType(typeof(ShellController), typeof(ShellController));
        container.AddFactory(typeof(IShellUi), c => c.GetRequiredService<ShellController>());

        return container;
    }

    /// <summary>
    /// AI 튜닝 서비스(Core 구현은 스캔 대상이 아니라 직접 등록): 실행 토큰 저장소(앱 전체 하나)·에이전트 세션 출처·게이트.
    /// 드라이버(<c>CliDriver</c>)와 다리(<c>AgentBridgeHost</c>)는 Platform 이 [OrandaService] 로 올리고, 다리를 여는 일은 시작 작업이 aiTuning 이 켜졌을 때만 한다.
    /// 드라이버가 받는 다리 끝점(<c>IAgentBridgeEndpoint</c>)은 Platform 안쪽 형식이라 이름으로 찾아, 시작 작업이 여는 다리와 같은 인스턴스를 쓰게 잇는다.
    /// </summary>
    private static void RegisterAi(ServiceContainer container)
    {
        container.AddType(typeof(IAgentRunTokens), typeof(AgentRunTokenStore));
        container.AddFactory(typeof(IAgentSessionSource), c => new AppAgentSessionSource(c).Publish());
        container.AddType(typeof(IAgentGate), typeof(AgentGate));
        if (typeof(PlatformAssembly).Assembly.GetType("Oranda.Platform.Ai.Bridge.IAgentBridgeEndpoint") is { } endpoint)
        {
            container.AddFactory(endpoint, c => c.GetRequiredService<IAgentBridgeHost>());
        }
    }
}
