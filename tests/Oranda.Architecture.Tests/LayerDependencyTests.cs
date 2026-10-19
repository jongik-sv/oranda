using System.Reflection;
using Oranda.Core.Connection;
using Oranda.Language;

namespace Oranda.Architecture.Tests;

/// <summary>
/// TRD 3.2: Core·Language 는 Oracle 드라이버와 Avalonia 를 참조하지 않는다.
/// 컴파일된 어셈블리의 참조 목록을 Oranda.* 를 따라 재귀로 훑어 금지된 어셈블리가 있으면 실패한다.
/// </summary>
public class LayerDependencyTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Avalonia",
        "Oracle.ManagedDataAccess",
    ];

    public static TheoryData<string> GuardedAssemblies => new()
    {
        typeof(ConnectionProfile).Assembly.GetName().Name!,
        typeof(LanguageAssembly).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(GuardedAssemblies))]
    public void Does_not_reference_ui_or_oracle_driver(string assemblyName)
    {
        var violations = FindForbiddenReferences(Assembly.Load(assemblyName));

        Assert.True(violations.Count == 0,
            $"{assemblyName} 가 금지된 어셈블리를 참조한다: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Core_does_not_reference_other_oranda_assemblies()
    {
        var core = typeof(ConnectionProfile).Assembly;

        var oranda = core.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Oranda.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(oranda);
    }

    private static List<string> FindForbiddenReferences(Assembly root)
    {
        var violations = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current.GetName().Name!))
            {
                continue;
            }

            foreach (var reference in current.GetReferencedAssemblies())
            {
                var name = reference.Name!;
                if (ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                {
                    violations.Add($"{current.GetName().Name} -> {name}");
                }
                else if (name.StartsWith("Oranda.", StringComparison.Ordinal))
                {
                    pending.Push(Assembly.Load(reference));
                }
            }
        }

        return violations;
    }
}
