using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.Templates;

/// <summary>
/// 템플릿 창 화면. 키보드만으로 찾기 → 목록 → 삽입이 된다: 찾기 칸에서 ↓ 는 목록 첫 항목으로, Enter 는 고른(없으면 첫) 항목 삽입.
/// 목록에서 Enter·두 번 누르기 = 삽입, F2 = 편집, Delete = 지우기(사용자 조각만), Esc = 찾기 칸으로. 항목을 끌어 편집기에 놓으면
/// 삽입될 글자(자리 표시자는 기본값)가 들어간다. 오른쪽 클릭 메뉴: 삽입, 본문 복사, 사용자 조각으로 복제, 편집·지우기.
/// </summary>
public partial class TemplatesView : UserControl
{
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private TemplateItemRow? _pressRow;
    private bool _dragging;

    public TemplatesView()
    {
        InitializeComponent();
        Preview.TextEditorControl.ShowLineNumbers = false;
        Preview.TextEditorControl.WordWrap = true;

        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(PointerPressedEvent, OnListPressed, RoutingStrategies.Tunnel);
        List.PointerMoved += OnListMoved;
        List.DoubleTapped += OnListDoubleTapped;
        List.ContextMenu = BuildContextMenu();

        NewButton.Click += (_, _) => Run(vm => vm.NewSnippetAsync());
        InsertButton.Click += (_, _) => Vm?.InsertSelected();
        DuplicateButton.Click += (_, _) => Run(vm => vm.DuplicateSelectedAsync());
        EditButton.Click += (_, _) => Run(vm => vm.EditSelectedAsync());
        DeleteButton.Click += (_, _) => Run(vm => vm.ConfirmAndDeleteAsync());

        DataContextChanged += async (_, _) =>
        {
            if (DataContext is TemplatesViewModel vm)
            {
                vm.ShowEditor = ShowEditorAsync;
                await vm.LoadUserAsync().ConfigureAwait(true);
            }
        };
    }

    private TemplatesViewModel? Vm => DataContext as TemplatesViewModel;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                if (vm.SelectFirst())
                {
                    FocusList();
                }

                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                if (vm.SelectFirst())
                {
                    vm.InsertSelected();
                }

                e.Handled = true;
                break;
            case Key.Escape when vm.Query.Length > 0:
                vm.Query = string.Empty;
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when vm.SelectedRow is TemplateGroupRow group:
                TemplatesViewModel.Toggle(group);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.InsertSelected();
                e.Handled = true;
                break;
            case Key.F2:
                Run(v => v.EditSelectedAsync());
                e.Handled = true;
                break;
            case Key.Delete when vm.IsUserSelected:
                Run(v => v.ConfirmAndDeleteAsync());
                e.Handled = true;
                break;
            case Key.Escape:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 묶음 머리를 두 번 누르면 TreeViewItem 이 접고 편다. 항목이면 삽입.
        if (Vm is { SelectedRow: TemplateItemRow } vm && RowAt(e.Source) is TemplateItemRow)
        {
            vm.InsertSelected();
            e.Handled = true;
        }
    }

    private void OnListPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressArgs = e;
        _pressPoint = e.GetPosition(List);
        _pressRow = RowAt(e.Source) as TemplateItemRow;
        _dragging = false;

        // 오른쪽 단추는 누른 행을 먼저 고른다(메뉴가 그 행에 대해 열린다).
        if (RowAt(e.Source) is { } row && e.GetCurrentPoint(List).Properties.IsRightButtonPressed && Vm is { } vm)
        {
            vm.SelectedRow = row;
        }
    }

    private async void OnListMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _pressRow is not { } row || _pressArgs is null || !e.GetCurrentPoint(List).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var d = e.GetPosition(List) - _pressPoint;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6)
        {
            return;
        }

        _dragging = true;
        try
        {
            // 편집기는 일반 텍스트 끌어 놓기로 받는다(자리 표시자는 기본값으로 풀린 글자).
            var data = new Avalonia.Input.DataTransfer();
            data.Add(DataTransferItem.CreateText(row.Snippet.Example));
            await DragDrop.DoDragDropAsync(_pressArgs, data, DragDropEffects.Copy).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // async void 라서 여기서 못 잡으면 앱이 죽는다(Windows OLE 는 COMException 을 던진다, TSK-07-05).
            if (Vm is { } vm)
            {
                vm.Status = ex.Message;
            }
        }
        finally
        {
            _pressRow = null;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            if (Vm is not { Selected: { } } vm)
            {
                e.Cancel = true;
                return;
            }

            MenuItem Item(string key, string fallback, string icon, bool enabled, Action action)
            {
                var text = UiText.Get(key, fallback);
                var item = new MenuItem { Header = text, Icon = Shell.Icons.ShellIcons.Create(icon), IsEnabled = enabled };
                Avalonia.Automation.AutomationProperties.SetName(item, text);
                item.Click += (_, _) => action();
                return item;
            }

            menu.ItemsSource = new object[]
            {
                Item("Templates.menu.insert", "삽입", "tabAdd", true, () => vm.InsertSelected()),
                Item("Templates.menu.copy", "본문 복사", "copy", true, CopyBody),
                Item("Templates.menu.duplicate", "사용자 조각으로 복제", "add", true, () => Run(v => v.DuplicateSelectedAsync())),
                new Separator(),
                Item("Templates.menu.edit", "편집", "docEdit", vm.IsUserSelected, () => Run(v => v.EditSelectedAsync())),
                Item("Templates.menu.delete", "지우기", "delete", vm.IsUserSelected, () => Run(v => v.ConfirmAndDeleteAsync())),
            };
        };
        return menu;
    }

    private async void CopyBody()
    {
        if (Vm is { Selected: { } s } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(s.Example).ConfigureAwait(true);
            vm.Status = UiText.Get("Templates.status.copied", "본문을 복사했다.");
        }
    }

    private async Task<bool> ShowEditorAsync(SnippetEditViewModel edit)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        var window = new SnippetEditWindow { DataContext = edit };
        var saved = await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        if (saved)
        {
            FocusList();
        }

        return saved;
    }

    private void FocusList()
    {
        if (Vm?.SelectedRow is { } row && List.TreeContainerFromItem(row) is TreeViewItem item)
        {
            item.Focus();
        }
        else
        {
            List.Focus();
        }
    }

    private static TemplateRow? RowAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is TreeViewItem { DataContext: TemplateRow row })
            {
                return row;
            }
        }

        return null;
    }

    private async void Run(Func<TemplatesViewModel, Task> action)
    {
        if (Vm is { } vm)
        {
            await action(vm).ConfigureAwait(true);
        }
    }
}
