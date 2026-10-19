namespace Oranda.Core.Connection;

/// <summary>Oracle 에서 SYSDBA/SYSOPER 로 붙는지. ODP.NET Managed 가 지원하는 값만 둔다.</summary>
public enum AuthMode
{
    Default,
    SysDba,
    SysOper,
}

/// <summary>
/// 접속 정보. <see cref="DataSource"/> 는 Easy Connect Plus 문자열
/// (예: <c>tcps://host:1522/svc?retry_count=3</c>, <c>host:1521/svc</c>) 또는 tnsnames.ora 별칭.
/// 비밀번호는 보관하지 않는다 — 연결을 만들 때 따로 넘긴다.
/// 연결 문자열 조립과 ODP.NET 프로세스 설정은 Oranda.Oracle 의 <c>ConnectionProfileExtensions</c> 가 맡는다.
/// </summary>
public sealed record ConnectionProfile(
    string Name,
    string User,
    string DataSource,
    AuthMode Auth = AuthMode.Default,
    string? TnsAdmin = null,
    string? WalletLocation = null,
    int ConnectTimeoutSeconds = 15);
