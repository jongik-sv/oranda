using System.Collections;
using System.Globalization;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.MyBatis;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>
/// 동적 SQL 계산기(MB-02, TRD 3.2·4, TSK-02-02). MyBatis 3.5.19 의 SqlNode 동작(DynamicContext 의 공백 이음, trim·where·set 의
/// 앞뒤 다듬기, foreach 의 PrefixedContext·항목 이름 바꾸기, bind)을 그대로 따른다. 정답 = tests/Oranda.Language.Tests/MyBatis/Golden.
/// 펼친 SQL 의 <c>#{…}</c> 는 Oracle 바인드 <c>:이름</c> 이 되고, 값은 MyBatis DefaultParameterHandler 처럼 다 펼친 뒤 마지막 바인딩에서 꺼낸다.
/// </summary>
[OrandaService(typeof(IMyBatisEvaluator))]
public sealed partial class MyBatisEvaluator : IMyBatisEvaluator
{
    private const char MarkOpen = '';
    private const char MarkClose = '';
    private const string ItemPrefix = "__frch_";

    private static readonly string[] WherePrefixes = ["AND ", "OR ", "AND\n", "OR\n", "AND\r", "OR\r", "AND\t", "OR\t"];
    private static readonly string[] SetOverrides = [","];
    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>(StringComparer.Ordinal);

    public EvalResult Evaluate(
        MyBatisElement statement,
        IMyBatisFragmentResolver fragments,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<TextRange, bool>? blockOverrides,
        MyBatisEvalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(fragments);
        options ??= MyBatisEvalOptions.Default;
        var expanded = Expand(statement, fragments);
        var run = new Run(values ?? NoValues, blockOverrides, options.AllOn);
        run.CollectBlocks(expanded.Body);
        var root = new RootContext();
        run.ApplyAll(expanded.Body, root);
        var (sql, binds) = run.Finish(root.Sql);
        if (options.LegacyNotation)
        {
            sql = RenderLegacy(expanded.Body);
        }

        var issues = new List<MyBatisEvalIssue>(expanded.Issues);
        foreach (var i in run.Issues)
        {
            if (!issues.Contains(i))
            {
                issues.Add(i);
            }
        }

        return new EvalResult(sql, binds, run.Substitutions, run.Blocks(), issues);
    }

    public IReadOnlyList<MyBatisParameter> ExtractParameters(MyBatisElement statement, IMyBatisFragmentResolver fragments)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(fragments);
        return ParameterExtractor.Extract(Expand(statement, fragments).Body);
    }

    public IReadOnlyList<ConditionCombination> Combinations(EvalResult allOn, int fullLimit = 8)
    {
        ArgumentNullException.ThrowIfNull(allOn);
        return ConditionCombinations.Build(allOn.Blocks, fullLimit);
    }

    // ---- 문맥(MyBatis DynamicContext 와 그 감싸개) --------------------------------------------------

    private abstract class SqlContext
    {
        public abstract void Append(string sql);
    }

    /// <summary>DynamicContext: 조각을 공백 하나로 잇는다(StringJoiner(" ")), 끝에서 앞뒤를 자른다.</summary>
    private sealed class RootContext : SqlContext
    {
        private readonly List<string> _parts = [];

        public string Sql => JavaTrim(string.Join(' ', _parts));

        public override void Append(string sql) => _parts.Add(sql);
    }

    /// <summary>TrimSqlNode.FilteredDynamicContext: 그대로 붙여 모았다가 앞뒤 낱말을 떼고 접두·접미를 붙여 바깥에 넘긴다.</summary>
    private sealed class TrimContext(
        SqlContext parent, string? prefix, IReadOnlyList<string> prefixOverrides, string? suffix, IReadOnlyList<string> suffixOverrides, Action<string> fail)
        : SqlContext
    {
        private readonly StringBuilder _buffer = new();

        public override void Append(string sql) => _buffer.Append(sql);

        /// <summary>내용이 남았으면 참.</summary>
        public bool ApplyAll()
        {
            var trimmed = JavaTrim(_buffer.ToString());
            var sql = new StringBuilder(trimmed);
            var upper = trimmed.ToUpperInvariant();
            if (upper.Length > 0)
            {
                foreach (var p in prefixOverrides)
                {
                    if (upper.StartsWith(p, StringComparison.Ordinal))
                    {
                        // StringBuilder.delete(0, n) 는 n 이 길이를 넘으면 길이까지만 지운다.
                        sql.Remove(0, Math.Min(JavaTrim(p).Length, sql.Length));
                        break;
                    }
                }

                if (prefix is not null)
                {
                    sql.Insert(0, ' ').Insert(0, prefix);
                }

                foreach (var s in suffixOverrides)
                {
                    var t = JavaTrim(s);
                    if (upper.EndsWith(s, StringComparison.Ordinal) || upper.EndsWith(t, StringComparison.Ordinal))
                    {
                        if (sql.Length < t.Length)
                        {
                            // 접두를 떼고 나니 접미보다 짧다: MyBatis 3.5 는 StringIndexOutOfBoundsException 으로 실패한다.
                            fail($"접두를 떼고 남은 글이 접미 「{t}」 보다 짧다(MyBatis 3.5 는 StringIndexOutOfBoundsException).");
                            break;
                        }

                        sql.Remove(sql.Length - t.Length, t.Length);
                        break;
                    }
                }

                if (suffix is not null)
                {
                    sql.Append(' ').Append(suffix);
                }
            }

            parent.Append(sql.ToString());
            return upper.Length > 0;
        }
    }

    /// <summary>ForEachSqlNode.PrefixedContext: 처음으로 빈칸 아닌 글이 올 때 구분자(첫 항목은 빈 글)를 먼저 붙인다.</summary>
    private sealed class PrefixedContext(SqlContext parent, string prefix) : SqlContext
    {
        public bool PrefixApplied { get; private set; }

        public override void Append(string sql)
        {
            if (!PrefixApplied && JavaTrim(sql).Length > 0)
            {
                parent.Append(prefix);
                PrefixApplied = true;
            }

            parent.Append(sql);
        }
    }

    /// <summary>ForEachSqlNode.FilteredDynamicContext: 이 반복의 item·index 를 가리키는 바인드를 반복마다 다른 이름으로 바꾼다.</summary>
    private sealed class ForeachContext(SqlContext parent, Run run, ForeachFrame frame) : SqlContext
    {
        public override void Append(string sql)
        {
            int at = sql.IndexOf(MarkOpen, StringComparison.Ordinal);
            while (at >= 0)
            {
                if (run.TryReadMark(sql, at, out var mark, out int end))
                {
                    if (!frame.Itemize(mark, frame.Item, frame.ItemNumber, frame.ItemDisplay))
                    {
                        frame.Itemize(mark, frame.Index, frame.IndexNumber, null);
                    }
                }

                at = sql.IndexOf(MarkOpen, end);
            }

            parent.Append(sql);
        }
    }

    /// <summary>foreach 반복 하나의 이름표.</summary>
    private sealed class ForeachFrame
    {
        public string? Item { get; init; }

        public string? Index { get; init; }

        public int Unique { get; init; }

        /// <summary>펼친 SQL 바인드 이름에 붙일 반복 번호(1부터). 0 = 번호 없음(모두 켬).</summary>
        public int ItemNumber { get; init; }

        public int IndexNumber { get; init; }

        /// <summary>item 값이 나온 파라미터 경로(<c>deptIds[0]</c>).</summary>
        public required string ItemDisplay { get; init; }

        public bool Itemize(BindMark mark, string? name, int number, string? display)
        {
            if (name is null || !RefersTo(mark.Property, name))
            {
                return false;
            }

            var rest = mark.Property[name.Length..];
            mark.Property = ItemPrefix + name + "_" + Unique.ToString(CultureInfo.InvariantCulture) + rest;
            var baseName = SanitizeName(name + rest);
            mark.NameBase = number > 0 ? baseName + "_" + number.ToString(CultureInfo.InvariantCulture) : baseName;
            mark.Display = display is null ? mark.NameBase : display + rest;
            return true;
        }
    }

    /// <summary><c>#{…}</c> 하나. 문맥을 지나며 이름이 바뀌고 마지막에 <c>:이름</c> 이 된다.</summary>
    private sealed class BindMark
    {
        /// <summary>값을 꺼낼 MyBatis 경로(foreach 안이면 <c>__frch_id_0</c>).</summary>
        public required string Property { get; set; }

        /// <summary>사람에게 보일 파라미터 경로(<see cref="MyBatisBind.Parameter"/>).</summary>
        public required string Display { get; set; }

        public required string NameBase { get; set; }

        public string? JdbcType { get; init; }

        public Origin Origin { get; init; }
    }

    /// <summary>블록 하나의 계산 상태(원문 태그마다 하나).</summary>
    private sealed class BlockState
    {
        public required string Tag { get; init; }

        public string? Test { get; init; }

        public Origin Origin { get; init; }

        public bool IsCondition { get; init; }

        public bool IsOn { get; set; }

        public bool IsOverridden { get; set; }
    }

    // MyBatis ForEach 의 정규식 ^\s*item(?![^.,:\s]) 과 같다(Property 는 이미 앞 공백·뒤 속성을 뗀 이름).
    private static bool RefersTo(string property, string name) =>
        property.StartsWith(name, StringComparison.Ordinal)
        && (property.Length == name.Length || property[name.Length] is '.' or ',' or ':' || char.IsWhiteSpace(property[name.Length]));

    /// <summary>Oracle 바인드 이름: 점·괄호를 밑줄로(<c>user.name</c> → <c>user_name</c>).</summary>
    private static string SanitizeName(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (char c in path)
        {
            char x = char.IsLetterOrDigit(c) || c is '_' or '$' or '#' ? c : '_';
            if (x == '_' && sb.Length > 0 && sb[^1] == '_')
            {
                continue;
            }

            sb.Append(x);
        }

        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "p" : s;
    }

    /// <summary>Java String.trim: 앞뒤의 <c>' '</c> 이하 글자를 뗀다.</summary>
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

        return start == 0 && end == s.Length ? s : s[start..end];
    }

    /// <summary><c>#{ename, jdbcType=VARCHAR}</c>·<c>#{x:VARCHAR}</c> 의 속성 이름과 jdbcType·javaType.</summary>
    private static (string Property, string? JdbcType, string? JavaType) ParseHash(string content)
    {
        int cut = content.IndexOfAny([',', ':']);
        var property = (cut < 0 ? content : content[..cut]).Trim();
        string? jdbc = null;
        string? java = null;
        if (cut >= 0)
        {
            var rest = content[(cut + 1)..];
            if (content[cut] == ':')
            {
                int comma = rest.IndexOf(',', StringComparison.Ordinal);
                jdbc = (comma < 0 ? rest : rest[..comma]).Trim();
                rest = comma < 0 ? string.Empty : rest[(comma + 1)..];
            }

            foreach (var part in rest.Split(','))
            {
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                {
                    continue;
                }

                var key = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                if (string.Equals(key, "jdbcType", StringComparison.Ordinal))
                {
                    jdbc = value;
                }
                else if (string.Equals(key, "javaType", StringComparison.Ordinal))
                {
                    java = value;
                }
            }
        }

        return (property, string.IsNullOrEmpty(jdbc) ? null : jdbc, string.IsNullOrEmpty(java) ? null : java);
    }

    // 속성 값 안 범위를 문장(또는 조각) 원문 범위로. 엔티티·줄바꿈이 끼어 길이가 다르면 속성 값 전체.
    private static TextRange MapAttributeRange(TextRange valueRange, string decoded, TextRange inner) =>
        valueRange.Length == decoded.Length && inner.End <= decoded.Length
            ? new TextRange(valueRange.Start + inner.Start, inner.Length)
            : valueRange;

    private static bool IsNumberedList(object? value) => value is IEnumerable and not string;

    private static IEnumerable<(object? Index, object? Item)> Iterate(object value)
    {
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry e in dictionary)
            {
                yield return (e.Key, e.Value);
            }

            yield break;
        }

        int i = 0;
        foreach (var item in (IEnumerable)value)
        {
            yield return (i, item);
            i++;
        }
    }
}
