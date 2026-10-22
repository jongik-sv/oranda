using System.Text.Json.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Formatting;

public enum LetterCase
{
    Upper,
    Lower,
    AsIs,
}

public enum CommaPosition
{
    Leading,
    Trailing,
}

public enum SelectListLayout
{
    OnePerLine,
    FillToWidth,
}

/// <summary>절 키워드 맞춤(Orange SQL Formatter 「Keyword Align」). Right = 키워드 끝을 SELECT 끝에 맞춘 강(river) 형식.</summary>
public enum KeywordAlign
{
    Left,
    Right,
}

/// <summary>GROUP BY·ORDER BY 목록 배치(Orange 「Group/Order By Column List」).</summary>
public enum ByListLayout
{
    /// <summary>한 줄(최대 폭을 넘으면 그때만 감는다).</summary>
    SingleLine,

    /// <summary>한 줄에 하나씩.</summary>
    MultiLine,
}

/// <summary>
/// 질의 절 배치(TSK-13-43). <see cref="Indent"/> = 절 본문을 들여 쓰는 옛 출력. <see cref="Column"/> = 사내 쿼리 서식:
/// 절 키워드 맨 앞 열·본문 7번째 열·앞 쉼표 5번째 열·여러 줄 괄호 묶음(여는·닫는 괄호 혼자 한 줄, 안쪽 +들여쓰기)·짧은 서브쿼리와 CASE 는 한 줄.
/// Column 은 SELECT·WITH·INSERT·UPDATE·DELETE 문에만 쓰고 나머지(MERGE·DDL·PL/SQL)는 Indent 로 둔다.
/// </summary>
public enum ClauseLayout
{
    Indent,
    Column,
}

/// <summary>ORDER BY 목록 배치(TSK-13-43). <see cref="FollowGroupBy"/> = <see cref="LineBreakOptions.GroupOrderBy"/> 를 따른다(옛 출력).</summary>
public enum OrderByLayout
{
    FollowGroupBy,
    SingleLine,
    MultiLine,
}

public enum AndOrBreak
{
    Before,
    After,
    None,
}

public enum StatementTerminator
{
    Keep,
    Add,
    Remove,
}

/// <summary><c>formatter/{id}.json</c> 루트(data-model 2.3). 기본 제공: <c>oranda-default</c>, <c>orange5</c>, <c>compact</c>.</summary>
public sealed record FormatterProfile
{
    public int SchemaVersion { get; init; } = SchemaVersions.Formatter;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public FormatterOptions Options { get; init; } = new();
}

/// <summary>
/// 서식 옵션. 속성 기본값 = 옛 「Oranda 기본」(TSK-13-43 전) 출력이며 저장된 프로필 파일에 없는 새 속성도 이 값으로 읽힌다(사용자 프로필 출력 불변).
/// 기본 제공 「Oranda 기본」 프로필(oranda-default)의 옵션은 <see cref="OrandaDefault"/>(사내 쿼리 서식, TSK-13-43).
/// </summary>
public sealed record FormatterOptions
{
    /// <summary>
    /// 기본 제공 「Oranda 기본」 프로필 옵션 = 사내 쿼리 서식(키워드·이름 대문자, 절 키워드 맨 앞 열·본문 7번째 열, 앞 쉼표,
    /// GROUP BY 여러 줄·ORDER BY 한 줄, 외부 조인·같은 칼럼 범위 비교 맞춤, 120자). TSK-13-43.
    /// </summary>
    public static FormatterOptions OrandaDefault { get; } = new()
    {
        Case = new CaseOptions { Keyword = LetterCase.Upper, Function = LetterCase.Upper, Identifier = LetterCase.Upper },
        CommaPosition = CommaPosition.Leading,
        ClauseLayout = ClauseLayout.Column,
        LineBreaks = new LineBreakOptions
        {
            SelectList = SelectListLayout.OnePerLine,
            GroupOrderBy = ByListLayout.MultiLine,
            OrderBy = OrderByLayout.SingleLine,
            InListThreshold = 0,
            MaxLineWidth = 120,
        },
        Align = new AlignOptions { Alias = false, Assignment = true, ColumnDefinition = true, EqualsSign = false, Comparison = true },
    };

    public CaseOptions Case { get; init; } = new();

    public IndentOptions Indent { get; init; } = new();

    public CommaPosition CommaPosition { get; init; } = CommaPosition.Trailing;

    /// <summary>절 키워드 맞춤(TSK-13-38). 기본 Left = 지금 출력.</summary>
    public KeywordAlign KeywordAlign { get; init; } = KeywordAlign.Left;

    /// <summary>질의 절 배치(TSK-13-43). 기본 Indent = 옛 출력. Column 이면 <see cref="KeywordAlign"/> 은 쓰지 않는다.</summary>
    public ClauseLayout ClauseLayout { get; init; } = ClauseLayout.Indent;

    public bool OperatorSpacing { get; init; } = true;

    public bool ParenInnerSpace { get; init; }

    public LineBreakOptions LineBreaks { get; init; } = new();

    public AlignOptions Align { get; init; } = new();

    public CommentOptions Comments { get; init; } = new();

    public StatementTerminator StatementTerminator { get; init; } = StatementTerminator.Keep;

    /// <summary><c>-- @formatter:off</c>/<c>on</c> 구간 존중(FMT-10).</summary>
    public bool HonorFormatterOff { get; init; } = true;
}

public sealed record CaseOptions
{
    public LetterCase Keyword { get; init; } = LetterCase.Upper;

    public LetterCase Function { get; init; } = LetterCase.Upper;

    public LetterCase Identifier { get; init; } = LetterCase.AsIs;
}

/// <summary>들여쓰기. <see cref="Size"/> 1~16, 단계 수 0~4.</summary>
public sealed record IndentOptions
{
    public bool UseTabs { get; init; }

    public int Size { get; init; } = 4;

    public int Subquery { get; init; } = 1;

    public int Case { get; init; } = 1;

    public int PlsqlBlock { get; init; } = 1;
}

/// <summary>줄바꿈. <see cref="InListThreshold"/> 0 = 줄바꿈 안 함, <see cref="MaxLineWidth"/> 40~400.</summary>
public sealed record LineBreakOptions
{
    public SelectListLayout SelectList { get; init; } = SelectListLayout.OnePerLine;

    /// <summary>GROUP BY·ORDER BY 목록(TSK-13-38). 기본 SingleLine = 지금 출력.</summary>
    public ByListLayout GroupOrderBy { get; init; } = ByListLayout.SingleLine;

    /// <summary>ORDER BY 목록만 따로(TSK-13-43). 기본 FollowGroupBy = <see cref="GroupOrderBy"/> 를 따른다(옛 출력).</summary>
    public OrderByLayout OrderBy { get; init; } = OrderByLayout.FollowGroupBy;

    /// <summary>ORDER BY 목록에 실제로 쓸 배치(저장하지 않는다).</summary>
    [JsonIgnore]
    public ByListLayout EffectiveOrderBy => OrderBy switch
    {
        OrderByLayout.SingleLine => ByListLayout.SingleLine,
        OrderByLayout.MultiLine => ByListLayout.MultiLine,
        _ => GroupOrderBy,
    };

    public bool JoinClause { get; init; } = true;

    public AndOrBreak AndOr { get; init; } = AndOrBreak.Before;

    public int InListThreshold { get; init; } = 5;

    public int MaxLineWidth { get; init; } = 120;
}

public sealed record AlignOptions
{
    public bool Alias { get; init; } = true;

    public bool Assignment { get; init; } = true;

    public bool ColumnDefinition { get; init; } = true;

    /// <summary>JSON <c>equals</c>. C# <c>Equals</c> 와 겹쳐 이름을 바꿨다(data-model 6.3).</summary>
    [JsonPropertyName("equals")]
    public bool EqualsSign { get; init; }

    /// <summary>
    /// 이어진 조건 줄 중 외부 조인 <c>(+)</c> 묶음(같은 표 별칭)·같은 왼쪽 식(범위 조건)의 비교 연산자와 오른쪽 값을 같은 열에 맞춘다
    /// (<c>C.USE_YN(+)  = 'Y'</c>, <c>A.DTM &lt;  :to</c>). <see cref="ClauseLayout.Column"/> 에서만 쓴다(TSK-13-43).
    /// </summary>
    public bool Comparison { get; init; }
}

/// <summary>주석. <see cref="MaxBlankLines"/> 0~5.</summary>
public sealed record CommentOptions
{
    public bool Preserve { get; init; } = true;

    public int MaxBlankLines { get; init; } = 1;
}
