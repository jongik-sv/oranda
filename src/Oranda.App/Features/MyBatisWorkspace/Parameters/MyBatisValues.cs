using System.Globalization;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Parameters;

/// <summary>
/// 시나리오 값(<see cref="MyBatisScenarioValue"/>, 글자로 저장)과 계산기 값(<c>object?</c>)·화면 글자 사이의 변환(MB-04).
/// 숫자는 불변 문화권 글자로 두고 계산기에는 decimal 로 넘긴다. 목록(foreach)은 화면에서 쉼표로 넣는다(「10, 20」).
/// </summary>
public static partial class MyBatisValues
{
    /// <summary>계산기에 넘길 값. 숫자 글자를 읽지 못하면 글자 그대로 넘긴다(계산기가 비교에서 알린다).</summary>
    public static object? ToEvaluator(MyBatisScenarioValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            MyBatisScenarioValueKind.Null => null,
            MyBatisScenarioValueKind.List => (value.Items ?? []).Select(i => Scalar(value.ItemKind, i)).ToList(),
            _ => Scalar(value.Kind, value.Text),
        };
    }

    /// <summary>계산기 값 사전(점이 든 평평한 열쇠 그대로, 계산기가 점 경로로 읽는다).</summary>
    public static Dictionary<string, object?> ToEvaluator(IReadOnlyDictionary<string, MyBatisScenarioValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            result[name] = ToEvaluator(value);
        }

        return result;
    }

    /// <summary>표의 값 칸에 보일 글자. null 은 <c>null</c>, 목록은 「10, 20」.</summary>
    public static string Display(MyBatisScenarioValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            MyBatisScenarioValueKind.Null => "null",
            MyBatisScenarioValueKind.List => string.Join(", ", value.Items ?? []),
            _ => value.Text ?? string.Empty,
        };
    }

    /// <summary>
    /// 값 칸 글자를 <paramref name="kind"/> 의 값으로 읽는다. 목록은 쉼표로 나누고 앞뒤 공백을 떼며 빈 항목은 버린다(빈 글자 = 빈 목록).
    /// 숫자·참거짓은 글자 그대로 두고(저장 모양), 계산기에 넘길 때 읽는다.
    /// </summary>
    public static MyBatisScenarioValue Parse(string? text, MyBatisScenarioValueKind kind, MyBatisScenarioValueKind itemKind = MyBatisScenarioValueKind.Text)
    {
        text ??= string.Empty;
        return kind switch
        {
            MyBatisScenarioValueKind.Null => MyBatisScenarioValue.Null,
            MyBatisScenarioValueKind.List => new MyBatisScenarioValue(
                MyBatisScenarioValueKind.List,
                Items: [.. text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)],
                ItemKind: itemKind),
            MyBatisScenarioValueKind.Number or MyBatisScenarioValueKind.Boolean => new MyBatisScenarioValue(kind, text.Trim()),
            _ => new MyBatisScenarioValue(kind, text),
        };
    }

    /// <summary>
    /// 값이 없는 파라미터의 처음 종류: foreach collection 이면 목록, 숫자 jdbcType 이면 숫자, 그 밖은 글자.
    /// 목록 항목 종류도 같은 규칙(숫자 jdbcType 이면 숫자).
    /// </summary>
    public static (MyBatisScenarioValueKind Kind, MyBatisScenarioValueKind ItemKind) GuessKind(MyBatisParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var numeric = IsNumericJdbcType(parameter.JdbcType);
        var scalar = numeric ? MyBatisScenarioValueKind.Number : MyBatisScenarioValueKind.Text;
        return (parameter.Usage & MyBatisParameterUsage.Foreach) != 0 ? (MyBatisScenarioValueKind.List, scalar) : (scalar, scalar);
    }

    /// <summary>쓰인 곳 표시(「test · #{}」, 「foreach」, 「${}」). 여럿이면 「 · 」 로 잇는다.</summary>
    public static string UsageText(MyBatisParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var parts = new List<string>(5);
        var usage = parameter.Usage;
        if ((usage & MyBatisParameterUsage.Test) != 0)
        {
            parts.Add("test");
        }

        if ((usage & MyBatisParameterUsage.Foreach) != 0)
        {
            parts.Add("foreach");
        }

        if ((usage & MyBatisParameterUsage.Bind) != 0)
        {
            parts.Add("#{}");
        }

        if ((usage & MyBatisParameterUsage.Substitution) != 0)
        {
            parts.Add("${}");
        }

        if ((usage & MyBatisParameterUsage.BindTag) != 0)
        {
            parts.Add("bind");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 시나리오 값을 「바인드 변수」 창 값 칸 글로(<see cref="MyBatisBindText"/> 규칙). 칸에서 저장한 값은 칸 글 그대로이고,
    /// 다른 곳(로그 붙여 넣기 등)이 만든 종류 있는 값은 같은 뜻의 칸 글로 바꾼다: null = 빈 칸, 빈 글자 = <c>''</c>, 목록 = <c>['A', 10]</c>.
    /// </summary>
    public static string CellText(MyBatisScenarioValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            MyBatisScenarioValueKind.Null => string.Empty,
            MyBatisScenarioValueKind.List => "[" + string.Join(", ", (value.Items ?? []).Select(i => value.ItemKind == MyBatisScenarioValueKind.Text ? Quote(i) : i)) + "]",
            MyBatisScenarioValueKind.Text when string.IsNullOrEmpty(value.Text) => MyBatisBindText.EmptyString,
            _ => value.Text ?? string.Empty,
        };
    }

    /// <summary>값 칸 글을 시나리오 값으로(칸 글 그대로 저장, 빈 칸 = null).</summary>
    public static MyBatisScenarioValue FromCellText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? MyBatisScenarioValue.Null : new MyBatisScenarioValue(MyBatisScenarioValueKind.Text, text);

    /// <summary>
    /// 「바인드 변수」 창 행 이름. <c>${}</c> 에만 쓰인 이름은 앞에 <c>$</c> 를 붙여 표시한다. 행 이름은 창이 대문자로 바꾼다
    /// (<see cref="Oranda.App.Features.Binds.BindVariableRow.Normalize"/>), 그래서 계산 상태가 원래 이름과의 대응을 들고 있다.
    /// </summary>
    public static string RowName(MyBatisParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var substitutionOnly = (parameter.Usage & MyBatisParameterUsage.Substitution) != 0 && (parameter.Usage & MyBatisParameterUsage.Bind) == 0;
        return Oranda.App.Features.Binds.BindVariableRow.Normalize(substitutionOnly ? "$" + parameter.Name : parameter.Name);
    }

    /// <summary>
    /// 새 행의 처음 형식과 목록 여부. foreach collection = 목록. jdbcType 이 있으면 그 형식. test 에만 쓰였으면 식에서 추정한다:
    /// <c>true</c>·<c>false</c> 와 비교 = 참거짓, 수와 비교 = 숫자, <c>.size()</c>·<c>.isEmpty()</c> = 목록, 그 밖 = 글자.
    /// </summary>
    public static (Oranda.Core.Execution.BindDataType Type, bool IsList) GuessType(MyBatisParameter parameter, string statementText)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(statementText);
        var text = Oranda.Core.Execution.BindDataType.Varchar2;
        if ((parameter.Usage & MyBatisParameterUsage.Foreach) != 0)
        {
            return (text, true);
        }

        if (Run.MyBatisRunBinds.TypeOf(parameter.JdbcType) is { } jdbc)
        {
            return (jdbc, false);
        }

        if ((parameter.Usage & MyBatisParameterUsage.Test) == 0)
        {
            return (text, false);
        }

        foreach (var at in parameter.Locations)
        {
            if (at.End > statementText.Length)
            {
                continue;
            }

            var after = statementText.AsSpan(at.End, Math.Min(40, statementText.Length - at.End)).ToString();
            var before = statementText.AsSpan(Math.Max(0, at.Start - 40), Math.Min(40, at.Start)).ToString();
            if (SizeCall().IsMatch(after))
            {
                return (text, true);
            }

            if (BoolAfter().IsMatch(after) || BoolBefore().IsMatch(before))
            {
                return (Oranda.Core.Execution.BindDataType.Boolean, false);
            }

            if (NumberAfter().IsMatch(after) || NumberBefore().IsMatch(before))
            {
                return (Oranda.Core.Execution.BindDataType.Number, false);
            }
        }

        return (text, false);
    }

    private static string Quote(string? item) => item is null ? "null" : "'" + item.Replace("'", "''", StringComparison.Ordinal) + "'";

    private const string Compare = @"(==|!=|>=|<=|>|<|&gt;|&lt;|&gt;=|&lt;=|\b(?:eq|neq|gt|gte|lt|lte)\b)";

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\.\s*(size|isEmpty)\s*\(")]
    private static partial System.Text.RegularExpressions.Regex SizeCall();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*" + Compare + @"\s*(true|false)\b")]
    private static partial System.Text.RegularExpressions.Regex BoolAfter();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(true|false)\s*" + Compare + @"\s*$")]
    private static partial System.Text.RegularExpressions.Regex BoolBefore();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*" + Compare + @"\s*-?\d")]
    private static partial System.Text.RegularExpressions.Regex NumberAfter();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w.])-?\d+(\.\d+)?\s*" + Compare + @"\s*$")]
    private static partial System.Text.RegularExpressions.Regex NumberBefore();

    /// <summary>jdbcType 이 숫자 형인가(NUMERIC·DECIMAL·INTEGER·BIGINT·SMALLINT·TINYINT·DOUBLE·FLOAT·REAL).</summary>
    public static bool IsNumericJdbcType(string? jdbcType) =>
        jdbcType?.Trim().ToUpperInvariant() is "NUMERIC" or "DECIMAL" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "DOUBLE" or "FLOAT" or "REAL";

    private static object? Scalar(MyBatisScenarioValueKind kind, string? text) => kind switch
    {
        MyBatisScenarioValueKind.Null => null,
        MyBatisScenarioValueKind.Number when decimal.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        MyBatisScenarioValueKind.Boolean when bool.TryParse(text?.Trim(), out var b) => b,
        _ => text ?? string.Empty,
    };
}
