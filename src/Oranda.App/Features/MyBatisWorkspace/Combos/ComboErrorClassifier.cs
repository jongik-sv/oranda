using System.Globalization;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language.Lexing;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>조합 하나의 구문 오류를 사람이 읽는 이유와 원문 위치로 바꾼 것.</summary>
/// <param name="Reason">결과 칸 짧은 이유(「WHERE 앞 쉼표」).</param>
/// <param name="Detail">오른쪽 칸 설명(「구문 오류: … (줄 5 의 SAL = #{sal},)」).</param>
/// <param name="SqlRange">펼친 SQL 안 강조 자리.</param>
/// <param name="SourceLine">문장 원문 안 원인 줄(1 기반). 모르면 null.</param>
internal sealed record ComboSyntaxError(string Reason, string Detail, TextRange SqlRange, int? SourceLine);

/// <summary>
/// L2 구문 오류(첫 오류 1건)를 동적 SQL 에서 흔한 모양으로 분류하고 원문 줄을 찾는다(MB-07).
/// L2 메시지는 ANTLR 식이라 그대로 보이면 원인을 알기 어렵다. 오류 자리 앞뒤 L1 토큰으로 「WHERE 앞 쉼표」·「SET 뒤가 비어 있음」 같은 모양을 먼저 본다.
/// 펼친 SQL ↔ 원문 대응표는 계약에 없다. 오류 자리보다 앞에서 가장 가까운 바인드의 원문 자리(<see cref="MyBatisBind.Source"/>)를 원인 줄로 보고,
/// 바인드가 없으면 원문에서 그 키워드가 처음 나오는 텍스트 줄을 쓴다.
/// </summary>
internal static class ComboErrorClassifier
{
    // 쉼표 바로 뒤에 오면 「앞 쉼표」로 보는 절 머리.
    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "FROM", "SET", "ORDER", "GROUP", "HAVING", "VALUES", "UNION", "INTERSECT", "MINUS", "CONNECT", "START", "RETURNING", "INTO",
    };

    public static ComboSyntaxError Classify(string sql, Diagnostic error, EvalResult eval, string statementText)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(eval);
        ArgumentNullException.ThrowIfNull(statementText);

        var tokens = SqlLexer.Tokenize(sql).Where(t => t.Kind is not (TokenKind.Comment or TokenKind.Hint)).ToList();
        // 문장이 덜 끝난 오류는 L2 가 마지막 글자 하나를 범위로 준다(PlSqlAnalyzer.EndRange). 그때는 막힌 단어가 없다(끝).
        bool atEnd = error.Range.Length == 1 && error.Range.Start == sql.TrimEnd().Length - 1
            && error.Message.Contains("끝나지 않았", StringComparison.Ordinal);
        int at = atEnd ? -1 : tokens.FindIndex(t => t.End > error.Range.Start);
        int prevIndex = (at < 0 ? tokens.Count : at) - 1;
        string? atWord = at >= 0 ? Word(sql, tokens[at]) : null;
        string? prevWord = prevIndex >= 0 ? Word(sql, tokens[prevIndex]) : null;
        var prevRange = prevIndex >= 0 ? new TextRange(tokens[prevIndex].Start, tokens[prevIndex].Length) : error.Range;
        var atRange = at >= 0 ? new TextRange(tokens[at].Start, tokens[at].Length) : error.Range;
        string? atName = atWord?.ToUpperInvariant();

        if (prevWord == "," && (atWord is null || ClauseWords.Contains(atWord) || atWord == ")"))
        {
            var line = LineBefore(sql, tokens, prevIndex, eval, statementText, null);
            return new(atName is null ? ComboText.CommaAtEnd : ComboText.CommaBefore(atName),
                Detail(atName is null ? ComboText.CommaAtEndDetail : ComboText.CommaBeforeDetail(atName), line, statementText), prevRange, line);
        }

        if (IsWord(prevWord, "SET") && (atWord is null || ClauseWords.Contains(atWord)))
        {
            var line = LineBefore(sql, tokens, prevIndex, eval, statementText, "SET");
            return new(ComboText.EmptyAfterSet, Detail(ComboText.EmptyAfterSetDetail, line, statementText), prevRange, line);
        }

        if (IsWord(prevWord, "WHERE") && (IsWord(atWord, "AND") || IsWord(atWord, "OR")))
        {
            var line = LineBefore(sql, tokens, at, eval, statementText, "WHERE");
            return new(ComboText.RightAfterWhere(atName!), Detail(ComboText.RightAfterWhereDetail(atName!), line, statementText), atRange, line);
        }

        if (IsWord(prevWord, "WHERE") && (atWord is null || ClauseWords.Contains(atWord) || atWord == ")"))
        {
            var line = LineBefore(sql, tokens, prevIndex, eval, statementText, "WHERE");
            return new(ComboText.EmptyAfter("WHERE"), Detail(ComboText.EmptyAfterDetail("WHERE"), line, statementText), prevRange, line);
        }

        if ((IsWord(prevWord, "AND") || IsWord(prevWord, "OR")) && (atWord is null || ClauseWords.Contains(atWord) || atWord == ")"))
        {
            var word = prevWord!.ToUpperInvariant();
            var line = LineBefore(sql, tokens, prevIndex, eval, statementText, null);
            return new(ComboText.EmptyAfter(word), Detail(ComboText.EmptyAfterDetail(word), line, statementText), prevRange, line);
        }

        var fallback = LineBefore(sql, tokens, at < 0 ? tokens.Count - 1 : at, eval, statementText, null);
        return new(ComboText.SyntaxError, Detail(error.Message, fallback, statementText), error.Range.Length > 0 ? error.Range : atRange, fallback);
    }

    /// <summary>문장 원문 오프셋 → 1 기반 줄 번호.</summary>
    public static int LineOf(string text, int offset)
    {
        int line = 1;
        int end = Math.Clamp(offset, 0, text.Length);
        for (int i = 0; i < end; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>1 기반 줄의 글(앞뒤 공백 없이).</summary>
    public static string LineText(string text, int line)
    {
        int start = 0;
        for (int n = 1; n < line && start >= 0; n++)
        {
            int next = text.IndexOf('\n', start);
            start = next < 0 ? -1 : next + 1;
        }

        if (start < 0 || start > text.Length)
        {
            return string.Empty;
        }

        int end = text.IndexOf('\n', start);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    private static string Detail(string what, int? line, string statementText) =>
        line is { } n
            ? ComboText.SyntaxDetailAt(what, n, LineText(statementText, n))
            : ComboText.SyntaxDetail(what);

    // 토큰 index 이하에서 가장 가까운 바인드의 원문 줄. 없으면 원문 텍스트에서 keyword 가 처음 나오는 줄.
    private static int? LineBefore(string sql, List<Token> tokens, int index, EvalResult eval, string statementText, string? keyword)
    {
        for (int i = Math.Min(index, tokens.Count - 1); i >= 0; i--)
        {
            if (tokens[i].Kind != TokenKind.Bind)
            {
                continue;
            }

            var name = sql.Substring(tokens[i].Start + 1, tokens[i].Length - 1);
            if (eval.Binds.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) is { } bind)
            {
                return LineOf(statementText, bind.Source.Start);
            }
        }

        return keyword is null ? null : KeywordLine(statementText, keyword);
    }

    // 태그 밖 텍스트에서 단어가 처음 나오는 줄(속성값·태그 이름은 건너뜀).
    private static int? KeywordLine(string text, string keyword)
    {
        bool inTag = false;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inTag)
            {
                if (quote != '\0')
                {
                    quote = c == quote ? '\0' : quote;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    inTag = false;
                }

                continue;
            }

            if (c == '<')
            {
                inTag = true;
                continue;
            }

            if (string.Compare(text, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_')
                && (i + keyword.Length >= text.Length || !char.IsLetterOrDigit(text[i + keyword.Length]) && text[i + keyword.Length] != '_'))
            {
                return LineOf(text, i);
            }
        }

        return null;
    }

    private static string Word(string sql, Token t) => sql.Substring(t.Start, t.Length);

    private static bool IsWord(string? word, string keyword) => string.Equals(word, keyword, StringComparison.OrdinalIgnoreCase);
}
