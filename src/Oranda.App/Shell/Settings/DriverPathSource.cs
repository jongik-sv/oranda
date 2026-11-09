using Oranda.Core.Connection;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 설정 <c>drivers.tiberoPath</c> 를 드라이버 로더에 알린다(<see cref="IDriverPathSource"/>, Tibero 드라이버 지정 DU1). 읽는 순간의 현재 설정이라
/// 설정 화면에서 바꾼 값을 바로 본다. 환경 변수 <c>ORANDA_TIBERO_DRIVER</c> 가 설정보다 먼저인 규칙은 로더 몫이라 여기서는 읽지 않는다.
/// Tibero 가 아닌 DBMS(Oracle: 드라이버를 앱이 싣고 있음)와 빈 값·공백은 null.
/// </summary>
public sealed class DriverPathSource(SettingsService settings) : IDriverPathSource
{
    public string? GetPath(Dbms dbms) => dbms switch
    {
        Dbms.Tibero => Normalize(settings.Current.Drivers.TiberoPath),
        _ => null,
    };

    /// <summary>앞뒤 공백을 걷어 내고 빈 값이면 null(설정 화면과 로더가 같은 값을 본다).</summary>
    internal static string? Normalize(string? path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
