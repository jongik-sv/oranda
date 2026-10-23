using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

public partial class MapperFolderTreeView : UserControl
{
    public MapperFolderTreeView()
    {
        InitializeComponent();

        // 메뉴 명령의 대상은 고른 노드이므로 메뉴가 열리기 전에(터널 단계) 눌린 노드를 고른다.
        Tree.AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (DataContext is MapperFolderTreeViewModel vm && NodeAt(e.Source) is { } node)
            {
                vm.SelectedNode = node;
            }
        }, RoutingStrategies.Tunnel);
        // 폴더 노드는 TreeViewItem 이 두 번 눌러 접고 편다(여기서 또 바꾸면 그대로 남는다).
        Tree.DoubleTapped += (_, e) =>
        {
            if (DataContext is MapperFolderTreeViewModel vm && NodeAt(e.Source) is { Kind: not MapperFolderNodeKind.Folder } node)
            {
                _ = vm.OpenNodeAsync(node);
                e.Handled = true;
            }
        };
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is MapperFolderTreeViewModel { SelectedNode: { } node } vm)
            {
                _ = vm.OpenNodeAsync(node);
                e.Handled = true;
            }
        };
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        // 폴더 상태 구독은 화면에 붙어 있는 동안만(모드를 끄거나 창을 버리면 풀린다).
        AttachedToVisualTree += (_, _) => (DataContext as MapperFolderTreeViewModel)?.Activate();
        DetachedFromVisualTree += (_, _) => (DataContext as MapperFolderTreeViewModel)?.Deactivate();
        UsageList.DoubleTapped += (_, e) =>
        {
            if (DataContext is MapperFolderTreeViewModel vm
                && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is MapperFolderUsageRow row)
            {
                _ = vm.OpenUsageAsync(row);
            }
        };
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as MapperFolderTreeViewModel)?.ClearSearch();
        SearchBox.Focus();
    }

    // Ctrl+F = 검색 칸, Esc = 지움, 검색 칸에서 ↓ = 첫 쿼리, Enter = 고른(없으면 첫) 쿼리 열기.
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MapperFolderTreeViewModel vm)
        {
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.SearchText.Length > 0)
        {
            vm.ClearSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchBox.IsFocused)
        {
            vm.FlushSearch();
            if (vm.FirstElement() is { } first)
            {
                vm.SelectedNode = first;
                Dispatcher.UIThread.Post(() => Tree.TreeContainerFromItem(first)?.Focus(), DispatcherPriority.Loaded);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && SearchBox.IsFocused)
        {
            vm.FlushSearch();
            var target = vm.SelectedNode is { Kind: MapperFolderNodeKind.Element } picked ? picked : vm.FirstElement();
            if (target is not null)
            {
                _ = vm.OpenNodeAsync(target);
            }

            e.Handled = true;
        }
    }

    private static MapperFolderTreeNode? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as MapperFolderTreeNode;
}
