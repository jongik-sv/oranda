using Avalonia.Controls;

namespace Oranda.App.Features.Rollback;

public partial class RollbackScriptView : UserControl
{
    public RollbackScriptView()
    {
        InitializeComponent();
        RunButton.Click += async (_, _) =>
        {
            if (DataContext is RollbackScriptViewModel vm)
            {
                await vm.RunAsync();
            }
        };
        CancelRunButton.Click += (_, _) => (DataContext as RollbackScriptViewModel)?.Cancel();
        CopyButton.Click += (_, _) => (DataContext as RollbackScriptViewModel)?.CopyToEditor();
        ExportButton.Click += async (_, _) =>
        {
            if (DataContext is RollbackScriptViewModel vm)
            {
                await vm.ExportAsync();
            }
        };
    }
}
