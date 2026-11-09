namespace Oranda.Core.Connection;

/// <summary>
/// 접속 대상 DBMS 종류(D-046, docs/design/tibero.md 2장). 세션 단위로 공급자·카탈로그 파일·오류 분류를 고르는 키다.
/// JSON 에는 이름 문자열로 쓴다(<c>"Oracle"</c>·<c>"Tibero"</c>). 값을 더할 때는 끝에 덧붙인다.
/// </summary>
public enum Dbms
{
    /// <summary>Oracle Database. 옛 접속 파일(<c>dbms</c> 키 없음)은 이 값으로 읽힌다.</summary>
    Oracle,

    /// <summary>TmaxTibero(6·7).</summary>
    Tibero,
}
