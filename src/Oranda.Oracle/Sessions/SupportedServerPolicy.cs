using System.Globalization;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Sessions;

/// <summary>
/// 지원 버전 관문의 유일한 판별 지점(PRD 11.3, D-06, D-026, D-030). <see cref="MinimumVersion"/> 이상이면 모두 접속을 허용한다.
/// 12c·18c·21c 처럼 시험하지 않은 버전도 막지 않는다(시험한 버전은 <see cref="TestedVersions"/>). DB 없이 문자열만으로 판별하므로 단위 시험이 가능하다.
/// </summary>
public static class SupportedServerPolicy
{
    /// <summary>접속을 허용하는 가장 낮은 버전(주, 부). 11.1 이하는 막는다.</summary>
    public static (int Major, int Minor) MinimumVersion { get; } = (11, 2);

    /// <summary>실제 서버로 시험한 버전(안내용). 이 밖의 버전도 <see cref="MinimumVersion"/> 이상이면 허용한다.</summary>
    public static IReadOnlyList<string> TestedVersions { get; } = ["11.2", "19c", "23ai/26ai"];

    public static string MinimumText => string.Create(CultureInfo.InvariantCulture, $"{MinimumVersion.Major}.{MinimumVersion.Minor}");

    /// <summary>지원하면 true. 아니면 <paramref name="reason"/> 에 사용자에게 보일 이유.</summary>
    public static bool IsSupported(string? serverVersion, out string reason)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            reason = "서버 버전을 확인할 수 없습니다.";
            return false;
        }

        var parts = serverVersion.Trim().Split('.');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            reason = $"서버 버전 형식을 알 수 없습니다. Oranda 는 Oracle {MinimumText} 이상을 지원합니다.";
            return false;
        }

        var minor = 0;
        var hasMinor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
        if (parts.Length > 1 && !hasMinor)
        {
            reason = $"서버 버전 형식을 알 수 없습니다. Oranda 는 Oracle {MinimumText} 이상을 지원합니다.";
            return false;
        }

        if (major > MinimumVersion.Major || (major == MinimumVersion.Major && minor >= MinimumVersion.Minor))
        {
            reason = string.Empty;
            return true;
        }

        var shown = hasMinor ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}") : major.ToString(CultureInfo.InvariantCulture);
        reason = $"Oranda 는 Oracle {MinimumText} 이상을 지원합니다. 이 서버는 {shown} 입니다.";
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
