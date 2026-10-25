namespace Oranda.Core.Tests.Execution;

/// <summary>TSK-07-05 시험 틀(TSK-07-01 이 만들어 둠): 정책 관문 ReadOnlyRole 규칙(R6 만 → 질의·트랜잭션 제어·EXPLAIN 허용, DML·DDL·PL/SQL·분류 불확실 거부, 다른 Role 과 함께 → 거부 없음, 프로필 읽기 전용이 먼저). 채울 때 틀 시험을 지우거나 바꿔도 된다.</summary>
public sealed class ReadOnlyRolePolicyTests
{
    [Fact(Skip = "TSK-07-05 가 채운다")]
    public void Placeholder()
    {
    }
}
