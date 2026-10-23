using Oranda.Core.Text;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

// MyBatisAutoFix 의 일부: 문장 원문의 마지막 SQL 단어·줄바꿈·들여쓰기·줄 번호 훑기(MyBatisAutoFix.cs 에서 나눔, 동작 불변).
internal static partial class MyBatisAutoFix
{
    /// <summary>
    /// 텍스트 조각의 SQL 코드 부분을 훑어 마지막 글자가 단어인 경우 그 단어 범위를 돌려준다.
    /// 주석·문자열 리터럴 안이거나 끝이 단어가 아니면 null.
    /// </summary>
    private static LastWord? FindLastSqlWord(string text, Node node)
    {
        var state = SqlState.Code;
        LastWord? last = null;
        var i = node.Start;
        while (i < node.End)
        {
            var c = text[i];
            switch (state)
            {
                case SqlState.Code:
                    if (c == '-' && i + 1 < node.End && text[i + 1] == '-')
                    {
                        state = SqlState.LineComment;
                        last = null;
                        i += 2;
                    }
                    else if (c == '/' && i + 1 < node.End && text[i + 1] == '*')
                    {
                        state = SqlState.BlockComment;
                        last = null;
                        i += 2;
                    }
                    else if (c == '\'')
                    {
                        state = SqlState.String;
                        last = null;
                        i++;
                    }
                    else if (c == '"')
                    {
                        state = SqlState.QuotedIdentifier;
                        last = null;
                        i++;
                    }
                    else if (IsWordChar(c))
                    {
                        var start = i;
                        while (i < node.End && IsWordChar(text[i]))
                        {
                            i++;
                        }

                        last = new LastWord(start, i);
                    }
                    else
                    {
                        if (!char.IsWhiteSpace(c))
                        {
                            last = null;
                        }

                        i++;
                    }

                    break;
                case SqlState.LineComment:
                    if (c == '\n')
                    {
                        state = SqlState.Code;
                    }

                    i++;
                    break;
                case SqlState.BlockComment:
                    if (c == '*' && i + 1 < node.End && text[i + 1] == '/')
                    {
                        state = SqlState.Code;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;
                default:
                    var quote = state == SqlState.String ? '\'' : '"';
                    if (c == quote)
                    {
                        if (i + 1 < node.End && text[i + 1] == quote)
                        {
                            i += 2;
                        }
                        else
                        {
                            state = SqlState.Code;
                            i++;
                        }
                    }
                    else
                    {
                        i++;
                    }

                    break;
            }
        }

        return state == SqlState.Code ? last : null;
    }

    private enum SqlState
    {
        Code,
        LineComment,
        BlockComment,
        String,
        QuotedIdentifier,
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool IsBlank(string text, Node node)
    {
        for (var i = node.Start; i < node.End; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string DetectEol(string text)
    {
        var lf = text.IndexOf('\n', StringComparison.Ordinal);
        return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>위치가 있는 줄의 앞쪽 공백.</summary>
    private static string LineIndent(string text, int position)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(position - 1, 0)) + 1;
        if (position == 0)
        {
            lineStart = 0;
        }

        var end = lineStart;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
        {
            end++;
        }

        return text[lineStart..end];
    }

    private static int LineOf(string text, int position)
    {
        var line = 1;
        for (var i = 0; i < position && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
