using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Icons;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Menus;

/// <summary><see cref="MenuNode"/> 모델을 Avalonia 메뉴·툴바 컨트롤로 만든다.</summary>
public sealed class MenuControls(ICommandRegistry registry, IContextKeys keys, Func<string, KeyGesture?> gesture, DockWorkspace workspace)
{
    private static readonly Dictionary<string, string> AccessKeys = new(StringComparer.Ordinal)
    {
        ["파일"] = "파일(_F)",
        ["편집"] = "편집(_E)",
        ["보기"] = "보기(_V)",
        ["SQL"] = "_SQL",
        ["결과"] = "결과(_R)",
        ["데이터베이스"] = "데이터베이스(_D)",
        ["모니터"] = "모니터(_M)",
        ["도구"] = "도구(_T)",
        ["창"] = "창(_W)",
        ["도움말"] = "도움말(_H)",
    };

    private readonly List<RegistryCommand> _commands = [];

    /// <summary>문맥 키가 바뀌면 모든 메뉴·단추의 활성 상태를 다시 계산한다.</summary>
    public void RefreshEnablement()
    {
        foreach (var c in _commands)
        {
            c.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<Control> BuildMenuBar(IReadOnlyList<MenuNode> bar)
    {
        _commands.Clear();
        return [.. bar.Select(top => (Control)MenuItemFor(top, topLevel: true))];
    }

    /// <summary>툴바 단추와 구분선. 단추는 <see cref="Button"/>, 구분선은 <see cref="Separator"/>.</summary>
    public IReadOnlyList<Control> BuildToolbarItems(IReadOnlyList<MenuNode> nodes)
    {
        var items = new List<Control>();
        foreach (var n in nodes)
        {
            if (n.Kind == MenuNodeKind.Separator)
            {
                items.Add(new Separator { Classes = { "toolbar" } });
                continue;
            }

            var command = Track(n.CommandId!);

            // 아이콘이 있으면 아이콘만 그리고 이름은 툴팁으로 보인다(Orange·SQL Developer 툴바). 없으면 글자 단추.
            var icon = ShellIcons.Create(n.Icon);
            var button = new Button
            {
                Content = icon ?? (object)n.Title,
                Command = command,
                Classes = { "toolbar", icon is null ? "toolbar-text" : "toolbar-icon" },
                Tag = n.Icon, // 「…」 메뉴로 접힐 때 같은 아이콘을 다시 만든다(컨트롤은 한 부모만 갖는다).
            };
            AutomationProperties.SetName(button, n.Title);
            ToolTip.SetTip(button, n.Shortcut is null ? n.Title : $"{n.Title} ({Keymaps.KeyBindingTable.ForScreen(n.Shortcut)})");
            items.Add(button);
        }

        return items;
    }

    /// <summary>
    /// 오른쪽 클릭 메뉴 항목. 메뉴를 열 때마다 새로 만들므로 명령은 추적 목록에 넣지 않는다(활성 상태는 만들 때 계산된다).
    /// </summary>
    public IReadOnlyList<object> BuildContextItems(IReadOnlyList<MenuNode> nodes)
        => [.. nodes.Select(n => n.Kind == MenuNodeKind.Separator ? (object)new Separator() : MenuItemFor(n, track: false))];

    public RegistryCommand Track(string commandId)
    {
        var c = new RegistryCommand(commandId, registry, keys);
        _commands.Add(c);
        return c;
    }

    private MenuItem MenuItemFor(MenuNode n, bool topLevel = false, bool track = true)
    {
        switch (n.Kind)
        {
            case MenuNodeKind.Submenu:
                {
                    var header = topLevel && AccessKeys.TryGetValue(n.Title, out var withKey) ? withKey : n.Title;
                    var item = new MenuItem { Header = header };
                    AutomationProperties.SetName(item, n.Title);
                    item.ItemsSource = n.Children.Select(c => c.Kind == MenuNodeKind.Separator ? (object)new Separator() : MenuItemFor(c, track: track)).ToList();
                    return item;
                }

            case MenuNodeKind.PanelToggle:
                {
                    var id = n.PanelId!;
                    var item = new MenuItem
                    {
                        Header = n.Title,
                        ToggleType = MenuItemToggleType.CheckBox,
                        IsChecked = workspace.IsToolVisible(id),
                    };
                    item.Click += (_, _) => workspace.ToggleTool(id);
                    AutomationProperties.SetName(item, n.Title);
                    return item;
                }

            default:
                {
                    var command = track ? Track(n.CommandId!) : new RegistryCommand(n.CommandId!, registry, keys);
                    // 밑줄은 접근 키 표시로 읽히므로(「DBMS_OUTPUT」→「DBMSOUTPUT」) 두 번 써서 글자로 남긴다.
                    var item = new MenuItem { Header = n.Title.Replace("_", "__", StringComparison.Ordinal), Command = command, InputGesture = gesture(n.CommandId!), Icon = ShellIcons.Create(n.Icon) };
                    AutomationProperties.SetName(item, n.Title);
                    return item;
                }
        }
    }
}
