using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Oranda.Core.Catalog.Providers;

/// <summary>
/// 카탈로그 질의 결과 값(<c>DbDataReader.GetValue</c> 의 CLR 값)을 형에 맞게 읽는 도우미. null → null, 문화권 무관(Invariant).
/// 서비스 구현이 DBMS 마다 같은 모양으로 쓴다.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "행 값 도우미 이름(Int·Long·Double)은 P0 설계 계약이고 세 서비스가 쓰던 이름 그대로다.")]
public static class CatalogValues
{
    public static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    public static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    public static long? Long(object? v) => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    public static double? Double(object? v) => v is null ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    public static DateTime? Date(object? v) => v is DateTime d ? d : null;

    /// <summary>글 값이 <paramref name="yes"/> 와 같은가(대소문자 무시). null 은 false.</summary>
    public static bool Yes(object? v, string yes) => string.Equals(Str(v), yes, StringComparison.OrdinalIgnoreCase);
}
