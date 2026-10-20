using Avalonia.Controls;
using Oranda.App.Shell;

namespace Oranda.App.Features.Update;

/// <summary>업데이트 창. 비모달이며 하나만 뜬다(다시 부르면 앞으로 가져온다).</summary>
public partial class UpdateWindow : Window
{
    private static UpdateWindow? _open;

    public UpdateWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is UpdateViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
        Closed += (_, _) =>
        {
            (DataContext as UpdateViewModel)?.Dispose();
            if (ReferenceEquals(_open, this))
            {
                _open = null;
            }
        };
    }

    /// <summary>열려 있는 업데이트 창(없으면 null). 시험이 살펴본다.</summary>
    internal static UpdateWindow? Open => _open;

    /// <summary>창을 만들어 보인다. 이미 열려 있으면 앞으로 가져오고, 끝난 확인은 다시 돌린다.</summary>
    public static UpdateWindow ShowFor(UpdateService service, ShellController shell)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            _ = (existing.DataContext as UpdateViewModel)?.StartAsync();
            return existing;
        }

        var window = new UpdateWindow { DataContext = new UpdateViewModel(service, shell.Text) };
        _open = window;
        if (shell.MainWindow is { IsVisible: true } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        _ = ((UpdateViewModel)window.DataContext).StartAsync();
        return window;
    }
}
