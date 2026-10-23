using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.App.Features.MyBatisLog;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;
using Oranda.Core.Security;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.LogPaste;

/// <summary>
/// 로그의 Parameters 값을 문장의 파라미터에 맞춘다(MB-09, K2). 조건 켬·끔 조합과 foreach 항목 수를 바꿔 가며 펼친 SQL 을 만들고,
/// <c>:이름</c> 을 <c>?</c> 로 되돌린 모양이 로그의 Preparing 문장과 같아지는 첫 경우를 찾는다. 그때 펼친 SQL 의 <c>?</c> 순서와 로그 값 순서가 일대일이므로
/// <see cref="MyBatisBind.Parameter"/> 에 값을 붙이면 시나리오 값이 된다. 못 맞추면 null.
/// </summary>
public static partial class MyBatisLogValueMatcher
{
    /// <summary>한 문장당 펼쳐 볼 경우의 수 상한(조합 × foreach 크기). 넘으면 못 맞춘 것으로 본다.</summary>
    private const int MaxEvaluations = 5_000;

    [GeneratedRegex(@":(?<n>[A-Za-z_][\w$#]*)", RegexOptions.CultureInvariant)]
    private static partial Regex BindName();

    // Split 은 표지 번호를 조각 사이에 끼워 준다(짝수 칸 = 글자, 홀수 칸 = 번호).
    [GeneratedRegex(@"__sub(\d+)__", RegexOptions.CultureInvariant)]
    private static partial Regex SubMarkPattern();

    [GeneratedRegex(@"^(?<base>.+)\[(?<i>\d+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ListPath();

    public static IReadOnlyDictionary<string, MyBatisScenarioValue>? Match(
        IMyBatisEvaluator evaluator, MyBatisElement statement, IMyBatisFragmentResolver fragments, MyBatisLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(entry);
        var found = evaluator.ExtractParameters(statement, fragments);

        var lists = found.Where(p => p.Usage.HasFlag(MyBatisParameterUsage.Foreach)).Select(p => p.Name).Distinct().ToList();
        var scalars = found.Where(p => !p.Usage.HasFlag(MyBatisParameterUsage.Foreach)).Select(p => p.Name).Distinct().ToList();
        var substitutions = found.Where(p => p.Usage.HasFlag(MyBatisParameterUsage.Substitution)).Select(p => p.Name).Distinct().ToList();
        int bindCount = entry.Params.Count;
        var target = entry.RawSql.Trim().TrimEnd(';').TrimEnd();

        var allOn = evaluator.Evaluate(statement, fragments, BuildValues(scalars, lists, [.. lists.Select(_ => 1)], substitutions), null, new MyBatisEvalOptions(AllOn: true));

        // 목록이 없으면 모두 켠 결과가 바인드 수의 상한이다: 로그 값이 더 많으면 어떤 조합도 못 맞춘다.
        if (lists.Count == 0 && bindCount > allOn.Binds.Count)
        {
            return null;
        }

        var combinations = evaluator.Combinations(allOn);
        int budget = MaxEvaluations;
        foreach (var sizes in ListSizes(lists.Count, bindCount))
        {
            var values = BuildValues(scalars, lists, sizes, substitutions);
            foreach (var combination in combinations)
            {
                if (budget-- <= 0)
                {
                    return null;
                }

                var result = evaluator.Evaluate(statement, fragments, values, combination.Overrides);
                if (result.Binds.Count != bindCount || !TryMatchShape(Unbind(result), target, out var captured))
                {
                    continue;
                }

                var assigned = Assign(result, entry, substitutions, captured);
                if (assigned is not null && Replays(evaluator, statement, fragments, assigned, target, bindCount))
                {
                    return assigned;
                }

                continue;
            }
        }

        return null;
    }

    // 크기 0 도 한 경우다(foreach 가 비어도 다른 조합이 맞을 수 있다). 합이 로그 값 수를 넘는 조합은 만들지 않는다.
    private static IEnumerable<int[]> ListSizes(int count, int total)
    {
        if (count == 0)
        {
            yield return [];
            yield break;
        }

        var sizes = new int[count];
        foreach (var s in Recurse(sizes, 0, total))
        {
            yield return s;
        }
    }

    private static IEnumerable<int[]> Recurse(int[] sizes, int at, int left)
    {
        if (at == sizes.Length)
        {
            yield return [.. sizes];
            yield break;
        }

        for (int n = 0; n <= Math.Max(0, left); n++)
        {
            sizes[at] = n;
            foreach (var s in Recurse(sizes, at + 1, left - n))
            {
                yield return s;
            }

            if (n >= left)
            {
                break;
            }
        }
    }

    private static Dictionary<string, object?> BuildValues(List<string> scalars, List<string> lists, int[] sizes, List<string> substitutions)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in scalars)
        {
            values[name] = "x";
        }

        // ${} 는 값이 SQL 글자에 섞이므로 표지 글자로 계산해 두고 로그 문장에서 그 자리를 되읽는다.
        for (int i = 0; i < substitutions.Count; i++)
        {
            values[substitutions[i]] = SubMark(i);
        }

        for (int i = 0; i < lists.Count; i++)
        {
            values[lists[i]] = Enumerable.Repeat((object?)"x", sizes[i]).ToList();
        }

        return values;
    }

    // 펼친 SQL 의 :이름 중 바인드 이름만 ? 로 되돌린다(문자열 리터럴 안 같은 모양은 이름 집합에 없으니 그대로 둔다).
    private static string Unbind(EvalResult result)
    {
        var names = result.Binds.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        return BindName().Replace(result.Sql, m => names.Contains(m.Groups["n"].Value) ? "?" : m.Value);
    }

    // 맞춘 값만으로(조합 덮어쓰기 없이) 다시 펼쳐도 같은 문장이 나와야 저장할 가치가 있다: test 에만 쓰인 파라미터 같은 빈틈을 걸러 낸다.
    private static bool Replays(
        IMyBatisEvaluator evaluator, MyBatisElement statement, IMyBatisFragmentResolver fragments,
        Dictionary<string, MyBatisScenarioValue> assigned, string target, int bindCount)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, v) in assigned)
        {
            values[name] = v.Kind switch
            {
                MyBatisScenarioValueKind.Null => null,
                MyBatisScenarioValueKind.Number => decimal.TryParse(v.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : v.Text,
                MyBatisScenarioValueKind.List => (v.Items ?? []).Select(i => v.ItemKind == MyBatisScenarioValueKind.Number
                    && decimal.TryParse(i, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (object?)d : i).ToList(),
                _ => v.Text,
            };
        }

        var result = evaluator.Evaluate(statement, fragments, values, null);
        return result.Binds.Count == bindCount && TryMatchShape(Unbind(result), target, out _);
    }

    private static string SubMark(int index) => "__sub" + index.ToString(CultureInfo.InvariantCulture) + "__";

    // 펼친 SQL(바인드는 ? 로 되돌림)이 로그 문장과 같은 모양인지 본다. 공백은 MyBatis 와 계산기가 다를 수 있어(괄호·쉼표 둘레 등) 무시하고,
    // 표지 글자 자리는 로그 문장의 어떤 글자든 받아 치환 값으로 돌려준다(같은 표지가 또 나오면 같은 글자여야 한다).
    private static bool TryMatchShape(string expanded, string target, out Dictionary<int, string> captured)
    {
        captured = [];
        var parts = SubMarkPattern().Split(expanded.TrimEnd().TrimEnd(';').TrimEnd());

        // 값싼 사전 검사: 공백을 버린 글자 조각이 순서대로 나오는지.
        var flat = StripWhitespace(target);
        int at = 0;
        for (int i = 0; i < parts.Length; i += 2)
        {
            var chunk = StripWhitespace(parts[i]);
            int hit = i == 0 ? (flat.StartsWith(chunk, StringComparison.Ordinal) ? 0 : -1) : flat.IndexOf(chunk, at, StringComparison.Ordinal);
            if (hit < 0 || (i == parts.Length - 1 && !flat.EndsWith(chunk, StringComparison.Ordinal)))
            {
                return false;
            }

            at = hit + chunk.Length;
        }

        if (parts.Length == 1)
        {
            return flat == StripWhitespace(parts[0]);
        }

        var pattern = new StringBuilder("^\\s*");
        var seen = new HashSet<int>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 0)
            {
                AppendLiteral(pattern, parts[i]);
                continue;
            }

            int n = int.Parse(parts[i], CultureInfo.InvariantCulture);
            pattern.Append(seen.Add(n) ? "(?<s" + n.ToString(CultureInfo.InvariantCulture) + ">.+?)" : "\\k<s" + n.ToString(CultureInfo.InvariantCulture) + ">");
        }

        pattern.Append("\\s*$");
        Match m;
        try
        {
            m = Regex.Match(target, pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!m.Success)
        {
            return false;
        }

        foreach (var n in seen)
        {
            captured[n] = m.Groups["s" + n.ToString(CultureInfo.InvariantCulture)].Value.Trim();
        }

        return true;
    }

    private static void AppendLiteral(StringBuilder pattern, string literal)
    {
        for (int i = 0; i < literal.Length; i++)
        {
            var c = literal[i];
            if (char.IsWhiteSpace(c))
            {
                while (i + 1 < literal.Length && char.IsWhiteSpace(literal[i + 1]))
                {
                    i++;
                }

                pattern.Append("\\s*");
            }
            else if (c is '(' or ')' or ',')
            {
                pattern.Append("\\s*").Append(Regex.Escape(c.ToString())).Append("\\s*");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }
    }

    private static string StripWhitespace(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        foreach (var c in sql)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString().TrimEnd(';');
    }

    private static Dictionary<string, MyBatisScenarioValue>? Assign(
        EvalResult result, MyBatisLogEntry entry, List<string> substitutions, Dictionary<int, string> captured)
    {
        var scalars = new Dictionary<string, MyBatisLogParam>(StringComparer.Ordinal);
        var lists = new Dictionary<string, SortedDictionary<int, MyBatisLogParam>>(StringComparer.Ordinal);
        for (int i = 0; i < result.Binds.Count; i++)
        {
            var path = result.Binds[i].Parameter;
            var param = entry.Params[i];
            var list = ListPath().Match(path);
            if (list.Success)
            {
                var items = lists.TryGetValue(list.Groups["base"].Value, out var found)
                    ? found
                    : lists[list.Groups["base"].Value] = [];
                items[int.Parse(list.Groups["i"].Value, CultureInfo.InvariantCulture)] = param;
            }
            else if (path.Contains('['))
            {
                // 목록 항목의 속성(list[0].id)은 시나리오 값(글자 목록)으로 나타낼 수 없다.
                return null;
            }
            else if (scalars.TryGetValue(path, out var prior))
            {
                // 같은 파라미터가 두 번 쓰였는데 로그 값이 다르면 이 파라미터 하나로 설명되지 않는다.
                if (prior.IsNull != param.IsNull || prior.Value != param.Value)
                {
                    return null;
                }
            }
            else
            {
                scalars[path] = param;
            }
        }

        var values = new Dictionary<string, MyBatisScenarioValue>(StringComparer.Ordinal);
        foreach (var (n, text) in captured)
        {
            values[substitutions[n]] = SensitiveBindNames.IsSensitive(substitutions[n])
                ? MyBatisScenarioValue.Null
                : new MyBatisScenarioValue(MyBatisScenarioValueKind.Text, text);
        }

        foreach (var (path, param) in scalars)
        {
            values[path] = SensitiveBindNames.IsSensitive(path) ? MyBatisScenarioValue.Null : ToValue(param);
        }

        foreach (var (path, items) in lists)
        {
            values[path] = SensitiveBindNames.IsSensitive(path) ? MyBatisScenarioValue.Null : ToList(items.Values);
        }

        return values;
    }

    private static MyBatisScenarioValue ToValue(MyBatisLogParam p) => p.IsNull
        ? MyBatisScenarioValue.Null
        : new MyBatisScenarioValue(p.Type == BindDataType.Number ? MyBatisScenarioValueKind.Number : MyBatisScenarioValueKind.Text, p.Value);

    private static MyBatisScenarioValue ToList(IEnumerable<MyBatisLogParam> items)
    {
        var list = items.ToList();
        var kind = list.All(p => p.Type == BindDataType.Number && !p.IsNull) ? MyBatisScenarioValueKind.Number : MyBatisScenarioValueKind.Text;
        return new MyBatisScenarioValue(MyBatisScenarioValueKind.List, Items: [.. list.Select(p => p.Value)], ItemKind: kind);
    }
}
