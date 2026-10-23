using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

public partial class MyBatisStatementsView : UserControl
{
    private const string ContextSurface = "mybatisStatements";

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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MyBatisStatementsViewModel model)
            {
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MyBatisStatementsViewModel.MapperDirectory))
                    {
                        UpdateDirectoryText();
                    }
                };
                UpdateDirectoryText();
            }
        };
        AttachedToVisualTree += (_, _) => (DataContext as MyBatisStatementsViewModel)?.Attach();
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
