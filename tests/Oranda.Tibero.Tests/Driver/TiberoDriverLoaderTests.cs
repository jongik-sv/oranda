using System.Reflection;
using System.Runtime.Loader;
using Oranda.Core.Connection;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests.Driver;

public class TiberoDriverLoaderTests
{
    private static DriverCheck Inspect(string name, Version version, string corlib = "System.Runtime", bool ilOnly = true)
    {
        using var stream = TestAssemblies.Stream(name, version, corlib, ilOnly);
        return TiberoDriverLoader.Inspect(stream, "x.dll");
    }

    [Fact]
    public void Verified_version_is_ok()
    {
        var check = Inspect("Tibero.DataAccess", new Version(1, 1, 35, 0));

        Assert.Equal(DriverState.Ok, check.State);
        Assert.Equal("1.1.35", check.Version);
        Assert.Null(check.Reason);
        Assert.Equal("x.dll", check.Path);
    }

    [Fact]
    public void Other_1x_version_is_unverified()
    {
        var check = Inspect("Tibero.DataAccess", new Version(1, 2, 0, 0));

        Assert.Equal(DriverState.Unverified, check.State);
        Assert.Equal("1.2.0", check.Version);
        Assert.Contains("1.2.0", check.Reason);
        Assert.True(check.IsUsable);
    }

    [Fact]
    public void Mscorlib_reference_is_framework_build()
    {
        var check = Inspect("Tibero.DataAccess", new Version(2, 70, 0, 0), "mscorlib");

        Assert.Equal(DriverState.FrameworkBuild, check.State);
        Assert.StartsWith(".NET Framework 용 TDP.NET(2.x)", check.Reason);
    }

    [Fact]
    public void Core_build_with_other_major_is_wrong_assembly()
    {
        var check = Inspect("Tibero.DataAccess", new Version(2, 0, 0, 0));

        Assert.Equal(DriverState.WrongAssembly, check.State);
        Assert.Contains("2.0.0", check.Reason);
    }

    [Fact]
    public void Oracle_name_variant_has_its_own_reason()
    {
        var check = Inspect("Oracle.DataAccess", new Version(1, 1, 35, 0));

        Assert.Equal(DriverState.WrongAssembly, check.State);
        Assert.Contains("Oracle 호환", check.Reason);
    }

    [Fact]
    public void Other_assembly_name_is_wrong_assembly()
    {
        Assert.Equal(DriverState.WrongAssembly, Inspect("Something.Else", new Version(1, 1, 35, 0)).State);
    }

    [Fact]
    public void Not_il_only_is_wrong_assembly()
    {
        Assert.Equal(DriverState.WrongAssembly, Inspect("Tibero.DataAccess", new Version(1, 1, 35, 0), ilOnly: false).State);
    }

    [Fact]
    public void Text_file_is_wrong_assembly_and_not_locked()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.dll", "not a dll"u8.ToArray());

        var check = TiberoDriverLoader.Inspect(path);

        Assert.Equal(DriverState.WrongAssembly, check.State);
        Assert.Equal("관리 코드 어셈블리(.dll)가 아닙니다.", check.Reason);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Inspect_leaves_the_file_deletable()
    {
        using var temp = new TempFolder();
        var path = temp.File("Tibero.DataAccess.dll", TestAssemblies.Build("Tibero.DataAccess", new Version(1, 1, 35, 0)));

        Assert.Equal(DriverState.Ok, TiberoDriverLoader.Inspect(path).State);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_returns_the_driver_assembly_and_resolves_siblings_from_its_folder()
    {
        using var temp = new TempFolder();
        var path = temp.File("Tibero.DataAccess.dll", TestAssemblies.Build("Tibero.DataAccess", new Version(1, 1, 35, 0), references: "Dep"));
        temp.File("Dep.dll", TestAssemblies.Build("Dep", new Version(1, 0, 0, 0)));
        temp.File("Other.dll", TestAssemblies.Build("Other", new Version(1, 0, 0, 0)));
        var context = new AssemblyLoadContext("tb-driver-test", isCollectible: true);
        try
        {
            var assembly = TiberoDriverLoader.Load(context, path);
            Assert.Equal("Tibero.DataAccess", assembly.GetName().Name);

            // 두 번째 호출이 Resolving 핸들러를 중복 등록하지 않는다.
            Assert.Same(assembly, TiberoDriverLoader.Load(context, path));

            var dep = context.LoadFromAssemblyName(new AssemblyName("Dep"));
            Assert.Equal("Dep", dep.GetName().Name);
            Assert.Equal(Path.Combine(temp.Path, "Dep.dll"), dep.Location);

            // 드라이버가 참조하지 않는 이름은 드라이버 폴더에서 찾아 주지 않는다(앱 전체의 실패에 끼어들지 않음).
            Assert.Throws<FileNotFoundException>(() => context.LoadFromAssemblyName(new AssemblyName("Other")));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void Load_refuses_a_second_driver_folder_on_the_same_context()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        var image = TestAssemblies.Build("Tibero.DataAccess", new Version(1, 1, 35, 0));
        var context = new AssemblyLoadContext("tb-driver-test", isCollectible: true);
        try
        {
            TiberoDriverLoader.Load(context, first.File("Tibero.DataAccess.dll", image));
            Assert.Throws<InvalidOperationException>(() => TiberoDriverLoader.Load(context, second.File("Tibero.DataAccess.dll", image)));
        }
        finally
        {
            context.Unload();
        }
    }
}
