using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 대량 값(ODP.NET 형식)의 해석(<see cref="IBulkValueCodec"/>). 형식 목록은 <see cref="BulkBinds.ToDbType"/>, 키 글자는 <see cref="FailedRowKeys"/>
/// 를 그대로 감싼다. <see cref="Text"/>·<see cref="ToLong"/> 는 예전 <c>CopyCatalogQueries.Text</c>·<c>ToLong</c> 본문 그대로다(OracleDecimal 글자 불변).
/// </summary>
internal sealed class OracleBulkValueCodec : IBulkValueCodec
{
    public static readonly OracleBulkValueCodec Instance = new();

    public bool IsSupported(string serverType)
    {
        try
        {
            _ = BulkBinds.ToDbType(serverType);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public BulkKind KindOf(string serverType) => BulkBinds.ToDbType(serverType) switch
    {
        OracleDbType.Varchar2 or OracleDbType.NVarchar2 or OracleDbType.Char or OracleDbType.NChar => BulkKind.Text,
        OracleDbType.Clob or OracleDbType.NClob => BulkKind.Clob,
        OracleDbType.Blob => BulkKind.Blob,
        _ => BulkKind.Other,
    };

    public BindDataType BindTypeOf(string serverType) => FailedRowKeys.BindTypeOf(serverType);

    public string? ToKeyText(object? value, string serverType) => FailedRowKeys.ToText(value, serverType);

    public object? FromKeyText(string? text, string serverType) => FailedRowKeys.FromText(text, serverType);

    public long? ToInt64(object? value) => ToLong(value);

    public string? ToText(object? value) => Text(value);

    /// <summary>예전 실행기의 <c>n is OracleDecimal { IsNull: false } d ? d.ToString() : …</c> 그대로.</summary>
    public string? NumberText(object? value) => value is OracleDecimal { IsNull: false } d ? d.ToString() : null;

    /// <summary>예전 실행기의 <c>n is OracleDecimal { IsNull: false } d &amp;&amp; d.IsZero</c> 그대로.</summary>
    public bool IsZero(object? value) => value is OracleDecimal { IsNull: false } d && d.IsZero;

    public long ToInt64Exact(object? value) => value is OracleDecimal { IsNull: false } d
        ? ToLongExact(d)
        : throw new InvalidCastException("NUMBER 값이 아닙니다.");

    /// <summary>NUMBER → long. 범위를 넘으면 <see cref="OverflowException"/>(호출한 쪽이 알림으로 바꾼다). 예전 실행기 <c>ToLong(OracleDecimal)</c> 본문 그대로.</summary>
    internal static long ToLongExact(OracleDecimal value)
    {
        try
        {
            return (long)value;
        }
        catch (InvalidCastException ex)
        {
            throw new OverflowException("NUMBER 값이 long 범위를 넘습니다.", ex);
        }
    }

    /// <summary>카탈로그 조회 값 → 글자. OracleDecimal 은 ODP.NET 글자(<see cref="OracleDecimal.ToString()"/>) 그대로.</summary>
    internal static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        OracleDecimal d => d.IsNull ? null : d.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>NUMBER 결과(<see cref="OracleDecimal"/>)를 long 으로. NULL 이면 null, 범위를 넘으면 끝값으로 자른다.</summary>
    internal static long? ToLong(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case OracleDecimal d:
                if (d.IsNull)
                {
                    return null;
                }

                return d > long.MaxValue ? long.MaxValue : d < long.MinValue ? long.MinValue : (long)Math.Truncate(d.Value);
            case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                return (long)Math.Truncate(parsed);
            case IConvertible c:
                return Convert.ToInt64(c, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }
}
