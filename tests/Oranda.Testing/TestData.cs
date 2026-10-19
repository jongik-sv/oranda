using Oranda.Core.Connection;

namespace Oranda.Testing;

/// <summary>시험용 기본 모델 값.</summary>
public static class TestData
{
    public static ConnectionProfile Profile(
        ConnectionEnvironment environment = ConnectionEnvironment.Development,
        bool readOnly = false,
        LicensePack licensePack = LicensePack.None) =>
        new("test", "scott", "localhost:1521/FREEPDB1",
            Id: new Guid("b1f6c2d4-0000-4000-8000-000000000001"),
            Environment: environment,
            ReadOnly: readOnly,
            LicensePack: licensePack);

    public static ServerInfo Server(string version = "23.26.0.0.0") =>
        new(version, "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false),
            DbUniqueName: "FREE",
            SessionUser: "SCOTT");
}
