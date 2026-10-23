using System.Text.RegularExpressions;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.Language.Tests.MyBatis;

/// <summary>
/// 시험용 매퍼 읽기: 매퍼 원문에서 최상위 문장·sql 조각의 원문을 그대로 잘라 낸다(02-01 파서와 무관하게 계산기만 시험한다).
/// 최상위 요소는 같은 이름으로 겹치지 않으므로 여는 태그 ~ 같은 이름의 첫 닫는 태그로 자른다.
/// </summary>
internal sealed partial class GoldenMapper : IMyBatisFragmentResolver
{
    private readonly Dictionary<string, MyBatisFragment> _fragments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MyBatisElement> _statements = new(StringComparer.Ordinal);

    public GoldenMapper(string xml)
    {
        Namespace = NamespaceRegex().Match(xml) is { Success: true } ns ? ns.Groups[1].Value : null;
        foreach (Match m in ElementRegex().Matches(xml))
        {
            var name = m.Groups["name"].Value;
            var id = m.Groups["id"].Value;
            var text = m.Value;
            if (name == "sql")
            {
                _fragments[id] = new MyBatisFragment(id, Namespace, text);
                continue;
            }

            var kind = name switch
            {
                "select" => MyBatisElementKind.Select,
                "insert" => MyBatisElementKind.Insert,
                "update" => MyBatisElementKind.Update,
                _ => MyBatisElementKind.Delete,
            };
            _statements.TryAdd(id, new MyBatisElement { Kind = kind, Name = name, Id = id, Text = text });
        }
    }

    public string? Namespace { get; }

    public MyBatisElement this[string id] => _statements[id];

    public static GoldenMapper Of(string xml) => new(xml);

    /// <summary>조각 하나를 바꿔 끼운다(순환·없음 시험용).</summary>
    public GoldenMapper WithFragment(string id, string text)
    {
        _fragments[id] = new MyBatisFragment(id, Namespace, text);
        return this;
    }

    public MyBatisFragment? Resolve(string refid, string? currentNamespace)
    {
        var id = Namespace is not null && refid.StartsWith(Namespace + ".", StringComparison.Ordinal) ? refid[(Namespace.Length + 1)..] : refid;
        return _fragments.GetValueOrDefault(id);
    }

    [GeneratedRegex("<mapper\\b[^>]*\\bnamespace=\"([^\"]*)\"")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex("""<(?<name>select|insert|update|delete|sql)\b[^>]*\bid="(?<id>[^"]*)"[^>]*?(/>|>.*?</\k<name>>)""", RegexOptions.Singleline)]
    private static partial Regex ElementRegex();
}
