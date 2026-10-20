using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Oranda.App.Shell.Keymaps;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// DB 객체로 이동 창. 입력 → 목록 → <c>Enter</c> 열기, <c>Alt+Enter</c>(macOS ⌥↩) 스키마 브라우저에서 찾기, <c>Esc</c> 닫기, <c>↑↓</c> 이동.
/// 모양과 키 처리는 명령 팔레트(<see cref="Shell.Palette.CommandPaletteWindow"/>)를 따른다.
/// </summary>
public partial class QuickOpenObjectWindow : Window
{
    public QuickOpenObjectWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ToolTip.SetTip(RevealButton, KeyBindingTable.ForScreen("Alt+Enter"));
        Opened += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        };
        RevealButton.Click += async (_, _) =>
        {
            if (DataContext is QuickOpenObjectViewModel vm)
            {
                await vm.RevealSelectedAsync();
            }
        };
        ResultList.DoubleTapped += async (_, _) =>
        {
            if (DataContext is QuickOpenObjectViewModel vm)
            {
                await vm.OpenSelectedAsync();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QuickOpenObjectViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QuickOpenObjectViewModel vm)
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
            case Key.PageDown:
                vm.MoveSelection(10);
                e.Handled = true;
                break;
            case Key.PageUp:
                vm.MoveSelection(-10);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
                {
                    await vm.RevealSelectedAsync();
                }
                else
                {
                    await vm.OpenSelectedAsync();
                }

                break;
            case Key.Escape:
                e.Handled = true;
                vm.Cancel();
                break;
        }
    }
}
