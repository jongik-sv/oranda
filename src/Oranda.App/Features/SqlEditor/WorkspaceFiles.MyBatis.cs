using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.SqlEditor;

// WorkspaceFiles 의 일부: MyBatis 매퍼 읽기(TSK-01-02 에서 나눔, 동작 불변). 이 파일은 TSK-02-02 소유(docs/oranda1/design.md 7장·11장).
// Language 형 static 필드를 두지 않는다: KindOf·IsMyBatis 가 시작 복원 때 불리므로 그때 Oranda.Language 가 로드되면 안 된다.
public static partial class WorkspaceFiles
{
    // ---- MyBatis --------------------------------------------------------------------------

    /// <summary>
    /// MyBatis 매퍼의 select·insert·update·delete 를 탭으로 바꾼다. <c>#{x}</c> 는 바인드 변수 <c>:x</c>,
    /// <c>${x}</c> 는 치환 변수 <c>&amp;x</c> 가 된다. 동적 태그는 계산기(<see cref="MyBatisEvaluator"/>)의 「모두 켬」으로 펼친다:
    /// where·set·trim 은 앞뒤를 다듬고, if·foreach 는 내용을 남기고 조건을 주석으로 붙이며,
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
        return ExpandMapper(root, ns, fallbackName);
    }

    // 문장마다 계산기(TSK-02-02)의 「모두 켬 + 옛 표기」로 펼친다. Oranda.Language 형은 이 메서드 안에서만 쓴다(시작 복원 때 로드되지 않게).
    private static WorkspaceFile ExpandMapper(XElement root, string? ns, string fallbackName)
    {
        var fragments = new MapperFragments(root, ns);
        var evaluator = new MyBatisEvaluator();
        var options = new MyBatisEvalOptions(AllOn: true, LegacyNotation: true);
        var noValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        int dynamicTags = 0;
        var tabs = new List<WorkspaceFileTab>();
        foreach (var e in root.Elements())
        {
            var kind = e.Name.LocalName;
            if (kind is not ("select" or "insert" or "update" or "delete"))
            {
                continue;
            }

            var id = (string?)e.Attribute("id") ?? kind + (tabs.Count + 1).ToString(CultureInfo.InvariantCulture);
            var statement = new MyBatisElement
            {
                Kind = kind switch
                {
                    "select" => MyBatisElementKind.Select,
                    "insert" => MyBatisElementKind.Insert,
                    "update" => MyBatisElementKind.Update,
                    _ => MyBatisElementKind.Delete,
                },
                Name = kind,
                Id = id,
                Text = e.ToString(SaveOptions.DisableFormatting),
            };
            var result = evaluator.Evaluate(statement, fragments, noValues, null, options);
            // 옛 MyBatisText 처럼 실제로 펼친(켜진) 태그만 센다. 고르지 않은 when·otherwise 안은 세지 않는다.
            dynamicTags += result.Blocks.Count(b => b.IsOn && b.Tag is "if" or "where" or "set" or "trim" or "choose" or "foreach" or "bind");
            var body = Dedent(result.Sql, keepBlankLines: false);
            var header = $"-- MyBatis {(ns is null ? id : ns + "." + id)} <{kind}>";
            tabs.Add(new WorkspaceFileTab(id, header + "\n" + body));
        }

        var notes = new List<string>();
        if (tabs.Count == 0)
        {
            notes.Add("select·insert·update·delete 문장이 없다.");
        }

        if (dynamicTags > 0)
        {
            notes.Add($"동적 태그 {dynamicTags.ToString(CultureInfo.InvariantCulture)}개를 실행 가능한 모양으로 단순화했다(조건은 주석으로 남김).");
        }

        return new WorkspaceFile(ns?.Split('.').Last() ?? fallbackName, tabs, notes);
    }

    /// <summary>같은 매퍼의 sql 조각(include 대상). refid 앞의 자기 namespace 는 떼고 찾는다.</summary>
    private sealed class MapperFragments(XElement root, string? ns) : IMyBatisFragmentResolver
    {
        private readonly Dictionary<string, XElement> _fragments = root.Elements()
            .Where(e => e.Name.LocalName == "sql" && e.Attribute("id") is not null)
            .GroupBy(e => (string)e.Attribute("id")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            var id = ns is not null && refid.StartsWith(ns + ".", StringComparison.Ordinal) ? refid[(ns.Length + 1)..] : refid;
            return _fragments.TryGetValue(id, out var e) ? new MyBatisFragment(id, ns, e.ToString(SaveOptions.DisableFormatting)) : null;
        }
    }
}
