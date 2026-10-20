using System.ComponentModel;
using Avalonia.Controls;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>객체 상세 「데이터」 탭: 툴바(새로 고침·편집·거르기 줄·행 수 세기)와 결과 영역.</summary>
public partial class ObjectDataView : UserControl
{
    private ObjectDataViewModel? _vm;

    public ObjectDataView()
    {
        InitializeComponent();
        RefreshButton.Click += async (_, _) => await (_vm?.RefreshAsync() ?? Task.CompletedTask);
        CancelButton.Click += (_, _) => _vm?.CancelLoad();
        FilterButton.Click += (_, _) => _vm?.Pane.ToggleFilterBar();
        CountButton.Click += async (_, _) => await (_vm?.CountRowsAsync() ?? Task.CompletedTask);

        // 토글의 켜짐은 ViewModel 이 정한다(고칠 수 없으면 꺼진 채로 남는다): 누른 뒤 VM 상태로 되돌린다.
        EditToggle.Click += async (_, _) =>
        {
            if (_vm is { } vm)
            {
                await vm.SetEditingAsync(EditToggle.IsChecked == true);
            }

            SyncToggle();
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
            }

            _vm = DataContext as ObjectDataViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
            }

            SyncToggle();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ObjectDataViewModel.IsEditing))
        {
            SyncToggle();
        }
    }

    private void SyncToggle() => EditToggle.IsChecked = _vm?.IsEditing == true;
}
