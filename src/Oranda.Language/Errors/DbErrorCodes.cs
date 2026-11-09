using System.Globalization;
using Oranda.Core.Connection;

namespace Oranda.Language.Errors;

/// <summary>서버 오류 번호의 사람용 표기(D-046): Oracle <c>ORA-00942</c>(5자리), Tibero <c>TBR-8033</c>(자릿수 그대로).</summary>
public static class DbErrorCodes
{
    /// <summary><paramref name="number"/>(<see cref="Oranda.Core.Execution.DbError.Number"/>)를 <paramref name="dbms"/> 의 표기로. 부호는 무시한다.</summary>
    public static string Display(Dbms dbms, int number)
    {
        var abs = number == int.MinValue ? 0 : Math.Abs(number);
        return dbms == Dbms.Tibero
            ? string.Create(CultureInfo.InvariantCulture, $"TBR-{abs}")
            : string.Create(CultureInfo.InvariantCulture, $"ORA-{abs:00000}");
    }
}
