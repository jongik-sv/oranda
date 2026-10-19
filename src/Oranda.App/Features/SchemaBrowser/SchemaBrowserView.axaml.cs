using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

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
