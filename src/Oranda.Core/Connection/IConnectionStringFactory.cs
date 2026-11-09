namespace Oranda.Core.Connection;

/// <summary>
/// 접속 프로필로 드라이버 연결 문자열을 조립한다. DBMS 마다 하나씩 구현한다(Oracle = Oranda.Oracle, Tibero = Oranda.Tibero).
/// 구현은 <c>[OrandaService(typeof(IConnectionStringFactory), Dbms = …)]</c> 로 등록하고, 쓰는 쪽은
/// DBMS 별 구현 고르기(<c>IDbmsServiceSelector</c>)로 프로필의 DBMS 에 맞는 것을 꺼낸다.
/// </summary>
public interface IConnectionStringFactory
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>
    /// 비밀번호를 넣은 연결 문자열. 로그나 화면에 내보내지 않는다.
    /// 연결 문자열 밖에서 연결 객체에 거는 드라이버 설정(예: Oracle 의 <c>UseHourOffsetForUnsupportedTimezone</c>)은 들어가지 않으므로,
    /// 실제 연결은 이 문자열로 직접 만들지 말고 공급자(세션 관리자·<see cref="IConnectionProbe"/>)에 맡긴다.
    /// </summary>
    string Build(ConnectionProfile profile, string password);
}
