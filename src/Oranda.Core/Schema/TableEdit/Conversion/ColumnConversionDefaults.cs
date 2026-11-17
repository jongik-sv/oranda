using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit.Conversion;

/// <summary>
/// 기본 변환식이 집합 검사(Oracle 12.2+ <c>VALIDATE_CONVERSION</c>)로 셀 수 있는 꼴일 때의 검사 정보.
/// <see cref="TypeName"/> = <c>VALIDATE_CONVERSION(c AS 형식 …)</c> 의 형식(정밀도 없이 — 서버가 받지 않음, ORA-43910), <see cref="Format"/> = 날짜 형식 리터럴 안 글.
/// NUMBER 의 정밀도·소수는 <see cref="Precision"/>·<see cref="Scale"/> 로 넘침 조건을 따로 붙인다.
/// </summary>
public sealed record ConversionValidation(string TypeName, string? Format = null, int? Precision = null, int? Scale = null);

/// <summary>
/// 형식 쌍별 기본 변환식(설계 18.9: 기본 <c>CAST(c AS 새 형식)</c>, 형식 쌍에 맞는 기본값). 사용자가 창에서 고친다.
/// 문자 형식으로 가는 변환에는 <c>CAST</c> 를 쓰지 않는다 — Oracle 의 <c>CAST('abcd' AS VARCHAR2(3))</c> 는 오류 없이 잘라 버린다(실측).
/// 날짜 형식은 <see cref="DateFormat"/>·<see cref="TimestampFormat"/>(짧은 값도 받음: <c>'2026-01-05'</c> 는 시각 0시).
/// </summary>
public static class ColumnConversionDefaults
{
    public const string DateFormat = "YYYY-MM-DD HH24:MI:SS";

    public const string TimestampFormat = "YYYY-MM-DD HH24:MI:SS.FF";

    public const string TimestampTzFormat = "YYYY-MM-DD HH24:MI:SS.FF TZH:TZM";

    /// <summary>기본 변환식. 열 이름은 <paramref name="isReservedWord"/> 규칙으로 필요할 때만 따옴표.</summary>
    /// <param name="source">지금 열</param>
    /// <param name="target">새 정의</param>
    public static string Expression(ColumnDraft source, ColumnDraft target, Func<string, bool>? isReservedWord = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var c = SqlIdentifier.Quote(source.Name, isReservedWord ?? TableEditDefaults.IsReservedWord);
        var from = ColumnTypeText.Family(source.DataType);
        var to = ColumnTypeText.Family(target.DataType);
        var type = ColumnTypeText.Statement(target);

        if (IsCharacter(from))
        {
            switch (to)
            {
                case "NUMBER" or "FLOAT":
                    return $"TO_NUMBER({c})";
                case "BINARY_DOUBLE":
                    return $"TO_BINARY_DOUBLE({c})";
                case "BINARY_FLOAT":
                    return $"TO_BINARY_FLOAT({c})";
                case "DATE":
                    return $"TO_DATE({c}, '{DateFormat}')";
                case "TIMESTAMP" or "TIMESTAMP LTZ":
                    return $"TO_TIMESTAMP({c}, '{TimestampFormat}')";
                case "TIMESTAMP TZ":
                    return $"TO_TIMESTAMP_TZ({c}, '{TimestampTzFormat}')";
                case "RAW":
                    return $"HEXTORAW({c})";
            }
        }

        return to switch
        {
            "CLOB" => $"TO_CLOB({c})",
            "NCLOB" => $"TO_NCLOB({c})",
            "NVARCHAR2" or "NCHAR" => IsNational(from) ? c : $"TO_NCHAR({c})",
            "VARCHAR2" or "CHAR" => from switch
            {
                "VARCHAR2" or "CHAR" => c,
                "RAW" => $"RAWTOHEX({c})",
                "DATE" => $"TO_CHAR({c}, '{DateFormat}')",
                "TIMESTAMP" or "TIMESTAMP LTZ" => $"TO_CHAR({c}, '{TimestampFormat}')",
                "TIMESTAMP TZ" => $"TO_CHAR({c}, '{TimestampTzFormat}')",
                _ => $"TO_CHAR({c})",
            },
            _ when type.Length == 0 => c,
            _ => $"CAST({c} AS {type})",
        };
    }

    /// <summary>
    /// <paramref name="expression"/> 가 기본 변환식과 같고(비교 표기) 집합 검사로 셀 수 있는 쌍(문자 → 숫자·날짜·시각)이면 검사 정보, 아니면 null(행 단위 시험 질의).
    /// </summary>
    public static ConversionValidation? Validation(ColumnDraft source, ColumnDraft target, string expression, Func<string, bool>? isReservedWord = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!IsCharacter(ColumnTypeText.Family(source.DataType))
            || TableEditExpression.Canonical(expression) != TableEditExpression.Canonical(Expression(source, target, isReservedWord)))
        {
            return null;
        }

        return ColumnTypeText.Family(target.DataType) switch
        {
            "NUMBER" => new ConversionValidation("NUMBER", null, target.Precision, target.Precision is null ? null : target.Scale ?? 0),
            "FLOAT" => new ConversionValidation("NUMBER"),
            "BINARY_DOUBLE" => new ConversionValidation("BINARY_DOUBLE"),
            "BINARY_FLOAT" => new ConversionValidation("BINARY_FLOAT"),
            "DATE" => new ConversionValidation("DATE", DateFormat),
            "TIMESTAMP" or "TIMESTAMP LTZ" => new ConversionValidation("TIMESTAMP", TimestampFormat),
            "TIMESTAMP TZ" => new ConversionValidation("TIMESTAMP WITH TIME ZONE", TimestampTzFormat),
            _ => null,
        };
    }

    private static bool IsCharacter(string family) => family is "VARCHAR2" or "CHAR" or "NVARCHAR2" or "NCHAR";

    private static bool IsNational(string family) => family is "NVARCHAR2" or "NCHAR";
}
