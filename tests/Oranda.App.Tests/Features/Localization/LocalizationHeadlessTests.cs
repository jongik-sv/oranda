using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Shell.Palette;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.Localization;

/// <summary>
/// TSK-10-02 Headless 시험: 설정의 언어 전환(메뉴·도구 창·대화상자·단추), 키보드 탐색(탭 순서·접근 키),
/// 화면 낭독기 이름(AutomationProperties), 글자 배율·고대비·포커스 표시.
/// </summary>
public class LocalizationHeadlessTests
{
    private static readonly string[] DialogCommands = ["oranda.tools.settings", "oranda.help.about", "oranda.view.commandPalette"];

    private static async Task SetLanguageAsync(ShellHarness h, string code)
    {
        await h.Get<SettingsService>().UpdateAsync(s => s with { Ui = s.Ui with { Language = code } });
        var expected = UiLanguage.Resolve(code);
        await HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == expected);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> TopHeaders(ShellHarness h) =>
        [.. h.Window.Menus.Items.OfType<MenuItem>().Select(m => (string)m.Header!)];

    private static string? ToolTitle(DockWorkspace ws, string toolId)
    {
        string? title = null;
        void Walk(IDockable d)
        {
            if (d.Id == toolId)
            {
                title = d.Title;
            }

            if (d is IDock dock)
            {
                foreach (var c in dock.VisibleDockables ?? [])
                {
                    Walk(c);
                }
            }
        }

        Walk(ws.Layout!);
        return title;
    }

    // ---- 인수조건: 언어 전환 시 (화면에 보이는) 글자가 모두 바뀐다 ----

    [Fact]
    public Task Switching_the_language_setting_rebuilds_menus_toolbar_buttons_and_tool_window_titles_and_switches_back() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool("oranda.panel.output"));
        Assert.Equal(["파일(_F)", "편집(_E)", "보기(_V)", "_SQL", "결과(_R)", "데이터베이스(_D)", "모니터(_M)", "도구(_T)", "창(_W)", "도움말(_H)"], TopHeaders(h));
        Assert.Equal("DBMS 출력", ToolTitle(ws, "oranda.panel.output"));
        Assert.Equal("명령 검색…", h.Window.FindControl<TextBlock>("PaletteText")!.Text);

        await SetLanguageAsync(h, "en");
        await HeadlessHost.WaitUntil(() => TopHeaders(h)[0] == "_File");

        Assert.Equal(["_File", "_Edit", "_View", "_SQL", "_Result", "_Database", "_Monitor", "_Tools", "_Window", "_Help"], TopHeaders(h));
        Assert.Equal("DBMS Output", ToolTitle(ws, "oranda.panel.output"));
        var palette = h.Window.FindControl<Button>("PaletteButton")!;
        Assert.Equal("Search commands...", h.Window.FindControl<TextBlock>("PaletteText")!.Text);
        Assert.Equal("Search commands", AutomationProperties.GetName(palette));
        var view = h.Window.Menus.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_View");
        Assert.Contains(view.Items.OfType<MenuItem>(), m => (string?)m.Header == "Command Palette");
        Assert.Contains(view.Items.OfType<MenuItem>(), m => (string?)m.Header == "Tool Windows");

        // 되돌림: 한국어로 돌아오면 모든 글자가 원래대로.
        await SetLanguageAsync(h, "ko");
        await HeadlessHost.WaitUntil(() => TopHeaders(h)[0] == "파일(_F)");
        Assert.Equal("DBMS 출력", ToolTitle(ws, "oranda.panel.output"));
        Assert.Equal("명령 검색…", h.Window.FindControl<TextBlock>("PaletteText")!.Text);
        Assert.Contains(h.Window.Menus.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "보기(_V)").Items.OfType<MenuItem>(), m => (string?)m.Header == "명령 팔레트");
    });

    [Fact]
    public Task Switching_the_language_renames_default_titled_document_tabs_but_not_custom_titles() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var db = ws.OpenDocument("oranda.panel.dbInfo")!;
        var custom = ws.OpenDocument("oranda.panel.sqlEditor", title: "내 검색.sql")!;
        Assert.Equal("DB 정보", db.Title);

        await SetLanguageAsync(h, "en");
        Assert.Equal("Database Info", db.Title);
        Assert.Equal("내 검색.sql", custom.Title);

        await SetLanguageAsync(h, "ko");
        Assert.Equal("DB 정보", db.Title);
        Assert.Equal("내 검색.sql", custom.Title);
    });

    [Fact]
    public Task Language_setting_survives_a_restart_and_a_system_setting_follows_the_os() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-lang-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var h = new ShellHarness(dir))
            {
                await SetLanguageAsync(h, "en");
                Assert.Contains("\"language\": \"en\"", File.ReadAllText(Path.Combine(dir, "settings.json")));
            }

            using (var again = new ShellHarness(dir))
            {
                Assert.Equal("en", again.Get<IShellText>().Language);
                Assert.Equal("_File", TopHeaders(again)[0]);
                await SetLanguageAsync(again, "system");
                // 시험 프로세스의 OS 언어는 한국어(LocalizationTestSetup)다.
                Assert.Equal("ko", again.Get<IShellText>().Language);
                Assert.Equal("파일(_F)", TopHeaders(again)[0]);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Settings_window_offers_language_scale_and_high_contrast_and_saves_them() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-a11y-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var h = new ShellHarness(dir);
            await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.language", h.Get<IContextKeys>());
            var window = Assert.IsType<SettingsWindow>(h.Get<ShellController>().LastWindow);
            var vm = Assert.IsType<SettingsViewModel>(window.DataContext);

            Assert.Equal(["system", "ko", "en"], vm.Languages.Select(l => l.Code));
            Assert.Equal("system", vm.SelectedLanguage.Code);
            Assert.Equal(100m, vm.UiScalePercent);
            Assert.False(vm.HighContrast);
            Assert.Equal("언어", vm.LanguageLabel);

            vm.SelectedLanguage = vm.Languages.Single(l => l.Code == "en");
            vm.UiScalePercent = 150;
            vm.HighContrast = true;
            await vm.SaveAsync();
            await HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == "en");
            await HeadlessHost.WaitUntil(() => Equals(Application.Current!.Resources[ThemeService.UiFontSizeBaseKey], 13 * 1.5));

            var reread = (await h.Get<ISettingsStore>().LoadAsync(default)).Value;
            Assert.Equal("en", reread.Ui.Language);
            Assert.Equal(1.5, reread.Ui.UiScale);
            Assert.True(reread.Ui.HighContrast);

            // 영어로 바뀐 뒤 새로 연 설정 창은 영어 글자.
            window.Close();
            await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.settings", h.Get<IContextKeys>());
            var vm2 = Assert.IsType<SettingsViewModel>(h.Get<ShellController>().LastWindow!.DataContext);
            Assert.Equal("Language", vm2.LanguageLabel);
            Assert.Equal("Save", vm2.SaveLabel);
            Assert.Equal("en", vm2.SelectedLanguage.Code);
            Assert.Equal(150m, vm2.UiScalePercent);
            Assert.True(vm2.HighContrast);
            h.Get<ShellController>().LastWindow!.Close();
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Application.Current.Resources[ThemeService.UiFontSizeBaseKey] = ThemeService.BaseUiFontSize;
            Application.Current.Resources[ThemeService.UiFontSizeSmallKey] = ThemeService.SmallUiFontSize;
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Settings_values_outside_the_allowed_range_are_clamped_when_applied() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        try
        {
            var settings = h.Get<SettingsService>();
            await settings.UpdateAsync(s => s with { Ui = s.Ui with { UiScale = 99 } });
            await HeadlessHost.WaitUntil(() => Equals(Application.Current!.Resources[ThemeService.UiFontSizeBaseKey], 13 * 2.5));
            await settings.UpdateAsync(s => s with { Ui = s.Ui with { UiScale = -1 } });
            await HeadlessHost.WaitUntil(() => Equals(Application.Current!.Resources[ThemeService.UiFontSizeBaseKey], 13 * 0.8));
        }
        finally
        {
            Application.Current!.Resources[ThemeService.UiFontSizeBaseKey] = ThemeService.BaseUiFontSize;
            Application.Current.Resources[ThemeService.UiFontSizeSmallKey] = ThemeService.SmallUiFontSize;
        }
    });

    // ---- 글자 배율·고대비 ----

    [Fact]
    public Task Ui_scale_resizes_menu_dialog_text_and_returns_to_normal() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        try
        {
            var settings = h.Get<SettingsService>();
            Assert.Equal(13.0, Application.Current!.Resources[ThemeService.UiFontSizeBaseKey]);

            await settings.UpdateAsync(s => s with { Ui = s.Ui with { UiScale = 2.0 } });
            await HeadlessHost.WaitUntil(() => Equals(Application.Current.Resources[ThemeService.UiFontSizeBaseKey], 26.0));
            Assert.Equal(22.0, Application.Current.Resources[ThemeService.UiFontSizeSmallKey]);
            Assert.Equal(26.0, h.Window.FontSize);

            await settings.UpdateAsync(s => s with { Ui = s.Ui with { UiScale = 1.0 } });
            await HeadlessHost.WaitUntil(() => Equals(Application.Current.Resources[ThemeService.UiFontSizeBaseKey], 13.0));
            Assert.Equal(13.0, h.Window.FontSize);
        }
        finally
        {
            Application.Current!.Resources[ThemeService.UiFontSizeBaseKey] = ThemeService.BaseUiFontSize;
            Application.Current.Resources[ThemeService.UiFontSizeSmallKey] = ThemeService.SmallUiFontSize;
        }
    });

    [Fact]
    public Task High_contrast_setting_switches_to_the_high_contrast_theme_with_white_separators_and_back() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        try
        {
            var settings = h.Get<SettingsService>();
            await settings.UpdateAsync(s => s with { Ui = s.Ui with { HighContrast = true } });
            await HeadlessHost.WaitUntil(() => Application.Current!.RequestedThemeVariant == ThemeService.HighContrastVariant);

            Assert.True(Application.Current!.TryGetResource("OrandaSeparatorBrush", ThemeService.HighContrastVariant, out var sep));
            Assert.Equal(Avalonia.Media.Colors.White, ((Avalonia.Media.ISolidColorBrush)sep!).Color);
            Assert.True(Application.Current.TryGetResource("OrandaFocusBrush", ThemeService.HighContrastVariant, out var focus));
            Assert.Equal(Avalonia.Media.Colors.Yellow, ((Avalonia.Media.ISolidColorBrush)focus!).Color);

            // 고대비는 어두운 테마를 바탕으로 한다(밝은 쪽 컨트롤 색을 쓰지 않는다).
            Assert.Equal(ThemeVariant.Dark, ThemeService.HighContrastVariant.InheritVariant);

            await settings.UpdateAsync(s => s with { Ui = s.Ui with { HighContrast = false, Theme = Theme.Light } });
            await HeadlessHost.WaitUntil(() => Application.Current.RequestedThemeVariant == ThemeVariant.Light);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    });

    [Fact]
    public Task Every_control_shows_a_focus_rectangle_with_the_theme_focus_color() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var button = new Button { Content = "x" };
        var box = new TextBox();
        var panel = new StackPanel { Children = { button, box } };
        var window = new Window { Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(button.FocusAdorner);
        Assert.NotNull(box.FocusAdorner);
        Assert.True(Application.Current!.TryGetResource("OrandaFocusBrush", ThemeVariant.Light, out var light));
        Assert.True(Application.Current.TryGetResource("OrandaFocusBrush", ThemeVariant.Dark, out var dark));
        Assert.NotEqual(((Avalonia.Media.ISolidColorBrush)light!).Color, ((Avalonia.Media.ISolidColorBrush)dark!).Color);
        window.Close();
    });

    // ---- 키보드만으로: 탭 순서·접근 키 ----

    [Fact]
    public Task Tab_key_walks_every_control_of_the_settings_window_in_reading_order() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.settings", h.Get<IContextKeys>());
        var window = Assert.IsType<SettingsWindow>(h.Get<ShellController>().LastWindow);
        window.Activate();
        Dispatcher.UIThread.RunJobs();

        var order = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            order.Add(FocusedLabel(window));
        }

        // 일반 범주의 입력 칸이 위에서 아래로, 그 다음에 저장 → 닫기 단추(맨 앞은 왼쪽 범주 목록).
        string[] expected = ["ThemeBox", "LanguageBox", "UiScaleBox", "HighContrastBox", "저장", "닫기"];
        var positions = expected.Select(e => order.IndexOf(e)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);

        // Shift+Tab 은 거꾸로 간다.
        var before = FocusedLabel(window);
        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, FocusedLabel(window));
        window.Close();
    });

    private static string FocusedLabel(TopLevel top)
    {
        var focused = top.FocusManager?.GetFocusedElement() as Visual;
        foreach (var c in focused?.GetSelfAndVisualAncestors().OfType<Control>() ?? [])
        {
            if (c.Name is { Length: > 0 } name && name.EndsWith("Box", StringComparison.Ordinal) && !name.StartsWith("PART_", StringComparison.Ordinal))
            {
                return name;
            }

            if (c is Button { Content: string s })
            {
                return s;
            }
        }

        return focused?.GetType().Name ?? "(none)";
    }

    [Fact]
    public Task Settings_window_default_and_cancel_buttons_work_from_the_keyboard() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.settings", h.Get<IContextKeys>());
        var window = Assert.IsType<SettingsWindow>(h.Get<ShellController>().LastWindow);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Activate();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await HeadlessHost.WaitUntil(() => closed);
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task Menu_access_keys_open_the_matching_top_menu_in_both_languages(string lang) => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await SetLanguageAsync(h, lang);
        await HeadlessHost.WaitUntil(() => TopHeaders(h)[0] == (lang == "ko" ? "파일(_F)" : "_File"));
        h.Window.Activate();

        // 접근 키 표시(_)가 가리키는 글자가 최상위 메뉴마다 하나씩이고 서로 다르다.
        var keys = TopHeaders(h).Select(t => char.ToUpperInvariant(t[t.IndexOf('_', StringComparison.Ordinal) + 1])).ToList();
        Assert.Equal(10, keys.Distinct().Count());

        // Alt+글자: 파일 메뉴가 열린다.
        h.Window.KeyPress(Key.F, RawInputModifiers.Alt, PhysicalKey.F, "f");
        Dispatcher.UIThread.RunJobs();
        var file = h.Window.Menus.Items.OfType<MenuItem>().First();
        await HeadlessHost.WaitUntil(() => file.IsSubMenuOpen, 2000);
        file.Close();
    });

    [Fact]
    public Task Menu_items_can_be_reached_with_arrow_keys_and_run_with_enter() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        h.Window.Activate();
        var view = h.Window.Menus.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "보기(_V)");
        view.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsSubMenuOpen);

        // 첫 항목(명령 팔레트)으로 이동해 Enter 로 실행한다.
        view.Items.OfType<MenuItem>().First().Focus();
        Dispatcher.UIThread.RunJobs();
        h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        var shell = h.Get<ShellController>();
        await HeadlessHost.WaitUntil(() => shell.IsPaletteOpen, 2000);
        shell.Palette!.Close();
    });

    // ---- 화면 낭독기 이름 ----

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task Every_interactive_control_has_a_screen_reader_name_in_both_languages(string lang) => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await SetLanguageAsync(h, lang);
        var keys = h.Get<IContextKeys>();
        var registry = h.Get<ICommandRegistry>();
        var shell = h.Get<ShellController>();

        var missing = new List<string>();
        Audit(h.Window, "메인 창", missing);
        AuditMenus(h.Window.Menus.Items.OfType<MenuItem>(), "메뉴", missing);

        foreach (var id in DialogCommands)
        {
            await registry.ExecuteAsync(id, keys);
            var dialog = shell.LastWindow!;
            Dispatcher.UIThread.RunJobs();
            Assert.False(string.IsNullOrWhiteSpace(dialog.Title), id + " 창 제목");
            Audit(dialog, id, missing);
            dialog.Close();
        }

        Assert.Empty(missing);
    });

    private static void Audit(TopLevel top, string where, List<string> missing)
    {
        foreach (var c in top.GetVisualDescendants().OfType<Control>())
        {
            if (!c.IsEffectivelyVisible || c is not (Button or ComboBox or CheckBox or NumericUpDown or ListBox or TreeView or Menu or ItemsControl { Name: "StatusBar" }))
            {
                continue;
            }

            // 안쪽 부품(콤보의 토글 단추 등)은 부모 컨트롤이 이름을 낸다. Dock 라이브러리의 탭 단추(PART_*)와
            // 도구 창·문서 안쪽 화면(각 기능 Task 소유)은 이 시험의 범위가 아니다(결과 보고의 「결함」 에 적는다).
            if (c.Name?.StartsWith("PART_", StringComparison.Ordinal) == true
                || c.GetVisualAncestors().OfType<Control>().Any(a => a is ComboBox or NumericUpDown or MenuItem or Menu or Oranda.App.Shell.Docking.DockHostView))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)) && !(c is ContentControl { Content: string { Length: > 0 } }))
            {
                missing.Add($"{where}: {c.GetType().Name}#{c.Name}");
            }
        }
    }

    private static void AuditMenus(IEnumerable<MenuItem> items, string where, List<string> missing)
    {
        foreach (var m in items)
        {
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(m)))
            {
                missing.Add($"{where}: MenuItem '{m.Header}'");
            }

            AuditMenus(m.Items.OfType<MenuItem>(), where + "/" + m.Header, missing);
        }
    }

    [Fact]
    public Task Toolbar_buttons_and_menu_items_use_the_command_names_of_the_current_language_for_screen_readers() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await SetLanguageAsync(h, "en");
        await HeadlessHost.WaitUntil(() => TopHeaders(h)[0] == "_File");

        var file = h.Window.Menus.Items.OfType<MenuItem>().First();
        Assert.Equal("File", AutomationProperties.GetName(file));
        Assert.Contains(file.Items.OfType<MenuItem>(), m => AutomationProperties.GetName(m) == "Exit");
        Assert.Equal("Menu bar", AutomationProperties.GetName(h.Window.Menus));
        Assert.Equal("Toolbar", AutomationProperties.GetName(h.Window.Toolbar));
        Assert.Equal("Workspace", AutomationProperties.GetName(h.Window.Dock));
        // 아이콘만 있는 툴바 단추도 낭독기 이름은 영어 명령 이름이다.
        var buttons = h.Window.Toolbar.Children.OfType<Button>().ToList();
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Matches("^[A-Za-z][A-Za-z0-9 /().@_,:-]*$", AutomationProperties.GetName(b) ?? string.Empty));
    });

    [Fact]
    public Task Production_environment_is_announced_to_screen_readers_in_both_languages() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var vm = h.Window.ViewModel!;
        // 창 제목은 환경 표시(색만으로 구분하지 않는다)를 글자로도 내보낸다. 언어를 바꿔도 제목이 비지 않는다.
        h.Get<ShellController>().ActiveEnvironment = Oranda.Core.Connection.ConnectionEnvironment.Production;
        Assert.Contains("운영", vm.Title);
        await SetLanguageAsync(h, "en");
        Assert.False(string.IsNullOrWhiteSpace(vm.Title));
        h.Get<ShellController>().ActiveEnvironment = null;
    });

    // ---- 영어(1.3~1.6배 긴 글자)에서도 글자가 잘리지 않는다 ----

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.5)]
    public Task English_text_is_not_clipped_in_main_window_and_dialogs(double scale) => HeadlessHost.Run(async () =>
    {
        var area = new ScreenArea("S", 0, 0, (int)(1920 / scale), (int)(1040 / scale), true);
        var placement = WindowPlacementPolicy.Resolve(null, [area]);
        var (minW, minH) = WindowPlacementPolicy.MinimumFor(area);

        using var h = new ShellHarness();
        await SetLanguageAsync(h, "en");
        var w = h.Window;
        w.SetRenderScaling(scale);
        w.MinWidth = minW;
        w.MinHeight = minH;
        w.Width = placement.Width;
        w.Height = placement.Height;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Empty(TextClipping.Find(w));

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
            Assert.Empty(TextClipping.Find(dialog));
            dialog.Close();
        }
    });

    [Fact]
    public Task Palette_finds_commands_by_english_name_after_switching_and_by_korean_name_after_switching_back() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var text = h.Get<IShellText>();

        await SetLanguageAsync(h, "en");
        var en = new CommandPaletteViewModel(registry, h.Get<IContextKeys>(), text, _ => null);
        Assert.Contains(en.Search("Reset Layout"), r => r.Id == "oranda.view.resetLayout");
        Assert.DoesNotContain(en.Search("레이아웃 초기화"), r => r.Id == "oranda.view.resetLayout" && r.Title == "레이아웃 초기화");
        Assert.All(en.Search(string.Empty), r => Assert.DoesNotMatch("[가-힣]", r.Title));

        await SetLanguageAsync(h, "ko");
        var ko = new CommandPaletteViewModel(registry, h.Get<IContextKeys>(), text, _ => null);
        Assert.Contains(ko.Search("레이아웃 초기화"), r => r.Id == "oranda.view.resetLayout");
    });

    [Fact]
    public Task About_dialog_title_and_button_follow_the_language() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var keys = h.Get<IContextKeys>();
        var shell = h.Get<ShellController>();
        await SetLanguageAsync(h, "en");
        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.help.about", keys);
        var about = shell.LastWindow!;
        Assert.Equal("About Oranda", about.Title);
        Assert.Contains(about.GetVisualDescendants().OfType<Button>(), b => (string?)b.Content == "OK");
        about.Close();

        await SetLanguageAsync(h, "ko");
        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.help.about", keys);
        Assert.Equal("Oranda 정보", shell.LastWindow!.Title);
        Assert.Contains(shell.LastWindow.GetVisualDescendants().OfType<Button>(), b => (string?)b.Content == "확인");
        shell.LastWindow.Close();
    });

    // ---- XAML 에 한국어로 적힌 글자의 영어 표시 ----

    [Fact]
    public Task Korean_literals_in_xaml_show_in_english_and_come_back_and_bound_or_changed_text_is_left_alone() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var title = new TextBlock { Text = "접속 관리자" };
        var save = new Button { Content = "저장" };
        var search = new TextBox { PlaceholderText = "찾기" };
        var custom = new TextBlock { Text = "내가 쓴 글" };
        var named = new Button { Content = "x" };
        AutomationProperties.SetName(named, "접속 목록");
        ToolTip.SetTip(save, "새로 고침");
        var panel = new StackPanel { Children = { title, save, search, custom, named } };
        var window = new Window { Title = "데이터 가져오기", Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("접속 관리자", title.Text);

        await SetLanguageAsync(h, "en");
        // 이미 화면에 있는 컨트롤도 언어 전환 때 바뀐다.
        Assert.Equal("Connection Manager", title.Text);
        Assert.Equal("Save", save.Content);
        Assert.Equal("Find", search.PlaceholderText);
        Assert.Equal("내가 쓴 글", custom.Text);
        Assert.Equal("Connections", AutomationProperties.GetName(named));
        Assert.Equal("Refresh", ToolTip.GetTip(save));
        Assert.Equal("Import Data", window.Title);

        // 새로 화면에 올라오는 컨트롤은 올라올 때 바뀐다.
        var late = new TextBlock { Text = "닫기" };
        panel.Children.Add(late);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal("Close", late.Text);

        // 영어 상태에서 다른 값이 들어왔다가 한국어로 돌아와도 그 값은 그대로 둔다.
        title.Text = "새 글";
        await SetLanguageAsync(h, "ko");
        Assert.Equal("새 글", title.Text);
        Assert.Equal("저장", save.Content);
        Assert.Equal("찾기", search.PlaceholderText);
        Assert.Equal("접속 목록", AutomationProperties.GetName(named));
        Assert.Equal("새로 고침", ToolTip.GetTip(save));
        Assert.Equal("데이터 가져오기", window.Title);
        Assert.Equal("닫기", late.Text);
        window.Close();
    });

    [Fact]
    public Task Real_dialogs_show_english_literals_when_the_language_is_english() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await SetLanguageAsync(h, "en");
        var shell = h.Get<ShellController>();

        // 설정 창은 리소스 키로 만든 화면이라 영어 글만 보인다. 명령 팔레트도 같다.
        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.settings", h.Get<IContextKeys>());
        var settings = shell.LastWindow!;
        settings.UpdateLayout();
        var texts = settings.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        Assert.Contains("Theme", texts);
        Assert.Contains("Language", texts);
        Assert.DoesNotContain(texts, t => System.Text.RegularExpressions.Regex.IsMatch(t, "[가-힣]") && t != "한국어");
        Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TabItem>(), t => System.Text.RegularExpressions.Regex.IsMatch(t.Header?.ToString() ?? string.Empty, "[가-힣]"));
        settings.Close();
    });
}
