using System.Runtime.CompilerServices;
using System.Text;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.DedicatedSession;

/// <summary>
/// 전용 세션 SQL 탭(D-017) 조립: 컨테이너마다 등록부 하나(<see cref="Resolve"/>)와 「새 SQL 탭(전용 세션)」 명령.
/// 셸 조립 파일을 고치지 않으려고 처음 쓸 때 만든다(<see cref="ExecutionCommands.Resolve"/> 와 같은 방식).
/// </summary>
public static class DedicatedSessionCommands
{
    public const string NewTabCommandId = "oranda.file.newDedicatedSqlEditor";

    private static readonly ConditionalWeakTable<ServiceContainer, DedicatedSessionRegistry> Registries = [];
    private static readonly Lock Gate = new();

    /// <summary>컨테이너의 등록부. 처음이면 만든다(그때의 <see cref="SqlEditorHost.Current"/> 를 쓴다).</summary>
    public static DedicatedSessionRegistry Resolve(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        lock (Gate)
        {
            if (Registries.TryGetValue(container, out var existing))
            {
                return existing;
            }

            var bus = container.GetService<IEventBus>();
            var log = ExecutionLog.For(bus);
            var registry = new DedicatedSessionRegistry(SqlEditorHost.Current, ExecutionState.For(bus), bus)
            {
                Notify = text => Avalonia.Threading.Dispatcher.UIThread.Post(() => log.AddMessage(MessageKind.Warning, text)),
                ConfirmUncommitted = (session, title, ct) => ExecutionCommands.Resolve(container).ConfirmUncommittedAsync(session, title, ct),
            };
            Registries.Add(container, registry);
            if (container.GetService<DockWorkspace>() is { } docking)
            {
                docking.CloseConfirm ??= document => CloseConfirmFor(registry, document);
                registry.SqlDocumentActive = () => docking.ActiveDocument is not { } active || active.Panel is SqlWorkspaceViewModel;
                docking.ActiveDocumentChanged += (_, _) => registry.Refocus();
            }

            return registry;
        }
    }

    /// <summary>
    /// 문서 탭 × 로 SQL 작업 공간을 닫기 전 확인(<see cref="DockWorkspace.CloseConfirm"/>). 전용 세션 탭이 든 작업 공간만 SQL 탭 닫기 가드
    /// (<see cref="SqlCloseReason.Workspace"/>)를 거친다. 나머지 문서는 null(묻지 않고 바로 닫는다). 호스트가 이미 닫은 작업 공간도 null.
    /// </summary>
    public static Func<CancellationToken, Task<bool>>? CloseConfirmFor(DedicatedSessionRegistry registry, PanelDocument document)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(document);
        if (document.Panel is not SqlWorkspaceViewModel ws || !registry.Host.Workspaces.Contains(ws) || !registry.HasAny(ws.Tabs))
        {
            return null;
        }

        return ct => registry.Host.CanCloseAsync(ws.Tabs.ToList(), SqlCloseReason.Workspace, ct);
    }

    /// <summary>
    /// 앱을 끝내도 되는지 묻는다(주 창이 닫히기 전, 레이아웃 저장·종료 작업보다 먼저). SQL 탭 닫기 가드(<see cref="SqlCloseReason.AppExit"/>:
    /// 저장 확인은 묻지 않고 전용 세션 미커밋만 묻는다, D-016·D-017) → 공유 작업 세션 미커밋 순.
    /// 사용자가 취소하면 <see cref="ExitConfirmResult.Cancelled"/>, 확인 자체가 예외로 실패하면 <see cref="ExitConfirmResult.Failed"/>
    /// (주 창이 알리고 다음 닫기는 확인 없이 끝낸다: 확인이 늘 실패하면 앱을 끌 수 없게 되므로).
    /// </summary>
    public static async Task<ExitConfirmResult> ConfirmExitAsync(ServiceContainer container, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        var host = Registries.TryGetValue(container, out var registry) ? registry.Host : SqlEditorHost.Current;
        registry?.ResetGuardFailure();
        var result = ExitConfirmResult.Cancelled;
        try
        {
            // 전용 세션 확인을 마치면 그 세션들은 「닫는 중」으로 남는다(공유 세션 확인·커밋을 기다리는 동안 실행이 끼어들지 않게).
            if (!await host.CanShutdownAsync(ct).ConfigureAwait(true))
            {
                result = registry?.LastGuardFailed == true ? ExitConfirmResult.Failed : ExitConfirmResult.Cancelled;
                return result;
            }

            var ok = !ExecutionState.For(container.GetService<IEventBus>()).TxnPending
                || await ExecutionCommands.Resolve(container).ConfirmCloseAsync(ct).ConfigureAwait(true);
            result = ok ? ExitConfirmResult.Proceed : ExitConfirmResult.Cancelled;
            return result;
        }
#pragma warning disable CA1031 // 확인이 어떤 이유로 실패해도 주 창이 알리고 강제 종료 길을 열어야 한다.
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError("종료 확인 실패: {0}", e);
            result = ExitConfirmResult.Failed;
            return result;
        }
        finally
        {
            if (result != ExitConfirmResult.Proceed)
            {
                registry?.CancelClosing(); // 종료를 그만두면 다시 실행할 수 있게
            }
        }
    }

    /// <summary>앱 종료: 모든 전용 세션을 닫는다(<see cref="ConfirmExitAsync"/> 뒤). 등록부를 만든 적 없으면 할 일이 없다.</summary>
    public static Task ReleaseAllAsync(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return Registries.TryGetValue(container, out var registry) ? registry.ReleaseAllAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// 지금 작업 세션과 같은 접속으로 전용 세션(<see cref="SessionPurpose.Dedicated"/>)을 연다. 비밀번호는 안전 저장소에서 꺼내고,
    /// 없으면 묻는다(<c>SessionMonitorCommands.OpenMonitorSessionAsync</c> 와 같은 순서). 작업 세션이 없거나 사용자가 취소하면 null.
    /// </summary>
    public static async Task<ISession?> OpenSessionAsync(ServiceContainer container, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (ConnectionManagerCommands.Resolve(container).ActiveSession is not { } work)
        {
            return null;
        }

        var profile = work.Profile;
        string? secret = null;
        if (profile.CredentialRef is { } reference && container.GetService<ICredentialStore>() is { } store
            && await store.LoadAsync(reference).ConfigureAwait(true) is { } bytes)
        {
            secret = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
        }

        if (string.IsNullOrEmpty(secret) && container.GetService<ShellController>()?.MainWindow is { } owner)
        {
            secret = await ConnectionDialogs.PromptPasswordAsync(owner, profile).ConfigureAwait(true);
        }

        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        using var credential = new SecureCredential(secret);
        return await container.GetRequiredService<ISessionManager>()
            .OpenAsync(profile, credential, SessionPurpose.Dedicated, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 세션을 먼저 열고(비밀번호 취소·접속 실패가 있을 수 있다) 열리면 새 SQL 탭을 만들어 붙인다. 못 열면 탭을 만들지 않고 메시지를 남긴다.
    /// </summary>
    public static async Task<SqlEditorViewModel?> NewTabAsync(DedicatedSessionRegistry registry, Func<CancellationToken, Task<ISession?>> open,
        ExecutionLog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(log);
        ISession? session;
        try
        {
            session = await open(ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.AddMessage(MessageKind.Error, "전용 세션을 열지 못했습니다: " + e.Message);
            return null;
        }

        if (session is null)
        {
            log.AddMessage(MessageKind.Info, "전용 세션을 열지 않았습니다(접속이 없거나 비밀번호 입력을 취소했습니다).");
            return null;
        }

        if (registry.IsShutDown)
        {
            // 접속하는 동안 앱을 끝내기 시작했다: 탭을 만들지 않고 세션을 바로 닫는다(종료 정리가 이미 끝났을 수 있다).
            await ConnectionService.CloseAsync(session).ConfigureAwait(true);
            log.AddMessage(MessageKind.Warning, "앱을 끝내는 중이라 전용 세션을 닫았습니다.");
            return null;
        }

        SqlEditorViewModel tab;
        try
        {
            tab = registry.Host.NewDocument();
            registry.Attach(tab, session);
        }
        catch
        {
            await ConnectionService.CloseAsync(session).ConfigureAwait(true); // 붙이지 못한 세션을 열어 두지 않는다
            throw;
        }

        log.AddMessage(MessageKind.Info, $"「{tab.Title}」 탭을 전용 세션으로 열었습니다.");
        return tab;
    }
}

/// <summary>「새 SQL 탭(전용 세션)」: 이 탭만 쓰는 세션을 열고 새 SQL 탭에 붙인다(D-017).</summary>
[OrandaCommand(DedicatedSessionCommands.NewTabCommandId, CommandCategory.File, Enablement = "hasSession",
    Placements = ["menu:file/newDedicatedSqlEditor@11"])]
public sealed class DedicatedSessionCommandHandler(ServiceContainer container) : ICommandHandler
{
    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CommandId == DedicatedSessionCommands.NewTabCommandId)
        {
            await DedicatedSessionCommands.NewTabAsync(
                DedicatedSessionCommands.Resolve(container),
                ct => DedicatedSessionCommands.OpenSessionAsync(container, ct),
                ExecutionLog.For(container.GetService<IEventBus>()),
                cancellationToken).ConfigureAwait(true);
        }
    }
}

/// <summary>앱 종료 확인의 결과(<see cref="DedicatedSessionCommands.ConfirmExitAsync"/>).</summary>
public enum ExitConfirmResult
{
    /// <summary>끝내도 된다(미커밋이 없거나 사용자가 커밋·롤백했다).</summary>
    Proceed,

    /// <summary>사용자가 취소했다(또는 커밋·롤백이 실패해 미커밋이 남았다).</summary>
    Cancelled,

    /// <summary>확인 자체가 예외로 실패했다.</summary>
    Failed,
}
