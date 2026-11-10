using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL1. 테이블별 임시 작업 폴더 만들기·지우기·오래된 실행 폴더 정리.
public sealed class LoaderWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-ws-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Creates_the_table_folder_under_the_run_folder_and_deletes_both()
    {
        var workspace = LoaderWorkspace.Create("run 1", 3, "사원 EMP$1", _root);

        Assert.Equal(Path.Combine(_root, "oranda-load", "run_1", "3-___EMP$1"), workspace.Directory);
        Assert.True(Directory.Exists(workspace.Directory));
        File.WriteAllText(workspace.ControlFile, "x");
        Directory.CreateDirectory(Path.Combine(workspace.Directory, "lob", "1"));
        File.WriteAllBytes(Path.Combine(workspace.Directory, "lob", "1", "1.bin"), [1]);
        File.SetAttributes(workspace.ControlFile, FileAttributes.ReadOnly);

        workspace.Dispose();
        workspace.Dispose();

        Assert.Null(workspace.CleanupFailure);
        Assert.False(Directory.Exists(workspace.Directory));
        Assert.False(Directory.Exists(workspace.RunDirectory));
    }

    [Fact]
    public void Run_folder_stays_while_another_table_uses_it()
    {
        var a = LoaderWorkspace.Create(_root, "run-2", "A");
        var b = LoaderWorkspace.Create(_root, "run-2", "A");

        Assert.NotEqual(a.Directory, b.Directory);
        a.Dispose();
        Assert.True(Directory.Exists(b.RunDirectory));
        b.Dispose();
        Assert.False(Directory.Exists(b.RunDirectory));
    }

    [Fact]
    public void Read_text_returns_null_for_a_missing_file()
    {
        using var workspace = LoaderWorkspace.Create("run-3", 1, "T", _root);
        File.WriteAllText(workspace.LogFile, "Table \"S\".\"T\":\n  1 Row successfully loaded.\n");

        Assert.Contains("successfully", workspace.ReadText(workspace.LogFile), StringComparison.Ordinal);
        Assert.Contains("successfully", workspace.ReadText("load.log"), StringComparison.Ordinal);
        Assert.Null(workspace.ReadText(workspace.BadFile));
    }

    [Fact]
    public void Stale_run_folders_are_removed_and_fresh_ones_kept()
    {
        var old = LoaderWorkspace.Create("old-run", 1, "T", _root);
        var fresh = LoaderWorkspace.Create("new-run", 1, "T", _root);
        var past = DateTime.UtcNow.AddDays(-3);
        Directory.SetLastWriteTimeUtc(old.Directory, past);
        Directory.SetLastWriteTimeUtc(old.RunDirectory, past);

        var removed = LoaderWorkspace.DeleteStale(_root, TimeSpan.FromDays(1));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old.RunDirectory));
        Assert.True(Directory.Exists(fresh.Directory));
        Assert.Equal(0, LoaderWorkspace.DeleteStale(Path.Combine(_root, "none"), TimeSpan.Zero));
        fresh.Dispose();
    }
}
