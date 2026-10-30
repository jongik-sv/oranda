using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>「작업 공간 이름 바꾸기」 뒤 「작업 공간 저장」: 바꾼 이름이 남고 파일도 같은 폴더에서 새 이름으로 옮겨 간다.</summary>
public sealed partial class SqlEditorTests
{
    [Fact]
    public Task Saving_a_renamed_workspace_keeps_the_new_name_and_moves_the_file() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var oldPath = Path.Combine(_root, "옛이름.workspace.md");
        var dialogs = new FakeDialogs { SavePath = oldPath };
        var host = new SqlEditorHost(NewStore(), dialogs);
        host.NewDocument("select 1 from dual");
        Assert.True(await host.SaveWorkspaceFileAsync(askPath: true, default));

        dialogs.Prompt = "새이름";
        Assert.True(await host.RenameWorkspaceAsync(host.ActiveWorkspace!, default));
        dialogs.SavePath = null; // 다시 묻지 않는다
        Assert.True(await host.SaveWorkspaceFileAsync(askPath: false, default));

        var newPath = Path.Combine(_root, "새이름.workspace.md");
        Assert.Equal("새이름", host.ActiveWorkspace!.Name);
        Assert.Equal(newPath, host.ActiveWorkspace.SourcePath);
        Assert.False(File.Exists(oldPath));
        Assert.StartsWith("# 새이름", await File.ReadAllTextAsync(newPath), StringComparison.Ordinal);
    });

    [Fact]
    public Task Saving_without_a_rename_keeps_the_file_where_it_is_even_when_its_header_name_differs() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "파일이름.workspace.md");
        await File.WriteAllTextAsync(path, "# 머리글이름\n\n## SQL 1\n\n```sql\nselect 1 from dual\n```\n");
        var host = new SqlEditorHost(NewStore(), new FakeDialogs());
        await host.OpenWorkspaceFileAsync(path, default);

        Assert.True(await host.SaveWorkspaceFileAsync(askPath: false, default));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(_root, "머리글이름.workspace.md")));
        Assert.Equal(Path.GetFullPath(path), host.ActiveWorkspace!.SourcePath);
    });

    [Fact]
    public Task Saving_a_renamed_workspace_asks_where_when_a_file_with_the_new_name_exists() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var oldPath = Path.Combine(_root, "원래.workspace.md");
        var taken = Path.Combine(_root, "겹침.workspace.md");
        await File.WriteAllTextAsync(taken, "# 겹침\n");
        var dialogs = new FakeDialogs { SavePath = oldPath };
        var host = new SqlEditorHost(NewStore(), dialogs);
        host.NewDocument("select 1 from dual");
        Assert.True(await host.SaveWorkspaceFileAsync(askPath: true, default));

        dialogs.Prompt = "겹침";
        Assert.True(await host.RenameWorkspaceAsync(host.ActiveWorkspace!, default));
        dialogs.SavePath = null; // 저장 위치를 물으면 취소
        Assert.False(await host.SaveWorkspaceFileAsync(askPath: false, default));

        Assert.Equal("# 겹침\n", await File.ReadAllTextAsync(taken)); // 덮어쓰지 않았다
        Assert.True(File.Exists(oldPath));
        Assert.Equal("겹침", host.ActiveWorkspace!.Name);
    });
}
