using System.Globalization;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Sessions;

/// <summary>
/// 지원 버전 관문의 유일한 판별 지점(PRD 11.3, D-06). 허용 주 버전은 <see cref="SupportedMajorVersions"/> 한 곳에서만 정한다.
/// DB 없이 문자열만으로 판별하므로 단위 시험이 가능하다.
/// </summary>
public static class SupportedServerPolicy
{
    /// <summary>허용하는 주 버전(19.x 장기 지원, 23.x = 26ai 계열).</summary>
    public static IReadOnlyList<int> SupportedMajorVersions { get; } = [19, 23];

    public static string SupportedText => string.Join("·", SupportedMajorVersions.Select(v => $"{v}.x"));

    /// <summary>지원하면 true. 아니면 <paramref name="reason"/> 에 사용자에게 보일 이유.</summary>
    public static bool IsSupported(string? serverVersion, out string reason)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            reason = "서버 버전을 확인할 수 없습니다.";
            return false;
        }

        var head = serverVersion.Trim().Split('.')[0];
        if (!int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            reason = $"서버 버전 형식을 알 수 없습니다. 지원 버전은 {SupportedText} 입니다.";
            return false;
        }

        if (SupportedMajorVersions.Contains(major))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Oranda 는 Oracle {SupportedText} 만 지원합니다. 이 서버는 {major} 계열입니다.";
        return false;
    }

    /// <summary>지원하지 않으면 <see cref="UnsupportedServerException"/>.</summary>
    public static void EnsureSupported(string? serverVersion)
    {
        if (!IsSupported(serverVersion, out var reason))
        {
            throw new UnsupportedServerException(serverVersion ?? "(알 수 없음)", reason);
        }
    }
}
