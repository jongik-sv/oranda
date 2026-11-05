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
        "PASS", "PW", "PSWORD", "비번", "패스워드", "암호",
        "JUMN", "JMN_NO", "JMNO", "RESNO", "RES_REG", "RESI_NO", "RSDT", "RSDNT", "IHIDNUM",
        "CRDNO", "ACNT", "ACC_NO", "ACCNO", "ADRS", "ADRES", "주소",
        "API_KEY", "APIKEY", "AUTH_KEY", "AUTHKEY", "CREDENTIAL", "OTP_", "토큰", "비밀키", "인증키", "인증번호",
    ];

    /// <summary>조각을 우연히 품은 일반 낱말. 이 낱말은 지우고 남은 이름으로 조각을 찾는다(예: HOTEL_NM 은 민감하지 않고 HOTEL_TEL_NO 는 민감하다).</summary>
    public static IReadOnlyList<string> Exceptions { get; } = ["HOTEL", "TELLER", "DISCARD", "CURRENT", "BYPASS", "COMPASS", "PASSENGER"];

    /// <summary>이 접두로 시작하는 이름은 민감하다(통계 확장·숨은 열).</summary>
    private static readonly string[] HiddenPrefixes = ["SYS_STU", "SYS_STS"];

    /// <summary>「로컬 AI」 로 보낼 때도 값을 가리는 조각: 비밀번호·주민번호·카드·계좌·주소·인증값(D-045, 사용자 결정). <see cref="Fragments"/> 의 일부다.</summary>
    public static IReadOnlyList<string> AlwaysHiddenFragments { get; } =
    [
        "PASS", "PW", "PSWD", "PSWORD", "비밀번호", "비번", "패스워드", "암호",
        "RRN", "SSN", "JUMIN", "JUMN", "JMN_NO", "JMNO", "RESIDENT", "RES_NO", "RESNO", "RES_REG", "RESI_NO", "RSDT", "RSDNT", "IHIDNUM", "주민",
        "CARD", "CRD_NO", "CRDNO", "카드",
        "ACCT", "ACCOUNT", "ACNT", "ACNO", "ACC_NO", "ACCNO", "IBAN", "계좌",
        "ADDR", "ADRS", "ADRES", "주소",
        "SECRET", "TOKEN", "API_KEY", "APIKEY", "AUTH_KEY", "AUTHKEY", "CREDENTIAL", "OTP_", "토큰", "비밀키", "인증키", "인증번호",
    ];

    /// <summary>컬럼 이름이 민감한가. 비었으면 false.</summary>
    public static bool IsSensitive(string? columnName) => Matches(columnName, Fragments);

    /// <summary>「로컬 AI」 로 보낼 때도 값을 가리는 컬럼인가: 비밀번호·주민번호·카드·계좌·주소·인증값, 그리고 무엇을 담았는지 알 수 없는 가상·숨은 열. 비었으면 false.</summary>
    public static bool IsAlwaysHidden(string? columnName) => Matches(columnName, AlwaysHiddenFragments);

    private static bool Matches(string? columnName, IReadOnlyList<string> fragments)
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

        foreach (var fragment in fragments)
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
