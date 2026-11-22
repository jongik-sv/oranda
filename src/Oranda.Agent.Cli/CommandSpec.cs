namespace Oranda.Agent.Cli;

/// <summary>명령줄 값이 도구 인자로 들어갈 때의 꼴.</summary>
internal enum ValueKind
{
    /// <summary>글 그대로.</summary>
    String,

    /// <summary>정수(틀리면 사용법 오류).</summary>
    Integer,

    /// <summary>값 없는 옵션 → <c>true</c>.</summary>
    Flag,

    /// <summary>값 없는 옵션 → <c>false</c>(<c>--no-lint</c>).</summary>
    FalseFlag,

    /// <summary>쉼표로 나눈 글 배열. 옵션을 여러 번 줘도 이어 붙는다.</summary>
    List,

    /// <summary>JSON 객체 글(<c>--binds-json</c>).</summary>
    JsonObject,
}

/// <summary>옵션 하나(<c>--max-rows 20</c> → <c>max_rows: 20</c>).</summary>
internal sealed record OptionSpec(string Name, string Field, ValueKind Kind = ValueKind.String);

/// <summary>자리 인자 하나. <paramref name="Many"/> 면 남은 자리 인자 전부를 배열로.</summary>
internal sealed record PositionalSpec(string Field, ValueKind Kind = ValueKind.String, bool Many = false);

/// <summary>본문(<c>--file F</c> 또는 <c>-</c> = stdin)을 받는 방식.</summary>
internal enum BodyRule
{
    /// <summary>본문을 받지 않는다.</summary>
    None,

    /// <summary>본문 글이 꼭 있어야 한다.</summary>
    Required,

    /// <summary>본문 글이 있으면 넣는다.</summary>
    Optional,

    /// <summary>본문 글 또는 <c>--tab</c> 중 하나가 있어야 한다(<c>check_sql</c>·<c>format_sql</c>).</summary>
    RequiredUnlessTab,

    /// <summary>본문은 JSON: 배열이면 <c>variants</c>, 객체면 인자에 합친다(<c>tune bench</c>).</summary>
    Variants,
}

/// <summary>
/// 하위 명령 하나(설계 §4.2 표 한 줄). 모든 하위 명령은 도구 호출 하나로 바뀐다 — <c>orandactl call &lt;도구&gt;</c> 와 같다.
/// </summary>
/// <param name="Name">명령 낱말(<c>tab get</c>).</param>
/// <param name="Tool">부를 도구 이름.</param>
/// <param name="Usage">사용법 한 줄(명령 이름 뒤).</param>
/// <param name="Options">옵션.</param>
/// <param name="Positionals">자리 인자(순서대로, 모두 필수).</param>
/// <param name="BodyField">본문이 들어갈 인자 이름.</param>
/// <param name="Body">본문 규칙.</param>
internal sealed record CommandSpec(
    string Name,
    string Tool,
    string Usage,
    IReadOnlyList<OptionSpec> Options,
    IReadOnlyList<PositionalSpec> Positionals,
    string? BodyField = null,
    BodyRule Body = BodyRule.None)
{
    /// <summary>명령 낱말 배열.</summary>
    public string[] Words { get; } = Name.Split(' ');
}
