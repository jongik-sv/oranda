namespace Oranda.Core.Formatting;

/// <summary>
/// MyBatis 문장 포매터. 문장 탭 글(매퍼 요소 하나 — <c>&lt;select&gt;</c>·<c>&lt;insert&gt;</c>·<c>&lt;update&gt;</c>·<c>&lt;delete&gt;</c>·<c>&lt;sql&gt;</c>)의
/// 바깥 태그는 원문 그대로 두고 본문 SQL 만 포맷한다. 동적 태그 줄은 감싼 첫 SQL 줄과 같은 칸, 닫는 태그는 짝 여는 태그와 같은 칸에 둔다.
/// <see cref="ISqlFormatter.Format"/> 의 <c>range</c> 는 쓰지 않는다(늘 문장 전체). 문장 끝 옵션은 늘 Keep 으로 다룬다.
/// FMT-11 에 더해 XML 구간 열·붙은 경계·멱등을 검사하고, 하나라도 어긋나면 원문을 그대로 두고 실패 사유
/// (<see cref="FormatFailure.MarkupUnsupported"/>·<see cref="FormatFailure.MarkupMismatch"/> 등)를 채운다.
/// 구현: Oranda.Language <c>MyBatisStatementFormatter</c>. 매퍼 파일 전체 포맷은 범위 밖이다.
/// </summary>
public interface IMyBatisStatementFormatter : ISqlFormatter
{
}
