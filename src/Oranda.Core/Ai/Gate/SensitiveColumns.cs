using System.Text.RegularExpressions;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 민감 컬럼 이름 판별(연구서 §5.4·§7). 행 값을 줄 수 있는 수준(<c>QueryRows</c>)에서도 이 이름의 컬럼은 히스토그램 끝값·최솟값·최댓값을 내지 않는다.
/// 이름에 아래 조각이 하나라도 들어 있으면(대소문자 무시) 민감하다고 본다. 틀려도 가리는 쪽으로 틀리도록 조각은 넓게 잡되,
/// 흔한 일반 낱말(<see cref="Exceptions"/>)이 조각을 우연히 품은 경우는 그 낱말만 빼고 판단한다.
/// 가상·숨은 열(<c>SYS_NC00012$</c>, <c>SYS_STU…</c>, <c>SYS_STS…</c>)은 무엇을 담았는지 알 수 없어 기본 민감으로 본다.
/// </summary>
public static partial class SensitiveColumns
{
    /// <summary>이름에 들어 있으면 민감한 조각.</summary>
    public static IReadOnlyList<string> Fragments { get; } =
    [
        "RRN", "SSN", "JUMIN", "RESIDENT", "PASSWD", "PASSWORD", "PWD", "SECRET", "TOKEN",
        "PHONE", "TEL", "MOBILE", "HP_", "EMAIL", "MAIL_", "ACCT", "ACCOUNT", "CARD", "IBAN", "BIRTH",
        "ACNT_NO", "ACNO", "HPNO", "MBL", "E_MAIL", "CUST_NM", "ADDR", "RES_NO", "PSWD", "CRD_NO", "CUSTNM",
        "주민", "전화", "휴대", "카드", "계좌", "비밀번호", "이메일", "생년", "여권",
    ];

    /// <summary>조각을 우연히 품은 일반 낱말. 이 낱말은 지우고 남은 이름으로 조각을 찾는다(예: HOTEL_NM 은 민감하지 않고 HOTEL_TEL_NO 는 민감하다).</summary>
    public static IReadOnlyList<string> Exceptions { get; } = ["HOTEL", "TELLER", "DISCARD", "CURRENT"];

    /// <summary>이 접두로 시작하는 이름은 민감하다(통계 확장·숨은 열).</summary>
    private static readonly string[] HiddenPrefixes = ["SYS_STU", "SYS_STS"];

    /// <summary>컬럼 이름이 민감한가. 비었으면 false.</summary>
    public static bool IsSensitive(string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return false;
        }

        var name = columnName.Trim();
        if (VirtualColumn().IsMatch(name))
        {
            return true;
        }

        foreach (var prefix in HiddenPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 낱말을 지운 자리에 구분 문자를 둬서, 지운 뒤에 앞뒤가 붙어 새 조각이 생기지 않게 한다.
        foreach (var word in Exceptions)
        {
            name = name.Replace(word, "#", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var fragment in Fragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\ASYS_NC\d+\$\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualColumn();
}
