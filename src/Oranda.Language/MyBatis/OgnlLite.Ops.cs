using System.Collections;
using System.Globalization;
using System.Text;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

// 구문 트리 노드와 값 연산(참 판정·같음·크기·더하기·Java equals). 규칙은 MyBatis 3.5.19(OgnlOps) 실측과 같다.
internal static partial class OgnlLite
{
    private enum CompareOp { Eq, Ne, Lt, Le, Gt, Ge }

    private abstract class Node(int start, int end)
    {
        public int Start { get; } = start;

        public int End { get; } = end;

        public TextRange Range => new(Start, End - Start);

        public abstract object? Eval(OgnlLookup lookup);
    }

    private sealed class LiteralNode(object? value, int start, int end) : Node(start, end)
    {
        public override object? Eval(OgnlLookup lookup) => value;
    }

    private sealed class GroupNode(Node inner, int start, int end) : Node(start, end)
    {
        public override object? Eval(OgnlLookup lookup) => inner.Eval(lookup);
    }

    private sealed class PathNode(string path, int start, int end) : Node(start, end)
    {
        public override object? Eval(OgnlLookup lookup) => ResolvePath(path, lookup, Range);
    }

    private sealed class PropertyNode(Node target, string name, int end) : Node(target.Start, end)
    {
        public override object? Eval(OgnlLookup lookup) => Step(target.Eval(lookup), name, Range);
    }

    private sealed class NotNode(int start, Node operand) : Node(start, operand.End)
    {
        public override object? Eval(OgnlLookup lookup) => !OgnlBool(operand.Eval(lookup));
    }

    private sealed class NegNode(int start, Node operand) : Node(start, operand.End)
    {
        public override object? Eval(OgnlLookup lookup) => Negate(operand.Eval(lookup), Range);
    }

    // OGNL 의 and·or 는 마지막으로 계산한 피연산자 값을 그대로 돌려준다(참 판정은 바깥에서).
    private sealed class AndNode(Node left, Node right) : Node(left.Start, right.End)
    {
        public override object? Eval(OgnlLookup lookup)
        {
            var v = left.Eval(lookup);
            return OgnlBool(v) ? right.Eval(lookup) : v;
        }
    }

    private sealed class OrNode(Node left, Node right) : Node(left.Start, right.End)
    {
        public override object? Eval(OgnlLookup lookup)
        {
            var v = left.Eval(lookup);
            return OgnlBool(v) ? v : right.Eval(lookup);
        }
    }

    private sealed class CompareNode(CompareOp op, Node left, Node right) : Node(left.Start, right.End)
    {
        public override object? Eval(OgnlLookup lookup)
        {
            var a = left.Eval(lookup);
            var b = right.Eval(lookup);
            return op switch
            {
                CompareOp.Eq => Equal(a, b, Range),
                CompareOp.Ne => !Equal(a, b, Range),
                CompareOp.Lt => CompareWithConversion(a, b, Range) < 0,
                CompareOp.Le => CompareWithConversion(a, b, Range) <= 0,
                CompareOp.Gt => CompareWithConversion(a, b, Range) > 0,
                _ => CompareWithConversion(a, b, Range) >= 0,
            };
        }
    }

    private sealed class AddNode(Node left, Node right) : Node(left.Start, right.End)
    {
        public override object? Eval(OgnlLookup lookup) => Add(left.Eval(lookup), right.Eval(lookup), Range);
    }

    private sealed class MethodNode(Node target, string name, List<Node> args, int end) : Node(target.Start, end)
    {
        public override object? Eval(OgnlLookup lookup)
        {
            var t = target.Eval(lookup);
            if (t is null)
            {
                throw Err($"MyBatis 3.5 도 실행 때 예외(NullPointerException): 값이 null 이라 {name}() 를 부를 수 없다", Range);
            }

            switch (name)
            {
                case "size" when IsDict(t) || IsList(t):
                    return CountOf(t);
                case "isEmpty" when t is string s:
                    return s.Length == 0;
                case "isEmpty" when IsDict(t) || IsList(t):
                    return CountOf(t) == 0;
                case "length" when t is string s:
                    return s.Length;
                case "trim" when t is string s:
                    return JavaTrim(s);
                case "equals":
                    return JavaEquals(t, args[0].Eval(lookup));
                default:
                    throw Err($"{TypeName(t)} 값에는 {name}() 를 쓸 수 없다(부분집합 밖)", Range);
            }
        }
    }

    private static OgnlEvalException Err(string reason, TextRange range) => new(new OgnlFailure(range, reason));

    private static string TypeName(object v) => v switch
    {
        string => "글자",
        char => "char",
        bool => "bool",
        _ when IsJavaNumber(v) => "수",
        _ when IsDict(v) => "사전",
        _ when IsList(v) => "목록",
        _ => v.GetType().Name,
    };

    // ---- 값 찾기 ----

    private static bool IsDict(object? v) => v is IReadOnlyDictionary<string, object?> or IDictionary<string, object?> or IDictionary;

    private static bool IsList(object? v) => v is IEnumerable and not string && !IsDict(v);

    private static bool TryDictGet(object d, string key, out object? value)
    {
        switch (d)
        {
            case IReadOnlyDictionary<string, object?> r:
                return r.TryGetValue(key, out value);
            case IDictionary<string, object?> g:
                return g.TryGetValue(key, out value);
            case IDictionary n when n.Contains(key):
                value = n[key];
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static int CountOf(object v)
    {
        switch (v)
        {
            case IReadOnlyDictionary<string, object?> r:
                return r.Count;
            case ICollection c:
                return c.Count;
            case IDictionary<string, object?> g:
                return g.Count;
            default:
                int count = 0;
                foreach (var unused in (IEnumerable)v)
                {
                    count++;
                }

                return count;
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> Entries(object d)
    {
        switch (d)
        {
            case IReadOnlyDictionary<string, object?> r:
                return r;
            case IDictionary<string, object?> g:
                return g;
            default:
                return NonGenericEntries((IDictionary)d);
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> NonGenericEntries(IDictionary d)
    {
        foreach (DictionaryEntry e in d)
        {
            yield return new KeyValuePair<string, object?>(Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? string.Empty, e.Value);
        }
    }

    /// <summary>
    /// OGNL 속성 접근자로 값 하나에서 이름 하나를 따라간다(MyBatis 3.5.19 실측, Ognl.golden.json). null 은 null.
    /// 사전: size·isEmpty·keys·keySet·values 가 열쇠보다 먼저, 그 밖은 열쇠(없으면 null). 목록: size·isEmpty·empty.
    /// 글자: length·empty·isEmpty·trim. 그 밖의 속성(Java 빈 속성)은 계산하지 않는다.
    /// </summary>
    private static object? Step(object? v, string name, TextRange at)
    {
        if (v is null)
        {
            return null;
        }

        if (IsDict(v))
        {
            return name switch
            {
                "size" => CountOf(v),
                "isEmpty" => CountOf(v) == 0,
                "keys" or "keySet" => Entries(v).Select(e => (object?)e.Key).ToList(),
                "values" => Entries(v).Select(e => e.Value).ToList(),
                _ => TryDictGet(v, name, out var found) ? found : null,
            };
        }

        if (IsList(v))
        {
            return name switch
            {
                "size" => CountOf(v),
                "isEmpty" or "empty" => CountOf(v) == 0,
                _ => throw Err($"목록 값의 속성 '{name}' 은 계산하지 않는다(MyBatis 3.5 는 Java 속성이 없으면 NoSuchPropertyException)", at),
            };
        }

        if (v is string s)
        {
            return name switch
            {
                "length" => s.Length,
                "empty" or "isEmpty" => s.Length == 0,
                "trim" => JavaTrim(s),
                _ => throw Err($"글자 값의 속성 '{name}' 은 계산하지 않는다(MyBatis 3.5 는 Java 속성이 없으면 NoSuchPropertyException)", at),
            };
        }

        throw Err($"{TypeName(v)} 값의 속성 '{name}' 은 계산하지 않는다(MyBatis 3.5 는 Java 속성이 없으면 NoSuchPropertyException)", at);
    }

    /// <summary>Java <c>String.trim()</c>: 앞뒤의 U+0020 이하 글자를 뗀다.</summary>
    private static string JavaTrim(string s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && s[start] <= ' ')
        {
            start++;
        }

        while (end > start && s[end - 1] <= ' ')
        {
            end--;
        }

        return s[start..end];
    }
}
