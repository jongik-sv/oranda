using Oranda.Core.Ai.Profiles;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>프로필 조회(<see cref="IAiProfileProbe"/>)의 실제 HTTP 는 Platform 몫이다. Core 가 HttpClient 를 끌어오지 않음을 컴파일된 참조로 확인한다.</summary>
public class CoreHasNoHttpTests
{
    [Fact]
    public void Core_does_not_reference_the_http_client_assembly()
    {
        var http = typeof(IAiProfileProbe).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("System.Net.Http", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(http);
    }
}
