using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>셀 값으로 거르는 방식(오른쪽 클릭 메뉴 「이 값으로 거르기」·「이 값 빼고 거르기」·「NULL 인 행만」).</summary>
public enum CellFilterKind
{
    /// <summary>같은 값만(값이 NULL 이면 <c>IS NULL</c>).</summary>
    Equal,

    /// <summary>그 값만 빼고(값이 NULL 이면 <c>IS NOT NULL</c>). <c>&lt;&gt;</c> 는 NULL 행도 뺀다(SQL 규칙).</summary>
    NotEqual,

    /// <summary>그 열이 NULL 인 행만.</summary>
    IsNull,
}

/// <summary>
/// 결과 거르기 요청(GRID-10): 결과를 만든 질의 <see cref="Query"/> 를 <c>SELECT * FROM (…) WHERE … ORDER BY …</c> 로 감싸 다시 실행한다.
/// <see cref="Binds"/> 는 셀 값 조건이 쓴 바인드(이 결과 영역이 가진 값)이고, 나머지 바인드 이름은 「바인드 변수」 목록에서 읽는다.
/// 조건·정렬이 모두 비면 원래 질의를 그대로 다시 실행한다(거르기 지우기).
/// </summary>
public sealed record ResultFilterRequest(SqlStatement Query, string Where, string OrderBy)
{
    private static readonly Dictionary<string, BindValue> NoBinds = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, BindValue> Binds { get; init; } = NoBinds;

    /// <summary>조건도 정렬도 없다(원래 질의 그대로).</summary>
    public bool IsEmpty => Where.Length == 0 && OrderBy.Length == 0;
}

/// <summary>결과 거르기 줄의 글 다루기: 셀 값 조건(값은 바인드로만), 조건 잇기, 열 이름 따옴표, 바인드 이름 짓기.</summary>
public static partial class ResultFilter
{
    /// <summary>셀 값 조건이 쓰는 바인드 이름 앞머리(<c>:oranda_f1</c>…). 사용자의 바인드와 겹치지 않게 길게 둔다.</summary>
    public const string BindPrefix = "oranda_f";

    /// <summary>최근 조건 목록 길이.</summary>
    public const int HistorySize = 10;

    /// <summary>
    /// 셀 값 조건을 만든다. 값은 절대 본문에 이어 붙이지 않고 <paramref name="bindName"/> 바인드로 넘긴다.
    /// 값이 NULL 이면 <c>=</c> → <c>IS NULL</c>, <c>&lt;&gt;</c> → <c>IS NOT NULL</c> 로 바꾼다(<c>= NULL</c> 은 아무 행도 돌려주지 않는다).
    /// LOB·JSON·VECTOR 처럼 같음 비교를 할 수 없는 열은 <c>false</c> 와 이유(「NULL 인 행만」은 어느 열이나 된다).
    /// </summary>
    public static bool TryCellCondition(ResultColumn column, object? value, CellFilterKind kind, string bindName,
                                        out string condition, out BindValue? bind, out string? error)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentException.ThrowIfNullOrEmpty(bindName);
        condition = string.Empty;
        bind = null;
        error = null;
        if (QuoteColumn(column.Name) is not { } name)
        {
            error = $"열 이름 {column.Name} 은(는) 조건에 쓸 수 없습니다.";
            return false;
        }

        if (kind == CellFilterKind.IsNull || value is null or DBNull)
        {
            condition = name + (kind == CellFilterKind.NotEqual ? " IS NOT NULL" : " IS NULL");
            return true;
        }

        if (ToBind(column, value) is not { } b)
        {
            error = $"{column.DbTypeName} 열은 값으로 거를 수 없습니다(「NULL 인 행만」은 쓸 수 있습니다).";
            return false;
        }

        condition = name + (kind == CellFilterKind.NotEqual ? " <> :" : " = :") + bindName;
        bind = b;
        return true;
    }

    /// <summary>
    /// 결과 열 이름을 조건에 쓸 이름으로: 대문자 일반 식별자이고 예약어가 아니면 그대로, 아니면 큰따옴표로 감싼다
    /// (<c>SAL*2</c> 같은 식 이름도 인라인 뷰의 열 이름이라 따옴표로 쓸 수 있다). 따옴표로도 쓸 수 없으면 null.
    /// </summary>
    public static string? QuoteColumn(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('"', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        return Oranda.Language.Editing.RowDmlBuilder.QuoteIdentifier(name);
    }

    /// <summary>
    /// 셀 값 → 바인드 값. 형식은 열 분류로 정한다(DATE 열은 DATE 로 묶어야 열 쪽 형 변환 없이 색인을 쓴다).
    /// 같음 비교를 할 수 없는 분류(LOB·LONG·JSON·VECTOR·XML·사용자 정의 형식·간격 등)는 null.
    /// </summary>
    public static BindValue? ToBind(ResultColumn column, object value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        var type = column.DbTypeName.ToUpperInvariant();
        return column.Kind switch
        {
            ColumnDataKind.Text when type is "LONG" => null,
            ColumnDataKind.Text when value is string s && type is "NVARCHAR2" or "NCHAR" => new BindValue(BindDataType.NVarchar2, s),
            ColumnDataKind.Text or ColumnDataKind.RowId when value is string s => new BindValue(BindDataType.Varchar2, s),
            ColumnDataKind.Number => value switch
            {
                decimal d => new BindValue(BindDataType.Number, d),
                string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => new BindValue(BindDataType.Number, d),

                // decimal 범위를 넘는 원시 숫자 글: 문자로 묶으면 서버가 열 형식(NUMBER)으로 바꿔 비교한다(정밀도 그대로).
                string s => new BindValue(BindDataType.Varchar2, s),
                IConvertible c => new BindValue(BindDataType.Number, c.ToDecimal(CultureInfo.InvariantCulture)),
                _ => null,
            },
            ColumnDataKind.BinaryFloat when value is float or double => new BindValue(BindDataType.BinaryDouble, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            ColumnDataKind.Date when value is DateTime dt => new BindValue(BindDataType.Date, dt),
            ColumnDataKind.Timestamp when value is DateTime dt => new BindValue(BindDataType.Timestamp, dt),
            ColumnDataKind.TimestampTz when value is DateTimeOffset dto => new BindValue(BindDataType.TimestampTz, dto),
            ColumnDataKind.Raw when value is byte[] bytes => new BindValue(BindDataType.Raw, bytes),
            ColumnDataKind.Boolean when value is bool flag => new BindValue(BindDataType.Boolean, flag),
            _ => null,
        };
    }

    /// <summary>
    /// 지금 조건 뒤에 새 조건을 <c>AND</c> 로 잇는다. 지금 조건에 <c>OR</c> 가 있으면 괄호로 감싸 우선순위를 지킨다.
    /// </summary>
    public static string Combine(string? existing, string added)
    {
        ArgumentException.ThrowIfNullOrEmpty(added);
        var current = existing?.Trim() ?? string.Empty;
        if (current.Length == 0)
        {
            return added;
        }

        return (OrWord().IsMatch(current) ? "(" + current + ")" : current) + " AND " + added;
    }

    /// <summary>
    /// 새 셀 값 바인드 이름: <c>oranda_f1</c>, <c>oranda_f2</c>… 중 <paramref name="taken"/>(원래 문장의 바인드, 이미 쓴 이름)에 없는 첫 이름.
    /// </summary>
    public static string NextBindName(IEnumerable<string> taken, ref int counter)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string name;
        do
        {
            name = BindPrefix + (++counter).ToString(CultureInfo.InvariantCulture);
        }
        while (used.Contains(name));

        return name;
    }

    [GeneratedRegex(@"\bOR\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrWord();
}
