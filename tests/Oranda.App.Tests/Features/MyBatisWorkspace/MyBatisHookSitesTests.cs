using System.ComponentModel;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Storage;
using Oranda.Core.Workspaces;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-01-02 B6: 호스트의 MyBatis 훅 자리(design.md 9장)가 가짜 훅을 부르는지, 훅이 없으면 지금 경로로 가는지 본다.
/// 기능(매퍼 열기·저장·복구 본체)은 03-* 이 만든다. 여기서는 부르는 자리만 단정한다.
/// </summary>
public sealed class MyBatisHookSitesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbhooks-" + Guid.NewGuid().ToString("N"));

    public MyBatisHookSitesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteMapper()
    {
        var path = Path.Combine(_root, "EmpMapper.xml");
        File.WriteAllText(path, MapperSamples.EmpMapper);
        return path;
    }

    /// <summary>MyBatis 세션이 붙은 작업 공간 하나와 문장 탭 하나를 가진 호스트.</summary>
    private static (SqlEditorHost Host, SqlWorkspaceViewModel Ws, SqlEditorViewModel Tab, FakeMyBatisWorkspaceSession Session) NewMapperHost(
        MyBatisHooks hooks, IWorkspaceStore? store = null, IEditorDialogs? dialogs = null)
    {
        var host = new SqlEditorHost(store, dialogs) { MyBatisHooks = hooks };
        var ws = host.EnsureWorkspace();
        ws.SourcePath = "/tmp/UserMapper.xml";
        var session = new FakeMyBatisWorkspaceSession();
        ws.MyBatis = session;
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual") { Title = "selectUser" };
        tab.IsDirty = false;
        host.AddMyBatisTab(ws, tab);
        return (host, ws, tab, session);
    }

    // ---- ① 열기 ---------------------------------------------------------------------------

    [Fact]
    public Task Dropped_xml_goes_to_the_open_handler_only_when_one_is_registered() => Ui.Run(async () =>
    {
        var path = WriteMapper();
        Assert.Empty(DroppedWorkspaceFiles.Pick([path])); // 훅이 없으면 지금처럼 .sqd 만
        Assert.Equal([Path.GetFullPath(path)], DroppedWorkspaceFiles.Pick([path], includeMyBatis: true));

        var bare = new SqlEditorHost();
        Assert.False(await DroppedWorkspaceFiles.OpenAsync(bare, [path], default));

        var handler = new FakeOpenHandler(handled: true);
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: handler) };
        Assert.True(await DroppedWorkspaceFiles.OpenAsync(host, [path], default));
        Assert.Equal([Path.GetFullPath(path)], handler.Paths);
    });

    [Fact]
    public Task Opening_an_xml_workspace_file_goes_to_the_open_handler_and_skips_the_read_only_expansion() => Ui.Run(async () =>
    {
        var path = WriteMapper();
        var handler = new FakeOpenHandler(handled: true);
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: handler) };

        var file = await host.OpenWorkspaceFileAsync(path, default);

        Assert.Equal([Path.GetFullPath(path)], handler.Paths);
        Assert.NotNull(file);
        Assert.Empty(file.Tabs);
        Assert.Empty(host.Workspaces); // 지금 경로(읽기 전용 펼침 작업 공간)는 만들지 않는다
    });

    [Fact]
    public Task Opening_an_xml_file_goes_to_the_open_handler_and_returns_the_active_tab() => Ui.Run(async () =>
    {
        var path = WriteMapper();
        var handler = new FakeOpenHandler(handled: true);
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: handler) };

        var tab = await host.OpenFileAsync(path, default);

        Assert.Equal([Path.GetFullPath(path)], handler.Paths);
        Assert.Null(tab); // 활성 탭이 없다
        Assert.Empty(host.Tabs);
    });

    [Fact]
    public Task Unhandled_or_missing_open_handler_keeps_the_current_xml_paths() => Ui.Run(async () =>
    {
        var path = WriteMapper();
        foreach (var hooks in new[] { MyBatisHooks.Empty, new MyBatisHooks(openHandler: new FakeOpenHandler(handled: false)) })
        {
            var host = new SqlEditorHost { MyBatisHooks = hooks };

            await host.OpenWorkspaceFileAsync(path, default);
            var ws = Assert.Single(host.Workspaces);
            Assert.Equal(SqlWorkspaceKind.Sql, ws.DocumentKind);
            Assert.NotEmpty(ws.Tabs); // 문장마다 읽기 전용 SQL 탭

            var text = await host.OpenFileAsync(path, default);
            Assert.NotNull(text);
            Assert.Equal(Path.GetFullPath(path), text.FilePath); // 일반 글자 탭
        }
    });

    [Fact]
    public Task Open_handler_is_not_asked_about_non_xml_files() => Ui.Run(async () =>
    {
        var sql = Path.Combine(_root, "a.sql");
        File.WriteAllText(sql, "select 1 from dual");
        var handler = new FakeOpenHandler(handled: true);
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: handler) };

        Assert.NotNull(await host.OpenFileAsync(sql, default));

        Assert.Empty(handler.Paths);
    });

    // ---- ② 저장 ---------------------------------------------------------------------------

    [Fact]
    public Task Saving_a_mybatis_tab_goes_to_the_save_handler_once() => Ui.Run(async () =>
    {
        var saver = new FakeSaveHandler();
        var (host, ws, tab, _) = NewMapperHost(new MyBatisHooks(saveHandler: saver));

        Assert.True(await host.SaveAsync(tab, default));

        Assert.Equal([ws], saver.Saved);
    });

    [Fact]
    public Task Saving_a_mybatis_tab_without_a_save_handler_fails_and_save_as_is_refused() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs();
        var (host, _, tab, _) = NewMapperHost(MyBatisHooks.Empty, dialogs: dialogs);

        Assert.False(await host.SaveAsync(tab, default));
        Assert.False(await host.SaveAsAsync(tab, default));

        Assert.Equal(0, dialogs.SavePicks); // 「다른 이름으로」 대화상자도 열지 않는다
    });

    [Fact]
    public Task Save_all_calls_the_save_handler_once_per_dirty_mybatis_workspace_and_skips_its_tabs() => Ui.Run(async () =>
    {
        var saver = new FakeSaveHandler();
        var (host, ws, tab, session) = NewMapperHost(new MyBatisHooks(saveHandler: saver));
        var second = new SqlEditorViewModel(Guid.NewGuid(), "select 2 from dual") { Title = "selectTwo" };
        host.AddMyBatisTab(ws, second);
        tab.IsDirty = true;
        second.IsDirty = true;
        session.IsDirty = true;

        Assert.True(await host.SaveAllAsync(default));

        Assert.Equal([ws], saver.Saved); // 탭 두 개가 모두 dirty 여도 작업 공간 단위로 한 번
    });

    [Fact]
    public Task Save_all_ignores_a_clean_mybatis_workspace_and_fails_when_a_dirty_one_has_no_handler() => Ui.Run(async () =>
    {
        var saver = new FakeSaveHandler();
        var (host, _, _, session) = NewMapperHost(new MyBatisHooks(saveHandler: saver));
        Assert.True(await host.SaveAllAsync(default));
        Assert.Empty(saver.Saved);

        var (bare, _, _, dirty) = NewMapperHost(MyBatisHooks.Empty);
        dirty.IsDirty = true;
        Assert.False(await bare.SaveAllAsync(default));
        Assert.False(session.IsDirty);
    });

    // ---- ③ 닫기 ---------------------------------------------------------------------------

    [Fact]
    public Task Closing_a_dirty_mybatis_workspace_with_no_tabs_asks_the_save_handler_and_cancel_keeps_it_open() => Ui.Run(async () =>
    {
        var saver = new FakeSaveHandler { ConfirmClose = false };
        var (host, ws, tab, session) = NewMapperHost(new MyBatisHooks(saveHandler: saver));
        host.Close(tab); // 문장 탭을 닫아도 고침은 세션에 남는다
        Assert.Empty(ws.Tabs);
        session.IsDirty = true;

        Assert.False(await host.TryCloseWorkspaceAsync(ws, default));
        Assert.Equal([ws], saver.ConfirmClosed);
        Assert.Contains(ws, host.Workspaces);
        Assert.Same(session, ws.MyBatis);

        saver.ConfirmClose = true;
        Assert.True(await host.TryCloseWorkspaceAsync(ws, default));
        Assert.DoesNotContain(ws, host.Workspaces);
        Assert.Null(ws.MyBatis); // 닫으면 세션을 뗀다
        Assert.Equal(0, session.StateChangedSubscribers);
    });

    [Fact]
    public Task Closing_a_clean_mybatis_workspace_does_not_ask() => Ui.Run(async () =>
    {
        var saver = new FakeSaveHandler { ConfirmClose = false };
        var (host, ws, _, _) = NewMapperHost(new MyBatisHooks(saveHandler: saver));

        Assert.True(await host.TryCloseWorkspaceAsync(ws, default));

        Assert.Empty(saver.ConfirmClosed);
    });

    [Fact]
    public Task Closing_a_mybatis_statement_tab_does_not_ask_to_save_and_is_not_remembered() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs();
        var (host, ws, tab, _) = NewMapperHost(MyBatisHooks.Empty, dialogs: dialogs);
        tab.IsDirty = true;

        Assert.True(await host.CloseAsync(tab, default));

        Assert.Equal(0, dialogs.SaveAsked);
        Assert.Empty(ws.Tabs);
        Assert.False(host.CanReopenClosed);
    });

    // ---- ④ 새 SQL 탭은 일반 작업 공간으로 --------------------------------------------------------

    [Fact]
    public Task New_document_while_a_mybatis_workspace_is_active_goes_to_the_last_sql_workspace() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var plain = host.NewWorkspace("plain");
        var (_, ws, tab, _) = MapperIn(host);
        Assert.Same(ws, host.ActiveWorkspace);
        Assert.Same(tab, host.Active);

        var created = host.NewDocument("select 1 from dual");

        Assert.Same(plain, host.WorkspaceOf(created));
        Assert.Same(plain, host.ActiveWorkspace);
        Assert.Equal(SqlWorkspaceKind.Sql, plain.DocumentKind);
        Assert.Single(ws.Tabs); // 문장 탭 하나 그대로
    });

    [Fact]
    public Task New_document_with_only_a_mybatis_workspace_opens_a_new_sql_workspace() => Ui.Run(() =>
    {
        var (host, ws, _, _) = NewMapperHost(MyBatisHooks.Empty);

        var created = host.NewDocument();
        var target = host.WorkspaceOf(created);

        Assert.NotNull(target);
        Assert.NotSame(ws, target);
        Assert.Null(target.MyBatis);
        Assert.Equal(2, host.Workspaces.Count);
        Assert.Single(ws.Tabs);
        Assert.Single(target.Tabs); // 빈 탭이 하나 더 생기지 않는다
        Assert.Same(target, host.ActiveWorkspace);
    });

    [Fact]
    public Task Duplicating_a_mybatis_tab_makes_a_sql_tab_in_a_sql_workspace() => Ui.Run(() =>
    {
        var (host, ws, tab, _) = NewMapperHost(MyBatisHooks.Empty);

        var copy = host.Duplicate(tab);

        Assert.NotSame(ws, host.WorkspaceOf(copy));
        Assert.Null(host.WorkspaceOf(copy)!.MyBatis);
        Assert.Equal(tab.Text, copy.Text);
    });

    [Fact]
    public Task Without_a_session_new_document_stays_in_the_active_workspace() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var a = host.NewWorkspace("a");
        var b = host.NewWorkspace("b");

        Assert.Same(b, host.WorkspaceOf(host.NewDocument()));
        host.ActiveWorkspace = a;
        Assert.Same(a, host.WorkspaceOf(host.NewDocument()));
        Assert.Equal(2, host.Workspaces.Count);
    });

    // ---- ⑤ 복구 참여자 -------------------------------------------------------------------------

    [Fact]
    public Task Autosave_writes_participant_state_only_for_mybatis_workspaces_and_follows_the_fingerprint() => Ui.Run(async () =>
    {
        var participant = new FakeParticipant();
        var store = new FakeStore();
        var (host, ws, _, _) = NewMapperHost(new MyBatisHooks(recoveryParticipants: [participant]), store);
        host.NewWorkspace("plain");

        Assert.True(await host.AutosaveAsync(default));
        Assert.Equal([$"save {ws.Id} Autosave session=True"], participant.Calls); // 일반 작업 공간에는 부르지 않는다
        Assert.Equal(2, store.Autosaved.Count);

        Assert.False(await host.AutosaveAsync(default)); // 바뀐 것이 없다
        participant.Print = "changed";
        Assert.True(await host.AutosaveAsync(default)); // MyBatis 쪽만 바뀌어도 자동 저장이 돈다
        Assert.Equal(2, participant.Calls.Count);
        Assert.Contains("fp", participant.FingerprintCalls); // 표지를 물었다
    });

    [Fact]
    public Task Shutdown_saves_participant_state_and_deletes_the_autosave_state() => Ui.Run(async () =>
    {
        var participant = new FakeParticipant();
        var store = new FakeStore();
        var (host, ws, _, _) = NewMapperHost(new MyBatisHooks(recoveryParticipants: [participant]), store);

        await host.ShutdownAsync(default);

        Assert.Equal([$"save {ws.Id} Saved session=True", $"delete {ws.Id} Autosave"], participant.Calls);
        Assert.Equal([ws.Id], store.AutosaveDeleted);
    });

    [Fact]
    public Task Closing_a_workspace_saves_participant_state_before_the_session_is_detached() => Ui.Run(async () =>
    {
        var participant = new FakeParticipant();
        var store = new FakeStore();
        var (host, ws, _, session) = NewMapperHost(new MyBatisHooks(recoveryParticipants: [participant]), store);
        var id = ws.Id;

        await host.CloseWorkspaceAsync(ws, default);

        Assert.Equal([$"save {id} Saved session=True", $"delete {id} Autosave"], participant.Calls);
        Assert.Null(ws.MyBatis);
        Assert.Equal(0, session.StateChangedSubscribers);
        Assert.Contains(id, store.Saved.Keys);
    });

    [Fact]
    public Task Restore_tells_participants_which_side_file_to_read() => Ui.Run(() =>
    {
        var participant = new FakeParticipant();
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(recoveryParticipants: [participant]) };
        Workspace Mapper(string id, string path) => new()
        {
            Id = id,
            Name = id,
            SavedAt = DateTimeOffset.UtcNow,
            SourcePath = path,
            Tabs = [new WorkspaceTab { Id = Guid.NewGuid(), Title = "selectUser", Content = "select 1 from dual" }],
        };

        var saved = host.Restore(Mapper("saved", "/tmp/A.xml"));
        host.Restore(Mapper("auto", "/tmp/B.xml"), MyBatisRecoveryTarget.Autosave);
        host.Restore(Mapper("plain", "/tmp/C.md"));
        host.Restore(Mapper("none", null!));

        Assert.Equal(["restore saved Saved", "restore auto Autosave"], participant.Calls);
        Assert.Equal(SqlWorkspaceKind.Sql, saved.DocumentKind); // 가짜 참여자는 세션을 붙이지 않는다
    });

    [Fact]
    public Task Initialize_keeps_an_empty_mapper_autosave_only_when_a_participant_can_restore_it() => Ui.Run(async () =>
    {
        var emptyMapper = new Workspace { Id = "mapper-1", Name = "mapper", SavedAt = DateTimeOffset.UtcNow, SourcePath = "/tmp/A.xml" };

        // 참여자가 있다: 탭이 0개여도 복구 대상이고 Autosave 곁 파일을 읽는다.
        var participant = new FakeParticipant();
        var withHook = new FakeStore();
        withHook.AutosaveDocs[emptyMapper.Id] = emptyMapper;
        var host = new SqlEditorHost(withHook, new FakeDialogs { Confirm = true }) { MyBatisHooks = new MyBatisHooks(recoveryParticipants: [participant]) };
        Assert.True(await host.InitializeAsync(default));
        Assert.Equal(["restore mapper-1 Autosave"], participant.Calls);
        Assert.Empty(withHook.AutosaveDeleted);

        // 훅이 없다: 지금 동작 그대로 빈 자동 저장은 지운다.
        var noHook = new FakeStore();
        noHook.AutosaveDocs[emptyMapper.Id] = emptyMapper;
        var bare = new SqlEditorHost(noHook, new FakeDialogs { Confirm = true });
        Assert.False(await bare.InitializeAsync(default));
        Assert.Equal(["mapper-1"], noHook.AutosaveDeleted);
        Assert.Empty(bare.Workspaces);
    });

    [Fact]
    public Task Declined_autosave_recovery_deletes_the_participant_state_too() => Ui.Run(async () =>
    {
        var participant = new FakeParticipant();
        var store = new FakeStore();
        store.AutosaveDocs["u"] = new Workspace
        {
            Id = "u",
            Name = "u",
            SavedAt = DateTimeOffset.UtcNow,
            Tabs = [new WorkspaceTab { Id = Guid.NewGuid(), Title = "SQL 1", Content = "select 1 from dual" }],
        };
        var host = new SqlEditorHost(store, new FakeDialogs { Confirm = false }) { MyBatisHooks = new MyBatisHooks(recoveryParticipants: [participant]) };

        await host.InitializeAsync(default);

        Assert.Equal(["delete u Autosave"], participant.Calls);
        Assert.Equal(["u"], store.AutosaveDeleted);
    });

    [Fact]
    public Task Saving_the_workspace_under_a_new_id_renames_and_rewrites_the_participant_state() => Ui.Run(async () =>
    {
        var participant = new FakeParticipant();
        var store = new FakeStore();
        var (host, ws, _, _) = NewMapperHost(new MyBatisHooks(recoveryParticipants: [participant]), store, new FakeDialogs { Prompt = "My Mapper" });
        var oldId = ws.Id;

        Assert.True(await host.SaveWorkspaceAsync(default));

        Assert.Equal("my-mapper", ws.Id);
        Assert.Equal(
            [$"rename {oldId}>my-mapper", "save my-mapper Saved session=True", $"delete {oldId} Autosave"],
            participant.Calls);
        Assert.Equal([oldId], store.AutosaveDeleted);
    });

    [Fact]
    public Task Without_participants_the_store_calls_are_exactly_the_old_ones() => Ui.Run(async () =>
    {
        var store = new FakeStore();
        var host = new SqlEditorHost(store, new FakeDialogs { Prompt = "Renamed" });
        var ws = host.NewWorkspace("w");
        var oldId = ws.Id;

        await host.AutosaveAsync(default);
        await host.SaveWorkspaceAsync(default);
        await host.ShutdownAsync(default);
        await host.CloseWorkspaceAsync(ws, default);

        Assert.Equal(
            [$"autosave {oldId}", "save renamed", $"delete-autosave {oldId}", "save renamed", "delete-autosave renamed", "save renamed", "delete-autosave renamed"],
            store.Log);
    });

    // ---- ⑥ 작업 공간 IsDirty ------------------------------------------------------------------

    [Fact]
    public Task Workspace_dirty_follows_the_session_and_unsubscribes_when_the_session_is_detached() => Ui.Run(() =>
    {
        var (_, ws, _, session) = NewMapperHost(MyBatisHooks.Empty);
        var raised = new List<string?>();
        ws.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(ws.IsDirty);

        session.IsDirty = true;
        Assert.True(ws.IsDirty);
        Assert.StartsWith("* ", ws.DisplayTitle, StringComparison.Ordinal);
        Assert.Contains(nameof(SqlWorkspaceViewModel.IsDirty), raised);
        Assert.Contains(nameof(SqlWorkspaceViewModel.DisplayTitle), raised);

        var replacement = new FakeMyBatisWorkspaceSession();
        ws.MyBatis = replacement; // 세션이 바뀌면 옛 구독을 풀고 새 세션을 따른다
        Assert.Equal(0, session.StateChangedSubscribers);
        Assert.False(ws.IsDirty);
        replacement.IsDirty = true;
        Assert.True(ws.IsDirty);

        ws.MyBatis = null;
        Assert.Equal(0, replacement.StateChangedSubscribers);
        Assert.False(ws.IsDirty);
    });

    [Fact]
    public Task Renaming_a_mybatis_tab_is_a_no_op_and_stamps_include_the_participant_fingerprint() => Ui.Run(() =>
    {
        var participant = new FakeParticipant();
        var (_, ws, tab, _) = NewMapperHost(new MyBatisHooks(recoveryParticipants: [participant]));
        Assert.False(ws.IsTabRenameAllowed);

        ws.BeginRenameTabCommand.Execute(tab);
        Assert.False(tab.IsRenaming);

        Assert.True(ws.ChangedSinceAutosave(out var first));
        ws.MarkAutosaved(first);
        Assert.False(ws.ChangedSinceAutosave(out _));
        participant.Print = "x";
        Assert.True(ws.ChangedSinceAutosave(out var second));
        Assert.Contains("fp:x", second, StringComparison.Ordinal);
    });

    [Fact]
    public Task Plain_workspace_stamp_ignores_participants_and_renaming_still_works() => Ui.Run(() =>
    {
        var participant = new FakeParticipant();
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(recoveryParticipants: [participant]) };
        var ws = host.NewWorkspace("plain");
        var tab = ws.Tabs[0];

        ws.ChangedSinceAutosave(out var stamp);
        participant.Print = "other";
        Assert.False(ws.ChangedSinceAutosave(out var again) && again != stamp);
        Assert.Empty(participant.FingerprintCalls);
        Assert.True(ws.IsTabRenameAllowed);
        ws.BeginRenameTabCommand.Execute(tab);
        Assert.True(tab.IsRenaming);
    });

    // ---- ⑦ 문맥 키 ---------------------------------------------------------------------------

    [Fact]
    public Task Context_keys_are_computed_from_the_active_document_panel() => Ui.Run(() =>
    {
        Assert.Equal(default, MyBatisWorkspaceWiring.ComputeKeys(null));

        var host = new SqlEditorHost();
        var plain = host.NewWorkspace("plain");
        Assert.Equal(default, MyBatisWorkspaceWiring.ComputeKeys(plain));

        // PL/SQL·객체 상세 같은 다른 문서가 활성이면(Panel 이 작업 공간이 아니다) 모두 거짓.
        Assert.Equal(default, MyBatisWorkspaceWiring.ComputeKeys(new object()));

        var (_, ws, _, session) = NewMapperHost(MyBatisHooks.Empty);
        Assert.Equal(new MyBatisContextKeys(true, false, false), MyBatisWorkspaceWiring.ComputeKeys(ws));
        session.IsDirty = true;
        Assert.Equal(new MyBatisContextKeys(true, true, false), MyBatisWorkspaceWiring.ComputeKeys(ws));
        session.SelectStatement("selectUser");
        Assert.Equal(new MyBatisContextKeys(true, true, true), MyBatisWorkspaceWiring.ComputeKeys(ws));
        session.SelectStatement(null);
        Assert.False(MyBatisWorkspaceWiring.ComputeKeys(ws).StatementSelected);
    });

    [Fact]
    public Task Wiring_installs_hooks_and_feeds_the_context_keys_from_the_active_dock_document_and_session_changes() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var keys = new ContextKeyService();
        var host = new SqlEditorHost();
        var container = new ServiceContainer();
        var wiring = new MyBatisWorkspaceWiring(container, keys, docking);
        Assert.Same(MyBatisHooks.Empty, host.MyBatisHooks);

        wiring.Attach(host);
        Assert.NotSame(MyBatisHooks.Empty, host.MyBatisHooks);
        var plain = host.NewWorkspace("plain");
        var mapper = host.NewWorkspace("mapper");
        var plainDoc = docking.OpenDocument("oranda.panel.sqlEditor", plain)!;
        var mapperDoc = docking.OpenDocument("oranda.panel.sqlEditor", mapper)!;
        var plsqlDoc = docking.OpenDocument("oranda.panel.plsqlEditor", new object())!;
        var detailDoc = docking.OpenDocument("oranda.panel.objectDetail")!;

        docking.Activate(plainDoc);
        AssertKeys(keys, false, false, false);

        docking.Activate(mapperDoc);
        var session = new FakeMyBatisWorkspaceSession();
        mapper.MyBatis = session; // 같은 Dock 문서 안에서 종류가 바뀐다: DocumentKind 알림이 전환 신호
        AssertKeys(keys, true, false, false);
        session.IsDirty = true;
        session.SelectStatement("selectUser");
        AssertKeys(keys, true, true, true);

        docking.Activate(plainDoc); // 옛 작업 공간의 세션 구독을 풀고 키를 내린다
        AssertKeys(keys, false, false, false);
        Assert.Equal(1, session.StateChangedSubscribers); // 작업 공간 자신의 구독만 남는다
        session.IsDirty = false;
        AssertKeys(keys, false, false, false);

        docking.Activate(mapperDoc);
        AssertKeys(keys, true, false, true);

        // PL/SQL·객체 상세 문서가 활성이면 호스트의 활성 작업 공간이 MyBatis 문서 그대로여도 거짓이다.
        host.ActiveWorkspace = mapper;
        docking.Activate(plsqlDoc);
        AssertKeys(keys, false, false, false);
        session.IsDirty = true;
        AssertKeys(keys, false, false, false);
        docking.Activate(mapperDoc);
        AssertKeys(keys, true, true, true);
        docking.Activate(detailDoc);
        AssertKeys(keys, false, false, false);

        docking.Activate(mapperDoc);
        mapper.MyBatis = null;
        AssertKeys(keys, false, false, false);
        Assert.Equal(0, session.StateChangedSubscribers);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Wiring_run_installs_the_hooks_synchronously_without_posting() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var previous = SqlEditorHost.Current;
        var current = new SqlEditorHost();
        SqlEditorHost.Current = current;
        try
        {
            var wiring = new MyBatisWorkspaceWiring(new ServiceContainer(), new ContextKeyService(), h.Get<DockWorkspace>());
            Assert.Same(MyBatisHooks.Empty, current.MyBatisHooks);

            var run = wiring.RunAsync(CancellationToken.None);

            // 작업 공간 복구(Post 된 InitializeAsync)가 훅 자리를 읽기 전에 서 있어야 하므로 Post 없이 끝난다.
            Assert.True(run.IsCompletedSuccessfully);
            Assert.NotSame(MyBatisHooks.Empty, current.MyBatisHooks);
        }
        finally
        {
            SqlEditorHost.Current = previous;
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task New_tab_plus_in_a_mybatis_workspace_begins_a_new_statement_instead_of_a_plain_document() => Ui.Run(() =>
    {
        var (host, ws, _, session) = NewMapperHost(MyBatisHooks.Empty);
        var tabs = host.Tabs.Count;
        var workspaces = host.Workspaces.Count;

        ws.NewTabCommand.Execute(null);

        Assert.Equal(1, session.BeginNewStatementCalls);
        Assert.Equal(tabs, host.Tabs.Count); // 일반 새 문서는 만들지 않는다
        Assert.Equal(workspaces, host.Workspaces.Count);

        // 일반 SQL 작업 공간은 지금처럼 빈 탭을 더한다.
        var plain = host.NewWorkspace("plain");
        var before = plain.Tabs.Count;
        plain.NewTabCommand.Execute(null);
        Assert.Equal(before + 1, plain.Tabs.Count);
        Assert.Equal(1, session.BeginNewStatementCalls);
    });

    [Fact]
    public Task Hooks_from_the_container_are_resolved_lazily_in_order_without_scanning_deferred_assemblies() => Ui.Run(() =>
    {
        var container = new ServiceContainer().AddDeferredAssembly("Oranda.Language");
        var late = new FakeDecorator(200);
        var early = new FakeDecorator(0);
        var participant = new FakeParticipant();
        container.AddInstance<IMyBatisEditorDecorator>(late).AddInstance<IMyBatisEditorDecorator>(early).AddInstance<IMyBatisRecoveryParticipant>(participant);
        var hooks = new MyBatisHooks(container);

        Assert.Equal<IMyBatisEditorDecorator>([early, late], hooks.EditorDecorators);
        Assert.Same(participant, Assert.Single(hooks.RecoveryParticipants));
        // 구현이 없는 훅도 단일 형식을 묻지 않으므로 지연 어셈블리(Language·Oracle)를 훑지 않는다(D-006).
        Assert.Null(hooks.OpenHandler);
        Assert.Null(hooks.SaveHandler);
        Assert.Null(hooks.EditorViewFactory);
        Assert.Empty(hooks.BadgeProviders);
        Assert.Empty(hooks.ViewModes);
        Assert.Empty(hooks.StatementLocators);
        Assert.False(container.DeferredScanned, container.DeferredScanTrigger);
    });

    [Fact]
    public void Empty_hooks_have_nothing_and_a_single_registration_resolves_to_the_last_one()
    {
        Assert.Null(MyBatisHooks.Empty.OpenHandler);
        Assert.Null(MyBatisHooks.Empty.SaveHandler);
        Assert.Null(MyBatisHooks.Empty.EditorViewFactory);
        Assert.Empty(MyBatisHooks.Empty.RecoveryParticipants);
        Assert.Empty(MyBatisHooks.Empty.EditorDecorators);
        Assert.Empty(MyBatisHooks.Empty.BadgeProviders);
        Assert.Empty(MyBatisHooks.Empty.ViewModes);
        Assert.Empty(MyBatisHooks.Empty.StatementLocators);

        var first = new FakeOpenHandler(false);
        var last = new FakeOpenHandler(true);
        var container = new ServiceContainer().AddInstance<IMyBatisOpenHandler>(first).AddInstance<IMyBatisOpenHandler>(last);
        Assert.Same(last, new MyBatisHooks(container).OpenHandler);
    }

    // ---- 도우미 --------------------------------------------------------------------------------

    private static void AssertKeys(ContextKeyService keys, bool active, bool dirty, bool selected)
    {
        Assert.Equal(active, keys.Get("mybatisDocActive"));
        Assert.Equal(dirty, keys.Get("mybatisDocDirty"));
        Assert.Equal(selected, keys.Get("mybatisStatementSelected"));
    }

    private static (SqlEditorHost Host, SqlWorkspaceViewModel Ws, SqlEditorViewModel Tab, FakeMyBatisWorkspaceSession Session) MapperIn(SqlEditorHost host)
    {
        var ws = host.NewWorkspace("mapper");
        host.Close(ws.Tabs[0]);
        ws.SourcePath = "/tmp/UserMapper.xml";
        var session = new FakeMyBatisWorkspaceSession();
        ws.MyBatis = session;
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual") { Title = "selectUser" };
        tab.IsDirty = false;
        host.AddMyBatisTab(ws, tab);
        return (host, ws, tab, session);
    }

    private sealed class FakeOpenHandler(bool handled) : IMyBatisOpenHandler
    {
        public List<string> Paths { get; } = [];

        public Task<bool> TryOpenAsync(SqlEditorHost host, string fullPath, CancellationToken ct)
        {
            Paths.Add(fullPath);
            return Task.FromResult(handled);
        }
    }

    private sealed class FakeSaveHandler : IMyBatisSaveHandler
    {
        public List<SqlWorkspaceViewModel> Saved { get; } = [];

        public List<SqlWorkspaceViewModel> ConfirmClosed { get; } = [];

        public bool ConfirmClose { get; set; } = true;

        public Task<bool> SaveAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
        {
            Saved.Add(workspace);
            return Task.FromResult(true);
        }

        public Task<bool> ConfirmCloseAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
        {
            ConfirmClosed.Add(workspace);
            return Task.FromResult(ConfirmClose);
        }
    }

    private sealed class FakeDecorator(int order) : IMyBatisEditorDecorator
    {
        public int Order { get; } = order;

        public IDisposable Attach(MyBatisEditorContext context) => throw new NotSupportedException();
    }

    /// <summary>호출 기록: <c>save id target session=세션 붙어 있음</c>, <c>restore id target</c>, <c>delete id target</c>, <c>rename a&gt;b</c>.</summary>
    private sealed class FakeParticipant : IMyBatisRecoveryParticipant
    {
        public List<string> Calls { get; } = [];

        public List<string> FingerprintCalls { get; } = [];

        public string Print { get; set; } = "same";

        public string Key => "fp";

        public string? Fingerprint(SqlWorkspaceViewModel workspace)
        {
            FingerprintCalls.Add(Key);
            return Print;
        }

        public Task SaveAsync(SqlWorkspaceViewModel workspace, MyBatisRecoveryTarget target, CancellationToken ct)
        {
            Calls.Add($"save {workspace.Id} {target} session={workspace.MyBatis is not null}");
            return Task.CompletedTask;
        }

        public void Restore(SqlWorkspaceViewModel workspace, MyBatisRecoveryTarget target) => Calls.Add($"restore {workspace.Id} {target}");

        public Task DeleteAsync(string workspaceId, MyBatisRecoveryTarget target, CancellationToken ct)
        {
            Calls.Add($"delete {workspaceId} {target}");
            return Task.CompletedTask;
        }

        public Task RenameAsync(string oldWorkspaceId, string newWorkspaceId, CancellationToken ct)
        {
            Calls.Add($"rename {oldWorkspaceId}>{newWorkspaceId}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore : IWorkspaceStore
    {
        public Dictionary<string, Workspace> Saved { get; } = [];

        public Dictionary<string, Workspace> AutosaveDocs { get; } = [];

        public List<Workspace> Autosaved { get; } = [];

        public List<string> AutosaveDeleted { get; } = [];

        /// <summary>저장소 호출 순서(<c>autosave id</c>, <c>save id</c>, <c>delete-autosave id</c>).</summary>
        public List<string> Log { get; } = [];

        public Task<StoredDocument<Workspace>?> LoadAsync(string id, CancellationToken ct) =>
            Task.FromResult(Saved.TryGetValue(id, out var w) ? new StoredDocument<Workspace>(w) : null);

        public Task SaveAsync(Workspace workspace, CancellationToken ct)
        {
            Saved[workspace.Id] = workspace;
            Log.Add("save " + workspace.Id);
            return Task.CompletedTask;
        }

        public Task AutosaveAsync(Workspace workspace, CancellationToken ct)
        {
            Autosaved.Add(workspace);
            AutosaveDocs[workspace.Id] = workspace;
            Log.Add("autosave " + workspace.Id);
            return Task.CompletedTask;
        }

        public Task<Workspace?> LoadAutosaveAsync(string id, CancellationToken ct) =>
            Task.FromResult(AutosaveDocs.GetValueOrDefault(id));

        public Task DeleteAutosaveAsync(string id, CancellationToken ct)
        {
            AutosaveDeleted.Add(id);
            AutosaveDocs.Remove(id);
            Log.Add("delete-autosave " + id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListAutosaveIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([.. AutosaveDocs.Keys]);
    }

    private sealed class FakeDialogs : IEditorDialogs
    {
        public string? Prompt { get; set; }

        public bool Confirm { get; set; }

        public int SavePicks { get; private set; }

        public int SaveAsked { get; private set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct)
        {
            SavePicks++;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult(Prompt);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(Confirm);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct)
        {
            SaveAsked++;
            return Task.FromResult(SaveChoice.Cancel);
        }
    }
}
