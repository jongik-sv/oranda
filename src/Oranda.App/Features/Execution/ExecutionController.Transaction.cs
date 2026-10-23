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

    /// <summary>활성 탭 세션을 커밋한다(<c>oranda.txn.commit</c>).</summary>
    public Task CommitAsync(CancellationToken ct = default) => CommitAsync(FocusedSession(), ct);

    /// <summary>활성 탭 세션을 롤백한다(<c>oranda.txn.rollback</c>).</summary>
    public Task RollbackAsync(CancellationToken ct = default) => RollbackAsync(FocusedSession(), ct);

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

            if (!await ConfirmConnectionUncommittedAsync(session, ct).ConfigureAwait(true))
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
        return ConfirmUncommittedCoreAsync(session, () => _d.Dialogs.AskConnectionUncommittedAsync(_d.ConnectionLabel(session), ct), ct);
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
            ct);
    }

    private async Task<bool> ConfirmUncommittedCoreAsync(ISession session, Func<Task<UncommittedChoice>> ask, CancellationToken ct)
    {
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
            case UncommittedChoice.Commit:
                await CommitAsync(session, ct).ConfigureAwait(true);
                return !State.IsTxnPending(session.Id) && !session.HasPendingTransaction;
            case UncommittedChoice.Rollback:
                await RollbackAsync(session, ct).ConfigureAwait(true);
                return !State.IsTxnPending(session.Id) && !session.HasPendingTransaction;
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
