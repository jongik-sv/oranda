using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Oranda.App.Features.SqlEditor;
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

    /// <summary>숨김·이유 계산에 쓰는 문맥 키(PL/SQL 탭 여부를 도킹에서 채운다, <see cref="MenuScopeKeys"/>).</summary>
    private readonly IContextKeys _menuKeys = new MenuScopeKeys(keys, () => workspace.ActiveDocument?.Panel
        is Oranda.App.Features.PlsqlEditor.PlsqlEditorViewModel or Oranda.App.Features.PlsqlWorkspace.PlsqlWorkspaceViewModel { ActiveTab: not null });

    /// <summary>시스템 메뉴 뿌리(창)마다 따로 갖는 추적 목록. 창이 여럿(메인 창·떠 있는 도킹 창)이어도 각자 자기 메뉴 인스턴스를 갖는다.</summary>
    private readonly Dictionary<NativeMenu, NativeSet> _natives = [];

    /// <summary>창 안 메뉴 막대의 최상위 메뉴마다 묶은 컨트롤(숨김·이유 툴팁·빈 메뉴 안내를 다시 계산할 때 쓴다).</summary>
    private readonly List<Bound> _bar = [];

    private NativeSet? _building;

    /// <summary>툴바 모델 한 마디와 그것을 그린 단추·구분선(<see cref="BuildToolbarItems"/>).</summary>
    private readonly List<(MenuNode Node, Control Control)> _toolbar = [];

    /// <summary>명령 <c>When</c> 으로 숨은 툴바 단추·구분선에 다는 클래스. 넘침 계산은 이 컨트롤을 세지 않는다.</summary>
    public const string ToolbarOutOfScopeClass = "toolbar-out-of-scope";

    /// <summary>툴바 단추·구분선의 <c>When</c> 숨김이 바뀌었다(주 창이 넘침 「…」 을 다시 계산한다).</summary>
    public event EventHandler? ToolbarScopeChanged;

    /// <summary>
    /// 메뉴 모델 한 마디와 그것을 그린 컨트롤. <see cref="Children"/> 은 <see cref="MenuNode.Children"/> 과 같은 차례다.
    /// 상태는 <see cref="MenuVisibility.Evaluate"/> 로 계산해 그대로 옮긴다.
    /// </summary>
    private sealed class Bound(MenuNode node, Action<bool> setVisible, Action<string?> setTip)
    {
        public MenuNode Node { get; } = node;

        public Action<bool> SetVisible { get; } = setVisible;

        public Action<string?> SetTip { get; } = setTip;

        public List<Bound> Children { get; } = [];

        /// <summary>최상위 메뉴만: 빈 메뉴 안내 줄 보이기.</summary>
        public Action<bool>? SetEmpty { get; set; }

        public static Bound Top(MenuNode node) => new(node, _ => { }, _ => { });
    }

    private sealed class NativeSet(Func<object?> focused)
    {
        public Func<object?> Focused { get; } = focused;

        public List<RegistryCommand> Commands { get; } = [];

        public List<(NativeMenuItem Item, string PanelId)> Panels { get; } = [];

        public List<(NativeMenuItem Item, string CommandId, KeyGesture Gesture)> Gestures { get; } = [];

        public List<Bound> Tops { get; } = [];

        /// <summary>지금 열려 있는 최상위 메뉴 수(<see cref="NativeMenu.Opening"/>·<see cref="NativeMenu.Closed"/>). 0 보다 크면 단축키를 모두 보인다.</summary>
        public int Open { get; set; }

        public void Clear()
        {
            Open = 0; // 갈아 끼운 옛 메뉴의 닫힘은 오지 않을 수 있다: 키를 계속 쥐지 않게 닫힌 상태로 돌린다
            Tops.Clear();
            Commands.Clear();
            Panels.Clear();
            Gestures.Clear();
        }
    }

    /// <summary>문맥 키가 바뀌면 모든 메뉴·단추의 활성 상태와 주 메뉴의 숨김·이유 툴팁을 다시 계산한다.</summary>
    public void RefreshEnablement()
    {
        foreach (var c in _commands)
        {
            c.RaiseCanExecuteChanged();
        }

        foreach (var top in _bar)
        {
            ApplyState(top);
        }

        ApplyToolbarState();

        foreach (var set in _natives.Values)
        {
            foreach (var c in set.Commands)
            {
                c.RaiseCanExecuteChanged();
            }

            foreach (var top in set.Tops)
            {
                ApplyState(top);
            }

            foreach (var (item, panelId) in set.Panels)
            {
                item.IsChecked = workspace.IsToolVisible(panelId);
            }

            // 단축키는 지금 문맥에서 키맵이 이 명령으로 보낼 때만 메뉴가 가져간다(그리드에서 ⌘C 를 편집기 복사가 가로채지 않게).
            // Gesture 는 표시도 맡으므로 메뉴가 열려 있는 동안(키를 찾지 않을 때)은 창 안 메뉴처럼 모두 보인다.
            foreach (var (item, commandId, key) in set.Gestures)
            {
                var mine = set.Open > 0 || resolve is null || string.Equals(resolve(key), commandId, StringComparison.Ordinal);
                if (mine != (item.Gesture is not null))
                {
                    item.Gesture = mine ? key : null;
                }
            }
        }
    }

    /// <summary>
    /// 창 안 메뉴 막대. 주 메뉴 규칙(<see cref="MenuVisibility"/>)으로 지금 탭·패널에 맞지 않는 명령은 숨기고,
    /// 회색 명령에는 이유 툴팁을 단다. 상태는 <see cref="RefreshEnablement"/> 때마다 다시 계산한다.
    /// </summary>
    public IReadOnlyList<Control> BuildMenuBar(IReadOnlyList<MenuNode> bar)
    {
        _commands.Clear();
        _bar.Clear();
        var result = new List<Control>();
        foreach (var top in bar)
        {
            var item = new MenuItem { Header = top.AccessHeader ?? top.Title };
            AutomationProperties.SetName(item, top.Title);
            var bound = Bound.Top(top);
            var children = BarLevel(top.Children, bound);
            if (top.EmptyText is { } emptyText)
            {
                var empty = new MenuItem { Header = emptyText, IsEnabled = false, IsVisible = false, Classes = { "menu-empty" } };
                AutomationProperties.SetName(empty, emptyText);
                children.Add(empty);
                bound.SetEmpty = v => empty.IsVisible = v;
            }

            item.ItemsSource = children;

            // 문맥 키가 그대로여도 보이는 문서가 바뀌었을 수 있다(PL/SQL 탭 등): 메뉴를 열 때 한 번 더 맞춘다.
            item.SubmenuOpened += (_, _) => ApplyState(bound);
            _bar.Add(bound);
            ApplyState(bound);
            result.Add(item);
        }

        return result;
    }

    private List<object> BarLevel(IReadOnlyList<MenuNode> nodes, Bound owner)
    {
        var list = new List<object>(nodes.Count);
        foreach (var n in nodes)
        {
            Control control;
            Bound bound;
            switch (n.Kind)
            {
                case MenuNodeKind.Separator:
                    {
                        var separator = new Separator();
                        control = separator;
                        bound = new Bound(n, v => separator.IsVisible = v, _ => { });
                        break;
                    }

                case MenuNodeKind.Submenu:
                    {
                        var sub = new MenuItem { Header = n.Title };
                        AutomationProperties.SetName(sub, n.Title);
                        control = sub;
                        bound = new Bound(n, v => sub.IsVisible = v, _ => { });
                        var items = new System.Collections.ObjectModel.ObservableCollection<object>(BarLevel(n.Children, bound));
                        sub.ItemsSource = items;
                        if (n.Key is { } key)
                        {
                            // 바뀌는 줄(최근 파일 등)은 열 때마다 공급자에게서 다시 받아 명령 줄 앞에 둔다. 숨김·이유 계산(Bound)에는 넣지 않는다.
                            var added = 0;
                            void Fill() => added = FillSource(items, added, key);
                            Fill();
                            sub.SubmenuOpened += (_, _) => Fill();
                        }

                        break;
                    }

                default:
                    {
                        var item = MenuItemFor(n);
                        control = item;
                        if (n.Kind == MenuNodeKind.Command)
                        {
                            ToolTip.SetShowOnDisabled(item, true);
                        }

                        bound = new Bound(n, v => item.IsVisible = v, tip => SetTip(item, tip));
                        break;
                    }
            }

            owner.Children.Add(bound);
            list.Add(control);
        }

        return list;
    }

    /// <summary>
    /// 공급자(<see cref="MenuItemSources"/>)의 줄로 <paramref name="items"/> 앞쪽 <paramref name="added"/> 개를 바꾼다. 줄이 있으면 명령 줄과의 사이에 구분선을 둔다.
    /// 돌려준 수 = 이번에 넣은 개수(구분선 포함).
    /// </summary>
    private static int FillSource(System.Collections.ObjectModel.ObservableCollection<object> items, int added, string key)
    {
        for (var i = 0; i < added; i++)
        {
            items.RemoveAt(0);
        }

        if (MenuItemSources.Find(key)?.Items() is not { Count: > 0 } lines)
        {
            return 0;
        }

        var at = 0;
        foreach (var line in lines)
        {
            items.Insert(at++, SourceMenuItem(line));
        }

        if (items.Count > at)
        {
            items.Insert(at++, new Separator());
        }

        return at;
    }

    /// <summary>바뀌는 줄 하나: 이름 + 회색 보조 글(테마 키), 툴팁 = 전체 글. 실행이 없으면 회색 안내 줄.</summary>
    private static MenuItem SourceMenuItem(MenuSourceItem line)
    {
        object header = line.Title.Replace("_", "__", StringComparison.Ordinal);
        if (line.Detail is { } detail)
        {
            var gray = new TextBlock { Text = detail, Margin = new Avalonia.Thickness(12, 0, 0, 0) };
            gray.Bind(TextBlock.ForegroundProperty, gray.GetResourceObservable("SystemControlForegroundBaseMediumBrush"));
            header = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children = { new TextBlock { Text = line.Title }, gray },
            };
        }

        var item = new MenuItem { Header = header, IsEnabled = line.Run is not null };
        AutomationProperties.SetName(item, line.Title);
        if (line.ToolTip is { } tip)
        {
            ToolTip.SetTip(item, tip);
        }

        if (line.Run is { } run)
        {
            item.Click += (_, _) => _ = RunSourceAsync(run);
        }

        return item;
    }

    private static async Task RunSourceAsync(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("메뉴 줄 실행 중 예외: {0}", ex);
        }
    }

    private static void SetTip(Control control, string? tip)
    {
        if (!Equals(ToolTip.GetTip(control), tip))
        {
            ToolTip.SetTip(control, tip);
        }
    }

    /// <summary>최상위 메뉴 하나의 지금 상태를 컨트롤에 옮긴다.</summary>
    private void ApplyState(Bound top)
    {
        var state = MenuVisibility.Evaluate(top.Node, _menuKeys);
        ApplyChildren(top, state);
        top.SetEmpty?.Invoke(state.ShowEmpty);
    }

    private static void ApplyChildren(Bound bound, MenuState state)
    {
        for (var i = 0; i < bound.Children.Count && i < state.Children.Count; i++)
        {
            var child = bound.Children[i];
            var s = state.Children[i];
            child.SetVisible(s.Visible);
            child.SetTip(s.Reason);
            if (child.Children.Count > 0)
            {
                ApplyChildren(child, s);
            }
        }
    }

    /// <summary>
    /// macOS 시스템 메뉴 막대의 뿌리 메뉴. 창마다 하나씩 만들어 그 창에 한 번만 붙이고 <see cref="FillNativeMenu"/> 로 항목만 갈아 끼운다
    /// (Avalonia 가 창에 처음 붙은 <see cref="NativeMenu"/> 인스턴스가 아닌 메뉴로는 갱신을 거부하고, 한 인스턴스를 두 창에 붙일 수도 없다).
    /// 메뉴를 열거나 단축키를 찾을 때(<see cref="NativeMenu.NeedsUpdate"/>) 활성·체크·단축키 상태를 다시 계산한다.
    /// </summary>
    /// <param name="windowFocus">이 창에서 포커스를 가진 요소(입력 칸 편집 키 전달용). 없으면 생성자에서 받은 것을 쓴다.</param>
    public NativeMenu CreateNativeRoot(Func<object?>? windowFocus = null)
    {
        var root = new NativeMenu();
        root.NeedsUpdate += (_, _) => RefreshEnablement();
        _natives[root] = new NativeSet(windowFocus ?? focused ?? (() => null));
        return root;
    }

    /// <summary>창이 닫힐 때 그 창의 뿌리 메뉴 추적을 거둔다.</summary>
    public void ReleaseNativeRoot(NativeMenu root) => _natives.Remove(root);

    /// <summary>
    /// <see cref="BuildMenuBar"/> 와 같은 <see cref="MenuNode"/> 모델로 <paramref name="root"/>(<see cref="CreateNativeRoot"/> 로 만든 것)의 항목을 다시 채운다.
    /// 접근 키 표기는 쓰지 않고(「파일」), 단축키는 <see cref="NativeMenuItem.Gesture"/> 로 보이며 시스템이 같은 키를 먼저 실행한다.
    /// </summary>
    public void FillNativeMenu(NativeMenu root, IReadOnlyList<MenuNode> bar)
    {
        var set = _natives[root];
        set.Clear();
        root.Items.Clear();
        _building = set;
        try
        {
            foreach (var top in bar)
            {
                var bound = Bound.Top(top);
                var menu = NativeLevel(top.Children, bound);
                if (top.EmptyText is { } emptyText)
                {
                    var empty = new NativeMenuItem(emptyText) { IsEnabled = false, IsVisible = false };
                    menu.Add(empty);
                    bound.SetEmpty = v => empty.IsVisible = v;
                }

                // 시스템은 메뉴를 열 때 NeedsUpdate 다음에 Opening 을 낸다. 하위 메뉴의 열림·닫힘은 세지 않는다(부모가 아직 열려 있다).
                menu.Opening += (_, _) =>
                {
                    set.Open++;
                    RefreshEnablement();
                };
                menu.Closed += (_, _) =>
                {
                    set.Open = Math.Max(0, set.Open - 1);
                    RefreshEnablement();
                };
                root.Add(new NativeMenuItem(top.Title) { Menu = menu });
                set.Tops.Add(bound);
                ApplyState(bound);
            }
        }
        finally
        {
            _building = null;
        }
    }

    /// <summary>시스템 메뉴 한 단계. 창 안 메뉴와 같은 숨김 규칙을 쓰고, 회색 이유는 <see cref="NativeMenuItem.ToolTip"/> 로 단다(보이는지는 플랫폼 몫).</summary>
    private NativeMenu NativeLevel(IEnumerable<MenuNode> nodes, Bound owner)
    {
        var menu = new NativeMenu();
        menu.NeedsUpdate += (_, _) => RefreshEnablement();
        foreach (var n in nodes)
        {
            menu.Add(NativeItemFor(n, owner));
        }

        return menu;
    }

    private NativeMenuItemBase NativeItemFor(MenuNode n, Bound owner)
    {
        switch (n.Kind)
        {
            case MenuNodeKind.Separator:
                {
                    var separator = new NativeMenuItemSeparator();
                    owner.Children.Add(new Bound(n, v => separator.IsVisible = v, _ => { }));
                    return separator;
                }

            case MenuNodeKind.Submenu:
                {
                    var sub = new NativeMenuItem(n.Title);
                    var bound = new Bound(n, v => sub.IsVisible = v, _ => { });
                    owner.Children.Add(bound);
                    var menu = NativeLevel(n.Children, bound);
                    sub.Menu = menu;
                    if (n.Key is { } key)
                    {
                        // 시스템 메뉴는 두 색을 못 쓴다: 이름 — 줄인 경로 한 줄, 전체 경로는 툴팁.
                        // NeedsUpdate 는 단축키를 찾을 때도 온다: 줄이 그대로면 다시 넣지 않는다(시스템 메뉴 재수출을 줄인다).
                        var added = 0;
                        string? shown = null;
                        void Fill() => added = FillNativeSource(menu, added, key, ref shown);
                        Fill();
                        menu.NeedsUpdate += (_, _) => Fill();
                    }

                    return sub;
                }

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
                    _building!.Panels.Add((item, id));
                    owner.Children.Add(new Bound(n, v => item.IsVisible = v, _ => { }));
                    return item;
                }

            default:
                {
                    var key = gesture(n.CommandId!);
                    var set = _building!;
                    var command = new RegistryCommand(n.CommandId!, registry, keys);
                    set.Commands.Add(command);
                    var item = new NativeMenuItem(n.Title.Replace("_", "__", StringComparison.Ordinal))
                    {
                        Command = new NativeMenuCommand(command, key, set.Focused, dispatcher, AlignEditorTarget),
                        Gesture = key,
                    };
                    if (key is not null)
                    {
                        set.Gestures.Add((item, n.CommandId!, key));
                    }

                    owner.Children.Add(new Bound(n, v => item.IsVisible = v, tip => item.ToolTip = tip));
                    return item;
                }
        }
    }

    /// <summary>시스템 메뉴판 <see cref="FillSource"/>.</summary>
    private static int FillNativeSource(NativeMenu menu, int added, string key, ref string? shown)
    {
        var lines = MenuItemSources.Find(key)?.Items() ?? [];
        var signature = string.Join('\n', lines.Select(l => $"{l.Title}\t{l.Detail}\t{l.ToolTip}\t{l.Run is not null}"));
        if (signature == shown)
        {
            return added;
        }

        shown = signature;
        for (var i = 0; i < added; i++)
        {
            menu.Items.RemoveAt(0);
        }

        if (lines.Count == 0)
        {
            return 0;
        }

        var at = 0;
        foreach (var line in lines)
        {
            var text = line.Detail is { } detail ? $"{line.Title} — {detail}" : line.Title;
            var item = new NativeMenuItem(text.Replace("_", "__", StringComparison.Ordinal)) // 「_」 는 접근 키 표시라 글자로 두 번 쓴다
            {
                IsEnabled = line.Run is not null,
                ToolTip = line.ToolTip,
            };
            if (line.Run is { } run)
            {
                item.Click += (_, _) => _ = RunSourceAsync(run);
            }

            menu.Items.Insert(at++, item);
        }

        if (menu.Items.Count > at)
        {
            menu.Items.Insert(at++, new NativeMenuItemSeparator());
        }

        return at;
    }

    /// <summary>편집기 호스트의 활성 작업 공간을 지금 도킹에서 보이는 SQL 작업 공간에 맞춘다(보이는 문서가 SQL 작업 공간이 아니면 그대로 둔다).</summary>
    internal void AlignEditorTarget()
    {
        var host = SqlEditorHost.Current;
        if (workspace.ActiveDocument?.Panel is SqlWorkspaceViewModel shown && host.Workspaces.Contains(shown))
        {
            host.ActiveWorkspace = shown;
        }
    }

    /// <summary>
    /// 툴바 단추와 구분선. 단추는 <see cref="Button"/>, 구분선은 <see cref="Separator"/>.
    /// 명령의 <c>When</c> 이 지금 맞지 않는 단추와 양옆이 모두 숨은 구분선은 숨기고 <see cref="ToolbarOutOfScopeClass"/> 를 단다(<see cref="RefreshEnablement"/> 때마다 다시 계산).
    /// </summary>
    public IReadOnlyList<Control> BuildToolbarItems(IReadOnlyList<MenuNode> nodes)
    {
        _toolbar.Clear();
        var items = new List<Control>();
        foreach (var n in nodes)
        {
            if (n.Kind == MenuNodeKind.Separator)
            {
                var separator = new Separator { Classes = { "toolbar" } };
                _toolbar.Add((n, separator));
                items.Add(separator);
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
            _toolbar.Add((n, button));
            items.Add(button);
        }

        ApplyToolbarState(notify: false);
        return items;
    }

    /// <summary>툴바의 <c>When</c> 숨김을 컨트롤에 옮긴다. 바뀐 것이 있으면 <see cref="ToolbarScopeChanged"/>.</summary>
    private void ApplyToolbarState(bool notify = true)
    {
        if (_toolbar.Count == 0)
        {
            return;
        }

        var visible = MenuVisibility.Toolbar([.. _toolbar.Select(t => t.Node)], _menuKeys);
        var changed = false;
        for (var i = 0; i < _toolbar.Count; i++)
        {
            var control = _toolbar[i].Control;
            var hidden = !visible[i];
            if (control.Classes.Contains(ToolbarOutOfScopeClass) == hidden)
            {
                continue;
            }

            control.Classes.Set(ToolbarOutOfScopeClass, hidden);
            control.IsVisible = !hidden; // 다시 보일 때는 주 창의 넘침 계산이 폭에 맞춰 다시 접는다

            changed = true;
        }

        if (changed && notify)
        {
            ToolbarScopeChanged?.Invoke(this, EventArgs.Empty);
        }
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

    private MenuItem MenuItemFor(MenuNode n, bool track = true)
    {
        switch (n.Kind)
        {
            case MenuNodeKind.Submenu:
                {
                    var item = new MenuItem { Header = n.Title };
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
