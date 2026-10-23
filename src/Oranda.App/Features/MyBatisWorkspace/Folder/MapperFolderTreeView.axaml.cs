using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

public partial class MapperFolderTreeView : UserControl
{
    /// <summary>한 번에 화면에 붙이는 최상위 노드 수. 노드 하나를 처음 그리는 데 수 ms 가 들어 한 번에 다 붙이면 UI 스레드가 오래 멈춘다(TSK-03-12).</summary>
    internal const int ShowBatch = 4;

    // 트리에 보이는 최상위 노드: ViewModel 의 Roots 를 따라가되 몇 개씩 나눠(배경 우선순위) 붙인다. 사이사이 입력·그리기가 돈다.
    private readonly ObservableCollection<MapperFolderTreeNode> _shown = [];
    private MapperFolderTreeViewModel? _vm;
    private bool _syncQueued;

    public MapperFolderTreeView()
    {
        InitializeComponent();
        Tree.ItemsSource = _shown;
        DataContextChanged += (_, _) => Follow(DataContext as MapperFolderTreeViewModel);

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

    /// <summary>ViewModel 의 최상위 노드를 모두 트리에 붙였다(시험용).</summary>
    internal bool IsShowingAll => _vm is null ? _shown.Count == 0 : _shown.SequenceEqual(_vm.Roots);

    private void Follow(MapperFolderTreeViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.Roots.CollectionChanged -= OnRootsChanged;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.Roots.CollectionChanged += OnRootsChanged;
        }

        QueueSync();
    }

    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueSync();

    private void QueueSync()
    {
        if (!_syncQueued)
        {
            _syncQueued = true;
            Dispatcher.UIThread.Post(Sync, DispatcherPriority.Background);
        }
    }

    // 키로 첫 쿼리를 고를 때: 고를 노드가 트리에 있어야 하므로 남은 노드를 지금 다 붙인다.
    internal void ShowAll()
    {
        while (_vm is not null && !IsShowingAll)
        {
            Sync(int.MaxValue);
        }
    }

    /// <summary>한 차례(<see cref="ShowBatch"/> 개) 붙인다(시험용: 배경 우선순위 차례 하나와 같다).</summary>
    internal void SyncOnce() => Sync(ShowBatch);

    private void Sync() => Sync(ShowBatch);

    // 앞부분이 같으면 두고, 다른 곳부터 지운 뒤 batch 개만 더 붙인다. 남았으면 다음 차례에 이어 붙인다.
    private void Sync(int batch)
    {
        _syncQueued = false;
        var source = _vm?.Roots;
        if (source is null)
        {
            _shown.Clear();
            return;
        }

        int same = 0;
        while (same < _shown.Count && same < source.Count && ReferenceEquals(_shown[same], source[same]))
        {
            same++;
        }

        if (same == 0 && _shown.Count > 0)
        {
            _shown.Clear();
        }

        while (_shown.Count > same)
        {
            _shown.RemoveAt(_shown.Count - 1);
        }

        int end = (int)Math.Min(source.Count, (long)same + batch);
        for (int i = same; i < end; i++)
        {
            _shown.Add(source[i]);
        }

        if (_shown.Count < source.Count)
        {
            QueueSync();
        }
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
            ShowAll();
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
            ShowAll();
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
