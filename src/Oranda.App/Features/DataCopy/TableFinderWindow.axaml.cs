using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;

namespace Oranda.App.Features.DataCopy;

/// <summary>데이터 복사 「테이블 찾기」 팝업. 「선택한 N개 추가」를 누르면 true 로 닫힌다(고른 표는 모델의 <see cref="TableFinderViewModel.PickedTables"/>).</summary>
public sealed partial class TableFinderWindow : Window
{
    /// <summary>이미 규칙에 있는 줄은 흐리게.</summary>
    public static readonly IValueConverter AddedOpacity = new FuncValueConverter<bool, double>(added => added ? 0.5 : 1.0);

    public TableFinderWindow()
    {
        InitializeComponent();
        AddButton.Click += OnAdd;
        CloseButton.Click += (_, _) => Close(false);
        Opened += async (_, _) =>
        {
            FilterBox.Focus();
            if (DataContext is TableFinderViewModel vm)
            {
                await vm.LoadAsync().ConfigureAwait(true);
            }
        };
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TableFinderViewModel { CanAdd: true })
        {
            Close(true);
        }
    }
}
