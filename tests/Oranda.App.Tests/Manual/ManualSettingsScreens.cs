using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Templates;
using Oranda.App.Shell;
using Oranda.App.Shell.Palette;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 단축키·창 배치·설정 장 화면 캡처 생성기. DB 에 접속하지 않고 임시 데이터 폴더만 쓴다. 단축키는 화면에서만 바꾸고 저장하지 않는다.
/// 번호의 뜻은 <c>settings/shortcuts.md</c>, <c>settings/options.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualSettingsScreens
{
    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Named<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    [Fact]
    public Task Keymap_editor() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        using var shell = ManualShell.OpenOffline();
        var harness = shell.Harness;
        var vm = new KeymapEditorViewModel(
            harness.Get<ICommandRegistry>().Commands, new UserKeymapStore(harness.Services.GetService<IAppDataPaths>()!), harness.Get<SettingsService>(), null, OperatingSystem.IsMacOS())
        {
            Filter = "plan",
        };
        vm.SelectedRow = vm.Rows.First(r => r.Keys.Length > 0);
        vm.NewKeys = "Primary+Enter"; // 「현재 문장 실행」 과 겹치는 키. 저장하지 않고 충돌 안내만 보인다.
        Assert.True(vm.Assign(), vm.Warning);
        Assert.True(vm.HasWarning, "충돌 안내가 나오지 않았다.");
        var window = new KeymapEditorWindow { DataContext = vm };
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();

        ManualCapture.Save(window, "keymap-editor",
        [
            new(ManualCapture.Find<ComboBox>(window), 1),
            new(ManualCapture.Find<TextBox>(window, t => t.PlaceholderText == "명령·키 찾기"), 2, 725, 14),
            new(ManualCapture.Find<ListBox>(window), 3, 660, 16),
            new(ManualCapture.Find<TextBox>(window, t => t.PlaceholderText?.StartsWith("새 키", StringComparison.Ordinal) == true), 4, 440, 14),
            new(ManualCapture.Find<TextBlock>(window, t => t.Text == vm.Warning), 5, 700, 8),
        ]);
        window.Close();
    });

    [Fact]
    public Task Command_palette() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        using var shell = ManualShell.OpenOffline();
        var harness = shell.Harness;
        var vm = new CommandPaletteViewModel(
            harness.Get<ICommandRegistry>(), harness.Get<IContextKeys>(), harness.Get<IShellText>(), harness.Get<ShellController>().Keymap.ShortcutText)
        {
            Query = "실행",
        };
        var window = new CommandPaletteWindow { DataContext = vm };
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        var overlay = ManualCapture.AddOverlay(window); // 결과 줄은 내용을 옮겨 붙이면 새로 만들어진다
        Pump();

        var shortcut = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Margin.Left == 12 && !string.IsNullOrEmpty(t.Text));
        ManualCapture.SaveOverlaid(window, overlay, "command-palette",
        [
            new(Named<TextBox>(window, "QueryBox"), 1, 590, 14),
            new(Named<ListBox>(window, "ResultList"), 2, 300, 24),
            new(shortcut, 3, -22, 8),
        ]);
        window.Close();
    });

    [Fact]
    public Task Layout_name() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var box = new TextBox { Text = "내 레이아웃", MinWidth = 280 };
        var window = AvaloniaEditorDialogs.BuildDialog("레이아웃 이름", box, null, "", () => { });
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        ManualCapture.Save(window, "layout-name");
        window.Close();
    });

    private static SettingsWindow OpenSettings(int tab)
    {
        var settings = new SettingsService(new MemorySettingsStore());
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko")) { SelectedTab = tab };
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(20); // 탭·펼침 전환 애니메이션을 끝낸다
            Pump();
            window.UpdateLayout();
        }

        return window;
    }

    [Fact]
    public Task Settings_features() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var window = OpenSettings(SettingsViewModel.FeaturesTabIndex);
        ManualCapture.Save(window, "settings-features",
        [
            new(Named<ItemsControl>(window, "PrimaryList"), 1),
            new(Named<CheckBox>(window, "PlsqlAddonBox"), 2),
            new(Named<Expander>(window, "AdvancedExpander"), 3),
        ]);
        window.Close();
    });

    [Fact]
    public Task Settings_execution() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var window = OpenSettings(4);
        ManualCapture.Save(window, "settings-execution",
        [
            new(ManualCapture.FindByText(window, "트랜잭션"), 1, -14),
            new(ManualCapture.FindByText(window, "DBMS_OUTPUT"), 2, -14),
            new(ManualCapture.FindByText(window, "스크립트 오류 시 (기본값)"), 3, -14),
            new(ManualCapture.FindByText(window, "문장 나누기"), 4, -14),
            new(ManualCapture.FindByText(window, "실제 계획"), 5, -14),
        ]);
        window.Close();
    });

    [Fact]
    public Task Settings_grid() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var window = OpenSettings(SettingsViewModel.GridTabIndex);
        ManualCapture.Save(window, "settings-grid",
        [
            new(ManualCapture.FindByText(window, "한 번에 가져올 행"), 1, -14),
            new(ManualCapture.FindByText(window, "메모리 한도(MB)"), 2, -14),
            new(ManualCapture.FindByText(window, "LOB 미리 읽기(바이트)"), 3, -14),
            new(ManualCapture.FindByText(window, "결과 그리드 글꼴"), 4, -14),
            new(ManualCapture.FindByText(window, "결과 그리드 글자 크기"), 5, -14),
            new(ManualCapture.FindByText(window, "변경 추적하기"), 6, -14),
        ]);
        window.Close();
    });
}
