using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

// 토크나이저 + 재귀 하강 파서. 식 글자별 파싱 결과는 캐시한다(스레드 안전, 상한 있음).
internal static partial class OgnlLite
{
    private const int CacheLimit = 4096;
    private const int MaxDepth = 200;

    private const int MaxLength = 4000;

    private static readonly ConcurrentDictionary<string, Parsed> TestCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Parsed> ValueCache = new(StringComparer.Ordinal);

    private enum TokenKind { Ident, Int, Float, Char, Str, Op, Bad, End }

    /// <summary>토큰. <see cref="Text"/> 는 이름·연산자 기호·(Bad 일 때) 이유, <see cref="Literal"/> 은 리터럴 값.</summary>
    private readonly record struct Token(TokenKind Kind, string Text, int Start, int Length, object? Literal = null);

    private sealed record Parsed(Node? Root, OgnlFailure? Failure);

    private sealed class OgnlParseException(OgnlFailure failure) : Exception(failure.Reason)
    {
        public OgnlFailure Failure { get; } = failure;
    }

    private sealed class OgnlEvalException(OgnlFailure failure) : Exception(failure.Reason)
    {
        public OgnlFailure Failure { get; } = failure;
    }

    private static Parsed Parse(string expression, bool allowPlus)
    {
        var cache = allowPlus ? ValueCache : TestCache;
        if (cache.TryGetValue(expression, out var hit))
        {
            return hit;
        }

        Parsed parsed;
        try
        {
            // 계산은 재귀라 아주 긴 and·or 사슬은 스택을 넘칠 수 있다. 손으로 쓴 식은 이 길이에 닿지 않는다.
            parsed = expression.Length > MaxLength
                ? new Parsed(null, new OgnlFailure(new TextRange(0, expression.Length), $"식이 너무 길다({expression.Length}자, 상한 {MaxLength}자)"))
                : new Parsed(new Parser(expression, allowPlus).ParseAll(), null);
        }
        catch (OgnlParseException e)
        {
            parsed = new Parsed(null, e.Failure);
        }

        if (cache.Count >= CacheLimit)
        {
            cache.Clear();
        }

        cache[expression] = parsed;
        return parsed;
    }

    private static bool IsOp(Token t, string op) => t.Kind == TokenKind.Op && t.Text == op;

    private static bool IsWord(Token t, string word) => t.Kind == TokenKind.Ident && t.Text == word;

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static readonly string[] TwoCharOps = ["==", "!=", "<=", ">=", "&&", "||", "=~", "<<", ">>"];

    /// <summary>글자를 토큰으로 자른다. 알아볼 수 없는 부분은 Bad 토큰으로 남기고 계속 읽는다(References 가 알아본 만큼 쓰도록). 마지막은 항상 End.</summary>
    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int n = s.Length;
        int i = 0;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (IsIdentStart(c))
            {
                int j = i + 1;
                while (j < n && IsIdentPart(s[j]))
                {
                    j++;
                }

                list.Add(new Token(TokenKind.Ident, s[i..j], i, j - i));
                i = j;
            }
            else if (c is >= '0' and <= '9')
            {
                i = ReadNumber(s, i, list);
            }
            else if (c is '\'' or '"')
            {
                i = ReadQuoted(s, i, list);
            }
            else
            {
                i = ReadOperator(s, i, list);
            }
        }

        list.Add(new Token(TokenKind.End, string.Empty, n, 0));
        return list;
    }

    // OGNL 숫자 리터럴(ognl.jj makeInt·makeFloat): 0 으로 시작하면 8진수, 0x 는 16진수, 접미사 l·L = long, h·H = BigInteger,
    // 소수·지수는 double(f·F = float, d·D = double, b·B = BigDecimal). 접미사 없는 정수가 int 를 넘으면 NumberFormatException.
    private static int ReadNumber(string s, int i, List<Token> list)
    {
        int n = s.Length;
        int j = i;
        if (s[i] == '0' && i + 1 < n && s[i + 1] is 'x' or 'X')
        {
            j = i + 2;
            while (j < n && Uri.IsHexDigit(s[j]))
            {
                j++;
            }

            char suffix = j < n && s[j] is 'l' or 'L' or 'h' or 'H' ? s[j] : '\0';
            var hex = s[(i + 2)..j];
            int end = suffix == '\0' ? j : j + 1;
            if (hex.Length == 0 || (end < n && IsIdentPart(s[end])))
            {
                return Bad(s, i, end, list);
            }

            list.Add(MakeInt(s[i..end], hex, 16, suffix, i, end - i));
            return end;
        }

        while (j < n && s[j] is >= '0' and <= '9')
        {
            j++;
        }

        bool isFloat = false;
        if (j + 1 < n && s[j] == '.' && s[j + 1] is >= '0' and <= '9')
        {
            isFloat = true;
            j++;
            while (j < n && s[j] is >= '0' and <= '9')
            {
                j++;
            }
        }

        if (j < n && s[j] is 'e' or 'E')
        {
            int k = j + 1;
            if (k < n && s[k] is '+' or '-')
            {
                k++;
            }

            if (k < n && s[k] is >= '0' and <= '9')
            {
                isFloat = true;
                while (k < n && s[k] is >= '0' and <= '9')
                {
                    k++;
                }

                j = k;
            }
        }

        var digits = s[i..j];
        char tail = j < n ? s[j] : '\0';
        if (tail is 'f' or 'F' or 'd' or 'D' or 'b' or 'B')
        {
            int end = j + 1;
            if (end < n && IsIdentPart(s[end]))
            {
                return Bad(s, i, end, list);
            }

            list.Add(MakeFloat(s[i..end], digits, tail, i, end - i));
            return end;
        }

        if (isFloat)
        {
            if (j < n && IsIdentPart(s[j]))
            {
                return Bad(s, i, j + 1, list);
            }

            list.Add(MakeFloat(digits, digits, 'd', i, j - i));
            return j;
        }

        char intSuffix = tail is 'l' or 'L' or 'h' or 'H' ? tail : '\0';
        int intEnd = intSuffix == '\0' ? j : j + 1;
        if (intEnd < n && IsIdentPart(s[intEnd]))
        {
            return Bad(s, i, intEnd, list);
        }

        bool octal = digits.Length > 1 && digits[0] == '0';
        if (octal && digits.Any(c => c > '7'))
        {
            return Bad(s, i, intEnd, list);
        }

        list.Add(MakeInt(s[i..intEnd], octal ? digits[1..] : digits, octal ? 8 : 10, intSuffix, i, intEnd - i));
        return intEnd;
    }

    private static int Bad(string s, int i, int end, List<Token> list)
    {
        while (end < s.Length && IsIdentPart(s[end]))
        {
            end++;
        }

        list.Add(new Token(TokenKind.Bad, $"MyBatis(OGNL) 가 읽지 못하는 숫자 표기 {s[i..end]}", i, end - i));
        return end;
    }

    private static Token MakeInt(string text, string digits, int radix, char suffix, int start, int length)
    {
        if (suffix is 'h' or 'H')
        {
            return new Token(TokenKind.Bad, $"BigInteger 리터럴 {text} 은 Oranda 값 형에 없어 계산하지 않는다", start, length);
        }

        bool asLong = suffix is 'l' or 'L';
        ulong value = 0;
        bool overflow = false;
        foreach (char c in digits)
        {
            int d = c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
            ulong next = unchecked((value * (ulong)radix) + (ulong)d);
            if (value > (ulong.MaxValue - (ulong)d) / (ulong)radix)
            {
                overflow = true;
                break;
            }

            value = next;
        }

        // Integer.valueOf(s, radix)·Long.valueOf(s, radix): 부호 없는 리터럴이 그 형의 양수 범위를 넘으면 NumberFormatException.
        ulong max = asLong ? (ulong)long.MaxValue : int.MaxValue;
        if (overflow || value > max)
        {
            return new Token(TokenKind.Bad, $"MyBatis 3.5 도 실행 때 예외(NumberFormatException): For input string: \"{digits}\"", start, length);
        }

        return new Token(TokenKind.Int, text, start, length, asLong ? (object)(long)value : (object)(int)value);
    }

    private static Token MakeFloat(string text, string digits, char suffix, int start, int length)
    {
        switch (suffix)
        {
            case 'f' or 'F':
                return new Token(TokenKind.Float, text, start, length, float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture));
            case 'b' or 'B':
                return decimal.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
                    ? new Token(TokenKind.Float, text, start, length, m)
                    : new Token(TokenKind.Bad, $"BigDecimal 리터럴 {text} 은 decimal 범위를 넘어 계산하지 않는다", start, length);
            default:
                return new Token(TokenKind.Float, text, start, length, double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture));
        }
    }

    private static int ReadQuoted(string s, int i, List<Token> list)
    {
        char quote = s[i];
        int n = s.Length;
        var sb = new StringBuilder();
        int j = i + 1;
        while (true)
        {
            if (j >= n)
            {
                list.Add(new Token(TokenKind.Bad, "닫히지 않은 글자 리터럴", i, n - i));
                return n;
            }

            char c = s[j];
            if (c == quote)
            {
                j++;
                break;
            }

            if (c != '\\')
            {
                sb.Append(c);
                j++;
                continue;
            }

            if (j + 1 >= n)
            {
                list.Add(new Token(TokenKind.Bad, "닫히지 않은 글자 리터럴", i, n - i));
                return n;
            }

            char e = s[j + 1];
            switch (e)
            {
                case 'n': sb.Append('\n'); j += 2; break;
                case 't': sb.Append('\t'); j += 2; break;
                case 'r': sb.Append('\r'); j += 2; break;
                case 'b': sb.Append('\b'); j += 2; break;
                case 'f': sb.Append('\f'); j += 2; break;
                case '\\': sb.Append('\\'); j += 2; break;
                case '\'': sb.Append('\''); j += 2; break;
                case '"': sb.Append('"'); j += 2; break;
                case 'u' when j + 6 <= n && ushort.TryParse(s.AsSpan(j + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort u):
                    sb.Append((char)u);
                    j += 6;
                    break;
                default:
                    list.Add(new Token(TokenKind.Bad, $"부분집합 밖의 이스케이프 \\{e}", j, 2));
                    return j + 2;
            }
        }

        var text = sb.ToString();
        if (quote == '\'' && text.Length == 1)
        {
            list.Add(new Token(TokenKind.Char, text, i, j - i, text[0])); // OGNL: 한 글자 작은따옴표 = char
        }
        else
        {
            list.Add(new Token(TokenKind.Str, text, i, j - i, text));
        }

        return j;
    }

    private static int ReadOperator(string s, int i, List<Token> list)
    {
        if (string.CompareOrdinal(s, i, ">>>", 0, 3) == 0)
        {
            list.Add(new Token(TokenKind.Op, ">>>", i, 3));
            return i + 3;
        }

        foreach (var op in TwoCharOps)
        {
            if (string.CompareOrdinal(s, i, op, 0, 2) == 0)
            {
                list.Add(new Token(TokenKind.Op, op, i, 2));
                return i + 2;
            }
        }

        char c = s[i];
        if ("<>=!&|^~*/%?:[]{}#@+-().,".Contains(c, StringComparison.Ordinal))
        {
            list.Add(new Token(TokenKind.Op, c.ToString(), i, 1));
        }
        else
        {
            list.Add(new Token(TokenKind.Bad, $"알 수 없는 글자 '{c}'", i, 1));
        }

        return i + 1;
    }

    private sealed class Parser(string source, bool allowPlus)
    {
        private readonly List<Token> _t = Tokenize(source);
        private int _p;
        private int _depth;

        private Token Peek(int ahead = 0) => _t[Math.Min(_p + ahead, _t.Count - 1)];

        private Token Next() => _t[Math.Min(_p++, _t.Count - 1)];

        public Node ParseAll()
        {
            if (Peek().Kind == TokenKind.End)
            {
                throw Fail(new TextRange(0, source.Length), "빈 식");
            }

            var node = ParseOr();
            if (Peek().Kind != TokenKind.End)
            {
                throw Unexpected(Peek());
            }

            return node;
        }

        private static OgnlParseException Fail(TextRange range, string reason) => new(new OgnlFailure(range, reason));

        private static TextRange Span(Token t) => new(t.Start, t.Length);

        private string SourceOf(Token t) => source.Substring(t.Start, t.Length);

        /// <summary>끝에 이른 경우의 위치: 마지막 토큰(없으면 식 전체).</summary>
        private TextRange EndRange() => _t.Count >= 2 ? Span(_t[^2]) : new TextRange(0, source.Length);

        private OgnlParseException Unexpected(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.End:
                    return Fail(EndRange(), "식이 끝났는데 피연산자나 닫는 괄호가 없다");
                case TokenKind.Bad:
                    return Fail(Span(t), t.Text);
                case TokenKind.Op:
                    return Fail(Span(t), t.Text switch
                    {
                        "+" or "-" or "*" or "/" or "%" => $"부분집합 밖의 연산자 {t.Text}",
                        "?" or ":" => "부분집합 밖의 삼항 연산자 ? :",
                        "[" or "]" => "부분집합 밖의 색인 [ ]",
                        "{" or "}" => "부분집합 밖의 목록·선택·투영 { }",
                        "#" => "부분집합 밖의 #this·#변수",
                        "@" => "부분집합 밖의 정적 호출 @클래스@메서드",
                        "=" or "=~" => $"부분집합 밖의 대입·정규식 연산자 {t.Text}",
                        "&" or "|" or "^" or "~" or "<<" or ">>" or ">>>" => $"부분집합 밖의 비트 연산자 {t.Text}",
                        _ => $"구문 오류: 예상하지 못한 '{t.Text}'",
                    });
                case TokenKind.Ident:
                    if (t.Text == "not" && IsWord(Peek(1), "in"))
                    {
                        return Fail(new TextRange(t.Start, Peek(1).Start + Peek(1).Length - t.Start), "부분집합 밖의 연산자 not in");
                    }

                    if (t.Text is "in" or "instanceof" or "new" or "band" or "bor" or "xor" or "shl" or "shr" or "ushr")
                    {
                        return Fail(Span(t), $"부분집합 밖의 연산자 {t.Text}");
                    }

                    if (t.Text is "AND" or "OR")
                    {
                        return Fail(Span(t), $"구문 오류 '{t.Text}': MyBatis 3.5 도 ParseException, and·or 는 소문자만 쓴다");
                    }

                    return Fail(Span(t), $"구문 오류: 예상하지 못한 '{t.Text}'");
                default:
                    return Fail(Span(t), $"구문 오류: 예상하지 못한 '{SourceOf(t)}'");
            }
        }

        private Node ParseOr()
        {
            if (++_depth > MaxDepth)
            {
                throw Fail(Span(Peek()), "식이 너무 깊게 겹쳐 있다");
            }

            var left = ParseAnd();
            while (IsWord(Peek(), "or") || IsOp(Peek(), "||"))
            {
                Next();
                var right = ParseAnd();
                left = new OrNode(left, right);
            }

            _depth--;
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseEquality();
            while (IsWord(Peek(), "and") || IsOp(Peek(), "&&"))
            {
                Next();
                var right = ParseEquality();
                left = new AndNode(left, right);
            }

            return left;
        }

        private Node ParseEquality()
        {
            var left = ParseRelational();
            while (true)
            {
                var t = Peek();
                CompareOp op;
                if (IsOp(t, "==") || IsWord(t, "eq"))
                {
                    op = CompareOp.Eq;
                }
                else if (IsOp(t, "!=") || IsWord(t, "neq"))
                {
                    op = CompareOp.Ne;
                }
                else
                {
                    return left;
                }

                Next();
                left = new CompareNode(op, left, ParseRelational());
            }
        }

        private Node ParseRelational()
        {
            var left = ParseAdditive();
            while (true)
            {
                var t = Peek();
                CompareOp op;
                if (IsOp(t, "<") || IsWord(t, "lt"))
                {
                    op = CompareOp.Lt;
                }
                else if (IsOp(t, "<=") || IsWord(t, "lte"))
                {
                    op = CompareOp.Le;
                }
                else if (IsOp(t, ">") || IsWord(t, "gt"))
                {
                    op = CompareOp.Gt;
                }
                else if (IsOp(t, ">=") || IsWord(t, "gte"))
                {
                    op = CompareOp.Ge;
                }
                else
                {
                    return left;
                }

                Next();
                left = new CompareNode(op, left, ParseAdditive());
            }
        }

        private Node ParseAdditive()
        {
            var left = ParseUnary();
            while (allowPlus && IsOp(Peek(), "+"))
            {
                Next();
                left = new AddNode(left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            var t = Peek();
            if (IsOp(t, "!") || IsWord(t, "not") || IsOp(t, "-"))
            {
                if (++_depth > MaxDepth)
                {
                    throw Fail(Span(t), "식이 너무 깊게 겹쳐 있다");
                }

                Next();
                var operand = ParseUnary();
                _depth--;
                return IsOp(t, "-") ? new NegNode(t.Start, operand) : new NotNode(t.Start, operand);
            }

            return ParsePostfix();
        }

        private Node ParsePostfix()
        {
            var node = ParsePrimary();
            while (IsOp(Peek(), "."))
            {
                var dot = Next();
                var name = Peek();
                if (name.Kind != TokenKind.Ident)
                {
                    throw Unexpected(name.Kind == TokenKind.End ? dot : name);
                }

                Next();
                if (!IsOp(Peek(), "("))
                {
                    node = new PropertyNode(node, name.Text, name.Start + name.Length);
                    continue;
                }

                if (name.Text is not ("size" or "isEmpty" or "length" or "trim" or "equals"))
                {
                    throw Fail(Span(name), $"부분집합 밖의 메서드 {name.Text}()");
                }

                Next(); // (
                var args = new List<Node>();
                if (!IsOp(Peek(), ")"))
                {
                    while (true)
                    {
                        args.Add(ParseOr());
                        if (IsOp(Peek(), ","))
                        {
                            Next();
                            continue;
                        }

                        break;
                    }
                }

                if (!IsOp(Peek(), ")"))
                {
                    throw Unexpected(Peek());
                }

                var close = Next();
                int expected = name.Text == "equals" ? 1 : 0;
                if (args.Count != expected)
                {
                    throw Fail(Span(name), $"{name.Text}() 의 인자는 {expected}개여야 한다");
                }

                node = new MethodNode(node, name.Text, args, close.Start + close.Length);
            }

            return node;
        }

        private Node ParsePrimary()
        {
            var t = Peek();
            switch (t.Kind)
            {
                case TokenKind.Int or TokenKind.Float or TokenKind.Char or TokenKind.Str:
                    Next();
                    return new LiteralNode(t.Literal, t.Start, t.Start + t.Length);
                case TokenKind.Ident:
                    return ParseName(t);
                case TokenKind.Op when t.Text == "(":
                    Next();
                    var inner = ParseOr();
                    if (!IsOp(Peek(), ")"))
                    {
                        throw Unexpected(Peek());
                    }

                    var close = Next();
                    return new GroupNode(inner, t.Start, close.Start + close.Length);
                default:
                    throw Unexpected(t);
            }
        }

        private Node ParseName(Token t)
        {
            switch (t.Text)
            {
                case "null":
                    Next();
                    return new LiteralNode(null, t.Start, t.Start + t.Length);
                case "true":
                    Next();
                    return new LiteralNode(true, t.Start, t.Start + t.Length);
                case "false":
                    Next();
                    return new LiteralNode(false, t.Start, t.Start + t.Length);
                case "and" or "or" or "not" or "eq" or "neq" or "lt" or "lte" or "gt" or "gte" or "in" or "instanceof" or "new":
                    throw Unexpected(t);
            }

            Next();
            var path = t.Text;
            int end = t.Start + t.Length;
            // 점 경로: 뒤에 ( 가 붙지 않는 .이름 조각만 경로에 합친다. 메서드 호출 앞까지가 받는 값이다.
            while (IsOp(Peek(), ".") && Peek(1).Kind == TokenKind.Ident && !IsOp(Peek(2), "("))
            {
                Next();
                var seg = Next();
                path = path + "." + seg.Text;
                end = seg.Start + seg.Length;
            }

            return new PathNode(path, t.Start, end);
        }
    }
}
