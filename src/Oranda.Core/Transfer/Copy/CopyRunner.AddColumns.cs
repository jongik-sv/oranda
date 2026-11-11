using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 실행기의 「대상에 열 추가」(PC1, 결정 D-066, <see cref="CopyTablePlan.AddColumns"/>). 사전 점검이 정해 미리 보기에 보인 <c>ALTER TABLE … ADD</c> 를 그대로 보낸다.
/// DDL(묵시 커밋)이라 표 만들기 뒤·대상 정리(DELETE)·적재 전에 보낸다. 개발 접속에서만 보낸다(사전 점검과 따로 한 번 더 막는다, 「없으면 만들기」와 같은 조건).
/// </summary>
public partial class CopyRunner
{
    /// <summary>진행 단계: 대상에 열 추가.</summary>
    public const string PhaseAddColumn = "열 추가";

    /// <summary>점검 계획에 더할 열이 있으면 대상이 개발 접속인지 한 번 더 본다. 아니면 아무것도 바꾸지 않고 멈춘다.</summary>
    private static void EnsureAddColumnsAllowed(CopySessions sessions, PrecheckReport report)
    {
        if (report.Order.Any(p => p.AddColumns.Count > 0) && !CopyTargetGuard.CanRecreate(sessions.Target.Profile))
        {
            throw new InvalidOperationException($"대상 접속 '{sessions.Target.Profile.Name}' 은 개발 접속이 아니라 「대상에 열 추가」로 열을 더할 수 없습니다.");
        }
    }

    /// <summary>
    /// 표마다(부모 → 자식) 더할 열의 문장을 보낸다. 하나라도 실패하면 그 표는 실패로 두고(정리·적재하지 않는다 — 넣는 문장이 없는 열을 쓴다) 다른 표는 계속한다.
    /// 더한 열은 적재가 실패해도 지우지 않는다(NULL 허용 열이라 남아도 기존 행·다른 쓰기에 영향이 없다, 결정 D-066). 결과 알림에 더한 열과 지우는 문장을 적는다.
    /// </summary>
    private async Task AddColumnsAsync(RunState run, CancellationToken ct)
    {
        foreach (var plan in run.Report.Order.Where(p => p.AddColumns.Count > 0))
        {
            ct.ThrowIfCancellationRequested();
            var table = run.Table(plan);
            if (table.Outcome == CopyOutcome.Failed)
            {
                continue;
            }

            run.Progress(table.Name, 0, null, PhaseAddColumn);
            foreach (var text in plan.AddColumns)
            {
                var r = await CleanupStatementAsync(run, CopyCommonStatements.RecreateDdl(text), CleanupTimeout);
                if (r.Status != ExecutionStatus.Succeeded)
                {
                    table.Fail("대상에 열을 더하지 못해 이 테이블을 복사하지 않았습니다: " + Describe(r));
                    break;
                }

                table.Note("대상에 열을 더했습니다: " + text);
            }
        }
    }
}
