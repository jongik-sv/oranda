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
}

/// <summary>
/// 이 어셈블리에 <see cref="OrandaServiceAttribute"/> 구현이 있음을 알린다. 앱 조립은 이 특성이 붙은 어셈블리만 훑는다.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class OrandaServiceAssemblyAttribute : Attribute
{
}
