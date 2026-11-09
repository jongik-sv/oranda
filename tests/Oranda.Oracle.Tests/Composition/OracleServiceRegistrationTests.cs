using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Composition;

// 소유: Tibero T1 O3. Oranda.Oracle 의 서비스 등록은 모두 Dbms.Oracle 키를 단다(D-046) — 새 서비스가 키를 빼먹지 않게 지킨다.
public class OracleServiceRegistrationTests
{
    private static readonly Assembly OracleAssembly = typeof(OracleErrorClassifier).Assembly;

    [Fact]
    public void Every_oracle_service_registration_has_the_oracle_key()
    {
        var registrations = OracleAssembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<OrandaServiceAttribute>(inherit: false).Select(a => (Type: t, Attr: a)))
            .ToList();

        Assert.NotEmpty(registrations);
        var missing = registrations.Where(r => r.Attr.ForDbms != Dbms.Oracle).Select(r => r.Type.Name).ToList();
        Assert.True(missing.Count == 0, "Dbms.Oracle 키가 없는 등록: " + string.Join(", ", missing));
    }
}
