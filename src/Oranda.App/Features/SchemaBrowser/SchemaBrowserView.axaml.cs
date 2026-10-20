using System;
using System.Linq;
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
    private SchemaBrowserViewModel? _revealSource;

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
    /// 표·뷰 노드는 트리가 두 번 누르기로 펼침을 바꾸므로 누르기 전 상태로 되돌린다(여는 동작만 남긴다).
    /// </summary>
    private async void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SchemaBrowserViewModel vm)
        {
            return;
        }

        if (vm.SelectedNode is { Kind: SchemaNodeKind.More } more)
        {
            vm.ShowMore(more);
            return;
        }

        if (NodeFrom(e.Source) is not { Kind: SchemaNodeKind.Item, Ref: not null } node || IsExpander(e.Source))
        {
            return;
        }

        if (node.IsExpanded != _pressExpanded)
        {
            node.IsExpandedSilently = _pressExpanded;
        }

        await vm.OpenPinnedAsync(node);
    }

    /// <summary>한 번 누르기: 객체 노드면 미리보기를 예약한다(펼침 단추를 누른 것은 빼고). 포커스는 트리에 남는다.</summary>
    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SchemaBrowserViewModel vm && NodeFrom(e.Source) is { } node && !IsExpander(e.Source))
        {
            vm.SchedulePreview(node);
        }
    }

    /// <summary>Enter: 「더 보기」는 다음 쪽, 객체 노드는 고정 문서로 연다(포커스는 그 문서로).</summary>
    private async void OnTreeEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || DataContext is not SchemaBrowserViewModel vm)
        {
            return;
        }

        switch (vm.SelectedNode)
        {
            case { Kind: SchemaNodeKind.More } more:
                e.Handled = true;
                vm.ShowMore(more);
                break;
            case { Kind: SchemaNodeKind.Item, Ref: not null } node:
                e.Handled = true;
                await vm.OpenPinnedAsync(node);
                break;
        }
    }

    /// <summary>방향키·Home·End·Page 로 고른 뒤: 잠깐 멈추면 미리보기로 연다(누르고 있는 동안은 계속 미룬다).</summary>
    private void OnTreeNavigated(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            && DataContext is SchemaBrowserViewModel vm)
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
        _dragging = false;

        // 오른쪽 단추는 누른 노드를 먼저 고른다(오른쪽 클릭 메뉴가 그 노드에 대해 열린다).
        if (_pressNode is { } node && e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed && DataContext is SchemaBrowserViewModel vm)
        {
            vm.SelectedNode = node;
        }
    }

    private async void OnTreeMoved(object? sender, PointerEventArgs e)
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
        try
        {
            // Ctrl = 이름만, Shift = 열 목록, 그 밖 = SELECT 문(표·뷰). 편집기는 일반 텍스트 끌어 놓기로 받는다.
            var text = await vm.PrepareDragTextAsync(node, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (text is null)
            {
                return;
            }

            var data = new Avalonia.Input.DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await DragDrop.DoDragDropAsync(_pressArgs!, data, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            vm.StatusText = "끌어 놓기를 시작하지 못했다: " + ex.Message;
        }
        finally
        {
            _pressNode = null;
        }
    }
}
