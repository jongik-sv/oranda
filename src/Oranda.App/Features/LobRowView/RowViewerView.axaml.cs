using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.LobRowView;

public partial class RowViewerView : UserControl
{
    public RowViewerView()
    {
        InitializeComponent();
        FirstButton.Click += (_, _) => Vm?.MoveFirst();
        PrevButton.Click += (_, _) => Vm?.MoveRow(-1);
        NextButton.Click += (_, _) => Vm?.MoveRow(1);
        LastButton.Click += (_, _) => Vm?.MoveLast();
        CopyValueItem.Click += async (_, _) => await CopyAsync(Vm?.SelectedValueText());
        CopyAllItem.Click += async (_, _) => await CopyAsync(Vm?.AllFieldsText());
        FieldList.KeyDown += OnFieldKeyDown;
        FieldList.DoubleTapped += (_, _) => RowViewerViewModel.BeginEdit(Vm?.SelectedField);
        // 값 입력칸: Enter 반영, Esc 취소, 포커스를 잃으면 반영.
        FieldList.AddHandler(KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        FieldList.AddHandler(LostFocusEvent, OnEditorLostFocus);
    }

    private RowViewerViewModel? Vm => DataContext as RowViewerViewModel;

    private async void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        if (e.Key == Key.C && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            await CopyAsync(Vm?.SelectedValueText());
        }
        else if (e.Key is Key.F2 or Key.Enter)
        {
            e.Handled = true;
            RowViewerViewModel.BeginEdit(Vm?.SelectedField);
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { DataContext: RowField f })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitEdit(f);
            FieldList.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RowViewerViewModel.CancelEdit(f);
            FieldList.Focus();
        }
    }

    private void OnEditorLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: RowField f })
        {
            Vm?.CommitEdit(f);
        }
    }

    private async Task CopyAsync(string? text)
    {
        if (text is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
