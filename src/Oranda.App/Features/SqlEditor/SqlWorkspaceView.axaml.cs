using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 작업 공간 문서: 위에 작업 공간의 SQL 탭 줄, 아래에 고른 탭의 편집기. 편집기 View 는 탭마다 하나씩 만들어 두고
/// 탭을 바꿀 때 끼워 넣는다(View 를 새로 만들면 접기·스크롤 위치가 사라진다). 캐시는 이 View 인스턴스 것이다:
/// 도킹이 문서를 옮기며 작업 공간 View 를 새로 만들면 편집기 View 도 새로 만든다(한 컨트롤은 부모를 둘 가질 수 없다).
/// </summary>
public partial class SqlWorkspaceView : UserControl
{
    private readonly Dictionary<SqlEditorViewModel, SqlEditorView> _views = [];
    private SqlWorkspaceViewModel? _vm;

    private const double DragThreshold = 6;

    private SqlEditorViewModel? _dragTab;
    private Point _dragStart;
    private bool _dragging;
    private ListBoxItem? _dropItem;
    private int _dropIndex = -1;

    /// <summary>탭 줄의 스크롤 영역(ListBox 템플릿의 PART_ScrollViewer). 템플릿이 붙기 전에는 null.</summary>
    private ScrollViewer? _tabScroller;

    /// <summary>휠 한 칸에 옮기는 가로 거리, 끌기 중 가장자리에 닿았을 때 한 번에 옮기는 거리.</summary>
    private const double WheelStep = 48;

    private const double EdgeScrollZone = 24;

    static SqlWorkspaceView() =>
        // 이름 편집 칸이 보이면 바로 포커스를 주고 글자를 모두 선택한다.
        Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (box.Classes.Contains("tab-rename") && e.NewValue is true)
            {
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        box.Focus();
                        box.SelectAll();
                    },
                    DispatcherPriority.Input);
            }
        });

    public SqlWorkspaceView()
    {
        InitializeComponent();
        SqlTabs.DoubleTapped += OnTabDoubleTapped;
        SqlTabs.AddHandler(KeyDownEvent, OnTabKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SqlTabs.AddHandler(LostFocusEvent, OnTabLostFocus, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SqlTabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SqlTabs.AddHandler(PointerMovedEvent, OnTabPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SqlTabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SqlTabs.AddHandler(PointerWheelChangedEvent, OnTabWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SqlTabs.TemplateApplied += (_, e) => AttachTabScroller(e.NameScope.Find<ScrollViewer>("PART_ScrollViewer"));
        TabListButton.Click += (_, _) => ShowTabList();
        AddHandler(KeyDownEvent, OnWorkspaceKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => Bind(DataContext as SqlWorkspaceViewModel);

        // 도킹이 문서를 떼었다 붙이면(분할·옮기기) 떨어져 있는 동안 구독을 풀어 둔다.
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AttachedToVisualTree += (_, _) => Bind(DataContext as SqlWorkspaceViewModel);
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
        }
    }

    private void Bind(SqlWorkspaceViewModel? vm)
    {
        Unsubscribe();
        if (!ReferenceEquals(_vm, vm))
        {
            _views.Clear(); // 다른 작업 공간이 들어왔다. 같은 작업 공간이 다시 붙은 것이면 편집기 View 를 그대로 쓴다.
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged += OnTabsChanged;
        }

        ShowTab(_vm?.ActiveTab);
    }

    private static SqlEditorViewModel? TabOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SqlEditorViewModel;

    private static bool IsInteractive(object? source) =>
        source is Visual v && (v.FindAncestorOfType<Button>(includeSelf: true) is not null || v.FindAncestorOfType<TextBox>(includeSelf: true) is not null);

    /// <summary>탭 머리를 두 번 누르면 제자리 이름 편집.</summary>
    private void OnTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsInteractive(e.Source) && TabOf(e.Source) is { } tab && _vm is not null)
        {
            _vm.BeginRenameTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    /// <summary>편집 칸에서 Enter 는 확정(빈 이름이면 편집 유지), Esc 는 취소. 활성 탭이 있을 때 F2 는 편집 시작.</summary>
    private void OnTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox && TabOf(e.Source) is { IsRenaming: true } tab)
        {
            if (e.Key == Key.Enter)
            {
                if (tab.CommitRename())
                {
                    _views.GetValueOrDefault(tab)?.FocusEditor();
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                tab.CancelRename();
                _views.GetValueOrDefault(tab)?.FocusEditor();
                e.Handled = true;
            }
        }
    }

    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.F2, KeyModifiers: KeyModifiers.None, Handled: false } && e.Source is not TextBox && _vm?.ActiveTab is { } tab)
        {
            _vm.BeginRenameTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    /// <summary>편집 칸이 포커스를 잃으면 확정한다(빈 이름이면 원래대로).</summary>
    private void OnTabLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is TextBox && TabOf(e.Source) is { IsRenaming: true } tab && !tab.CommitRename())
        {
            tab.CancelRename();
        }
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragTab = null;
        _dragging = false;
        if (!IsInteractive(e.Source) && e.GetCurrentPoint(SqlTabs).Properties.IsLeftButtonPressed)
        {
            _dragTab = TabOf(e.Source);
            _dragStart = e.GetPosition(SqlTabs);
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is null || _vm is null)
        {
            return;
        }

        var pos = e.GetPosition(SqlTabs);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < DragThreshold && Math.Abs(pos.Y - _dragStart.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            e.Pointer.Capture(SqlTabs);
        }

        UpdateDropTarget(pos);
        e.Handled = true;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var tab = _dragTab;
        var index = _dropIndex;
        var wasDragging = _dragging;
        _dragTab = null;
        _dragging = false;
        ClearDropMarker();
        if (wasDragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
            if (tab is not null && _vm is not null && index >= 0)
            {
                _vm.Host.MoveTab(tab, index);
            }
        }
    }

    /// <summary>포인터가 가리키는 탭 머리와 그 앞/뒤를 정해 놓일 자리를 표시한다.</summary>
    private void UpdateDropTarget(Point pos)
    {
        ClearDropMarker();

        // 끌어서 줄 가장자리에 닿으면 그쪽으로 스크롤해 가려진 탭 자리에도 놓을 수 있게 한다.
        if (pos.X < EdgeScrollZone)
        {
            ScrollTabsBy(-WheelStep / 4);
        }
        else if (pos.X > SqlTabs.Bounds.Width - EdgeScrollZone)
        {
            ScrollTabsBy(WheelStep / 4);
        }
        if (_vm is null || _dragTab is null)
        {
            return;
        }

        foreach (var tab in _vm.Tabs)
        {
            if (SqlTabs.ContainerFromItem(tab) is not ListBoxItem item || item.TranslatePoint(default, SqlTabs) is not { } origin)
            {
                continue;
            }

            var rect = new Rect(origin, item.Bounds.Size);
            if (!rect.Contains(pos))
            {
                continue;
            }

            var after = pos.X > rect.Center.X;
            var target = _vm.Tabs.IndexOf(tab);
            var from = _vm.Tabs.IndexOf(_dragTab);
            // 앞/뒤 삽입 자리를 Move 가 받는 최종 인덱스로 바꾼다(빼고 넣으므로 원래 자리보다 뒤면 하나 당겨진다).
            var insert = after ? target + 1 : target;
            _dropIndex = insert > from ? insert - 1 : insert;
            _dropItem = item;
            item.Classes.Set(after ? "drop-after" : "drop-before", true);
            return;
        }

        _dropIndex = -1;
    }

    private void AttachTabScroller(ScrollViewer? scroller)
    {
        if (_tabScroller is not null)
        {
            _tabScroller.ScrollChanged -= OnTabScrollChanged;
        }

        _tabScroller = scroller;
        if (_tabScroller is not null)
        {
            _tabScroller.ScrollChanged += OnTabScrollChanged;
        }
    }

    /// <summary>탭이 줄에 다 들어가지 않을 때만 탭 목록 단추를 보인다.</summary>
    private void OnTabScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        TabListButton.IsVisible = _tabScroller is { } s && s.Extent.Width > s.Viewport.Width + 0.5;

    /// <summary>탭 줄 위의 휠은 가로 스크롤이다(세로 휠·가로 휠 모두).</summary>
    private void OnTabWheel(object? sender, PointerWheelEventArgs e)
    {
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (delta != 0 && ScrollTabsBy(-delta * WheelStep))
        {
            e.Handled = true;
        }
    }

    /// <summary>탭 줄을 가로로 옮긴다. 옮길 수 있었으면 true.</summary>
    private bool ScrollTabsBy(double dx)
    {
        if (_tabScroller is not { } s)
        {
            return false;
        }

        var max = Math.Max(0, s.Extent.Width - s.Viewport.Width);
        var x = Math.Clamp(s.Offset.X + dx, 0, max);
        if (Math.Abs(x - s.Offset.X) < 0.01)
        {
            return false;
        }

        s.Offset = s.Offset.WithX(x);
        return true;
    }

    /// <summary>모든 SQL 탭을 메뉴로 보인다. 고르면 그 탭으로 옮겨 간다.</summary>
    private void ShowTabList()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        var menu = new MenuFlyout();
        foreach (var tab in vm.Tabs)
        {
            var item = new MenuItem { Header = tab.DisplayTitle, FontWeight = ReferenceEquals(tab, vm.ActiveTab) ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal };
            item.Click += (_, _) => vm.ActiveTab = tab;
            menu.Items.Add(item);
        }

        menu.ShowAt(TabListButton);
    }

    private void ClearDropMarker()
    {
        _dropItem?.Classes.Remove("drop-before");
        _dropItem?.Classes.Remove("drop-after");
        _dropItem = null;
        _dropIndex = -1;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.ActiveTab))
        {
            ShowTab(_vm?.ActiveTab);
        }
    }

    /// <summary>닫힌 탭의 편집기 View 는 버린다(작업 공간이 오래 열려 있어도 쌓이지 않게).</summary>
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var gone in _views.Keys.Where(t => _vm is null || !_vm.Tabs.Contains(t)).ToList())
        {
            _views.Remove(gone);
        }
    }

    private void ShowTab(SqlEditorViewModel? tab)
    {
        if (tab is null)
        {
            EditorHost.Content = null;
            return;
        }

        if (!_views.TryGetValue(tab, out var view))
        {
            view = new SqlEditorView { DataContext = tab };
            _views[tab] = view;
        }

        EditorHost.Content = view;

        // 줄이 넘쳐도 고른 탭 머리는 보이게(배치가 끝난 뒤에 스크롤한다).
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_vm?.ActiveTab, tab))
                {
                    SqlTabs.ScrollIntoView(tab);
                }
            },
            DispatcherPriority.Loaded);

        // 탭을 고르거나 새로 열면 바로 칠 수 있게 편집기로 포커스를 옮긴다(보고 있는 작업 공간일 때만).
        if (_vm is { } vm && ReferenceEquals(vm.Host.ActiveWorkspace, vm))
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(EditorHost.Content, view) && TopLevel.GetTopLevel(view) is not null)
                    {
                        view.FocusEditor();
                    }
                },
                DispatcherPriority.Background);
        }
    }
}
