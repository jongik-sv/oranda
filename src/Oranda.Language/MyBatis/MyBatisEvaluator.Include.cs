using System.Text;
using Oranda.Core.MyBatis;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

// include 펼치기: MyBatis XMLIncludeTransformer 처럼 파싱 때 조각을 그 자리에 끼운다(값과 무관).
// property 는 조각 안 글·속성의 ${이름} 을 바꾸고 중첩 include 로 이어진다. 문장 자신의 글은 바꾸지 않는다.
public sealed partial class MyBatisEvaluator
{
    /// <summary>펼친 include. <see cref="Children"/> = 조각 본문(property 치환 뒤).</summary>
    private sealed class IncludeNode : Node
    {
        public required string RefId { get; init; }

        public bool Found { get; set; }

        public List<Node> Children { get; } = [];
    }

    /// <summary>문장 본문을 펼친 결과와 펼치는 동안 생긴 문제(조각 없음·순환).</summary>
    private sealed record Expanded(ElementNode Root, List<Node> Body, List<MyBatisEvalIssue> Issues);

    private static Expanded Expand(MyBatisElement statement, IMyBatisFragmentResolver fragments)
    {
        var bare = new List<int>();
        var root = ParseXml(statement.Text, bare) ?? new ElementNode { Name = statement.Name };
        StampStatement(root);
        var issues = bare.Select(at => new MyBatisEvalIssue(MyBatisEvalIssueKind.InvalidTag, new TextRange(at, 1), null, BareLessThanReason)).ToList();
        var body = ExpandChildren(root.Children, fragments, null, new Dictionary<string, string>(StringComparer.Ordinal), [], issues, included: false);
        return new Expanded(root, body, issues);
    }

    // 문장 원문 노드는 StatementRange = Range.
    private static void StampStatement(Node node)
    {
        node.Origin = node.Origin with { StatementRange = node.Origin.Range };
        if (node is ElementNode e)
        {
            foreach (var c in e.Children)
            {
                StampStatement(c);
            }
        }
    }

    private const string BareLessThanReason = "xml 글 안의 < 는 &lt; 또는 CDATA 로 써야 한다(MyBatis 3.5 는 이 매퍼를 읽지 못한다).";

    private static List<Node> ExpandChildren(
        List<Node> children,
        IMyBatisFragmentResolver fragments,
        string? currentNamespace,
        Dictionary<string, string> props,
        List<string> including,
        List<MyBatisEvalIssue> issues,
        bool included)
    {
        var result = new List<Node>(children.Count);
        foreach (var child in children)
        {
            result.Add(ExpandNode(child, fragments, currentNamespace, props, including, issues, included));
        }

        return result;
    }

    private static Node ExpandNode(
        Node node,
        IMyBatisFragmentResolver fragments,
        string? currentNamespace,
        Dictionary<string, string> props,
        List<string> including,
        List<MyBatisEvalIssue> issues,
        bool included)
    {
        if (node is TextNode text)
        {
            return included && props.Count > 0 ? SubstituteText(text, props) : text;
        }

        var element = (ElementNode)node;
        if (!string.Equals(element.Name, "include", StringComparison.Ordinal))
        {
            var copy = new ElementNode { Name = element.Name, Origin = element.Origin };
            foreach (var a in element.Attributes)
            {
                copy.Attributes.Add(included && props.Count > 0 ? (a.Name, ReplaceProperties(a.Value, props), a.ValueRange) : a);
            }

            copy.Children.AddRange(ExpandChildren(element.Children, fragments, currentNamespace, props, including, issues, included));
            return copy;
        }

        var refid = ReplaceProperties(element.Attr("refid") ?? string.Empty, props);
        var include = new IncludeNode { RefId = refid, Origin = element.Origin };
        var fragment = refid.Length == 0 ? null : fragments.Resolve(refid, currentNamespace);
        if (fragment is null)
        {
            issues.Add(Issue(MyBatisEvalIssueKind.FragmentMissing, element.Origin, refid, $"sql 조각 「{refid}」 을 찾지 못했다."));
            return include;
        }

        var key = (fragment.Namespace ?? string.Empty) + "." + fragment.Id;
        if (including.Contains(key))
        {
            issues.Add(Issue(MyBatisEvalIssueKind.IncludeCycle, element.Origin, refid,
                $"include 가 돌고 돈다: {string.Join(" → ", including.Append(key))}"));
            return include;
        }

        // 이 include 의 property: 물려받은 것 + 자기 것(값은 물려받은 것으로 치환). MyBatis getVariablesContext 와 같다.
        var next = new Dictionary<string, string>(props, StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.Children.OfType<ElementNode>())
        {
            if (string.Equals(p.Name, "property", StringComparison.Ordinal) && p.Attr("name") is { } name)
            {
                if (!declared.Add(name))
                {
                    issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, p.Origin, name, $"include 의 property 「{name}」 이 둘 이상이다(MyBatis 3.5 는 BuilderException)."));
                }

                next[name] = ReplaceProperties(p.Attr("value") ?? string.Empty, props);
            }
        }

        var fragmentBare = new List<int>();
        var fragmentRoot = ParseXml(fragment.Text, fragmentBare);
        if (fragmentRoot is null)
        {
            issues.Add(Issue(MyBatisEvalIssueKind.FragmentMissing, element.Origin, refid, $"sql 조각 「{refid}」 원문을 읽지 못했다."));
            return include;
        }

        // 조각 노드의 StatementRange = 문장 원문의 바깥 include 태그(중첩이어도 문장에 보이는 자리). Fragment = 쓴 그대로의 refid.
        StampFragment(fragmentRoot, refid, element.Origin.StatementRange);
        foreach (var at in fragmentBare)
        {
            issues.Add(new MyBatisEvalIssue(MyBatisEvalIssueKind.InvalidTag, element.Origin.StatementRange, null, BareLessThanReason, refid, new TextRange(at, 1)));
        }

        // 중첩 include 도 문장 매퍼의 namespace 로 찾는다(MyBatis XMLIncludeTransformer 는 문장 쪽 builderAssistant 를 쓴다).
        including.Add(key);
        include.Children.AddRange(ExpandChildren(fragmentRoot.Children, fragments, currentNamespace, next, including, issues, included: true));
        including.RemoveAt(including.Count - 1);
        include.Found = true;
        return include;
    }

    private static void StampFragment(Node node, string fragmentId, TextRange statementRange)
    {
        node.Origin = new Origin(node.Origin.Range, fragmentId, statementRange);
        if (node is ElementNode e)
        {
            foreach (var c in e.Children)
            {
                StampFragment(c, fragmentId, statementRange);
            }
        }
    }

    // 조각 글의 ${이름} 중 property 에 있는 것만 바꾼다. 바꾼 값에 #{…}·${…} 가 있으면 다시 토큰으로 나눈다.
    private static TextNode SubstituteText(TextNode text, Dictionary<string, string> props)
    {
        var copy = new TextNode { Origin = text.Origin };
        if (text.Pieces.Any(p => p is HashPiece { Content: var h } && h.Contains("${", StringComparison.Ordinal)
            || p is DollarPiece { Content: var d } && d.Contains("${", StringComparison.Ordinal)))
        {
            // PropertyParser 는 토큰을 나누기 전에 글 전체를 바꾼다(#{${col}} → #{empNo}). 이때는 노드 전체를 다시 나눈다.
            var raw = string.Concat(text.Pieces.Select(p => p switch
            {
                HashPiece h => "#{" + h.Content + "}",
                DollarPiece d => "${" + d.Content + "}",
                LiteralPiece l => l.Text,
                _ => string.Empty,
            }));
            AddSplit(copy.Pieces, ReplaceProperties(raw, props), text.Origin.Range);
            return copy;
        }

        foreach (var piece in text.Pieces)
        {
            if (piece is DollarPiece d && props.TryGetValue(d.Content, out var value))
            {
                AddSplit(copy.Pieces, value, d.Range);
            }
            else if (piece is LiteralPiece l && l.Text.Contains("${", StringComparison.Ordinal))
            {
                AddSplit(copy.Pieces, ReplaceProperties(l.Text, props), l.Range);
            }
            else
            {
                copy.Pieces.Add(piece);
            }
        }

        return copy;
    }

    private static void AddSplit(List<Piece> pieces, string text, TextRange range)
    {
        int i = 0;
        var literal = new StringBuilder();
        while (i < text.Length)
        {
            if ((text[i] == '#' || text[i] == '$') && i + 1 < text.Length && text[i + 1] == '{')
            {
                int close = text.IndexOf('}', i + 2);
                if (close > 0)
                {
                    if (literal.Length > 0)
                    {
                        pieces.Add(new LiteralPiece(literal.ToString(), range));
                        literal.Clear();
                    }

                    var content = text[(i + 2)..close];
                    pieces.Add(text[i] == '#' ? new HashPiece(content, range) : new DollarPiece(content, range));
                    i = close + 1;
                    continue;
                }
            }

            literal.Append(text[i]);
            i++;
        }

        if (literal.Length > 0)
        {
            pieces.Add(new LiteralPiece(literal.ToString(), range));
        }
    }

    // MyBatis PropertyParser: ${열쇠} 중 열쇠가 있는 것만 바꾸고 나머지는 그대로 둔다.
    private static string ReplaceProperties(string text, Dictionary<string, string> props)
    {
        if (props.Count == 0 || !text.Contains("${", StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf("${", i, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf('}', open + 2);
            if (close < 0)
            {
                break;
            }

            sb.Append(text, i, open - i);
            var key = text[(open + 2)..close];
            if (props.TryGetValue(key, out var value))
            {
                sb.Append(value);
            }
            else
            {
                sb.Append(text, open, close + 1 - open);
            }

            i = close + 1;
        }

        sb.Append(text, i, text.Length - i);
        return sb.ToString();
    }

    private static MyBatisEvalIssue Issue(MyBatisEvalIssueKind kind, Origin origin, string? expression, string reason) =>
        new(kind, origin.StatementRange, expression, reason, origin.Fragment, origin.FragmentRange);
}
