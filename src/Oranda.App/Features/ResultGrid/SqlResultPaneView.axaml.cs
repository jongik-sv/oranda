using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.ResultGrid;

public partial class SqlResultPaneView : UserControl
{
    private bool _syncing;
    private SqlResultPaneViewModel? _bound;

    public SqlResultPaneView()
    {
        InitializeComponent();
        GridControl.ScrollChanged += (_, _) => SyncBars();
        GridControl.SizeChanged += (_, _) => SyncBars();
        VBar.Scroll += (_, e) =>
        {
            if (!_syncing)
            {
                GridControl.ScrollTo(GridControl.OffsetX, e.NewValue);
            }
        };
        HBar.Scroll += (_, e) =>
        {
            if (!_syncing)
            {
                GridControl.ScrollTo(e.NewValue, GridControl.OffsetY);
            }
        };
        FindNextButton.Click += async (_, _) => await FindNextAsync();
        FindCloseButton.Click += (_, _) => CloseFind();
        FindBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await FindNextAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseFind();
            }
        };
        DataContextChanged += (_, _) => Bind();
    }

    private SqlResultPaneViewModel? Vm => DataContext as SqlResultPaneViewModel;

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnVmPropertyChanged;
            _bound = null;
        }

        if (Vm is not { } vm)
        {
            GridControl.Model = null;
            return;
        }

        _bound = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        AttachModel(vm);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not SqlResultPaneViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(SqlResultPaneViewModel.Model))
        {
            AttachModel(vm);
        }
        else if (e.PropertyName == nameof(SqlResultPaneViewModel.IsFindVisible) && vm.IsFindVisible)
        {
            FindBox.Focus();
        }
    }

    private void AttachModel(SqlResultPaneViewModel vm)
    {
        GridControl.Model = vm.Model;
        if (vm.Model is { } m)
        {
            m.ClipboardWriter = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
                {
                    await cb.SetTextAsync(text);
                }
            };
        }

        SyncBars();
    }

    private async Task FindNextAsync()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        await vm.FindNextAsync();
        if (vm.Model is { HasSelection: true } m)
        {
            GridControl.EnsureVisible(m.Active);
        }
    }

    private void CloseFind()
    {
        if (Vm is { } vm)
        {
            vm.IsFindVisible = false;
        }

        GridControl.Focus();
    }

    private void SyncBars()
    {
        _syncing = true;
        try
        {
            VBar.Maximum = GridControl.MaxOffsetY;
            VBar.ViewportSize = GridControl.ViewportHeight;
            VBar.LargeChange = GridControl.ViewportHeight;
            VBar.SmallChange = GridControl.RowHeight;
            VBar.Value = GridControl.OffsetY;
            VBar.IsVisible = GridControl.MaxOffsetY > 0;
            HBar.Maximum = GridControl.MaxOffsetX;
            HBar.ViewportSize = GridControl.ViewportWidth;
            HBar.LargeChange = GridControl.ViewportWidth;
            HBar.SmallChange = 40;
            HBar.Value = GridControl.OffsetX;
            HBar.IsVisible = GridControl.MaxOffsetX > 0;
        }
        finally
        {
            _syncing = false;
        }
    }
}
