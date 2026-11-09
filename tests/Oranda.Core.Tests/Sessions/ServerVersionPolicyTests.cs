using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.Core.Tests.Sessions;

// 소유: tb-core C4. IServerVersionPolicy 의 EnsureSupported 확장.

public class ServerVersionPolicyTests
{
    [Fact]
    public void EnsureSupported_throws_with_version_and_reason_only_when_unsupported()
    {
        var policy = new FakePolicy();

        policy.EnsureSupported("7.0");

        var ex = Assert.Throws<UnsupportedServerException>(() => policy.EnsureSupported("1.0"));
        Assert.Equal("1.0", ex.ServerVersion);
        Assert.Equal("too old", ex.Reason);

        var unknown = Assert.Throws<UnsupportedServerException>(() => policy.EnsureSupported(null));
        Assert.Equal("(알 수 없음)", unknown.ServerVersion);
    }

    private sealed class FakePolicy : IServerVersionPolicy
    {
        public Dbms Dbms => Dbms.Tibero;

        public string MinimumText => "6.0";

        public IReadOnlyList<string> TestedVersions { get; } = ["6.0"];

        public bool IsSupported(string? serverVersion, out string reason)
        {
            reason = "too old";
            return serverVersion is not null && string.CompareOrdinal(serverVersion, "6.0") >= 0;
        }
    }
}
