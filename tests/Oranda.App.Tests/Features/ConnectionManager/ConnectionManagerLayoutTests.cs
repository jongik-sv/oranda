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
public class ConnectionManagerLayoutTests
{
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
        Assert.Equal("New connection", ToolbarText(dialog.FindControl<Button>("AddButton")!));
        Assert.Empty(TextClipping.Find(dialog));
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
        Assert.Equal("Duplicate", ToolbarText(dialog.FindControl<Button>("DuplicateButton")!));
        Assert.Contains(dialog.FindControl<ProfileEditorView>("EditorView")!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "General");
        // 입력 칸 안내 글(PART_Placeholder)은 TextBox 가 칸 경계에서 잘라 그리므로 뺀다.
        // 헤드리스 글꼴은 글자마다 13px 고정폭이라 긴 영어 안내 글이 칸 밖까지 계산된다.
        var clipped = TextClipping.Find(dialog).Where(c => !c.Contains("PART_Placeholder", StringComparison.Ordinal)).ToList();
        Assert.True(clipped.Count == 0, string.Join(Environment.NewLine, clipped));
        var scroll = dialog.FindControl<ScrollViewer>("EditorScroll")!;
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, $"폼 높이 {scroll.Extent.Height} > 보이는 높이 {scroll.Viewport.Height}");
        dialog.Close();
    });

    private static string? ToolbarText(Button b) => b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
}
