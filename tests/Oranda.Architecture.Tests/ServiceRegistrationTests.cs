using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Language;
using Oranda.Oracle;
using Oranda.Platform;

namespace Oranda.Architecture.Tests;

/// <summary>
/// 서비스 자동 수집 장치(TSK-02-03)의 규칙만 검사한다. 어떤 서비스가 몇 개 있는지(스텁 목록)는 단정하지 않는다.
/// </summary>
public class ServiceRegistrationTests
{
    public static TheoryData<string> ServiceAssemblies => new()
    {
        typeof(LanguageAssembly).Assembly.GetName().Name!,
        typeof(OracleAssembly).Assembly.GetName().Name!,
        typeof(PlatformAssembly).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(ServiceAssemblies))]
    public void Implementation_assembly_is_marked_for_scanning(string assemblyName)
    {
        Assert.NotNull(Assembly.Load(assemblyName).GetCustomAttribute<OrandaServiceAssemblyAttribute>());
    }

    [Theory]
    [MemberData(nameof(ServiceAssemblies))]
    public void Every_registered_class_is_concrete_and_implements_its_service(string assemblyName)
    {
        var violations = Assembly.Load(assemblyName).GetTypes()
            .SelectMany(t => t.GetCustomAttributes<OrandaServiceAttribute>().Select(a => (Type: t, a.ServiceType)))
            .Where(x => x.Type.IsAbstract || x.Type.IsGenericTypeDefinition || !x.ServiceType.IsAssignableFrom(x.Type))
            .Select(x => $"{x.Type.FullName} -> {x.ServiceType.Name}")
            .ToList();

        Assert.Empty(violations);
    }
}
