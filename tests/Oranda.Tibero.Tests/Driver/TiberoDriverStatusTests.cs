using Oranda.Core.Connection;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests.Driver;

public class TiberoDriverStatusTests
{
    private sealed class FakePaths(string? path) : IDriverPathSource
    {
        public string? GetPath(Dbms dbms) => dbms == Dbms.Tibero ? path : null;
    }

    [Fact]
    public void Check_of_blank_is_missing()
    {
        var status = new TiberoDriverStatus();

        Assert.Equal(DriverState.Missing, status.Check(null).State);
        Assert.Equal(DriverState.Missing, status.Check("  ").State);
    }

    [Fact]
    public void Current_prefers_environment_over_configured_path()
    {
        using var temp = new TempFolder();
        var envDir = Path.Combine(temp.Path, "env");
        temp.File(Path.Combine("env", "Tibero.DataAccess.dll"), TestAssemblies.Build("Tibero.DataAccess", new Version(1, 1, 35, 0)));
        var cfgDir = Path.Combine(temp.Path, "cfg");
        temp.File(Path.Combine("cfg", "Tibero.DataAccess.dll"), TestAssemblies.Build("Tibero.DataAccess", new Version(1, 2, 0, 0)));

        var withEnv = new TiberoDriverStatus(new FakePaths(cfgDir), () => envDir, temp.Path);
        Assert.Equal(DriverState.Ok, withEnv.Current.State);
        Assert.Equal(cfgDir, withEnv.ConfiguredPath);

        var withoutEnv = new TiberoDriverStatus(new FakePaths(cfgDir), () => null, temp.Path);
        Assert.Equal(DriverState.Unverified, withoutEnv.Current.State);
        Assert.False(withoutEnv.RestartRequired);
    }

    [Fact]
    public void Nothing_configured_is_missing()
    {
        var status = new TiberoDriverStatus(new FakePaths(" "), () => null, Path.GetTempPath());

        Assert.Equal(Dbms.Tibero, status.Dbms);
        Assert.Null(status.ConfiguredPath);
        Assert.Equal(DriverState.Missing, status.Current.State);
    }
}
