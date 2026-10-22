using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.SchemaBrowser;

public partial class SchemaBrowserView : UserControl
{
    private Point _pressPoint;
    private SchemaNode? _pressNode;
    private bool _dragging;
    private PointerPressedEventArgs? _pressArgs;
    private bool _pressExpanded;
    private int _pressClicks;
    private SchemaNode? _keySelected;
    private SchemaBrowserViewModel? _revealSource;

    /// <summary>끌기를 시작하는 호출. 제품은 <see cref="DragDrop.DoDragDropAsync"/> 를 쓰고, 시험이 실패를 흉내 내려고 바꾼다.</summary>
    internal Func<PointerPressedEventArgs, IDataTransfer, DragDropEffects, Task<DragDropEffects>> DragStarter { get; set; } = DragStart.Default;

    public SchemaBrowserView()
    {
        InitializeComponent();
        TypeBox.ItemsSource = SchemaBrowserViewModel.KnownTypes;
        InvalidToggle.Content = ShellIcons.Create("invalid");
        FavoritesToggle.Content = ShellIcons.Create("star");
        RefreshButton.Content = ShellIcons.Create("refresh");
        RefreshButton.Click += async (_, _) =>
        {
            if (DataContext is SchemaBrowserViewModel vm)
            {
                await vm.LoadAsync();
            }
        };
        FilterBox.InnerLeftContent = ShellIcons.Create("find", 14);
        SavedFilterButton.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 1,
            Children =
            {
                ShellIcons.Create("filter")!,
                new TextBlock { Text = "▾", FontSize = 9, Opacity = 0.7, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            },
        };
        var savedFilterMenu = new MenuFlyout();
        savedFilterMenu.Opening += (_, _) =>
        {
            savedFilterMenu.Items.Clear();
            foreach (var item in BuildSavedFilterMenu())
            {
                savedFilterMenu.Items.Add(item);
            }
        };
        SavedFilterButton.Flyout = savedFilterMenu;
        ShellContextMenus.Attach(Tree, "schema");
        Tree.AddHandler(PointerPressedEvent, OnTreePressed, handledEventsToo: true);
        Tree.AddHandler(PointerMovedEvent, OnTreeMoved, handledEventsToo: true);
        // 객체 노드의 머리글을 두 번 누르면 트리가 펼침을 바꾼 뒤 이벤트를 처리됨으로 표시하므로, 처리된 것도 받는다.
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(TappedEvent, OnTreeTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeEnter, RoutingStrategies.Tunnel);
        Tree.AddHandler(KeyDownEvent, OnTreeNavigated, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SchemaBrowserViewModel vm)
            {
                vm.FilterFocusRequested += (_, _) => Dispatcher.UIThread.Post(() => FilterBox.Focus());
                vm.FilterEditorRequested += (_, _) => Dispatcher.UIThread.Post(() => _ = OpenFilterEditorAsync());
            }

            ListenForReveal(TopLevel.GetTopLevel(this) is not null);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ListenForReveal(attached: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ListenForReveal(attached: false);
    }

    /// <summary>
    /// 화면에 붙어 있는 동안만 「트리에서 찾기」 알림을 받는다. 도킹 배치를 다시 지으면 이 View 가 새로 만들어지므로,
    /// 떨어진 옛 View 가 ViewModel 에 매달려 남지 않게 한다.
    /// </summary>
    private void ListenForReveal(bool attached)
    {
        var wanted = attached ? DataContext as SchemaBrowserViewModel : null;
        if (ReferenceEquals(wanted, _revealSource))
        {
            return;
        }

        if (_revealSource is not null)
        {
            _revealSource.RevealRequested -= OnRevealRequested;
        }

        _revealSource = wanted;
        if (wanted is not null)
        {
            wanted.RevealRequested += OnRevealRequested;
        }
    }

    /// <summary>필터 단추 ▾ 메뉴: 필터 없음 · 저장한 필터(지금 것에 체크) · 필터 편집…(열 때마다 새로 만든다).</summary>
    internal System.Collections.Generic.List<Control> BuildSavedFilterMenu()
    {
        var items = new System.Collections.Generic.List<Control>();
        if (DataContext is not SchemaBrowserViewModel vm)
        {
            return items;
        }

        MenuItem Choice(string header, string? id)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = vm.ActiveSavedFilter?.Id == id };
            item.Click += async (_, _) => await vm.SelectSavedFilterAsync(id);
            return item;
        }

        items.Add(Choice("필터 없음", null));
        foreach (var f in vm.SavedFilters)
        {
            items.Add(Choice(f.Name, f.Id));
        }

        items.Add(new Separator());
        var edit = new MenuItem { Header = "필터 편집…" };
        edit.Click += async (_, _) => await OpenFilterEditorAsync();
        items.Add(edit);
        return items;
    }

    /// <summary>편집 대화상자를 연다. 저장은 그때그때 목록에 쓰고, 확인은 대화상자에서 고른 필터를 이 접속에 건다.</summary>
    private async Task OpenFilterEditorAsync()
    {
        if (DataContext is not SchemaBrowserViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var editor = new SchemaFilterEditorViewModel(vm.SavedFilters, vm.ActiveSavedFilter?.Id, vm.AllSchemaNames,
            list => vm.SaveFiltersKeepingChoiceAsync(list));
        if (await new SchemaFilterEditorWindow { DataContext = editor }.ShowDialog<bool>(owner))
        {
            await vm.SaveFiltersAsync(editor.BuildFilters(), editor.SelectedId);
        }
    }

    private void OnRevealRequested(object? sender, SchemaNode node) =>
        Dispatcher.UIThread.Post(() => ShowRevealedNode(node, 0), DispatcherPriority.Background);

    /// <summary>
    /// 트리에서 찾은 노드를 화면에 보이고 포커스를 준다(방향키로 바로 이어 다니게). 막 펼친 노드는 컨테이너가 아직 없을 수 있어
    /// 잠깐 뒤 몇 번 더 본다.
    /// </summary>
    private void ShowRevealedNode(SchemaNode node, int attempt)
    {
        var item = Tree.GetVisualDescendants().OfType<TreeViewItem>().FirstOrDefault(i => ReferenceEquals(i.DataContext, node));
        if (item is null)
        {
            if (attempt < 10)
            {
                DispatcherTimer.RunOnce(() => ShowRevealedNode(node, attempt + 1), TimeSpan.FromMilliseconds(50));
            }

            return;
        }

        item.BringIntoView();
        item.Focus();
    }

    /// <summary>
    /// 두 번 누르기: 「더 보기」는 다음 쪽을 올리고, 객체 노드는 고정 문서로 연다(포커스는 그 문서로).
    /// 펼침은 첫 번째 누르기에서 한 번 바뀐 상태를 유지한다. 트리가 두 번 누르기로 펼침을 한 번 더 바꾸므로 두 번째 누르기 전 상태로 되돌린다.
    /// </summary>
    private async void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SchemaBrowserViewModel vm)
        {
            return;
        }

        if (NodeFrom(e.Source) is { HasChildrenToLoad: true } expandable && !IsExpander(e.Source) && expandable.IsExpanded != _pressExpanded)
        {
            expandable.IsExpandedSilently = _pressExpanded;
        }

        if (vm.SelectedNode is { Kind: SchemaNodeKind.More } more)
        {
            vm.ShowMore(more);
            return;
        }

        if (NodeFrom(e.Source) is not { OpenTarget: not null } node || IsExpander(e.Source))
        {
            return;
        }

        await vm.OpenPinnedAsync(node);
    }

    /// <summary>
    /// 한 번 누르기(왼쪽 단추): 자식을 가질 수 있는 노드는 줄 어디를 눌러도 펼침을 바꾸고, 객체 노드면 미리보기도 예약한다.
    /// 펼침 단추(▸)를 누른 것은 단추가 직접 바꾸므로 뺀다. 두 번째 누르기는 펼침을 다시 바꾸지 않는다. 끌기 뒤에는 바꾸지 않는다.
    /// 포커스는 트리에 남는다.
    /// </summary>
    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SchemaBrowserViewModel vm || NodeFrom(e.Source) is not { } node || IsExpander(e.Source))
        {
            return;
        }

        if (node.HasChildrenToLoad && _pressClicks <= 1 && !_dragging)
        {
            node.IsExpanded = !node.IsExpanded;
        }

        vm.SchedulePreview(node);
    }

    /// <summary>Enter: 「더 보기」는 다음 쪽, 객체 노드는 고정 문서로 연다(포커스는 그 문서로). 다른 키는 누르기 전 선택만 적어 둔다.</summary>
    private async void OnTreeEnter(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SchemaBrowserViewModel vm)
        {
            return;
        }

        _keySelected = vm.SelectedNode;
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (vm.SelectedNode)
        {
            case { Kind: SchemaNodeKind.More } more:
                e.Handled = true;
                vm.ShowMore(more);
                break;
            case { OpenTarget: not null } node:
                e.Handled = true;
                await vm.OpenPinnedAsync(node);
                break;
        }
    }

    /// <summary>키(방향키·Home·End·Page·글자 찾기)로 다른 노드를 고른 뒤: 잠깐 멈추면 미리보기로 연다(누르고 있는 동안은 계속 미룬다).</summary>
    private void OnTreeNavigated(object? sender, KeyEventArgs e)
    {
        if (DataContext is SchemaBrowserViewModel vm && !ReferenceEquals(vm.SelectedNode, _keySelected))
        {
            vm.SchedulePreview(vm.SelectedNode);
        }
    }

    private static SchemaNode? NodeFrom(object? source) => (source as Control)?.DataContext as SchemaNode;

    /// <summary>트리 항목 앞의 펼침 단추(▸)를 누른 것인지.</summary>
    private static bool IsExpander(object? source) =>
        source is Control c && (c as ToggleButton ?? c.FindAncestorOfType<ToggleButton>()) is { } toggle
        && toggle.FindAncestorOfType<TreeViewItem>() is not null;

    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        _pressArgs = e;
        _pressPoint = e.GetPosition(Tree);
        _pressNode = (e.Source as Control)?.DataContext as SchemaNode;
        _pressExpanded = _pressNode?.IsExpanded ?? false;
        _pressClicks = e.ClickCount;
        _dragging = false;

        // 끌 글자의 재료(열 목록)는 끌기가 시작되기 전에 불러 둔다(끌기 시작 때는 await 를 둘 수 없다).
        if (_pressNode is { } pressed && e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed && DataContext is SchemaBrowserViewModel loader)
        {
            _ = loader.PreloadDragMaterialAsync(pressed);
        }

        // 오른쪽 단추는 누른 노드를 먼저 고른다(오른쪽 클릭 메뉴가 그 노드에 대해 열린다).
        if (_pressNode is { } node && e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed && DataContext is SchemaBrowserViewModel vm)
        {
            vm.SelectedNode = node;
        }
    }

    private void OnTreeMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _pressNode is not { } node || _pressArgs is null || DataContext is not SchemaBrowserViewModel vm
            || !e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var d = e.GetPosition(Tree) - _pressPoint;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6)
        {
            return;
        }

        _dragging = true;
        _ = StartDragAsync(node, _pressArgs, vm, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    /// <summary>
    /// 끌기를 시작한다. 포인터 이벤트가 살아 있는 동안 OS 끌기 호출까지 가야 하므로(Windows OLE) 호출 전에 <c>await</c> 를 두지 않는다.
    /// 끌 글자는 누름 때 미리 불러 둔 재료로 동기로 만든다. Ctrl = 이름만, Shift = 열 목록, 그 밖 = SELECT 문(표·뷰).
    /// 편집기는 일반 텍스트 끌어 놓기로 받는다.
    /// </summary>
    private async Task StartDragAsync(SchemaNode node, PointerPressedEventArgs pressArgs, SchemaBrowserViewModel vm, bool ctrl, bool shift)
    {
        try
        {
            var (text, columnsMissing) = vm.BuildDragText(node, ctrl, shift);
            if (text is null)
            {
                return;
            }

            if (columnsMissing)
            {
                vm.StatusText = shift
                    ? "열 목록을 아직 불러오지 못해 이름만 넣었습니다"
                    : "열 목록을 아직 불러오지 못해 SELECT * 로 넣었습니다";
            }

            var data = new Avalonia.Input.DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await DragStart.RunAsync(DragStarter, pressArgs, data, DragDropEffects.Copy, message => vm.StatusText = message);
        }
        catch (Exception ex)
        {
            DragStart.Fail(ex, message => vm.StatusText = message);
        }
        finally
        {
            _pressNode = null;
        }
    }
}
