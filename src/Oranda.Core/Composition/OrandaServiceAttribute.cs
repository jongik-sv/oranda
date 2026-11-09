using System.ComponentModel;
using Oranda.Core.Connection;

namespace Oranda.Core.Composition;

/// <summary>
/// 서비스 자동 등록 표시. 구현 클래스에 달면 앱 조립(Oranda.App)이 <see cref="OrandaServiceAssemblyAttribute"/> 가 붙은
/// 어셈블리를 훑어 DI 에 등록한다. 기능 Task 는 공유 진입점에 줄을 더하지 않는다.
/// internal 클래스여도 된다. 한 클래스가 여러 인터페이스를 내면 여러 번 단다.
/// </summary>
/// <example><c>[OrandaService(typeof(ISessionManager))] internal sealed class OracleSessionManager : ISessionManager</c></example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OrandaServiceAttribute(Type serviceType) : Attribute
{
    /// <summary>등록할 서비스 형식(보통 Core 인터페이스).</summary>
    public Type ServiceType { get; } = serviceType;

    public ServiceLifetimeKind Lifetime { get; set; } = ServiceLifetimeKind.Singleton;

    /// <summary>이 OS 에서만 등록(예: DPAPI 는 Windows).</summary>
    public ServicePlatform Platform { get; set; } = ServicePlatform.Any;

    private Dbms? _dbms;

    /// <summary>
    /// 이 구현이 맡는 DBMS(D-046). 달면 <see cref="ServiceType"/> 로 바로 등록하지 않고 (서비스 형식, DBMS) 키로 등록해
    /// <see cref="IDbmsServiceSelector"/> 로만 꺼낸다. 앱이 보는 <see cref="ServiceType"/> 자리는 세션의 DBMS 로 고르는 디스패처가 맡는다.
    /// 달지 않으면(<see cref="ForDbms"/> = null) 지금처럼 DBMS 와 무관한 서비스로 등록한다.
    /// <see cref="Lifetime"/>·<see cref="Platform"/> 은 키 등록에도 그대로 적용한다.
    /// 이 속성은 특성에 값을 다는 용도다. 읽을 때는 <see cref="ForDbms"/> 를 쓴다 — 안 달았을 때도 여기서는 Oracle 이 읽힌다.
    /// </summary>
    /// <example><c>[OrandaService(typeof(IExecutionService), Dbms = Dbms.Oracle)]</c></example>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Dbms Dbms
    {
        get => _dbms ?? Dbms.Oracle;
        set => _dbms = value;
    }

    /// <summary><see cref="Dbms"/> 를 달았으면 그 값, 안 달았으면 null(DBMS 와 무관한 서비스).</summary>
    public Dbms? ForDbms => _dbms;
}

public enum ServiceLifetimeKind
{
    Singleton,
    Transient,
}

public enum ServicePlatform
{
    Any,
    Windows,
    MacOS,

    /// <summary>Windows·macOS 가 아닌 OS(Linux 등, 빌드만 되고 정식 지원하지 않음 — PRD 2). 플랫폼 서비스의 안전한 대체를 단다.</summary>
    Other,
}

/// <summary>
/// 이 어셈블리에 <see cref="OrandaServiceAttribute"/> 구현이 있음을 알린다. 앱 조립은 이 특성이 붙은 어셈블리만 훑는다.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class OrandaServiceAssemblyAttribute : Attribute
{
}
