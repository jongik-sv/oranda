namespace Oranda.Core.Connection;

/// <summary>
/// 서버 버전으로 정해지는 기능 켜짐(TSK-03-07, D-026). 권한으로 정해지는 것은 <see cref="ServerCapabilities"/> 가 맡는다.
/// 기능이 꺼지면 화면은 그 항목을 숨기거나 <see cref="UnavailableReason"/> 을 보인다.
/// </summary>
/// <param name="Release12Columns">identity·invisible 열, editionable 객체 속성(12c 이상)</param>
/// <param name="Multitenant">CDB/PDB, CON_NAME 컨텍스트(12c 이상)</param>
/// <param name="RowLimiting">FETCH FIRST·OFFSET 행 제한 문법(12c 이상)</param>
/// <param name="Release23Types">23ai 이후 타입·객체(도메인, VECTOR, BOOLEAN 열 등)</param>
/// <param name="Dbms">접속한 서버의 DBMS. 기본은 Oracle 이다.</param>
public sealed record ServerFeatures(
    bool Release12Columns,
    bool Multitenant,
    bool RowLimiting,
    bool Release23Types,
    Dbms Dbms = Dbms.Oracle)
{
    /// <summary>이 서버 버전에 없는 기능 자리에 보이는 안내.</summary>
    public const string UnavailableText = "이 서버 버전에서는 쓸 수 없음";

    /// <summary>주 버전으로 기능을 정한다(Oracle). 0(버전 모름)은 최신으로 본다 — 관문이 이미 지원 버전만 들여보낸다.</summary>
    public static ServerFeatures For(int majorVersion)
    {
        var at12 = majorVersion == 0 || majorVersion >= 12;
        var at23 = majorVersion == 0 || majorVersion >= 23;
        return new ServerFeatures(Release12Columns: at12, Multitenant: at12, RowLimiting: at12, Release23Types: at23);
    }

    /// <summary>
    /// DBMS 와 주 버전으로 기능을 정한다. Oracle 은 <see cref="For(int)"/> 와 같다.
    /// Tibero 는 Oracle 12c·23ai 의 출시 기능이라 첫 판에서는 모두 없는 것으로 본다(실측 뒤에 고친다).
    /// <para>
    /// 확인 필요: docs/design/tibero.md 3장에는 이 네 칸에 맞는 행이 없다. 안전한 쪽(끔)으로 둔 값이다.
    /// Tibero 6 이상은 FETCH FIRST·OFFSET(<see cref="RowLimiting"/>)이나 identity 열(<see cref="Release12Columns"/>)을 지원할 수 있어
    /// T2 실측 뒤 칸마다 고친다. <see cref="Multitenant"/>(CDB/PDB)·<see cref="Release23Types"/> 는 Oracle 전용으로 본다.
    /// </para>
    /// </summary>
    public static ServerFeatures For(int majorVersion, Dbms dbms) => dbms switch
    {
        Dbms.Oracle => For(majorVersion),
        Dbms.Tibero => new ServerFeatures(
            Release12Columns: false, Multitenant: false, RowLimiting: false, Release23Types: false, Dbms: Dbms.Tibero),
        _ => throw new ArgumentOutOfRangeException(nameof(dbms), dbms, null),
    };

    /// <summary>버전 기능이 꺼졌을 때 그 옆에 보일 이유. Oracle 은 <see cref="UnavailableText"/>, 그 밖의 DBMS 는 "에서는 지원하지 않음" 문구.</summary>
    public string UnavailableReason => Dbms == Dbms.Oracle ? UnavailableText : DbmsSupport.NotSupportedText(Dbms);

    /// <summary>이 서버의 DBMS 가 해당 기능을 지원하는지.</summary>
    public bool Supports(DbmsFeature feature) => DbmsSupport.Supports(Dbms, feature);

    /// <summary>지원하지 않을 때 보일 이유. 지원하면 null.</summary>
    public string? UnsupportedReason(DbmsFeature feature) => DbmsSupport.UnsupportedReason(Dbms, feature);
}
