using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

// ExtractParameters(MB-04): 값과 상관없이 문장(펼친 include 포함)을 훑어 파라미터 이름·쓰인 곳·타입 힌트를 처음 나온 순서로 모은다.
// foreach item·index 와 앞에서 선언한 bind name 은 문장 안에서 생긴 이름이라 넣지 않는다.
public sealed partial class MyBatisEvaluator
{
    private static class ParameterExtractor
    {
        public static List<MyBatisParameter> Extract(List<Node> body)
        {
            var found = new List<Entry>();
            var byName = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var locals = new List<string>();
            var binds = new HashSet<string>(StringComparer.Ordinal);
            Walk(body, found, byName, locals, binds);
            return found.Select(e => new MyBatisParameter(e.Name, e.Usage, e.JdbcType, e.JavaType, e.Locations)).ToList();
        }

        private sealed class Entry(string name)
        {
            public string Name { get; } = name;

            public MyBatisParameterUsage Usage { get; set; }

            public string? JdbcType { get; set; }

            public string? JavaType { get; set; }

            public List<TextRange> Locations { get; } = [];
        }

        private static void Walk(List<Node> nodes, List<Entry> found, Dictionary<string, Entry> byName, List<string> locals, HashSet<string> binds)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case TextNode text:
                        foreach (var piece in text.Pieces)
                        {
                            var at = text.Origin.Fragment is null ? piece.Range : text.Origin.StatementRange;
                            if (piece is HashPiece h)
                            {
                                var (property, jdbc, java) = ParseHash(h.Content);
                                Add(found, byName, locals, binds, property, MyBatisParameterUsage.Bind, at, jdbc, java);
                            }
                            else if (piece is DollarPiece d)
                            {
                                // ${…} 안은 OGNL 식이다(${sort == null ? 'EMPNO' : sort}). 식에서 쓴 이름만 뽑는다.
                                foreach (var reference in OgnlLite.References(d.Content))
                                {
                                    Add(found, byName, locals, binds, reference.Path, MyBatisParameterUsage.Substitution, at, null, null);
                                }
                            }
                        }

                        break;
                    case IncludeNode include:
                        Walk(include.Children, found, byName, locals, binds);
                        break;
                    case ElementNode e:
                        WalkElement(e, found, byName, locals, binds);
                        break;
                }
            }
        }

        private static void WalkElement(ElementNode e, List<Entry> found, Dictionary<string, Entry> byName, List<string> locals, HashSet<string> binds)
        {
            switch (e.Name)
            {
                case "if" or "when":
                    AddExpression(e, "test", MyBatisParameterUsage.Test, found, byName, locals, binds);
                    break;
                case "foreach":
                    {
                        AddExpression(e, "collection", MyBatisParameterUsage.Foreach, found, byName, locals, binds);
                        int mark = locals.Count;
                        if (e.Attr("item") is { } item)
                        {
                            locals.Add(item);
                        }

                        if (e.Attr("index") is { } index)
                        {
                            locals.Add(index);
                        }

                        Walk(e.Children, found, byName, locals, binds);
                        locals.RemoveRange(mark, locals.Count - mark);
                        return;
                    }

                case "bind":
                    AddExpression(e, "value", MyBatisParameterUsage.BindTag, found, byName, locals, binds);
                    if (e.Attr("name") is { } name)
                    {
                        binds.Add(name);
                    }

                    return;
                case "selectKey":
                    return;
            }

            Walk(e.Children, found, byName, locals, binds);
        }

        private static void AddExpression(
            ElementNode e, string attribute, MyBatisParameterUsage usage, List<Entry> found, Dictionary<string, Entry> byName, List<string> locals, HashSet<string> binds)
        {
            var expression = e.Attr(attribute);
            if (expression is null)
            {
                return;
            }

            var valueRange = e.AttrRange(attribute) ?? e.Origin.Range;
            foreach (var reference in OgnlLite.References(expression))
            {
                var at = e.Origin.Fragment is null ? MapAttributeRange(valueRange, expression, reference.Range) : e.Origin.StatementRange;
                Add(found, byName, locals, binds, reference.Path, usage, at, null, null);
            }
        }

        // 사용자가 값을 넣을 이름: _parameter. 접두와 [색인] 을 떼고, 끝의 OGNL 특수 속성(list.size·m.isEmpty)도 뗀다.
        private static string ParameterPath(string path)
        {
            if (path.StartsWith("_parameter.", StringComparison.Ordinal))
            {
                path = path["_parameter.".Length..];
            }

            int bracket = path.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                path = path[..bracket];
            }

            int dot = path.LastIndexOf('.');
            return dot > 0 && path[(dot + 1)..] is "size" or "isEmpty" or "empty" or "length" or "keys" or "keySet" or "values" ? path[..dot] : path;
        }

        private static void Add(
            List<Entry> found,
            Dictionary<string, Entry> byName,
            List<string> locals,
            HashSet<string> binds,
            string path,
            MyBatisParameterUsage usage,
            TextRange at,
            string? jdbcType,
            string? javaType)
        {
            path = ParameterPath(path);
            if (path.Length == 0)
            {
                return;
            }

            int cut = path.IndexOfAny(['.', '[']);
            var root = cut < 0 ? path : path[..cut];
            if (root is "_parameter" or "_databaseId" || locals.Contains(root) || binds.Contains(root))
            {
                return;
            }

            if (!byName.TryGetValue(path, out var entry))
            {
                entry = new Entry(path);
                byName[path] = entry;
                found.Add(entry);
            }

            entry.Usage |= usage;
            entry.JdbcType ??= jdbcType;
            entry.JavaType ??= javaType;
            if (!entry.Locations.Contains(at))
            {
                entry.Locations.Add(at);
            }
        }
    }
}
