namespace Oranda.Core.Text;

/// <summary>
/// SQL 식별자 따옴표 규칙 한 곳. 호출하는 쪽마다 달랐던 규칙 셋(필요할 때만 / 예약어도 감쌈 / 항상)을 이름으로 구분한다.
/// 순수 함수라 UI 없이 시험한다.
/// </summary>
public static class SqlIdentifier
{
    /// <summary>대문자로 시작하고 대문자·숫자·<c>_$#</c> 로만 이루어진 일반 식별자인가.</summary>
    public static bool IsPlain(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length > 0 && name[0] is >= 'A' and <= 'Z'
            && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '$' or '#');
    }

    /// <summary>
    /// 일반 식별자(<see cref="IsPlain"/>)이고 <paramref name="isReserved"/> 가 예약어로 보지 않으면 그대로, 아니면 큰따옴표로 감싼다(안의 큰따옴표는 두 번).
    /// <paramref name="isReserved"/> 가 null 이면 예약어를 따지지 않는다.
    /// </summary>
    public static string Quote(string name, Func<string, bool>? isReserved = null) =>
        IsPlain(name) && (isReserved is null || !isReserved(name)) ? name : QuoteAlways(name);

    /// <summary>항상 큰따옴표로 감싼다(안의 큰따옴표는 두 번).</summary>
    public static string QuoteAlways(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
