using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Oranda.App.Features.HistoryScripts;

public partial class HistoryView : UserControl
{
    /// <summary>고른 문장 상자의 처음 높이(DIP).</summary>
    public const double DefaultDetailHeight = 140;

    /// <summary>고른 문장 상자의 가장 작은 높이(DIP). 목록 쪽 최소는 axaml 의 줄 정의(60).</summary>
    public const double MinDetailHeight = 40;

    private const int DetailRow = 3;

    // 끌어서 바꾼 높이. 앱을 켠 동안만 기억한다(창을 다시 만들어도 이어진다). 다른 곁 패널 안 분할도 높이를 저장하지 않는다.
    private static double s_detailHeight = DefaultDetailHeight;

    private HistoryViewModel? _observed;

    public HistoryView()
    {
        InitializeComponent();
        DetailSplitter.DragCompleted += (_, _) => RememberDetailHeight();
        DataContextChanged += (_, _) => ObserveViewModel();
        ApplyDetailRow();
        RerunButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel { Selected: { } item } vm)
            {
                await vm.RerunAsync(item);
            }
        };
        ToEditorButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel { Selected: { } item } vm)
            {
                await vm.SendToEditorAsync(item);
            }
        };
        DeleteButton.Click += OnDelete;
        ClearButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
            {
                await vm.ClearAsync(vm.CurrentConnectionOnly);
            }
        };
        MoreButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
            {
                await vm.LoadMoreAsync();
            }
        };
        List.DoubleTapped += async (_, _) =>
        {
            if (DataContext is HistoryViewModel { Selected: { } item } vm)
            {
                await vm.SendToEditorAsync(item);
            }
        };
        List.KeyDown += OnListKeyDown;
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
            {
                await vm.ReloadAsync();
            }
        };
    }

    /// <summary>앱을 켠 동안 기억하는 고른 문장 상자 높이(시험용).</summary>
    internal static double RememberedDetailHeight
    {
        get => s_detailHeight;
        set => s_detailHeight = Math.Max(MinDetailHeight, value);
    }

    private RowDefinition DetailRowDefinition => SplitGrid.RowDefinitions[DetailRow];

    private void ObserveViewModel()
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = DataContext as HistoryViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyDetailRow();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryViewModel.Selected))
        {
            ApplyDetailRow();
        }
    }

    // 고른 것이 있으면 기억한 높이로 펼치고, 없으면 줄을 0 으로 접는다(빈 상자가 자리를 차지하지 않게).
    private void ApplyDetailRow()
    {
        var row = DetailRowDefinition;
        if (_observed?.Selected is not null)
        {
            row.MinHeight = MinDetailHeight;
            row.Height = new GridLength(s_detailHeight);
        }
        else
        {
            row.MinHeight = 0;
            row.Height = new GridLength(0);
        }
    }

    private void RememberDetailHeight()
    {
        var row = DetailRowDefinition;
        if (row.ActualHeight > 0)
        {
            RememberedDetailHeight = row.ActualHeight;
            row.Height = new GridLength(s_detailHeight);
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HistoryViewModel vm)
        {
            await vm.DeleteAsync([.. List.SelectedItems?.OfType<HistoryItem>() ?? []]);
        }
    }

    private async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is HistoryViewModel vm)
        {
            await vm.DeleteAsync([.. List.SelectedItems?.OfType<HistoryItem>() ?? []]);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && DataContext is HistoryViewModel { Selected: { } item } vm2)
        {
            await vm2.RerunAsync(item);
            e.Handled = true;
        }
    }
}
