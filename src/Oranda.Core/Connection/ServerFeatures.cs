namespace Oranda.Core.Connection;

/// <summary>
/// 서버 버전으로 정해지는 기능 켜짐(TSK-03-07, D-026). 권한으로 정해지는 것은 <see cref="ServerCapabilities"/> 가 맡는다.
/// 기능이 꺼지면 화면은 그 항목을 숨기거나 <see cref="UnavailableText"/> 를 보인다.
/// </summary>
/// <param name="Release12Columns">identity·invisible 열, editionable 객체 속성(12c 이상)</param>
/// <param name="Multitenant">CDB/PDB, CON_NAME 컨텍스트(12c 이상)</param>
/// <param name="RowLimiting">FETCH FIRST·OFFSET 행 제한 문법(12c 이상)</param>
/// <param name="Release23Types">23ai 이후 타입·객체(도메인, VECTOR, BOOLEAN 열 등)</param>
public sealed record ServerFeatures(bool Release12Columns, bool Multitenant, bool RowLimiting, bool Release23Types)
{
    /// <summary>이 서버 버전에 없는 기능 자리에 보이는 안내.</summary>
    public const string UnavailableText = "이 서버 버전에서는 쓸 수 없음";

    /// <summary>주 버전으로 기능을 정한다. 0(버전 모름)은 최신으로 본다 — 관문이 이미 지원 버전만 들여보낸다.</summary>
    public static ServerFeatures For(int majorVersion)
    {
        var at12 = majorVersion == 0 || majorVersion >= 12;
        var at23 = majorVersion == 0 || majorVersion >= 23;
        return new ServerFeatures(Release12Columns: at12, Multitenant: at12, RowLimiting: at12, Release23Types: at23);
    }
}
