using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private static MapperFolderTreeNode? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as MapperFolderTreeNode;
}
