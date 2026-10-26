using System.Text;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.PlsqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.PlsqlWorkspace;

/// <summary>
/// <c>.plsql-workspace.md</c> 공유 파일(B-3 b3): 글 형식 왕복·손으로 쓴 글·접속 이름 풀기(순수), 파일 열기(재열기·이미 열린 접속에 바로 붙기·읽기 실패)와 저장(덮어쓰기·대화상자·원자적 쓰기).
/// </summary>
[Collection(PlsqlStatics.Name)]
public sealed class PlsqlWorkspaceMarkdownTests : IDisposable
{
    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-plsql-md-" + Guid.NewGuid().ToString("N"));

    public PlsqlWorkspaceMarkdownTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        PlsqlEditorViewModel.Active = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- 형식(순수) ----

    [Fact]
    public void A_workspace_round_trips_including_fences_inside_the_text()
    {
        var tricky = "-- 울타리\n```sql\nSELECT 1;\n```\n````\n끝\n";
        var file = new PlsqlWorkspaceFile
        {
            Id = "old-id",
            Name = "주문 작업",
            ActiveTab = 1,
            Tabs =
            [
                new PlsqlTabFile
                {
                    Owner = "APP", Name = "PK_ORDER", Type = "PACKAGE BODY", ConnectionProfileId = ProfileA,
                    LastDdlTime = new DateTime(2026, 10, 24, 10, 0, 0, 123, DateTimeKind.Unspecified),
                    Title = "APP.PK_ORDER (본문)", Text = "CREATE OR REPLACE PACKAGE BODY \"APP\".pk_order AS\nEND;\n", IsDirty = true,
                },
                new PlsqlTabFile { Title = "새 PL/SQL", Text = tricky, IsDirty = true },
                new PlsqlTabFile
                {
                    Title = "emp.pkb", FilePath = "/work/emp.pkb", Encoding = "cp949", ConnectionProfileId = ProfileA, Text = "BEGIN NULL; END;\n",
                },
            ],
        };

        var md = PlsqlWorkspaceMarkdown.Write(file, id => id == ProfileA ? "ORA 개발" : null);
        var back = PlsqlWorkspaceMarkdown.Read(md, "fallback", name => name == "ORA 개발" ? ProfileA : null);

        Assert.StartsWith("# 주문 작업\n\n<!-- oranda-plsql-workspace 1 -->\n\n## APP.PK_ORDER (PACKAGE BODY)\n\n<!-- connection: ORA 개발 -->\n", md);
        Assert.Contains("\n`````sql\n", md); // 글 안 가장 긴 백틱(4) + 1
        Assert.DoesNotContain('\r', md);
        Assert.Equal("주문 작업", back.Name);
        Assert.NotEqual("old-id", back.Id);
        Assert.Equal(0, back.ActiveTab);
        Assert.Equal(3, back.Tabs.Count);
        Assert.All(back.Tabs, t => Assert.False(t.IsDirty));

        var server = back.Tabs[0];
        Assert.Equal(("APP", "PK_ORDER", "PACKAGE BODY"), (server.Owner, server.Name, server.Type));
        Assert.Null(server.Title); // 복원할 때 정한다
        Assert.Equal(ProfileA, server.ConnectionProfileId);
        Assert.Equal(file.Tabs[0].LastDdlTime, server.LastDdlTime);
        Assert.Equal(file.Tabs[0].Text, server.Text);

        Assert.Equal(("새 PL/SQL", tricky), (back.Tabs[1].Title, back.Tabs[1].Text));
        Assert.Null(back.Tabs[1].ConnectionProfileId);
        Assert.Null(back.Tabs[1].Owner);

        var fileTab = back.Tabs[2];
        Assert.Equal(("emp.pkb", "/work/emp.pkb", "cp949"), (fileTab.Title, fileTab.FilePath, fileTab.Encoding));
        Assert.Null(fileTab.Owner);
        Assert.Equal(ProfileA, fileTab.ConnectionProfileId);
    }

    [Fact]
    public void A_hand_written_file_without_a_cover_opens_and_unknown_connections_become_none()
    {
        var md = "intro 설명\r\n\r\n## SCOTT.P (PROCEDURE)\r\n\r\n<!-- connection: 없는 접속 -->\r\n<!-- lastDdlTime: 2026-10-24T10:00:00 -->\r\n\r\n```plsql\r\nCREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;\r\n```\r\n\r\n```sql\r\n-- 같은 제목 아래 둘째 블록은 설명이라 건너뛴다\r\n```\r\n";

        var file = PlsqlWorkspaceMarkdown.Read(md, "손글씨", _ => null);

        Assert.Equal("손글씨", file.Name);
        Assert.Single(file.Tabs);
        Assert.Equal(("SCOTT", "P", "PROCEDURE"), (file.Tabs[0].Owner, file.Tabs[0].Name, file.Tabs[0].Type));
        Assert.Null(file.Tabs[0].ConnectionProfileId);
        Assert.Equal(new DateTime(2026, 10, 24, 10, 0, 0), file.Tabs[0].LastDdlTime);
        Assert.Equal("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;\n", file.Tabs[0].Text);
        Assert.Equal("PL/SQL 1", Assert.Single(PlsqlWorkspaceMarkdown.Read("```sql\nBEGIN NULL; END;\n```\n", "f", _ => null).Tabs).Title);

        var plain = PlsqlWorkspaceMarkdown.Read("그냥 글", "이름", _ => null);
        Assert.Equal("그냥 글", Assert.Single(plain.Tabs).Text);
    }

    [Fact]
    public void A_newer_format_version_is_refused_and_the_suggested_name_has_no_path_separator()
    {
        Assert.Throws<FormatException>(() => PlsqlWorkspaceMarkdown.Read("# x\n\n<!-- oranda-plsql-workspace 2 -->\n", "f", _ => null));
        Assert.Equal("PL-SQL 1.plsql-workspace.md", PlsqlWorkspaceMarkdown.SuggestFileName("PL/SQL 1"));
        Assert.True(PlsqlWorkspaceMarkdown.IsWorkspaceFile("/a/B.PLSQL-WORKSPACE.MD"));
        Assert.False(PlsqlWorkspaceMarkdown.IsWorkspaceFile("/a/b.workspace.md"));
    }

    [Fact]
    public async Task Source_path_survives_the_b2_json_round_trip_and_the_viewmodel_capture()
    {
        var workspace = new PlsqlWorkspaceViewModel { Name = "w", SourcePath = "/x/w.plsql-workspace.md" };
        var store = new PlsqlWorkspaceStore(Path.Combine(_dir, "store"), Path.Combine(_dir, "backup"));

        await store.SaveAsync(workspace.Capture());
        var loaded = await store.LoadAsync(workspace.Id);

        Assert.Equal("/x/w.plsql-workspace.md", loaded!.SourcePath);
        Assert.Equal(1, loaded.SchemaVersion);
    }

    [Fact]
    public void Identifiers_that_are_not_plain_are_quoted_in_the_heading_and_round_trip()
    {
        var file = new PlsqlWorkspaceFile
        {
            Id = "x", Name = "q",
            Tabs =
            [
                new PlsqlTabFile { Owner = "my app", Name = "a.b (c)", Type = "PROCEDURE", Text = "BEGIN NULL; END;\n" },
                new PlsqlTabFile { Owner = "scott", Name = "say\"hi", Type = "PACKAGE BODY", Text = "x\n" },
                new PlsqlTabFile { Owner = "SCOTT", Name = "P_1$#", Type = "FUNCTION", Text = "y\n" },
            ],
        };

        var md = PlsqlWorkspaceMarkdown.Write(file, _ => null);
        var back = PlsqlWorkspaceMarkdown.Read(md, "f", _ => null);

        Assert.Contains("## \"my app\".\"a.b (c)\" (PROCEDURE)\n", md, StringComparison.Ordinal);
        Assert.Contains("## SCOTT.P_1$# (FUNCTION)\n", md, StringComparison.Ordinal);
        Assert.Equal([("my app", "a.b (c)", "PROCEDURE"), ("scott", "say\"hi", "PACKAGE BODY"), ("SCOTT", "P_1$#", "FUNCTION")],
            back.Tabs.Select(t => (t.Owner!, t.Name!, t.Type!)));
    }

    [Fact]
    public void Fence_info_strings_the_version_mark_and_unknown_encodings_are_read_defensively()
    {
        var info = PlsqlWorkspaceMarkdown.Read("## a\n\n```sql title=\"x y\" {1}\nSELECT 1;\n```\n\n## b\n\n~~~ plsql `x`\nSELECT 2;\n~~~\n", "f", _ => null);
        Assert.Equal(["SELECT 1;\n", "SELECT 2;\n"], info.Tabs.Select(t => t.Text));

        Assert.Throws<FormatException>(() => PlsqlWorkspaceMarkdown.Read("<!-- oranda-plsql-workspace 99999999999999999999 -->\n", "f", _ => null));

        var enc = PlsqlWorkspaceMarkdown.Read("## a.pkb\n\n<!-- file: /w/a.pkb -->\n<!-- encoding: koi8-r -->\n\n```sql\nx\n```\n", "f", _ => null);
        Assert.Equal(("/w/a.pkb", "utf-8"), (enc.Tabs[0].FilePath, enc.Tabs[0].Encoding));
    }

    [Fact]
    public void A_server_tab_keeps_its_first_line_column_shift_through_the_file()
    {
        var file = new PlsqlWorkspaceFile
        {
            Id = "x", Name = "q",
            Tabs = [new PlsqlTabFile { Owner = "SCOTT", Name = "P", Type = "PROCEDURE", Line1Shift = 7, Text = "CREATE OR REPLACE PROCEDURE \"SCOTT\".p IS\nEND;\n" }],
        };

        var back = PlsqlWorkspaceMarkdown.Read(PlsqlWorkspaceMarkdown.Write(file, _ => null), "f", _ => null);

        Assert.Equal(7, back.Tabs[0].Line1Shift);
    }

    // ---- 열기·저장 ----

    [Fact]
    public Task Opening_the_file_sets_source_path_resolves_names_and_attaches_an_already_open_session_at_once() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var session = await Fixture.PrepareAsync(h);
        var path = await WriteWorkspaceAsync(session);
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();

        await registry.ExecuteAsync("oranda.file.open", keys, path);
        Dispatcher.UIThread.RunJobs();

        var workspace = Assert.Single(Workspaces(h));
        Assert.Equal(path, workspace.SourcePath);
        Assert.Equal("주문", workspace.Name);
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Same(workspace, h.Get<DockWorkspace>().ActiveDocument!.Panel);
        Assert.All(workspace.Tabs, t =>
        {
            Assert.False(t.AwaitingSession);
            Assert.Same(session, t.Session);
            Assert.False(t.Editor.IsDirty);
        });
        Assert.Equal(new DbObjectRef("SCOTT", "P", "PROCEDURE"), workspace.Tabs[0].Target);

        // 렌더 점검(ORANDA_RENDER_DIR 이 있을 때만 PNG 를 남긴다): .plsql-workspace.md 로 연 작업 공간, 탭 2개.
        h.Window.UpdateLayout();
        Oranda.App.Tests.Features.MyBatisWorkspace.Flow.MyBatisFlowRig.Render(h.Window, "plsql-workspace-md",
            (int)h.Window.Bounds.Width, (int)h.Window.Bounds.Height);

        // 같은 파일을 다시 열면(두 명령 모두) 같은 작업 공간을 앞으로 낸다.
        await registry.ExecuteAsync("oranda.file.openWorkspaceFile", keys, path);
        await registry.ExecuteAsync("oranda.file.open", keys, path);
        Assert.Same(workspace, Assert.Single(Workspaces(h)));
    });

    [Fact]
    public Task A_connection_that_is_not_open_leaves_the_tab_waiting_and_a_broken_file_is_reported() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await Fixture.PrepareAsync(h, open: false);
        var path = await WriteWorkspaceAsync(null);
        var dialogs = new RecordingDialogs();
        SqlEditorHost.Current.Dialogs = dialogs;

        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.openWorkspaceFile", h.Get<IContextKeys>(), path);
        var waiting = Assert.Single(Workspaces(h)).Tabs;
        Assert.All(waiting, t => Assert.True(t.AwaitingSession));
        Assert.Equal(ProfileA, waiting[0].RestoredProfileId);

        var bad = Path.Combine(_dir, "bad.plsql-workspace.md");
        await File.WriteAllTextAsync(bad, "# x\n\n<!-- oranda-plsql-workspace 9 -->\n");
        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.open", h.Get<IContextKeys>(), bad);
        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.open", h.Get<IContextKeys>(), Path.Combine(_dir, "missing.plsql-workspace.md"));

        Assert.Equal(2, dialogs.Messages.Count);
        Assert.Single(Workspaces(h));
    });

    [Fact]
    public Task Save_overwrites_the_source_path_and_without_one_asks_then_remembers_it() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var session = await Fixture.PrepareAsync(h);
        var path = await WriteWorkspaceAsync(session);
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var dialogs = new RecordingDialogs();
        SqlEditorHost.Current.Dialogs = dialogs;
        await registry.ExecuteAsync("oranda.file.open", keys, path);
        var workspace = Assert.Single(Workspaces(h));

        // 명령은 실제 문맥 키(plsqlDocActive)로 실행된다. 경로가 있으면 묻지 않고 덮어쓴다.
        workspace.Tabs[0].Editor.Document.Insert(0, "-- 고침\n");
        Assert.True(await registry.ExecuteAsync("oranda.plsql.saveWorkspaceFile", keys));
        var written = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(0xEF, written[0]); // BOM 없음
        var text = Encoding.UTF8.GetString(written);
        Assert.Contains("-- 고침\n", text, StringComparison.Ordinal);
        Assert.Contains("<!-- connection: ORA 개발 -->", text, StringComparison.Ordinal);
        Assert.Empty(dialogs.Picked);
        Assert.Empty(Directory.GetFiles(_dir, "*.oranda-tmp"));

        // 경로가 없는 작업 공간은 이름을 제안해 묻고, 저장한 경로를 기억한다.
        var fresh = new PlsqlEditorViewModel(h.Services);
        fresh.StartNew("CREATE OR REPLACE PROCEDURE q IS BEGIN NULL; END;\n", session);
        var second = new PlsqlWorkspaceViewModel { Name = "PL/SQL 9" };
        h.Get<DockWorkspace>().OpenDocument(PlsqlWorkspaceRouter.PanelId, second, second.Name);
        second.Add(fresh);
        PlsqlWorkspaceRouter.For(h.Get<DockWorkspace>(), h.Services).Show(second, fresh, focus: true);
        var target = Path.Combine(_dir, "sub", "새.plsql-workspace.md");
        dialogs.SavePath = target;

        Assert.True(await registry.ExecuteAsync("oranda.plsql.saveWorkspaceFile", keys));

        Assert.Equal(["PL-SQL 9.plsql-workspace.md"], dialogs.Picked);
        Assert.Equal(target, second.SourcePath);
        Assert.Contains("## 새 PL/SQL", await File.ReadAllTextAsync(target), StringComparison.Ordinal);
        Assert.Equal(target, second.Capture().SourcePath);
    });

    [Fact]
    public Task A_file_tab_in_the_markdown_is_reread_from_disk_when_the_plsql_file_exists_else_it_becomes_a_plain_document() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await Fixture.PrepareAsync(h);
        var cp949 = Path.Combine(_dir, "emp.pkb");
        var bytes = FileEncoding.Encode("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; /* 한글 */ END;\r\n", "cp949");
        await File.WriteAllBytesAsync(cp949, bytes);
        var settings = Path.Combine(_dir, "settings.json");
        await File.WriteAllTextAsync(settings, "{ \"disk\": true }");
        var file = new PlsqlWorkspaceFile
        {
            Id = "x", Name = "파일",
            Tabs =
            [
                new PlsqlTabFile { Title = "emp.pkb", FilePath = cp949, Encoding = "utf-8", Text = "-- md 쪽 옛 글\n" },
                new PlsqlTabFile { Title = "gone.pkb", FilePath = Path.Combine(_dir, "gone.pkb"), Text = "-- 파일 없음\n" },
                new PlsqlTabFile { Title = "settings.json", FilePath = settings, Text = "-- 다른 확장자\n" },
            ],
        };
        var path = Path.Combine(_dir, "파일.plsql-workspace.md");
        await File.WriteAllTextAsync(path, PlsqlWorkspaceMarkdown.Write(file, _ => null), new UTF8Encoding(false));

        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.open", h.Get<IContextKeys>(), path);
        Dispatcher.UIThread.RunJobs();

        var tabs = Assert.Single(Workspaces(h)).Tabs;
        Assert.Equal((cp949, "cp949", false), (tabs[0].Editor.FilePath, tabs[0].Editor.Encoding, tabs[0].Editor.IsDirty));
        Assert.Equal("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; /* 한글 */ END;\r\n", tabs[0].Editor.Text.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal));
        Assert.True(await SqlEditorHost.Current.SaveAsync(tabs[0].Editor, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cp949)); // 인코딩·바이트 유지
        Assert.Null(tabs[1].Editor.FilePath);
        Assert.Equal(("gone.pkb", "-- 파일 없음\n"), (tabs[1].Title, tabs[1].Editor.Text));
        Assert.Null(tabs[2].Editor.FilePath); // 존재하는 다른 확장자 파일에는 묶지 않는다
        Assert.Equal("-- 다른 확장자\n", tabs[2].Editor.Text);
        Assert.Equal("{ \"disk\": true }", await File.ReadAllTextAsync(settings));
    });

    [Fact]
    public Task Profiles_with_the_same_name_leave_the_tab_without_a_connection_and_tell_the_user_once() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        h.Services.AddInstance<IConnectionStore>(new TwinStore(
        [
            new ConnectionProfile("ORA 개발", "scott", "localhost/A", Id: ProfileA),
            new ConnectionProfile("ORA 개발", "scott", "localhost/B", Id: other), // 저장소가 막는 중복을 손으로 고친 파일처럼 흉내 낸다
        ]));
        var dialogs = new RecordingDialogs();
        SqlEditorHost.Current.Dialogs = dialogs;
        var path = await WriteWorkspaceAsync(null);

        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.file.open", h.Get<IContextKeys>(), path);

        var workspace = Assert.Single(Workspaces(h));
        Assert.All(workspace.Tabs, t => Assert.Null(t.Editor.ConnectionProfileId));
        var message = Assert.Single(dialogs.Messages);
        Assert.Contains("ORA 개발", message, StringComparison.Ordinal);
    });

    [Fact]
    public Task A_renamed_workspace_writes_its_original_name_back_and_save_as_picks_a_file_and_adds_the_extension() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await Fixture.PrepareAsync(h);
        var first = await WriteWorkspaceAsync(null);
        var copy = Path.Combine(_dir, "복사.plsql-workspace.md");
        File.Copy(first, copy);
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var dialogs = new RecordingDialogs();
        SqlEditorHost.Current.Dialogs = dialogs;
        await registry.ExecuteAsync("oranda.file.open", keys, first);
        await registry.ExecuteAsync("oranda.file.open", keys, copy); // 같은 이름 「주문」 이 열려 있어 새 번호가 붙는다
        var second = Workspaces(h).Single(w => w.SourcePath == copy);
        Assert.NotEqual("주문", second.Name);
        Assert.Equal("주문", second.SourceName);

        Assert.True(await PlsqlWorkspaceFiles.SaveAsync(h.Services, SqlEditorHost.Current, second, default));
        Assert.StartsWith("# 주문\n", await File.ReadAllTextAsync(copy), StringComparison.Ordinal);

        // 다른 이름으로 저장: SourcePath 를 두고도 묻고, .plsql-workspace.md 로 끝나지 않으면 붙인다.
        dialogs.SavePath = Path.Combine(_dir, "새이름.md");
        Assert.True(await PlsqlWorkspaceFiles.SaveAsync(h.Services, SqlEditorHost.Current, second, default, saveAs: true));
        Assert.Equal(Path.Combine(_dir, "새이름.plsql-workspace.md"), second.SourcePath);
        Assert.True(File.Exists(second.SourcePath));
        dialogs.SavePath = Path.Combine(_dir, "확장자없음");
        Assert.True(await PlsqlWorkspaceFiles.SaveAsync(h.Services, SqlEditorHost.Current, second, default, saveAs: true));
        Assert.Equal(Path.Combine(_dir, "확장자없음.plsql-workspace.md"), second.SourcePath);

        // 쓰지 못하면(부모가 파일) 알리고 실패한다.
        dialogs.Messages.Clear();
        dialogs.SavePath = Path.Combine(first, "x.plsql-workspace.md");
        Assert.False(await PlsqlWorkspaceFiles.SaveAsync(h.Services, SqlEditorHost.Current, second, default, saveAs: true));
        Assert.Single(dialogs.Messages);
        Assert.Equal(Path.Combine(_dir, "확장자없음.plsql-workspace.md"), second.SourcePath);
    });

    [Fact]
    public Task Opening_the_same_file_twice_quickly_makes_one_workspace_or_tab_and_an_unreadable_plsql_file_is_reported() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await Fixture.PrepareAsync(h);
        var dialogs = new RecordingDialogs();
        SqlEditorHost.Current.Dialogs = dialogs;
        var md = await WriteWorkspaceAsync(null);
        var pkb = Path.Combine(_dir, "fast.pkb");
        await File.WriteAllTextAsync(pkb, "CREATE OR REPLACE PROCEDURE f IS BEGIN NULL; END;\n");

        await Task.WhenAll(PlsqlWorkspaceFiles.OpenAsync(h.Services, SqlEditorHost.Current, md, default),
            PlsqlWorkspaceFiles.OpenAsync(h.Services, SqlEditorHost.Current, md, default));
        await Task.WhenAll(PlsqlFiles.OpenAsync(h.Services, SqlEditorHost.Current, pkb, null, default),
            PlsqlFiles.OpenAsync(h.Services, SqlEditorHost.Current, pkb, null, default));

        Assert.Single(Workspaces(h), w => w.SourcePath == md);
        Assert.Single(Workspaces(h).SelectMany(w => w.Tabs), t => t.Editor.FilePath == pkb);

        Assert.True(await PlsqlFiles.OpenAsync(h.Services, SqlEditorHost.Current, Path.Combine(_dir, "none.pkb"), null, default));
        Assert.Single(dialogs.Messages);
    });

    [Fact]
    public Task The_plsql_doc_key_follows_the_active_document_and_its_active_tab() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single().RunAsync(default);
        var keys = h.Get<ContextKeyService>();
        var docking = h.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, h.Services);
        Assert.False(keys.Evaluate("plsqlDocActive"));

        var tab = new PlsqlEditorViewModel(h.Services);
        tab.StartNew("BEGIN NULL; END;\n", null);
        var doc = router.Place(tab, focus: true)!;
        var workspace = (PlsqlWorkspaceViewModel)doc.Panel!;
        Assert.True(keys.Evaluate("plsqlDocActive")); // 탭이 있는 활성 작업 공간

        var second = new PlsqlEditorViewModel(h.Services);
        second.StartNew("BEGIN NULL; END;\n", null);
        router.Place(second, near: workspace, focus: true);
        workspace.ActiveTab = null; // 보이는 탭이 없다
        Assert.False(keys.Evaluate("plsqlDocActive"));
        workspace.ActiveTab = tab;
        Assert.True(keys.Evaluate("plsqlDocActive"));

        workspace.Remove(second);
        workspace.Remove(tab); // 탭이 비면 꺼진다(문서는 남는다)
        Assert.Contains(docking.Documents, d => ReferenceEquals(d.Panel, workspace));
        Assert.False(keys.Evaluate("plsqlDocActive"));

        workspace.Add(tab);
        router.Activate(workspace, focus: false);
        Assert.True(keys.Evaluate("plsqlDocActive"));

        var sql = Path.Combine(_dir, "q.sql");
        await File.WriteAllTextAsync(sql, "SELECT 1 FROM dual;\n");
        await SqlEditorHost.Current.OpenFileAsync(sql, default); // 다른 문서가 활성이 된다
        Dispatcher.UIThread.RunJobs();
        Assert.IsNotType<PlsqlWorkspaceViewModel>(docking.ActiveDocument!.Panel);
        Assert.False(keys.Evaluate("plsqlDocActive"));

        router.Activate(workspace, focus: false);
        Assert.True(keys.Evaluate("plsqlDocActive"));
        docking.CloseDocument(doc); // 문서가 닫힘
        Assert.False(keys.Evaluate("plsqlDocActive"));
    });

    // ---- 도우미 ----

    private static List<PlsqlWorkspaceViewModel> Workspaces(ShellHarness h) =>
        [.. h.Get<DockWorkspace>().Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>()];

    /// <summary>서버 탭(프로시저)과 새 탭을 가진 작업 공간 파일을 쓴다. 접속 이름은 「ORA 개발」.</summary>
    private async Task<string> WriteWorkspaceAsync(ISession? session)
    {
        _ = session;
        var file = new PlsqlWorkspaceFile
        {
            Id = "x",
            Name = "주문",
            Tabs =
            [
                new PlsqlTabFile
                {
                    Owner = "SCOTT", Name = "P", Type = "PROCEDURE", ConnectionProfileId = ProfileA,
                    Text = "CREATE OR REPLACE PROCEDURE \"SCOTT\".p IS BEGIN NULL; END;\n",
                },
                new PlsqlTabFile { Title = "새 PL/SQL", ConnectionProfileId = ProfileA, Text = "CREATE OR REPLACE PROCEDURE q IS BEGIN NULL; END;\n" },
            ],
        };
        var path = Path.Combine(_dir, "주문.plsql-workspace.md");
        await File.WriteAllTextAsync(path, PlsqlWorkspaceMarkdown.Write(file, _ => "ORA 개발"), new UTF8Encoding(false));
        return path;
    }

    private static class Fixture
    {
        /// <summary>접속 저장소에 「ORA 개발」 프로필을 두고, 열린 접속이 있는 접속 서비스·메타데이터 가짜를 끼운다.</summary>
        public static async Task<ISession> PrepareAsync(ShellHarness h, bool open = true)
        {
            var profile = new ConnectionProfile("ORA 개발", "scott", "localhost/FREEPDB1", Id: ProfileA);
            await h.Get<IConnectionStore>().SaveAsync(new ConnectionsDocument { Profiles = [profile] }, default);
            var session = new ProfiledSession(profile);
            h.Services.AddInstance<IConnectionService>(new OpenConnections(open ? session : null));
            h.Services.AddInstance<IMetadataProvider>(new PlsqlEditorTests.FakeMeta { Source = "PROCEDURE p IS BEGIN NULL; END;\n" });
            return session;
        }
    }

    private sealed class ProfiledSession(ConnectionProfile profile) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = profile;

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TwinStore(IReadOnlyList<ConnectionProfile> profiles) : IConnectionStore
    {
        public Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct) =>
            Task.FromResult(new StoredDocument<ConnectionsDocument>(new ConnectionsDocument { Profiles = profiles }));

        public Task SaveAsync(ConnectionsDocument document, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingDialogs : IEditorDialogs
    {
        public List<string> Picked { get; } = [];

        public List<string> Messages { get; } = [];

        public string? SavePath { get; set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<string?> PickWorkspaceFileAsync(bool save, string suggestedName, CancellationToken ct)
        {
            Picked.Add(suggestedName);
            return Task.FromResult(SavePath);
        }

        public Task ShowMessageAsync(string title, string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    internal sealed class OpenConnections(ISession? open) : IConnectionService
    {
        public ISession? ActiveSession => open;

        public IReadOnlyList<ISession> Sessions => open is null ? [] : [open];

        public ConnectionProfile? LastProfile => open?.Profile;

        public bool IsBroken => false;

#pragma warning disable CS0067 // 시험에서 쓰지 않는 이벤트
        public event EventHandler? ActiveSessionChanged;

        public event EventHandler<ISession>? Connected;
#pragma warning restore CS0067

        public ISession? SessionOf(Guid profileId) => open?.Profile.Id == profileId ? open : null;

        public bool IsSessionBroken(Guid sessionId) => false;

        public bool Switch(ISession session) => ReferenceEquals(session, open);

        public Task DisconnectAsync(ISession session, CancellationToken ct) => throw new NotSupportedException();

        public Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectOutcome> ReconnectAsync(string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task<PasswordChangeOutcome> ChangePasswordAsync(ConnectionProfile profile, string? oldPassword, string newPassword, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> HasStoredPasswordAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> ImportWalletAsync(string zipPath, string? walletAlias, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionTestOutcome> TestConnectionAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionProfile> DuplicateProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task UpdateFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit, CancellationToken ct) => throw new NotSupportedException();
    }
}
