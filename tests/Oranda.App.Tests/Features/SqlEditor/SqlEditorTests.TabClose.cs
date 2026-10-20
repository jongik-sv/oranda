using Oranda.App.Features.SqlEditor;
using Oranda.Core.Workspaces;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>고친 SQL 탭을 닫을 때의 저장 확인(저장·저장 안 함·취소).</summary>
public sealed partial class SqlEditorTests
{
    private async Task<(SqlEditorHost Host, FakeDialogs Dialogs, SqlEditorViewModel Tab, string Path)> DirtyFileTabAsync(SaveChoice answer)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "dirty.sql");
        await File.WriteAllTextAsync(path, "select 1 from dual");
        var dialogs = new FakeDialogs { SaveAnswer = answer };
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = (await host.OpenFileAsync(path, default))!;
        tab.Document.Insert(0, "/* edited */ ");
        return (host, dialogs, tab, path);
    }

    [Theory]
    [InlineData(SaveChoice.Save, true, "/* edited */ select 1 from dual")]
    [InlineData(SaveChoice.DontSave, true, "select 1 from dual")]
    [InlineData(SaveChoice.Cancel, false, "select 1 from dual")]
    public Task Closing_a_dirty_file_tab_asks_and_follows_the_answer(SaveChoice answer, bool closed, string fileText) => Ui.Run(async () =>
    {
        var (host, dialogs, tab, path) = await DirtyFileTabAsync(answer);

        Assert.Equal(closed, await host.CloseAsync(tab, default));

        Assert.Equal(["dirty.sql"], dialogs.SaveAsked);
        Assert.Equal(closed, !host.Tabs.Contains(tab));
        Assert.Equal(fileText, await File.ReadAllTextAsync(path));
    });

    [Fact]
    public Task Clean_file_tab_and_empty_untitled_tab_close_without_asking() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "clean.sql");
        await File.WriteAllTextAsync(path, "select 1");
        var dialogs = new FakeDialogs { SaveAnswer = SaveChoice.Cancel };
        var host = new SqlEditorHost(dialogs: dialogs);
        var clean = (await host.OpenFileAsync(path, default))!;
        var empty = host.NewDocument();
        var blank = host.NewDocument("  \n ");

        Assert.True(await host.CloseAsync(clean, default));
        Assert.True(await host.CloseAsync(empty, default));
        Assert.True(await host.CloseAsync(blank, default));

        Assert.Empty(dialogs.SaveAsked);
    });

    [Fact]
    public Task Untitled_tab_with_text_saves_through_save_as_and_cancelling_the_picker_cancels_the_close() => Ui.Run(async () =>
    {
        var target = Path.Combine(_root, "named.sql");
        Directory.CreateDirectory(_root);
        var dialogs = new FakeDialogs { SaveAnswer = SaveChoice.Save, SavePath = null };
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument("select 42 from dual");

        Assert.False(await host.CloseAsync(tab, default)); // 파일 고르기 취소 → 닫기도 취소
        Assert.Contains(tab, host.Tabs);
        Assert.False(File.Exists(target));

        dialogs.SavePath = target;
        Assert.True(await host.CloseAsync(tab, default));
        Assert.DoesNotContain(tab, host.Tabs);
        Assert.Equal("select 42 from dual", await File.ReadAllTextAsync(target));
    });

    [Fact]
    public Task Closing_a_workspace_does_not_ask() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs { SaveAnswer = SaveChoice.Cancel };
        var host = new SqlEditorHost(dialogs: dialogs);
        host.NewDocument("select 1");
        var workspace = host.ActiveWorkspace!;

        Assert.True(await host.TryCloseWorkspaceAsync(workspace, default));
        Assert.True(await host.CanShutdownAsync(default));

        Assert.Empty(dialogs.SaveAsked);
    });

    [Fact]
    public Task Replacing_a_workspace_asks_for_each_dirty_tab_and_a_later_cancel_keeps_everything() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs { SaveAnswer = SaveChoice.Cancel };
        var host = new SqlEditorHost(dialogs: dialogs);
        host.NewDocument("select 1");
        var workspace = host.ActiveWorkspace!;
        var incoming = new Workspace { Id = workspace.Id, Name = workspace.Name, SavedAt = DateTimeOffset.UtcNow };

        Assert.Null(await host.RestoreAsync(incoming, default));
        Assert.Single(workspace.Tabs);

        dialogs.SaveAnswer = SaveChoice.DontSave;
        Assert.NotNull(await host.RestoreAsync(incoming, default));
        Assert.Empty(workspace.Tabs);
        Assert.Equal(2, dialogs.SaveAsked.Count);
    });

    [Fact]
    public Task Replace_saves_the_first_tab_then_cancel_on_the_second_stops_the_restore() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "first.sql");
        await File.WriteAllTextAsync(path, "x");
        var dialogs = new SequencedDialogs([SaveChoice.Save, SaveChoice.Cancel]);
        var host = new SqlEditorHost(dialogs: dialogs);
        var first = (await host.OpenFileAsync(path, default))!;
        first.Document.Insert(0, "kept ");
        var second = host.NewDocument("second");
        var workspace = host.ActiveWorkspace!;

        Assert.Null(await host.RestoreAsync(new Workspace { Id = workspace.Id, Name = workspace.Name, SavedAt = DateTimeOffset.UtcNow }, default));

        Assert.Equal("kept x", await File.ReadAllTextAsync(path)); // 저장한 것은 저장된 채 남는다
        Assert.Equal([first, second], workspace.Tabs);
    });

    [Fact]
    public Task Save_confirmation_runs_before_guards_registered_later() => Ui.Run(async () =>
    {
        var (host, dialogs, tab, _) = await DirtyFileTabAsync(SaveChoice.Cancel);
        var later = 0;
        host.RegisterCloseGuard((_, _, _) => { later++; return Task.FromResult(true); });

        Assert.False(await host.CloseAsync(tab, default));
        Assert.Equal(0, later); // 저장 확인이 취소되면 뒤 가드는 부르지 않는다

        dialogs.SaveAnswer = SaveChoice.DontSave;
        Assert.True(await host.CloseAsync(tab, default));
        Assert.Equal(1, later);
    });

    [Fact]
    public Task Without_a_dialog_a_modified_tab_stays_open() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(); // NoDialogs: 묻지 못하면 글을 잃는 쪽으로 가지 않는다
        var tab = host.NewDocument("select 1");

        Assert.False(await host.CloseAsync(tab, default));

        Assert.Contains(tab, host.Tabs);
    });

    [Fact]
    public Task Failed_save_keeps_the_tab_and_tells_the_user() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "blocked.sql");
        Directory.CreateDirectory(blocked); // 같은 이름의 폴더가 있어 파일로 쓸 수 없다
        var dialogs = new FakeDialogs { SaveAnswer = SaveChoice.Save, SavePath = blocked };
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument("select 1");

        Assert.False(await host.CloseAsync(tab, default));

        Assert.Contains(tab, host.Tabs);
        Assert.Single(dialogs.Messages);
    });

    [Fact]
    public Task Unreadable_restored_file_tab_does_not_offer_to_overwrite_the_original_with_nothing() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs { SaveAnswer = SaveChoice.Save };
        var host = new SqlEditorHost(dialogs: dialogs);
        var vm = SqlEditorViewModel.FromWorkspaceTab(new WorkspaceTab { Id = Guid.NewGuid(), Title = "gone.sql", FilePath = Path.Combine(_root, "gone.sql") }, null);
        Assert.True(vm.IsDirty);
        Assert.True(vm.FileUnreadable);
        host.Restore(new Workspace { Id = "w", Name = "w", SavedAt = DateTimeOffset.UtcNow, Tabs = [new WorkspaceTab { Id = vm.TabId, Title = "gone.sql", FilePath = vm.FilePath }] });
        var tab = host.Tabs.Single();

        Assert.True(await host.CloseAsync(tab, default));

        Assert.Empty(dialogs.SaveAsked);
        Assert.False(File.Exists(Path.Combine(_root, "gone.sql")));
    });

    [Fact]
    public Task Shell_close_of_a_dirty_tab_that_is_cancelled_keeps_the_tab_and_still_reports_handled() => Ui.Run(async () =>
    {
        var (host, _, tab, _) = await DirtyFileTabAsync(SaveChoice.Cancel);

        Assert.True(await host.ActiveWorkspace!.CloseActiveInnerTabAsync(default));

        Assert.Contains(tab, host.Tabs);
    });

    [Fact]
    public Task Tab_header_close_command_asks_too() => Ui.Run(async () =>
    {
        var (host, dialogs, tab, _) = await DirtyFileTabAsync(SaveChoice.DontSave);

        await host.ActiveWorkspace!.CloseTabCommand.ExecuteAsync(tab);

        Assert.Equal(["dirty.sql"], dialogs.SaveAsked);
        Assert.DoesNotContain(tab, host.Tabs);
    });

    /// <summary>저장 확인에 정해진 순서로 답한다. 나머지 대화상자는 취소·거절.</summary>
    private sealed class SequencedDialogs(IReadOnlyList<SaveChoice> answers) : IEditorDialogs
    {
        private int _next;

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct) => Task.FromResult(answers[_next++]);
    }
}
