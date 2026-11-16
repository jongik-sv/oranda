using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

public sealed partial class DataCopyViewModel
{
    /// <summary>
    /// 실행·재시도 직전 확인: 점검한 작업·접속이 지금도 그대로인지 본다. 접속 목록을 다시 읽어 대상이 운영 접속이 되었거나
    /// 환경 라벨·읽기 전용·사용자·주소가 바뀌었거나 작업이 바뀌었으면 실행하지 않고 점검 결과를 지운다. 같으면 열려 있는 세션을 돌려준다.
    /// </summary>
    private async Task<CopySessions?> PreflightAsync(CancellationToken ct)
    {
        string? reason = null;
        if (_sessions is null || CurrentJob is null || _checkedJob is null || _checkedSource is null || _checkedTarget is null
            || !ReferenceEquals(_checkedJob, CurrentJob))
        {
            reason = "점검한 뒤 작업이 바뀌었습니다.";
        }
        else
        {
            await RefreshProfilesAsync(ct).ConfigureAwait(true);
            var source = _profiles.FirstOrDefault(p => p.Id == _checkedSource.Id && p.Name == _checkedSource.Name);
            var target = _profiles.FirstOrDefault(p => p.Id == _checkedTarget.Id && p.Name == _checkedTarget.Name);
            if (target is not null && !CopyTargetGuard.IsAllowedTarget(target))
            {
                reason = DataCopyText.ProductionTarget;
            }
            else if (source is null || target is null || !SameSnapshot(source, _checkedSource) || !SameSnapshot(target, _checkedTarget)
                || !SameConnection(_sessions.Source, source, readOnlySource: true) || !SameConnection(_sessions.Target, target))
            {
                reason = "점검한 뒤 접속 설정이 바뀌었습니다.";
            }
        }

        if (reason is null)
        {
            return _sessions;
        }

        Precheck.Invalidate();
        _checkedJob = null;
        await CloseSessionPairAsync().ConfigureAwait(true); // 바뀐 접속에는 옛 세션을 쓰지 않는다
        Say(reason + " 다시 점검하세요.", error: true);
        return null;
    }

    /// <summary>「실패 행만 다시 넣기」: 실행과 같은 직전 확인을 거친다.</summary>
    public async Task RetryFailedRowsAsync(CancellationToken ct = default)
    {
        if (IsBusy || Run.IsRunning || !Run.CanRetry || ReadOnlyJobBlocked())
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await PreflightAsync(ct).ConfigureAwait(true) is null)
            {
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        await Run.RetryFailedAsync(ct).ConfigureAwait(true);
        await CloseAbandonedSessionsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 2단계 → 3단계: 「복사 실행」. 점검 결과가 없거나 오류가 있거나 이 작업에 대한 점검이 아니면 아무것도 하지 않는다.
    /// 점검한 기간 그대로 실행한다.
    /// </summary>
    public async Task StartRunAsync(CancellationToken ct = default)
    {
        if (CurrentJob is null || IsBusy || Run.IsRunning || Precheck.Report is not { } report || !Precheck.CanRun
            || Step != DataCopyStep.Precheck || _checkedPeriod is not { } period || ReadOnlyJobBlocked())
        {
            return;
        }

        // 대상 계정이 둘 이상이거나 접속 계정이 아닌 계정에 쓰면 계정별 지울 표를 보이고 묻는다(결정 B-2 a, 기본 단추 취소).
        // 먼저 묻고 그 뒤에 직전 확인(PreflightAsync)을 한다: 대화상자가 떠 있는 사이에 접속 설정이 바뀌어도 바뀐 접속으로 실행하지 않는다.
        var job = CurrentJob;
        var sessionUser = (_checkedTarget?.User ?? (_sessions is { } pair ? OpenedWith(pair.Target).User : string.Empty)).ToUpperInvariant();
        if (RunConfirmation.For(job, sessionUser) is { } confirmation)
        {
            if (ConfirmRun is not { } ask)
            {
                Say(DataCopyText.RunConfirmMissing, error: true); // 묻지 못했으면 실행하지 않는다
                return;
            }

            if (!await ask(confirmation).ConfigureAwait(true))
            {
                Say("복사를 시작하지 않았습니다.");
                return;
            }

            if (!ReferenceEquals(job, CurrentJob) || Run.IsRunning || IsBusy || !Precheck.CanRun || Step != DataCopyStep.Precheck)
            {
                return; // 묻는 사이에 작업·상태가 바뀌었다
            }
        }

        IsBusy = true;
        CopySessions? sessions;
        try
        {
            sessions = await PreflightAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (sessions is null)
        {
            return;
        }

        // 확인한 작업·대상 접속 계정 그대로인지 한 번 더 본다(직전 확인이 점검한 것과 같은지 봤지만, 물은 내용과도 같아야 한다).
        if (!ReferenceEquals(job, CurrentJob)
            || !string.Equals(OpenedWith(sessions.Target).User.ToUpperInvariant(), sessionUser, StringComparison.Ordinal))
        {
            Precheck.Invalidate();
            _checkedJob = null;
            Say("확인한 뒤 작업이나 대상 접속이 바뀌었습니다. 다시 점검하세요.", error: true);
            return;
        }

        var decisions = Precheck.BuildDecisions();
        _checkUsedByRun = true; // 실행하면 대상이 바뀐다(만든 테이블·더한 열): 이 결과로 돌아가지 않고 다시 점검한다
        OnPropertyChanged(nameof(HasPrecheckResult));
        Step = DataCopyStep.Run;
        Say("복사하는 중…");
        await Run.StartAsync(sessions, job, report, decisions, period, ct).ConfigureAwait(true);
        if (Run.LastRun is { } last && CurrentJob is { } current && current.Id == job.Id)
        {
            // 점검한 작업과 같은 객체였다면(복사 도중 작업 내용이 바뀌지 않았다면) 마지막 실행 요약만 바뀐 새 객체를 점검한 작업으로도 맞춘다.
            // 그렇지 않으면 「실패 행만 다시 넣기」의 직전 확인이 같은 객체인지 비교해서 복사 직후에도 늘 거부한다.
            // 작업 내용이 실제로 바뀌었으면(다른 객체였으면) 점검한 작업은 그대로 두어 다시 점검을 요구한다.
            var unchangedSinceCheck = ReferenceEquals(_checkedJob, current);
            CurrentJob = current with { LastRun = last }; // 다음 저장이 옛 값으로 덮어쓰지 않게 열린 작업에 합친다
            if (unchangedSinceCheck)
            {
                _checkedJob = CurrentJob;
            }

            Editor.UpdateBase(CurrentJob);
        }

        Say(Run.StatusText, Run.Outcome is CopyOutcome.Failed);
        await CloseAbandonedSessionsAsync().ConfigureAwait(true);
        await Jobs.RefreshAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 실행기가 돌아오지 않는 명령 때문에 세션을 버렸으면(리뷰 PF1-3) 세션 쌍을 닫는다: 그 세션은 명령을 쥐고 있어 다음 점검·실행에 쓸 수 없다.
    /// 닫으면 커밋하지 않은 대상 변경은 되돌아간다(닫기는 시간 제한이 있다, <see cref="ConnectionManager.ConnectionService"/>).
    /// 점검 결과도 지워 다시 점검하게 한다(점검이 새 세션을 연다).
    /// </summary>
    private async Task CloseAbandonedSessionsAsync()
    {
        if (!Run.SessionsAbandoned)
        {
            return;
        }

        await _sessionGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await CloseSessionPairAsync().ConfigureAwait(true);
        }
        finally
        {
            _sessionGate.Release();
        }

        Precheck.Invalidate();
        _checkedJob = null;
        Say(DataCopyText.L(DataCopyText.SessionsAbandoned), error: true);
    }
}
