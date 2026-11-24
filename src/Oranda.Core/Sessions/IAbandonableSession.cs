namespace Oranda.Core.Sessions;

/// <summary>
/// 돌아오지 않는 명령을 버릴 수 있는 세션(PHC-7). 취소(드라이버 break)를 보냈는데 정해진 시간 안에 드라이버가 돌아오지 않으면
/// (서버 OOB 가 막혔거나 긴급 데이터가 사라지는 경로) 그 명령은 명령 잠금을 쥔 채 영영 돌아오지 않을 수 있다. 이때 부르는 쪽은 기다리기를 그만두고
/// <see cref="Abandon"/> 으로 세션을 버린다: 세션은 곧바로 <see cref="SessionState.Broken"/>(<c>SessionBroken</c> 이벤트, 다음 일은 다시 접속)이 되고,
/// 연결 닫기는 시간 제한(<see cref="TimedClose"/>)을 두고 백그라운드에서 시작한다(닫으면 막힌 수신이 깨어 일이 끝날 수 있다). 기다리지 않는다.
/// 이후 이 세션에 오는 질의는 잠금을 기다리지 않고 바로 실패한다. 서버 쪽 대기는 연결이 닫힐 때까지(또는 막는 실행이 끝날 때까지) 남을 수 있다.
/// </summary>
public interface IAbandonableSession
{
    /// <summary>세션을 버린다(위 설명). 이미 끊겼거나 닫혔으면 아무것도 안 한다. <paramref name="reason"/> = 끊김 이유(사람이 읽는 글).</summary>
    void Abandon(string reason);
}
