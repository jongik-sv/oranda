using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Features.Sessions;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>
/// TSK-14-12: 접속 관리자·접속 끊기가 세션(접속) 콤보 바로 오른쪽 첫 두 단추이고, 콤보는 긴 세션 이름(「새 세션(첫 실행 때) — 사용자@DB」,
/// 「번호 - 사용자@DB (2)」)이 잘리지 않을 만큼 넓다. 넓힌 콤보로도 첫 실행 창(최대 1280)에서 툴바가 넘치지 않는다.
/// Headless 글자는 글자마다 글꼴 크기만큼(14 DIP) 차지해 실제 글꼴(영문 약 7~8 DIP)보다 넓다: 여기서 안 잘리면 실제로도 안 잘린다.
/// </summary>
public sealed class ToolbarConnectionPlacementTests
{
    private static readonly string[] ConnectionButtons = ["oranda.conn.manage", "oranda.conn.disconnect"];

    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    // ---- 순서 ----------------------------------------------------------------------------------

    [Fact]
    public void Toolbar_model_starts_with_connection_manager_then_disconnect_in_their_own_group()
    {
        var toolbar = MenuModelBuilder.BuildToolbar(AppCommands, new ShellText(language: "ko"), _ => null);
        Assert.Equal(ConnectionButtons, toolbar.Take(2).Select(n => n.CommandId));
        Assert.Equal(MenuNodeKind.Separator, toolbar[2].Kind); // 그 뒤 새 SQL·열기·저장 묶음과는 구분선으로 나뉜다
        Assert.Equal("oranda.file.newSqlEditor", toolbar[3].CommandId);
    }

    [Fact]
    public Task Main_window_puts_connection_manager_and_disconnect_right_after_the_session_combo() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        Dispatcher.UIThread.RunJobs();
        var first = h.Window.Toolbar.Children.Take(3).ToList();
        Assert.Equal(ConnectionButtons, first.Take(2).Select(c => ((RegistryCommand)((Button)c).Command!).CommandId));
        Assert.IsType<Separator>(first[2]);
        var combo = h.Window.FindControl<ContentControl>("ConnectionComboHost")!;
        Assert.True(combo.Bounds.Right <= first[0].TranslatePoint(default, h.Window)!.Value.X + 0.5); // 콤보 바로 오른쪽
    });

    // ---- 콤보 폭 -------------------------------------------------------------------------------

    [Fact]
    public Task Session_combo_is_wider_than_before() => HeadlessHost.Run(() =>
    {
        var view = new ConnectionComboView();
        Assert.True(view.Combo.MinWidth >= ConnectionComboView.MinComboWidth);
        Assert.True(ConnectionComboView.MinComboWidth >= 240);
        Assert.True(view.Combo.MaxWidth >= 460);
    });

    [Fact]
    public Task A_long_extra_session_label_is_not_cut() => HeadlessHost.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        var p = await rig.ConnectAsync("hr_dev_main");
        var s2 = await rig.OpenExtraAsync(p);
        rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tab));
        var vm = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        await vm.ChooseAsync(vm.Items.Single(i => ReferenceEquals(i.Session, s2) && !i.IsNewSession));
        SessionsViewModelTests.Pump();
        var label = vm.Selected!.Label;
        Assert.EndsWith("(2)", label, StringComparison.Ordinal); // 「2 - scott@hr_dev_main (2)」 류
        await AssertNotCutAsync(vm, label, checkDropDown: true);
    });

    [Fact]
    public Task A_waiting_new_session_placeholder_is_not_cut() => HeadlessHost.Run(async () =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        await rig.ConnectAsync("hr_dev");
        rig.Host.EnsureWorkspace();
        Assert.True(await rig.Router.PrepareAsync(rig.Host.NewDocument()));
        var b = rig.Host.NewWorkspace();
        rig.Host.Active = b.Tabs[0];
        SessionsViewModelTests.Pump();
        var vm = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        Assert.Null(vm.Selected);
        Assert.Contains("첫 실행", vm.PlaceholderText, StringComparison.Ordinal);
        await AssertNotCutAsync(vm, vm.PlaceholderText!);
    });

    /// <summary>
    /// 콤보를 첫 실행 창 폭(1280) 창에 놓고 보이는 글(고른 줄 또는 자리 글)이 줄임표 없이 다 들어가는지 본다.
    /// <paramref name="checkDropDown"/> 이면 펼친 목록도 콤보보다 좁지 않고 줄마다 글이 다 들어가는지 본다.
    /// </summary>
    private static async Task AssertNotCutAsync(ConnectionComboViewModel vm, string text, bool checkDropDown = false)
    {
        var view = new ConnectionComboView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 200, Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { view } } };
        window.Show();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                SessionsViewModelTests.Pump();
                await Task.Delay(10);
            }

            window.UpdateLayout();
            var shown = view.Combo.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text && t.IsEffectivelyVisible);
            var full = new TextBlock { Text = text, FontSize = shown.FontSize, FontFamily = shown.FontFamily, FontStyle = shown.FontStyle, FontWeight = shown.FontWeight };
            full.Measure(Size.Infinity);
            Assert.True(shown.Bounds.Width + 0.5 >= full.DesiredSize.Width, $"「{text}」 이 {shown.Bounds.Width:0} 칸에 잘린다(필요 {full.DesiredSize.Width:0}, 콤보 {view.Combo.Bounds.Width:0})");

            // 글 상자 자체가 줄지 않아도 콤보 테두리·펼침 화살표에 가려지면 잘린 것이다.
            var right = shown.TranslatePoint(new Point(full.DesiredSize.Width, 0), view.Combo)!.Value.X;
            var glyph = view.Combo.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "DropDownGlyph");
            var limit = glyph?.TranslatePoint(default, view.Combo)?.X ?? view.Combo.Bounds.Width;
            Assert.True(right <= limit + 0.5, $"「{text}」 끝({right:0})이 펼침 화살표({limit:0}) 밑으로 들어간다(콤보 {view.Combo.Bounds.Width:0})");

            if (checkDropDown)
            {
                view.Combo.IsDropDownOpen = true;
                for (var i = 0; i < 5; i++)
                {
                    SessionsViewModelTests.Pump();
                    await Task.Delay(10);
                }

                var rows = view.Combo.GetRealizedContainers().OfType<ComboBoxItem>().ToList();
                Assert.Equal(vm.Entries.Count, rows.Count);
                foreach (var row in rows)
                {
                    var item = (ConnectionComboItem)row.Content!;
                    var line = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == item.Label);
                    var need = new TextBlock { Text = item.Label, FontSize = line.FontSize, FontFamily = line.FontFamily, FontStyle = line.FontStyle, FontWeight = line.FontWeight };
                    need.Measure(Size.Infinity);
                    Assert.True(row.Bounds.Width + 2.5 >= view.Combo.Bounds.Width, $"펼친 목록(테두리 1 DIP 씩 안쪽)({row.Bounds.Width:0})이 콤보({view.Combo.Bounds.Width:0})보다 좁다");
                    Assert.True(line.Bounds.Width + 0.5 >= need.DesiredSize.Width, $"펼친 목록의 「{item.Label}」 이 {line.Bounds.Width:0} 칸에 잘린다(필요 {need.DesiredSize.Width:0})");
                }

                view.Combo.IsDropDownOpen = false;
                SessionsViewModelTests.Pump();
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ---- 넘침 ---------------------------------------------------------------------------------

    [Fact]
    public Task The_widest_combo_still_leaves_every_toolbar_button_visible_in_the_first_run_window() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        h.Window.Width = 1280; // WindowPlacementPolicy.FirstRunMaxWidth
        var combo = ((ConnectionComboView)h.Window.FindControl<ContentControl>("ConnectionComboHost")!.Content!).Combo;
        combo.Width = combo.MaxWidth; // 이름이 아주 길어 상한까지 늘어난 콤보
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();
        h.Window.UpdateToolbarOverflow();
        Assert.False(h.Window.FindControl<Button>("OverflowButton")!.IsVisible);
        Assert.All(h.Window.Toolbar.Children.Where(c => !c.Classes.Contains(MenuControls.ToolbarOutOfScopeClass)), c => Assert.True(c.IsVisible));
    });

    [Fact]
    public Task A_narrow_window_folds_buttons_from_the_right_and_keeps_the_connection_buttons_next_to_the_combo() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        h.Window.Width = 960; // 주 창 최소 폭
        var combo = ((ConnectionComboView)h.Window.FindControl<ContentControl>("ConnectionComboHost")!.Content!).Combo;
        combo.Width = combo.MaxWidth;
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();
        h.Window.UpdateToolbarOverflow();
        var overflow = h.Window.FindControl<Button>("OverflowButton")!;
        Assert.True(overflow.IsVisible);
        var visible = h.Window.Toolbar.Children.OfType<Button>().Where(b => b.IsVisible).Select(b => ((RegistryCommand)b.Command!).CommandId).ToList();
        Assert.Equal(ConnectionButtons, visible.Take(2));
        var folded = ((MenuFlyout)overflow.Flyout!).Items.OfType<MenuItem>().Select(m => ((RegistryCommand)m.Command!).CommandId).ToList();
        Assert.Equal("oranda.tools.settings", folded[^1]); // 맨 오른쪽부터 접는다
        Assert.DoesNotContain(folded, ConnectionButtons.Contains);
    });
}
