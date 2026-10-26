using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Oranda.App.Shell.Palette;

/// <summary>명령 팔레트 창. 입력 → 목록 → <c>Enter</c>·클릭 실행, <c>Esc</c> 취소, <c>↑↓</c> 이동.</summary>
public partial class CommandPaletteWindow : Window
{
    public CommandPaletteWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ResultList.Tapped += OnResultTapped;
        Opened += (_, _) => QueryBox.Focus();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CommandPaletteViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
    }

    private async void OnResultTapped(object? sender, TappedEventArgs e)
    {
        // 줄 밖(스크롤 막대 등)을 누른 것은 무시한다.
        if (DataContext is not CommandPaletteViewModel vm
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: PaletteItem item })
        {
            return;
        }

        e.Handled = true;
        vm.Selected = item;
        await vm.ExecuteSelectedAsync();
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await vm.ExecuteSelectedAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                vm.Cancel();
                break;
        }
    }
}

/// <summary>팔레트 바인딩용 변환기.</summary>
public static class PaletteConverters
{
    /// <summary>비활성 명령은 흐리게.</summary>
    public static readonly IValueConverter EnabledOpacity = new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);
}
