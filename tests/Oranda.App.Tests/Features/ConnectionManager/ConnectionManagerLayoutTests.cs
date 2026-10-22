using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>접속 관리자 배치: 영어(긴 글자)에서도 툴바 단추 글자가 잘리지 않는다.</summary>
[Collection("UiLanguage")]
public sealed class ConnectionManagerLayoutTests : IDisposable
{
    // ShellController 가 만들어질 때 프로세스 전역 UiText.Current 를 그 셸(여기서는 영어)로 채우고 되돌리지 않으므로,
    // 이 묶음이 끝난 뒤 한국어 글자를 단정하는 다른 시험(SaveFeedback 등)이 영어 문구를 받지 않게 원래 값으로 되돌린다.
    private readonly IShellText? _previousText = UiText.Current;

    public void Dispose() => UiText.Current = _previousText;

    [Fact]
    public Task English_toolbar_buttons_are_not_clipped() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await h.Get<SettingsService>().UpdateAsync(s => s with { Ui = s.Ui with { Language = "en" } });
        await HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == "en");
        Dispatcher.UIThread.RunJobs();

        using var fx = new ServiceFixture();
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        var dialog = new ConnectionManagerWindow { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        // 목록 툴바는 아이콘만 그리고 이름은 도움말·낭독기 이름으로 옮긴다(영어로 바뀐다).
        var add = dialog.FindControl<Button>("AddButton")!;
        Assert.Equal("New connection", Avalonia.Automation.AutomationProperties.GetName(add));
        Assert.Equal("New connection", ToolTip.GetTip(add));
        Assert.Empty(TextClipping.Find(dialog));
        dialog.Close();
    });

    [Fact]
    public Task List_toolbar_buttons_stay_on_one_row_at_default_and_minimum_width() => HeadlessHost.Run(async () =>
    {
        // 사용자 지적(2026-10-22): 새 접속·새 폴더·복제… 단추가 두 줄로 내려간다.
        using var fx = new ServiceFixture();
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        var dialog = new ConnectionManagerWindow { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var width in new[] { dialog.Width, dialog.MinWidth })
        {
            dialog.Width = width;
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();

            var toolbar = dialog.FindControl<Panel>("ListToolbar")!;
            var buttons = toolbar.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
            Assert.True(buttons.Count >= 5, $"단추 {buttons.Count}개");
            var tops = buttons.Select(b => Math.Round(b.TranslatePoint(new Point(0, 0), dialog)!.Value.Y)).Distinct().ToList();
            Assert.True(tops.Count == 1, $"폭 {width}: 단추 줄 {tops.Count}개");

            var tree = dialog.FindControl<TreeView>("ConnectionTree")!;
            var listRight = tree.TranslatePoint(new Point(tree.Bounds.Width, 0), dialog)!.Value.X;
            var last = buttons[^1];
            var lastRight = last.TranslatePoint(new Point(last.Bounds.Width, 0), dialog)!.Value.X;
            Assert.True(lastRight <= listRight + 0.5, $"폭 {width}: 마지막 단추 오른쪽 {lastRight} > 목록 오른쪽 {listRight}");
            Assert.All(buttons, b => Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))));
        }

        dialog.Close();
    });

    [Fact]
    public Task English_editor_with_sections_and_preview_is_not_clipped_and_needs_no_scroll() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await h.Get<SettingsService>().UpdateAsync(s => s with { Ui = s.Ui with { Language = "en" } });
        await HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == "en");
        Dispatcher.UIThread.RunJobs();

        using var fx = new ServiceFixture();
        await fx.Service.SaveProfileAsync(new Oranda.Core.Connection.ConnectionProfile("ft-dev", "ft", "localhost:1522/FREEPDB1", Group: "dev"), "pw", true, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        vm.Selected = vm.Tree[0].Children[0];
        var dialog = new ConnectionManagerWindow { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();

        Assert.Equal("Test", ToolbarText(dialog.FindControl<Button>("TestButton")!));
        Assert.Equal("Duplicate", Avalonia.Automation.AutomationProperties.GetName(dialog.FindControl<Button>("DuplicateButton")!));
        Assert.Contains(dialog.FindControl<ProfileEditorView>("EditorView")!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "General");
        // 입력 칸 안내 글(PART_Placeholder)은 TextBox 가 칸 경계에서 잘라 그리므로 뺀다.
        // 헤드리스 글꼴은 글자마다 13px 고정폭이라 긴 영어 안내 글이 칸 밖까지 계산된다.
        var clipped = TextClipping.Find(dialog).Where(c => !c.Contains("PART_Placeholder", StringComparison.Ordinal)).ToList();
        Assert.True(clipped.Count == 0, string.Join(Environment.NewLine, clipped));
        var scroll = dialog.FindControl<ScrollViewer>("EditorScroll")!;
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, $"폼 높이 {scroll.Extent.Height} > 보이는 높이 {scroll.Viewport.Height}");
        dialog.Close();
    });

    [Fact]
    public Task Long_connect_strings_are_trimmed_and_the_last_connected_time_stays_inside_the_list() => HeadlessHost.Run(async () =>
    {
        // 줄이 목록 폭을 넘어 마지막 접속 시각이 오른쪽 밖으로 잘려 「0」 만 보이던 결함(실제 창에서 발견).
        using var h = new ShellHarness();
        using var fx = new ServiceFixture();
        var host = "very-long-database-host-name.internal.example.com:1521/SOME_REALLY_LONG_SERVICE_NAME";
        await fx.Service.SaveProfileAsync(
            new Oranda.Core.Connection.ConnectionProfile("ft-dba", "system", host, LastConnectedAt: DateTimeOffset.UtcNow.AddDays(-400)),
            "pw", true, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        var dialog = new ConnectionManagerWindow { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();

        var tree = dialog.FindControl<TreeView>("ConnectionTree")!;
        var node = vm.AllNodes().Single(n => n.Title == "ft-dba");
        var time = tree.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == node.LastConnectedText && !string.IsNullOrEmpty(t.Text));
        var right = time.TranslatePoint(new Point(time.Bounds.Width, 0), tree)!.Value.X;
        Assert.True(right <= tree.Bounds.Width + 0.5, $"마지막 접속 시각 오른쪽 끝 {right} > 목록 폭 {tree.Bounds.Width}");
        dialog.Close();
    });

    private static string? ToolbarText(Button b) => b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
}
