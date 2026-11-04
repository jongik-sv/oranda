using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using Dock.Avalonia.Controls;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Templates;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 편집기 장 화면 캡처 생성기. 실제 셸과 View 를 그리되 SQL 은 샘플 DB(<c>sample</c>)의 지어낸 조회만 쓴다.
/// 번호의 뜻은 <c>site/src/content/docs/editor/sql-editor.md</c>, <c>assist.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualEditorScreens
{
    private const string MainSql = "SELECT e.emp_name, d.dept_name, e.salary\nFROM employees e\nJOIN departments d ON d.dept_id = e.dept_id\nWHERE e.salary > 5000000\nORDER BY e.salary DESC";

    private const string OrdersSql = "SELECT c.customer_name, COUNT(*) AS order_count, SUM(o.total_amount) AS total\nFROM customers c\nJOIN orders o ON o.customer_id = c.customer_id\nGROUP BY c.customer_name\nORDER BY total DESC";

    /// <summary>번호 표시 한 개. 글자를 가리지 않게 필요하면 모서리에서 옮긴다.</summary>
    private sealed record Mark(Control Target, int Number, double Dx = 0, double Dy = 0);

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static Button ButtonNamed(Visual root, string name) =>
        ManualCapture.Find<Button>(root, b => AutomationProperties.GetName(b) == name || b.Content as string == name);

    private static T Up<T>(Visual from)
        where T : Visual =>
        from.FindAncestorOfType<T>() ?? throw new Xunit.Sdk.XunitException($"{typeof(T).Name} 조상을 찾지 못했다.");

    /// <summary>
    /// 셸 창은 내용을 옮겨 붙이면 문서 영역 View 가 새로 만들어져 찾아 둔 컨트롤이 떨어진다.
    /// 그래서 번호 표시 겹침판을 캡처 도우미에 맡기지 않고, 컨트롤을 찾기 전에 한 번만 직접 얹는다.
    /// </summary>
    private static Canvas AddOverlay(Window window)
    {
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var original = window.Content as Control ?? throw new InvalidOperationException("창 내용이 컨트롤이 아니다.");
        window.Content = null;
        var host = new Grid();
        host.Children.Add(original);
        var overlay = new Canvas { IsHitTestVisible = false };
        host.Children.Add(overlay);
        window.Content = host;
        Pump(window);
        return overlay;
    }

    private static void Badge(Canvas overlay, Control target, int number, double dx = 0, double dy = 0)
    {
        const double diameter = 22;
        var origin = target.TranslatePoint(new Point(0, 0), overlay)
            ?? throw new InvalidOperationException($"번호 {number}: 컨트롤 위치를 알 수 없다.");
        var badge = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D93025")),
            BorderBrush = Avalonia.Media.Brushes.White,
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Foreground = Avalonia.Media.Brushes.White,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(badge, Math.Max(0, origin.X - diameter / 2 + dx));
        Canvas.SetTop(badge, Math.Max(0, origin.Y - diameter / 2 + dy));
        overlay.Children.Add(badge);
    }

    private static void SaveShell(Window window, Canvas overlay, string name, IReadOnlyList<Mark> callouts)
    {
        foreach (var callout in callouts)
        {
            Badge(overlay, callout.Target, callout.Number, callout.Dx, callout.Dy);
        }

        Pump(window);
        ManualCapture.Save(window, name);
    }

    private static async Task<(ManualShell Shell, SqlEditorViewModel First, Canvas Overlay)> OpenEditorShellAsync()
    {
        var shell = await ManualShell.OpenAsync();
        try
        {
            await shell.ShowOnlySampleSchemaAsync();
            var first = ManualShell.Host.NewDocument(MainSql);
            ManualShell.Host.NewDocument(OrdersSql);
            ManualShell.Host.Active = first;
            var window = shell.Harness.Window;
            window.Width = 1280;
            window.Height = 800;
            Pump(window);
            var overlay = AddOverlay(window);
            return (shell, first, overlay);
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    [Fact]
    public Task Sql_editor_main() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, first, overlay) = await OpenEditorShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            var view = ManualCapture.Find<SqlEditorView>(window);
            view.TextEditorControl.CaretOffset = first.Document.GetLineByNumber(3).Offset + 11; // 「줄 3, 열 12」
            Pump(window);
            var workspace = ManualCapture.Find<SqlWorkspaceView>(window);
            var tabItem = window.GetVisualDescendants().OfType<DocumentTabStripItem>().FirstOrDefault(t => t.IsSelected)
                ?? ManualCapture.Find<DocumentTabStripItem>(window);
            SaveShell(window, overlay, "sql-editor-main",
            [
                new Mark(tabItem, 1, 6),
                new Mark(ManualCapture.Find<ListBox>(workspace, l => l.Name == "SqlTabs"), 2),
                new Mark(ButtonNamed(workspace, "새 SQL 탭"), 3),
                new Mark(ManualCapture.Find<LineNumberMargin>(view), 4, 0, 100),
                new Mark(view.TextEditorControl.TextArea.TextView, 5),
                new Mark(ManualCapture.Find<EditorStatusView>(window), 6, 62),
            ]);
        }
    });

    [Fact]
    public Task Sql_editor_find() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, first, overlay) = await OpenEditorShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            first.OpenFind(replace: true);
            first.FindText = "dept_id";
            first.ReplaceText = "department_id";
            Pump(window);
            var view = ManualCapture.Find<SqlEditorView>(window);
            var panel = ManualCapture.Find<Border>(view, b => b.Name == "FindPanel");
            SaveShell(window, overlay, "sql-editor-find",
            [
                new Mark(ManualCapture.Find<TextBox>(panel, t => t.Name == "FindBox"), 1),
                new Mark(ManualCapture.Find<Button>(panel, b => b.Name == "PrevButton"), 2),
                new Mark(ManualCapture.Find<ToggleButton>(panel, t => t.Content as string == "Aa"), 3),
                new Mark(ManualCapture.Find<TextBlock>(panel, t => t.Text == first.FindStatus && first.FindStatus.Length > 0), 4, -6, -14),
                new Mark(ManualCapture.Find<Button>(panel, b => b.Name == "CloseFindButton"), 5),
                new Mark(ManualCapture.Find<TextBox>(panel, t => t.Name == "ReplaceBox"), 6),
                new Mark(ManualCapture.Find<Button>(panel, b => b.Name == "ReplaceButton"), 7),
            ]);
        }
    });

    [Fact]
    public Task Templates_panel() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "oranda-manual-tpl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new TemplatesViewModel(new AppDataPaths(root), new ShellText(language: "ko")) { Host = new SqlEditorHost() };
            await vm.LoadUserAsync();
            var view = new TemplatesView { DataContext = vm };
            var window = new Window { Width = 400, Height = 700, Content = view };
            window.Show();
            Pump(window);
            vm.SelectedRow = vm.Groups.SelectMany(g => g.Items).First();
            Pump(window);

            T Named<T>(string name)
                where T : Control => ManualCapture.Find<T>(view, c => c.Name == name);
            ManualCapture.Save(window, "templates-panel",
            [
                new(Named<TextBox>("SearchBox"), 1),
                new(Named<StackPanel>("KindBar"), 2),
                new(ManualCapture.Find<TreeViewItem>(view, i => i.DataContext is TemplateGroupRow), 3),
                new(ManualCapture.Find<TreeViewItem>(view, i => i.DataContext is TemplateItemRow), 4),
                new(Up<Border>(Named<TextBlock>("PreviewHeader")), 5),
                new(Named<SqlCodeView>("Preview"), 6),
                new(Up<Border>(Named<TextBlock>("FooterText")), 7),
            ]);
            window.Close();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    [Fact]
    public Task Formatter_profiles() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        using var shell = ManualShell.OpenOffline();
        var vm = new FormatterProfilesViewModel(shell.Harness.Services);
        await vm.LoadAsync();
        await vm.CopySelectedAsync(); // 내 프로필을 하나 만들어 이름 줄과 「내 프로필」 묶음이 보이게 한다
        var view = new FormatterProfilesView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 780, Content = view };
        window.Show();
        Pump(window);

        T Named<T>(string name)
            where T : Control => ManualCapture.Find<T>(view, c => c.Name == name);
        ManualCapture.Save(window, "formatter-profiles",
        [
            new(Up<Border>(Named<Button>("NewButton")), 1),
            new(Named<ListBox>("ProfileList"), 2),
            new(Named<StackPanel>("TitleLine"), 3),
            new(Named<ScrollViewer>("OptionsScroll"), 4),
            new(Up<StackPanel>(Named<ToggleButton>("SampleToggle")), 5),
            new(Up<Border>(Named<SqlCodeView>("PreviewBox")), 6),
            new(Up<Border>(Named<TextBlock>("StatusLine")), 7),
        ]);
        window.Close();
    });

    [Fact]
    public Task Settings_sql_format() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        using var shell = ManualShell.OpenOffline();
        // 키워드 맞춤은 절 배치가 「들여쓰기」일 때만 켜진다. 그 값을 쓰는 예시 프로필을 기본으로 둔다.
        await shell.Harness.Get<IFormatterProfileStore>().SaveAsync(
            new FormatterProfile { Id = "team-style", Name = "팀 서식", Options = new FormatterOptions { ClauseLayout = ClauseLayout.Indent } }, default);
        await shell.Harness.Get<SettingsService>().UpdateAsync(c => c with { FormatterProfileId = "team-style" }, default);
        var vm = new SettingsViewModel(shell.Harness.Get<SettingsService>(), new ShellText(language: "ko"));
        await vm.SqlFormat.Loaded;
        vm.SelectedTab = SettingsViewModel.FormatTabIndex;
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Pump(window);

        T Named<T>(string name)
            where T : Control => ManualCapture.Find<T>(window, c => c.Name == name);
        ManualCapture.Save(window, "settings-sql-format",
        [
            new(Named<Border>("FmtAlignGroup"), 1),
            new(Up<Border>(Named<RadioButton>("FmtCommaTrailing")), 2),
            new(Up<Border>(Named<RadioButton>("FmtCaseAsIs")), 3),
            new(Up<Border>(Named<RadioButton>("FmtBySingle")), 4),
            new(Named<Button>("FmtDefaultButton"), 5),
            new(Named<TextBlock>("FmtProfile"), 6),
            new(Named<Button>("FmtMoreButton"), 7),
            new(Up<Border>(Named<TextBlock>("FmtPreview")), 8),
        ]);
        window.Close();
    });
}
