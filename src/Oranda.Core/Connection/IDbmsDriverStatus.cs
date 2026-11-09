namespace Oranda.Core.Connection;

/// <summary>
/// 드라이버 파일 사전 검사 결과의 종류(Tibero 드라이버 지정, D-046 결정25). 파일을 올리지 않고 메타데이터만 읽어 정한다.
/// </summary>
public enum DriverState
{
    /// <summary>쓸 수 있다(확인된 버전).</summary>
    Ok,

    /// <summary>위치가 지정되지 않았다.</summary>
    Missing,

    /// <summary>지정한 위치에 파일이 없다(또는 읽을 수 없다).</summary>
    NotFound,

    /// <summary>드라이버가 아니다(어셈블리 이름이 다르거나 Oracle.DataAccess 같은 이름 변형·관리 코드가 아닌 파일).</summary>
    WrongAssembly,

    /// <summary>.NET Framework 용 빌드(TDP.NET 2.x). TDP.NET Core 1.1.x 가 필요하다.</summary>
    FrameworkBuild,

    /// <summary>쓸 수는 있지만 시험하지 않은 버전(1.x 중 확인된 1.1.35 가 아닌 것). <see cref="DriverCheck.Version"/> 에 버전.</summary>
    Unverified,
}

/// <summary>드라이버 사전 검사 결과. <see cref="Reason"/> 은 사용자에게 보일 한국어 문장(<see cref="DriverState.Ok"/> 면 null 일 수 있다).</summary>
public sealed record DriverCheck(DriverState State, string? Version = null, string? Path = null, string? Reason = null)
{
    /// <summary>접속에 쓸 수 있는 상태(<see cref="DriverState.Ok"/>·<see cref="DriverState.Unverified"/>).</summary>
    public bool IsUsable => State is DriverState.Ok or DriverState.Unverified;

    /// <summary>위치가 지정되지 않은 상태.</summary>
    public static DriverCheck Missing { get; } = new(DriverState.Missing);
}

/// <summary>
/// 사용자가 위치를 지정해야 하는 DBMS 드라이버의 상태(Tibero). 그 DBMS 의 공급자 어셈블리가 구현하고
/// <c>[OrandaService(typeof(IDbmsDriverStatus), Dbms = …)]</c> 로 등록한다. 쓰는 쪽은 <c>IDbmsServiceSelector</c> 로 DBMS 에 맞는 것을 꺼내며,
/// 구현이 없는 DBMS(Oracle: 드라이버를 앱이 싣고 있음)는 「지정할 드라이버 없음」으로 본다.
/// </summary>
public interface IDbmsDriverStatus
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>
    /// <paramref name="path"/>(dll·폴더·nupkg) 를 사전 검사한다. 올리지 않고 잠그지 않는다. null·빈 값은 <see cref="DriverState.Missing"/>.
    /// 환경 변수·설정을 읽지 않는다 — 넘긴 경로만 본다(설정 화면의 [확인] 용).
    /// </summary>
    DriverCheck Check(string? path);

    /// <summary>지금 쓰는 드라이버의 상태: 환경 변수·설정 순으로 해석한 경로를 <see cref="Check"/> 한 결과. 읽은 뒤에는 읽은 것의 결과.</summary>
    DriverCheck Current { get; }

    /// <summary>설정(<c>drivers.tiberoPath</c>)에 적힌 경로. 지정 안 함 = null. 환경 변수는 포함하지 않는다.</summary>
    string? ConfiguredPath { get; }

    /// <summary>드라이버를 이미 읽은 뒤 경로가 바뀌어 다시 시작해야 적용되는 상태.</summary>
    bool RestartRequired { get; }
}

/// <summary>DBMS 별 드라이버 위치 설정 읽기. App 구성이 <c>AppSettings.Drivers</c> 에서 구현해 공급자(로더)에 준다.</summary>
public interface IDriverPathSource
{
    /// <summary>설정에 적힌 <paramref name="dbms"/> 드라이버 경로. 지정 안 함·해당 없음 = null.</summary>
    string? GetPath(Dbms dbms);
}
