using Avalonia.Threading;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.PlsqlEditor;
using Oranda.App.Tests.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;

namespace Oranda.App.Tests.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간 파일 저장소(결정 71, B-2 ①): 왕복·손상 파일 백업·새 판 파일 보호·닫힌 파일 20개 제한.
/// 정적 활성 탭을 쓰지 않으므로 <see cref="PlsqlStatics"/> 묶음 밖에서 돈다.
/// </summary>
public sealed class PlsqlWorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-plsql-store-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "plsql-workspaces");

    private string Backup => Path.Combine(_root, "backup", "plsql-workspaces");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_saved_workspace_loads_back_field_for_field_with_an_unspecified_ddl_time()
    {
        var store = new PlsqlWorkspaceStore(Folder, Backup);
        var profile = Guid.NewGuid();
        var ddl = new DateTime(2026, 10, 20, 9, 30, 15, DateTimeKind.Unspecified); // Oracle 이 돌려주는 DATE 와 같은 종류
        var file = new PlsqlWorkspaceFile
        {
            Id = "a1",
            Name = "PL/SQL 1",
            ActiveTab = 1,
            Tabs =
            [
                new PlsqlTabFile { Owner = "SCOTT", Name = "P", Type = "PROCEDURE", ConnectionProfileId = profile, LastDdlTime = ddl, Title = "P", Text = "CREATE OR REPLACE PROCEDURE p IS\r\nBEGIN NULL; END;", Caret = 7, Line1Shift = 18 },
                new PlsqlTabFile { Title = "새 PL/SQL", Text = "-- mine", IsDirty = true, FilePath = "/tmp/x.sql" },
            ],
        };

        await store.SaveAsync(file);
        var loaded = await new PlsqlWorkspaceStore(Folder, Backup).LoadAsync("a1");

        Assert.NotNull(loaded);
        Assert.Equal(file.Id, loaded.Id);
        Assert.Equal(file.Name, loaded.Name);
        Assert.Equal(1, loaded.ActiveTab);
        Assert.Equal(file.Tabs, loaded.Tabs);
        Assert.Equal(DateTimeKind.Unspecified, loaded.Tabs[0].LastDdlTime!.Value.Kind);
        Assert.False(File.Exists(Path.Combine(Folder, "a1.json.tmp")));

        await store.SaveIndexAsync(new PlsqlWorkspaceIndex { OpenIds = ["a1", "b2"], LastId = "b2" });
        var index = await store.LoadIndexAsync();
        Assert.Equal(["a1", "b2"], index!.OpenIds.ToArray());
        Assert.Equal("b2", index.LastId);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":0,\"id\":\"c3\",\"name\":\"x\"}")]
    [InlineData("{\"id\":\"c3\",\"name\":\"x\"}")] // 판 없음
    [InlineData("{\"schemaVersion\":1,\"id\":\"zz\",\"name\":\"x\"}")] // 파일 이름과 다른 ID
    [InlineData("{\"schemaVersion\":1,\"id\":\"c3\",\"name\":\"x\",\"tabs\":[{\"text\":null}]}")]
    public async Task A_corrupt_file_moves_to_backup_and_loads_as_missing(string json)
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(Path.Combine(Folder, "c3.json"), json);
        var store = new PlsqlWorkspaceStore(Folder, Backup);

        Assert.Null(await store.LoadAsync("c3"));
        Assert.False(File.Exists(Path.Combine(Folder, "c3.json")));
        var moved = Assert.Single(Directory.GetFiles(Backup));
        Assert.StartsWith("c3.json.corrupt-", Path.GetFileName(moved), StringComparison.Ordinal);
        Assert.Equal(json, await File.ReadAllTextAsync(moved));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"id\":\"d4\",\"name\":\"future\",\"tabs\":[],\"newField\":true}", true)]
    [InlineData("{\"schemaVersion\":2,\"id\":\"d4\",\"title\":{\"ko\":\"future\"}}", false)] // 모양이 바뀐 새 판
    public async Task A_file_written_by_a_newer_version_is_never_moved_or_overwritten(string json, bool readable)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "d4.json");
        await File.WriteAllTextAsync(path, json);
        var store = new PlsqlWorkspaceStore(Folder, Backup);

        var loaded = await store.LoadAsync("d4");
        Assert.Equal(readable, loaded is not null);
        await store.SaveAsync(new PlsqlWorkspaceFile { Id = "d4", Name = "mine" });

        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(Backup));
    }

    [Fact]
    public async Task Retire_moves_the_file_to_backup_and_keeps_only_the_newest_twenty_closed_files()
    {
        Directory.CreateDirectory(Backup);
        // 이름 끝 시각을 일부러 정해 둔다(밀리초 단위라 같은 시각이 나면 차례가 흔들린다). 모두 지금보다 옛날이다.
        var seeded = Enumerable.Range(0, 22).Select(i => $"old{i:00}.json.closed-20200101000000{i:000}").ToList();
        seeded.ForEach(n => File.WriteAllText(Path.Combine(Backup, n), "{}"));
        File.WriteAllText(Path.Combine(Backup, "other.json.corrupt-20200101000000000"), "{}");
        var store = new PlsqlWorkspaceStore(Folder, Backup);
        await store.SaveAsync(new PlsqlWorkspaceFile { Id = "e5", Name = "PL/SQL 5" });

        await store.RetireAsync("e5");

        Assert.False(File.Exists(Path.Combine(Folder, "e5.json")));
        var left = Directory.GetFiles(Backup).Select(Path.GetFileName).ToList();
        Assert.Single(left, n => n!.StartsWith("e5.json.closed-", StringComparison.Ordinal));
        Assert.Contains("other.json.corrupt-20200101000000000", left); // 손상 백업은 세지 않는다
        // 새것(e5) 1개 + 옛것 중 새 19개 = 20. 가장 옛것 3개(00~02)가 지워진다.
        Assert.Equal(seeded.Skip(3).Order(StringComparer.Ordinal), left.Where(n => n!.StartsWith("old", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        await store.RetireAsync("e5"); // 이미 옮긴 작업 공간이면 아무것도 하지 않는다
        Assert.Equal(21, Directory.GetFiles(Backup).Length);
    }
}

/// <summary>
/// PL/SQL 작업 공간 자동 저장(결정 71, B-2 ①): 바뀐 작업 공간만 쓰기, 미리보기 탭·미리보기 전용 작업 공간 빼기, 열린 목록 차례·마지막 활성,
/// 작업 공간을 연 적 없으면 폴더를 만들지 않음, 종료 저장은 돌던 자동 저장 뒤 고친 글까지 씀.
/// 타이머(<see cref="PlsqlWorkspacePersistence.RunAsync"/>)는 돌리지 않고 저장을 직접 부른다.
/// </summary>
[Collection(PlsqlStatics.Name)]
public sealed class PlsqlWorkspacePersistenceTests
{
    [Fact]
    public Task Nothing_is_written_when_only_a_preview_workspace_was_ever_opened() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var persistence = Persistence(h);
        var preview = new PlsqlEditorViewModel();
        preview.StartNew(PlsqlEditorCommandHandler.NewTemplate, new SchemaOpenTests.OpenFakeSession());
        try
        {
            await persistence.SaveChangedAsync(default);
            PlsqlWorkspaceRouter.For(h.Get<DockWorkspace>(), h.Services).Place(preview, preview: true);
            Dispatcher.UIThread.RunJobs();
            await persistence.SaveChangedAsync(default);

            Assert.False(Directory.Exists(persistence.Store.Folder));
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task Autosave_writes_only_changed_workspaces_without_the_preview_tab_and_an_index_in_document_order() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var persistence = Persistence(h);
        var docking = h.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, h.Services);
        var procedure = await PlsqlEditorTests.OpenedProcedure();
        var mine = new PlsqlEditorViewModel();
        mine.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
        mine.Editor.Document.Insert(0, "-- mine\n");
        var preview = new PlsqlEditorViewModel();
        preview.StartNew(PlsqlEditorCommandHandler.NewTemplate, new SchemaOpenTests.OpenFakeSession());
        try
        {
            router.Place(procedure);
            router.Place(mine);
            router.Place(preview, preview: true);
            var first = (PlsqlWorkspaceViewModel)docking.Documents.Single().Panel;
            var second = new PlsqlWorkspaceViewModel { Name = "PL/SQL 2" };
            docking.OpenDocument(PlsqlWorkspaceRouter.PanelId, second, second.Name);
            router.Show(first, mine, focus: false);
            Dispatcher.UIThread.RunJobs();

            await persistence.SaveChangedAsync(default);

            var saved = await persistence.Store.LoadAsync(first.Id);
            Assert.NotNull(saved);
            Assert.Equal(2, saved.Tabs.Count); // 미리보기 탭은 빠진다
            Assert.Equal(1, saved.ActiveTab);
            var p = saved.Tabs[0];
            Assert.Equal(("SCOTT", "P", "PROCEDURE"), (p.Owner, p.Name, p.Type));
            Assert.Equal(new DateTime(2026, 10, 20, 9, 0, 0), p.LastDdlTime);
            Assert.NotNull(p.ConnectionProfileId);
            Assert.Equal(procedure.Editor.Text, p.Text);
            Assert.False(p.IsDirty);
            Assert.True(saved.Tabs[1].IsDirty);
            Assert.Null(saved.Tabs[1].Owner);
            Assert.StartsWith("-- mine", saved.Tabs[1].Text, StringComparison.Ordinal);
            Assert.Empty((await persistence.Store.LoadAsync(second.Id))!.Tabs);
            var index = await persistence.Store.LoadIndexAsync();
            Assert.Equal(new[] { first.Id, second.Id }, index!.OpenIds);
            Assert.Equal(first.Id, index.LastId);

            // 바뀌지 않은 작업 공간은 다시 쓰지 않는다: 지운 파일이 돌아오지 않는다.
            File.Delete(Path.Combine(persistence.Store.Folder, first.Id + ".json"));
            File.Delete(Path.Combine(persistence.Store.Folder, second.Id + ".json"));
            await persistence.SaveChangedAsync(default);
            Assert.Null(await persistence.Store.LoadAsync(first.Id));

            mine.Editor.Document.Insert(0, "-- more\n");
            await persistence.SaveChangedAsync(default);
            Assert.StartsWith("-- more", (await persistence.Store.LoadAsync(first.Id))!.Tabs[1].Text, StringComparison.Ordinal);
            Assert.Null(await persistence.Store.LoadAsync(second.Id));
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task The_index_left_by_an_earlier_run_is_untouched_until_something_opens_and_its_ids_are_kept_after_ours() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var persistence = Persistence(h);
        var docking = h.Get<DockWorkspace>();
        var indexPath = Path.Combine(persistence.Store.Folder, "open.json");
        Directory.CreateDirectory(persistence.Store.Folder);
        // 저장소가 쓰는 모양(들여쓰기)과 다르게 써 둔다: 다시 쓰면 바이트가 달라진다.
        const string earlier = "{\"schemaVersion\":1,\"openIds\":[\"old1\",\"old2\"],\"lastId\":\"old2\"}";
        await File.WriteAllTextAsync(indexPath, earlier);
        var tab = new PlsqlEditorViewModel();
        tab.StartNew("-- mine\n", null);
        try
        {
            await persistence.SaveChangedAsync(default);
            Assert.Equal(earlier, await File.ReadAllTextAsync(indexPath));

            PlsqlWorkspaceRouter.For(docking, h.Services).Place(tab);
            Dispatcher.UIThread.RunJobs();
            var doc = docking.Documents.Single();
            var ours = ((PlsqlWorkspaceViewModel)doc.Panel).Id;
            await persistence.SaveChangedAsync(default);
            var index = await persistence.Store.LoadIndexAsync();
            Assert.Equal([ours, "old1", "old2"], index!.OpenIds.ToArray());
            Assert.Equal(ours, index.LastId);

            Assert.True(docking.CloseDocument(doc));
            Dispatcher.UIThread.RunJobs();
            await persistence.SaveChangedAsync(default);
            index = await persistence.Store.LoadIndexAsync();
            Assert.Equal(["old1", "old2"], index!.OpenIds.ToArray());
            Assert.Equal("old2", index.LastId);
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"openIds\":[\"old1\"],\"lastId\":\"old1\"}", true)] // 새 버전 앱의 목록: 아무것도 쓰지 않는다
    [InlineData("{\"schemaVersion\":1,\"openIds\":null}", false)] // 손상: 옮기고 우리 목록을 쓴다
    public Task A_newer_index_blocks_every_write_and_a_corrupt_one_does_not(string earlier, bool newer) => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var persistence = Persistence(h);
        var docking = h.Get<DockWorkspace>();
        var indexPath = Path.Combine(persistence.Store.Folder, "open.json");
        Directory.CreateDirectory(persistence.Store.Folder);
        await File.WriteAllTextAsync(indexPath, earlier);
        var tab = new PlsqlEditorViewModel();
        tab.StartNew("-- mine\n", null);
        try
        {
            PlsqlWorkspaceRouter.For(docking, h.Services).Place(tab);
            Dispatcher.UIThread.RunJobs();
            var ours = ((PlsqlWorkspaceViewModel)docking.Documents.Single().Panel).Id;

            await persistence.SaveChangedAsync(default);

            var ourFile = Path.Combine(persistence.Store.Folder, ours + ".json");
            if (newer)
            {
                Assert.Equal(earlier, await File.ReadAllTextAsync(indexPath));
                Assert.False(File.Exists(ourFile));
            }
            else
            {
                Assert.Equal([ours], (await persistence.Store.LoadIndexAsync())!.OpenIds.ToArray());
                Assert.True(File.Exists(ourFile));
            }
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task The_shutdown_save_does_not_stop_at_a_running_autosave_and_writes_the_latest_text() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var persistence = Persistence(h);
        var tab = new PlsqlEditorViewModel();
        tab.StartNew("-- first\n", null);
        try
        {
            PlsqlWorkspaceRouter.For(h.Get<DockWorkspace>(), h.Services).Place(tab);
            Dispatcher.UIThread.RunJobs();
            var workspace = (PlsqlWorkspaceViewModel)h.Get<DockWorkspace>().Documents.Single().Panel;

            Task autosave, shutdown;
            await PlsqlWorkspaceStore.WriteLock.WaitAsync();
            try
            {
                autosave = persistence.SaveChangedAsync(default); // 글을 읽고 쓰기 차례를 기다린다
                Assert.False(autosave.IsCompleted);
                Assert.Same(autosave, persistence.SaveChangedAsync(default)); // 겹친 자동 저장은 새로 쓰지 않는다
                tab.Editor.Document.Insert(0, "-- last\n");
                shutdown = persistence.FlushAsync(default);
            }
            finally
            {
                PlsqlWorkspaceStore.WriteLock.Release();
            }

            await autosave;
            await shutdown;

            Assert.StartsWith("-- last", (await persistence.Store.LoadAsync(workspace.Id))!.Tabs[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    // ---- 시작 복원·닫기(B-2 ②) ----

    [Fact]
    public Task A_workspace_comes_back_after_a_restart_and_its_waiting_tab_keeps_the_keys_off() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-plsql-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var procedure = await PlsqlEditorTests.OpenedProcedure();
            var body = await PlsqlEditorTests.OpenedPackageBody();
            string id;
            using (var h = new ShellHarness(dir))
            {
                var router = PlsqlWorkspaceRouter.For(h.Get<DockWorkspace>(), h.Services);
                router.Place(procedure);
                router.Place(body);
                body.Editor.Document.Insert(0, "-- mine\n");
                id = ((PlsqlWorkspaceViewModel)h.Get<DockWorkspace>().Documents.Single().Panel).Id;
                await Persistence(h).ShutdownAsync(default);
                PlsqlEditorViewModel.Active = null;
            }

            using (var h = new ShellHarness(dir))
            {
                var keys = h.Get<Oranda.Core.Shell.IContextKeys>();
                var docking = h.Get<DockWorkspace>();
                var persistence = Persistence(h);
                persistence.Wire();
                await persistence.RestoreAsync(default);
                Dispatcher.UIThread.RunJobs();

                var doc = Assert.Single(docking.Documents);
                Assert.Same(doc, docking.ActiveDocument);
                var workspace = Assert.IsType<PlsqlWorkspaceViewModel>(doc.Panel);
                Assert.Equal(id, workspace.Id);
                Assert.Equal(new[] { procedure.Title, body.Title }, workspace.Tabs.Select(t => t.Title));
                Assert.Equal(procedure.Editor.Text, workspace.Tabs[0].Editor.Text);
                var restored = workspace.Tabs[1];
                Assert.Same(restored, workspace.ActiveTab);
                Assert.Equal(body.Editor.Text, restored.Editor.Text);
                Assert.Equal(body.BaselineDdlTime, restored.BaselineDdlTime);

                // 접속이 아직 없다: 패키지 본문인데도 명세/본문 전환·실행 키가 꺼져 있다.
                Assert.Same(restored, PlsqlEditorViewModel.Active);
                Assert.True(restored.AwaitingSession);
                Assert.False(keys.Evaluate("isPackageDoc"));
                Assert.False(keys.Evaluate("plsqlRunnable"));
                Assert.NotNull(docking.CloseConfirm!(doc)); // 고친 채 저장한 탭은 닫을 때 묻는다
                h.Window.UpdateLayout();
                Oranda.App.Tests.Features.MyBatisWorkspace.Flow.MyBatisFlowRig.Render(h.Window, "plsql-workspace-restored",
                    (int)h.Window.Bounds.Width, (int)h.Window.Bounds.Height);
            }
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // 다른 문서(SQL 작업 공간 복원 등)가 먼저 활성이면 빼앗지 않지만 마지막 활성 작업 공간은 그대로 기억한다
    public Task Restore_opens_at_most_ten_workspaces_and_keeps_the_rest_in_the_index(bool otherActive) => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var persistence = Persistence(h);
        var other = otherActive ? docking.OpenDocument("oranda.panel.objectDetail", new Oranda.App.Features.ObjectDetail.ObjectDetailViewModel(), "SCOTT.EMP") : null;
        var ids = Enumerable.Range(0, 12).Select(i => $"w{i:00}").ToList();
        foreach (var id in ids)
        {
            await persistence.Store.SaveAsync(new PlsqlWorkspaceFile { Id = id, Name = "PL/SQL " + id, ActiveTab = 0, Tabs = [new PlsqlTabFile { Title = id, Text = "-- " + id }] });
        }

        await persistence.Store.SaveIndexAsync(new PlsqlWorkspaceIndex { OpenIds = ids, LastId = "w03" });
        try
        {
            await persistence.RestoreAsync(default);
            Dispatcher.UIThread.RunJobs();

            var open = docking.Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>().ToList();
            Assert.Equal(ids.Take(PlsqlWorkspacePersistence.RestoreLimit), open.Select(w => w.Id));
            if (other is null)
            {
                Assert.Equal("w03", ((PlsqlWorkspaceViewModel)docking.ActiveDocument!.Panel).Id);
            }
            else
            {
                Assert.Same(other, docking.ActiveDocument);
            }

            await persistence.SaveChangedAsync(default);
            var index = await persistence.Store.LoadIndexAsync();
            Assert.Equal(ids, index!.OpenIds);
            Assert.Equal("w03", index.LastId);
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task Restore_drops_missing_and_corrupt_files_from_the_index_and_keeps_a_newer_one_unopened() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var persistence = Persistence(h);
        var folder = persistence.Store.Folder;
        await persistence.Store.SaveAsync(new PlsqlWorkspaceFile { Id = "fine", Name = "PL/SQL 1" });
        await File.WriteAllTextAsync(Path.Combine(folder, "bad.json"), "{");
        const string newer = "{\"schemaVersion\":2,\"id\":\"newer\",\"name\":\"future\",\"tabs\":[]}";
        await File.WriteAllTextAsync(Path.Combine(folder, "newer.json"), newer);
        await File.WriteAllTextAsync(Path.Combine(folder, "reshaped.json"), "{\"schemaVersion\":2,\"id\":\"reshaped\"}"); // 이 판으로는 읽히지 않는 새 판
        await persistence.Store.SaveIndexAsync(new PlsqlWorkspaceIndex { OpenIds = ["gone", "bad", "newer", "reshaped", "fine", "Bad Id"], LastId = "gone" });
        try
        {
            await persistence.RestoreAsync(default);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("fine", Assert.IsType<PlsqlWorkspaceViewModel>(Assert.Single(docking.Documents).Panel).Id);
            await persistence.SaveChangedAsync(default);
            var index = await persistence.Store.LoadIndexAsync();
            Assert.Equal(["fine", "newer", "reshaped"], index!.OpenIds.ToArray());
            Assert.Equal("fine", index.LastId);
            Assert.Equal(newer, await File.ReadAllTextAsync(Path.Combine(folder, "newer.json")));
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task An_opened_session_attaches_to_the_waiting_tabs_of_its_profile_in_turn_and_turns_their_keys_on() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var keys = h.Get<Oranda.Core.Shell.IContextKeys>();
        var docking = h.Get<DockWorkspace>();
        var persistence = Persistence(h);
        var session = new SchemaOpenTests.OpenFakeSession();
        var otherProfile = Guid.NewGuid();
        PlsqlTabFile Tab(string name, Guid profile) =>
            new() { Owner = "SCOTT", Name = name, Type = "PROCEDURE", ConnectionProfileId = profile, Title = "SCOTT." + name, Text = "CREATE OR REPLACE PROCEDURE " + name };
        await persistence.Store.SaveAsync(new PlsqlWorkspaceFile
        {
            Id = "ws", Name = "PL/SQL 1", ActiveTab = 0, Tabs = [Tab("A", session.Profile.Id), Tab("B", session.Profile.Id), Tab("C", otherProfile)],
        });
        await persistence.Store.SaveIndexAsync(new PlsqlWorkspaceIndex { OpenIds = ["ws"], LastId = "ws" });
        try
        {
            await persistence.RestoreAsync(default);
            Dispatcher.UIThread.RunJobs();
            var tabs = ((PlsqlWorkspaceViewModel)Assert.Single(docking.Documents).Panel).Tabs;
            Assert.All(tabs, t => Assert.True(t.AwaitingSession));
            Assert.False(keys.Evaluate("plsqlRunnable"));

            persistence.AttachOpened(session);
            for (var i = 0; i < 100 && tabs.Take(2).Any(t => t.AwaitingSession || t.IsBusy); i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Same(session, tabs[0].Session);
            Assert.Same(session, tabs[1].Session);
            Assert.True(tabs[2].AwaitingSession); // 다른 접속의 탭은 그대로 기다린다
            Assert.True(keys.Evaluate("plsqlRunnable"));
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task Closing_a_workspace_moves_its_file_to_backup_but_closing_during_shutdown_keeps_it() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, h.Services);
        var persistence = Persistence(h);
        persistence.Wire();
        var first = new PlsqlEditorViewModel();
        first.StartNew("-- first\n", null);
        var second = new PlsqlEditorViewModel();
        second.StartNew("-- second\n", null);
        try
        {
            var closed = router.Place(first)!;
            var closedId = ((PlsqlWorkspaceViewModel)closed.Panel).Id;
            await persistence.SaveChangedAsync(default);

            Assert.True(docking.CloseDocument(closed));
            await persistence.FlushAsync(default);

            Assert.False(File.Exists(Path.Combine(persistence.Store.Folder, closedId + ".json")));
            Assert.Single(Directory.GetFiles(persistence.Store.BackupDirectory, closedId + ".json.closed-*"));

            var kept = router.Place(second)!;
            var keptId = ((PlsqlWorkspaceViewModel)kept.Panel).Id;
            await persistence.ShutdownAsync(default); // 종료 작업 뒤에는 저장하지 않는다: 창이 닫히며 문서가 닫혀도 파일·목록은 그대로
            Assert.True(docking.CloseDocument(kept));

            Assert.True(File.Exists(Path.Combine(persistence.Store.Folder, keptId + ".json")));
            Assert.Equal([keptId], (await persistence.Store.LoadIndexAsync())!.OpenIds.ToArray());
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    private static PlsqlWorkspacePersistence Persistence(ShellHarness h) =>
        h.Services.GetService<IEnumerable<IStartupTask>>()!.OfType<PlsqlWorkspacePersistence>().Single();
}
