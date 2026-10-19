using Avalonia.Controls;

namespace Oranda.App.Features.Execution;

public partial class ResultTabsView : UserControl
{
    public ResultTabsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyEnvironment();
    }

    private void ApplyEnvironment()
    {
        // 색은 App.axaml 의 env-frame 스타일(테마 리소스)에서만 받는다.
        var vm = DataContext as ResultTabsViewModel;
        var env = vm?.Profile?.Environment;
        BannerBorder.Classes.Set("env-frame", env is not null);
        BannerBorder.Classes.Set("env-production", env == Oranda.Core.Connection.ConnectionEnvironment.Production);
        BannerBorder.Classes.Set("env-test", env == Oranda.Core.Connection.ConnectionEnvironment.Test);
        BannerBorder.Classes.Set("env-development", env == Oranda.Core.Connection.ConnectionEnvironment.Development);
    }
}
