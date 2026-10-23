using System.Globalization;
using System.Text;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

// 문장·조각 원문을 범위를 지키며 읽는 작은 XML 파서. XmlReader 는 줄·칸만 알려 주므로 오프셋이 필요한 계산기는 직접 읽는다.
// 매퍼는 이미 잘 짜인 xml 이라는 전제다(깨진 곳은 남은 글자를 글로 본다). 주석·처리 명령은 버린다(MyBatis 도 버린다).
public sealed partial class MyBatisEvaluator
{
    /// <summary>노드가 나온 곳. 문장 원문이면 <see cref="Fragment"/> 가 null 이고 <see cref="Range"/> = <see cref="StatementRange"/>.</summary>
    private readonly record struct Origin(TextRange Range, string? Fragment, TextRange StatementRange)
    {
        public TextRange? FragmentRange => Fragment is null ? null : Range;
    }

    private abstract class Node
    {
        public Origin Origin { get; set; }
    }

    /// <summary>글 조각 하나. <c>#{…}</c>·<c>${…}</c> 는 따로 떼어 둔다(엔티티는 푼 글).</summary>
    private abstract record Piece(TextRange Range);

    private sealed record LiteralPiece(string Text, TextRange Range) : Piece(Range);

    /// <summary><c>#{…}</c>. <see cref="Content"/> = 괄호 안 글(<c>ename, jdbcType=VARCHAR</c>).</summary>
    private sealed record HashPiece(string Content, TextRange Range) : Piece(Range);

    /// <summary><c>${…}</c>. <see cref="Content"/> = 괄호 안 글.</summary>
    private sealed record DollarPiece(string Content, TextRange Range) : Piece(Range);

    private sealed class TextNode : Node
    {
        public List<Piece> Pieces { get; } = [];
    }

    private sealed class ElementNode : Node
    {
        public required string Name { get; init; }

        public List<(string Name, string Value, TextRange ValueRange)> Attributes { get; } = [];

        public List<Node> Children { get; } = [];

        public string? Attr(string name)
        {
            foreach (var a in Attributes)
            {
                if (string.Equals(a.Name, name, StringComparison.Ordinal))
                {
                    return a.Value;
                }
            }

            return null;
        }

        public TextRange? AttrRange(string name)
        {
            foreach (var a in Attributes)
            {
                if (string.Equals(a.Name, name, StringComparison.Ordinal))
                {
                    return a.ValueRange;
                }
            }

            return null;
        }
    }

    /// <summary>원문의 첫 요소를 읽는다. 요소가 없으면 null. 태그가 아닌 맨 <c>&lt;</c> 는 글로 읽고 그 자리를 <paramref name="bareLessThan"/> 에 모은다.</summary>
    private static ElementNode? ParseXml(string text, List<int>? bareLessThan = null)
    {
        int pos = 0;
        var stack = new Stack<(ElementNode Node, int Start)>();
        ElementNode? root = null;
        while (pos < text.Length)
        {
            if (text[pos] != '<' || !IsTagStart(text, pos))
            {
                int end = NextTag(text, pos, bareLessThan);

                if (stack.Count > 0)
                {
                    stack.Peek().Node.Children.Add(Text(text, pos, end, cdata: false));
                }

                pos = end;
                continue;
            }

            if (string.CompareOrdinal(text, pos, "<!--", 0, 4) == 0)
            {
                pos = Skip(text, pos, "-->");
                continue;
            }

            if (string.CompareOrdinal(text, pos, "<![CDATA[", 0, 9) == 0)
            {
                int bodyStart = pos + 9;
                int close = text.IndexOf("]]>", bodyStart, StringComparison.Ordinal);
                int bodyEnd = close < 0 ? text.Length : close;
                if (stack.Count > 0)
                {
                    stack.Peek().Node.Children.Add(Text(text, bodyStart, bodyEnd, cdata: true));
                }

                pos = close < 0 ? text.Length : close + 3;
                continue;
            }

            if (pos + 1 < text.Length && (text[pos + 1] == '?' || text[pos + 1] == '!'))
            {
                pos = Skip(text, pos, ">");
                continue;
            }

            if (pos + 1 < text.Length && text[pos + 1] == '/')
            {
                int gt = text.IndexOf('>', pos);
                int tagEnd = gt < 0 ? text.Length : gt + 1;
                if (stack.Count > 0)
                {
                    var (node, start) = stack.Pop();
                    node.Origin = new Origin(new TextRange(start, tagEnd - start), null, default);
                    if (stack.Count == 0)
                    {
                        return node;
                    }
                }

                pos = tagEnd;
                continue;
            }

            var (element, after, selfClosing) = OpenTag(text, pos);
            if (stack.Count > 0)
            {
                stack.Peek().Node.Children.Add(element);
            }
            else
            {
                root = element;
            }

            if (selfClosing)
            {
                element.Origin = new Origin(new TextRange(pos, after - pos), null, default);
                if (stack.Count == 0)
                {
                    return element;
                }
            }
            else
            {
                stack.Push((element, pos));
            }

            pos = after;
        }

        // 닫히지 않은 요소는 원문 끝까지로 본다.
        while (stack.Count > 0)
        {
            var (node, start) = stack.Pop();
            node.Origin = new Origin(new TextRange(start, text.Length - start), null, default);
        }

        return root;
    }

    // XML 이름 시작 글자·/·!·? 가 뒤따라야 태그다. 아니면(SQL 의 A < 1) 글이다(MyBatis 는 이 매퍼를 읽지 못한다).
    private static bool IsTagStart(string text, int pos) =>
        pos + 1 < text.Length && (char.IsLetter(text[pos + 1]) || text[pos + 1] is '_' or ':' or '/' or '!' or '?');

    private static int NextTag(string text, int from, List<int>? bareLessThan)
    {
        int i = from;
        while (i < text.Length)
        {
            if (text[i] == '<')
            {
                if (IsTagStart(text, i))
                {
                    return i;
                }

                bareLessThan?.Add(i);
            }

            i++;
        }

        return text.Length;
    }

    private static int Skip(string text, int pos, string terminator)
    {
        int end = text.IndexOf(terminator, pos, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + terminator.Length;
    }

    private static (ElementNode Node, int After, bool SelfClosing) OpenTag(string text, int pos)
    {
        int i = pos + 1;
        int nameStart = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/')
        {
            i++;
        }

        var element = new ElementNode { Name = text[nameStart..i] };
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            if (text[i] == '>')
            {
                return (element, i + 1, false);
            }

            if (text[i] == '/')
            {
                int gt = text.IndexOf('>', i);
                return (element, gt < 0 ? text.Length : gt + 1, true);
            }

            int an = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/')
            {
                i++;
            }

            var attrName = text[an..i];
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i < text.Length && text[i] == '=')
            {
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (i < text.Length && (text[i] == '"' || text[i] == '\''))
                {
                    char quote = text[i];
                    int vs = i + 1;
                    int ve = text.IndexOf(quote, vs);
                    if (ve < 0)
                    {
                        ve = text.Length;
                    }

                    element.Attributes.Add((attrName, AttributeValue(text, vs, ve), new TextRange(vs, ve - vs)));
                    i = Math.Min(ve + 1, text.Length);
                    continue;
                }
            }

            if (attrName.Length == 0)
            {
                i++;
            }
        }

        return (element, text.Length, false);
    }

    // XML 속성 값 정규화: 원문의 줄바꿈·탭은 공백 하나가 되고, 문자 참조(&#10;)로 쓴 것은 그대로 남는다(정규화 뒤에 엔티티를 푼다).
    private static string AttributeValue(string text, int start, int end)
    {
        var raw = text[start..end];
        if (raw.IndexOfAny(['\n', '\r', '\t']) >= 0)
        {
            raw = raw.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        }

        return Decode(raw, 0, raw.Length);
    }

    /// <summary>글 노드를 만든다. <c>#{</c>·<c>${</c> 토큰은 MyBatis GenericTokenParser 처럼 첫 <c>}</c> 까지, 앞의 <c>\</c> 는 이스케이프.</summary>
    private static TextNode Text(string text, int start, int end, bool cdata)
    {
        var node = new TextNode { Origin = new Origin(new TextRange(start, end - start), null, default) };
        var literal = new StringBuilder();
        int literalStart = start;
        int i = start;
        while (i < end)
        {
            char c = text[i];
            if ((c == '#' || c == '$') && i + 1 < end && text[i + 1] == '{')
            {
                if (i > start && text[i - 1] == '\\')
                {
                    literal.Length--;
                    literal.Append(c);
                    i++;
                    continue;
                }

                int close = text.IndexOf('}', i + 2, end - i - 2);
                if (close >= 0)
                {
                    Flush(node, literal, literalStart, i);
                    var content = cdata ? text[(i + 2)..close] : Decode(text, i + 2, close);
                    var range = new TextRange(i, close + 1 - i);
                    node.Pieces.Add(c == '#' ? new HashPiece(content, range) : new DollarPiece(content, range));
                    i = close + 1;
                    literalStart = i;
                    continue;
                }
            }

            if (c == '&' && !cdata)
            {
                int semi = text.IndexOf(';', i, Math.Min(12, end - i));
                if (semi > i && TryEntity(text.AsSpan(i + 1, semi - i - 1), out var decoded))
                {
                    literal.Append(decoded);
                    i = semi + 1;
                    continue;
                }
            }

            if (c == '\r')
            {
                // XML 줄 끝 정규화: CRLF·CR → LF.
                literal.Append('\n');
                i += i + 1 < end && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }

            literal.Append(c);
            i++;
        }

        Flush(node, literal, literalStart, end);
        return node;
    }

    private static void Flush(TextNode node, StringBuilder literal, int start, int end)
    {
        if (literal.Length > 0)
        {
            node.Pieces.Add(new LiteralPiece(literal.ToString(), new TextRange(start, end - start)));
            literal.Clear();
        }
    }

    private static string Decode(string text, int start, int end)
    {
        int amp = text.IndexOf('&', start, end - start);
        if (amp < 0)
        {
            return text[start..end];
        }

        var sb = new StringBuilder(end - start);
        int i = start;
        while (i < end)
        {
            if (text[i] == '&')
            {
                int semi = text.IndexOf(';', i, Math.Min(12, end - i));
                if (semi > i && TryEntity(text.AsSpan(i + 1, semi - i - 1), out var decoded))
                {
                    sb.Append(decoded);
                    i = semi + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool TryEntity(ReadOnlySpan<char> name, out string value)
    {
        value = name switch
        {
            "lt" => "<",
            "gt" => ">",
            "amp" => "&",
            "quot" => "\"",
            "apos" => "'",
            _ => string.Empty,
        };
        if (value.Length > 0)
        {
            return true;
        }

        if (name.Length > 1 && name[0] == '#')
        {
            bool hex = name[1] is 'x' or 'X';
            var digits = hex ? name[2..] : name[1..];
            if (int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int code)
                && code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
            {
                value = char.ConvertFromUtf32(code);
                return true;
            }
        }

        return false;
    }
}
