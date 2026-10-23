using System.Collections;
using System.Globalization;
using System.Text;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

// 계산 한 번의 상태: 바인딩(MyBatis ContextMap 의 자기 몫), 바인드 표식, 블록, 문제.
public sealed partial class MyBatisEvaluator
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "if", "when", "otherwise", "choose", "where", "set", "trim", "foreach", "bind",
    };

    private sealed class Run(IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<TextRange, bool>? overrides, bool allOn)
    {
        private readonly Dictionary<string, object?> _bindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _itemDisplay = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _iterations = new(StringComparer.Ordinal);
        private readonly Dictionary<Node, BlockState> _blocks = new(ReferenceEqualityComparer.Instance);
        private readonly List<BlockState> _blockOrder = [];
        private int _unique;

        public List<BindMark> Marks { get; } = [];

        public List<MyBatisSubstitution> Substitutions { get; } = [];

        public List<MyBatisEvalIssue> Issues { get; } = [];

        // ---- 블록 ----

        public void CollectBlocks(List<Node> body)
        {
            foreach (var node in body)
            {
                switch (node)
                {
                    case IncludeNode include:
                        Register(include, "include", null, isCondition: false);
                        CollectBlocks(include.Children);
                        break;
                    case ElementNode e:
                        if (BlockTags.Contains(e.Name))
                        {
                            bool condition = e.Origin.Fragment is null && e.Name is "if" or "when" or "foreach";
                            Register(e, e.Name, e.Name is "if" or "when" ? e.Attr("test") : null, condition);
                        }

                        CollectBlocks(e.Children);
                        break;
                }
            }
        }

        private void Register(Node node, string tag, string? test, bool isCondition)
        {
            var state = new BlockState { Tag = tag, Test = test, Origin = node.Origin, IsCondition = isCondition };
            _blocks[node] = state;
            _blockOrder.Add(state);
        }

        public List<MyBatisBlock> Blocks() =>
            _blockOrder.Select(b => new MyBatisBlock(
                b.Origin.StatementRange, b.Tag, b.Test, b.IsOn, b.IsOverridden, b.IsCondition, b.Origin.Fragment, b.Origin.FragmentRange)).ToList();

        private void TurnOn(Node node)
        {
            if (_blocks.TryGetValue(node, out var b))
            {
                b.IsOn = true;
            }
        }

        private bool TryOverride(Node node, out bool on)
        {
            on = false;
            if (overrides is null || !_blocks.TryGetValue(node, out var b) || !b.IsCondition
                || !overrides.TryGetValue(b.Origin.StatementRange, out on))
            {
                return false;
            }

            b.IsOverridden = true;
            return true;
        }

        // ---- 노드 적용(MyBatis SqlNode.apply) ----

        public void ApplyAll(List<Node> nodes, SqlContext ctx)
        {
            foreach (var node in nodes)
            {
                Apply(node, ctx);
            }
        }

        private bool Apply(Node node, SqlContext ctx)
        {
            switch (node)
            {
                case TextNode text:
                    ctx.Append(Render(text));
                    return true;
                case IncludeNode include:
                    if (include.Found)
                    {
                        TurnOn(include);
                        ApplyAll(include.Children, ctx);
                    }

                    return true;
            }

            var e = (ElementNode)node;
            switch (e.Name)
            {
                case "if":
                case "when":
                    if (!Decide(e))
                    {
                        return false;
                    }

                    TurnOn(e);
                    ApplyAll(e.Children, ctx);
                    return true;
                case "choose":
                    return Choose(e, ctx);
                case "where":
                    return ApplyTrim(e, ctx, "WHERE", WherePrefixes, null, []);
                case "set":
                    return ApplyTrim(e, ctx, "SET", SetOverrides, null, SetOverrides);
                case "trim":
                    return ApplyTrim(e, ctx, e.Attr("prefix"), Overrides(e.Attr("prefixOverrides")), e.Attr("suffix"), Overrides(e.Attr("suffixOverrides")));
                case "foreach":
                    return Foreach(e, ctx);
                case "bind":
                    Bind(e);
                    return true;
                case "selectKey":
                    return true;
                default:
                    Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, e.Origin, null, $"MyBatis 가 모르는 태그 <{e.Name}> 다(MyBatis 3.5 는 오류)."));
                    return false;
            }
        }

        private bool Decide(ElementNode e)
        {
            if (TryOverride(e, out bool on))
            {
                return on;
            }

            if (allOn)
            {
                return true;
            }

            var test = e.Attr("test");
            if (test is null)
            {
                Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, e.Origin, null, $"<{e.Name}> 에 test 가 없다."));
                return false;
            }

            var result = OgnlLite.Test(test, Lookup);
            if (result.Failure is { } f)
            {
                // 계산 못 한 식은 켬(사용자 결정 Q-B). 덮어쓰기로 끌 수 있다.
                Issues.Add(ExpressionIssue(e, "test", test, f));
                return true;
            }

            return result.Value is true;
        }

        private bool Choose(ElementNode e, SqlContext ctx)
        {
            ElementNode? otherwise = null;
            foreach (var child in e.Children.OfType<ElementNode>())
            {
                if (child.Name is "when" or "if")
                {
                    if (Decide(child))
                    {
                        TurnOn(child);
                        TurnOn(e);
                        ApplyAll(child.Children, ctx);
                        return true;
                    }
                }
                else if (child.Name == "otherwise")
                {
                    if (otherwise is not null)
                    {
                        Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, child.Origin, null, "choose 에 otherwise 가 둘 이상이다(MyBatis 3.5 는 오류)."));
                        continue;
                    }

                    otherwise = child;
                }
            }

            if (otherwise is null)
            {
                return false;
            }

            TurnOn(otherwise);
            TurnOn(e);
            ApplyAll(otherwise.Children, ctx);
            return true;
        }

        private bool ApplyTrim(ElementNode e, SqlContext ctx, string? prefix, IReadOnlyList<string> prefixOverrides, string? suffix, IReadOnlyList<string> suffixOverrides)
        {
            var trim = new TrimContext(ctx, prefix, prefixOverrides, suffix, suffixOverrides,
                reason => Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, e.Origin, null, reason)));
            ApplyAll(e.Children, trim);
            if (trim.ApplyAll())
            {
                TurnOn(e);
            }

            return true;
        }

        private static string[] Overrides(string? text) =>
            text is null ? [] : text.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToUpperInvariant()).ToArray();

        private bool Foreach(ElementNode e, SqlContext ctx)
        {
            var collection = e.Attr("collection");
            if (collection is null)
            {
                Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, e.Origin, null, "foreach 에 collection 이 없다."));
                return false;
            }

            var items = Items(e, collection, out bool numbered);
            if (items.Count == 0)
            {
                return true;
            }

            TurnOn(e);
            var item = e.Attr("item");
            var index = e.Attr("index");
            var open = e.Attr("open");
            var close = e.Attr("close");
            var separator = e.Attr("separator");
            var display = CollectionDisplay(collection);
            string? savedItemDisplay = item is not null && _itemDisplay.TryGetValue(item, out var d) ? d : null;
            if (open is not null)
            {
                ctx.Append(open);
            }

            bool first = true;
            foreach (var (key, value) in items)
            {
                var prefixed = new PrefixedContext(ctx, first || separator is null ? string.Empty : separator);
                int unique = _unique++;
                var itemDisplay = display + "[" + Convert.ToString(key, CultureInfo.InvariantCulture) + "]";
                if (index is not null)
                {
                    _bindings[index] = key;
                    _bindings[ItemPrefix + index + "_" + unique.ToString(CultureInfo.InvariantCulture)] = key;
                }

                if (item is not null)
                {
                    _bindings[item] = value;
                    _bindings[ItemPrefix + item + "_" + unique.ToString(CultureInfo.InvariantCulture)] = value;
                    _itemDisplay[item] = itemDisplay;
                }

                var frame = new ForeachFrame
                {
                    Item = item,
                    Index = index,
                    Unique = unique,
                    ItemNumber = numbered && item is not null ? NextIteration(item) : 0,
                    IndexNumber = numbered && index is not null ? NextIteration(index) : 0,
                    ItemDisplay = itemDisplay,
                };
                ApplyAll(e.Children, new ForeachContext(prefixed, this, frame));
                if (first)
                {
                    first = !prefixed.PrefixApplied;
                }
            }

            if (close is not null)
            {
                ctx.Append(close);
            }

            if (item is not null)
            {
                _bindings.Remove(item);
                if (savedItemDisplay is null)
                {
                    _itemDisplay.Remove(item);
                }
                else
                {
                    _itemDisplay[item] = savedItemDisplay;
                }
            }

            if (index is not null)
            {
                _bindings.Remove(index);
            }

            return true;
        }

        private int NextIteration(string name)
        {
            _iterations.TryGetValue(name, out int n);
            _iterations[name] = ++n;
            return n;
        }

        // foreach 가 돌 항목(색인·값). 덮어쓰기: 끔 = 빈 목록, 켬 = 값 그대로(없거나 비면 항목 하나). 모두 켬 = 번호 없는 항목 하나.
        private List<(object? Key, object? Value)> Items(ElementNode e, string collection, out bool numbered)
        {
            numbered = true;
            bool forced = TryOverride(e, out bool on);
            if (forced && !on)
            {
                return [];
            }

            if (!forced && allOn)
            {
                numbered = false;
                return [(0, null)];
            }

            var result = OgnlLite.Value(collection, Lookup);
            if (result.Failure is { } f)
            {
                Issues.Add(ExpressionIssue(e, "collection", collection, f));
                return [(0, null)];
            }

            var value = result.Value;
            if (value is null)
            {
                if (forced)
                {
                    return [(0, null)];
                }

                if (!string.Equals(e.Attr("nullable"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, e.Origin, collection,
                        $"foreach collection 「{collection}」 값이 null 이다(MyBatis 3.5 는 BuilderException). 빈 목록으로 계산했다."));
                }

                return [];
            }

            if (!IsNumberedList(value))
            {
                Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, e.Origin, collection,
                    $"foreach collection 「{collection}」 값이 목록이 아니다(MyBatis 3.5 는 BuilderException)."));
                return forced ? [(0, null)] : [];
            }

            var list = Iterate(value).Select(x => (x.Index, x.Item)).ToList();
            return forced && list.Count == 0 ? [(0, null)] : list;
        }

        private string CollectionDisplay(string collection)
        {
            var path = collection.Trim();
            int dot = path.IndexOf('.', StringComparison.Ordinal);
            var root = dot < 0 ? path : path[..dot];
            return _itemDisplay.TryGetValue(root, out var display) ? display + path[root.Length..] : path;
        }

        private void Bind(ElementNode e)
        {
            if (allOn)
            {
                return;
            }

            var name = e.Attr("name");
            var expression = e.Attr("value");
            if (name is null || expression is null)
            {
                Issues.Add(Issue(MyBatisEvalIssueKind.InvalidTag, e.Origin, null, "bind 에 name·value 가 없다."));
                return;
            }

            var result = OgnlLite.Value(expression, Lookup);
            if (result.Failure is { } f)
            {
                Issues.Add(ExpressionIssue(e, "value", expression, f));
            }

            _bindings[name] = result.Ok ? result.Value : null;
            TurnOn(e);
        }

        // ---- 글: ${} 는 지금 바인딩으로 바꾸고, #{} 는 표식으로 남긴다(MyBatis 는 다 펼친 뒤 #{} 를 ? 로 바꾼다) ----

        private string Render(TextNode text)
        {
            var sb = new StringBuilder();
            foreach (var piece in text.Pieces)
            {
                switch (piece)
                {
                    case LiteralPiece l:
                        sb.Append(l.Text);
                        break;
                    case DollarPiece d:
                        {
                            // MyBatis TextSqlNode: ${…} 안은 OGNL 식, null 이면 빈 글, 아니면 String.valueOf.
                            var expression = d.Content.Trim();
                            var result = OgnlLite.Value(expression, Lookup);
                            if (result.Failure is { } f)
                            {
                                Issues.Add(new MyBatisEvalIssue(MyBatisEvalIssueKind.Unevaluable, Source(text, d.Range), expression, f.Reason, text.Origin.Fragment, FragmentRange(text, d.Range)));
                            }

                            var str = result.Value is null ? null : OgnlLite.JavaString(result.Value);
                            sb.Append(str);
                            Substitutions.Add(new MyBatisSubstitution(DisplayOf(expression), str, Source(text, d.Range), text.Origin.Fragment, FragmentRange(text, d.Range)));
                            break;
                        }

                    case HashPiece h:
                        {
                            var (property, jdbcType, _) = ParseHash(h.Content);
                            var mark = new BindMark
                            {
                                Property = property,
                                Display = DisplayOf(property),
                                NameBase = SanitizeName(property),
                                JdbcType = jdbcType,
                                Origin = new Origin(h.Range, text.Origin.Fragment, Source(text, h.Range)),
                            };
                            sb.Append(MarkOpen).Append(Marks.Count.ToString(CultureInfo.InvariantCulture)).Append(MarkClose);
                            Marks.Add(mark);
                            break;
                        }
                }
            }

            return sb.ToString();
        }

        private string DisplayOf(string path)
        {
            int dot = path.IndexOf('.', StringComparison.Ordinal);
            var root = dot < 0 ? path : path[..dot];
            return _itemDisplay.TryGetValue(root, out var display) ? display + path[root.Length..] : path;
        }

        // 문장 원문이면 #{}·${} 자리, 조각 안이면 바깥 include 태그.
        private static TextRange Source(TextNode text, TextRange range) => text.Origin.Fragment is null ? range : text.Origin.StatementRange;

        private static TextRange? FragmentRange(TextNode text, TextRange range) => text.Origin.Fragment is null ? null : range;

        // ---- 값 찾기(MyBatis ContextMap: 자기 바인딩 → 파라미터 사전) ----

        private bool Lookup(string name, out object? value)
        {
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            var root = dot < 0 ? name : name[..dot];
            if (_bindings.TryGetValue(root, out var bound))
            {
                // 바인딩 이름으로 시작하는 점 경로는 바인딩 값에서 따라간다(평평한 열쇠보다 먼저).
                value = dot < 0 ? bound : null;
                return dot < 0;
            }

            if (values.TryGetValue(name, out value))
            {
                return true;
            }

            switch (name)
            {
                case "_parameter":
                    value = values;
                    return true;
                case "_databaseId":
                    value = null;
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        // ---- 마무리: 표식 → :이름, 값은 마지막 바인딩에서(DefaultParameterHandler) ----

        public (string Sql, IReadOnlyList<MyBatisBind> Binds) Finish(string sql)
        {
            var binds = new List<MyBatisBind>();
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(sql.Length);
            int i = 0;
            while (i < sql.Length)
            {
                int at = sql.IndexOf(MarkOpen, i);
                if (at < 0)
                {
                    sb.Append(sql, i, sql.Length - i);
                    break;
                }

                sb.Append(sql, i, at - i);
                if (!TryReadMark(sql, at, out var mark, out int end))
                {
                    // 표식처럼 보이지만 아닌 글(원문의 U+E000): 그대로 둔다.
                    sb.Append(sql[at]);
                    i = at + 1;
                    continue;
                }

                var name = UniqueName(owners, mark.NameBase, mark.Display);
                var found = OgnlLite.ParameterValue(mark.Property, Lookup);
                if (found.Failure is { } f)
                {
                    var issue = new MyBatisEvalIssue(MyBatisEvalIssueKind.Unevaluable, mark.Origin.StatementRange, mark.Property, f.Reason, mark.Origin.Fragment, mark.Origin.FragmentRange);
                    if (!Issues.Contains(issue))
                    {
                        Issues.Add(issue);
                    }
                }

                var value = found.Value;
                binds.Add(new MyBatisBind(name, mark.Display, mark.JdbcType, value, mark.Origin.StatementRange, mark.Origin.Fragment, mark.Origin.FragmentRange));
                sb.Append(':').Append(name);
                i = end;
            }

            return (sb.ToString(), binds);
        }

        /// <summary><paramref name="at"/> 의 표식(U+E000 번호 U+E001)을 읽는다. 표식이 아니면 거짓, <paramref name="end"/> = 다음에 볼 자리.</summary>
        public bool TryReadMark(string sql, int at, out BindMark mark, out int end)
        {
            mark = null!;
            int close = sql.IndexOf(MarkClose, at + 1);
            end = at + 1;
            if (close < 0 || !int.TryParse(sql.AsSpan(at + 1, close - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index >= Marks.Count)
            {
                return false;
            }

            mark = Marks[index];
            end = close + 1;
            return true;
        }

        // 같은 파라미터면 같은 이름, 다른 파라미터가 같은 이름을 원하면 뒤에 _2… (Oracle 바인드 이름은 대소문자를 가리지 않는다).
        private static string UniqueName(Dictionary<string, string> owners, string baseName, string parameter)
        {
            var name = baseName;
            for (int n = 2; ; n++)
            {
                if (!owners.TryGetValue(name, out var owner))
                {
                    owners[name] = parameter;
                    return name;
                }

                if (string.Equals(owner, parameter, StringComparison.Ordinal))
                {
                    return name;
                }

                name = baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static MyBatisEvalIssue ExpressionIssue(ElementNode e, string attribute, string expression, OgnlFailure failure)
        {
            var valueRange = e.AttrRange(attribute) ?? e.Origin.Range;
            var range = MapAttributeRange(valueRange, expression, failure.Range);
            return e.Origin.Fragment is null
                ? new MyBatisEvalIssue(MyBatisEvalIssueKind.Unevaluable, range, expression, failure.Reason)
                : new MyBatisEvalIssue(MyBatisEvalIssueKind.Unevaluable, e.Origin.StatementRange, expression, failure.Reason, e.Origin.Fragment, range);
        }
    }
}
