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

/// <summary>서식 옵션. 기본값 = 「Oranda 기본」 프로필(data-model 2.3).</summary>
public sealed record FormatterOptions
{
    public CaseOptions Case { get; init; } = new();

    public IndentOptions Indent { get; init; } = new();

    public CommaPosition CommaPosition { get; init; } = CommaPosition.Trailing;

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
}

/// <summary>주석. <see cref="MaxBlankLines"/> 0~5.</summary>
public sealed record CommentOptions
{
    public bool Preserve { get; init; } = true;

    public int MaxBlankLines { get; init; } = 1;
}
