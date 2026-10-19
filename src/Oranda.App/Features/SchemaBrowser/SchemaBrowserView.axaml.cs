using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.SchemaBrowser;

public partial class SchemaBrowserView : UserControl
{
    private Point _pressPoint;
    private SchemaNode? _pressNode;
    private bool _dragging;
    private PointerPressedEventArgs? _pressArgs;

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
        };
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
