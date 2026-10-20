using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Templates;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Language.Snippets;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.Templates;

/// <summary>
/// 템플릿 창 화면(Headless): 종류 단추·묶음 머리·미리보기, 키보드만으로 찾기 → 목록 → 삽입, 두 번 누르기 삽입,
/// 편집 대화상자, 영어 화면에서 글자 잘림 없음. 영어로 바꾸는 시험이 있어 UiLanguage 묶음에서 따로 돈다.
/// </summary>
[Collection("UiLanguage")]
public sealed class TemplatesPanelHeadlessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-tplh-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private (Window Window, TemplatesView View, TemplatesViewModel Vm, SqlEditorHost Host) Open(IShellText text, double width = 300)
    {
        var host = new SqlEditorHost();
        var vm = new TemplatesViewModel(new AppDataPaths(Path.Combine(_root, Guid.NewGuid().ToString("N"))), text) { Host = host };
        var view = new TemplatesView { DataContext = vm };
        var window = new Window { Width = width, Height = 640, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm, host);
    }

    private static T Named<T>(Control root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Press(Window window, Key key) => window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

    [Fact]
    public Task Kind_buttons_are_never_blank_and_filter_the_grouped_list() => HeadlessHost.Run(() =>
    {
        var (window, view, vm, _) = Open(new ShellText());
        string[] names = ["KindAll", "KindSnippet", "KindHint", "KindFunction", "KindPseudoColumn"];
        foreach (var name in names)
        {
            var radio = Named<RadioButton>(view, name);
            Assert.IsAssignableFrom<Control>(radio.Content); // 아이콘이 늘 그려진다(빈 칸 없음)
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(radio) as string), name);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(radio)), name);
            Assert.True(radio.Bounds.Width > 10, name);
        }

        Assert.True(Named<RadioButton>(view, "KindAll").IsChecked);

        // 묶음 머리: 아이콘·종류 이름·개수.
        var list = Named<TreeView>(view, "List");
        var headers = list.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.DataContext is TemplateGroupRow).ToList();
        Assert.Equal(4, headers.Count);
        var texts = headers[1].GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("힌트", texts);
        Assert.Contains(SnippetLibrary.Hints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), texts);

        // 함수 단추 → 함수 묶음 하나. 다시 눌러도 꺼지지 않는다.
        var function = Named<RadioButton>(view, "KindFunction");
        function.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SnippetKind.Function, vm.KindFilter);
        Assert.False(Named<RadioButton>(view, "KindAll").IsChecked);
        Assert.Equal(SnippetKind.Function, Assert.Single(vm.Groups).Kind);

        Named<RadioButton>(view, "KindAll").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.KindFilter);
        Assert.Equal(4, vm.Groups.Count);

        // 묶음 머리를 접으면 그 항목이 사라지고 다시 펴면 돌아온다.
        var hints = vm.Groups[1];
        TemplatesViewModel.Toggle(hints);
        Dispatcher.UIThread.RunJobs();
        Assert.False(((TreeViewItem)list.TreeContainerFromItem(hints)!).IsExpanded);
        TemplatesViewModel.Toggle(hints);
        Dispatcher.UIThread.RunJobs();
        Assert.True(((TreeViewItem)list.TreeContainerFromItem(hints)!).IsExpanded);
        window.Close();
    });

    [Fact]
    public Task Keyboard_only_find_list_and_insert_then_double_click_inserts_too() => HeadlessHost.Run(() =>
    {
        var (window, view, vm, host) = Open(new ShellText(), width: 360);
        var tab = host.NewDocument();
        var search = Named<TextBox>(view, "SearchBox");
        search.Focus();
        window.KeyTextInput("nvl2");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("nvl2", vm.Query);
        Assert.Equal("NVL2", vm.Selected?.Prefix);

        // 찾은 낱말은 줄임말에서 굵게 강조한다.
        window.UpdateLayout();
        var found = (TreeViewItem)Named<TreeView>(view, "List").TreeContainerFromItem(vm.SelectedRow!)!;
        var prefix = found.GetVisualDescendants().OfType<TextBlock>().First(t => t.Inlines is { Count: > 0 });
        var bold = Assert.IsType<Avalonia.Controls.Documents.Run>(prefix.Inlines![0]);
        Assert.Equal("NVL2", bold.Text);
        Assert.Equal(Avalonia.Media.FontWeight.Bold, bold.FontWeight);

        // 미리보기: 이름·설명 줄과 강조된 SQL.
        var preview = Named<SqlCodeView>(view, "Preview");
        Assert.Equal("SELECT NVL2(comm, 'Y', 'N') FROM emp", preview.TextEditorControl.Text);
        Assert.NotNull(preview.Colorizer);
        Assert.Contains("NVL2(expr, 값1, 값2)", Named<TextBlock>(view, "PreviewHeader").Inlines!.Select(i => (i as Avalonia.Controls.Documents.Run)?.Text));

        // ↓ 로 목록에 들어가고 Enter 로 넣는다.
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<TreeViewItem>(TopLevel.GetTopLevel(view)!.FocusManager!.GetFocusedElement());
        Press(window, Key.Enter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SELECT NVL2(comm, 'Y', 'N') FROM emp", tab.Text);

        // Esc 는 찾기 칸으로, 찾기 칸에서 Esc 는 찾는 글을 지운다.
        Named<TreeView>(view, "List").Focus();
        vm.Selected = vm.Items[0];
        Dispatcher.UIThread.RunJobs();
        var item = (TreeViewItem)Named<TreeView>(view, "List").TreeContainerFromItem(vm.SelectedRow!)!;
        item.Focus();
        Press(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.True(search.IsFocused);
        Press(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Query);

        // 찾기 칸에서 바로 Enter: 첫 항목을 넣는다.
        tab.Select(tab.Text.Length, 0);
        window.KeyTextInput("sysdate");
        Press(window, Key.Enter);
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith("SELECT SYSDATE FROM dual", tab.Text, StringComparison.Ordinal);

        // 두 번 누르기: 항목 행 가운데를 두 번 누른다.
        vm.Query = "rowid";
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var row = (TreeViewItem)Named<TreeView>(view, "List").TreeContainerFromItem(vm.SelectedRow!)!;
        var header = row.GetVisualDescendants().OfType<Grid>().First(g => g.DataContext is TemplateItemRow);
        var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        tab.Select(tab.Text.Length, 0);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith("SELECT ROWID, empno FROM emp", tab.Text, StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public Task Edit_dialog_highlights_the_body_validates_and_saves() => HeadlessHost.Run(async () =>
    {
        var (window, _, vm, _) = Open(new ShellText());
        var edit = vm.BeginNew();
        var dialog = new SnippetEditWindow { DataContext = edit };
        var shown = dialog.ShowDialog<bool>(window);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("새 조각", dialog.Title);
        Assert.NotNull(dialog.Colorizer);

        // 본문 칸에 친 글이 ViewModel 로 간다. 줄임말이 비면 저장되지 않고 오류를 보인다.
        dialog.BodyEditor.Document.Text = "SELECT ${1:x} FROM dual$0";
        Assert.Equal("SELECT ${1:x} FROM dual$0", edit.Body);
        Assert.False(dialog.Save());
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBlock>(dialog, "ErrorText").IsVisible);
        Assert.True(dialog.IsVisible);

        edit.Prefix = "mine";
        Assert.True(dialog.Save());
        Assert.True(await shown);
        Assert.True(await vm.CommitAsync(edit));
        Assert.Equal("mine", Assert.Single(vm.UserSnippets).Prefix);
        window.Close();
    });

    [Fact]
    public Task English_panel_and_edit_dialog_show_english_without_clipped_text() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await h.Get<SettingsService>().UpdateAsync(s => s with { Ui = s.Ui with { Language = "en" } });
        await HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == "en");
        Dispatcher.UIThread.RunJobs();

        var (window, view, vm, _) = Open(h.Get<IShellText>());
        await HeadlessHost.WaitUntil(() => view.IsLoaded);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Optimizer hints only", ToolTip.GetTip(Named<RadioButton>(view, "KindHint")));
        Assert.Equal("Search templates", Named<TextBox>(view, "SearchBox").PlaceholderText);
        Assert.Equal(["Snippets", "Hints", "Functions", "Pseudo-columns"], vm.Groups.Select(g => g.Title));
        vm.Selected = SnippetLibrary.FindByPrefix("sel");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal("Basic query", vm.PreviewDescription);

        // 창 경계 검사는 스크롤 밖 행도 센다. 묶음을 접어 화면에 보이는 것(단추·묶음 머리·미리보기·상태 줄)만 남긴다.
        // 항목 행의 글은 말줄임 + 도움말이라 원래 검사 대상이 아니다.
        foreach (var g in vm.Groups)
        {
            TemplatesViewModel.Toggle(g);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var clipped = TextClipping.Find(window);
        Assert.True(clipped.Count == 0, string.Join("\n", clipped));

        var dialog = new SnippetEditWindow { DataContext = vm.BeginNew() };
        var shown = dialog.ShowDialog<bool>(window);
        await HeadlessHost.WaitUntil(() => dialog.IsLoaded);
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Assert.Equal("New Snippet", dialog.Title);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Body");
        var clippedDialog = TextClipping.Find(dialog);
        Assert.True(clippedDialog.Count == 0, string.Join("\n", clippedDialog));
        dialog.Close(false);
        Assert.False(await shown);
        window.Close();
        Assert.Equal("en", LiteralTranslator.Language);
    });

    [Fact]
    public Task Panel_follows_light_dark_and_high_contrast_themes() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var settings = h.Get<SettingsService>();
        var (window, view, _, _) = Open(h.Get<IShellText>());
        var chip = Named<RadioButton>(view, "KindAll");
        var app = Application.Current!;
        try
        {
            foreach (var (theme, highContrast, variant) in new[]
            {
                (Oranda.Core.Settings.Theme.Light, false, ThemeVariant.Light),
                (Oranda.Core.Settings.Theme.Dark, false, ThemeVariant.Dark),
                (Oranda.Core.Settings.Theme.Light, true, Oranda.App.Shell.Appearance.ThemeService.HighContrastVariant),
            })
            {
                await settings.UpdateAsync(s => s with { Ui = s.Ui with { Theme = theme, HighContrast = highContrast } });
                await HeadlessHost.WaitUntil(() => app.ActualThemeVariant == variant);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                // 켜진 종류 단추는 그 테마의 툴바 눌림 색·아이콘 파랑 테두리로 그린다(색 값을 코드에 쓰지 않는다).
                var presenter = chip.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                Assert.True(chip.TryFindResource("OrandaToolbarPressedBrush", chip.ActualThemeVariant, out var pressed), variant.ToString());
                Assert.Same(pressed, presenter.Background);
                Assert.True(chip.TryFindResource("OrandaIconBlueBrush", chip.ActualThemeVariant, out var border));
                Assert.Same(border, presenter.BorderBrush);
            }
        }
        finally
        {
            await settings.UpdateAsync(s => s with { Ui = s.Ui with { Theme = Oranda.Core.Settings.Theme.System, HighContrast = false } });
            window.Close();
        }
    });

    [Fact]
    public Task View_templates_command_opens_the_panel_built_by_the_shell() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await h.Get<Oranda.Core.Shell.ICommandRegistry>().ExecuteAsync("oranda.view.templates", h.Get<Oranda.Core.Shell.IContextKeys>());
        TemplatesView? view = null;
        await HeadlessHost.WaitUntil(() => (view = h.Window.GetVisualDescendants().OfType<TemplatesView>().FirstOrDefault()) is not null);
        var vm = Assert.IsType<TemplatesViewModel>(view!.DataContext);
        Assert.Equal(4, vm.Groups.Count);
        Assert.Equal("ko", vm.Language);
        Assert.NotNull(vm.ShowEditor);
    });
}
