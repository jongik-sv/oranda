using System.IO.Compression;
using Oranda.Core.Connection;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests.Driver;

public class TiberoDriverLocatorTests
{
    private static byte[] Good => TestAssemblies.Build("Tibero.DataAccess", new Version(1, 1, 35, 0));

    private static string MakeNupkg(string path, string? entryName, byte[] content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (entryName is not null)
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(content);
        }
        else
        {
            zip.CreateEntry("readme.txt");
        }

        return path;
    }

    [Fact]
    public void SelectPath_prefers_non_blank_environment_then_configured()
    {
        Assert.Equal("env", TiberoDriverLocator.SelectPath(" env ", "cfg"));
        Assert.Equal("cfg", TiberoDriverLocator.SelectPath("  ", " cfg "));
        Assert.Null(TiberoDriverLocator.SelectPath(null, " "));
    }

    [Fact]
    public void SelectPath_strips_quotes_and_expands_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal("/opt/tibero/Tibero.DataAccess.dll", TiberoDriverLocator.SelectPath(null, "\"/opt/tibero/Tibero.DataAccess.dll\""));
        Assert.Equal(Path.Combine(home, "drivers"), TiberoDriverLocator.SelectPath("~/drivers", null));
        Assert.Null(TiberoDriverLocator.SelectPath(null, "\"\""));
    }

    [Fact]
    public void Missing_path_is_not_found()
    {
        using var temp = new TempFolder();

        var result = TiberoDriverLocator.Locate(Path.Combine(temp.Path, "nope.dll"), temp.Path);

        Assert.Null(result.DllPath);
        Assert.Equal(DriverState.NotFound, result.Failure!.State);
    }

    [Fact]
    public void Folder_with_dll_is_found()
    {
        using var temp = new TempFolder();
        var dll = temp.File("Tibero.DataAccess.dll", Good);

        Assert.Equal(dll, TiberoDriverLocator.Locate(temp.Path, temp.Path).DllPath);
    }

    [Fact]
    public void Folder_with_lib_net5_dll_is_found()
    {
        using var temp = new TempFolder();
        var dll = temp.File(Path.Combine("lib", "net5.0", "Tibero.DataAccess.dll"), Good);

        Assert.Equal(dll, TiberoDriverLocator.Locate(temp.Path, temp.Path).DllPath);
    }

    [Fact]
    public void Empty_folder_is_not_found_and_mentions_the_folder()
    {
        using var temp = new TempFolder();

        var result = TiberoDriverLocator.Locate(temp.Path, temp.Path);

        Assert.Equal(DriverState.NotFound, result.Failure!.State);
        Assert.Contains(temp.Path, result.Failure.Reason);
    }

    [Fact]
    public void Nupkg_is_extracted_inspected_and_reused()
    {
        using var temp = new TempFolder();
        var nupkg = MakeNupkg(Path.Combine(temp.Path, "tdp.nupkg"), "lib\\net5.0\\Tibero.DataAccess.dll", Good);
        var root = Path.Combine(temp.Path, "extract");

        var first = TiberoDriverLocator.Locate(nupkg, root);
        Assert.NotNull(first.DllPath);
        Assert.StartsWith(root, first.DllPath);
        Assert.Equal(DriverState.Ok, TiberoDriverLoader.Inspect(first.DllPath).State);

        var stamp = File.GetLastWriteTimeUtc(first.DllPath);
        var second = TiberoDriverLocator.Locate(nupkg, root);
        Assert.Equal(first.DllPath, second.DllPath);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second.DllPath!));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(first.DllPath)!, "*.tmp"));
    }

    [Fact]
    public void Nupkg_without_the_entry_is_wrong_assembly()
    {
        using var temp = new TempFolder();
        var nupkg = MakeNupkg(Path.Combine(temp.Path, "x.NUPKG"), null, []);

        var result = TiberoDriverLocator.Locate(nupkg, Path.Combine(temp.Path, "extract"));

        Assert.Equal(DriverState.WrongAssembly, result.Failure!.State);
    }

    [Fact]
    public void Corrupt_nupkg_is_wrong_assembly()
    {
        using var temp = new TempFolder();
        var nupkg = temp.File("bad.nupkg", "this is not a zip"u8.ToArray());

        var result = TiberoDriverLocator.Locate(nupkg, Path.Combine(temp.Path, "extract"));

        Assert.Equal(DriverState.WrongAssembly, result.Failure!.State);
    }
}
