using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 작업 변수(<see cref="CopyVariable"/>)를 조건에 넣는다. 조건의 <c>:이름</c>(작은따옴표 문자열·큰따옴표 식별자 밖)이 변수면
/// <list type="bullet">
/// <item>값 하나: <c>:이름</c> 그대로 바인드한다(<c>= :ORDER_NO</c>, <c>IN (:ORDER_NO)</c> 둘 다 된다).</item>
/// <item>값 2~1000개: <c>:이름_1, :이름_2, …</c> 로 펼친다(<c>IN (:COILS)</c> 로 쓴다).</item>
/// <item>값 1000개 넘음: Oracle 의 IN 목록 상한(ORA-01795, 11g)과 목록 형식 생성자의 인자 상한(999개, ORA-00939)을 피해
/// 999개씩 <c>SELECT COLUMN_VALUE FROM TABLE(SYS.ODCIVARCHAR2LIST(…)) UNION ALL …</c> 로 묶는다(역시 <c>IN (:COILS)</c> 안에 쓴다).</item>
/// </list>
/// <c>:FROM_DT</c>·<c>:TO_DT</c> 는 기간이 채우므로 변수 이름으로 쓸 수 없다.
/// </summary>
public static class CopyVariables
{
    /// <summary>이름 최대 길이: 펼친 바인드 이름(<c>이름_10000</c>)이 Oracle 바인드 이름 상한(30)을 넘지 않게.</summary>
    public const int MaxNameLength = 24;

    /// <summary>변수 하나에 넣을 수 있는 값 수.</summary>
    public const int MaxValues = 10_000;

    /// <summary>이 수까지는 <c>:이름_1, …</c> 로 펼치고, 넘으면 목록 형식으로 묶는다.</summary>
    public const int PlainListLimit = 1000;

    /// <summary>목록 형식 생성자 하나에 넣는 값 수(Oracle 상한 999).</summary>
    public const int ChunkSize = 999;

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyyMMdd"];

    private static readonly Regex NamePattern = new("^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant);

    // 문장 만들기가 쓰는 바인드 이름(기간 열 B1_FROM, 행 값 C1, 키 K1)과 펼친 이름(_숫자)이 겹치지 않게 막는 꼴.
    private static readonly Regex ReservedPattern = new(@"^(B\d+_(FROM|TO)|C\d+|K\d+)$|_\d+$", RegexOptions.CultureInvariant);

    private static readonly string[] PeriodNames = ["FROM_DT", "TO_DT"];

    /// <summary>값 글을 나눈다: 줄바꿈·쉼표·탭으로 나누고 앞뒤 공백을 지우며, 빈 칸과 겹치는 값은 뺀다(처음 나온 순서).</summary>
    public static IReadOnlyList<string> SplitValues(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();
        foreach (var part in text.Split(['\r', '\n', ',', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(part))
            {
                values.Add(part);
            }
        }

        return values;
    }

    /// <summary>값 목록을 편집 칸 글로(한 줄에 하나).</summary>
    public static string JoinValues(IReadOnlyList<string> values) => string.Join('\n', values ?? []);

    /// <summary>적은 이름을 정리한다: 앞뒤 공백과 앞의 <c>:</c> 를 지우고 대문자로.</summary>
    public static string NormalizeName(string? name) => (name ?? string.Empty).Trim().TrimStart(':').Trim().ToUpperInvariant();

    /// <summary>변수들의 문제(이름 꼴·겹침·예약 이름·값 없음·값 형식). 없으면 빈 목록.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<CopyVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in variables)
        {
            if (Problem(v) is { } problem)
            {
                problems.Add(problem);
            }
            else if (!names.Add(v.Name))
            {
                problems.Add($"변수 :{v.Name} 이(가) 두 번 있습니다.");
            }
        }

        return problems;
    }

    /// <summary>변수 하나의 문제. 없으면 null.</summary>
    public static string? Problem(CopyVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var name = variable.Name;
        if (name.Length == 0)
        {
            return "변수 이름이 비었습니다.";
        }

        if (name.Length > MaxNameLength || !NamePattern.IsMatch(name))
        {
            return $"변수 :{name}: 이름은 영문자로 시작하고 영문자·숫자·밑줄로 {MaxNameLength}자까지 씁니다.";
        }

        if (PeriodNames.Contains(name, StringComparer.Ordinal))
        {
            return $"변수 :{name}: 기간이 쓰는 이름이라 변수로 쓸 수 없습니다.";
        }

        if (ReservedPattern.IsMatch(name))
        {
            return $"변수 :{name}: 「_숫자」로 끝나거나 B1_FROM·C1·K1 꼴인 이름은 쓸 수 없습니다(복사가 쓰는 바인드 이름입니다).";
        }

        if (variable.Values.Count == 0)
        {
            return $"변수 :{name}: 값이 없습니다.";
        }

        if (variable.Values.Count > MaxValues)
        {
            return $"변수 :{name}: 값은 {MaxValues.ToString("N0", CultureInfo.InvariantCulture)}개까지 넣을 수 있습니다(지금 {variable.Values.Count.ToString("N0", CultureInfo.InvariantCulture)}개).";
        }

        foreach (var value in variable.Values)
        {
            if (!TryConvert(variable.Type, value, out _))
            {
                return variable.Type == CopyVariableType.Number
                    ? $"변수 :{name}: 「{value}」 은(는) 숫자가 아닙니다."
                    : $"변수 :{name}: 「{value}」 은(는) 날짜(yyyy-MM-dd 또는 yyyy-MM-dd HH:mm:ss)가 아닙니다.";
            }
        }

        return null;
    }

    /// <summary>조건에 쓴 바인드 이름(대문자, 처음 나온 순서, 겹침 없음). 문자열·큰따옴표 식별자 안의 <c>:</c> 는 보지 않는다.</summary>
    public static IReadOnlyList<string> BindNames(string where)
    {
        ArgumentNullException.ThrowIfNull(where);
        var names = new List<string>();
        Scan(where, null, (name, _) =>
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }

            return null;
        });
        return names;
    }

    /// <summary>조건에 쓴 바인드 가운데 변수도 기간(<c>:FROM_DT</c>·<c>:TO_DT</c>)도 아닌 것이 있으면 그 이유. 없으면 null.</summary>
    public static string? UnknownProblem(string where, IReadOnlyList<CopyVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var unknown = BindNames(where)
            .Where(n => !PeriodNames.Contains(n, StringComparer.Ordinal) && !variables.Any(v => string.Equals(v.Name, n, StringComparison.Ordinal)))
            .ToList();
        return unknown.Count == 0
            ? null
            : $"변수 {string.Join(", ", unknown.Select(n => ":" + n))} 이(가) 없습니다. 작업 변수에 더하세요.";
    }

    /// <summary>
    /// 조건에 쓴 변수의 값을 <paramref name="binds"/> 에 채우고, 여러 값 변수는 펼친 조건을 돌려준다. 쓴 변수에 문제가 있으면 <see cref="InvalidOperationException"/>.
    /// 변수가 아닌 바인드(<c>:FROM_DT</c> 등)는 그대로 둔다.
    /// </summary>
    public static string Expand(string where, IReadOnlyList<CopyVariable> variables, IDictionary<string, BindValue> binds)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(binds);
        if (variables.Count == 0)
        {
            return where;
        }

        var byName = new Dictionary<string, CopyVariable>(StringComparer.Ordinal);
        foreach (var v in variables)
        {
            byName.TryAdd(v.Name, v);
        }

        return Scan(where, new StringBuilder(where.Length), (name, _) =>
        {
            if (!byName.TryGetValue(name, out var variable))
            {
                return null;
            }

            if (Problem(variable) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            return Replacement(variable, binds);
        })!;
    }

    private static string Replacement(CopyVariable variable, IDictionary<string, BindValue> binds)
    {
        var name = variable.Name;
        if (variable.Values.Count == 1)
        {
            binds[name] = Bind(variable.Type, variable.Values[0]);
            return ":" + name;
        }

        var names = new List<string>(variable.Values.Count);
        for (var i = 0; i < variable.Values.Count; i++)
        {
            var bindName = name + "_" + (i + 1).ToString(CultureInfo.InvariantCulture);
            binds[bindName] = Bind(variable.Type, variable.Values[i]);
            names.Add(":" + bindName);
        }

        if (names.Count <= PlainListLimit)
        {
            return string.Join(", ", names);
        }

        var listType = variable.Type switch
        {
            CopyVariableType.Number => "SYS.ODCINUMBERLIST",
            CopyVariableType.Date => "SYS.ODCIDATELIST",
            _ => "SYS.ODCIVARCHAR2LIST",
        };
        return string.Join(" UNION ALL ", names.Chunk(ChunkSize).Select(chunk => $"SELECT COLUMN_VALUE FROM TABLE({listType}({string.Join(", ", chunk)}))"));
    }

    private static BindValue Bind(CopyVariableType type, string value)
    {
        TryConvert(type, value, out var converted);
        return type switch
        {
            CopyVariableType.Number => new BindValue(BindDataType.Number, converted),
            CopyVariableType.Date => new BindValue(BindDataType.Date, converted),
            _ => new BindValue(BindDataType.Varchar2, converted),
        };
    }

    /// <summary>값 글을 바인드 값으로(문자 = 그대로, 숫자 = decimal, 날짜 = DateTime).</summary>
    public static bool TryConvert(CopyVariableType type, string value, out object? converted)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (type)
        {
            case CopyVariableType.Number when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                converted = number;
                return true;
            case CopyVariableType.Date when DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                converted = date;
                return true;
            case CopyVariableType.Text:
                converted = value;
                return true;
            default:
                converted = null;
                return false;
        }
    }

    // 조건을 훑어 바인드 이름마다 replace 를 부른다. output 이 있으면 replace 가 돌려준 글(null 이면 원래 글)로 바꾼 조건을 만든다.
    private static string? Scan(string where, StringBuilder? output, Func<string, int, string?> replace)
    {
        var i = 0;
        while (i < where.Length)
        {
            var c = where[i];
            if (c is '\'' or '"')
            {
                var close = where.IndexOf(c, i + 1);
                var end = close < 0 ? where.Length : close + 1;
                output?.Append(where, i, end - i);
                i = end;
                continue;
            }

            if (c == ':' && i + 1 < where.Length && char.IsAsciiLetter(where[i + 1]))
            {
                var start = i + 1;
                var j = start;
                while (j < where.Length && (char.IsAsciiLetterOrDigit(where[j]) || where[j] is '_' or '$' or '#'))
                {
                    j++;
                }

                var name = where[start..j].ToUpperInvariant();
                var replaced = replace(name, i);
                output?.Append(replaced ?? where[i..j]);
                i = j;
                continue;
            }

            output?.Append(c);
            i++;
        }

        return output?.ToString();
    }
}
