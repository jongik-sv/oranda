using Oranda.Core.Execution;
using Oranda.Testing;

namespace Oranda.Tibero.Tests.Support;

/// <summary>모든 문장을 허용하는 정책. 시험 파일마다 따로 있던 같은 가짜를 모은 것.</summary>
internal sealed class AllowAll : IExecutionPolicy
{
    public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
}

/// <summary>Tibero 시험이 같이 쓰는 실행 가짜.</summary>
internal static class ExecutionFakes
{
    /// <summary>
    /// 묻지 않는 실행 대화: 확인 = 예, 치환 변수 = null(묻지 않음), 스크립트 오류 = 멈춤. 시험 파일마다 따로 있던 <c>NoQuestions</c> 와 같은 답이다.
    /// </summary>
    public static FakeExecutionInteraction NoQuestions() => new() { SubstitutionAnswer = null };
}
