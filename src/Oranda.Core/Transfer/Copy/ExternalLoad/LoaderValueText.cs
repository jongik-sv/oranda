using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>외부 도구로 적재할 열 하나. <see cref="TargetType"/> = 대상 서버 형식 이름(<c>NUMBER(10,2)</c>, <c>TIMESTAMP(6) WITH TIME ZONE</c> 등).</summary>
public sealed record LoaderColumn(string Name, string TargetType);

/// <summary>외부 도구 데이터 파일에서 열 값을 적는 방식(제어 파일의 필드 형식과 짝을 이룬다).</summary>
public enum LoaderFieldKind
{
    /// <summary>문자(VARCHAR2·NVARCHAR2·CHAR·NCHAR). 글자 그대로.</summary>
    Text,

    /// <summary>NUMBER·FLOAT 와 그 별칭. 고정소수점 글자.</summary>
    Number,

    /// <summary>DATE. <see cref="LoaderValueText.DateMask"/>.</summary>
    Date,

    /// <summary>TIMESTAMP. <see cref="LoaderValueText.TimestampMask"/>.</summary>
    Timestamp,

    /// <summary>TIMESTAMP WITH TIME ZONE. <see cref="LoaderValueText.TimestampTzMask"/>.</summary>
    TimestampTz,

    /// <summary>INTERVAL YEAR TO MONTH. <c>TO_YMINTERVAL</c> 이 받는 글자(<c>+02-03</c>).</summary>
    IntervalYm,

    /// <summary>INTERVAL DAY TO SECOND. <c>TO_DSINTERVAL</c> 이 받는 글자(<c>+01 02:03:04.123456789</c>).</summary>
    IntervalDs,

    /// <summary>RAW. 대문자 16진 글자.</summary>
    Raw,

    /// <summary>BINARY_FLOAT. 왕복 글자(<c>NaN</c>·<c>Inf</c>·<c>-Inf</c> 포함).</summary>
    BinaryFloat,

    /// <summary>BINARY_DOUBLE. 왕복 글자(<c>NaN</c>·<c>Inf</c>·<c>-Inf</c> 포함).</summary>
    BinaryDouble,

    /// <summary>CLOB·NCLOB. 값마다 LOB 파일 하나(UTF-8).</summary>
    Clob,

    /// <summary>BLOB. 값마다 LOB 파일 하나(바이트 그대로).</summary>
    Blob,
}

/// <summary>대상 형식 이름을 읽은 결과. <see cref="Length"/> = 괄호 안 첫 수(<c>VARCHAR2(20 CHAR)</c> → 20), 없으면 null.</summary>
public readonly partial record struct LoaderFieldType(LoaderFieldKind Kind, int? Length)
{
    public bool IsLob => Kind is LoaderFieldKind.Clob or LoaderFieldKind.Blob;

    /// <summary>
    /// 대상 형식 이름 → 적는 방식. 외부 도구 경로가 다루지 않는 형식(LONG·LONG RAW·XMLTYPE·객체/사용자 정의 형식·ROWID·UROWID·BFILE·
    /// TIMESTAMP WITH LOCAL TIME ZONE 등)이면 false.
    /// </summary>
    public static bool TryParse(string? targetType, out LoaderFieldType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return false;
        }

        var upper = targetType.Trim().ToUpperInvariant();
        int? length = null;
        var open = upper.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            var m = FirstInteger().Match(upper, open);
            if (m.Success && int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                length = n;
            }
        }

        // 괄호(정밀도·길이)를 걷어 낸 이름: TIMESTAMP(6) WITH TIME ZONE → TIMESTAMP WITH TIME ZONE.
        var name = Spaces().Replace(Parentheses().Replace(upper, " "), " ").Trim();
        LoaderFieldKind? kind = name switch
        {
            "VARCHAR2" or "VARCHAR" or "NVARCHAR2" or "CHAR" or "NCHAR" or "CHARACTER" or "CHAR VARYING"
                or "CHARACTER VARYING" or "NATIONAL CHAR" or "NATIONAL CHARACTER" or "NCHAR VARYING" => LoaderFieldKind.Text,
            "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "DEC" or "NUMERIC" or "REAL"
                or "DOUBLE PRECISION" => LoaderFieldKind.Number,
            "DATE" => LoaderFieldKind.Date,
            "TIMESTAMP" => LoaderFieldKind.Timestamp,
            "TIMESTAMP WITH TIME ZONE" => LoaderFieldKind.TimestampTz,
            "INTERVAL YEAR TO MONTH" => LoaderFieldKind.IntervalYm,
            "INTERVAL DAY TO SECOND" => LoaderFieldKind.IntervalDs,
            "RAW" => LoaderFieldKind.Raw,
            "BINARY_FLOAT" => LoaderFieldKind.BinaryFloat,
            "BINARY_DOUBLE" => LoaderFieldKind.BinaryDouble,
            "CLOB" or "NCLOB" => LoaderFieldKind.Clob,
            "BLOB" => LoaderFieldKind.Blob,
            _ => null,
        };

        if (kind is null)
        {
            return false;
        }

        type = new LoaderFieldType(kind.Value, length);
        return true;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstInteger();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parentheses();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// 이식 값(<see cref="CrossDbms"/> 의 PortableValues) → 외부 도구 데이터 파일의 필드 글자. 문화권과 무관(invariant)하다.
/// 받는 이식 값은 Oracle 배열 바인드(<c>OraclePortable.FromPortable</c>)와 같다: 같은 DBMS 복사·교차 복사 모두 원본 코덱의 <c>ToPortable</c> 결과를 그대로 넘긴다.
/// 실패하면 false 와 고정된 오류 글(열 이름·형식만, 값은 넣지 않는다 — 값은 민감할 수 있다). 그 행만 실패한다.
/// </summary>
public static class LoaderValueText
{
    /// <summary>DATE 필드 마스크(제어 파일).</summary>
    public const string DateMask = "YYYY-MM-DD HH24:MI:SS";

    /// <summary>TIMESTAMP 필드 마스크(초 아래 9자리).</summary>
    public const string TimestampMask = "YYYY-MM-DD HH24:MI:SS.FF9";

    /// <summary>TIMESTAMP WITH TIME ZONE 필드 마스크. 시간대는 오프셋(<c>+09:00</c>) 또는 지역 이름(<c>Asia/Seoul</c>).</summary>
    public const string TimestampTzMask = "YYYY-MM-DD HH24:MI:SS.FF9 TZR";

    private const long NanosPerSecond = 1_000_000_000L;

    /// <summary>외부 도구 경로가 이 대상 형식을 적을 수 있는지. false 면 사전 점검이 그 테이블을 기본 방식으로 돌린다.</summary>
    public static bool Supports(string? targetType) => LoaderFieldType.TryParse(targetType, out _);

    /// <summary>
    /// 이식 값 하나 → 필드 글자. null(NULL) 이면 <paramref name="text"/> = null 로 true. CLOB·NCLOB 은 값 글자 그대로(LOB 파일에 적을 글자),
    /// BLOB 은 글자로 적지 않으므로 false(<see cref="LoaderDataWriter"/> 가 바이트를 LOB 파일에 적는다).
    /// </summary>
    public static bool TryFormat(object? value, string column, string targetType, out string? text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!LoaderFieldType.TryParse(targetType, out var type))
        {
            text = null;
            error = UnsupportedType(column, targetType);
            return false;
        }

        return TryFormat(value, column, targetType, type, out text, out error);
    }

    internal static string UnsupportedType(string column, string? targetType) =>
        $"열 {column}({targetType}): 외부 도구 적재가 다루지 않는 형식입니다.";

    internal static string Mismatch(string column, string targetType, object value) =>
        $"열 {column}({targetType}): 이 형식에 맞지 않는 값입니다({value.GetType().Name}).";

    internal static bool TryFormat(object? value, string column, string targetType, LoaderFieldType type, out string? text, out string? error)
    {
        text = null;
        error = null;
        if (value is null or DBNull)
        {
            return true;
        }

        switch (type.Kind)
        {
            case LoaderFieldKind.Text:
            case LoaderFieldKind.Clob:
                if (value is string s)
                {
                    text = s;
                    return true;
                }

                break;
            case LoaderFieldKind.Number:
                switch (value)
                {
                    case PortableNumber n:
                        text = n.Text;
                        return true;
                    case PortableBinaryFloat bf:
                        return TryFiniteNumber(bf.Value, single: true, column, targetType, out text, out error);
                    case PortableBinaryDouble bd:
                        return TryFiniteNumber(bd.Value, single: false, column, targetType, out text, out error);
                }

                break;
            case LoaderFieldKind.Date:
                if (value is DateTime dt)
                {
                    text = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    return true;
                }

                break;
            case LoaderFieldKind.Timestamp:
                if (value is PortableTimestamp ts)
                {
                    text = FormatTimestamp(ts);
                    return true;
                }

                break;
            case LoaderFieldKind.TimestampTz:
                if (value is PortableTimestampTz tz)
                {
                    // 지역 이름이 있으면 지역(대상이 지역을 잃지 않게), 없으면 오프셋.
                    text = FormatTimestamp(tz.Local) + " " + (tz.Region ?? tz.Offset);
                    return true;
                }

                break;
            case LoaderFieldKind.IntervalYm:
                if (value is PortableIntervalYm ym)
                {
                    text = FormatIntervalYm(ym.Months);
                    return true;
                }

                break;
            case LoaderFieldKind.IntervalDs:
                if (value is PortableIntervalDs ds)
                {
                    text = FormatIntervalDs(ds.Nanoseconds);
                    return true;
                }

                break;
            case LoaderFieldKind.Raw:
                if (value is byte[] bytes)
                {
                    text = Convert.ToHexString(bytes);
                    return true;
                }

                break;
            case LoaderFieldKind.BinaryFloat:
                switch (value)
                {
                    case PortableBinaryFloat bf:
                        text = FormatSingle(bf.Value);
                        return true;
                    case PortableBinaryDouble bd:
                        text = FormatSingle((float)bd.Value);
                        return true;
                }

                break;
            case LoaderFieldKind.BinaryDouble:
                switch (value)
                {
                    case PortableBinaryDouble bd:
                        text = FormatDouble(bd.Value);
                        return true;
                    case PortableBinaryFloat bf:
                        text = FormatDouble(bf.Value);
                        return true;
                }

                break;
            case LoaderFieldKind.Blob:
                break;
        }

        error = Mismatch(column, targetType, value);
        return false;
    }

    /// <summary>TIMESTAMP 글자: <c>yyyy-MM-dd HH:mm:ss.fffffffff</c>(초 아래 9자리).</summary>
    public static string FormatTimestamp(PortableTimestamp value) =>
        value.Seconds.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "."
        + value.Nanos.ToString("D9", CultureInfo.InvariantCulture);

    /// <summary>INTERVAL YEAR TO MONTH 글자: <c>+02-03</c>, <c>-01-11</c>.</summary>
    public static string FormatIntervalYm(long months)
    {
        var sign = months < 0 ? '-' : '+';
        var abs = months < 0 ? (ulong)(-(months + 1)) + 1 : (ulong)months;
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / 12:D2}-{abs % 12:D2}");
    }

    /// <summary>INTERVAL DAY TO SECOND 글자: <c>+01 02:03:04.123456789</c>.</summary>
    public static string FormatIntervalDs(Int128 nanoseconds)
    {
        var sign = nanoseconds < 0 ? '-' : '+';
        var abs = Int128.Abs(nanoseconds);
        var fraction = (long)(abs % NanosPerSecond);
        var totalSeconds = (long)(abs / NanosPerSecond);
        var days = totalSeconds / 86_400;
        var rest = totalSeconds % 86_400;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{days:D2} {rest / 3600:D2}:{rest % 3600 / 60:D2}:{rest % 60:D2}.{fraction:D9}");
    }

    /// <summary>BINARY_FLOAT 글자: 왕복(<c>R</c>), <c>NaN</c>·<c>Inf</c>·<c>-Inf</c>.</summary>
    public static string FormatSingle(float value) =>
        float.IsNaN(value) ? "NaN"
        : float.IsPositiveInfinity(value) ? "Inf"
        : float.IsNegativeInfinity(value) ? "-Inf"
        : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>BINARY_DOUBLE 글자: 왕복(<c>R</c>), <c>NaN</c>·<c>Inf</c>·<c>-Inf</c>.</summary>
    public static string FormatDouble(double value) =>
        double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "Inf"
        : double.IsNegativeInfinity(value) ? "-Inf"
        : value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryFiniteNumber(double value, bool single, string column, string targetType, out string? text, out string? error)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            text = null;
            error = $"열 {column}({targetType}): NaN·Inf 는 NUMBER 에 넣을 수 없습니다.";
            return false;
        }

        // BINARY_FLOAT 은 float 의 왕복 글자로(1.1f → 1.1, double 로 넓히면 1.10000002…), 배열 바인드(FromSingle)와 같다.
        text = (single ? PortableNumber.FromSingle((float)value) : PortableNumber.FromDouble(value)).Text;
        error = null;
        return true;
    }
}
