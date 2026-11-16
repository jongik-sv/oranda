using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 1. 끄기 ----

    /// <summary>끌 목록을 먼저 기록(디스크)한 뒤 끈다(Review Focus 3). 하나라도 끄지 못하면 더 진행하지 않는다.</summary>
    private async Task DisableAsync(RunState run, CopyDecisions decisions, CancellationToken ct)
    {
        var chosen = DisableTargets(run.Report, decisions, run.Key);
        if (chosen.Count == 0)
        {
            return;
        }

        await run.Record!.RecordDisabledAsync([.. chosen.Select(c => c.Object)], ct);
        run.Log($"끌 목록을 실행 기록에 먼저 남겼습니다: 트리거 {chosen.Count(c => c.Object.Kind == DisabledKind.Trigger).ToString(CultureInfo.InvariantCulture)}개"
                + $", FK {chosen.Count(c => c.Object.Kind == DisabledKind.ForeignKey).ToString(CultureInfo.InvariantCulture)}개", phase: PhaseDisable);
        foreach (var (obj, key) in chosen)
        {
            run.Progress(key, 0, null, PhaseDisable); // 진행 줄은 규칙 키다(다른 계정의 같은 이름 표와 섞이지 않게)
            var stmt = obj.Kind == DisabledKind.Trigger
                ? _dialect.DisableTrigger(obj.Owner, obj.Name)
                : _dialect.DisableConstraint(obj);
            // 보내기 전에 목록에 넣는다: ALTER 가 도는 중에 중지되면 서버에서 꺼졌는지 알 수 없다. 꺼지지 않은 것을 켜는 것은 해가 없다.
            run.Disabled.Add(obj);
            run.DisabledKeys[obj] = key;
            var r = await ExecuteLoggedAsync(run, run.Target, stmt, NoBinds, ct);
            if (r.Status != ExecutionStatus.Succeeded)
            {
                // 확정적으로 끄지 못했다(거부·실패): 다시 켤 것도, 「다시 켜기」로 보일 것도 아니다.
                run.Disabled.Remove(obj);
                run.Abort($"{obj.Name} 을(를) 끄지 못해 복사를 시작하지 않았습니다: {Describe(r)}");
                return;
            }
        }
    }

    /// <summary>
    /// 결정에 따라 끌 트리거·FK 와 그 진행을 보일 규칙 키. 트리거는 규칙 키로 고른 표의 대상 계정 것(사전 점검이 그 계정 소유만 골랐다),
    /// FK 는 (계정, 이름)이 결정에 있는 것만이다: 이름만 보면 다른 계정의 같은 이름 FK 까지 끈다. 같은 FK 는 한 번만 넣는다.
    /// </summary>
    internal static IReadOnlyList<(DisabledObject Object, string Key)> DisableTargets(PrecheckReport report, CopyDecisions decisions,
                                                                                       Func<CopyTablePlan, string> keyOf)
    {
        var targets = new List<(DisabledObject, string)>();
        foreach (var plan in report.Order)
        {
            var key = keyOf(plan);
            if (plan.CanDisableTriggers && decisions.DisableTriggerTables.Contains(key))
            {
                targets.AddRange(plan.Triggers.Select(t => (new DisabledObject(DisabledKind.Trigger, plan.Target.Table.Owner, plan.Rule.Table, t), key)));
            }
        }

        var seen = new HashSet<(string, string, string)>();
        foreach (var plan in report.Order)
        {
            foreach (var fk in plan.DisablableForeignKeys)
            {
                if (decisions.DisableForeignKeys.Contains(ForeignKeyRef.Of(fk)) && seen.Add((fk.Owner, fk.Table, fk.Name)))
                {
                    targets.Add((fk, keyOf(plan)));
                }
            }
        }

        return targets;
    }

    // ---- 2. 대상 정리 ----

    /// <summary>
    /// 자식 → 부모 순서로 DELETE. 지우지 못한 표는 적재하지 않는다(겹친 행이 생기므로).
    /// <para>
    /// DELETE 하나하나도 멈춤 표시·대기 한도 아래 보낸다(리뷰 PF1-4: 행 잠금을 기다리는 DELETE 가 실제로 흔한 멈춤이다). 정리는 모든 테이블을 먼저
    /// 지우는 단계라, 한도를 넘으면 <b>그 DELETE 의 테이블만</b> 실패(지우지 않음 → 적재하지 않음)로 두고 다음 테이블을 계속 지운다:
    /// 취소한 문장은 그 문장만 되돌아가고(문장 단위 원자성) 앞 테이블의 DELETE 는 트랜잭션에 남는다. 부모 테이블의 DELETE 가 남은 자식 행 때문에
    /// 실패하면 그것도 보통 DELETE 실패로 그 테이블만 실패다. 취소한 DELETE 가 돌아오지 않으면 대상 세션을 버리고 실행을 멈춘다(적재로 가지 않는다).
    /// </para>
    /// </summary>
    private async Task DeleteTargetsAsync(RunState run, CancellationToken ct)
    {
        for (var i = run.Report.Order.Count - 1; i >= 0; i--)
        {
            var plan = run.Report.Order[i];
            var delete = run.Builder.TargetDelete(run.Key(plan));
            if (delete is null)
            {
                continue;
            }

            var table = run.Table(plan);
            if (table.Outcome == CopyOutcome.Failed)
            {
                continue; // 정리 전에 실패한 표(열 추가 실패 등)는 적재하지 않으므로 지우지도 않는다
            }

            run.Progress(table.Name, 0, null, PhaseDelete);
            var deleteStarted = Stopwatch.GetTimestamp();
            (PumpEnd End, ExecutionResult? Result, SideWatch Side) watched;
            try
            {
                watched = await WatchTargetCommandAsync(run, table, null, CopyWaitStep.Delete,
                    token => ExecuteLoggedAsync(run, run.Target, CopyCommonStatements.Delete(delete), delete.Binds, token), ct);
            }
            finally
            {
                table.Times.AddOutside(deleteStarted); // 테이블 전체 경과에 든다(원격 #155)
            }

            if (watched.End == PumpEnd.Stuck)
            {
                await AbandonStuckAsync(run, table, watched.Side, ct);
                return;
            }

            if (watched.End == PumpEnd.WaitLimit
                || (watched.Result is { Status: not ExecutionStatus.Succeeded } && watched.Side.LimitHit && !ct.IsCancellationRequested))
            {
                table.Fail(WaitLimitText(run, watched.Side));
                continue;
            }

            var r = watched.Result!;
            if (r.Status != ExecutionStatus.Succeeded)
            {
                table.Fail("대상 정리(DELETE)를 하지 못했습니다: " + Describe(r));
            }
            else
            {
                table.Deleted = true;
            }
        }
    }
}
