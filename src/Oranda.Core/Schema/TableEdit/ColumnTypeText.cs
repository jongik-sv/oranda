using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 편집 열의 형식 표기(TE-32). 데이터 복사의 <c>CreateTableDdl.TypeText</c> 는 늘 <c>n CHAR</c> 를 쓰지만, 편집기는 길이 기준을 모델에 있는 대로만 쓴다
/// (새 열 = 생략해 세션 기본값, 기존 열 = 원래 기준, TE-6·설계 12.1). <see cref="Statement"/> = 문장 표기, <see cref="Canonical"/> = 비교 표기(설계 8.4).
/// </summary>
internal static partial class ColumnTypeText
{
    /// <summary>문장에 쓰는 형식. 형식이 비었으면 빈 문자열(가상 열은 서버가 정함).</summary>
    public static string Statement(ColumnDraft c)
    {
        var t = Clean(c.DataType);
        if (t.Length == 0 || t.Contains('(', StringComparison.Ordinal) || t.Contains('"', StringComparison.Ordinal))
        {
            return t;
        }

        return t switch
        {
            "VARCHAR2" or "VARCHAR" or "CHAR" when c.Length is { } n => $"{(t == "VARCHAR" ? "VARCHAR2" : t)}({N(n)}{Semantics(c.CharSemantics)})",
            "NVARCHAR2" or "NVARCHAR" or "NCHAR" or "RAW" or "UROWID" when c.Length is { } n => $"{(t == "NVARCHAR" ? "NVARCHAR2" : t)}({N(n)})",
            "NUMBER" => Number(c.Precision, c.Scale),
            "FLOAT" when c.Precision is { } p => $"FLOAT({N(p)})",
            _ => t,
        };
    }

    /// <summary>비교 표기: 같은 뜻의 다른 표기(대소문자·<c>NUMBER(*,0)</c>·INTEGER·VARCHAR·기본 길이·기본 소수 자리)를 하나로.</summary>
    public static string Canonical(ColumnDraft c)
    {
        var t = Clean(c.DataType);
        if (t.Contains('"', StringComparison.Ordinal))
        {
            return t;
        }

        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            return TimestampDefault().Replace(t, "TIMESTAMP(6)", 1);
        }

        if (t.StartsWith("INTERVAL", StringComparison.Ordinal))
        {
            return SecondDefault().Replace(YearOrDayDefault().Replace(t, "$1(2)"), "SECOND(6)");
        }

        if (t.Contains('(', StringComparison.Ordinal))
        {
            return t;
        }

        return t switch
        {
            "VARCHAR2" or "VARCHAR" => $"VARCHAR2({N(c.Length ?? 0)}{Semantics(c.CharSemantics)})",
            "CHAR" => $"CHAR({N(c.Length ?? 1)}{Semantics(c.CharSemantics)})",
            "NVARCHAR2" or "NVARCHAR" => $"NVARCHAR2({N(c.Length ?? 0)})",
            "NCHAR" => $"NCHAR({N(c.Length ?? 1)})",
            "RAW" or "UROWID" => $"{t}({N(c.Length ?? 0)})",
            "INTEGER" or "INT" or "SMALLINT" => "NUMBER(*,0)",
            "NUMBER" or "DECIMAL" or "DEC" or "NUMERIC" => Number(c.Precision, c.Scale),
            "FLOAT" => $"FLOAT({N(c.Precision ?? 126)})",
            "REAL" => "FLOAT(63)",
            "DOUBLE PRECISION" => "FLOAT(126)",
            _ => t,
        };
    }

    /// <summary>문자 형식(길이 기준을 가지는 형식)인가.</summary>
    public static bool HasSemantics(string? dataType) => Clean(dataType) is "VARCHAR2" or "VARCHAR" or "CHAR";

    /// <summary>비교·검사용 형식 이름(괄호·뒤 수식 앞까지, 같은 뜻 이름은 하나로).</summary>
    public static string Family(string? dataType)
    {
        var t = Clean(dataType);
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        var head = paren >= 0 ? t[..paren] : t;
        return head switch
        {
            "VARCHAR" => "VARCHAR2",
            "NVARCHAR" => "NVARCHAR2",
            "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "DEC" or "NUMERIC" => "NUMBER",
            "REAL" or "DOUBLE PRECISION" => "FLOAT",
            _ when head.StartsWith("TIMESTAMP", StringComparison.Ordinal) => t.Contains("LOCAL", StringComparison.Ordinal) ? "TIMESTAMP LTZ"
                : t.Contains("TIME ZONE", StringComparison.Ordinal) ? "TIMESTAMP TZ" : "TIMESTAMP",
            _ => head,
        };
    }

    private static string Number(int? precision, int? scale) => (precision, scale) switch
    {
        ({ } p, { } s) when s != 0 => $"NUMBER({N(p)},{N(s)})",
        ({ } p, _) => $"NUMBER({N(p)})",
        (null, 0) => "NUMBER(*,0)",
        (null, { } s) => $"NUMBER(*,{N(s)})",
        _ => "NUMBER",
    };

    private static string Semantics(CharSemantics? semantics) => semantics switch
    {
        CharSemantics.Bytes => " BYTE",
        CharSemantics.Characters => " CHAR",
        _ => string.Empty,
    };

    /// <summary>앞뒤 공백을 떼고 공백을 하나로, 괄호 앞 공백을 없애고, 따옴표가 없으면 대문자로.</summary>
    private static string Clean(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return string.Empty;
        }

        var t = Spaces().Replace(dataType.Trim(), " ").Replace(" (", "(", StringComparison.Ordinal);
        return t.Contains('"', StringComparison.Ordinal) ? t : t.ToUpperInvariant();
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^TIMESTAMP(?!\()")]
    private static partial Regex TimestampDefault();

    [GeneratedRegex(@"\b(YEAR|DAY)(?!\()")]
    private static partial Regex YearOrDayDefault();

    [GeneratedRegex(@"\bSECOND(?!\()")]
    private static partial Regex SecondDefault();
}
