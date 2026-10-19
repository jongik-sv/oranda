using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

/// <summary>토큰 앞 간격 종류.</summary>
internal enum GapKind : byte
{
    None,
    Space,
    NewLine,

    /// <summary>원문 간격 그대로(문법 오류 구간·원문 유지).</summary>
    Verbatim,
}

/// <summary>
/// 포매터의 배치 단위 1개 = 토큰 1개(여러 줄에 걸친 문자열·주석은 1개로 합침) 또는 원문 그대로 둘 줄 1개
/// (SQL*Plus 명령·REM·단독 <c>/</c>). 확장 규칙(TSK-05-02 <c>LayoutRules</c>)은 <see cref="Gap"/>·<see cref="Depth"/> 를 고친다.
/// </summary>
internal sealed class FormatItem
{
    public FormatItem(string text, TokenKind kind, bool isRaw, int start, int line, int endLine)
    {
        Text = text;
        Kind = kind;
        IsRaw = isRaw;
        Start = start;
        Line = line;
        EndLine = endLine;
        Sym = !isRaw && text.Length == 1 && kind is TokenKind.Punct or TokenKind.Operator ? text[0] : '\0';
        Word = !isRaw && kind is TokenKind.Keyword or TokenKind.Identifier ? text.ToUpperInvariant() : "";
    }

    /// <summary>낱말(키워드·식별자)의 대문자 글자. 낱말이 아니면 빈 문자열.</summary>
    public string Word { get; }

    /// <summary>원문 글자(줄 주석·원문 줄은 끝 공백 제거).</summary>
    public string Text { get; }

    public TokenKind Kind { get; }

    /// <summary>원문 그대로 내보내는 줄(SQL*Plus·REM·<c>/</c>).</summary>
    public bool IsRaw { get; }

    /// <summary>조각 안 원문 위치. 포매터가 넣은 항목은 -1.</summary>
    public int Start { get; }

    /// <summary><see cref="FormatLayout.Items"/> 안 위치.</summary>
    public int Index { get; set; }

    public int End => Start + Text.Length;

    public int Line { get; }

    public int EndLine { get; }

    /// <summary>속한 문장 번호(StatementSplitter 조각). 문장 밖(끝 표지 <c>;</c>·문장 사이 주석)은 -1.</summary>
    public int Piece { get; set; } = -1;

    /// <summary>원문에서 이 항목 앞에 줄바꿈이 있었다.</summary>
    public bool OrigNewLineBefore { get; set; }

    /// <summary>원문에서 이 항목 앞 빈 줄 수(최대 개수로 자른 값).</summary>
    public int BlankBefore { get; set; }

    /// <summary>원문에서 앞 항목과 이 항목 사이 공백 글자.</summary>
    public string OrigGap { get; set; } = "";

    public GapKind Gap { get; set; }

    /// <summary>줄 첫 항목일 때 들여쓰기 단계.</summary>
    public int Depth { get; set; }

    /// <summary>이 항목이 놓인 출력 줄의 들여쓰기 단계.</summary>
    public int LineDepth { get; set; }

    /// <summary><see cref="GapKind.NewLine"/> 일 때 넣을 빈 줄 수.</summary>
    public int BlankLines { get; set; }

    /// <summary>출력 글자(대소문자 적용 후).</summary>
    public string OutText { get; set; } = "";

    public bool IsComment => !IsRaw && Kind == TokenKind.Comment;

    public bool IsLineComment => IsComment && Text.StartsWith("--", StringComparison.Ordinal);

    public bool IsCode => !IsRaw && Kind != TokenKind.Comment;

    public bool IsWord => Word.Length > 0;

    /// <summary>이 낱말인가. <paramref name="word"/> 는 대문자.</summary>
    public bool Is(string word) => string.Equals(Word, word, StringComparison.Ordinal);

    /// <summary>한 글자 문장 부호·연산자면 그 글자, 아니면 '\0'.</summary>
    public char Sym { get; }

    public bool IsSym(string sym) => sym.Length == 1
        ? Sym == sym[0]
        : !IsRaw && Kind == TokenKind.Operator && string.Equals(Text, sym, StringComparison.Ordinal);

    public override string ToString() => IsRaw ? "raw:" + Text : Kind + ":" + Text;
}
