using Avalonia.Controls;
using Avalonia.Input;
using Oranda.App.Shell.Controls;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>「만들던 새 테이블…」 고르기 창. DataContext = <see cref="TableDraftPickerViewModel"/>.</summary>
public partial class TableDraftPickerWindow : Window
{
    private TableDraftPickerViewModel? _vm;

    public TableDraftPickerWindow()
    {
        InitializeComponent();
        OpenButton.Click += (_, _) => _vm?.OpenSelected();
        DeleteButton.Click += async (_, _) =>
        {
            if (_vm is not null)
            {
                await _vm.DeleteSelectedAsync();
            }
        };
        CloseButton.Click += (_, _) => Close();
        DraftList.DoubleTapped += (_, _) => _vm?.OpenSelected();
        DraftList.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Delete && _vm is not null)
            {
                e.Handled = true;
                await _vm.DeleteSelectedAsync();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.CloseRequested -= OnCloseRequested;
            }

            _vm = DataContext as TableDraftPickerViewModel;
            if (_vm is not null)
            {
                _vm.CloseRequested += OnCloseRequested;
            }
        };
    }

    /// <summary>주 창 위에 띄우고 고른 저장분을 돌려준다(닫으면 null). 주 창이 없으면 null.</summary>
    internal static async Task<TableDraftEntry?> ShowAsync(TableDraftPickerViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (DialogShell.MainWindow is not { } owner)
        {
            return null;
        }

        var window = new TableDraftPickerWindow { DataContext = vm };
        await window.ShowDialog(owner).ConfigureAwait(true);
        return vm.Chosen;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
