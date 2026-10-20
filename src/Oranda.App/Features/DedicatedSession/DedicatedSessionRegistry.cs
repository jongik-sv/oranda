using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.DedicatedSession;

/// <summary>
/// 전용 세션 SQL 탭(D-017, SQL Developer 의 unshared worksheet)의 탭 → 세션 등록부. 탭마다 자기 세션을 쥐고, 탭이 닫히면 세션을 닫는다.
/// 키는 탭 객체다(<see cref="SqlEditorViewModel.TabId"/> 가 아니다: 작업 공간 복원·닫은 탭 다시 열기는 같은 ID 로 새 탭을 만들고,
/// 그 탭은 공유 세션 탭이어야 한다). 활성 탭이 바뀌면 실행 상태(<see cref="ExecutionState.Focus"/>)가 보여 줄 세션을 맞춘다.
/// 탭이 닫히기 전에는 SQL 탭 닫기 가드(<see cref="SqlEditorHost.RegisterCloseGuard"/>)로 미커밋을 묻는다: 저장 확인 가드(호스트가 먼저 등록)
/// 다음 차례다(D-017 「저장 확인 → 커밋/롤백 확인」). UI 스레드에서만 쓴다.
/// </summary>
public sealed class DedicatedSessionRegistry
{
    private readonly Dictionary<SqlEditorViewModel, ISession> _byTab = new(ReferenceEqualityComparer.Instance);
    private readonly SqlEditorHost _host;
    private readonly ExecutionState _state;
    private readonly List<Task> _releasing = [];
    private bool _shutDown;

    public DedicatedSessionRegistry(SqlEditorHost host, ExecutionState state, IEventBus? bus = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(state);
        _host = host;
        _state = state;
        host.TabClosed += (_, tab) => _ = ReleaseAsync(tab);
        host.RegisterCloseGuard(GuardAsync);
        host.ActiveChanged += (_, _) => SyncFocus();
        bus?.Subscribe<SessionClosed>(this, e => Post(() => Forget(e.SessionId)));
    }

    /// <summary>세션을 닫다가 난 오류·끊긴 세션 정리 같은 알림(메시지 창). 기본은 추적 로그만.</summary>
    public Action<string> Notify { get; set; } = text => Trace.TraceInformation(text);

    /// <summary>
    /// 미커밋 확인(세션, 탭 이름 → true = 이제 미커밋이 없다). 조립이 <see cref="ExecutionController.ConfirmUncommittedAsync"/> 를 넣는다.
    /// 없으면 미커밋 탭은 닫지 않는다(묻지 않고 롤백하지 않는다).
    /// </summary>
    public Func<ISession, string, CancellationToken, Task<bool>>? ConfirmUncommitted { get; set; }

    public SqlEditorHost Host => _host;

    /// <summary>
    /// SQL 작업 공간 문서가 활성인지(셸의 문서 영역). 아니면(PL/SQL 편집기·객체 상세 등) 상태 표시줄·문맥 키·커밋 명령이 공유 작업 세션을 본다:
    /// 그 문서들은 공유 세션을 쓰는데 마지막 SQL 탭의 전용 세션을 가리키면 엉뚱한 세션을 커밋한다. 없으면 늘 true.
    /// 바뀌면 <see cref="Refocus"/> 를 부른다.
    /// </summary>
    public Func<bool>? SqlDocumentActive { get; set; }

    /// <summary>실행 상태가 보여 줄 세션을 활성 탭·활성 문서에 다시 맞춘다.</summary>
    public void Refocus() => SyncFocus();

    /// <summary>
    /// 보이는 세션 맞추기를 대신할 곳(<see cref="ConnectionManager.SessionRouter.Refocus"/>: 탭에 묶인 접속까지 본다). 없으면 전용 세션만 본다.
    /// </summary>
    public Action? FocusSync { get; set; }

    /// <summary>탭 중 하나라도 전용 세션 탭이면 true(문서 탭 × 를 가로챌지 정할 때).</summary>
    public bool HasAny(IEnumerable<SqlEditorViewModel> tabs) => tabs.Any(_byTab.ContainsKey);

    /// <summary>앱 종료 정리(<see cref="ReleaseAllAsync"/>)를 시작했다. 그 뒤로는 세션을 붙이지 않는다.</summary>
    public bool IsShutDown => _shutDown;

    /// <summary>닫기 가드가 예외로 끝났다(마지막 <see cref="ResetGuardFailure"/> 뒤). 앱 종료 확인이 「실패」와 「취소」를 나눌 때 본다.</summary>
    public bool LastGuardFailed { get; private set; }

    public void ResetGuardFailure() => LastGuardFailed = false;

    /// <summary>모든 전용 세션의 「닫는 중」을 푼다(앱 종료 확인을 그만뒀다).</summary>
    public void CancelClosing()
    {
        foreach (var session in _byTab.Values)
        {
            _state.SetClosing(session.Id, false);
        }
    }

    /// <summary>지금 쥐고 있는 전용 세션 수(시험이 누수를 본다).</summary>
    public int Count => _byTab.Count;

    /// <summary>탭의 전용 세션. 공유 세션 탭이면 null.</summary>
    public ISession? SessionOf(SqlEditorViewModel? tab) => tab is not null && _byTab.TryGetValue(tab, out var s) ? s : null;

    /// <summary>전용 세션 탭과 그 세션 목록(등록 순서와 무관).</summary>
    public IReadOnlyList<KeyValuePair<SqlEditorViewModel, ISession>> Entries => [.. _byTab];

    /// <summary>
    /// 이미 연 전용 세션을 탭에 붙인다. 실행 상태에 그 세션의 상태를 바로 만든다(<c>SessionOpened</c> 를 기다리지 않는다).
    /// 탭에 이미 세션이 있으면 <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Attach(SqlEditorViewModel tab, ISession session)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(session);
        if (_shutDown)
        {
            throw new InvalidOperationException("앱을 끝내는 중이라 전용 세션을 붙이지 않습니다.");
        }

        if (!_byTab.TryAdd(tab, session))
        {
            throw new InvalidOperationException("이 탭에는 이미 전용 세션이 있습니다.");
        }

        _state.AddDedicated(session.Id, session.Profile);
        SyncFocus();
    }

    /// <summary>탭의 전용 세션을 떼고 닫는다. 미커밋 확인은 닫기 가드가 먼저 한다(확인 없이 닫히면 드라이버가 연결을 닫으며 남은 트랜잭션을 롤백한다).</summary>
    public Task ReleaseAsync(SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!_byTab.Remove(tab, out var session))
        {
            return Task.CompletedTask;
        }

        var broken = _state.IsBroken(session.Id) || session.State is SessionState.Broken;
        var lost = broken && (_state.IsTxnPending(session.Id) || session.HasPendingTransaction);
        _state.RemoveDedicated(session.Id);
        SyncFocus();
        var task = DisposeAsync(tab.Title, session, broken, lost);
        _releasing.Add(task);
        _ = ForgetWhenDoneAsync(task);
        return task;
    }

    /// <summary>지금 닫는 중인 세션이 모두 닫힐 때까지 기다린다(닫기는 UI 스레드 밖에서 도므로 시험이 닫힘을 확인할 때 쓴다).</summary>
    internal Task WhenReleasedAsync() => Task.WhenAll(_releasing.ToList());

    private async Task ForgetWhenDoneAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        finally
        {
            _releasing.Remove(task);
        }
    }

    private async Task DisposeAsync(string title, ISession session, bool broken, bool lost)
    {
        try
        {
            await ConnectionService.CloseAsync(session, Notify).ConfigureAwait(true);
            if (broken)
            {
                Notify(lost
                    ? $"연결이 끊긴 전용 세션을 확인 없이 정리했습니다({title}). 커밋하지 않은 변경은 서버에서 롤백됐습니다."
                    : $"연결이 끊긴 전용 세션을 확인 없이 정리했습니다({title}).");
            }
        }
        catch (Exception e)
        {
            Notify($"전용 세션을 닫지 못했습니다({title}): {e.Message}");
        }
    }

    /// <summary>모든 전용 세션을 닫는다(앱 종료, 미커밋 확인 뒤). 이미 닫는 중인 세션도 기다린다. 그 뒤로는 세션을 붙이지 않는다.</summary>
    public async Task ReleaseAllAsync()
    {
        _shutDown = true;
        foreach (var tab in _byTab.Keys.ToList())
        {
            _ = ReleaseAsync(tab);
        }

        await Task.WhenAll(_releasing.ToList()).ConfigureAwait(true);
    }

    /// <summary>
    /// 닫기 가드: 닫히려는 탭 중 전용 세션 탭마다 끊긴 세션은 묻지 않고 넘기고(닫힐 때 정리하며 알린다), 하나라도 실행 중이면 알리고 취소,
    /// 미커밋이면 그 탭을 보여 주고 커밋/롤백/취소를 묻는다. 묻는 동안과 닫힐 때까지는 그 세션들을 「닫는 중」(<see cref="ExecutionState.SetClosing"/>)으로
    /// 두어 새 실행·커밋이 끼어들지 못하게 한다. 앞 탭에서 커밋한 뒤 뒤 탭에서 취소하면 앞 탭의 커밋은 그대로다.
    /// 전용 세션 탭이 없으면 기다리지 않고 바로 true(완료된 Task)를 돌려준다.
    /// </summary>
    private async Task<bool> GuardAsync(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct)
    {
        try
        {
            return await GuardCoreAsync(tabs, reason, ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 호스트는 가드 예외를 「취소」로 삼킨다. 앱 종료는 사용자의 취소와 실패를 나눠야 하므로 남겨 둔다.
            LastGuardFailed = true;
            throw;
        }
    }

    private async Task<bool> GuardCoreAsync(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct)
    {
        var live = new List<(SqlEditorViewModel Tab, ISession Session)>();
        foreach (var tab in tabs)
        {
            if (SessionOf(tab) is not { } session || _state.IsBroken(session.Id) || session.State is SessionState.Broken or SessionState.Closed)
            {
                continue;
            }

            if (_state.IsRunning(session.Id))
            {
                _host.Active = tab;
                await _host.Dialogs.ShowMessageAsync(
                    "전용 세션", $"「{tab.Title}」 탭이 실행 중입니다. 실행을 취소하거나 끝난 뒤 닫으세요.", ct).ConfigureAwait(true);
                return false;
            }

            live.Add((tab, session));
        }

        if (live.Count == 0)
        {
            return true;
        }

        foreach (var (_, session) in live)
        {
            _state.SetClosing(session.Id, true);
        }

        var ok = false;
        try
        {
            foreach (var (tab, session) in live)
            {
                if (!_state.IsTxnPending(session.Id) && !session.HasPendingTransaction)
                {
                    continue;
                }

                _host.Active = tab;
                if (ConfirmUncommitted is not { } confirm || !await confirm(session, tab.Title, ct).ConfigureAwait(true))
                {
                    return false;
                }
            }

            ok = true;
            return true;
        }
        finally
        {
            if (ok && reason == SqlCloseReason.AppExit)
            {
                // 앱 종료: 공유 세션 확인이 이어지고 그동안 디스패처가 돈다. 종료 정리가 닫고, 종료를 그만두면 CancelClosing 이 푼다.
            }
            else if (ok)
            {
                // 닫는 쪽이 이어서 닫는다. 그래도 닫히지 않은 탭(다른 경로가 닫기를 그만둠)은 잠시 뒤 풀어 준다.
                Dispatcher.UIThread.Post(() => ClearClosing(live), DispatcherPriority.Background);
            }
            else
            {
                ClearClosing(live);
            }
        }
    }

    private void ClearClosing(List<(SqlEditorViewModel Tab, ISession Session)> sessions)
    {
        foreach (var (_, session) in sessions)
        {
            _state.SetClosing(session.Id, false); // 이미 닫힌 세션이면 아무것도 안 한다
        }
    }

    /// <summary>세션이 밖에서 닫혔다(<c>SessionClosed</c>): 탭은 공유 세션 탭으로 돌아간다.</summary>
    private void Forget(Guid sessionId)
    {
        foreach (var (tab, session) in _byTab.ToList())
        {
            if (session.Id == sessionId)
            {
                _byTab.Remove(tab);
                _state.RemoveDedicated(sessionId);
                SyncFocus();
            }
        }
    }

    private void SyncFocus()
    {
        if (FocusSync is { } sync)
        {
            sync();
        }
        else
        {
            _state.Focus(SqlDocumentActive?.Invoke() is false ? null : SessionOf(_host.Active)?.Id);
        }
    }

    private static void Post(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
