using System;
using Oranda.App.Features.Localization;
using Oranda.Core.Connection;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 접속 대화상자의 드라이버 상태 띠 규칙(Tibero 드라이버 지정 DU3, 결정25): 편집 중인 프로필의 DBMS 가 Tibero 인데 드라이버를 쓸 수 없으면
/// 띠 문구를 돌려주고(없으면 null = 띠 없음) 그 동안 [접속]·[시험] 은 꺼진다. 드라이버는 앱이 싣고 있지 않아 Oracle 은 늘 null.
/// 문구는 <see cref="DriverCheck.State"/> 별로 한 줄이고, 단추는 모두 같은 [드라이버 지정…] 이다.
/// </summary>
public static class DriverBandRules
{
    /// <summary>
    /// <paramref name="dbms"/> 의 띠 문구. 띠가 필요 없으면(Oracle, 사용 가능 = Ok·Unverified, <paramref name="source"/> 가 없는 편집기) null.
    /// <paramref name="source"/> 가 구현을 못 찾아 null 을 돌려주면 「지정되지 않음」 으로 본다. 상태를 읽다 예외가 나면 쓸 수 없는 드라이버로 보고 이유를 붙인다.
    /// </summary>
    public static string? Message(Dbms dbms, Func<Dbms, IDbmsDriverStatus?>? source)
    {
        if (dbms != Dbms.Tibero || source is null)
        {
            return null;
        }

        DriverCheck check;
        try
        {
            check = source(dbms)?.Current ?? DriverCheck.Missing;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            check = new DriverCheck(DriverState.Unusable, Reason: ex.Message);
        }

        return check.State switch
        {
            DriverState.Ok or DriverState.Unverified => null,
            DriverState.NotFound => UiText.Get("Connection.driver.band.notFound", "Tibero 드라이버 파일을 찾을 수 없습니다."),
            DriverState.Missing => UiText.Get("Connection.driver.band.missing", "Tibero 드라이버가 지정되지 않았습니다."),
            _ => string.IsNullOrWhiteSpace(check.Reason)
                ? UiText.Get("Connection.driver.band.unusable", "쓸 수 없는 Tibero 드라이버입니다.")
                : UiText.Format("Connection.driver.band.unusableReason", "쓸 수 없는 Tibero 드라이버입니다: {0}", check.Reason.Trim()),
        };
    }
}
