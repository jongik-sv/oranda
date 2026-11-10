using System.Globalization;
using System.Runtime.ExceptionServices;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 동시 테이블 복사(PF2, 조정 결정 71·72). 단위 = 레인(원본 세션 + 대상 세션 한 쌍). 첫 레인은 주 쌍(<see cref="CopySessions.Source"/>·<see cref="CopySessions.Target"/>),
/// 둘째부터는 <see cref="CopySessions.Workers"/> 다. 레인 안에서는 PF1 적재 흐름(<see cref="LoadTableAsync"/> → <see cref="PumpTableAsync"/>)을 그대로 쓴다.
/// <para>
/// 순서: 끄기·다시 만들기·없으면 만들기·정리(DELETE)는 예전처럼 주 세션에서 직렬로 한 뒤, 정리를 바로 커밋한다(커밋하지 않으면 다른 레인의 같은 키 INSERT 가
/// 커밋 안 된 DELETE 를 기다리는데, 주 세션은 레인을 기다리는 중이라 DB 가 교착으로 잡지 못하고 끝없이 멈춘다). 그 뒤 FK 단계(<see cref="PrecheckReport.Stages"/>)마다
/// 테이블을 레인에 나눠 적재한다(단계 사이 장벽: 앞 단계가 모두 끝난 뒤 다음 단계). 시퀀스·다시 켜기·되살리기·건수 비교는 모든 레인이 끝난 뒤 주 세션에서 한다.
/// </para>
/// <para>
/// 레인마다 따로인 것(세션 쌍·지금 표·단계·버린 세션)은 <see cref="RunLane"/> 에 있고, 레인 흐름이 <see cref="RunState.UseLane"/> 로 건다. 표 상태·알림·실패 행·기록은 공유다.
/// 실패 행 상한은 테이블마다(예전과 같다). 한 테이블이 실패해도 나머지는 계속한다. 전체 중단(스냅샷 너무 오래됨·돌아오지 않는 명령)이 나면 다른 레인은 다음 배치 앞에서
/// 자기 테이블을 멈춰 되돌린다. 레인 하나의 예상 못 한 예외는 나머지 레인을 취소하고 그 예외를 올린다.
/// </para>
/// <para>
/// LOB 열이 있는 테이블은 한 번에 하나만 적재한다(대량 읽기가 LOB 를 통째로 메모리에 올린다 — PF1 이 LOB 테이블 버퍼를 1 배치로 줄였지만 레인마다 쌓이면 다시 커진다).
/// 다른 레인은 그동안 LOB 없는 테이블을 먼저 가져간다.
/// </para>
/// </summary>
public partial class CopyRunner
{
    /// <summary>
    /// 둘째 레인부터의 세션 쌍을 확인한다(주 쌍의 <see cref="EnsureSessions"/> 와 같은 조건 + 같은 DB·계정, 서로 다른 세션).
    /// 레인 DbKey 가 주 세션과 다르면 다른 DB·계정에 읽고 쓰게 된다.
    /// </summary>
    private void EnsureWorkers(CopySessions sessions)
    {
        var seen = new HashSet<ISession>(ReferenceEqualityComparer.Instance) { sessions.Source, sessions.Target };
        foreach (var worker in sessions.Workers)
        {
            ArgumentNullException.ThrowIfNull(worker);
            EnsureSessions(worker);
            if (!string.Equals(worker.Source.DbKey, sessions.Source.DbKey, StringComparison.Ordinal)
                || !string.Equals(worker.Target.DbKey, sessions.Target.DbKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("동시 복사 레인 세션이 주 세션과 다른 DB·계정에 열려 있습니다. 같은 접속으로 다시 여세요.");
            }

            if (!seen.Add(worker.Source) || !seen.Add(worker.Target))
            {
                throw new InvalidOperationException("동시 복사 레인 세션이 다른 레인과 같은 세션입니다. 레인마다 세션을 따로 여세요.");
            }
        }
    }

    /// <summary>레인 수를 정하고 둘째 레인부터의 상태를 만든다. 판정(<see cref="CopyConcurrency.ForRun"/>)을 돌려준다(시작 로그).</summary>
    private CopyConcurrencyDecision PrepareLanes(RunState run, CopySessions sessions)
    {
        var decision = CopyConcurrency.ForRun(run.Job, run.Report, _sourceDbms);
        run.LaneCount = Math.Max(1, Math.Min(decision.Tables, 1 + sessions.Workers.Count));
        for (var i = 0; i < run.LaneCount - 1; i++)
        {
            run.Workers.Add(new RunLane(i + 2, sessions.Workers[i].Source, sessions.Workers[i].Target));
        }

        return decision;
    }

    /// <summary>시작 로그 둘째 줄: 동시 수를 2 이상 골랐는데 한 테이블씩 도는 이유, 또는 열린 레인 세션이 모자라 줄인 수. 1 을 골랐으면 아무것도 남기지 않는다.</summary>
    private static void LogLanes(RunState run, CopyConcurrencyDecision decision)
    {
        if (CopyConcurrency.Requested(run.Job.Options) <= 1)
        {
            return;
        }

        if (decision.Tables <= 1)
        {
            run.Log("한 테이블씩 복사합니다: " + decision.Reason);
        }
        else if (run.LaneCount < decision.Tables)
        {
            run.Log($"동시 테이블 {decision.Tables.ToString(CultureInfo.InvariantCulture)}개로 정했지만 레인 세션이 {(run.Workers.Count + 1).ToString(CultureInfo.InvariantCulture)}쌍만 열려"
                    + $" {run.LaneCount.ToString(CultureInfo.InvariantCulture)}개로 줄여 복사합니다.", CopyLogKind.Warning);
        }
    }

    // ---- 레인 적재 ----

    /// <summary>정리(DELETE)를 확정한 뒤 FK 단계마다 레인으로 적재한다(<see cref="LoadAllAsync"/> 대신).</summary>
    private async Task LoadInLanesAsync(RunState run, CopyRunControl control, CancellationToken ct)
    {
        if (!await CommitDeletesAsync(run, ct))
        {
            return;
        }

        // 결과·건수 비교 순서를 한 테이블씩 할 때와 같게 미리 만든다(레인은 표 상태를 새로 만들지 않는다: 사전을 함께 쓰므로).
        foreach (var plan in run.Report.Order)
        {
            run.Table(plan);
        }

        var stages = LaneStages(run);
        run.InLanes = true;
        try
        {
            for (var i = 0; i < stages.Count; i++)
            {
                if (run.Aborted)
                {
                    return;
                }

                var todo = stages[i].Where(p => run.Table(p).Outcome != CopyOutcome.Failed).ToList();
                if (todo.Count == 0)
                {
                    continue;
                }

                var width = Math.Min(run.LaneCount, todo.Count);
                var stage = new CopyStage(width, i + 1, stages.Count);
                run.Enter(PhaseLoad);
                run.Log(CopyLogText.Stage(stage) + ": " + string.Join(", ", todo.Select(p => run.Key(p))));
                run.ReportStage(stage);
                await RunStageAsync(run, todo, width, control, ct);
            }
        }
        finally
        {
            run.InLanes = false;
        }
    }

    /// <summary>
    /// 동시 복사 전에 정리(DELETE)를 주 대상 세션에서 커밋한다. 지운 테이블이 없으면 보내지 않는다. 확정하면 <see cref="RunState.DeletesPersisted"/> 를 켠다
    /// (첫 테이블 커밋 전에 중지해도 지운 행은 돌아오지 않는다 — 결과에 「대상 정리는 확정됐지만 적재를 끝내지 못했습니다」가 남는다). 커밋하지 못하면 전체를 멈춘다.
    /// </summary>
    private async Task<bool> CommitDeletesAsync(RunState run, CancellationToken ct)
    {
        if (!run.Tables.Values.Any(t => t.Deleted))
        {
            return true;
        }

        run.Enter(PhaseDelete);
        var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), ct, "동시 복사 전에 정리(DELETE) 확정");
        if (commit.Status == ExecutionStatus.Succeeded)
        {
            run.DeletesPersisted = true;
            return true;
        }

        if (commit.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        run.Abort("동시 복사 전에 대상 정리(DELETE)를 확정하지 못해 적재하지 않았습니다: " + Describe(commit));
        return false;
    }

    /// <summary>
    /// 레인 단계(규칙 키 → 계획). 단계 안은 예상 건수 큰 것부터(같으면 부모 → 자식 순서). 점검 결과의 단계에 없는 계획은 맨 뒤에 한 테이블씩 둔다.
    /// </summary>
    private static List<List<CopyTablePlan>> LaneStages(RunState run)
    {
        var byKey = run.Report.Order.Select((p, i) => (Plan: p, Index: i)).ToDictionary(x => run.Key(x.Plan), StringComparer.Ordinal);
        long Estimated(CopyTablePlan p) => run.Report.EstimatedRows.TryGetValue(run.Key(p), out var n) ? n : 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var stages = new List<List<CopyTablePlan>>();
        foreach (var stage in run.Report.Stages ?? [])
        {
            var plans = stage.Where(k => byKey.ContainsKey(k) && used.Add(k)).Select(k => byKey[k])
                .OrderByDescending(x => Estimated(x.Plan)).ThenBy(x => x.Index).Select(x => x.Plan).ToList();
            if (plans.Count > 0)
            {
                stages.Add(plans);
            }
        }

        stages.AddRange(run.Report.Order.Where(p => !used.Contains(run.Key(p))).Select(p => new List<CopyTablePlan> { p }));
        return stages;
    }

    /// <summary>
    /// 한 단계를 <paramref name="width"/> 개 레인으로 적재하고 모두 끝날 때까지 기다린다. 레인 하나가 예상 못 한 예외로 끝나면 나머지를 취소하고 그 예외를 다시 올린다.
    /// 중지면 모든 레인이 끝난 뒤 중지 예외를 올린다.
    /// </summary>
    private async Task RunStageAsync(RunState run, List<CopyTablePlan> plans, int width, CopyRunControl control, CancellationToken ct)
    {
        var queue = new StageQueue([.. plans.Select(p => new LaneItem(p, HasLobColumns(p)))]);
        using var lanes = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<Task>(width);
        for (var i = 0; i < width; i++)
        {
            var lane = i == 0 ? null : run.Workers[i - 1]; // 첫 레인 = 주 쌍(레인 상태 없이 주 값을 쓴다)
            tasks.Add(Task.Run(() => RunLaneAsync(run, lane, queue, control, lanes), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // 아래에서 레인마다 본다(WhenAll 은 첫 예외만 올린다).
        }

        var fault = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions)
            .FirstOrDefault(e => e is not OperationCanceledException);
        if (fault is not null)
        {
            ExceptionDispatchInfo.Capture(fault).Throw();
        }

        if (tasks.Any(t => t.IsCanceled || t.IsFaulted))
        {
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException("동시 복사 레인이 취소됐습니다.");
        }
    }

    /// <summary>레인 하나: 단계 줄에서 테이블을 하나씩 가져와 적재한다. 전체 중단·이 레인의 세션을 버렸으면 더 가져가지 않는다.</summary>
    private async Task RunLaneAsync(RunState run, RunLane? lane, StageQueue queue, CopyRunControl control, CancellationTokenSource lanes)
    {
        if (lane is not null)
        {
            run.UseLane(lane);
            lane.Used = true;
        }

        var token = lanes.Token;
        try
        {
            while (!run.Aborted && !run.SourceAbandoned && !run.TargetAbandoned)
            {
                if (await queue.TakeAsync(token) is not { } item)
                {
                    return;
                }

                var table = run.Table(item.Plan);
                lane?.Tables.Add(table);
                try
                {
                    await LoadTableAsync(run, item.Plan, table, control, token);
                }
                finally
                {
                    queue.Done(item);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await lanes.CancelAsync(); // 나머지 레인을 멈춘다. 이 예외는 단계가 다시 올린다.
            }
            catch (AggregateException)
            {
                // 취소 처리기(드라이버 취소)의 실패: 이 레인의 예외를 가리지 않는다. 멈추지 않는 명령은 감시가 그 세션을 버린다.
            }

            throw;
        }
    }

    /// <summary>
    /// 배치를 보내기 전(<see cref="FlushAsync"/> 앞): 레인이 도는 중에 다른 레인이 실행 전체를 멈췄으면 이 테이블을 실패로 두고 true(배치를 보내지 않는다).
    /// 부른 흐름이 표를 되돌린다(<see cref="LoadTableAsync"/>). 한 테이블씩 복사할 때는 늘 false 다(예전과 같다).
    /// </summary>
    private static bool StopForOtherLaneAbort(RunState run, TableState table)
    {
        if (!run.InLanes || !run.Aborted || table.Outcome == CopyOutcome.Failed)
        {
            return false;
        }

        table.Fail("다른 테이블에서 실행 전체를 멈춰 이 테이블을 멈추고 되돌렸습니다.");
        return true;
    }

    /// <summary>복사 열에 LOB(CLOB·NCLOB·BLOB)가 있는가(한 번에 하나만 적재한다). 형식을 모르면 LOB 가 아닌 것으로 본다(<see cref="HasLob"/> 와 같은 판정).</summary>
    private bool HasLobColumns(CopyTablePlan plan)
    {
        var copied = plan.Columns.ToHashSet(StringComparer.Ordinal);
        foreach (var column in plan.Source.Columns.Where(c => copied.Contains(c.Name)))
        {
            try
            {
                if (_sourceCodec.KindOf(column.DataType) is BulkKind.Clob or BulkKind.Blob)
                {
                    return true;
                }
            }
            catch (NotSupportedException)
            {
                // 대량 읽기가 모르는 형식: 열 배치(RowShape)가 따로 막는다.
            }
        }

        return false;
    }

    // ---- 레인 마무리 ----

    /// <summary>
    /// 둘째 레인부터 쓴 레인마다 대상 트랜잭션을 끝내고(중지·예외·전체 중단 = ROLLBACK, 아니면 COMMIT) 원본 읽기 전용 트랜잭션을 닫는다.
    /// 주 쌍(첫 레인)은 <see cref="FinishAsync"/> 가 끝낸다. 이것이 끝난 뒤에만 다시 켜기·되살리기·시퀀스·건수 비교가 돈다.
    /// 끝내지 못한 레인이 있으면 <see cref="RunState.LaneLeftOpen"/> 을 켠다(그 레인의 세션은 앱이 닫아 서버가 되돌린다).
    /// </summary>
    private async Task FinishLanesAsync(RunState run, bool faulted)
    {
        foreach (var lane in run.Workers.Where(l => l.Used))
        {
            if (!await FinishLaneAsync(run, lane, faulted))
            {
                run.LaneLeftOpen = true;
            }
        }
    }

    /// <summary>레인 하나를 끝낸다. 대상 트랜잭션을 끝냈으면 true. 단계마다 새 토큰을 쓴다(중지 토큰은 이미 취소됐을 수 있다).</summary>
    private async Task<bool> FinishLaneAsync(RunState run, RunLane lane, bool faulted)
    {
        run.UseLane(lane); // 이 메서드가 끝나면 부른 흐름은 주 값으로 돌아간다
        run.Enter(PhaseFinish);
        var label = "레인 " + lane.Number.ToString(CultureInfo.InvariantCulture);
        var ended = false;
        if (lane.TargetAbandoned)
        {
            run.Notices.Add($"{label}: 대상 세션의 명령이 돌아오지 않아 그 레인의 대상 트랜잭션을 끝내지 못했습니다. 복사 창을 닫아 세션을 정리하면 커밋하지 않은 변경은 되돌아갑니다.");
        }
        else
        {
            try
            {
                ended = await EndLaneTransactionAsync(run, lane, faulted, label);
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                run.Notices.Add($"{label}: 대상 트랜잭션을 끝내지 못했습니다: " + SafeError(ex.Message));
            }
        }

        if (lane.SourceAbandoned)
        {
            run.Notices.Add($"{label}: 원본 세션의 명령이 돌아오지 않아 원본 읽기 전용 트랜잭션을 닫지 않았습니다. 복사 창을 닫아 세션을 정리하세요.");
            return ended;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            var rollback = await ControlLoggedAsync(run, run.Source, CopyCommonStatements.Rollback(), step.Token, "이 레인의 원본 읽기 전용 트랜잭션 닫기");
            if (rollback.Status != ExecutionStatus.Succeeded)
            {
                run.Notices.Add($"{label}: 원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + Describe(rollback));
            }
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add($"{label}: 원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + SafeError(ex.Message));
        }

        return ended;
    }

    /// <summary>
    /// 레인의 대상 트랜잭션 끝내기(<see cref="EndTargetTransactionAsync"/> 의 레인판). 테이블마다·배치마다 커밋이라 남은 것은 거의 없다:
    /// 커밋하면 그 레인 테이블의 남은 실패 행을 확정하고, 되돌리면 버린다.
    /// </summary>
    private async Task<bool> EndLaneTransactionAsync(RunState run, RunLane lane, bool faulted, string label)
    {
        if (!(run.Stopped || faulted || run.Aborted))
        {
            using var commitStep = new CancellationTokenSource(CleanupTimeout);
            var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), commitStep.Token, "이 레인의 대상 트랜잭션 끝내기");
            if (commit.Status == ExecutionStatus.Succeeded)
            {
                foreach (var t in lane.Tables.Where(t => !t.WasRolledBack && (t.PendingFailed.Count > 0 || t.LoadCompleted)))
                {
                    await ConfirmFailedRowsAsync(run, t);
                }

                return true;
            }

            run.CommitFailed = true;
            run.Notices.Add($"{label}: 대상 커밋을 하지 못해 되돌립니다: " + Describe(commit));
        }

        using var step = new CancellationTokenSource(LongCleanupTimeout);
        var r = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Rollback(), step.Token, "이 레인의 확정하지 않은 대상 변경 되돌리기");
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add($"{label}: 대상 롤백을 하지 못했습니다: " + Describe(r));
            return false;
        }

        foreach (var t in lane.Tables.Where(t => t.PendingFailed.Count > 0 || (!t.Confirmed && t.FailedRows > 0)))
        {
            t.RolledBack();
        }

        return true;
    }

    // ---- 상태 ----

    /// <summary>
    /// 둘째 레인부터의 상태(<see cref="RunState.UseLane"/>): 세션 한 쌍, 지금 표·단계(작업 로그 칸), 버린 세션, 이 레인이 적재한 테이블.
    /// 첫 레인은 주 쌍이라 이것이 없다(<see cref="RunState"/> 의 주 값을 쓴다).
    /// </summary>
    private sealed class RunLane(int number, ISession source, ISession target)
    {
        /// <summary>레인 번호(2부터, 로그·알림 글).</summary>
        public int Number { get; } = number;

        public ISession Source { get; } = source;

        public ISession Target { get; } = target;

        public string Phase { get; set; } = PhaseLoad;

        public string? Table { get; set; }

        public bool SourceAbandoned { get; set; }

        public bool TargetAbandoned { get; set; }

        /// <summary>테이블을 하나라도 가져갔다(마무리할 트랜잭션이 있을 수 있다).</summary>
        public bool Used { get; set; }

        /// <summary>이 레인이 적재한 테이블(레인 마무리가 실패 행을 확정하거나 버린다). 이 레인 흐름만 쓴다.</summary>
        public List<TableState> Tables { get; } = [];
    }

    private sealed record LaneItem(CopyTablePlan Plan, bool Lob);

    /// <summary>
    /// 한 단계의 테이블 줄. 레인이 앞에서부터 가져가되, LOB 테이블이 이미 돌고 있으면 LOB 없는 테이블을 먼저 가져간다.
    /// 남은 것이 모두 LOB 테이블이면 돌던 LOB 테이블이 끝날 때까지 기다린다.
    /// </summary>
    private sealed class StageQueue(List<LaneItem> items)
    {
        private readonly object _gate = new();
        private int _lobRunning;
        private TaskCompletionSource? _lobFreed;

        public async Task<LaneItem?> TakeAsync(CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (items.Count == 0)
                    {
                        return null;
                    }

                    var pick = items.FirstOrDefault(i => !i.Lob || _lobRunning == 0);
                    if (pick is not null)
                    {
                        items.Remove(pick);
                        if (pick.Lob)
                        {
                            _lobRunning++;
                        }

                        return pick;
                    }

                    _lobFreed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _lobFreed.Task;
                }

                await wait.WaitAsync(ct);
            }
        }

        public void Done(LaneItem item)
        {
            if (!item.Lob)
            {
                return;
            }

            TaskCompletionSource? freed;
            lock (_gate)
            {
                _lobRunning--;
                freed = _lobFreed;
                _lobFreed = null;
            }

            freed?.TrySetResult();
        }
    }
}
