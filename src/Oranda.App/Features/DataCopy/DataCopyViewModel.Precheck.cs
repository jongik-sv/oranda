using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

public sealed partial class DataCopyViewModel
{
    /// <summary>
    /// 1단계 → 2단계: 입력을 확인하고 작업을 저장한 뒤 세션을 열어 사전 점검을 돌린다. 점검 결과가 남아 있고 그 뒤 규칙(작업 내용 지문)·기간·접속이
    /// 그대로면 다시 점검하지 않고 그 결과로 돌아간다(「n분 전 점검」, <see cref="MatchesCheck"/>). 바뀌었으면 다시 점검한다.
    /// </summary>
    public Task GoToPrecheckAsync(CancellationToken ct = default) => GoToPrecheckCoreAsync(recheck: false, ct);

    /// <summary>「다시 점검」: 남은 결과와 상관없이 지금 규칙으로 사전 점검을 다시 한다.</summary>
    public Task RecheckAsync(CancellationToken ct = default) => GoToPrecheckCoreAsync(recheck: true, ct);

    /// <summary>
    /// 규칙 편집의 「점검 결과 보기」: 남은 점검 결과를 다시 점검하지 않고 보인다. 점검한 뒤 규칙을 고쳤으면 결과는 그대로 보이되
    /// 「규칙이 바뀌어 다시 점검이 필요합니다」를 띄우고 「복사 실행」·「선택한 것 처리」를 막는다(<see cref="PrecheckViewModel.IsStale"/>).
    /// </summary>
    public void ShowPrecheckResult()
    {
        if (!HasPrecheckResult || IsBusy || Run.IsRunning || Precheck.IsRunning)
        {
            return;
        }

        CopyJob? job = null;
        try
        {
            job = Editor.BuildJob();
        }
        catch (InvalidOperationException)
        {
            // 지금 규칙으로 작업을 만들 수 없다: 결과는 옛것이다
        }

        var same = job is not null && MatchesCheck(job);
        if (same)
        {
            CurrentJob = _checkedJob; // 내용이 같다: 실행 직전 확인(같은 객체인지)이 점검한 작업을 보게 한다
        }

        Precheck.IsStale = !same;
        Precheck.RefreshCheckedAgo();
        Step = DataCopyStep.Precheck;
        Say(same ? DataCopyText.PrecheckReused(Precheck.CheckedAgoText) : DataCopyText.PrecheckStale, error: !same);
    }

    /// <summary>
    /// 지금 규칙으로 만든 작업이 남은 점검 결과를 그대로 쓸 수 있는가: 작업 내용 지문(저장 파일과 같은 JSON, 마지막 실행 요약 제외)·기간 경계·원본/대상 접속이
    /// 점검 때와 같고, 점검 세션이 그 접속으로 열려 있으며, 그 결과로 아직 실행하지 않았다.
    /// </summary>
    private bool MatchesCheck(CopyJob job)
    {
        if (Precheck.Report is null || _checkUsedByRun || _checkedJob is not { } checkedJob || _checkedPeriod is not { } period || _sessions is not { } sessions
            || _checkedSource is not { } checkedSource || _checkedTarget is not { } checkedTarget
            || Editor.Source?.Profile is not { } source || Editor.Target?.Profile is not { } target)
        {
            return false;
        }

        return string.Equals(Fingerprint(job), Fingerprint(checkedJob), StringComparison.Ordinal)
               && Editor.ResolvePeriod() == period
               && SameSnapshot(source, checkedSource) && SameSnapshot(target, checkedTarget)
               && SameConnection(sessions.Source, source, readOnlySource: true) && SameConnection(sessions.Target, target);
    }

    // 작업 내용 지문: 저장 파일과 같은 JSON(마지막 실행 요약은 뺀다 — 실행할 때마다 바뀌고 무엇을 복사하는지와 상관없다).
    private static string Fingerprint(CopyJob job) =>
        System.Text.Json.JsonSerializer.Serialize(job with { LastRun = null }, CopyJobJsonContext.Default.CopyJob);

    // 규칙을 고치지 않았으면 남은 점검 결과로 돌아간다(다시 점검하지 않는다). 돌아갔으면 참.
    private async Task<bool> TryReuseCheckAsync(CancellationToken ct)
    {
        if (Precheck.Report is null || Precheck.IsRunning || _checkUsedByRun || _checkedJob is null)
        {
            return false;
        }

        await RefreshProfilesAsync(ct).ConfigureAwait(true); // 접속의 환경 라벨은 열어 둔 사이에 바뀔 수 있다
        if (Editor.Validate() is not null)
        {
            return false;
        }

        CopyJob job;
        try
        {
            job = Editor.BuildJob();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!MatchesCheck(job))
        {
            return false;
        }

        CurrentJob = _checkedJob; // 내용이 같다: 실행 직전 확인(같은 객체인지)이 점검한 작업을 보게 한다
        Precheck.IsStale = false;
        Precheck.RefreshCheckedAgo();
        Step = DataCopyStep.Precheck;
        Say(DataCopyText.PrecheckReused(Precheck.CheckedAgoText));
        return true;
    }

    private async Task GoToPrecheckCoreAsync(bool recheck, CancellationToken ct)
    {
        if (CurrentJob is null || IsBusy || Run.IsRunning || ReadOnlyJobBlocked())
        {
            return; // 점검·세션 열기가 도는 중에 겹쳐 시작하지 않는다
        }

        IsBusy = true;
        try
        {
            if (!recheck && await TryReuseCheckAsync(ct).ConfigureAwait(true))
            {
                return;
            }

            Precheck.Invalidate(); // 입력 확인이 실패해 일찍 끝나도 옛 점검 결과로는 실행할 수 없다
            _checkUsedByRun = false;
            OnPropertyChanged(nameof(HasPrecheckResult));
            _checkedJob = null;
            await RefreshProfilesAsync(ct).ConfigureAwait(true); // 접속의 환경 라벨은 열어 둔 사이에 바뀔 수 있다
            if (Editor.Validate() is { } problem)
            {
                Say(problem, error: true);
                return;
            }

            var job = Editor.BuildJob();
            CurrentJob = job;
            if (_services.Store is { } store && !IsReadOnlyJob)
            {
                await store.SaveAsync(job, ct).ConfigureAwait(true);
            }

            if (await EnsureSessionsAsync(ct).ConfigureAwait(true) is not { } sessions)
            {
                return;
            }

            if (Run.IsRunning)
            {
                return; // 세션을 여는 사이에 실행이 시작됐다면 점검으로 바꾸지 않는다
            }

            Step = DataCopyStep.Precheck;
            Say("사전 점검 중…");
            _checkedPeriod = Editor.ResolvePeriod(); // 실행도 점검한 기간 그대로 쓴다(자정을 넘겨도 범위가 바뀌지 않게)
            _checkedJob = job; // 점검한 작업·접속의 지문(실행·재시도 직전에 같은지 본다)
            _checkedSource = Editor.Source!.Profile;
            _checkedTarget = Editor.Target!.Profile;
            await Precheck.RunAsync(sessions, job, _checkedPeriod, ct).ConfigureAwait(true);
            if (Precheck.Report is { } report)
            {
                Editor.ApplyPrecheckHints(job, report); // 대상에 이미 있는 테이블·만들 수 없는 테이블은 다음 전체 값 적용부터 「없으면 만들기」를 켜지 않는다
            }

            Say(Precheck.Notice.Length > 0 ? Precheck.Notice : Precheck.ProgressText, Precheck.HasErrors || Precheck.Notice.Length > 0);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
