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

    private static string? ToolbarText(Button b) => b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
}
