namespace Oranda.Core.MyBatis;

/// <summary>시나리오 값 하나의 종류.</summary>
public enum MyBatisScenarioValueKind
{
    Null,
    Text,
    Number,
    Boolean,

    /// <summary>목록(foreach collection). 항목은 <see cref="MyBatisScenarioValue.Items"/>, 항목 종류는 <see cref="MyBatisScenarioValue.ItemKind"/>.</summary>
    List,
}

/// <summary>
/// 파라미터 값 하나(MB-04). Core 는 Language 를 모르므로 계산기 값(<c>object?</c>)이 아니라 글자로 둔다.
/// 숫자는 불변 문화권 글자(<c>7369</c>, <c>1.5</c>), 참·거짓은 <c>true</c>·<c>false</c>.
/// </summary>
public sealed record MyBatisScenarioValue(
    MyBatisScenarioValueKind Kind,
    string? Text = null,
    IReadOnlyList<string>? Items = null,
    MyBatisScenarioValueKind ItemKind = MyBatisScenarioValueKind.Text)
{
    public static MyBatisScenarioValue Null { get; } = new(MyBatisScenarioValueKind.Null);
}

/// <summary>
/// 시나리오 = 문장 하나의 이름 있는 파라미터 값 묶음(MB-04, TRD 3.3, 표 <c>mybatis_scenario</c>). 계약(TSK-01-02).
/// 문장은 매퍼 경로 + 문장 id 로 찾고, 매퍼를 옮겼으면 namespace + 문장 id 로 찾는다.
/// <see cref="Values"/> 에는 비밀 이름(pass·pwd·secret·token 이 든 이름)의 값을 넣지 않는다(저장소가 <see cref="MyBatisScenarioValue.Null"/> 로 바꾼다).
/// </summary>
public sealed record MyBatisScenario
{
    /// <summary>저장소 ID. 아직 저장 안 한 새 시나리오면 0.</summary>
    public long Id { get; init; }

    /// <summary>매퍼 파일 절대 경로(저장소가 정규화한다).</summary>
    public required string MapperPath { get; init; }

    public string? Namespace { get; init; }

    public required string StatementId { get; init; }

    /// <summary>시나리오 이름(한글 가능). 예 「이름 검색」, 「로그 14:05」.</summary>
    public required string Name { get; init; }

    /// <summary>파라미터 이름(점 경로) → 값.</summary>
    public IReadOnlyDictionary<string, MyBatisScenarioValue> Values { get; init; } = new Dictionary<string, MyBatisScenarioValue>(StringComparer.Ordinal);

    /// <summary>목록 순서(작을수록 앞).</summary>
    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
