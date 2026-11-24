using System.Globalization;
using System.Text;
using Oranda.App.Features.Binds;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

// ExecutionController 의 일부: 트랜잭션·커밋 확인(ExecutionController.cs 에서 나눔, 동작 불변).
public sealed partial class ExecutionController
{
    // ---- 트랜잭션 ---------------------------------------------------------------------------

    /// <summary>활성 탭 세션을 커밋한다(<c>oranda.txn.commit</c>). 여러 작업 공간이 함께 쓰는 세션이면 먼저 묻는다(<see cref="ConfirmedCommitAsync"/>).</summary>
    public Task CommitAsync(CancellationToken ct = default) => ConfirmedCommitAsync(FocusedSession(), ct);

    /// <summary>활성 탭 세션을 롤백한다(<c>oranda.txn.rollback</c>). 여러 작업 공간이 함께 쓰는 세션이면 먼저 묻는다.</summary>
    public Task RollbackAsync(CancellationToken ct = default) => ConfirmedRollbackAsync(FocusedSession(), ct);

    /// <summary>
    /// 사용자가 고른 커밋(툴바·메뉴·「세션」 창, TSK-14-10·D-042): 그 세션을 작업 공간 둘 이상이 함께 쓰면 함께 확정되는 작업 공간 이름을 보이고 한 번 묻는다.
    /// 취소하면 커밋하지 않는다. 자동 커밋·미커밋 확인 창의 커밋은 이미 그 세션을 밝혀 물었으므로 묻지 않는다(<see cref="CommitAsync(ISession?, CancellationToken)"/>).
    /// </summary>
    public async Task ConfirmedCommitAsync(ISession? s, CancellationToken ct = default)
    {
        if (s is not null && await ConfirmSharedAsync(s, commit: true, ct).ConfigureAwait(true)
            && await ConfirmIncompleteCommitAsync(s, ct).ConfigureAwait(true))
        {
            await CommitAsync(s, ct).ConfigureAwait(true);
        }
    }

    /// <summary>사용자가 고른 롤백. 함께 쓰는 세션이면 <see cref="ConfirmedCommitAsync"/> 처럼 먼저 묻는다(남의 변경도 함께 되돌린다).</summary>
    public async Task ConfirmedRollbackAsync(ISession? s, CancellationToken ct = default)
    {
        if (s is not null && await ConfirmSharedAsync(s, commit: false, ct).ConfigureAwait(true))
        {
            await RollbackAsync(s, ct).ConfigureAwait(true);
        }
    }

    private Task<bool> ConfirmSharedAsync(ISession s, bool commit, CancellationToken ct)
    {
        var workspaces = _d.SharedWith(s);
        return workspaces.Count < 2 || (!State.IsTxnPending(s.Id) && !s.HasPendingTransaction)
            ? Task.FromResult(true) // 함께 쓰지 않거나 확정할 것이 없다
            : _d.Dialogs.ConfirmSharedTransactionAsync(_d.ConnectionLabel(s), workspaces, commit, ct);
    }

    public async Task CommitAsync(ISession? s, CancellationToken ct = default)
    {
        if (s is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => s.CommitAsync(ct), CancellationToken.None).ConfigureAwait(true);
            State.ApplyTransaction(s.Id, s.HasPendingTransaction);
            Log.AddMessage(MessageKind.Info, "커밋했습니다.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Error, "커밋 실패: " + e.Message);
        }
    }

    public async Task RollbackAsync(ISession? s, CancellationToken ct = default)
    {
        if (s is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => s.RollbackAsync(ct), CancellationToken.None).ConfigureAwait(true);
            State.ApplyTransaction(s.Id, s.HasPendingTransaction);
            Log.AddMessage(MessageKind.Info, "롤백했습니다.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Error, "롤백 실패: " + e.Message);
        }
    }

    /// <summary>자동 커밋을 켜면 이후 성공한 변경 문장 뒤에 바로 커밋한다(화면 수준 동작).</summary>
    public void ToggleAutoCommit() => State.AutoCommit = !State.AutoCommit;

    // ---- 실행 중 닫기(PHC-3) ------------------------------------------------------------------

    /// <summary>닫는 경로(종료·접속 끊기·세션 닫기)의 커밋·롤백 상한. 세션 명령 잠금을 기다리는 시간도 넣는다(멈춘 DDL 뒤에 끝없이 줄 서지 않게).</summary>
    internal TimeSpan CloseEndTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>「취소하고 닫기」 뒤 세션이 명령을 놓기를 기다리는 시간. 넘기면 그 세션을 기다리지 않고 닫는다(연결을 버린다).</summary>
    internal TimeSpan CancelWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>잠깐 쥔 잠금(메타데이터 읽기·Keep Alive 등)을 「실행 중」으로 묻지 않게 묻기 전에 기다려 보는 시간.</summary>
    internal TimeSpan BusyGrace { get; set; } = TimeSpan.FromMilliseconds(300);

    private const int IdlePollMs = 25;

    // 「취소하고 닫기」를 골랐는데 상한 안에 명령을 놓지 않은 세션. 아직 바쁘면 미커밋을 묻지 않고 닫게 둔다(커밋·롤백이 그 명령 뒤에 줄 선다).
    // 닫기 한 번 동안만 쓴다: 다음 「취소하고 닫기」 확인이 넘어온 세션의 표시를 지우고, 앱 종료를 그만두면 모두 지운다(ForgetAbandoned).
    private readonly HashSet<Guid> _abandoned = [];

    /// <summary>「취소하고 닫기」로 기다리지 않기로 한 표시를 모두 지운다(닫기를 그만뒀다: 다음 닫기는 처음부터 묻는다).</summary>
    public void ForgetAbandoned() => _abandoned.Clear();

    /// <summary>화면이 시작한 실행 중인지. 자기 상태가 없는 세션은 지금 접속의 상태로 대신 보이므로 넣지 않는다.</summary>
    private bool IsRunningTracked(ISession session) => State.IsTracked(session.Id) && State.IsRunning(session.Id);

    /// <summary>
    /// 세션에서 명령이 도는 중인지: 화면이 시작한 실행(<see cref="ExecutionState.IsRunning"/>)이거나 누가 세션 명령 잠금을 쥐고 있다(<see cref="ISession.IsCommandRunning"/>).
    /// 끊기거나 닫힌 세션은 바쁘지 않다(서버가 이미 정리했다).
    /// </summary>
    public bool IsSessionBusy(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.State is not (SessionState.Broken or SessionState.Closed) && (IsRunningTracked(session) || session.IsCommandRunning);
    }

    /// <summary>앱 종료: 열린 공유 작업 세션 모두에 <see cref="ConfirmCancelRunningAsync(IReadOnlyList{ISession}, CancellationToken)"/>.</summary>
    public Task<bool> ConfirmCancelRunningAsync(CancellationToken ct = default) =>
        ConfirmCancelRunningAsync(_d.WorkSessions?.Invoke() ?? (_d.Session() is { } only ? [only] : []), ct);

    /// <summary>
    /// 닫을 세션 중 명령이 도는 것이 있으면(<see cref="IsSessionBusy"/>) 「실행 중인 작업을 취소하고 닫을까요?」(취소하고 닫기 / 돌아가기)를 미커밋 확인보다 먼저 묻는다(PHC-3·3b).
    /// 취소하고 닫기 = 도는 명령을 취소(break)하고 <see cref="CancelWaitTimeout"/> 까지 명령을 놓기를 기다린다. 못 놓은 세션은 기다리지 않고 닫는다
    /// (미커밋 확인을 건너뛰고, 닫기는 접속 서비스의 시간 제한 닫기가 한다). true = 계속 닫아도 된다. false = 돌아가기(아무것도 취소하지 않았다).
    /// </summary>
    public async Task<bool> ConfirmCancelRunningAsync(IReadOnlyList<ISession> sessions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        foreach (var session in sessions)
        {
            _abandoned.Remove(session.Id); // 이전 닫기(그만둔 것)의 표시는 쓰지 않는다
        }

        var busy = sessions.Where(IsSessionBusy).Distinct().ToList();
        if (busy.Count == 0)
        {
            return true;
        }

        // 화면이 시작한 실행이 아니면 잠깐 쥔 잠금일 수 있다: 조금 기다려 보고 그래도 바쁜 것만 묻는다.
        if (!busy.Any(IsRunningTracked))
        {
            await WaitIdleAsync(busy, BusyGrace, ct).ConfigureAwait(true);
            busy = [.. busy.Where(IsSessionBusy)];
            if (busy.Count == 0)
            {
                return true;
            }
        }

        var uncommitted = busy.Any(s => State.IsTxnPending(s.Id) || s.HasPendingTransaction);
        if (!await _d.Dialogs.ConfirmCancelRunningAsync([.. busy.Select(_d.ConnectionLabel)], uncommitted, CancelWaitTimeout, ct).ConfigureAwait(true))
        {
            return false;
        }

        foreach (var session in busy)
        {
            CancelRunsOf(session);
        }

        await WaitIdleAsync(busy, CancelWaitTimeout, ct).ConfigureAwait(true);
        foreach (var session in busy.Where(IsSessionBusy))
        {
            _abandoned.Add(session.Id);
            Log.AddMessage(MessageKind.Warning, ExecutionDialogText.Abandoned(_d.ConnectionLabel(session), CancelWaitTimeout));
        }

        return true;
    }

    /// <summary>세션에서 도는 일을 모두 취소한다: 컨트롤러가 시작한 실행의 토큰과 다른 기능이 맡겨 둔 취소(<see cref="ExecutionState.RegisterCancel"/>). 맡긴 취소가 없는 일(잠금만 쥔 일)은 끊지 못하고 기다림 상한으로 넘긴다.</summary>
    private void CancelRunsOf(ISession session)
    {
        var requested = false;
        if (_runs.TryGetValue(session.Id, out var cts) && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            requested = true;
        }

        requested |= State.CancelBackground(session.Id);
        if (requested && State.IsTracked(session.Id))
        {
            State.MarkCancelling(session.Id);
            Log.AddMessage(MessageKind.Info, "실행 취소를 요청했습니다.");
        }
    }

    /// <summary><paramref name="sessions"/> 가 모두 명령을 놓을 때까지 <paramref name="limit"/> 까지 기다린다(UI 스레드에서 짧게 쉬며 본다). 다 놓았으면 true.</summary>
    private async Task<bool> WaitIdleAsync(IReadOnlyList<ISession> sessions, TimeSpan limit, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (sessions.Any(IsSessionBusy))
        {
            if (watch.Elapsed >= limit)
            {
                return false;
            }

            await Task.Delay(IdlePollMs, ct).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>
    /// 닫는 경로의 커밋·롤백(<see cref="BoundedTransactionEnd"/>, 상한 <see cref="CloseEndTimeout"/>). true = 이제 미커밋이 없거나 닫아도 된다.
    /// 상한을 넘으면 「세션이 응답하지 않아 커밋/롤백하지 못했습니다」: 롤백이면 알리고 그대로 닫고(닫으면 서버가 되돌린다),
    /// 커밋이면 커밋하지 않고 닫을지 묻는다(기본 돌아가기). 상한 안에 실패하면 지금처럼 메시지 창에 남기고 false.
    /// </summary>
    private async Task<bool> EndForCloseAsync(ISession session, bool commit, CancellationToken ct)
    {
        var r = await BoundedTransactionEnd.RunAsync(session, commit, CloseEndTimeout).ConfigureAwait(true);
        switch (r.Outcome)
        {
            case TransactionEndOutcome.Done:
                State.ApplyTransaction(session.Id, session.HasPendingTransaction);
                Log.AddMessage(MessageKind.Info, commit ? "커밋했습니다." : "롤백했습니다.");
                return !State.IsTxnPending(session.Id) && !session.HasPendingTransaction;
            case TransactionEndOutcome.Failed:
                Log.AddMessage(MessageKind.Error, (commit ? "커밋 실패: " : "롤백 실패: ") + r.Error?.Message);
                return false;
            default:
                var label = _d.ConnectionLabel(session);
                if (!commit)
                {
                    Log.AddMessage(MessageKind.Warning, ExecutionDialogText.RollbackTimedOut(label, CloseEndTimeout));
                    return true;
                }

                Log.AddMessage(MessageKind.Warning, ExecutionDialogText.CommitTimedOut(label, CloseEndTimeout));
                return await _d.Dialogs.ConfirmCloseWithoutCommitAsync(label, CloseEndTimeout, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 열린 접속(공유 작업 세션)마다 미커밋 트랜잭션이 있으면 커밋/롤백/취소를 묻는다(SAFE: 종료 시 확인, CONN-06). true = 계속 닫아도 된다.
    /// 하나라도 취소하면 거기서 멈춘다. 전용 세션 탭은 SQL 탭 닫기 가드가 묻는다(D-017). 앱 종료 때 주 창(<c>MainWindow.OnClosing</c>)이 부른다.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync(CancellationToken ct = default)
    {
        var sessions = _d.WorkSessions?.Invoke() ?? (_d.Session() is { } only ? [only] : []);
        foreach (var session in sessions.ToList())
        {
            if (sessions.Count > 1 && !State.IsTracked(session.Id) && !session.HasPendingTransaction)
            {
                continue; // 자기 상태가 없는 세션에 지금 접속의 미커밋을 묻지 않는다
            }

            if (!await ConfirmConnectionCloseAsync(session, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 접속 하나(<paramref name="session"/>)에 미커밋 트랜잭션이 있으면 접속 이름을 밝혀 커밋/롤백/취소를 묻는다(접속 끊기·앱 종료).
    /// true = 이제 미커밋이 없다.
    /// </summary>
    public Task<bool> ConfirmConnectionUncommittedAsync(ISession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ConfirmUncommittedCoreAsync(session, () => _d.Dialogs.AskConnectionUncommittedAsync(_d.ConnectionLabel(session), ct), closing: false, ct);
    }

    /// <summary>
    /// 접속(세션)을 닫기 전(접속 끊기·세션 닫기·작업 공간 닫기·앱 종료)의 <see cref="ConfirmConnectionUncommittedAsync"/>: 같은 것을 묻되 커밋·롤백에 시간 상한을 둔다
    /// (<see cref="CloseEndTimeout"/>, PHC-3). 롤백이 상한을 넘으면 알리고 true(닫으면 서버가 되돌린다), 커밋이 넘으면 커밋하지 않고 닫을지 묻는다.
    /// </summary>
    public Task<bool> ConfirmConnectionCloseAsync(ISession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ConfirmUncommittedCoreAsync(session, () => _d.Dialogs.AskConnectionUncommittedAsync(_d.ConnectionLabel(session), ct), closing: true, ct);
    }

    /// <summary>
    /// <paramref name="session"/> 에 미커밋 트랜잭션이 있으면 커밋/롤백/취소를 묻고 고른 대로 한다. true = 이제 미커밋이 없다(닫아도 된다).
    /// 커밋·롤백이 실패하면(메시지 창에 남는다) 트랜잭션이 남으므로 false. <paramref name="subject"/> = 전용 세션 탭 이름(대화상자 글).
    /// </summary>
    public Task<bool> ConfirmUncommittedAsync(ISession session, string? subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ConfirmUncommittedCoreAsync(
            session,
            () => subject is null ? _d.Dialogs.AskUncommittedAsync(ct) : _d.Dialogs.AskUncommittedAsync(subject, ct),
            closing: true,
            ct);
    }

    /// <summary>
    /// 접속 콤보로 탭의 접속을 바꾸기 전(TSK-12-04): 이전 접속 <paramref name="session"/> 에 미커밋이 있으면 커밋 / 롤백 / 그대로 두고 바꾸기 / 취소를 묻는다.
    /// true = 바꿔도 된다(미커밋이 없거나, 커밋·롤백했거나, 그대로 두기로 했다). 그대로 둔 트랜잭션은 그 접속에 남아 앱 종료·접속 끊기 때 다시 묻는다.
    /// </summary>
    public Task<bool> ConfirmSwitchAsync(ISession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ConfirmUncommittedCoreAsync(session, () => _d.Dialogs.AskSwitchUncommittedAsync(_d.ConnectionLabel(session), ct), closing: false, ct);
    }

    /// <summary>
    /// <paramref name="closing"/> = 세션을 닫는 경로(종료·접속 끊기·세션·전용 탭 닫기): 커밋·롤백에 시간 상한을 두고(<see cref="EndForCloseAsync"/>),
    /// 「취소하고 닫기」로 기다리지 않기로 한 세션(<see cref="ConfirmCancelRunningAsync(IReadOnlyList{ISession}, CancellationToken)"/>)이 아직 바쁘면 묻지 않고 닫게 둔다.
    /// 접속 콤보로 바꾸기는 세션이 남으므로 지금처럼 끝까지 기다린다.
    /// </summary>
    private async Task<bool> ConfirmUncommittedCoreAsync(ISession session, Func<Task<UncommittedChoice>> ask, bool closing, CancellationToken ct)
    {
        if (closing && _abandoned.Contains(session.Id))
        {
            // 잠깐 쥔 잠금(Keep Alive·메타데이터)을 아직 도는 명령으로 보지 않게 조금 기다려 본다.
            if (!await WaitIdleAsync([session], BusyGrace, ct).ConfigureAwait(true))
            {
                return true; // 취소하고 닫기로 했고 아직 명령을 놓지 않았다: 커밋·롤백은 그 뒤에 줄 서므로 묻지 않는다(이미 알렸다)
            }

            _abandoned.Remove(session.Id); // 그사이 끝났다: 보통대로 묻는다
        }

        if (!State.IsTxnPending(session.Id) && !session.HasPendingTransaction)
        {
            return true;
        }

        if (session.State == SessionState.Broken || State.IsBroken(session.Id))
        {
            // 끊긴 세션은 커밋·롤백할 수 없고 서버가 이미 되돌렸다: 묻지 않고 알린 뒤 닫게 둔다(종료·끊기가 막히지 않게).
            State.ApplyTransaction(session.Id, false);
            Log.AddMessage(MessageKind.Warning, $"「{session.Profile.User}@{session.Profile.Name}」 접속이 끊겨 커밋하지 않은 변경은 서버에서 되돌려졌습니다.");
            return true;
        }

        var choice = await ask().ConfigureAwait(true);
        switch (choice)
        {
            case UncommittedChoice.Commit when closing:
                return await EndForCloseAsync(session, commit: true, ct).ConfigureAwait(true);
            case UncommittedChoice.Rollback when closing:
                return await EndForCloseAsync(session, commit: false, ct).ConfigureAwait(true);
            case UncommittedChoice.Commit:
                await CommitAsync(session, ct).ConfigureAwait(true);
                return !State.IsTxnPending(session.Id) && !session.HasPendingTransaction;
            case UncommittedChoice.Rollback:
                await RollbackAsync(session, ct).ConfigureAwait(true);
                return !State.IsTxnPending(session.Id) && !session.HasPendingTransaction;
            case UncommittedChoice.Keep: // 접속 콤보 묻기에서만 나온다: 트랜잭션은 그 접속에 남긴다
                return true;
            default:
                return false;
        }
    }

    /// <summary>조회 결과 창 보이기/숨기기(<c>oranda.window.toggleResultPane</c>). 숨겨도 탭의 결과는 남는다.</summary>
    public void ToggleResultPane() => _d.TogglePanel(ResultsPanelId);

    /// <summary>그리드 편집 반영 전 DML 미리보기. 사용자가 확인하면 true.</summary>
    public Task<bool> ConfirmPreviewAsync(string text, bool commit, CancellationToken ct) => _d.Dialogs.PreviewDmlAsync(text, commit, ct);

    /// <summary>그리드 반영 결과(커밋 여부 포함)를 메시지 창에 남긴다. 실패 행이 있으면 경고로 보인다.</summary>
    public void ReportGridApply(string notice)
        => Log.AddMessage(notice.Contains("반영하지 않았습니다", StringComparison.Ordinal) ? MessageKind.Warning : MessageKind.Info, notice);
}
