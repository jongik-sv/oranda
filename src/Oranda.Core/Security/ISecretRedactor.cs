namespace Oranda.Core.Security;

/// <summary>
/// 이력·로그에 쓰기 전 SQL 안의 비밀을 <c>***</c> 로 가린다(data-model 5.3). 구현: Oranda.Language(TSK-06-06).
/// L1 렉서 토큰 기준이며 문자열·주석 안은 건드리지 않는다.
/// </summary>
public interface ISecretRedactor
{
    /// <summary><c>IDENTIFIED BY</c>, <c>REPLACE</c>, <c>CONNECT u/p</c>, <c>PASSWORD</c> 다음 토큰을 가린 문장.</summary>
    string Redact(string sqlText);

    /// <summary>이름에 pass·pwd·secret·token 이 들어간 바인드면 true(값을 <c>***</c> 로 저장). 규칙은 <see cref="SensitiveBindNames"/>.</summary>
    bool IsSensitiveBindName(string bindName);
}
