using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Language.Lexing;

namespace Oranda.Language.MyBatis;

/// <summary>로그의 파라미터 하나. <see cref="Name"/> 은 콜론 없는 <c>p1</c>, <c>p2</c>… 순서다.</summary>
public sealed record MyBatisLogParam(string Name, BindDataType Type, string Value, bool IsNull);

/// <summary>Preparing/Parameters 쌍 하나. <see cref="Sql"/> 은 <c>?</c> 를 <c>:pN</c> 으로 바꾼 문장이다(번호는 로그 전체에서 이어진다).</summary>
public sealed record MyBatisLogStatement(string Sql, IReadOnlyList<MyBatisLogParam> Params);

/// <summary>
/// MyBatis 로그(<c>==&gt;  Preparing: …</c>, <c>==&gt; Parameters: 1(Integer), abc(String)</c>)를 읽어 바인드 변수를 쓴 문장으로 되돌린다(TSK-13-03).
/// 매퍼 XML 의 if·foreach 평가는 하지 않는다. UI 를 모른다.
/// </summary>
public static class MyBatisLogParser
{
    private const string PreparingMarker = "Preparing:";
    private const string ParametersMarker = "Parameters:";

    /// <summary>
    /// 로그 글에서 Preparing/Parameters 쌍을 순서대로 모두 읽는다. 쌍이 없으면 빈 목록.
    /// 바인드 이름은 쌍마다 겹치지 않게 로그 전체에서 <c>p1..pN</c> 으로 이어 붙인다(바인드 창은 이름 하나에 값 하나다).
    /// </summary>
    public static IReadOnlyList<MyBatisLogStatement> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<MyBatisLogStatement>();
        int offset = 0;
        string? sql = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int prep = line.IndexOf(PreparingMarker, StringComparison.Ordinal);
            if (prep >= 0)
            {
                if (sql is not null)
                {
                    result.Add(Build(sql, string.Empty, ref offset));
                }

                sql = line[(prep + PreparingMarker.Length)..].Trim();
                continue;
            }

            int par = line.IndexOf(ParametersMarker, StringComparison.Ordinal);
            if (par >= 0 && sql is not null)
            {
                result.Add(Build(sql, line[(par + ParametersMarker.Length)..].Trim(), ref offset));
                sql = null;
            }
        }

        if (sql is not null)
        {
            result.Add(Build(sql, string.Empty, ref offset));
        }

        return result;
    }

    /// <summary>Java 타입 이름(완전 이름 가능)을 바인드 타입으로 대응한다. 모르는 타입은 문자.</summary>
    public static BindDataType MapType(string javaType)
    {
        ArgumentNullException.ThrowIfNull(javaType);
        var simple = javaType[(javaType.LastIndexOf('.') + 1)..];
        return simple switch
        {
            "Integer" or "Long" or "Short" or "Byte" or "BigDecimal" or "BigInteger" or "Double" or "Float" => BindDataType.Number,
            "Timestamp" or "LocalDateTime" => BindDataType.Timestamp,
            "Date" or "LocalDate" => BindDataType.Date,
            _ => BindDataType.Varchar2,
        };
    }

    /// <summary><c>?</c> 를 <c>:p1..:pN</c> 으로 바꾼다. 문자열 리터럴·주석·힌트 안의 <c>?</c> 는 그대로 둔다. 바꾼 개수를 돌려준다.</summary>
    public static string ReplacePlaceholders(string sql, out int count) => ReplacePlaceholders(sql, 0, out count);

    private static string ReplacePlaceholders(string sql, int offset, out int count)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var sb = new StringBuilder(sql.Length + 8);
        int last = 0;
        count = 0;
        foreach (var t in SqlLexer.Tokenize(sql))
        {
            if (t.Length != 1 || sql[t.Start] != '?' || t.Kind is TokenKind.StringLiteral or TokenKind.Comment or TokenKind.Hint or TokenKind.QuotedIdent)
            {
                continue;
            }

            count++;
            sb.Append(sql, last, t.Start - last).Append(":p").Append((offset + count).ToString(CultureInfo.InvariantCulture));
            last = t.End;
        }

        return sb.Append(sql, last, sql.Length - last).ToString();
    }

    private static MyBatisLogStatement Build(string sql, string parameters, ref int offset)
    {
        var converted = ReplacePlaceholders(sql, offset, out int count);
        var items = SplitParameters(parameters, count);
        var list = new List<MyBatisLogParam>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var (value, type, isNull) = items[i];
            list.Add(new MyBatisLogParam("p" + (offset + i + 1).ToString(CultureInfo.InvariantCulture), type, isNull ? string.Empty : value, isNull));
        }

        offset += Math.Max(count, items.Count);
        return new MyBatisLogStatement(converted, list);
    }

    /// <summary>
    /// <c>값(타입), 값(타입), null</c> 을 나눈다. 값 안에 <c>, </c> 나 괄호가 있을 수 있어 뒤에서부터 잘라 가며,
    /// 파라미터 수가 <paramref name="expected"/> 와 맞는 분할을 먼저 찾는다. 맞는 분할이 없으면 가능한 만큼 잘게 나눈다.
    /// </summary>
    private static List<(string Value, BindDataType Type, bool IsNull)> SplitParameters(string text, int expected)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var found = new List<(string, BindDataType, bool)>();
        if (expected > 0 && TrySplit(text, text.Length, expected, found))
        {
            found.Reverse();
            return found;
        }

        found.Clear();
        TrySplit(text, text.Length, -1, found);
        found.Reverse();
        return found;
    }

    /// <summary>
    /// text[0..end] 를 정확히 <paramref name="remaining"/> 개(-1 이면 가능한 만큼)로 나눈다. 뒤에서부터 모으므로 <paramref name="acc"/> 는 거꾸로 쌓인다.
    /// 마지막 항목의 시작 후보는 ", " 바로 뒤이거나 0 이고, 짧은 값부터 시도한다.
    /// </summary>
    private static bool TrySplit(string text, int end, int remaining, List<(string Value, BindDataType Type, bool IsNull)> acc)
    {
        int mark = acc.Count;
        for (int s = end; s >= 0; s--)
        {
            if (s != 0 && !(s >= 2 && text[s - 2] == ',' && text[s - 1] == ' '))
            {
                continue;
            }

            if (!TryItem(text, s, end, out var item))
            {
                continue;
            }

            if (s == 0)
            {
                if (remaining is 1 or -1)
                {
                    acc.Add(item);
                    return true;
                }

                continue;
            }

            if (remaining == 1)
            {
                continue;
            }

            acc.Add(item);
            if (TrySplit(text, s - 2, remaining == -1 ? -1 : remaining - 1, acc))
            {
                return true;
            }

            acc.RemoveRange(mark, acc.Count - mark);
        }

        return false;
    }

    private static bool TryItem(string text, int start, int end, out (string Value, BindDataType Type, bool IsNull) item)
    {
        item = default;
        var s = text.AsSpan(start, end - start);
        if (s.SequenceEqual("null"))
        {
            item = (string.Empty, BindDataType.Varchar2, true);
            return true;
        }

        if (s.Length == 0 || s[^1] != ')')
        {
            return false;
        }

        int open = s.LastIndexOf('(');
        if (open < 0)
        {
            return false;
        }

        var type = s[(open + 1)..^1];
        if (type.Length == 0 || !IsTypeName(type))
        {
            return false;
        }

        var typeText = type.ToString();
        item = (s[..open].ToString(), MapType(typeText), false);
        return true;
    }

    private static bool IsTypeName(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c is '.' or '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }
}
