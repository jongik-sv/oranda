using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

public partial class MyBatisStatementsView : UserControl
{
    private const string ContextSurface = "mybatisStatements";

    // 보기 모드의 View 는 이 화면 몫이다: 같은 ViewModel 을 보는 창이 둘이어도(도킹 재배치·떠 있는 창) 같은 Control 을 두 부모에 붙이지 않는다.
    private readonly Dictionary<IMyBatisStatementsViewMode, Control> _modeViews = [];
    private MyBatisStatementsViewModel? _model;
    private bool _listening;

    public MyBatisStatementsView()
    {
        InitializeComponent();
        ShellContextMenus.Attach(Tree, ContextSurface);
        ShellContextMenus.Attach(HeaderBorder, ContextSurface);

        // 메뉴 항목은 열 때 만들어지고 명령 대상은 세션의 고른 문장이므로, 메뉴가 열리기 전에(터널 단계) 눌린 항목을 고른다.
        Tree.AddHandler(ContextRequestedEvent, OnTreeContextRequested, RoutingStrategies.Tunnel);

        // 묶음 머리 줄은 고르는 줄이 아니라 접고 펼치는 줄이다(오른쪽 단추는 메뉴용이라 건드리지 않는다).
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem item)
            {
                item.Focusable = (item.DataContext as MyBatisStatementNode)?.IsGroup != true; // 방향키가 머리 줄에 서지 않게
            }
        };
        Tree.DoubleTapped += (_, e) =>
        {
            if (DataContext is MyBatisStatementsViewModel vm && NodeAt(e.Source) is { IsGroup: false } node)
            {
                vm.OpenNode(node);
            }
        };
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is MyBatisStatementsViewModel { SelectedNode: { IsGroup: false } node } vm)
            {
                vm.OpenNode(node);
                e.Handled = true;
            }
        };
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        HeaderBorder.SizeChanged += (_, _) => UpdateDirectoryText();
        DataContextChanged += (_, _) => Follow(DataContext as MyBatisStatementsViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            (DataContext as MyBatisStatementsViewModel)?.Attach();
            Listen(true);
            UpdateDirectoryText();
            ShowActiveMode();
        };

        // 도킹 재배치로 버려진 화면을 오래 사는 ViewModel 이 붙잡지 않게 떨어지면 구독을 푼다(모드 View 캐시는 다시 붙을 때 쓴다).
        DetachedFromVisualTree += (_, _) => Listen(false);
    }

    private void Follow(MyBatisStatementsViewModel? model)
    {
        if (ReferenceEquals(model, _model))
        {
            return;
        }

        Listen(false);
        _model = model;
        foreach (var view in _modeViews.Values)
        {
            (view.DataContext as IDisposable)?.Dispose();
        }

        _modeViews.Clear();
        Listen(true);
        UpdateDirectoryText();
        ShowActiveMode();
    }

    private void Listen(bool on)
    {
        if (_listening == on || _model is null)
        {
            _listening = on && _model is not null;
            return;
        }

        if (on)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
        }
        else
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        _listening = on;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MyBatisStatementsViewModel.MapperDirectory))
        {
            UpdateDirectoryText();
        }
        else if (e.PropertyName == nameof(MyBatisStatementsViewModel.ActiveMode))
        {
            ShowActiveMode();
        }
    }

    /// <summary>눌린 보기 모드의 View 를 붙인다. 모드마다 이 화면에서 한 번 만들고, 껐다 켜면 같은 것을 다시 쓴다.</summary>
    private void ShowActiveMode()
    {
        if (_model?.ActiveMode?.Mode is not { } mode)
        {
            ModeHost.Content = null;
            return;
        }

        if (!_modeViews.TryGetValue(mode, out var view))
        {
            view = mode.CreateView();
            _modeViews[mode] = view;
        }

        ModeHost.Content = view;
    }

    /// <summary>검색 칸으로 간다(Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as MyBatisStatementsViewModel)?.ClearSearch();
        SearchBox.Focus();
    }

    // 창에 포커스가 있을 때: Ctrl+F = 검색 칸, Esc = 검색 지움, 검색 칸에서 ↓ = 목록 첫 행, Enter = 고른(없으면 첫) 쿼리 열기.
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MyBatisStatementsViewModel vm || vm.IsModeActive)
        {
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            FocusSearch();
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
            if (vm.FirstItem() is { } first)
            {
                vm.SelectNode(first);
                Tree.ScrollIntoView(first);
                Dispatcher.UIThread.Post(() => Tree.ContainerFromItem(first)?.Focus(), DispatcherPriority.Loaded);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && SearchBox.IsFocused)
        {
            vm.FlushSearch();
            vm.OpenNode(vm.SelectedNode is { IsGroup: false } picked ? picked : vm.FirstItem());
            e.Handled = true;
        }
    }

    /// <summary>폴더 경로는 파일 이름 아래 흐린 줄에 보이고, 좁으면 앞을 줄인다(…끝쪽 폴더가 남는다).</summary>
    private void UpdateDirectoryText()
    {
        var full = (DataContext as MyBatisStatementsViewModel)?.MapperDirectory ?? string.Empty;
        var box = DirectoryText;
        double width = HeaderBorder.Bounds.Width - HeaderBorder.Padding.Left - HeaderBorder.Padding.Right;
        if (full.Length == 0 || width <= 0)
        {
            box.Text = full;
            return;
        }

        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
        double Measure(string text) => new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, box.FontSize, null).Width;

        if (Measure(full) <= width)
        {
            box.Text = full;
            return;
        }

        int lo = 1, hi = full.Length - 1; // 앞에서 lo 글자를 버린 "…" + 나머지
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Measure("…" + full[mid..]) <= width)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        box.Text = "…" + full[lo..];
    }

    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is MyBatisStatementsViewModel vm && NodeAt(e.Source) is { } node)
        {
            vm.SelectNode(node); // 묶음 머리면 고른 문장을 비운다
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (NodeAt(e.Source) is not { IsGroup: true } group || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 1)
        {
            group.IsExpanded = !group.IsExpanded;
        }

        e.Handled = true;
    }

    private static MyBatisStatementNode? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MyBatisStatementNode;
}
