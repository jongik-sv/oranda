using Oranda.Core.Execution;

namespace Oranda.Core.Connection;

/// <summary>
/// 접속 시험과 비밀번호 변경. DBMS 마다 하나씩 구현한다(Oracle = Oranda.Oracle, Tibero = Oranda.Tibero).
/// 구현은 <c>[OrandaService(typeof(IConnectionProbe), Dbms = …)]</c> 로 등록하고, 쓰는 쪽은
/// DBMS 별 구현 고르기(<c>IDbmsServiceSelector</c>)로 프로필의 DBMS 에 맞는 것을 꺼낸다.
/// </summary>
public interface IConnectionProbe
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>
    /// 저장된 프로필 그대로 연결 하나를 열어 서버 버전을 읽고, 이 DBMS 의 <c>IServerVersionPolicy</c> 로 지원 여부를 가린다.
    /// 드라이버용 변환(Oracle 의 <c>proxy[user]</c> 표기·tnsnames.ora 기술자 펼치기 등)은 구현이 한다 — 호출자가 미리 바꿔 넘기지 않는다.
    /// 드라이버 오류는 던지지 않고 실패 결과로 돌려준다. 취소되면 <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ServerProbeResult> ProbeAsync(ConnectionProfile profile, string password, CancellationToken ct);

    /// <summary>
    /// 비밀번호를 바꾼다. <paramref name="profile"/> 은 저장된 프로필 그대로다(<see cref="ProbeAsync"/> 와 같음, 드라이버용 변환은 구현이 한다).
    /// 프록시 프로필이면 프록시 사용자 자신의 비밀번호를 바꾼다.
    /// 드라이버 오류는 드라이버 예외 그대로 던진다(호출자가 <c>IDbErrorClassifier.FromException</c> 으로 <see cref="DbError"/> 로 옮긴다).
    /// </summary>
    Task ChangePasswordAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct);
}

/// <summary>
/// 접속 시험 결과. 지원하지 않는 서버 버전이면 <see cref="Succeeded"/> 가 false, <see cref="ServerVersion"/> 은 채우고
/// <see cref="Error"/> 는 null, <see cref="Message"/> 에 이유를 담는다.
/// 성공이어도 <see cref="Message"/> 에 안내 한 줄이 실릴 수 있다(예: 버전 뷰를 볼 권한이 없어 드라이버가 알려 준 버전으로 대신함).
/// </summary>
public sealed record ServerProbeResult(bool Succeeded, string? ServerVersion, DbError? Error, string? Message)
{
    /// <param name="serverVersion">서버 버전. 알 수 없으면 null.</param>
    /// <param name="notice">성공이지만 사용자에게 알릴 안내(없으면 null).</param>
    public static ServerProbeResult Ok(string? serverVersion, string? notice = null) => new(true, serverVersion, null, notice);

    public static ServerProbeResult Fail(DbError? error, string message) => new(false, null, error, message);

    /// <summary>접속은 됐지만 지원하지 않는 서버 버전.</summary>
    public static ServerProbeResult Unsupported(string? serverVersion, string reason) => new(false, serverVersion, null, reason);
}
