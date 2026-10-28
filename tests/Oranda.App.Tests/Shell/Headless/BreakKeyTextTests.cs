using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// 실행 중지(Ctrl+Break) 단축키가 툴팁·메뉴에 「Ctrl+Cancel」 로 보이던 결함.
/// Windows 는 Ctrl+Break 를 VK_CANCEL(Avalonia <see cref="Key.Cancel"/>)로 보내므로 키맵 파일·키 입력은 <c>Cancel</c> 그대로 두고 표시만 「Break」 로 한다.
/// </summary>
public class BreakKeyTextTests
{
    private const string CancelId = "oranda.exec.cancel";

    [Theory]
    [InlineData(Key.Cancel, KeyModifiers.Control, "Ctrl+Break")]
    [InlineData(Key.Cancel, KeyModifiers.None, "Break")]
    [InlineData(Key.Pause, KeyModifiers.Control, "Ctrl+Pause")]
    [InlineData(Key.Escape, KeyModifiers.Shift, "Shift+Escape")]
    public void Display_names_the_break_key_like_people_do(Key key, KeyModifiers modifiers, string expected) =>
        Assert.Equal(expected, KeyBindingTable.Display(new KeyGesture(key, modifiers)));

    [Fact]
    public void Default_keymap_still_reads_Cancel_and_binds_the_same_key()
    {
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), macOS: false);

        Assert.Equal("Ctrl+Break", table.ShortcutText(CancelId));
        Assert.Equal(new KeyGesture(Key.Cancel, KeyModifiers.Control), table.SingleGesture(CancelId)); // 키 입력은 그대로
        Assert.Equal(new KeyGesture(Key.Cancel, KeyModifiers.Control), KeyChord.Parse("Primary+Cancel", macOS: false).Single());
    }

    [Fact]
    public void Mac_default_keeps_cmd_period()
    {
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), macOS: true);

        Assert.Equal(new KeyGesture(Key.OemPeriod, KeyModifiers.Meta), table.SingleGesture(CancelId));
        Assert.DoesNotContain("Cancel", table.ShortcutText(CancelId), StringComparison.Ordinal);
    }

    [Fact]
    public void Menu_gesture_converter_only_renames_the_break_key()
    {
        var converter = ShortcutGestureConverter.Instance;
        var ctrlS = new KeyGesture(Key.S, KeyModifiers.Control);
        var platform = new Avalonia.Controls.Converters.PlatformKeyGestureConverter();
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.EndsWith("Break", (string)converter.Convert(new KeyGesture(Key.Cancel, KeyModifiers.Control), typeof(string), null, culture)!, StringComparison.Ordinal);
        Assert.Equal(platform.Convert(ctrlS, typeof(string), null, culture), converter.Convert(ctrlS, typeof(string), null, culture));
        Assert.Equal(platform.Convert(null, typeof(string), null, culture), converter.Convert(null, typeof(string), null, culture));
    }

    [Fact]
    public Task Toolbar_tooltip_and_sql_menu_show_ctrl_break() => HeadlessHost.Run(() =>
    {
        if (OperatingSystem.IsMacOS())
        {
            return; // 맥은 ⌘. 이고 주 메뉴는 시스템 메뉴다.
        }

        using var h = new ShellHarness();
        Dispatcher.UIThread.RunJobs();

        var button = h.Window.Toolbar.Children.OfType<Button>().Single(b => b.Command is RegistryCommand { CommandId: CancelId });
        Assert.Equal("실행 중지 (Ctrl+Break)", ToolTip.GetTip(button));

        var sql = (MenuItem)h.Window.Menus.ItemsSource!.Cast<object>().ElementAt(MenuModelBuilder.TopMenus.ToList().IndexOf("sql"));
        sql.Open();
        Dispatcher.UIThread.RunJobs();
        var item = sql.ItemsSource!.Cast<object>().OfType<MenuItem>().Single(m => m.Command is RegistryCommand { CommandId: CancelId });
        Assert.Equal(new KeyGesture(Key.Cancel, KeyModifiers.Control), item.InputGesture); // 키는 그대로
        item.ApplyTemplate();
        Dispatcher.UIThread.RunJobs();
        var shown = item.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_InputGestureText");
        Assert.Equal("Ctrl+Break", shown.Text);
        sql.Close();
    });
}
