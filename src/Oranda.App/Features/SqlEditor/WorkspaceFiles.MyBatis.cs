using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Oranda.App.Features.SqlEditor;

// WorkspaceFiles 의 일부: MyBatis 매퍼 읽기(TSK-01-02 에서 나눔, 동작 불변). 이 파일은 TSK-02-02 소유(docs/oranda1/design.md 7장·11장).
// Language 형 static 필드를 두지 않는다: KindOf·IsMyBatis 가 시작 복원 때 불리므로 그때 Oranda.Language 가 로드되면 안 된다.
public static partial class WorkspaceFiles
{
    // ---- MyBatis --------------------------------------------------------------------------

    /// <summary>
    /// MyBatis 매퍼의 select·insert·update·delete 를 탭으로 바꾼다. <c>#{x}</c> 는 바인드 변수 <c>:x</c>,
    /// <c>${x}</c> 는 치환 변수 <c>&amp;x</c> 가 된다. 동적 태그는 실행해 볼 수 있는 모양으로 단순화한다:
    /// where·set·trim 은 MyBatis 규칙대로 앞뒤를 다듬고, if·foreach 는 내용을 남기고 조건을 주석으로 붙이며,
    /// choose 는 첫 when 만 남긴다. include 는 같은 파일의 sql 조각을 펼친다. DTD 는 읽지 않는다(외부 요청 없음).
    /// </summary>
    public static WorkspaceFile ReadMyBatis(string xml, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(xml), settings))
        {
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        var root = doc.Root ?? throw new FormatException("XML 루트가 없다.");
        if (root.Name.LocalName != "mapper")
        {
            throw new FormatException($"MyBatis 매퍼가 아니다(루트 <{root.Name.LocalName}>).");
        }

        var ns = (string?)root.Attribute("namespace");
        var fragments = root.Elements().Where(e => e.Name.LocalName == "sql" && e.Attribute("id") is not null)
            .GroupBy(e => (string)e.Attribute("id")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var converter = new MyBatisText(fragments);
        var tabs = new List<WorkspaceFileTab>();
        foreach (var e in root.Elements())
        {
            var kind = e.Name.LocalName;
            if (kind is not ("select" or "insert" or "update" or "delete"))
            {
                continue;
            }

            var id = (string?)e.Attribute("id") ?? kind + (tabs.Count + 1).ToString(CultureInfo.InvariantCulture);
            var body = Dedent(converter.Render(e), keepBlankLines: false);
            var header = $"-- MyBatis {(ns is null ? id : ns + "." + id)} <{kind}>";
            tabs.Add(new WorkspaceFileTab(id, header + "\n" + body));
        }

        var notes = new List<string>();
        if (tabs.Count == 0)
        {
            notes.Add("select·insert·update·delete 문장이 없다.");
        }

        if (converter.DynamicTags > 0)
        {
            notes.Add($"동적 태그 {converter.DynamicTags.ToString(CultureInfo.InvariantCulture)}개를 실행 가능한 모양으로 단순화했다(조건은 주석으로 남김).");
        }

        return new WorkspaceFile(ns?.Split('.').Last() ?? fallbackName, tabs, notes);
    }

    private sealed class MyBatisText(Dictionary<string, XElement> fragments)
    {
        private readonly HashSet<string> _including = new(StringComparer.Ordinal);

        public int DynamicTags { get; private set; }

        public string Render(XElement element)
        {
            var sb = new StringBuilder();
            foreach (var node in element.Nodes())
            {
                sb.Append(RenderNode(node));
            }

            return sb.ToString();
        }

        private string RenderNode(XNode node) => node switch
        {
            XCData c => Params(c.Value),
            XText t => Params(t.Value),
            XElement e => RenderElement(e),
            _ => string.Empty,
        };

        private string RenderElement(XElement e)
        {
            switch (e.Name.LocalName)
            {
                case "include":
                    {
                        var refid = (string?)e.Attribute("refid");
                        if (refid is null || !fragments.TryGetValue(refid, out var fragment) || !_including.Add(refid))
                        {
                            return $" /* include {refid} */ ";
                        }

                        try
                        {
                            return Render(fragment);
                        }
                        finally
                        {
                            _including.Remove(refid);
                        }
                    }

                case "if":
                    DynamicTags++;
                    return Render(e).TrimEnd() + $" /* if {(string?)e.Attribute("test")} */\n";
                case "where":
                    DynamicTags++;
                    return Trim(Render(e), "WHERE", ["AND ", "OR "], []);
                case "set":
                    DynamicTags++;
                    return Trim(Render(e), "SET", [], [","]);
                case "trim":
                    DynamicTags++;
                    return Trim(Render(e), (string?)e.Attribute("prefix") ?? string.Empty,
                        Split((string?)e.Attribute("prefixOverrides")), Split((string?)e.Attribute("suffixOverrides")),
                        (string?)e.Attribute("suffix") ?? string.Empty);
                case "choose":
                    {
                        DynamicTags++;
                        var first = e.Elements().FirstOrDefault(c => c.Name.LocalName == "when")
                            ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "otherwise");
                        return first is null ? string.Empty
                            : Render(first).TrimEnd() + $" /* choose: {(string?)first.Attribute("test") ?? "otherwise"} */\n";
                    }

                case "foreach":
                    DynamicTags++;
                    return $" /* foreach {(string?)e.Attribute("collection")} */ "
                        + ((string?)e.Attribute("open") ?? string.Empty) + Render(e).Trim() + ((string?)e.Attribute("close") ?? string.Empty);
                case "bind":
                    DynamicTags++;
                    return $" /* bind {(string?)e.Attribute("name")} = {(string?)e.Attribute("value")} */ ";
                case "selectKey":
                    return string.Empty;
                default:
                    return Render(e);
            }
        }

        private static string[] Split(string? overrides) =>
            overrides is null ? [] : overrides.Split('|', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>MyBatis &lt;trim&gt; 규칙: 내용이 비면 아무것도, 아니면 앞뒤 겹치는 낱말을 떼고 접두·접미를 붙인다.</summary>
        private static string Trim(string inner, string prefix, string[] prefixOverrides, string[] suffixOverrides, string suffix = "")
        {
            var body = inner.Trim();
            if (body.Length == 0)
            {
                return string.Empty;
            }

            foreach (var p in prefixOverrides)
            {
                if (body.StartsWith(p.Trim(), StringComparison.OrdinalIgnoreCase)
                    && (body.Length == p.Trim().Length || char.IsWhiteSpace(body[p.Trim().Length]) || p.Trim().EndsWith(',')))
                {
                    body = body[p.Trim().Length..].TrimStart();
                    break;
                }
            }

            foreach (var s in suffixOverrides)
            {
                var t = s.Trim();
                var cut = TrailingComment(body);
                if (cut.Code.EndsWith(t, StringComparison.OrdinalIgnoreCase))
                {
                    body = cut.Code[..^t.Length].TrimEnd() + cut.Comment;
                    break;
                }
            }

            return $"\n{prefix} {body}{(suffix.Length > 0 ? " " + suffix : string.Empty)}\n";
        }

        /// <summary>끝의 <c>/* if … */</c> 주석을 떼어 놓는다(접미 「,」 가 주석 앞에 있으므로).</summary>
        private static (string Code, string Comment) TrailingComment(string body)
        {
            var m = Regex.Match(body, @"(\s*/\*[^*]*\*/\s*)$");
            return m.Success ? (body[..m.Index].TrimEnd(), " " + m.Value.Trim()) : (body, string.Empty);
        }

        /// <summary><c>#{a.b, jdbcType=X}</c> → <c>:a_b</c>, <c>${x}</c> → <c>&amp;x</c>.</summary>
        private static string Params(string text)
        {
            text = Regex.Replace(text, @"#\{\s*([A-Za-z_][\w.]*)[^}]*\}", m => ":" + m.Groups[1].Value.Replace('.', '_'));
            return Regex.Replace(text, @"\$\{\s*([A-Za-z_][\w.]*)[^}]*\}", m => "&" + m.Groups[1].Value.Replace('.', '_'));
        }
    }
}
