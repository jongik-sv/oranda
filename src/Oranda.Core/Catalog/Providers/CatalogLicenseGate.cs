using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Catalog.Providers;

/// <summary>카탈로그 머리 <c>license</c> 관문(SAFE-07). 머리 값 기준이라 DBMS 무관 — 공급자의 질의 포트가 드라이버로 보내기 전에 부른다.</summary>
public static class CatalogLicenseGate
{
    /// <summary>쿼리가 팩을 요구하면 보유 선언(서버 값을 알면 둘 중 낮은 것)과 대조한다. 모자라면 드라이버로 보내지 않는다.</summary>
    public static void Ensure(ISession s, CatalogLicense required)
    {
        if (required == CatalogLicense.None)
        {
            return;
        }

        var need = required == CatalogLicense.Tuning ? LicensePack.DiagnosticsAndTuning : LicensePack.Diagnostics;
        var held = s.Profile.LicensePack;
        if (ExecutionPolicy.ParseServerPackAccess(s.Server.ManagementPackAccess) is { } server && server < held)
        {
            held = server;
        }

        if (need > held)
        {
            throw new UnauthorizedAccessException(
                $"이 조회는 {need} 팩이 필요합니다. 접속 '{s.Profile.Name}' 의 보유 선언(또는 서버 설정)이 없어 보내지 않았습니다.");
        }
    }
}
