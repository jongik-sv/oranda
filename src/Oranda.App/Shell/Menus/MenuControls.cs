using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Menus;

/// <summary><see cref="MenuNode"/> 모델을 Avalonia 메뉴·툴바 컨트롤로 만든다.</summary>
public sealed class MenuControls(ICommandRegistry registry, IContextKeys keys, Func<string, KeyGesture?> gesture, DockWorkspace workspace,
    Func<object?>? focused = null, Func<KeyGesture, string?>? resolve = null, KeyDispatcher? dispatcher = null)
{
    private readonly List<RegistryCommand> _commands = [];
    private readonly List<(NativeMenuItem Item, string PanelId)> _nativePanels = [];
    private readonly List<(NativeMenuItem Item, string CommandId, KeyGesture Gesture)> _nativeGestures = [];

    /// <summary>문맥 키가 바뀌면 모든 메뉴·단추의 활성 상태를 다시 계산한다.</summary>
    public void RefreshEnablement()
    {
        foreach (var c in _commands)
        {
            c.RaiseCanExecuteChanged();
        }

        foreach (var (item, panelId) in _nativePanels)
        {
            item.IsChecked = workspace.IsToolVisible(panelId);
        }

        // 단축키는 지금 문맥에서 키맵이 이 명령으로 보낼 때만 메뉴가 가져간다(그리드에서 ⌘C 를 편집기 복사가 가로채지 않게).
        foreach (var (item, commandId, key) in _nativeGestures)
        {
            var mine = resolve is null || string.Equals(resolve(key), commandId, StringComparison.Ordinal);
            if (mine != (item.Gesture is not null))
            {
                item.Gesture = mine ? key : null;
            }
        }
    }

    public IReadOnlyList<Control> BuildMenuBar(IReadOnlyList<MenuNode> bar)
    {
        _commands.Clear();
        return [.. bar.Select(top => (Control)MenuItemFor(top, topLevel: true))];
    }

    /// <summary>
    /// macOS 시스템 메뉴 막대의 뿌리 메뉴. 창에 한 번만 붙이고 <see cref="FillNativeMenu"/> 로 항목만 갈아 끼운다
    /// (Avalonia 가 창에 처음 붙은 <see cref="NativeMenu"/> 인스턴스가 아닌 메뉴로는 갱신을 거부한다).
    /// 메뉴를 열거나 단축키를 찾을 때(<see cref="NativeMenu.NeedsUpdate"/>) 활성·체크·단축키 상태를 다시 계산한다.
    /// </summary>
    public NativeMenu CreateNativeRoot()
    {
        var root = new NativeMenu();
        root.NeedsUpdate += (_, _) => RefreshEnablement();
        return root;
    }

    /// <summary>
    /// <see cref="BuildMenuBar"/> 와 같은 <see cref="MenuNode"/> 모델로 <paramref name="root"/> 의 항목을 다시 채운다.
    /// 접근 키 표기는 쓰지 않고(「파일」), 단축키는 <see cref="NativeMenuItem.Gesture"/> 로 보이며 시스템이 같은 키를 먼저 실행한다.
    /// </summary>
    public void FillNativeMenu(NativeMenu root, IReadOnlyList<MenuNode> bar)
    {
        _commands.Clear();
        _nativePanels.Clear();
        _nativeGestures.Clear();
        root.Items.Clear();
        foreach (var n in bar)
        {
            root.Add(NativeItemFor(n));
        }
    }

    private NativeMenu NativeLevel(IEnumerable<MenuNode> nodes)
    {
        var menu = new NativeMenu();
        menu.NeedsUpdate += (_, _) => RefreshEnablement();
        foreach (var n in nodes)
        {
            menu.Add(NativeItemFor(n));
        }

        return menu;
    }

    private NativeMenuItemBase NativeItemFor(MenuNode n)
    {
        switch (n.Kind)
        {
            case MenuNodeKind.Separator:
                return new NativeMenuItemSeparator();

            case MenuNodeKind.Submenu:
                return new NativeMenuItem(n.Title) { Menu = NativeLevel(n.Children) };

            case MenuNodeKind.PanelToggle:
                {
                    var id = n.PanelId!;
                    var item = new NativeMenuItem(n.Title)
                    {
                        ToggleType = MenuItemToggleType.CheckBox,
                        IsChecked = workspace.IsToolVisible(id),
                    };
                    item.Click += (_, _) =>
                    {
                        workspace.ToggleTool(id);
                        item.IsChecked = workspace.IsToolVisible(id);
                    };
                    _nativePanels.Add((item, id));
                    return item;
                }

            default:
                {
                    var key = gesture(n.CommandId!);
                    var item = new NativeMenuItem(n.Title.Replace("_", "__", StringComparison.Ordinal))
                    {
                        Command = new NativeMenuCommand(Track(n.CommandId!), key, focused ?? (() => null), dispatcher),
                        Gesture = key,
                    };
                    if (key is not null)
                    {
                        _nativeGestures.Add((item, n.CommandId!, key));
                    }

                    return item;
                }
        }
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
                    var header = topLevel ? n.AccessHeader ?? n.Title : n.Title;
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
