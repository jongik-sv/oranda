using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        AttachedToVisualTree += (_, _) => (DataContext as MyBatisStatementsViewModel)?.Attach();
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
