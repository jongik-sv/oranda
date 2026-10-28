using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

/// <summary>툴바 나눔 단추(ai-ui u1): <c>toolbar:main/&lt;부모&gt;/&lt;자식&gt;@n</c> 배치가 ▾ 자식 메뉴가 되고, 자식 없는 단추는 그대로다.</summary>
public class ToolbarSplitTests
{
    private static CommandDefinition Cmd(string id, params string[] placements) =>
        new() { Id = id, Title = "Cmd." + id, Category = CommandCategory.Sql, Placements = placements };

    [Fact]
    public void Children_attach_to_their_parent_and_are_not_top_level_buttons()
    {
        var nodes = MenuModelBuilder.BuildToolbar(
            [Cmd("tune", "toolbar:main/tune@10"), Cmd("tune.sel", "toolbar:main/tune/sel@10"), Cmd("tune.exp", "toolbar:main/tune/exp@20"), Cmd("plain", "toolbar:main/plain@110")],
            new ShellText(), _ => null);

        Assert.Equal(["tune", "plain"], nodes.Where(n => n.Kind == MenuNodeKind.Command).Select(n => n.CommandId!));
        Assert.Equal(["tune.sel", "tune.exp"], nodes.Single(n => n.CommandId == "tune").Children.Select(n => n.CommandId!));
        Assert.Empty(nodes.Single(n => n.CommandId == "plain").Children);
    }

    [Fact]
    public void Child_without_a_parent_button_is_dropped()
    {
        var nodes = MenuModelBuilder.BuildToolbar([Cmd("orphan", "toolbar:main/gone/orphan@10"), Cmd("a", "toolbar:main/a@20")], new ShellText(), _ => null);
        Assert.Equal(["a"], nodes.Select(n => n.CommandId!));
    }

    [Fact]
    public Task Down_arrow_opens_the_child_menu_and_the_button_keeps_the_toolbar_look() => HeadlessHost.Run(() =>
    {
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "자식" } } };
        var split = new ToolbarSplitButton(new TextBlock { Text = "AI" }, flyout) { Classes = { "toolbar" } };
        var window = new Window { Content = split };
        window.Show();
        split.Focus();

        Assert.IsAssignableFrom<Button>(split);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(flyout.IsOpen);
        window.Close();
    });
}
