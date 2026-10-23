using System.Collections;
using System.Globalization;
using System.Text;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>이름 하나(평평한 열쇠, 점이 들어 있을 수 있다)의 값을 찾는다. 없으면 거짓.</summary>
internal delegate bool OgnlLookup(string name, out object? value);

/// <summary>식 계산 실패. <paramref name="Range"/> 는 식 글자 안 범위(물결 밑줄 자리), <paramref name="Reason"/> 은 사람이 읽는 이유.</summary>
internal sealed record OgnlFailure(TextRange Range, string Reason);

/// <summary>식 계산 결과. <see cref="Failure"/> 가 있으면 <see cref="Value"/> 는 의미가 없다.</summary>
internal readonly record struct OgnlValue(object? Value, OgnlFailure? Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>식에서 쓴 이름(점 경로) 하나와 식 글자 안 범위.</summary>
internal readonly record struct OgnlReference(string Path, TextRange Range);

/// <summary>
/// MyBatis test·bind 식의 OGNL 부분집합 계산기(MB-02, TRD 3.2). 반사·동적 코드 생성·임의 메서드 호출을 쓰지 않는다.
/// 지원: null·true·false, 숫자·글자 리터럴(<c>'Y'</c> 한 글자는 char), <c>== != &lt; &lt;= &gt; &gt;=</c> 와 eq·neq·lt·lte·gt·gte,
/// and·or·&amp;&amp;·||·not·!, 괄호, 점 경로, size()·isEmpty()·length()·trim()·equals(). 비교·참 판정은 MyBatis 3.5.19(OgnlOps) 실측과 같다
/// (tests/Oranda.Language.Tests/MyBatis/Golden/Ognl.golden.json). 그 밖의 식과 MyBatis 도 실행 때 예외를 내는 식은 <see cref="OgnlFailure"/> 로 돌려준다.
/// </summary>
internal static partial class OgnlLite
{
    /// <summary>if·when test 식을 참·거짓으로 계산한다(MyBatis ExpressionEvaluator.evaluateBoolean 규칙).</summary>
    public static OgnlValue Test(string expression, OgnlLookup lookup)
    {
        var r = Evaluate(expression, lookup, allowPlus: false);
        if (!r.Ok)
        {
            return r;
        }

        try
        {
            return new OgnlValue(MyBatisBool(r.Value, new TextRange(0, expression.Length)), null);
        }
        catch (OgnlEvalException e)
        {
            return new OgnlValue(null, e.Failure);
        }
    }

    /// <summary>bind value 식을 계산한다. test 식 문법에 <c>+</c>(글자 잇기·수 더하기)만 더 받는다.</summary>
    public static OgnlValue Value(string expression, OgnlLookup lookup) => Evaluate(expression, lookup, allowPlus: true);

    /// <summary>
    /// OGNL 점 경로 값(test·bind·foreach collection·${} 의 이름). 평평한 열쇠(<c>"user.name"</c>)가 먼저, 없으면 가장 긴 앞부분 열쇠에서
    /// OGNL 속성 접근자 규칙으로 따라간다(사전의 size·isEmpty·keys·values 는 열쇠보다 먼저, 목록의 size·isEmpty, 글자의 length·empty). 없는 열쇠 = null.
    /// </summary>
    public static OgnlValue Resolve(string path, OgnlLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(lookup);
        try
        {
            return new OgnlValue(ResolvePath(path, lookup, new TextRange(0, path.Length)), null);
        }
        catch (OgnlEvalException e)
        {
            return new OgnlValue(null, e.Failure);
        }
    }

    private static object? ResolvePath(string path, OgnlLookup lookup, TextRange at)
    {
        if (lookup(path, out var whole))
        {
            return whole;
        }

        int dot = path.LastIndexOf('.');
        while (dot > 0)
        {
            if (lookup(path[..dot], out var head))
            {
                object? cur = head;
                foreach (var seg in path[(dot + 1)..].Split('.'))
                {
                    cur = Step(cur, seg, at);
                    if (cur is null)
                    {
                        break;
                    }
                }

                return cur;
            }

            dot = path.LastIndexOf('.', dot - 1);
        }

        return null;
    }

    /// <summary>
    /// <c>#{…}</c> 바인드 값(MyBatis DefaultParameterHandler 의 MetaObject 규칙). 평평한 열쇠가 먼저, 사전은 열쇠로만(size 도 열쇠),
    /// <c>[n]</c> 은 목록 색인·사전 열쇠. null 을 지나면 null. 목록·글자·수의 속성은 MyBatis 도 오류라 실패로 돌려준다.
    /// </summary>
    public static OgnlValue ParameterValue(string property, OgnlLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(lookup);
        var at = new TextRange(0, property.Length);
        try
        {
            if (lookup(property, out var whole))
            {
                return new OgnlValue(whole, null);
            }

            var segments = SplitProperty(property);
            for (int k = segments.Count; k >= 1; k--)
            {
                var head = string.Join('.', segments.Take(k));
                if (lookup(head, out var found))
                {
                    return new OgnlValue(WalkMeta(found, segments.Skip(k), at), null);
                }

                var (name, index) = SplitIndex(segments[k - 1]);
                if (index is not null)
                {
                    var bare = string.Join('.', segments.Take(k - 1).Append(name));
                    if (lookup(bare, out var collection))
                    {
                        return new OgnlValue(WalkMeta(MetaIndex(collection, index, at), segments.Skip(k), at), null);
                    }
                }
            }

            return new OgnlValue(null, null);
        }
        catch (OgnlEvalException e)
        {
            return new OgnlValue(null, e.Failure);
        }
    }

    // PropertyTokenizer 처럼 [ ] 안의 점은 자르지 않는다.
    private static List<string> SplitProperty(string property)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < property.Length; i++)
        {
            if (property[i] == '[')
            {
                depth++;
            }
            else if (property[i] == ']')
            {
                depth--;
            }
            else if (property[i] == '.' && depth == 0)
            {
                result.Add(property[start..i]);
                start = i + 1;
            }
        }

        result.Add(property[start..]);
        return result;
    }

    private static (string Name, string? Index) SplitIndex(string segment)
    {
        int open = segment.IndexOf('[', StringComparison.Ordinal);
        return open < 0 || !segment.EndsWith(']') ? (segment, null) : (segment[..open], segment[(open + 1)..^1]);
    }

    private static object? WalkMeta(object? cur, IEnumerable<string> segments, TextRange at)
    {
        foreach (var segment in segments)
        {
            if (cur is null)
            {
                return null;
            }

            var (name, index) = SplitIndex(segment);
            if (name.Length > 0)
            {
                if (!IsDict(cur))
                {
                    throw Err($"MyBatis 3.5 도 실행 때 예외(ReflectionException): {TypeName(cur)} 값에서 속성 '{name}' 을 읽을 수 없다", at);
                }

                cur = TryDictGet(cur, name, out var v) ? v : null;
            }

            if (index is not null)
            {
                cur = MetaIndex(cur, index, at);
            }
        }

        return cur;
    }

    // MapWrapper·BaseWrapper.getCollectionValue: 사전은 열쇠, 목록은 int 색인.
    private static object? MetaIndex(object? collection, string index, TextRange at)
    {
        if (collection is null)
        {
            return null;
        }

        if (IsDict(collection))
        {
            return TryDictGet(collection, index, out var v) ? v : null;
        }

        if (IsList(collection))
        {
            if (!int.TryParse(index, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i))
            {
                throw Err($"MyBatis 3.5 도 실행 때 예외(NumberFormatException): 색인 '{index}'", at);
            }

            int n = 0;
            foreach (var item in (IEnumerable)collection)
            {
                if (n++ == i)
                {
                    return item;
                }
            }

            throw Err($"MyBatis 3.5 도 실행 때 예외(IndexOutOfBoundsException): 색인 {index}, 길이 {n}", at);
        }

        throw Err($"MyBatis 3.5 도 실행 때 예외(ReflectionException): {TypeName(collection)} 값은 목록·배열이 아니라 [{index}] 를 쓸 수 없다", at);
    }

    /// <summary>식에서 쓴 이름(점 경로, 메서드 이름 제외)과 범위를 처음 나온 순서로. 계산 못 하는 식이어도 알아본 만큼 돌려준다.</summary>
    public static IReadOnlyList<OgnlReference> References(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var t = Tokenize(expression);
        var result = new List<OgnlReference>();
        int n = t.Count - 1; // 마지막은 End
        int i = 0;
        while (i < n)
        {
            var tok = t[i];
            if (tok.Kind != TokenKind.Ident)
            {
                i++;
                continue;
            }

            if (IsReferenceKeyword(tok.Text))
            {
                i = tok.Text is "instanceof" or "new" ? SkipDotted(t, i + 1, n) : i + 1;
                continue;
            }

            var prev = i > 0 ? t[i - 1] : default;
            if (prev.Kind == TokenKind.Op && prev.Text == "#")
            {
                i++; // #this, #변수
                continue;
            }

            if (prev.Kind == TokenKind.Op && prev.Text == "@")
            {
                i = SkipDotted(t, i, n); // @클래스@메서드
                if (i < n && t[i].Kind == TokenKind.Op && t[i].Text == "@")
                {
                    i = SkipDotted(t, i + 1, n);
                }

                continue;
            }

            if (prev.Kind == TokenKind.Op && prev.Text == ".")
            {
                i++; // 값의 뒤붙음(메서드 호출 뒤)
                continue;
            }

            int last = i;
            while (last + 2 < n && IsOp(t[last + 1], ".") && t[last + 2].Kind == TokenKind.Ident
                && !(last + 3 < n && IsOp(t[last + 3], "(")))
            {
                last += 2;
            }

            string path;
            if (last == i)
            {
                path = tok.Text;
            }
            else
            {
                var sb = new StringBuilder(tok.Text);
                for (int k = i + 2; k <= last; k += 2)
                {
                    sb.Append('.').Append(t[k].Text);
                }

                path = sb.ToString();
            }

            result.Add(new OgnlReference(path, new TextRange(tok.Start, t[last].Start + t[last].Length - tok.Start)));
            i = last + 1;
        }

        return result;
    }

    private static int SkipDotted(List<Token> t, int i, int n)
    {
        while (i < n && t[i].Kind == TokenKind.Ident)
        {
            i++;
            if (i + 1 < n && IsOp(t[i], ".") && t[i + 1].Kind == TokenKind.Ident)
            {
                i++;
                continue;
            }

            break;
        }

        return i;
    }

    private static bool IsReferenceKeyword(string s) =>
        s is "and" or "or" or "not" or "eq" or "neq" or "lt" or "lte" or "gt" or "gte" or "null" or "true" or "false" or "in" or "instanceof" or "new";

    private static OgnlValue Evaluate(string expression, OgnlLookup lookup, bool allowPlus)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var parsed = Parse(expression ?? string.Empty, allowPlus);
        if (parsed.Failure is not null)
        {
            return new OgnlValue(null, parsed.Failure);
        }

        try
        {
            return new OgnlValue(parsed.Root!.Eval(lookup), null);
        }
        catch (OgnlEvalException e)
        {
            return new OgnlValue(null, e.Failure);
        }
    }
}
