using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oranda.App.Features.Execution;

/// <summary>바인드 변수 창 View. 실행이 새 변수를 만나면 그 줄의 값 칸으로 포커스를 옮긴다(창이 늦게 만들어져도).</summary>
public partial class BindVariablesView : UserControl
{
    private BindVariablesViewModel? _vm;

    public BindVariablesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Store.FocusRequested -= OnFocusRequested;
            }

            _vm = DataContext as BindVariablesViewModel;
            if (_vm is not null)
            {
                _vm.Store.FocusRequested += OnFocusRequested;
                if (_vm.Store.PendingFocus is { } pending)
                {
                    Dispatcher.UIThread.Post(() => FocusRow(pending), DispatcherPriority.Background);
                }
            }
        };
    }

    private void OnFocusRequested(object? sender, string name) =>
        Dispatcher.UIThread.Post(() => FocusRow(name), DispatcherPriority.Background);

    /// <summary>이름이 같은 줄(빈 이름이면 마지막 빈 줄)을 고르고 값 칸(빈 이름이면 이름 칸)에 포커스를 둔다.</summary>
    private void FocusRow(string name)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        var row = name.Length == 0
            ? _vm.Rows.LastOrDefault(r => r.Name.Trim().Length == 0)
            : _vm.Store.Find(name);
        if (row is null)
        {
            return;
        }

        _vm.Store.PendingFocus = null;
        _vm.Selected = row;
        RowList.ScrollIntoView(row);
        RowList.UpdateLayout();
        if (RowList.ContainerFromItem(row) is not Control container)
        {
            return;
        }

        var boxName = name.Length == 0 ? "NameBox" : "ValueBox";
        if (container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == boxName) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }
}
