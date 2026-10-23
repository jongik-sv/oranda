using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Language.MyBatis;

// 옛 ReadMyBatis 표기(MyBatisEvalOptions.LegacyNotation, design.md 7장). 「모두 켬」 모양을 사람이 읽고 바로 실행해 볼 수 있게
// #{a.b} → :a_b, ${x} → &x, 조건은 /* if … */ 주석으로 남기고, where·set·trim 은 앞뒤만 다듬는다. 옛 WorkspaceFiles.MyBatisText 를 옮겼다.
public sealed partial class MyBatisEvaluator
{
    private static string RenderLegacy(List<Node> body)
    {
        var sb = new StringBuilder();
        foreach (var node in body)
        {
            sb.Append(LegacyNode(node));
        }

        return sb.ToString();
    }

    private static string LegacyNode(Node node)
    {
        switch (node)
        {
            case TextNode text:
                {
                    var sb = new StringBuilder();
                    foreach (var piece in text.Pieces)
                    {
                        sb.Append(piece switch
                        {
                            LiteralPiece l => l.Text,
                            HashPiece h => LegacyName(':', h.Content, "#{"),
                            DollarPiece d => LegacyName('&', d.Content, "${"),
                            _ => string.Empty,
                        });
                    }

                    return sb.ToString();
                }

            case IncludeNode include:
                return include.Found ? RenderLegacy(include.Children) : $" /* include {include.RefId} */ ";
        }

        var e = (ElementNode)node;
        switch (e.Name)
        {
            case "if":
                return RenderLegacy(e.Children).TrimEnd() + $" /* if {e.Attr("test")} */\n";
            case "where":
                return LegacyTrim(RenderLegacy(e.Children), "WHERE", ["AND ", "OR "], []);
            case "set":
                return LegacyTrim(RenderLegacy(e.Children), "SET", [], [","]);
            case "trim":
                return LegacyTrim(RenderLegacy(e.Children), e.Attr("prefix") ?? string.Empty,
                    LegacySplit(e.Attr("prefixOverrides")), LegacySplit(e.Attr("suffixOverrides")), e.Attr("suffix") ?? string.Empty);
            case "choose":
                {
                    var children = e.Children.OfType<ElementNode>().ToList();
                    var first = children.FirstOrDefault(c => c.Name == "when") ?? children.FirstOrDefault(c => c.Name == "otherwise");
                    return first is null ? string.Empty
                        : RenderLegacy(first.Children).TrimEnd() + $" /* choose: {first.Attr("test") ?? "otherwise"} */\n";
                }

            case "foreach":
                return $" /* foreach {e.Attr("collection")} */ " + (e.Attr("open") ?? string.Empty) + RenderLegacy(e.Children).Trim() + (e.Attr("close") ?? string.Empty);
            case "bind":
                return $" /* bind {e.Attr("name")} = {e.Attr("value")} */ ";
            case "selectKey":
                return string.Empty;
            default:
                return RenderLegacy(e.Children);
        }
    }

    // #{a.b, jdbcType=X} → :a_b, ${x} → &x. 이름으로 시작하지 않는 식은 원래 글 그대로.
    private static string LegacyName(char sigil, string content, string open)
    {
        var m = LegacyNameRegex().Match(content);
        return m.Success ? sigil + m.Groups[1].Value.Replace('.', '_') : open + content + "}";
    }

    [GeneratedRegex(@"^\s*([A-Za-z_][\w.]*)")]
    private static partial Regex LegacyNameRegex();

    private static string[] LegacySplit(string? overrides) =>
        overrides is null ? [] : overrides.Split('|', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>옛 &lt;trim&gt; 단순화: 내용이 비면 아무것도, 아니면 앞뒤 겹치는 낱말을 떼고 접두·접미를 붙인다.</summary>
    private static string LegacyTrim(string inner, string prefix, string[] prefixOverrides, string[] suffixOverrides, string suffix = "")
    {
        var body = inner.Trim();
        if (body.Length == 0)
        {
            return string.Empty;
        }

        foreach (var p in prefixOverrides)
        {
            var t = p.Trim();
            if (body.StartsWith(t, StringComparison.OrdinalIgnoreCase)
                && (body.Length == t.Length || char.IsWhiteSpace(body[t.Length]) || t.EndsWith(',')))
            {
                body = body[t.Length..].TrimStart();
                break;
            }
        }

        foreach (var s in suffixOverrides)
        {
            var t = s.Trim();
            var (code, comment) = LegacyTrailingComment(body);
            if (code.EndsWith(t, StringComparison.OrdinalIgnoreCase))
            {
                body = code[..^t.Length].TrimEnd() + comment;
                break;
            }
        }

        return $"\n{prefix} {body}{(suffix.Length > 0 ? " " + suffix : string.Empty)}\n";
    }

    /// <summary>끝의 <c>/* if … */</c> 주석을 떼어 놓는다(접미 「,」 가 주석 앞에 있으므로).</summary>
    private static (string Code, string Comment) LegacyTrailingComment(string body)
    {
        var m = LegacyCommentRegex().Match(body);
        return m.Success ? (body[..m.Index].TrimEnd(), " " + m.Value.Trim()) : (body, string.Empty);
    }

    [GeneratedRegex(@"(\s*/\*[^*]*\*/\s*)$")]
    private static partial Regex LegacyCommentRegex();
}
