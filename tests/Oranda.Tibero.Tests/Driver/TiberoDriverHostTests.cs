using System.Runtime.Loader;
using Oranda.Core.Connection;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests.Driver;

/// <summary>드라이버를 한 번 올려 묶는 곳(시험마다 새 로드 컨텍스트). 가짜 드라이버·메모리 PE 로 본다.</summary>
public class TiberoDriverHostTests
{
    private sealed class FixedPaths(string? path) : IDriverPathSource
    {
        public string? Path { get; set; } = path;

        public string? GetPath(Dbms dbms) => dbms == Dbms.Tibero ? Path : null;
    }

    private static TiberoDriverHost NewHost(string extractRoot) =>
        new(new AssemblyLoadContext("tb-host-test-" + Guid.NewGuid().ToString("N"), isCollectible: false), static () => null, extractRoot);

    [Fact]
    public void Require_loads_and_binds_the_fake_driver_once()
    {
        using var temp = new TempFolder();
        var host = NewHost(temp.Path);
        var paths = new FixedPaths(FakeDriverHost.DllPath);

        var driver = host.Require(paths);

        Assert.Same(driver, host.Require(paths));
        Assert.Equal(new Version(1, 1, 35, 0), driver.Version);
        Assert.Equal(Path.GetFullPath(FakeDriverHost.DllPath), host.LoadedPath);
        Assert.Equal(DriverState.Ok, host.LoadedCheck!.State);

        var status = new TiberoDriverStatus(paths, host);
        Assert.Equal(DriverState.Ok, status.Current.State);
        Assert.False(status.RestartRequired);

        paths.Path = temp.Path;
        Assert.True(status.RestartRequired);
    }

    [Fact]
    public void Failures_before_loading_are_not_remembered()
    {
        using var temp = new TempFolder();
        var host = NewHost(temp.Path);
        var paths = new FixedPaths(null);

        var missing = Assert.Throws<TiberoDriverUnavailableException>(() => host.Require(paths));
        Assert.Equal(DriverState.Missing, missing.Check.State);
        Assert.Equal(TiberoDriver.NotSpecifiedMessage, missing.Message);

        paths.Path = Path.Combine(temp.Path, "nope.dll");
        Assert.Equal(DriverState.NotFound, Assert.Throws<TiberoDriverUnavailableException>(() => host.Require(paths)).Check.State);
        Assert.Null(host.LoadedPath);

        paths.Path = FakeDriverHost.DllPath;
        Assert.NotNull(host.Require(paths));
    }

    [Fact]
    public void A_bind_failure_after_loading_is_sticky()
    {
        // 이름·버전은 맞지만 형식이 하나도 없는 이미지 → 검사는 통과, 묶기에서 실패.
        using var temp = new TempFolder();
        var dll = temp.File("Tibero.DataAccess.dll", TestAssemblies.Build("Tibero.DataAccess", new Version(1, 1, 35, 0)));
        var host = NewHost(temp.Path);
        var paths = new FixedPaths(dll);

        var first = Assert.Throws<TiberoDriverBindException>(() => host.Require(paths));
        Assert.Contains("TiberoCommand", first.MissingMembers);
        Assert.False(first.Check.IsUsable);

        Assert.Equal(DriverState.Unusable, first.Check.State);
        Assert.Contains("없는 멤버", first.Message, StringComparison.Ordinal);

        paths.Path = FakeDriverHost.DllPath;
        Assert.Same(first, Assert.Throws<TiberoDriverBindException>(() => host.Require(paths)));
        Assert.Equal(first.Check, new TiberoDriverStatus(paths, host).Current);
    }
}
