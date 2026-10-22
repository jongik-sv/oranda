using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Oranda.App.Features.ConnectionImport;

/// <summary>접속 내보내기·가져오기 고르기 창. 확인이면 true(고른 접속은 <see cref="ConnectionPickViewModel.Chosen"/>).</summary>
public partial class ConnectionPickDialog : Window
{
    public ConnectionPickDialog()
    {
        InitializeComponent();
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary><paramref name="owner"/> 위에 모달로 띄운다. 확인이면 true.</summary>
    public static Task<bool> ShowAsync(Window owner, ConnectionPickViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new ConnectionPickDialog { DataContext = model }.ShowDialog<bool>(owner);
    }
}
