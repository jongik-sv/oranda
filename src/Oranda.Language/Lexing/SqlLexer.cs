using System.Collections.Frozen;

namespace Oranda.Language.Lexing;

/// <summary>
/// 줄 단위 손 PL/SQL 토크나이저. 줄 시작 상태(int)를 받아 줄 끝 상태를 돌려준다.
/// 상태 = Mode(하위 8비트) | 닫는 구분자 문자(상위 16비트, q-quote 용).
/// </summary>
public static class SqlLexer
{
    public const int Normal = 0, InBlockComment = 1, InHint = 2, InString = 3, InQString = 4, InQuotedIdent = 5;

    static readonly FrozenSet<string> Keywords = new[]
    {
        "ALL","ALTER","AND","ANY","AS","ASC","BEGIN","BETWEEN","BODY","BULK","BY","CASE","CHAR","CLOSE","COLLECT","COMMIT",
        "CONSTANT","CREATE","CROSS","CURSOR","DATE","DECLARE","DEFAULT","DELETE","DESC","DISTINCT","DROP","ELSE","ELSIF",
        "END","EXCEPTION","EXECUTE","EXISTS","EXIT","FETCH","FOR","FORALL","FROM","FULL","FUNCTION","GOTO","GROUP","HAVING",
        "IF","IMMEDIATE","IN","INDEX","INNER","INSERT","INTERSECT","INTO","IS","JOIN","LEFT","LIKE","LIMIT","LOOP","MATCHED",
        "MERGE","MINUS","NOT","NULL","NUMBER","OF","ON","OPEN","OR","ORDER","OTHERS","OUT","OUTER","PACKAGE","PLS_INTEGER",
        "PRAGMA","PRIOR","PROCEDURE","RAISE","RECORD","REPLACE","RETURN","RETURNING","REVERSE","RIGHT","ROLLBACK","ROWTYPE",
        "SAVEPOINT","SELECT","SET","SUBTYPE","SYS_REFCURSOR","TABLE","THEN","TO","TRIGGER","TYPE","UNION","UPDATE","USING",
        "VECTOR","DOMAIN","ANNOTATIONS","JSON","DUALITY","COMPOUND","ARRAY","LATERAL","FLASHBACK","PURGE","TRUNCATE","GRANT","REVOKE","COMMENT","RENAME","LOCK","CALL","ANALYZE","MATERIALIZED","SEQUENCE","SYNONYM","USER","ROLE","PUBLIC","DIRECTORY","VALUES","VARCHAR2","VIEW","WHEN","WHERE","WHILE","WITH","BOOLEAN","CLOB","BLOB","TIMESTAMP","INTERVAL","VARRAY",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KwLookup =
        Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    static int Mode(int state) => state & 0xFF;
    static char Delim(int state) => (char)(state >> 16);
    static int Make(int mode, char delim = '\0') => mode | (delim << 16);

    static char QClose(char open) => open switch { '[' => ']', '{' => '}', '<' => '>', '(' => ')', _ => open };

    /// <summary>문서 전체를 렉싱한다(토큰 위치 = 문서 오프셋). 줄 사이에 걸친 주석·문자열은 줄마다 토큰이 나뉜다.</summary>
    public static List<Token> Tokenize(string text)
    {
        var all = new List<Token>();
        var line = new List<Token>();
        int state = Normal, pos = 0;
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            line.Clear();
            state = LexLine(text.AsSpan(pos, end - pos), state, line);
            foreach (var t in line) all.Add(t with { Start = t.Start + pos });
            if (nl < 0) break;
            pos = nl + 1;
        }
        return all;
    }

    public static int LexLine(ReadOnlySpan<char> s, int state, List<Token>? tokens)
    {
        int i = 0, n = s.Length;
        // 앞 줄에서 이어지는 상태
        switch (Mode(state))
        {
            case InBlockComment:
            case InHint:
                {
                    int end = s.IndexOf("*/");
                    var kind = Mode(state) == InHint ? TokenKind.Hint : TokenKind.Comment;
                    if (end < 0) { if (n > 0) tokens?.Add(new(0, n, kind)); return state; }
                    tokens?.Add(new(0, end + 2, kind));
                    i = end + 2; state = Normal; break;
                }
            case InString:
                {
                    int j = ScanQuoted(s, 0, '\'');
                    if (j < 0) { if (n > 0) tokens?.Add(new(0, n, TokenKind.StringLiteral)); return state; }
                    tokens?.Add(new(0, j, TokenKind.StringLiteral)); i = j; state = Normal; break;
                }
            case InQuotedIdent:
                {
                    int j = s.IndexOf('"');
                    if (j < 0) { if (n > 0) tokens?.Add(new(0, n, TokenKind.QuotedIdent)); return state; }
                    tokens?.Add(new(0, j + 1, TokenKind.QuotedIdent)); i = j + 1; state = Normal; break;
                }
            case InQString:
                {
                    char close = Delim(state);
                    int j = FindQEnd(s, 0, close);
                    if (j < 0) { if (n > 0) tokens?.Add(new(0, n, TokenKind.StringLiteral)); return state; }
                    tokens?.Add(new(0, j, TokenKind.StringLiteral)); i = j; state = Normal; break;
                }
        }

        while (i < n)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }
            int start = i;
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                tokens?.Add(new(start, n - start, TokenKind.Comment)); return Normal;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                bool hint = i + 2 < n && s[i + 2] == '+';
                int end = s[(i + 2)..].IndexOf("*/");
                var kind = hint ? TokenKind.Hint : TokenKind.Comment;
                if (end < 0) { tokens?.Add(new(start, n - start, kind)); return hint ? InHint : InBlockComment; }
                i = i + 2 + end + 2; tokens?.Add(new(start, i - start, kind)); continue;
            }
            // q'[ ... ]' (N/n 접두 포함)
            int qp = i;
            if ((c == 'n' || c == 'N') && i + 1 < n && (s[i + 1] == 'q' || s[i + 1] == 'Q')) qp = i + 1;
            if ((s[qp] == 'q' || s[qp] == 'Q') && qp + 2 < n && s[qp + 1] == '\'')
            {
                char close = QClose(s[qp + 2]);
                int j = FindQEnd(s, qp + 3, close);
                if (j < 0) { tokens?.Add(new(start, n - start, TokenKind.StringLiteral)); return Make(InQString, close); }
                i = j; tokens?.Add(new(start, i - start, TokenKind.StringLiteral)); continue;
            }
            if (c == '\'' || ((c == 'n' || c == 'N') && i + 1 < n && s[i + 1] == '\''))
            {
                int from = c == '\'' ? i + 1 : i + 2;
                int j = ScanQuoted(s, from, '\'');
                if (j < 0) { tokens?.Add(new(start, n - start, TokenKind.StringLiteral)); return InString; }
                i = j; tokens?.Add(new(start, i - start, TokenKind.StringLiteral)); continue;
            }
            if (c == '"')
            {
                int j = s[(i + 1)..].IndexOf('"');
                if (j < 0) { tokens?.Add(new(start, n - start, TokenKind.QuotedIdent)); return InQuotedIdent; }
                i = i + 1 + j + 1; tokens?.Add(new(start, i - start, TokenKind.QuotedIdent)); continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1])))
            {
                i++;
                while (i < n && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                if (i < n && (s[i] == 'e' || s[i] == 'E'))
                {
                    int k = i + 1;
                    if (k < n && (s[k] == '+' || s[k] == '-')) k++;
                    if (k < n && char.IsAsciiDigit(s[k])) { i = k; while (i < n && char.IsAsciiDigit(s[i])) i++; }
                }
                if (i < n && (s[i] is 'f' or 'F' or 'd' or 'D') && (i + 1 >= n || !IsIdentPart(s[i + 1]))) i++;
                tokens?.Add(new(start, i - start, TokenKind.Number)); continue;
            }
            if (IsIdentStart(c))
            {
                i++;
                while (i < n && IsIdentPart(s[i])) i++;
                var word = s[start..i];
                tokens?.Add(new(start, i - start, KwLookup.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier));
                continue;
            }
            if (c == ':' && i + 1 < n && (IsIdentStart(s[i + 1]) || char.IsAsciiDigit(s[i + 1])))
            {
                i += 2;
                while (i < n && IsIdentPart(s[i])) i++;
                tokens?.Add(new(start, i - start, TokenKind.Bind)); continue;
            }
            if (c == '&')
            {
                int k = i + 1;
                if (k < n && s[k] == '&') k++;
                if (k < n && (IsIdentStart(s[k]) || char.IsAsciiDigit(s[k])))
                {
                    i = k + 1;
                    while (i < n && IsIdentPart(s[i])) i++;
                    tokens?.Add(new(start, i - start, TokenKind.Substitution)); continue;
                }
            }
            // 연산자
            if (i + 1 < n)
            {
                var two = s.Slice(i, 2);
                if (two is ":=" or "||" or "<=" or ">=" or "<>" or "!=" or "^=" or "=>" or ".." or "**" or "<<" or ">>")
                { i += 2; tokens?.Add(new(start, 2, TokenKind.Operator)); continue; }
            }
            i++;
            tokens?.Add(new(start, 1, c is '(' or ')' or ',' or ';' or '.' ? TokenKind.Punct
                : c is '+' or '-' or '*' or '/' or '=' or '<' or '>' or '%' or '@' or ':' ? TokenKind.Operator : TokenKind.Error));
        }
        return Normal;
    }

    static bool IsIdentStart(char c) => char.IsLetter(c);
    static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

    /// <summary>from 위치부터 닫는 따옴표(겹따옴표 이스케이프) 다음 인덱스. 없으면 -1.</summary>
    static int ScanQuoted(ReadOnlySpan<char> s, int from, char q)
    {
        int i = from;
        while (true)
        {
            int j = s[i..].IndexOf(q);
            if (j < 0) return -1;
            i += j;
            if (i + 1 < s.Length && s[i + 1] == q) { i += 2; continue; }
            return i + 1;
        }
    }

    static int FindQEnd(ReadOnlySpan<char> s, int from, char close)
    {
        for (int i = from; i + 1 < s.Length; i++)
            if (s[i] == close && s[i + 1] == '\'') return i + 2;
        return -1;
    }
}
