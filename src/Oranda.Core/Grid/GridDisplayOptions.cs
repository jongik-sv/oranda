namespace Oranda.Core.Grid;

/// <summary>
/// 결과 셀을 글자로 바꿀 때의 표시 옵션(PRD GRID-02·GRID-08). 날짜 형식은 .NET 형식이다.
/// 세션 NLS 형식(Oracle 표기)은 <see cref="CellValueFormatter.NlsToDotNetFormat"/> 으로 바꿔 넣는다.
/// </summary>
public sealed record GridDisplayOptions
{
    /// <summary>화면용 기본값: NULL 은 <c>(null)</c>, 한 줄 표시, 천 단위 끔.</summary>
    public static GridDisplayOptions Default { get; } = new();

    /// <summary>복사용 기본값: NULL 은 빈 글자, 줄바꿈 유지, BOOLEAN 은 TRUE/FALSE, VECTOR 는 펼침.</summary>
    public static GridDisplayOptions Plain { get; } = new()
    {
        NullText = string.Empty,
        SingleLine = false,
        TrueText = "TRUE",
        FalseText = "FALSE",
        ExpandVector = true,
    };

    public string NullText { get; init; } = "(null)";

    /// <summary>NUMBER 열 정수부에 천 단위 쉼표를 넣는다.</summary>
    public bool ThousandsSeparator { get; init; }

    public string DateFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";

    public string TimestampFormat { get; init; } = "yyyy-MM-dd HH:mm:ss.ffffff";

    public string TimestampTzFormat { get; init; } = "yyyy-MM-dd HH:mm:ss.ffffff zzz";

    public string TrueText { get; init; } = "✓";

    public string FalseText { get; init; } = "✗";

    /// <summary>줄바꿈·탭을 보이는 기호/공백으로 바꿔 한 줄로 만든다(그리드 셀용).</summary>
    public bool SingleLine { get; init; } = true;

    /// <summary>VECTOR 를 <c>VECTOR(768, FLOAT32)</c> 요약 대신 값 목록으로 보인다.</summary>
    public bool ExpandVector { get; init; }

    /// <summary>JSON 을 들여쓰기해 보인다(셀 상세용). 끄면 한 줄 압축.</summary>
    public bool PrettyJson { get; init; }

    /// <summary>VECTOR 펼침 때 보일 최대 원소 수. 넘으면 <c>…</c>.</summary>
    public int MaxVectorElements { get; init; } = 64;
}
