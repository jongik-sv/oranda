using System.Text;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>요소 본문 조각 하나: 태그 한 줄이거나 SQL 덩어리. <see cref="Depth"/> 0 = 요소 바로 안.</summary>
internal sealed record ConvertPart(bool IsTag, string Text, int Depth);

public static partial class ConvertEngine
{
    // 들여쓰기: 요소 태그 0칸, 안쪽 태그·CDATA 표지 2칸, SQL 4칸(깊이마다 2칸 더).
    const int TagIndent = 2;
    const int SqlIndent = 4;

    /// <summary>바꾼 SQL 을 본문 조각으로 나눈다. 감싸기 계획이 없으면 SQL 한 덩어리다.</summary>
    static List<ConvertPart> BuildParts(string sql, ConvertRewriter rewriter, ConvertWherePlan? plan, IReadOnlyDictionary<string, MyBatisScenarioValue> scenario)
    {
        var parts = new List<ConvertPart>();
        if (plan is null)
        {
            AddSql(parts, Apply(sql, rewriter.Edits, 0, sql.Length), 0);
            return parts;
        }

        AddSql(parts, Apply(sql, rewriter.Edits, 0, plan.WhereStart), 0);
        parts.Add(new ConvertPart(true, "<where>", 0));
        var pending = new StringBuilder();
        for (int i = 0; i < plan.Conditions.Count; i++)
        {
            var cond = plan.Conditions[i];
            string text = Apply(sql, rewriter.Edits, cond.Start, cond.End).Trim();
            if (!IsIfWrapped(cond, rewriter, scenario))
            {
                // 바인드 없는(또는 첫 시나리오 값이 Null 인) 조건은 그대로 두되 이어지는 것끼리 한 덩어리로 묶는다(<where> 가 맨 앞 AND 를 지운다).
                if (i > 0) pending.Append("AND ");
                pending.Append(text).Append('\n');
                continue;
            }

            AddSql(parts, pending.ToString(), 1);
            pending.Clear();
            var names = new List<string>();
            foreach (int tokIndex in cond.BindTokens)
            {
                string name = rewriter.CanonByTok[tokIndex];
                if (!names.Contains(name)) names.Add(name);
            }

            string test = string.Join(" and ", names.Select(n => n + " != null"));
            parts.Add(new ConvertPart(true, "<if test=\"" + test + "\">", 1));
            AddSql(parts, "AND " + text, 2);
            parts.Add(new ConvertPart(true, "</if>", 1));
        }

        AddSql(parts, pending.ToString(), 1);
        parts.Add(new ConvertPart(true, "</where>", 0));
        AddSql(parts, Apply(sql, rewriter.Edits, plan.ClauseEnd, sql.Length), 0);
        return parts;
    }

    /// <summary>
    /// 조건을 <c>&lt;if&gt;</c> 로 감싸는가: 바인드가 있고 전부 첫 시나리오 값이 Null 이 아닐 때.
    /// Null 값 바인드가 든 조건은 감싸면 조건이 빠져 원래 탭(NULL 비교) 결과와 달라지므로 그대로 둔다.
    /// </summary>
    static bool IsIfWrapped(ConvertWhereCondition cond, ConvertRewriter rewriter, IReadOnlyDictionary<string, MyBatisScenarioValue> scenario) =>
        cond.BindTokens.Count > 0
        && cond.BindTokens.All(k => scenario.TryGetValue(rewriter.CanonByTok[k], out var v) && v.Kind != MyBatisScenarioValueKind.Null);

    static void AddSql(List<ConvertPart> parts, string text, int depth)
    {
        string trimmed = text.Trim();
        if (trimmed.Length > 0) parts.Add(new ConvertPart(false, trimmed, depth));
    }

    /// <summary>글 <c>[from, to)</c> 에 편집을 적용한 결과(편집은 범위를 가로지르지 않는다).</summary>
    static string Apply(string sql, List<ConvertRewriter.Edit> edits, int from, int to)
    {
        var sb = new StringBuilder();
        int pos = from;
        foreach (var e in edits)
        {
            if (e.End <= from) continue;
            if (e.Start >= to) break;
            sb.Append(sql, pos, e.Start - pos).Append(e.Text);
            pos = e.End;
        }

        return sb.Append(sql, pos, to - pos).ToString();
    }

    /// <summary>요소 원문을 만든다. 줄 끝은 <c>\n</c>, CDATA 는 &lt; &gt; &amp; 가 든 SQL 덩어리마다 따로 쓴다.</summary>
    static string Render(ConvertStatementKind kind, string id, List<ConvertPart> parts, bool useCdata)
    {
        string tag = kind switch
        {
            ConvertStatementKind.Select => "select",
            ConvertStatementKind.Insert => "insert",
            ConvertStatementKind.Delete => "delete",
            _ => "update",
        };
        var sb = new StringBuilder();
        sb.Append('<').Append(tag).Append(" id=\"").Append(id).Append('"');
        if (kind == ConvertStatementKind.Callable) sb.Append(" statementType=\"CALLABLE\"");
        sb.Append(">\n");

        foreach (var part in parts)
        {
            if (part.IsTag)
            {
                sb.Append(' ', TagIndent + (2 * part.Depth)).Append(part.Text).Append('\n');
                continue;
            }

            string body = IndentLines(part.Text, SqlIndent + (2 * part.Depth));
            bool needsEscape = part.Text.AsSpan().IndexOfAny('<', '>', '&') >= 0;
            if (!needsEscape)
            {
                sb.Append(body).Append('\n');
            }
            else if (useCdata)
            {
                // 글 안 ]]> 는 CDATA 를 나눠 쓴다.
                string pad = new(' ', TagIndent + (2 * part.Depth));
                sb.Append(pad).Append("<![CDATA[\n")
                    .Append(body.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal)).Append('\n')
                    .Append(pad).Append("]]>\n");
            }
            else
            {
                sb.Append(body.Replace("&", "&amp;", StringComparison.Ordinal)
                    .Replace("<", "&lt;", StringComparison.Ordinal)
                    .Replace(">", "&gt;", StringComparison.Ordinal)).Append('\n');
            }
        }

        return sb.Append("</").Append(tag).Append(">\n").ToString();
    }

    /// <summary>
    /// 요소 원문의 둘째 줄부터 <paramref name="indent"/> 를 붙인다(쓰기용). 빈 줄과 SQL 문자열·따옴표 이름 안에서 시작하는 줄은 값이 바뀌므로 그대로 둔다.
    /// 끝 줄바꿈은 떼고 줄 끝은 <c>\n</c> 이다.
    /// </summary>
    public static string IndentElement(string elementText, string indent)
    {
        ArgumentNullException.ThrowIfNull(elementText);
        ArgumentNullException.ThrowIfNull(indent);

        string text = elementText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', '\r');
        var sb = new StringBuilder();
        int state = 0, pos = 0;
        bool first = true;
        while (true)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(pos, end - pos);
            if (!first && !IsInsideLiteral(state) && !line.IsWhiteSpace()) sb.Append(indent);
            sb.Append(line);
            first = false;

            state = LexLineState(line, state);
            if (nl < 0) break;
            sb.Append('\n');
            pos = nl + 1;
        }

        return sb.ToString();
    }

    /// <summary>줄마다 <paramref name="pad"/> 칸 들여쓴다. 빈 줄은 비우고, 여러 줄 문자열·따옴표 이름 안 줄은 값이 바뀌므로 그대로 둔다.</summary>
    static string IndentLines(string text, int pad)
    {
        var sb = new StringBuilder();
        int state = 0, pos = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(pos, end - pos);
            bool inLiteral = IsInsideLiteral(state);
            if (inLiteral) sb.Append(line);
            else if (!line.IsWhiteSpace()) sb.Append(' ', pad).Append(line);

            state = LexLineState(line, state);
            if (nl < 0) break;
            sb.Append('\n');
            pos = nl + 1;
        }

        return sb.ToString();
    }
}
