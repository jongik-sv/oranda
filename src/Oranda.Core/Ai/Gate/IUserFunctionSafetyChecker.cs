using Oranda.Core.Sessions;

namespace Oranda.Core.Ai.Gate;

/// <summary>사용자 함수·패키지 함수 확인 결과(문장을 실제로 실행하는 도구에서만 쓴다).</summary>
public enum UserFunctionVerdict
{
    /// <summary>호출이 닿는 PL/SQL 객체 어디에도 자치 트랜잭션·외부 접근이 없다. 읽기 전용 트랜잭션 안에서 실행해도 된다.</summary>
    Safe,

    /// <summary>호출이 닿는 객체 중에 <c>PRAGMA AUTONOMOUS_TRANSACTION</c> 이 든 것이 있다(읽기 전용 트랜잭션이 막지 못한다).</summary>
    Autonomous,

    /// <summary>호출이 닿는 객체가 네트워크·파일·큐·작업 예약 패키지(UTL_HTTP 등)에 의존한다.</summary>
    External,

    /// <summary>확인하지 못했다(객체를 못 찾음·소스를 못 읽음·래핑됨·깊이/개수 상한 초과·조회 실패). 닫힌 쪽으로 거부한다.</summary>
    Unverified,
}

/// <summary>
/// 사용자 함수가 든 SELECT 를 실행하기 전에 그 함수가 닿는 PL/SQL 객체를 ALL_DEPENDENCIES·ALL_SOURCE 로 훑어 보는 확인기.
/// 게이트는 실행 도구(<c>benchmark_queries</c>)에서만 부른다. 해석·조회만 하는 도구는 확인 없이 허용한다.
/// 구현은 <c>Oranda.Language</c>. 남는 위험: <c>EXECUTE IMMEDIATE</c> 등 동적 SQL 로 숨은 호출은 의존 정보에 나타나지 않아 잡지 못한다.
/// </summary>
public interface IUserFunctionSafetyChecker
{
    /// <summary>
    /// 호출 이름(<c>F</c>, <c>PKG.F</c>, <c>SCHEMA.PKG.F</c> 형태, 대문자)이 가리키는 객체와 그것이 닿는 객체(깊이·개수 상한 안)를 확인한다.
    /// 어떤 실패도 예외 대신 <see cref="UserFunctionVerdict.Unverified"/> 로 돌려준다(취소는 예외).
    /// </summary>
    ValueTask<UserFunctionVerdict> CheckAsync(ISession db, IReadOnlyList<string> functions, string? defaultSchema, CancellationToken ct);
}
