using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.GridEdit;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.App.Views;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Testing;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>
/// TSK-07-09·10 화면 눈 확인용 PNG(라이트·다크). 환경 변수 <c>ORANDA_RENDER_DIR</c> 가 있을 때만 파일을 쓴다(없으면 상태 단정만 하고 끝난다).
/// </summary>
[Trait("Category", "Integration")]
[Collection("RoleItestUi")]
public sealed class RoleRenderTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaPumpDelay();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AvaloniaPumpDelay() => Thread.Sleep(30);

    private static void Render(Window window, string name, int width, int height)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    [Fact]
    public Task Settings_features_tab_with_no_role_ticked() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            var vm = new SettingsViewModel(app.Get<SettingsService>(), app.Get<ShellController>().Text, featureGate: app.Gate) { SelectedTab = SettingsViewModel.FeaturesTabIndex };
            vm.Features.UseRoles = true;
            foreach (var r in vm.Features.Roles)
            {
                r.IsChecked = false;
            }

            var window = new SettingsWindow { DataContext = vm };
            window.Show();
            Pump();
            window.UpdateLayout();
            Render(window, "settings-features-no-role", (int)window.Bounds.Width, (int)window.Bounds.Height);
            window.Close();
            await Task.CompletedTask;
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Theory]
    [InlineData("viewer", "status-viewer-role")]
    [InlineData("tuner", "status-tuner-export")]
    public Task Main_window_status_bar(string role, string name) => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, PrdMatrix.FeaturesJson([role]));
            app.Get<IEventBus>().Publish(new SessionOpened(Guid.NewGuid(), TestData.Profile(readOnly: false), TestData.Server(), SessionPurpose.Work));
            await RoleApp.SettleAsync();
            SaveFeedback.ShowExported(120, Path.Combine(dir, "result.xlsx"));
            try
            {
                Pump();
                var window = app.Window;
                window.Width = 1100;
                window.Height = 700;
                Pump();
                window.UpdateLayout();
                Render(window, name, (int)window.Bounds.Width, (int)window.Bounds.Height);
            }
            finally
            {
                SaveFeedback.Clear();
            }
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });
}
