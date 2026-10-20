using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

/// <summary>작업 공간 파일 안의 탭 하나: 탭 이름과 SQL. <see cref="Connection"/> 은 접속 프로필 ID(없으면 null), <see cref="Binds"/> 는 파일이 싣고 있는 바인드 값(없으면 null).</summary>
public sealed record WorkspaceFileTab(string Title, string Text, string? Connection = null, IReadOnlyList<WorkspaceBind>? Binds = null);

/// <summary>읽은 작업 공간 파일. <see cref="Notes"/> 는 읽으면서 바꾼 것에 대한 알림(MyBatis 동적 태그 단순화 등).</summary>
public sealed record WorkspaceFile(string Name, IReadOnlyList<WorkspaceFileTab> Tabs, IReadOnlyList<string> Notes);

/// <summary>
/// 작업 공간 파일(Orange SQL Tool 의 Workspace 파일에 해당). 탭 여러 개를 파일 하나로 저장·연다.
/// 저장 형식은 사람이 읽고 차이 보기 쉬운 Markdown 이고, MyBatis 매퍼 XML 은 읽기만 한다(문장 하나가 탭 하나).
/// </summary>
public static class WorkspaceFiles
{
    /// <summary>Markdown 파일 둘째 줄의 표지. 읽을 때 꼭 있어야 하지는 않다(손으로 쓴 Markdown 도 연다).</summary>
    public const string Marker = "<!-- oranda-workspace 1 -->";

    public static bool IsMyBatis(string path) =>
        string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase);

    public static bool IsSqd(string path) =>
        string.Equals(Path.GetExtension(path), ".sqd", StringComparison.OrdinalIgnoreCase);

    /// <summary>다른 도구가 만든 읽기 전용 원본(MyBatis .xml, Orange .sqd). 저장은 Markdown 작업 공간으로 간다.</summary>
    public static bool IsReadOnlySource(string path) => IsMyBatis(path) || IsSqd(path);

    /// <summary>파일을 읽는다: .xml 은 MyBatis 매퍼, .sqd 는 Orange 작업 공간, 나머지는 Markdown.</summary>
    public static WorkspaceFile Load(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (IsSqd(path))
        {
            return ReadSqd(File.ReadAllBytes(path), name);
        }

        var text = File.ReadAllText(path);
        return IsMyBatis(path) ? ReadMyBatis(text, name) : ReadMarkdown(text, name);
    }

    // ---- Orange .sqd ----------------------------------------------------------------------

    /// <summary>바이트로 읽는다: BOM 이 있으면 그 인코딩, 없으면 UTF-8 로 읽히는지 보고 아니면 CP949.</summary>
    public static WorkspaceFile ReadSqd(byte[] bytes, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return ReadSqd(FileEncoding.Decode(bytes).Text, fallbackName);
    }

    /// <summary>
    /// Orange 5.x 작업 공간(.sqd). <c>[WORKSPACE]</c> 구역에서 탭마다 <c>NAME=</c>, <c>STMT=줄 수</c>, 그 줄 수만큼 SQL,
    /// <c>VAR=개수</c>, 그 개수만큼 <c>:이름=타입,값</c>. SQL 은 줄 수로 잘라 읽으므로 본문 줄이 NAME= 처럼 보여도 키로 읽지 않는다.
    /// 깨진 곳은 읽은 만큼 열고 알림에 남긴다. 이름에 pass·pwd·secret·token 이 든 바인드는 값을 버린다.
    /// </summary>
    public static WorkspaceFile ReadSqd(string text, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inv = CultureInfo.InvariantCulture;
        var notes = new List<string>();
        var start = Array.FindIndex(lines, l => l.Trim().Equals("[WORKSPACE]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            throw new FormatException("Orange 작업 공간 파일이 아니다([WORKSPACE] 구역이 없다).");
        }

        var i = start + 1;
        int? count = null;
        if (i < lines.Length && lines[i].StartsWith("COUNT=", StringComparison.OrdinalIgnoreCase))
        {
            count = int.TryParse(lines[i][6..].Trim(), NumberStyles.None, inv, out var c) ? c : null;
            i++;
        }

        var tabs = new List<WorkspaceFileTab>();
        var unknownTypes = new SortedSet<string>(StringComparer.Ordinal);
        var skipped = false;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("NAME=", StringComparison.Ordinal))
            {
                if (lines[i].Trim().Length > 0)
                {
                    skipped = true;
                }

                i++;
                continue;
            }

            var title = lines[i][5..].Trim();
            if (title.Length == 0)
            {
                title = "SQL " + (tabs.Count + 1).ToString(inv);
            }

            i++;
            var label = $"탭 {(tabs.Count + 1).ToString(inv)}({title})";
            var body = new List<string>();
            if (i < lines.Length && lines[i].StartsWith("STMT=", StringComparison.Ordinal))
            {
                if (!int.TryParse(lines[i][5..].Trim(), NumberStyles.None, inv, out var stmt))
                {
                    notes.Add($"{label}: STMT 값을 읽지 못해 본문을 비웠다.");
                    stmt = 0;
                }

                i++;
                var take = Math.Min(stmt, lines.Length - i);
                for (var k = 0; k < take; k++)
                {
                    body.Add(lines[i + k]);
                }

                i += take;
                if (take < stmt)
                {
                    notes.Add($"{label}: 본문이 {stmt.ToString(inv)}줄이어야 하는데 {take.ToString(inv)}줄만 읽었다.");
                }
            }
            else
            {
                notes.Add($"{label}: STMT 줄이 없어 본문을 비웠다.");
            }

            var binds = new List<WorkspaceBind>();
            if (i < lines.Length && lines[i].StartsWith("VAR=", StringComparison.Ordinal))
            {
                int.TryParse(lines[i][4..].Trim(), NumberStyles.None, inv, out var vars);
                i++;
                var take = Math.Min(vars, lines.Length - i);
                for (var k = 0; k < take; k++)
                {
                    if (ParseSqdBind(lines[i + k], unknownTypes) is { } bind)
                    {
                        binds.Add(bind);
                    }
                    else
                    {
                        notes.Add($"{label}: 바인드 줄을 읽지 못했다.");
                    }
                }

                i += take;
                if (take < vars)
                {
                    notes.Add($"{label}: 바인드가 {vars.ToString(inv)}개여야 하는데 {take.ToString(inv)}개만 읽었다.");
                }
            }

            tabs.Add(new WorkspaceFileTab(title, string.Join('\n', body), null, binds.Count > 0 ? binds : null));
        }

        if (skipped)
        {
            notes.Add("탭 사이에 읽을 수 없는 줄이 있어 건너뛰었다.");
        }

        if (count is { } expected && expected != tabs.Count)
        {
            notes.Add($"COUNT 는 {expected.ToString(inv)}인데 실제 탭은 {tabs.Count.ToString(inv)}개다. 읽은 만큼 열었다.");
        }

        if (count is null)
        {
            notes.Add("COUNT 를 읽지 못했다. 읽은 만큼 열었다.");
        }

        if (unknownTypes.Count > 0)
        {
            notes.Add("모르는 바인드 타입 코드(" + string.Join(", ", unknownTypes) + ")는 문자열로 읽었다.");
        }

        if (tabs.Count == 0)
        {
            notes.Add("탭이 없다.");
        }

        return new WorkspaceFile(fallbackName, tabs, notes);
    }

    /// <summary><c>:이름=타입,값</c>. 값은 첫 쉼표 뒤 전부(비어 있을 수 있다).</summary>
    private static WorkspaceBind? ParseSqdBind(string line, SortedSet<string> unknownTypes)
    {
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        var name = (eq < 0 ? line : line[..eq]).Trim().TrimStart(':');
        if (eq < 0 || name.Length == 0)
        {
            return null;
        }

        var rest = line[(eq + 1)..];
        var comma = rest.IndexOf(',', StringComparison.Ordinal);
        var type = comma < 0 ? rest.Trim() : rest[..comma].Trim();
        var value = comma < 0 ? string.Empty : rest[(comma + 1)..];
        if (type != "0")
        {
            unknownTypes.Add(type);
        }

        if (SensitiveBindNames.IsSensitive(name))
        {
            value = string.Empty;
        }

        return new WorkspaceBind { Name = name, Type = BindDataType.Varchar2, Direction = BindDirection.In, Value = value, IsNull = value.Length == 0 };
    }

    // ---- Markdown -------------------------------------------------------------------------

    /// <summary>
    /// 탭마다 <c>## 탭 이름</c> 과 <c>```sql</c> 코드 블록. SQL 안에 백틱이 이어 나오면 울타리를 그보다 길게 만든다.
    /// </summary>
    public static string WriteMarkdown(string name, IEnumerable<WorkspaceFileTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(OneLine(name));
        sb.AppendLine();
        sb.AppendLine(Marker);
        foreach (var tab in tabs)
        {
            sb.AppendLine();
            sb.Append("## ").AppendLine(OneLine(tab.Title));
            sb.AppendLine();
            if (tab.Connection is { Length: > 0 } conn)
            {
                sb.Append("<!-- connection: ").Append(OneLine(conn)).AppendLine(" -->");
                sb.AppendLine();
            }

            var fence = new string('`', Math.Max(3, LongestRun(tab.Text, '`') + 1));
            sb.Append(fence).AppendLine("sql");
            sb.Append(tab.Text.TrimEnd('\r', '\n')).AppendLine();
            sb.AppendLine(fence);
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>## 제목</c> 아래 첫 코드 블록이 그 탭의 SQL 이다. 제목이 없는 코드 블록은 「SQL n」 탭이 되고,
    /// 코드 블록이 하나도 없으면 파일 전체를 탭 하나로 연다.
    /// </summary>
    public static WorkspaceFile ReadMarkdown(string text, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var name = fallbackName;
        var tabs = new List<WorkspaceFileTab>();
        string? title = null;
        string? connection = null;
        var tookBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("# ", StringComparison.Ordinal) && tabs.Count == 0 && title is null)
            {
                name = line[2..].Trim();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                title = line[3..].Trim();
                connection = null;
                tookBlock = false;
                continue;
            }

            var conn = Regex.Match(line, @"^<!--\s*connection:\s*(.+?)\s*-->\s*$");
            if (conn.Success)
            {
                connection = conn.Groups[1].Value;
                continue;
            }

            var open = Regex.Match(line, @"^(`{3,}|~{3,})\s*([A-Za-z/]*)\s*$");
            if (!open.Success)
            {
                continue;
            }

            var fence = open.Groups[1].Value;
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                var close = lines[j].TrimEnd();
                if (close.Length >= fence.Length && close.All(c => c == fence[0]))
                {
                    break;
                }

                body.Add(lines[j]);
            }

            i = j;
            if (tookBlock)
            {
                continue; // 한 제목 아래 두 번째 코드 블록은 설명용으로 보고 건너뛴다.
            }

            var tabTitle = title ?? "SQL " + (tabs.Count + 1).ToString(CultureInfo.InvariantCulture);
            tabs.Add(new WorkspaceFileTab(tabTitle, string.Join('\n', body), connection));
            tookBlock = title is not null;
        }

        if (tabs.Count == 0)
        {
            tabs.Add(new WorkspaceFileTab(fallbackName, text));
        }

        return new WorkspaceFile(name, tabs, []);
    }

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

    // ---- 공통 -----------------------------------------------------------------------------

    /// <summary>공통 들여쓰기를 걷어 낸다. 빈 줄은 연속이면 하나로, <paramref name="keepBlankLines"/> 가 거짓이면 모두 뺀다.</summary>
    private static string Dedent(string text, bool keepBlankLines = true)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var indent = lines.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        var result = new List<string>();
        foreach (var l in lines)
        {
            if (l.Length == 0 && (!keepBlankLines || (result.Count > 0 && result[^1].Length == 0)))
            {
                continue;
            }

            result.Add(l.Length >= indent ? l[indent..] : l.TrimStart());
        }

        return string.Join('\n', result);
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }
}
