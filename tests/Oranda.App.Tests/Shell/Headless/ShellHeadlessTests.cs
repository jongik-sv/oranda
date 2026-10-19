using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Palette;
using Oranda.App.Shell.Settings;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Shell.Headless;

// TSK-03-01 Headless UI 시험: 도킹 배치 저장·복원, 명령 팔레트, 테마, 설정 창, 메뉴, 키 입력, 배율.
public class ShellHeadlessTests
{
    private static IToolDock FindToolDock(IRootDock root, string toolId)
    {
        IToolDock? found = null;
        void Walk(IDockable d)
        {
            if (d is IToolDock td && (td.VisibleDockables ?? []).Any(t => t.Id == toolId))
            {
                found = td;
            }

            if (d is IDock dock)
            {
                foreach (var c in dock.VisibleDockables ?? [])
                {
                    Walk(c);
                }
            }
        }

        Walk(root);
        return found ?? throw new Xunit.Sdk.XunitException($"도구 창 {toolId} 의 묶음이 도킹 모델에 없다.");
    }

    private static MenuItem TopMenu(ShellHarness h, string header) =>
        h.Window.Menus.Items.OfType<MenuItem>().Single(m => m.Header is string s && s.StartsWith(header, StringComparison.Ordinal));

    [Fact]
    public Task Main_window_has_the_ten_top_menus_in_order() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();

        var headers = h.Window.Menus.Items.OfType<MenuItem>().Select(m => (string)m.Header!).ToList();

        Assert.Equal(["파일(_F)", "편집(_E)", "보기(_V)", "_SQL", "결과(_R)", "데이터베이스(_D)", "모니터(_M)", "도구(_T)", "창(_W)", "도움말(_H)"], headers);
    });

    [Fact]
    public Task Menus_are_built_from_the_registry_with_keymap_shortcuts() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();

        var view = TopMenu(h, "보기");
        var palette = view.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "명령 팔레트");

        // 키맵의 Primary 는 macOS 에서 ⌘(Meta), 그 밖에서 Ctrl 이다(commands.md 5.1).
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.Equal(new KeyGesture(Key.P, primary | KeyModifiers.Shift), palette.InputGesture);
        Assert.NotNull(palette.Command);
        Assert.Contains(view.Items.OfType<MenuItem>(), m => (string?)m.Header == "테마");
        // 보기 > 도구 창: 등록된 도구 창이 자동으로 나열된다.
        var panels = view.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "도구 창");
        var registry = h.Get<IToolPanelRegistry>();
        Assert.Equal(registry.Panels.Count(p => p.Kind == ToolPanelKind.Tool), panels.Items.Count);
    });

    [Fact]
    public Task Default_layout_is_shown_on_first_run() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();

        var panels = h.Get<IToolPanelRegistry>().Panels;
        var expected = LayoutPlanner.CreateDefault(panels);
        var actual = ws.Capture();

        Assert.True(LayoutPlanner.SameShape(expected.Root, actual.Root), Dump(expected) + "\n---\n" + Dump(actual));
        Assert.Equal(expected.HiddenTools, actual.HiddenTools);
        Assert.Contains("oranda.panel.schemaBrowser", ws.VisibleToolIds);
        Assert.Contains("oranda.panel.output", ws.VisibleToolIds);
        Assert.DoesNotContain("oranda.panel.outline", ws.VisibleToolIds);
        Assert.NotNull(h.Window.Dock.DockControl.Layout);
    });

    [Fact]
    public Task Tool_panel_view_models_are_created_lazily_when_first_shown() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();

        // 숨겨진 도구 창은 아직 만들지 않았다. 보이지 않는 탭(활성이 아닌 탭)도 만들지 않는다.
        Assert.DoesNotContain("oranda.panel.outline", ws.CreatedToolIds);
        Assert.DoesNotContain("oranda.panel.templates", ws.CreatedToolIds);

        ws.ShowTool("oranda.panel.outline");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("oranda.panel.outline", ws.CreatedToolIds);
        Assert.DoesNotContain("oranda.panel.templates", ws.CreatedToolIds);
    });

    [Fact]
    public Task Changed_layout_is_restored_after_restart() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            LayoutDocument before;
            using (var h = new ShellHarness(dir))
            {
                var ws = h.Get<DockWorkspace>();
                ws.ShowTool("oranda.panel.outline");
                ws.HideTool("oranda.panel.scripts");
                ws.ShowTool("oranda.panel.history");
                // 구분선을 끌면 이웃 둘의 비율이 함께 바뀐다(합 1). 왼쪽 묶음의 이웃은 문서·아래 묶음을 담은 오른쪽 열이다.
                var left = FindToolDock(ws.Layout!, "oranda.panel.schemaBrowser");
                left.Proportion = 0.30;
                ((IDock)left.Owner!).VisibleDockables!.OfType<IDock>().Single(d => !ReferenceEquals(d, left)).Proportion = 0.70;
                before = ws.Capture();
                await h.Get<ShellController>().SaveLayoutAsync(h.Window.CapturePlacement());
            }

            Assert.True(File.Exists(Path.Combine(dir, "layout.json")));

            using var again = new ShellHarness(dir);
            var after = again.Get<DockWorkspace>().Capture();

            Assert.True(LayoutPlanner.SameShape(before.Root, after.Root), "재시작 뒤 배치가 같지 않다\n" + Dump(before) + "\n---\n" + Dump(after));
            Assert.Equal(before.HiddenTools, after.HiddenTools);
            Assert.Contains("oranda.panel.outline", again.Get<DockWorkspace>().VisibleToolIds);
            Assert.DoesNotContain("oranda.panel.scripts", again.Get<DockWorkspace>().VisibleToolIds);
            Assert.Equal(0.30, FindToolDock(again.Get<DockWorkspace>().Layout!, "oranda.panel.schemaBrowser").Proportion, 3);
            Assert.NotNull(again.Get<ShellController>().SavedWindow);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Closing_the_main_window_saves_the_layout() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-close-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var h = new ShellHarness(dir))
            {
                h.Get<DockWorkspace>().HideTool("oranda.panel.output");
                h.Window.Close();
                await HeadlessHost.WaitUntil(() => File.Exists(Path.Combine(dir, "layout.json")));
            }

            using var again = new ShellHarness(dir);
            Assert.DoesNotContain("oranda.panel.output", again.Get<DockWorkspace>().VisibleToolIds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Layout_file_from_a_removed_panel_is_cleaned_on_restore() => HeadlessHost.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "layout.json"),
                """
                {
                  "schemaVersion": 1,
                  "root": { "kind": "Split", "orientation": "Horizontal", "children": [
                      { "kind": "ToolGroup", "proportion": 0.2, "tools": ["oranda.panel.removedPlugin", "oranda.panel.scripts"], "activeTool": "oranda.panel.removedPlugin" },
                      { "kind": "Documents" } ] },
                  "hiddenTools": ["oranda.panel.removedPlugin2"]
                }
                """);

            using var h = new ShellHarness(dir);
            var ws = h.Get<DockWorkspace>();

            Assert.Contains("oranda.panel.scripts", ws.VisibleToolIds);
            Assert.DoesNotContain("oranda.panel.removedPlugin", ws.VisibleToolIds);
            // 새로 등록된(저장 파일에 없는) 도구 창은 자기 기본 위치에 들어간다.
            Assert.Contains("oranda.panel.output", ws.VisibleToolIds);
            Assert.DoesNotContain("oranda.panel.removedPlugin2", ws.Capture().HiddenTools);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Corrupt_layout_file_falls_back_to_default_layout() => HeadlessHost.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "layout.json"), "{ not json");

            using var h = new ShellHarness(dir);

            Assert.NotNull(h.Get<ShellController>().LayoutNotice);
            var expected = LayoutPlanner.CreateDefault(h.Get<IToolPanelRegistry>().Panels);
            var actual = h.Get<DockWorkspace>().Capture();
            Assert.True(LayoutPlanner.SameShape(expected.Root, actual.Root), Dump(expected) + "\n---\n" + Dump(actual));
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "backup"), "layout.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Reset_layout_command_restores_the_default_arrangement() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.HideTool("oranda.panel.schemaBrowser");
        ws.ShowTool("oranda.panel.outline");

        var ran = await h.Get<ICommandRegistry>().ExecuteAsync("oranda.view.resetLayout", h.Get<IContextKeys>());

        Assert.True(ran);
        Assert.True(LayoutPlanner.SameShape(LayoutPlanner.CreateDefault(h.Get<IToolPanelRegistry>().Panels).Root, ws.Capture().Root));
    });

    [Fact]
    public Task Documents_open_cycle_split_and_close_through_commands() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var ctx = h.Get<ContextKeyService>();

        Assert.False(registry.CanExecute("oranda.file.close", keys));
        var doc = ws.OpenDocument("oranda.panel.sqlEditor");
        var second = ws.OpenDocument("oranda.panel.sqlEditor");
        Assert.NotNull(doc);
        Assert.NotNull(second);
        Assert.NotSame(doc, second);
        Assert.Equal(2, ws.Documents.Count);
        Assert.True(ctx.Get("hasDocument"));
        Assert.True(registry.CanExecute("oranda.file.close", keys));

        Assert.Same(second, ws.ActiveDocument);
        await registry.ExecuteAsync("oranda.window.previousDocument", keys);
        Assert.Same(doc, ws.ActiveDocument);
        await registry.ExecuteAsync("oranda.window.nextDocument", keys);
        Assert.Same(second, ws.ActiveDocument);

        // 분할해도 저장 형식의 문서 영역은 하나다.
        await registry.ExecuteAsync("oranda.window.splitRight", keys);
        Assert.Equal(1, CountDocumentNodes(ws.Capture().Root!));
        Assert.Equal(2, ws.Documents.Count);

        await registry.ExecuteAsync("oranda.file.close", keys);
        Assert.Single(ws.Documents);
        await registry.ExecuteAsync("oranda.file.closeAll", keys);
        Assert.Empty(ws.Documents);
        Assert.False(ctx.Get("hasDocument"));
        Assert.False(registry.CanExecute("oranda.file.close", keys));
    });

    [Fact]
    public Task Single_instance_document_kinds_reuse_the_open_tab() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();

        var a = ws.OpenDocument("oranda.panel.dbInfo");
        var b = ws.OpenDocument("oranda.panel.dbInfo");

        Assert.Same(a, b);
        Assert.Single(ws.Documents);
        Assert.Null(ws.OpenDocument("oranda.panel.unknown"));
        Assert.Null(ws.OpenDocument("oranda.panel.schemaBrowser")); // 도구 창은 문서로 열지 않는다
    });

    [Fact]
    public Task Documents_survive_a_layout_rebuild() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument("oranda.panel.sqlEditor")!;

        ws.ShowTool("oranda.panel.outline");
        ws.HideTool("oranda.panel.output");

        Assert.Same(doc, Assert.Single(ws.Documents));
        Assert.Same(doc, ws.ActiveDocument);
    });

    [Fact]
    public Task Theme_commands_apply_immediately_and_persist() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var h = new ShellHarness(dir))
            {
                var registry = h.Get<ICommandRegistry>();
                var keys = h.Get<IContextKeys>();

                await registry.ExecuteAsync("oranda.view.themeDark", keys);
                await HeadlessHost.WaitUntil(() => Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);

                await registry.ExecuteAsync("oranda.view.themeLight", keys);
                await HeadlessHost.WaitUntil(() => Application.Current!.RequestedThemeVariant == ThemeVariant.Light);

                await registry.ExecuteAsync("oranda.view.themeDark", keys);
                await HeadlessHost.WaitUntil(() => Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
            }

            Assert.Contains("\"theme\": \"Dark\"", File.ReadAllText(Path.Combine(dir, "settings.json")));

            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            using var again = new ShellHarness(dir);
            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);

            // OS 따르기 = Default.
            await again.Get<ICommandRegistry>().ExecuteAsync("oranda.view.themeSystem", again.Get<IContextKeys>());
            await HeadlessHost.WaitUntil(() => Application.Current.RequestedThemeVariant == ThemeVariant.Default);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Zoom_commands_change_editor_font_size_within_8_to_32() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var settings = h.Get<SettingsService>();

        await registry.ExecuteAsync("oranda.view.zoomIn", keys);
        await HeadlessHost.WaitUntil(() => Equals(Application.Current!.Resources["OrandaEditorFontSize"], 14.0));
        Assert.Equal(14, settings.Current.Editor.FontSize);

        await registry.ExecuteAsync("oranda.view.zoomOut", keys);
        await registry.ExecuteAsync("oranda.view.zoomOut", keys);
        await HeadlessHost.WaitUntil(() => Equals(Application.Current!.Resources["OrandaEditorFontSize"], 12.0));

        await registry.ExecuteAsync("oranda.view.zoomReset", keys);
        await HeadlessHost.WaitUntil(() => Equals(Application.Current!.Resources["OrandaEditorFontSize"], 13.0));

        for (var i = 0; i < 40; i++)
        {
            await registry.ExecuteAsync("oranda.view.zoomOut", keys);
        }

        Assert.Equal(8, settings.Current.Editor.FontSize);
        await settings.ZoomResetAsync();
    });

    [Fact]
    public Task Settings_window_saves_theme_and_editor_font_and_applies_without_restart() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var h = new ShellHarness(dir);
            await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.settings", h.Get<IContextKeys>());

            var window = Assert.IsType<SettingsWindow>(h.Get<ShellController>().LastWindow);
            var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
            Assert.Equal(Theme.System, vm.SelectedTheme.Theme);
            Assert.Equal(13m, vm.FontSize);

            vm.SelectedTheme = vm.Themes.Single(t => t.Theme == Theme.Light);
            vm.FontFamily = "Consolas";
            vm.FontSize = 16;
            vm.TabSize = 2;
            await vm.SaveAsync();

            await HeadlessHost.WaitUntil(() => Application.Current!.RequestedThemeVariant == ThemeVariant.Light);
            await HeadlessHost.WaitUntil(() => Equals(Application.Current!.Resources["OrandaEditorFontSize"], 16.0));
            var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.Contains("\"fontFamily\": \"Consolas\"", json);
            Assert.Contains("\"tabSize\": 2", json);

            var reread = (await h.Get<ISettingsStore>().LoadAsync(default)).Value;
            Assert.Equal(Theme.Light, reread.Ui.Theme);
            Assert.Equal(16, reread.Editor.FontSize);
            Assert.Equal("Consolas", reread.Editor.FontFamily);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Settings_window_reports_read_only_when_file_is_from_a_newer_version() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-future-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string original = """{ "schemaVersion": 9, "ui": { "theme": "Dark" } }""";
        File.WriteAllText(Path.Combine(dir, "settings.json"), original);
        try
        {
            using var h = new ShellHarness(dir);
            await HeadlessHost.WaitUntil(() => Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
            await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.settings", h.Get<IContextKeys>());
            var vm = (SettingsViewModel)h.Get<ShellController>().LastWindow!.DataContext!;
            vm.FontSize = 20;
            await vm.SaveAsync();

            Assert.True(vm.IsReadOnly);
            Assert.True(vm.HasNotice);
            Assert.Equal(original, File.ReadAllText(Path.Combine(dir, "settings.json")));
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Command_palette_opens_with_the_keyboard_shortcut_and_runs_a_command() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var shell = h.Get<ShellController>();
        h.Window.Activate();

        var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        h.Window.KeyPress(Key.P, primary | RawInputModifiers.Shift, PhysicalKey.P, "P");
        await HeadlessHost.WaitUntil(() => shell.IsPaletteOpen);

        var window = shell.Palette!;
        var vm = Assert.IsType<CommandPaletteViewModel>(window.DataContext);
        vm.Query = "정보";
        Assert.Equal("oranda.help.about", vm.Selected!.Id);

        await vm.ExecuteSelectedAsync();
        await HeadlessHost.WaitUntil(() => !shell.IsPaletteOpen);
        Assert.Equal("Oranda 정보", shell.LastWindow!.Title);
        shell.LastWindow.Close();
    });

    [Fact]
    public Task Command_palette_finds_every_registered_command_by_id_and_by_title() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var text = h.Get<IShellText>();
        var vm = new CommandPaletteViewModel(registry, h.Get<IContextKeys>(), text, _ => null);

        var all = registry.Commands.Where(c => c.ShowInPalette).ToList();
        Assert.NotEmpty(all);
        Assert.Equal(all.Count, vm.Results.Count);

        foreach (var c in all)
        {
            Assert.True(vm.Search(c.Id).Any(r => r.Id == c.Id), "id: " + c.Id);
            Assert.True(vm.Search(text.Title(c)).Any(r => r.Id == c.Id), "title: " + text.Title(c));
            Assert.True(vm.Search(text.CategoryName(c.Category) + ": " + text.Title(c)).Any(r => r.Id == c.Id), "display: " + text.CategoryName(c.Category) + ": " + text.Title(c));
        }
    });

    [Fact]
    public Task Command_palette_finds_commands_registered_later_by_extensions() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var ran = new List<string>();
        using var reg = registry.Register(
            new CommandDefinition { Id = "oranda.ext.aiChat.explainPlan", Title = "Cmd.oranda.ext.aiChat.explainPlan", Category = CommandCategory.Tools, Aliases = ["oranda.ext.oldExplain"] },
            new RecordingHandler(ran));

        var vm = new CommandPaletteViewModel(registry, h.Get<IContextKeys>(), h.Get<IShellText>(), _ => null);
        Assert.Contains(vm.Search("explain plan"), r => r.Id == "oranda.ext.aiChat.explainPlan");
        Assert.Contains(vm.Search("oldExplain"), r => r.Id == "oranda.ext.aiChat.explainPlan");

        vm.Query = "aiChat";
        Assert.True(await vm.ExecuteSelectedAsync());
        Assert.Equal(["oranda.ext.aiChat.explainPlan"], ran);
    });

    [Fact]
    public Task Command_palette_greys_out_disabled_commands_and_does_not_run_them() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var vm = new CommandPaletteViewModel(h.Get<ICommandRegistry>(), h.Get<IContextKeys>(), h.Get<IShellText>(), _ => null);

        vm.Query = "oranda.file.close";
        var item = vm.Results.First(r => r.Id == "oranda.file.close");
        Assert.False(item.IsEnabled);

        vm.Selected = item;
        Assert.False(await vm.ExecuteSelectedAsync());
    });

    [Fact]
    public Task Command_palette_puts_recently_run_commands_first_and_orders_by_match_quality() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var text = h.Get<IShellText>();

        var plain = new CommandPaletteViewModel(registry, keys, text, _ => null);
        var recent = new CommandPaletteViewModel(registry, keys, text, _ => null, ["oranda.help.about"]);

        Assert.Equal("oranda.help.about", recent.Search(string.Empty)[0].Id);
        Assert.NotEqual("oranda.help.about", plain.Search(string.Empty)[0].Id);

        // 제목이 검색어로 시작하는 명령이 id 에만 걸리는 명령보다 앞선다.
        var results = plain.Search("글자");
        Assert.StartsWith("글자", results[0].Title, StringComparison.Ordinal);
    });

    [Fact]
    public Task Palette_button_and_menu_item_open_the_same_palette() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var shell = h.Get<ShellController>();

        var item = TopMenu(h, "보기").Items.OfType<MenuItem>().Single(m => (string?)m.Header == "명령 팔레트");
        item.Command!.Execute(null);
        await HeadlessHost.WaitUntil(() => shell.IsPaletteOpen);
        shell.Palette!.Close();
        await HeadlessHost.WaitUntil(() => !shell.IsPaletteOpen);

        h.Window.FindControl<Button>("PaletteButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => shell.IsPaletteOpen);
        shell.Palette!.Close();
    });

    [Fact]
    public Task Menu_items_follow_context_keys() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var window = TopMenu(h, "창");
        var next = window.Items.OfType<MenuItem>().First(m => (string?)m.Header == "다음 탭");
        var close = TopMenu(h, "파일").Items.OfType<MenuItem>().First(m => (string?)m.Header == "탭 닫기");

        Assert.False(next.Command!.CanExecute(null));
        Assert.False(close.Command!.CanExecute(null));

        h.Get<DockWorkspace>().OpenDocument("oranda.panel.sqlEditor");
        await HeadlessHost.WaitUntil(() => close.Command.CanExecute(null));

        Assert.True(next.Command.CanExecute(null));
    });

    [Fact]
    public Task Runtime_command_registration_adds_a_menu_item() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();

        using var reg = registry.Register(
            new CommandDefinition { Id = "oranda.ext.test.hello", Title = "Cmd.oranda.ext.test.hello", Category = CommandCategory.Tools, Placements = ["menu:tools/hello@500"] },
            new RecordingHandler([]));
        await HeadlessHost.WaitUntil(() => TopMenu(h, "도구").Items.OfType<MenuItem>().Any(m => m.Command is RegistryCommand { CommandId: "oranda.ext.test.hello" }));
    });

    [Fact]
    public Task Active_environment_draws_the_border_and_marks_the_title() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ui = h.Get<IShellUi>();
        var border = h.Window.FindControl<Border>("EnvironmentBorder")!;

        Assert.Equal(0, border.BorderThickness.Left);
        Assert.Equal("Oranda", h.Window.Title);

        ui.ActiveEnvironment = ConnectionEnvironment.Production;
        Assert.Equal(3, border.BorderThickness.Left);
        Assert.Contains("env-production", border.Classes);
        Assert.Contains("[운영]", h.Window.Title);
        Assert.Contains("env-production", h.Window.FindControl<Border>("StatusBarBorder")!.Classes);

        ui.ActiveEnvironment = ConnectionEnvironment.Development;
        Assert.DoesNotContain("env-production", border.Classes);
        Assert.Contains("env-development", border.Classes);
        Assert.DoesNotContain("[운영]", h.Window.Title);

        ui.ActiveEnvironment = null;
        Assert.Equal(0, border.BorderThickness.Left);
    });

    [Fact]
    public Task Startup_does_not_load_oracle_or_antlr_assemblies() => HeadlessHost.Run(() =>
    {
        var loaded = new List<string>();
        void OnLoad(object? s, AssemblyLoadEventArgs e) => loaded.Add(e.LoadedAssembly.GetName().Name ?? string.Empty);
        AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
        try
        {
            using var h = new ShellHarness();
            h.Window.RebuildMenus();
            Dispatcher.UIThread.RunJobs();

            Assert.False(h.Services.DeferredScanned);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnLoad;
        }

        Assert.DoesNotContain(loaded, n => n.StartsWith("Oracle.", StringComparison.Ordinal)
            || n.StartsWith("Antlr4", StringComparison.Ordinal)
            || n is "Oranda.Oracle" or "Oranda.Language");
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    public Task Text_is_not_clipped_in_main_window_and_dialogs_at_every_scale(double scale) => HeadlessHost.Run(async () =>
    {
        // 1920×1080 화면(작업 영역 1040)의 배율별 DIP 작업 영역에서 연다: 250 % = 768×416 DIP.
        var area = new ScreenArea("S", 0, 0, (int)(1920 / scale), (int)(1040 / scale), true);
        var placement = WindowPlacementPolicy.Resolve(null, [area]);
        var (minW, minH) = WindowPlacementPolicy.MinimumFor(area);

        using var h = new ShellHarness();
        var w = h.Window;
        w.SetRenderScaling(scale);
        w.MinWidth = minW;
        w.MinHeight = minH;
        w.Width = placement.Width;
        w.Height = placement.Height;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        AssertNoClipping(TextClipping.Find(w));

        // 설정 창·정보 창·명령 팔레트도 같은 배율에서 글자가 잘리지 않는다.
        var keys = h.Get<IContextKeys>();
        var registry = h.Get<ICommandRegistry>();
        var shell = h.Get<ShellController>();
        foreach (var id in DialogCommands)
        {
            await registry.ExecuteAsync(id, keys);
            var dialog = shell.LastWindow!;
            dialog.SetRenderScaling(scale);
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            AssertNoClipping(TextClipping.Find(dialog));
            Assert.True(dialog.Bounds.Width <= area.Width + 1 && dialog.Bounds.Height <= area.Height + 1, $"{id} 창이 작업 영역({area.Width}×{area.Height})보다 크다: {dialog.Bounds}");
            dialog.Close();
        }
    });

    [Fact]
    public Task Window_placement_is_saved_in_dip_with_minimum_size() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-place-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var h = new ShellHarness(dir))
            {
                Assert.True(h.Window.MinWidth <= 960 && h.Window.MinHeight <= 600);
                await h.Get<ShellController>().SaveLayoutAsync(h.Window.CapturePlacement());
            }

            using var again = new ShellHarness(dir);
            var saved = again.Get<ShellController>().SavedWindow!;
            Assert.True(saved.Width >= 800 && saved.Height >= 500);
            Assert.Equal(WindowStateKind.Normal, saved.State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    private static readonly string[] DialogCommands = ["oranda.tools.settings", "oranda.help.about", "oranda.view.commandPalette"];

    private static void AssertNoClipping(IReadOnlyList<string> offenders) => Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

    private static string Dump(LayoutDocument l) => System.Text.Json.JsonSerializer.Serialize(l, ShellJsonContext.Default.LayoutDocument);

    private static int CountDocumentNodes(DockNode n) =>
        (n.Kind == DockNodeKind.Documents ? 1 : 0) + (n.Children ?? []).Sum(CountDocumentNodes);

    private sealed class RecordingHandler(List<string> calls) : ICommandHandler
    {
        public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
        {
            calls.Add(context.CommandId);
            return Task.CompletedTask;
        }
    }
}
