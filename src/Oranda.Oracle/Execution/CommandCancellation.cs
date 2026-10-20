using System.Data.Common;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 실행 중인 서버 호출을 취소 토큰에 묶는다(EXEC-02). 토큰이 취소되면 취소하는 스레드에서 바로 <see cref="DbCommand.Cancel"/> 을 부른다.
/// <para>
/// ODP.NET 관리 드라이버의 Cancel 은 그 자리에서 서버에 브레이크를 보낸다(OOB 가 협상되면 TCP 긴급 데이터, 아니면 in-band 마커).
/// 서버가 브레이크를 언제 읽는지는 서버 설정에 달렸다: <c>DISABLE_OOB=ON</c> 인 서버는 PL/SQL 대기(<c>DBMS_SESSION.SLEEP</c>) 중에는
/// 읽지 않고, CPU 를 쓰는 동안에는 <c>BREAK_POLL_SKIP</c> 마다 확인한다(TSK-06-11).
/// </para>
/// </summary>
internal static class CommandCancellation
{
    /// <summary>호출이 끝나면 등록을 푼다(늦은 취소가 같은 연결의 다음 문장으로 새지 않게).</summary>
    public static CancellationTokenRegistration CancelOnToken(this DbCommand command, CancellationToken ct) =>
        ct.Register(static c => ((DbCommand)c!).Cancel(), command);
}
