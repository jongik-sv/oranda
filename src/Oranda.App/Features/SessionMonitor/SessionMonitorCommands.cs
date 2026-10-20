using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>
/// TSK-09-01 세션 모니터 — 명령 ID(commands.md 7장 「모니터」)와 도우미. 처리기는 <see cref="SessionMonitorCommandHandler"/>.
/// </summary>
public static class SessionMonitorCommands
{
    /// <summary>문서 종류 ID.</summary>
    public const string PanelId = "oranda.panel.sessionMonitor";

    /// <summary>이 Task 가 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids =
    [
        "oranda.monitor.sessions",
        "oranda.monitor.refresh",
        "oranda.monitor.toggleAutoRefresh",
        "oranda.monitor.toggleDelta",
        "oranda.monitor.blockingTree",
        "oranda.monitor.sessionDetail",
        "oranda.monitor.killSession",
        "oranda.monitor.disconnectSession",
        "oranda.monitor.traceOn",
        "oranda.monitor.traceOff",
    ];

    private static readonly ConditionalWeakTable<ContextKeyService, IDisposable> Attached = new();

    /// <summary>
    /// 작업 세션이 열릴 때 서버 점검 결과로 메뉴 키(<c>canKillSession</c>, <c>canTraceSessions</c>)를 맞춘다.
    /// 「세션 모니터」·「SQL 모니터」 메뉴는 이 키로 끄지 않는다(권한이 없으면 창이 이유와 해결 방법을 보인다).
    /// 같은 키 저장소에 두 번 붙이지 않는다. 셸 조립이 시작 때 부르고, 이 기능의 처리기·화면도 처음 만들어질 때 부른다.
    /// </summary>
    public static void AttachPrivilegeKeys(IEventBus bus, ContextKeyService keys)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(keys);
        lock (Attached)
        {
            if (Attached.TryGetValue(keys, out _))
            {
                return;
            }

            Attached.Add(keys, bus.Subscribe<SessionOpened>(keys, e =>
            {
                if (e.Purpose == SessionPurpose.Work)
                {
                    ApplyPrivilegeKeys(keys, e.Server.Capabilities);
                }
            }));
        }
    }

    /// <summary>작업 세션 계정의 권한으로 메뉴 키를 맞춘다.</summary>
    public static void ApplyPrivilegeKeys(ContextKeyService keys, Oranda.Core.Connection.ServerCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(caps);
        keys.Set("canKillSession", caps.CanQueryPerformanceViews && caps.CanKillSession);
        keys.Set("canTraceSessions", caps.CanQueryPerformanceViews && caps.CanTraceSessions);
    }

    /// <summary>
    /// <c>ALTER SYSTEM DISCONNECT SESSION 'sid,serial#[,@inst]' POST_TRANSACTION|IMMEDIATE</c>. 실행 엔진의 위험 명령 확인 대상
    /// (<c>IMonitorService.KillAsync</c> 에는 Disconnect 선택이 없어 여기서 만든다).
    /// </summary>
    public static SqlStatement DisconnectStatement(SessionKey target, bool immediate, bool rac)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"{target.Sid},{target.Serial}");
        if (rac)
        {
            id += string.Create(CultureInfo.InvariantCulture, $",@{target.InstanceId}");
        }

        return new SqlStatement($"ALTER SYSTEM DISCONNECT SESSION '{id}' {(immediate ? "IMMEDIATE" : "POST_TRANSACTION")}", default, StatementKind.Other)
        {
            Verb = "ALTER SYSTEM",
            DangerousTarget = id,
        };
    }

    /// <summary>
    /// 모니터 전용 세션(<see cref="SessionPurpose.Monitor"/>)을 여는 도구를 만든다. <paramref name="monitorId"/> 는 기억하는 프로필의 구분 키.
    /// 비밀번호는 안전 저장소에서 꺼내고 없으면 묻는다. 프로필 고르기·비밀번호 묻기는 주 창을 소유자로 띄운다.
    /// </summary>
    public static MonitorSessionOpener CreateOpener(ServiceContainer container, string monitorId)
    {
        ArgumentNullException.ThrowIfNull(container);
        var paths = container.GetService<IAppDataPaths>();
        var opener = new MonitorSessionOpener(
            ConnectionManagerCommands.Resolve(container),
            container.GetRequiredService<ISessionManager>(),
            container.GetService<ICredentialStore>(),
            paths is null ? null : new MonitorProfileMemory(System.IO.Path.Combine(paths.DataDirectory, "monitor-profiles.json")),
            monitorId);
        var shell = container.GetService<ShellController>();
        opener.PickProfile = (profiles, current) => shell?.MainWindow is { } owner
            ? MonitorProfilePickerWindow.PickAsync(owner, profiles, current, MonitorName(monitorId))
            : Task.FromResult<ConnectionProfile?>(null);
        opener.AskPassword = profile => shell?.MainWindow is { } owner
            ? ConnectionDialogs.PromptPasswordAsync(owner, profile)
            : Task.FromResult<string?>(null);
        return opener;
    }

    private static string MonitorName(string monitorId) => monitorId == "sqlMonitor" ? "SQL 모니터" : "세션 모니터";
}

/// <summary>모니터 메뉴·명령 팔레트의 세션 모니터 명령(SESS-01~05).</summary>
[OrandaCommand("oranda.monitor.sessions", CommandCategory.Monitor, Icon = "sessions", Placements = ["menu:monitor/sessions@10", "toolbar:main/sessions@510"])]
[OrandaCommand("oranda.monitor.refresh", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionOpen",
    Placements = ["menu:monitor/refresh@100", "context:monitor/refresh@10"])]
[OrandaCommand("oranda.monitor.toggleAutoRefresh", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionOpen",
    Placements = ["menu:monitor/autoRefresh@110"])]
[OrandaCommand("oranda.monitor.toggleDelta", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionOpen",
    Placements = ["menu:monitor/delta@120"])]
[OrandaCommand("oranda.monitor.blockingTree", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionOpen",
    Placements = ["menu:monitor/blockingTree@130"])]
[OrandaCommand("oranda.monitor.sessionDetail", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionSelected",
    Placements = ["menu:monitor/detail@200", "context:monitor/detail@20"])]
[OrandaCommand("oranda.monitor.killSession", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionSelected && canKillSession",
    Placements = ["menu:monitor/kill@210", "context:monitor/kill@30"])]
[OrandaCommand("oranda.monitor.disconnectSession", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionSelected && canKillSession",
    Placements = ["menu:monitor/disconnect@220", "context:monitor/disconnect@31"])]
[OrandaCommand("oranda.monitor.traceOn", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionSelected && canTraceSessions",
    Placements = ["menu:monitor/traceOn@230", "context:monitor/traceOn@40"])]
[OrandaCommand("oranda.monitor.traceOff", CommandCategory.Monitor, When = "monitorFocus", Enablement = "monitorSessionSelected && canTraceSessions",
    Placements = ["menu:monitor/traceOff@231", "context:monitor/traceOff@41"])]
public sealed class SessionMonitorCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;

    public SessionMonitorCommandHandler()
    {
    }

    public SessionMonitorCommandHandler(ServiceContainer container)
    {
        _container = container;
        if (container.GetService<IEventBus>() is { } bus && container.GetService<ContextKeyService>() is { } keys)
        {
            SessionMonitorCommands.AttachPrivilegeKeys(bus, keys);
        }
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        if (context.CommandId == "oranda.monitor.sessions")
        {
            await OpenAsync(shell.Docking, cancellationToken).ConfigureAwait(true);
            return;
        }

        if ((context.Parameter as SessionMonitorViewModel ?? shell.Docking.ActiveDocument?.Panel as SessionMonitorViewModel) is not { } vm)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.monitor.refresh":
            case "oranda.monitor.blockingTree":
                await vm.RefreshAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.monitor.toggleAutoRefresh":
                vm.AutoRefresh = !vm.AutoRefresh;
                break;
            case "oranda.monitor.toggleDelta":
                vm.DeltaMode = !vm.DeltaMode;
                break;
            case "oranda.monitor.sessionDetail":
                await vm.LoadDetailAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.monitor.killSession":
                await vm.KillAsync(immediate: true, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.monitor.disconnectSession":
                await vm.DisconnectAsync(immediate: true, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.monitor.traceOn":
                await vm.SetTraceAsync(true, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.monitor.traceOff":
                await vm.SetTraceAsync(false, cancellationToken).ConfigureAwait(true);
                break;
        }
    }

    private async Task OpenAsync(Shell.Docking.DockWorkspace docking, CancellationToken ct)
    {
        var container = _container!;
        if (docking.OpenDocument(SessionMonitorCommands.PanelId) is not { Panel: SessionMonitorViewModel vm } doc)
        {
            return;
        }

        if (vm.OpenSession is null)
        {
            var opener = SessionMonitorCommands.CreateOpener(container, "sessionMonitor");
            vm.OpenSession = opener.OpenDefaultAsync;
            vm.ChooseSession = opener.OpenChosenAsync;
            vm.Remember = opener.Remember;
            vm.Confirm ??= message => container.GetService<ShellController>()?.MainWindow is { } owner
                ? ConnectionDialogs.ConfirmAsync(owner, message)
                : Task.FromResult(false);

            // 문서 탭이 닫히면 모니터 세션도 닫는다.
            void OnLayout(object? sender, EventArgs e)
            {
                if (!docking.Documents.Contains(doc))
                {
                    docking.LayoutChanged -= OnLayout;
                    docking.ActiveDocumentChanged -= OnLayout;
                    _ = vm.StopAsync();
                }
            }

            docking.LayoutChanged += OnLayout;
            docking.ActiveDocumentChanged += OnLayout;
        }

        await vm.StartAsync(ct).ConfigureAwait(true);
    }
}
