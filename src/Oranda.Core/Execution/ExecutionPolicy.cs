namespace Oranda.Core.Execution;

/// <summary>정책 관문 규칙(TRD 4.9). 앱 조립이 등록한다(Core 는 스캔 대상이 아니다). 소유: TSK-06-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
public sealed class ExecutionPolicy : IExecutionPolicy
{
    public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-01");
}
