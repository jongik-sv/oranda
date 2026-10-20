using Avalonia.Input;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Templates;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Language.Snippets;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.Templates;

/// <summary>TSK-04-07 코드 조각 삽입·탭 이동, 템플릿 창, 키맵 프리셋·사용자 정의·충돌, 설정 내보내기·가져오기.</summary>
public sealed class TemplatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-tpl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppDataPaths Paths(string name) => new(Path.Combine(_root, name));

    private static (Key, KeyModifiers)[] Typed(Key key, KeyModifiers mods = KeyModifiers.None) => [(key, mods)];

    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Saved { get; private set; } = new();

        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Saved));

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    // ---- 등록 ----------------------------------------------------------------------------

    [Fact]
    public void Commands_and_panel_are_registered()
    {
        var ids = ShellTestHelpers.AppContributions.Commands.Select(c => c.Definition.Id).ToHashSet();
        foreach (var id in TemplatesCommands.Ids)
        {
            Assert.Contains(id, ids);
        }

        Assert.NotNull(new TemplatesViewModel());
        Assert.Equal(DockLocation.Right, ShellTestHelpers.RequirePanel("oranda.panel.templates").DefaultLocation);
    }

    // ---- Orange 5 프리셋 -------------------------------------------------------------------

    [Fact]
    public void Orange5_preset_keeps_orange_run_keys()
    {
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset("orange5"), macOS: false);
        var keys = FakeContextKeys.AllTrue;
        Assert.Equal("oranda.exec.runScript", table.Find(Typed(Key.F5), keys).CommandId);
        Assert.Equal("oranda.exec.runStatement", table.Find(Typed(Key.Enter, KeyModifiers.Control), keys).CommandId);
        Assert.Equal("oranda.exec.runForEdit", table.Find(Typed(Key.F9), keys).CommandId);
        Assert.Equal("oranda.exec.runSelection", table.Find(Typed(Key.L, KeyModifiers.Control), keys).CommandId);
    }

    [Fact]
    public void All_presets_load_and_have_no_conflicts()
    {
        foreach (var id in KeymapLoader.PresetIds)
        {
            var conflicts = KeymapComposer.FindConflicts(KeymapLoader.LoadPreset(id));
            Assert.True(conflicts.Count == 0, $"{id}: " + string.Join("; ", conflicts.Select(c => c.Keys + "=" + string.Join(",", c.Commands))));
        }
    }

    // ---- 겹치기·충돌 -----------------------------------------------------------------------

    [Fact]
    public void Compose_overrides_clears_and_adds()
    {
        var baseKeymap = KeymapLoader.LoadPreset("orange5");
        var user = new Keymap
        {
            Id = "my-keys",
            Name = "내",
            BasedOn = "orange5",
            Bindings =
            [
                new Oranda.Core.Shell.KeyBinding { Command = "oranda.exec.runScript", Keys = ["Primary+Shift+R"], When = "sqlDocFocus" },
                new Oranda.Core.Shell.KeyBinding { Command = "oranda.exec.runForEdit", Keys = [], When = "sqlDocFocus" },
                new Oranda.Core.Shell.KeyBinding { Command = "oranda.view.templates", Keys = ["Alt+T"] },
            ],
        };
        var table = KeyBindingTable.Build(KeymapComposer.Compose(baseKeymap, user), macOS: false);
        var keys = FakeContextKeys.AllTrue;
        Assert.Equal("oranda.exec.runScript", table.Find(Typed(Key.R, KeyModifiers.Control | KeyModifiers.Shift), keys).CommandId);
        Assert.NotEqual("oranda.exec.runScript", table.Find(Typed(Key.F5), keys).CommandId);
        Assert.NotEqual("oranda.exec.runForEdit", table.Find(Typed(Key.F9), keys).CommandId);
        Assert.Equal("oranda.view.templates", table.Find(Typed(Key.T, KeyModifiers.Alt), keys).CommandId);
        Assert.Equal("oranda.exec.runStatement", table.Find(Typed(Key.Enter, KeyModifiers.Control), keys).CommandId);
    }

    [Fact]
    public void FindConflicts_reports_same_scope_same_key_only()
    {
        var km = new Keymap
        {
            Id = "k",
            Name = "k",
            Bindings =
            [
                new Oranda.Core.Shell.KeyBinding { Command = "a", Keys = ["F5"], When = "editorFocus" },
                new Oranda.Core.Shell.KeyBinding { Command = "b", Keys = ["F5"], When = "editorFocus" },
                new Oranda.Core.Shell.KeyBinding { Command = "c", Keys = ["F5"], When = "gridFocus" },
                new Oranda.Core.Shell.KeyBinding { Command = "d", Keys = ["Primary+K Primary+C"] },
                new Oranda.Core.Shell.KeyBinding { Command = "e", Keys = ["Primary+K"] },
            ],
        };
        var conflicts = KeymapComposer.FindConflicts(km);
        Assert.Contains(conflicts, c => c.Keys == "F5" && c.When == "editorFocus" && c.Commands.SequenceEqual(["a", "b"]));
        Assert.DoesNotContain(conflicts, c => c.When == "gridFocus");
        Assert.Contains(conflicts, c => c.Keys == "Primary+K");
    }

    [Fact]
    public void SetKeys_drops_override_when_same_as_base()
    {
        var baseKeymap = KeymapLoader.LoadPreset("default");
        var user = new Keymap { Id = "my-keys", Name = "x", BasedOn = "default" };
        var bind = baseKeymap.Bindings.First(b => b.Command == "oranda.exec.runScript");
        var changed = KeymapComposer.SetKeys(user, baseKeymap, bind.Command, bind.When, ["F6"]);
        Assert.Single(changed.Bindings);
        var back = KeymapComposer.SetKeys(changed, baseKeymap, bind.Command, bind.When, bind.Keys);
        Assert.Empty(back.Bindings);
    }

    [Theory]
    [InlineData("Primary+Shift+F", true)]
    [InlineData("Primary+K Primary+C", true)]
    [InlineData("Shift+Primary+F", false)]
    [InlineData("Primary+Nope", false)]
    [InlineData("", false)]
    public void Validate_checks_key_notation(string keys, bool ok) => Assert.Equal(ok, KeymapComposer.Validate(keys) is null);

    // ---- 키맵 편집 화면 --------------------------------------------------------------------

    [Fact]
    public async Task Editor_assign_warns_on_conflict_and_saves_user_keymap()
    {
        var paths = Paths("editor");
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        await settings.LoadAsync();
        var keymapStore = new UserKeymapStore(paths);
        var vm = new KeymapEditorViewModel([.. ShellTestHelpers.AppContributions.Commands.Select(c => c.Definition)], keymapStore, settings);
        vm.SelectedRow = vm.Rows.First(r => r.CommandId == "oranda.file.open");

        vm.NewKeys = "Shift+Primary+O";
        Assert.False(vm.Assign());
        Assert.True(vm.HasWarning);

        vm.NewKeys = "Primary+S";
        Assert.True(vm.Assign());
        Assert.Contains("충돌", vm.Warning, StringComparison.Ordinal);
        Assert.Contains("oranda.file.save", vm.Warning, StringComparison.Ordinal);

        vm.NewKeys = "Primary+Shift+Alt+O".Replace("Primary+Shift+Alt+O", "Primary+Alt+Shift+O", StringComparison.Ordinal);
        Assert.True(vm.Assign());
        Assert.False(vm.HasWarning);

        await vm.SaveAsync();
        Assert.Equal(UserKeymapStore.DefaultId, store.Saved.KeymapId);
        var loaded = await keymapStore.LoadAsync(UserKeymapStore.DefaultId);
        Assert.NotNull(loaded);
        Assert.Contains(loaded!.Bindings, b => b.Command == "oranda.file.open" && b.Keys.SequenceEqual(["Primary+Alt+Shift+O"]));

        var resolved = await KeymapResolver.ResolveAsync(UserKeymapStore.DefaultId, keymapStore);
        var table = KeyBindingTable.Build(resolved, macOS: false);
        Assert.Equal("oranda.file.open", table.Find(Typed(Key.O, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift), FakeContextKeys.AllTrue).CommandId);
    }

    [Fact]
    public async Task Editor_with_no_overrides_saves_preset_id()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        await settings.LoadAsync();
        var vm = new KeymapEditorViewModel([.. ShellTestHelpers.AppContributions.Commands.Select(c => c.Definition)], new UserKeymapStore(Paths("p")), settings)
        {
            SelectedPresetId = "orange5",
        };
        await vm.SaveAsync();
        Assert.Equal("orange5", store.Saved.KeymapId);
    }

    [Fact]
    public async Task Resolver_falls_back_to_default_for_unknown_id()
    {
        var km = await KeymapResolver.ResolveAsync("nope", new UserKeymapStore(Paths("r")));
        Assert.Equal("default", km.Id);
    }

    // ---- 설정 내보내기·가져오기 -------------------------------------------------------------

    [Fact]
    public async Task Export_then_import_on_another_pc_gives_same_shortcuts()
    {
        var a = Paths("pcA");
        var storeA = new UserKeymapStore(a);
        var baseKeymap = KeymapLoader.LoadPreset("orange5");
        var user = KeymapComposer.SetKeys(new Keymap { Id = "my-keys", Name = "내", BasedOn = "orange5" }, baseKeymap, "oranda.exec.runScript", "sqlDocFocus", ["Primary+Shift+R"]);
        await storeA.SaveAsync(user);
        await new SnippetStore(a).SaveAsync([new Snippet("my", "내", "", "SELECT 1$0", IsUser: true)]);
        var settingsA = new AppSettings { KeymapId = "my-keys", Editor = new EditorSettings { FontSize = 17 } };

        var json = new SettingsTransfer(a, storeA).Export(settingsA);

        var b = Paths("pcB");
        var storeB = new UserKeymapStore(b);
        var imported = await new SettingsTransfer(b, storeB).ImportAsync(json);

        Assert.Equal("my-keys", imported.KeymapId);
        Assert.Equal(17, imported.Editor.FontSize);
        var tableA = KeyBindingTable.Build(await KeymapResolver.ResolveAsync("my-keys", storeA), false);
        var tableB = KeyBindingTable.Build(await KeymapResolver.ResolveAsync(imported.KeymapId, storeB), false);
        foreach (var cmd in new[] { "oranda.exec.runScript", "oranda.exec.runStatement", "oranda.exec.runForEdit", "oranda.file.save" })
        {
            Assert.Equal(tableA.ShortcutText(cmd), tableB.ShortcutText(cmd));
        }

        Assert.Equal("Ctrl+Shift+R", tableB.ShortcutText("oranda.exec.runScript"));
        Assert.Single(await new SnippetStore(b).LoadAsync());
    }

    [Fact]
    public async Task Import_rejects_bad_bundle_without_writing()
    {
        var p = Paths("bad");
        var transfer = new SettingsTransfer(p, new UserKeymapStore(p));
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ImportAsync("not json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ImportAsync("""{"bundleVersion":9,"settings":{}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ImportAsync("""{"bundleVersion":1,"settings":{},"snippets":{"schemaVersion":9}}"""));
        Assert.False(Directory.Exists(p.KeymapDirectory));
    }

    [Fact]
    public async Task Import_fills_missing_settings_with_defaults()
    {
        var p = Paths("min");
        var imported = await new SettingsTransfer(p, new UserKeymapStore(p)).ImportAsync("""{"bundleVersion":1,"settings":{"keymapId":"sqldev"}}""");
        Assert.Equal("sqldev", imported.KeymapId);
        Assert.NotNull(imported.Editor);
    }

    // ---- 조각 삽입·탭 이동 -------------------------------------------------------------------

    [Fact]
    public void Insert_snippet_selects_first_stop_and_tab_moves_through_stops()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "sel");
        vm.UpdateCaret(3, 3, 0);
        Assert.True(SnippetInserter.TryExpandPrefix(vm));
        Assert.Equal("SELECT *\n  FROM table_name\n WHERE 1 = 1", vm.Text);
        Assert.Equal("*", vm.Text.Substring(vm.SelectionStart, vm.SelectionLength));

        var session = SnippetInserter.ActiveSession(vm);
        Assert.NotNull(session);

        // 첫 자리에 타이핑 후 다음으로.
        vm.Document.Replace(vm.SelectionStart, vm.SelectionLength, "a.*");
        Assert.True(session!.MoveNext());
        Assert.Equal("table_name", vm.Text.Substring(vm.SelectionStart, vm.SelectionLength));
        Assert.True(session.MoveNext());
        Assert.Equal("1 = 1", vm.Text.Substring(vm.SelectionStart, vm.SelectionLength));
        Assert.True(session.MoveNext());
        Assert.Equal(vm.Text.Length, vm.SelectionStart);
        Assert.Equal(0, vm.SelectionLength);
        Assert.Null(SnippetInserter.ActiveSession(vm));
        Assert.StartsWith("SELECT a.*", vm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mirrored_stops_copy_typed_text_when_leaving()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), string.Empty);
        SnippetInserter.Insert(vm, SnippetLibrary.FindByPrefix("selg")!);
        Assert.Contains("GROUP BY col", vm.Text, StringComparison.Ordinal);
        vm.Document.Replace(vm.SelectionStart, vm.SelectionLength, "dept");
        SnippetInserter.ActiveSession(vm)!.MoveNext();
        Assert.Contains("SELECT dept, COUNT(*)", vm.Text, StringComparison.Ordinal);
        Assert.Contains("GROUP BY dept", vm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_keeps_line_indent_and_replaces_selection()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "BEGIN\n    xx\nEND;");
        vm.Select(10, 2);
        SnippetInserter.Insert(vm, SnippetLibrary.FindByPrefix("ifb")!);
        Assert.Contains("    IF cond THEN\n      NULL;\n    ELSE", vm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("xx", vm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_refuses_read_only_and_non_prefix_word()
    {
        var ro = new SqlEditorViewModel(Guid.NewGuid(), "sel") { IsReadOnly = true };
        Assert.False(SnippetInserter.Insert(ro, SnippetLibrary.Snippets[0]));
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "hello");
        vm.UpdateCaret(5, 5, 0);
        Assert.False(SnippetInserter.TryExpandPrefix(vm));
    }

    [Fact]
    public void Hint_insert_puts_example_as_is()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), string.Empty);
        SnippetInserter.Insert(vm, SnippetLibrary.Hints.First(h => h.Prefix == "FULL"));
        Assert.Equal("SELECT /*+ FULL(e) */ * FROM emp e", vm.Text);
    }

    // ---- 템플릿 창 ---------------------------------------------------------------------------

    private TemplatesViewModel Panel(string name, SqlEditorHost? host = null, string language = "ko") =>
        new(Paths(name), new ShellText(language: language)) { Host = host ?? new SqlEditorHost() };

    [Fact]
    public void Templates_panel_kind_filter_always_has_one_choice_and_groups_by_kind()
    {
        var vm = Panel("kinds");

        // 처음은 전체: 네 종류 묶음이 모두 있고 개수가 내장 목록과 같다.
        Assert.True(vm.IsAllKinds);
        Assert.Null(vm.KindFilter);
        Assert.Equal([SnippetKind.Snippet, SnippetKind.Hint, SnippetKind.Function, SnippetKind.PseudoColumn], vm.Groups.Select(g => g.Kind));
        Assert.Equal(SnippetLibrary.Snippets.Count, vm.Groups[0].Count);
        Assert.Equal(SnippetLibrary.Hints.Count, vm.Groups[1].Count);
        Assert.Equal(SnippetLibrary.Functions.Count, vm.Groups[2].Count);
        Assert.Equal(SnippetLibrary.PseudoColumns.Count, vm.Groups[3].Count);
        Assert.All(vm.Groups, g => Assert.Equal(g.Count, g.Items.Count));
        Assert.Equal(["조각", "힌트", "함수", "의사 열"], vm.Groups.Select(g => g.Title));

        // 힌트만 → 힌트 묶음 하나. 켜진 단추가 꺼지는 값(false)이 와도 빈 거르기가 되지 않는다.
        vm.IsHintKind = true;
        Assert.Equal(SnippetKind.Hint, vm.KindFilter);
        Assert.Equal(SnippetKind.Hint, Assert.Single(vm.Groups).Kind);
        Assert.All(vm.Items, s => Assert.Equal(SnippetKind.Hint, s.Kind));
        vm.IsHintKind = false;
        Assert.True(vm.IsHintKind);
        Assert.False(vm.IsAllKinds);

        vm.IsPseudoColumnKind = true;
        Assert.False(vm.IsHintKind);
        Assert.Equal(SnippetLibrary.PseudoColumns.Count, Assert.Single(vm.Groups).Count);

        // 다시 전체.
        vm.IsAllKinds = true;
        Assert.Null(vm.KindFilter);
        Assert.Equal(4, vm.Groups.Count);
        Assert.Equal(SnippetLibrary.All.Count, vm.Items.Count);
    }

    [Fact]
    public void Templates_panel_search_counts_matches_per_group_and_expands_collapsed_groups()
    {
        var vm = Panel("search");
        var functions = vm.Groups.First(g => g.Kind == SnippetKind.Function);
        TemplatesViewModel.Toggle(functions);
        Assert.False(functions.IsExpanded);

        vm.Query = "to_";
        Assert.True(functions.IsExpanded);
        Assert.Equal(SnippetKind.Function, Assert.Single(vm.Groups).Kind);
        Assert.Equal(4, functions.Count); // TO_CHAR, TO_DATE, TO_NUMBER, TO_TIMESTAMP

        // 찾을 때는 첫 항목을 골라 둔다(Enter 로 바로 넣게).
        Assert.Equal("TO_CHAR", vm.Selected?.Prefix);

        vm.Query = "rownum";
        Assert.Contains(vm.Items, s => s.Kind == SnippetKind.PseudoColumn && s.Prefix == "ROWNUM");
        Assert.Equal(vm.Items.Count, vm.Groups.Sum(g => g.Count));

        vm.Query = "zzzz-없음";
        Assert.Empty(vm.Groups);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public void Templates_panel_rows_show_description_instead_of_repeating_the_prefix()
    {
        var vm = Panel("rows");
        var rows = vm.Groups.SelectMany(g => g.Items).ToList();
        var rowid = rows.First(r => r.Prefix == "ROWID");
        Assert.Equal("행의 물리 주소. 가장 빠른 행 접근 방법이지만 행이 옮겨지면 바뀔 수 있다.", rowid.Summary);
        Assert.All(rows, r => Assert.False(string.Equals(r.Summary, r.Prefix, StringComparison.OrdinalIgnoreCase), r.Prefix));
        Assert.Contains("ROWID", rowid.ToolTip, StringComparison.Ordinal);
        Assert.Equal("column", rowid.IconKey);
        Assert.Equal("objFunction", rows.First(r => r.Prefix == "NVL").IconKey);

        var en = Panel("rows-en", language: "en");
        Assert.Equal(["Snippets", "Hints", "Functions", "Pseudo-columns"], en.Groups.Select(g => g.Title));
        Assert.StartsWith("Physical address", en.Groups.SelectMany(g => g.Items).First(r => r.Prefix == "ROWID").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Templates_panel_preview_follows_the_selected_row()
    {
        var vm = Panel("preview");
        Assert.False(vm.HasSelection);
        Assert.Empty(vm.PreviewText);

        var selr = vm.Groups[0].Items.First(r => r.Prefix == "selr");
        vm.SelectedRow = selr;
        Assert.Same(selr.Snippet, vm.Selected);
        Assert.Equal("SELECT 상위 N건", vm.PreviewTitle);
        Assert.Equal("FETCH FIRST 로 상위 N건", vm.PreviewDescription);
        Assert.Equal("SELECT *\n  FROM table_name\n ORDER BY 1\n FETCH FIRST 10 ROWS ONLY", vm.PreviewText);
        Assert.False(vm.IsUserSelected);

        var hint = SnippetLibrary.Hints.First(h => h.Prefix == "USE_NL");
        vm.Selected = hint;
        Assert.Equal(hint, (vm.SelectedRow as TemplateItemRow)?.Snippet);
        Assert.Equal("SELECT /*+ USE_NL(e) */ * FROM dept d, emp e WHERE e.deptno = d.deptno", vm.PreviewText);

        // 묶음 머리를 고르면 미리보기가 빈다.
        vm.SelectedRow = vm.Groups[2];
        Assert.Null(vm.Selected);
        Assert.Empty(vm.PreviewText);
    }

    [Fact]
    public void Templates_panel_inserts_selected_item_into_the_active_editor()
    {
        var host = new SqlEditorHost();
        var vm = Panel("insert", host);
        vm.Selected = SnippetLibrary.FindByPrefix("del");
        Assert.False(vm.InsertSelected());
        Assert.NotEmpty(vm.Status);

        var tab = host.NewDocument();
        Assert.True(vm.InsertSelected());
        Assert.Equal("DELETE FROM table_name\n WHERE id = value", tab.Text);
        Assert.Empty(vm.Status);

        vm.Query = "sysdate";
        Assert.True(vm.SelectFirst());
        tab.Select(tab.Text.Length, 0);
        Assert.True(vm.InsertSelected());
        Assert.EndsWith("SELECT SYSDATE FROM dual", tab.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Templates_panel_creates_edits_duplicates_and_deletes_user_snippets()
    {
        var paths = Paths("tpl");
        var vm = new TemplatesViewModel(paths, new ShellText()) { Host = new SqlEditorHost() };
        Func<SnippetEditViewModel, Task<bool>>? fill = null;
        vm.ShowEditor = e => fill!(e);

        // 새로 만들기(대화상자에서 입력하고 저장).
        fill = e =>
        {
            Assert.Equal("새 조각", e.Title);
            e.Prefix = "mine";
            e.Name = "내 조각";
            e.Body = "SELECT ${1:x} FROM dual$0";
            return Task.FromResult(true);
        };
        Assert.True(await vm.NewSnippetAsync());
        Assert.Single(vm.UserSnippets);
        Assert.Equal("mine", vm.Selected?.Prefix);
        Assert.Equal("docEdit", vm.Groups[0].Items[0].IconKey); // 찾는 글이 없으면 사용자 조각이 묶음 맨 앞
        Assert.Equal(SnippetLibrary.Snippets.Count + 1, vm.Groups[0].Count);
        Assert.Contains("mine", vm.Status, StringComparison.Ordinal);

        // 고치기: 같은 조각이 바뀐다(개수 그대로).
        fill = e =>
        {
            Assert.Equal("조각 편집", e.Title);
            Assert.Equal("mine", e.Prefix);
            e.Description = "설명을 붙였다";
            e.Prefix = "mine2";
            return Task.FromResult(true);
        };
        Assert.True(await vm.EditSelectedAsync());
        Assert.Equal("mine2", Assert.Single(vm.UserSnippets).Prefix);
        Assert.Equal("설명을 붙였다", vm.Groups[0].Items.First(r => r.Snippet.IsUser).Summary);

        // 기본 제공 항목은 고칠 수 없다.
        vm.Selected = SnippetLibrary.FindByPrefix("sel");
        Assert.Null(vm.BeginEdit());
        Assert.False(await vm.DeleteUserSnippetAsync());
        Assert.NotEmpty(vm.Status);

        // 복제: 기본 조각과 겹치지 않는 줄임말을 제안하고, 본문·이름·설명을 가져온다.
        fill = e =>
        {
            Assert.Equal("sel_2", e.Prefix);
            Assert.Equal("SELECT", e.Name);
            Assert.Equal(SnippetLibrary.FindByPrefix("sel")!.Body, e.Body);
            Assert.False(e.HasNote);
            return Task.FromResult(true);
        };
        Assert.True(await vm.DuplicateSelectedAsync());
        Assert.Equal(2, vm.UserSnippets.Count);

        // 같은 줄임말(대소문자 무시)의 사용자 조각은 거부한다. 기본 조각과 같은 줄임말은 「대신한다」 알림만.
        var dup = vm.BeginNew();
        dup.Prefix = "MINE2";
        dup.Body = "SELECT 2";
        Assert.False(await vm.CommitAsync(dup));
        Assert.Contains("mine2", dup.Error, StringComparison.Ordinal);
        dup.Prefix = "has space";
        Assert.False(dup.Validate());
        dup.Prefix = "sel";
        Assert.True(dup.HasNote);
        dup.Body = "  ";
        Assert.False(dup.Validate());
        Assert.Equal(2, vm.UserSnippets.Count);

        // 대화상자에서 취소하면 아무것도 바뀌지 않는다.
        fill = _ => Task.FromResult(false);
        Assert.False(await vm.NewSnippetAsync());
        Assert.Equal(2, vm.UserSnippets.Count);

        // 다시 열어도 남아 있고, 지우면 파일에서도 빠진다.
        var again = new TemplatesViewModel(paths, new ShellText()) { Host = new SqlEditorHost() };
        await again.LoadUserAsync();
        Assert.Equal(["mine2", "sel_2"], again.UserSnippets.Select(s => s.Prefix));
        again.Query = "mine2";
        Assert.True(again.SelectFirst());
        Assert.True(again.IsUserSelected);
        Assert.True(await again.DeleteUserSnippetAsync());
        Assert.Equal(["sel_2"], again.UserSnippets.Select(s => s.Prefix));
        Assert.Single(await new SnippetStore(paths).LoadAsync());
    }

    [Fact]
    public void Templates_text_keys_exist_in_both_languages()
    {
        var dir = AppContext.BaseDirectory;
        while (Directory.GetFiles(dir, "Oranda.sln*").Length == 0)
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("저장소 루트를 찾지 못했다.");
        }

        var keys = Directory.EnumerateFiles(Path.Combine(dir, "src", "Oranda.App", "Features", "Templates"), "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(Templates\\.[A-Za-z.]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.True(keys.Count >= 20, "키 검색이 동작하지 않는다");
        foreach (var lang in Oranda.App.Features.Localization.StringCatalog.Languages)
        {
            Assert.Empty(keys.Where(k => Oranda.App.Features.Localization.StringCatalog.Get(lang, k) is null).Select(k => lang + ":" + k));
        }
    }

    // ---- 명령 처리기 ---------------------------------------------------------------------------

    [Fact]
    public async Task Handler_inserts_snippet_by_parameter_prefix()
    {
        var host = new SqlEditorHost();
        host.NewDocument();
        var handler = new TemplatesCommandHandler { Host = host };
        await handler.ExecuteAsync(new CommandContext("oranda.edit.insertSnippet", FakeContextKeys.AllTrue, "del"), CancellationToken.None);
        Assert.StartsWith("DELETE FROM table_name", host.Active!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_switches_preset_via_settings()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        await settings.LoadAsync();
        var handler = new TemplatesCommandHandler(new Services(settings));
        await handler.ExecuteAsync(new CommandContext("oranda.tools.keymapPreset.plsqldev", FakeContextKeys.AllTrue), CancellationToken.None);
        Assert.Equal("plsqldev", store.Saved.KeymapId);
    }

    [Fact]
    public async Task Handler_export_and_import_files()
    {
        var paths = Paths("h");
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        await settings.UpdateAsync(s => s with { KeymapId = "sqldev" });
        var handler = new TemplatesCommandHandler(new Services(settings, paths));
        var file = Path.Combine(_root, "out.json");
        await handler.ExecuteAsync(new CommandContext("oranda.tools.exportSettings", FakeContextKeys.AllTrue, file), CancellationToken.None);
        Assert.True(File.Exists(file));

        await settings.UpdateAsync(s => s with { KeymapId = "default" });
        await handler.ExecuteAsync(new CommandContext("oranda.tools.importSettings", FakeContextKeys.AllTrue, file), CancellationToken.None);
        Assert.Equal("sqldev", store.Saved.KeymapId);
    }

    private sealed class Services(SettingsService settings, IAppDataPaths? paths = null) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(SettingsService) ? settings : serviceType == typeof(IAppDataPaths) ? paths : null;
    }
}
