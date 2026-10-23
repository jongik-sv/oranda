using Oranda.Core.Text;

namespace Oranda.Core.Formatting;

/// <summary>
/// SQL 포매터(TRD 4.6). FMT-11 보장: 출력 토큰 열(공백·주석 위치 제외)이 입력과 같고 주석 수가 같아야 하며,
/// 다르면 결과를 버리고 <see cref="FormatResult.Failure"/> 를 채운다. 같은 입력을 두 번 포맷한 결과는 같다(멱등).
/// 구현: Oranda.Language <c>SqlFormatter</c>(TSK-05-01, 확장 규칙 TSK-05-02).
/// </summary>
public interface ISqlFormatter
{
    /// <param name="text">문서 전체</param>
    /// <param name="o">서식 옵션(프로필의 <c>options</c>)</param>
    /// <param name="range">null = 전체, 아니면 그 범위(선택·현재 문장)만</param>
    FormatResult Format(string text, FormatterOptions o, TextRange? range = null);
}

public enum FormatFailure
{
    None,
    TokenMismatch,
    CommentCountMismatch,
    NotIdempotent,
    InternalError,

    /// <summary>MyBatis 문장: 포맷할 수 없는 모양(닫히지 않은 태그·속성 따옴표, 낱말에 붙은 태그, 여러 줄 <c>#{…}</c>, 원문에 이미 있는 <c>/*@</c> 표식 등).</summary>
    MarkupUnsupported,

    /// <summary>MyBatis 문장: 포맷 전후 XML 구간(태그·CDATA·XML 주석의 원문·순서, 낱말에 붙은 경계)이 달라졌다.</summary>
    MarkupMismatch,
}

/// <summary>
/// 포맷 결과. <see cref="Failure"/> 가 <see cref="FormatFailure.None"/> 이 아니면 <see cref="Text"/> 는 입력 그대로다.
/// <see cref="Range"/> 는 바뀐 범위(원문 기준).
/// </summary>
public sealed record FormatResult(string Text, TextRange Range, FormatFailure Failure = FormatFailure.None)
{
    public bool Succeeded => Failure == FormatFailure.None;

    public string? Message { get; init; }
}
