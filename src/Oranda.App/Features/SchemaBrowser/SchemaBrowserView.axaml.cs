using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        Tree.DoubleTapped += OnTreeDoubleTapped;
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

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SchemaBrowserViewModel vm && vm.SelectedNode is { Kind: SchemaNodeKind.More } more)
        {
            vm.ShowMore(more);
        }
    }

    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        _pressArgs = e;
        _pressPoint = e.GetPosition(Tree);
        _pressNode = (e.Source as Control)?.DataContext as SchemaNode;
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
