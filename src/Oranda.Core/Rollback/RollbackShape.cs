namespace Oranda.Core.Rollback;

/// <summary>쓰기 문장의 동사(되돌리기 지점 설계 13.2). 이름은 <c>rollback_statement.verb</c> 열 값과 같다.</summary>
public enum DmlVerb
{
    Update,
    Delete,
    InsertValues,
    InsertSelect,
    Merge,
    PlSql,
    Other,
}

/// <summary>
/// 치환 변수를 처리한 뒤 글로 만든 쓰기 문장의 모양(설계 6.1·6.2). 이름의 따옴표 규칙은 <see cref="Journal.ChangeTarget"/> 과 같다.
/// <see cref="TableText"/>·<see cref="WhereText"/> 는 문장 글에서 그대로 잘라 낸 것이라 잠금 읽기(1단계)에 그대로 붙인다.
/// </summary>
public sealed record DmlShape(DmlVerb Verb)
{
    /// <summary>문장에 스키마가 없으면 null.</summary>
    public string? Owner { get; init; }

    public string? Name { get; init; }

    public string? DbLink { get; init; }

    /// <summary>문장에 적힌 표 참조 그대로(파티션 절·별칭 포함).</summary>
    public string? TableText { get; init; }

    public string? Alias { get; init; }

    /// <summary>WHERE 뒤 글(바인드 포함). WHERE 가 없으면 null.</summary>
    public string? WhereText { get; init; }

    public IReadOnlyList<string> SetColumns { get; init; } = [];

    public IReadOnlyList<string> InsertColumns { get; init; } = [];

    /// <summary>원복 대상이 아닌 이유 한 줄(예: <c>MERGE</c>). null = 대상.</summary>
    public string? NotTarget { get; init; }
}

/// <summary>쓰기 문장의 모양 분석(설계 6.1 의 2단계). 구현: Oranda.Language <c>DmlShapeAnalyzer</c>(RB-30).</summary>
public interface IDmlAnalyzer
{
    /// <summary>쓰기 문장이 아니면 null. 예외를 던지지 않는다.</summary>
    DmlShape? Analyze(string text);
}
