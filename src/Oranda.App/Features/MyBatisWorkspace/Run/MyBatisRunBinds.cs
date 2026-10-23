using System.Globalization;
using Oranda.App.Features.Binds;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.Core.Execution;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Run;

/// <summary>
/// 계산기가 낸 바인드(<see cref="MyBatisBind"/>)를 실행 엔진의 바인드 값(<see cref="BindValue"/>)으로 바꾼다(MB-05, design.md 4.1).
/// 같은 파라미터는 같은 이름으로 여러 번 나오므로 이름으로 합친다(대소문자 무시, 값은 마지막 것).
/// jdbcType 이 있으면 형식만 정하고 값 변환은 일반 탭의 바인드 값과 같게 <see cref="BindInput"/> 에 맡긴다(DATE·TIMESTAMP 글자 규칙을 맞춘다).
/// jdbcType 이 없으면 값의 형으로 고른다. 11g SQL 에는 BOOLEAN 이 없으므로 참거짓은 숫자 1/0 이다.
/// </summary>
public static class MyBatisRunBinds
{
    /// <summary>
    /// 바인드 이름 → 값 사전. 못 바꾼 바인드가 있으면 <paramref name="error"/> 에 이유를 담고 null.
    /// <paramref name="rowType"/> = 파라미터 경로 → 「바인드 변수」 창 행 형식(사용자가 고른 것이 jdbcType 보다 먼저). 목록 항목이면 null.
    /// </summary>
    public static Dictionary<string, BindValue>? Build(IReadOnlyList<MyBatisBind> binds, out string? error, Func<string, BindDataType?>? rowType = null)
    {
        ArgumentNullException.ThrowIfNull(binds);
        var result = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var bind in binds)
        {
            if (ToBindValue(bind, out error, rowType) is not { } value)
            {
                return null;
            }

            result[bind.Name] = value;
        }

        error = null;
        return result;
    }

    /// <summary>바인드 하나의 값. 못 바꾸면 <paramref name="error"/> 에 이유를 담고 null.</summary>
    public static BindValue? ToBindValue(MyBatisBind bind, out string? error, Func<string, BindDataType?>? rowType = null)
    {
        ArgumentNullException.ThrowIfNull(bind);
        if ((rowType?.Invoke(bind.Parameter) ?? TypeOf(bind.JdbcType)) is { } type)
        {
            if (!TextOf(bind.Value, out var text, out var isNull))
            {
                error = $":{bind.Name} 의 값이 목록입니다. 항목 하나씩 쓰는 바인드여야 합니다.";
                return null;
            }

            var input = new BindInput(bind.Name) { Type = type, Text = text, IsNull = isNull }.ToBindValue(out error);

            // 참거짓 형식도 글자 규칙(Y·true·1)만 일반 탭과 같게 읽고 숫자 1/0 으로 보낸다(11g SQL 에는 BOOLEAN 이 없다. 값 넣은 글 복사와 같다).
            return input is { Type: BindDataType.Boolean } ? input with { Type = BindDataType.Number, Value = input.Value is bool b ? (b ? 1m : 0m) : null } : input;
        }

        return ByValue(bind, out error);
    }

    /// <summary>jdbcType 에 대응하는 바인드 형식. 모르는 jdbcType 이면 null(값의 형으로 고른다).</summary>
    public static BindDataType? TypeOf(string? jdbcType)
    {
        var name = jdbcType?.Trim().ToUpperInvariant();
        if (MyBatisValues.IsNumericJdbcType(name))
        {
            return BindDataType.Number;
        }

        return name switch
        {
            "VARCHAR" or "CHAR" or "LONGVARCHAR" => BindDataType.Varchar2,
            "NVARCHAR" or "NCHAR" => BindDataType.NVarchar2,
            "DATE" => BindDataType.Date,
            "TIMESTAMP" => BindDataType.Timestamp,
            "CLOB" or "NCLOB" => BindDataType.Clob,
            "BOOLEAN" or "BIT" => BindDataType.Boolean,
            _ => null,
        };
    }

    // 값을 값 칸 글자로(일반 탭 바인드 값 칸과 같은 모양). 목록이면 false.
    private static bool TextOf(object? value, out string text, out bool isNull)
    {
        isNull = value is null;
        text = string.Empty;
        switch (value)
        {
            case null:
                return true;
            case string s:
                text = s;
                return true;
            case bool b:
                text = b ? "true" : "false";
                return true;
            case DateTime dt:
                text = dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
                return true;
            case System.Collections.IEnumerable:
                return false;
            default:
                text = System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
        }
    }

    private static BindValue? ByValue(MyBatisBind bind, out string? error)
    {
        error = null;
        switch (bind.Value)
        {
            case null:
                return new BindValue(BindDataType.Varchar2, null);
            case string s:
                return new BindValue(BindDataType.Varchar2, s);
            case bool b:
                return new BindValue(BindDataType.Number, b ? 1m : 0m);
            case decimal d:
                return new BindValue(BindDataType.Number, d);
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                return new BindValue(BindDataType.Number, System.Convert.ToDecimal(bind.Value, CultureInfo.InvariantCulture));
            case double or float:
                try
                {
                    return new BindValue(BindDataType.Number, System.Convert.ToDecimal(bind.Value, CultureInfo.InvariantCulture));
                }
                catch (OverflowException)
                {
                    error = $":{bind.Name} 값 '{System.Convert.ToString(bind.Value, CultureInfo.InvariantCulture)}' 을(를) 숫자로 읽을 수 없습니다.";
                    return null;
                }

            case DateTime dt:
                return new BindValue(BindDataType.Timestamp, dt);
            case DateTimeOffset o:
                return new BindValue(BindDataType.TimestampTz, o);
            default:
                error = $":{bind.Name} 의 값 형식({bind.Value.GetType().Name})을 바인드로 쓸 수 없습니다. 항목 하나씩 쓰는 바인드여야 합니다.";
                return null;
        }
    }
}
