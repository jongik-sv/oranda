using System.Reflection;
using Oranda.App.Features.Localization;
using Velopack.Locators;

namespace Oranda.App.Features.Update;

/// <summary>
/// 화면에 보일 앱 버전을 한 곳에서 정한다(정보 창·업데이트 대화상자·설정). 설치본이면 Velopack 이 아는 설치 버전,
/// 아니면 어셈블리 정보 버전에 「(개발 실행본)」을 붙인다. CI 는 같은 값을 <c>-p:Version</c> 과 <c>vpk pack --packVersion</c> 에 넘긴다.
/// </summary>
public static class AppVersion
{
    /// <summary>설치본의 Velopack 버전. 설치본이 아니면 null. 시작 직후(<c>VelopackApp.Run</c> 뒤)부터 값이 있다.</summary>
    public static string? Installed
    {
        get
        {
            try
            {
                return VelopackLocator.IsCurrentSet ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToString() : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Velopack 설치 버전을 읽지 못했다: {0}", ex.Message);
                return null;
            }
        }
    }

    /// <summary>어셈블리 정보 버전(<c>+커밋</c> 꼬리를 뗀 값). 없으면 <c>0.1.0</c>.</summary>
    public static string Assembly
    {
        get
        {
            var info = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info))
            {
                return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            }

            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? info[..plus] : info;
        }
    }

    /// <summary>지금 실행 중인 버전 글: 설치본 <c>0.1.7</c>, 개발 실행본 <c>0.1.0 (개발 실행본)</c>.</summary>
    public static string Display => Installed ?? Assembly + " " + UiText.Get("Update.devBuildSuffix", "(개발 실행본)");

    /// <summary>빌드 때 <c>-p:OrandaUpdateFeed=…</c> 로 넣은 기본 피드 주소. 넣지 않았으면 null.</summary>
    public static string? BuildDefaultFeed =>
        typeof(AppVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "OrandaUpdateFeed")?.Value is { Length: > 0 } value ? value : null;
}
