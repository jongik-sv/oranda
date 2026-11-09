using Oranda.Core.Sessions;

namespace Oranda.Core.Execution;

/// <summary>
/// 스크립트 지시: SQL*Plus 처리기가 세션별로 기억하는 상태 중 실행 엔진이 함께 봐야 하는 것(WHENEVER SQLERROR EXIT, DEFINE 값).
/// <see cref="ISqlPlusCommandProcessor"/> 구현이 함께 구현한다. 앱 조립에서 실행 엔진이 받는 처리기는 Core 디스패처라서
/// 실행 엔진은 구현 형식이 아니라 이 계약으로 묻는다(D-046, K0).
/// </summary>
public interface IScriptDirectives
{
    /// <summary>WHENEVER SQLERROR EXIT 가 켜져 있어 오류 뒤 스크립트를 멈춰야 하는지.</summary>
    bool ExitOnError(ISession s);

    /// <summary>WHENEVER SQLERROR EXIT COMMIT|ROLLBACK 의 트랜잭션 처리(스크립트가 오류로 멈출 때).</summary>
    Task ApplyOnErrorTransactionAsync(ISession s, CancellationToken ct);

    /// <summary>세션의 DEFINE 값(없으면 null). 실행 엔진의 치환 변수도 이 값을 본다.</summary>
    string? GetDefine(ISession s, string name);

    /// <summary>실행 엔진이 받은 &amp;&amp;이름 값을 같은 DEFINE 저장소에 기억한다.</summary>
    void SetDefine(ISession s, string name, string value);
}
