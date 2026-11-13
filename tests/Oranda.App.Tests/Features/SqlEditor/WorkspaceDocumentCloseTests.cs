using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.DedicatedSession;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>문서 탭 × 로 SQL 작업 공간을 닫을 때의 저장 확인: 고친 탭이 있으면 묻고, 취소하면 남는다. 고친 것이 없으면 바로 닫힌다.</summary>
public sealed class WorkspaceDocumentCloseTests
{
    private static async Task<(DockWorkspace Dock, SqlEditorHost Host)> WiredAsync(ShellHarness h)
    {
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        return (h.Get<DockWorkspace>(), SqlEditorHost.Current);
    }

    private static SqlEditorViewModel EditedTabInNewWorkspace(SqlEditorHost host, string text)
    {
        host.NewWorkspace();
        var tab = host.NewDocument();
        tab.Document.Insert(0, text);
        return tab;
    }

    [Fact]
    public Task Close_all_asks_each_edited_workspace_in_turn_and_cancel_stops_there() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness(); // 세션 라우터·전용 세션을 아직 만들지 않은 시작 직후 상태
        var (dock, host) = await WiredAsync(h);
        var a = EditedTabInNewWorkspace(host, "select 1 from dual");
        var b = EditedTabInNewWorkspace(host, "select 2 from dual");
        Dispatcher.UIThread.RunJobs();
        var answers = new Queue<SaveChoice>([SaveChoice.DontSave, SaveChoice.Cancel]);
        var asked = new List<string>();
        host.Dialogs = new SequencedSaveDialogs(answers, asked);

        dock.CloseAllDocuments();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([a.Title, b.Title], asked); // 차례로 묻는다(둘째가 말없이 취소되지 않는다)
        Assert.Null(host.WorkspaceOf(a));
        Assert.NotNull(host.WorkspaceOf(b)); // 취소한 작업 공간은 남는다
        Assert.Contains(dock.Documents, d => ReferenceEquals(d.Panel, host.WorkspaceOf(b)));
    });

    /// <summary>저장 확인에 차례로 답하고 물은 탭 이름을 남긴다.</summary>
    private sealed class SequencedSaveDialogs(Queue<SaveChoice> answers, List<string> asked) : IEditorDialogs
    {
        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct)
        {
            asked.Add(tabName);
            return Task.FromResult(answers.Dequeue());
        }
    }

    [Fact]
    public Task Document_close_button_asks_to_save_edited_tabs_and_cancel_keeps_the_workspace() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness(); // 세션 라우터 없이도(접속 전) 묻는다
        var (dock, host) = await WiredAsync(h);
        var dialogs = new EditorDialogs(SaveChoice.Cancel);
        host.Dialogs = dialogs;
        var clean = host.NewDocument("select 1");
        host.NewWorkspace();
        var edited = host.NewDocument();
        edited.Document.Insert(0, "select 2 from dual");
        Dispatcher.UIThread.RunJobs();
        var cleanDoc = dock.Documents.Single(d => ReferenceEquals(d.Panel, host.WorkspaceOf(clean)));
        var editedWs = host.WorkspaceOf(edited)!;
        var editedDoc = dock.Documents.Single(d => ReferenceEquals(d.Panel, editedWs));

        Assert.NotNull(dock.CloseConfirm?.Invoke(editedDoc));
        Assert.Null(dock.CloseConfirm!.Invoke(cleanDoc)); // 고친 것이 없으면 묻지 않고 바로 닫는다

        dock.CloseDocument(editedDoc); // 취소
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([edited.Title], dialogs.SaveAsked);
        Assert.Contains(editedDoc, dock.Documents);
        Assert.Contains(editedWs, host.Workspaces);

        var dontSave = new EditorDialogs(SaveChoice.DontSave);
        host.Dialogs = dontSave;
        dock.CloseDocument(editedDoc);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([edited.Title], dontSave.SaveAsked);
        Assert.DoesNotContain(editedDoc, dock.Documents);
        Assert.DoesNotContain(editedWs, host.Workspaces);
    });

    [Fact]
    public Task Close_workspace_command_with_docking_asks_once_and_closes_the_document() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var (dock, host) = await WiredAsync(h);
        var edited = EditedTabInNewWorkspace(host, "select 3 from dual");
        Dispatcher.UIThread.RunJobs();
        var ws = host.WorkspaceOf(edited)!;
        var dialogs = new EditorDialogs(SaveChoice.DontSave);
        host.Dialogs = dialogs;

        Assert.True(await host.TryCloseWorkspaceAsync(ws, default));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([edited.Title], dialogs.SaveAsked); // 문서가 따라 닫힐 때 다시 묻지 않는다
        Assert.DoesNotContain(dock.Documents, d => ReferenceEquals(d.Panel, ws));
    });
}
