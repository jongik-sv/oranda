using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>매퍼 최상위 요소 종류(PRD 2 범위). 문장 = select·insert·update·delete.</summary>
public enum MyBatisElementKind
{
    Select,
    Insert,
    Update,
    Delete,

    /// <summary>sql 조각(include 대상). 편집할 수 있다.</summary>
    Sql,

    /// <summary>resultMap. 목록에만 보이고 편집하지 않는다.</summary>
    ResultMap,

    /// <summary>cache·parameterMap 등 그 밖의 요소. 목록에만 보인다.</summary>
    Other,
}

/// <summary>
/// 매퍼의 최상위 요소 하나(MB-01, TRD 3.1). 계약(TSK-01-02) — 02-01 파서가 만들고, 02-02 계산기·03-* 화면이 읽는다.
/// <para>
/// 오프셋 기준(design.md 9.1): <see cref="ElementRange"/>·<see cref="BodyRange"/> 는 <b>매퍼 원문</b> 기준이다.
/// 계산 결과(<see cref="EvalResult"/>)의 범위는 모두 <b>문장 원문</b>(<see cref="Text"/>, 여는 태그의 <c>&lt;</c> 가 0) 기준이다.
/// </para>
/// <para>
/// 원문 문자열만으로 만들 수 있다: 계산기는 <see cref="Kind"/>·<see cref="Id"/>·<see cref="Text"/> 만 쓴다.
/// 매퍼 안 위치(<see cref="ElementRange"/>·<see cref="BodyRange"/>)는 파서가 채우고, 없으면 기본값(0, 0)이다.
/// </para>
/// </summary>
public sealed record MyBatisElement
{
    public required MyBatisElementKind Kind { get; init; }

    /// <summary>요소 이름 그대로(<c>select</c>·<c>resultMap</c>·<c>cache</c> …).</summary>
    public required string Name { get; init; }

    /// <summary>id 속성. 없으면 null.</summary>
    public string? Id { get; init; }

    /// <summary>여는 태그의 속성(이름 → 값, 엔티티를 푼 값). 원문 순서.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = [];

    /// <summary>요소 원문(여는 태그 ~ 닫는 태그, 빈 요소면 그 태그). 문장 탭 편집기 글과 같다.</summary>
    public required string Text { get; init; }

    /// <summary>매퍼 원문 안에서 요소 전체 범위.</summary>
    public TextRange ElementRange { get; init; }

    /// <summary>매퍼 원문 안에서 안쪽 본문 범위(여는 태그 뒤 ~ 닫는 태그 앞). 빈 요소면 길이 0.</summary>
    public TextRange BodyRange { get; init; }

    /// <summary>select·insert·update·delete 인가.</summary>
    public bool IsStatement => Kind is MyBatisElementKind.Select or MyBatisElementKind.Insert
        or MyBatisElementKind.Update or MyBatisElementKind.Delete;
}
