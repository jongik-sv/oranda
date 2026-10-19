using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Oranda.App.Shell.Docking;
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
                items.Add(new Separator { Width = 1, Margin = new Avalonia.Thickness(4, 2) });
                continue;
            }

            var command = Track(n.CommandId!);
            var button = new Button
            {
                Content = n.Title,
                Command = command,
                MinWidth = 32,
                Padding = new Avalonia.Thickness(8, 3),
            };
            AutomationProperties.SetName(button, n.Title);
            ToolTip.SetTip(button, n.Shortcut is null ? n.Title : $"{n.Title} ({n.Shortcut})");
            items.Add(button);
        }

        return items;
    }

    public RegistryCommand Track(string commandId)
    {
        var c = new RegistryCommand(commandId, registry, keys);
        _commands.Add(c);
        return c;
    }

    private MenuItem MenuItemFor(MenuNode n, bool topLevel = false)
    {
        switch (n.Kind)
        {
            case MenuNodeKind.Submenu:
                {
                    var header = topLevel && AccessKeys.TryGetValue(n.Title, out var withKey) ? withKey : n.Title;
                    var item = new MenuItem { Header = header };
                    AutomationProperties.SetName(item, n.Title);
                    item.ItemsSource = n.Children.Select(c => c.Kind == MenuNodeKind.Separator ? (object)new Separator() : MenuItemFor(c)).ToList();
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
                    var command = Track(n.CommandId!);
                    var item = new MenuItem { Header = n.Title, Command = command, InputGesture = gesture(n.CommandId!) };
                    AutomationProperties.SetName(item, n.Title);
                    return item;
                }
        }
    }
}
