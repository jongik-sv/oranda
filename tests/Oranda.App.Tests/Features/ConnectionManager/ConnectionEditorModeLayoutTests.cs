using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>
/// 접속 방식(호스트·포트 / TNS / 사용자 지정)을 붙은 단추로 고르고, 방식마다 그 방식이 쓰는 칸만 보인다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 방식마다 라이트·다크 PNG(<c>conn-editor-{방식}-{톤}.png</c>)를 쓴다.
/// </summary>
public sealed class ConnectionEditorModeLayoutTests : IDisposable
{
    private readonly string _tns = Directory.CreateTempSubdirectory("oranda-tns-").FullName;

    public void Dispose() => Directory.Delete(_tns, true);

    [Fact]
    public Task Mode_buttons_switch_the_fields_and_each_mode_shows_only_its_own_fields() => HeadlessHost.Run(async () =>
    {
        File.WriteAllText(Path.Combine(_tns, "tnsnames.ora"), "SALES = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521)) (CONNECT_DATA = (SID = SALES)))\n");
        using var fx = new ServiceFixture();
        await fx.Service.SaveProfileAsync(new ConnectionProfile("sales", "scott", "SALES", TnsAdmin: _tns, Group: "dev"), "pw", true, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        vm.Selected = vm.Tree[0].Children[0];
        var window = new ConnectionManagerWindow { DataContext = vm };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var editor = window.FindControl<ProfileEditorView>("EditorView")!;
            var modes = editor.FindControl<ListBox>("ModeBox")!;
            Assert.Equal(ConnectionMode.TnsAlias, vm.Editor!.Mode);
            Assert.Equal(1, modes.SelectedIndex); // 호스트·포트 / TNS / 사용자 지정
            Visible(editor, "TnsAdminBox", "TnsAliasBox", "WalletBox");
            Hidden(editor, "HostBox", "EasyConnectBox");
            Shots(window, "tns");

            modes.SelectedIndex = 0; // 단추를 누르면 방식이 바뀐다
            vm.Editor.Host = "db.example.com";
            vm.Editor.Service = "sales.example.com";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ConnectionMode.HostPortService, vm.Editor.Mode);
            Visible(editor, "HostBox", "PortBox", "ServiceBox", "TlsBox");
            Hidden(editor, "TnsAdminBox", "TnsAliasBox", "WalletBox", "EasyConnectBox");
            Shots(window, "host");

            modes.SelectedIndex = 2;
            vm.Editor.EasyConnect = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db.example.com)(PORT=1521))(CONNECT_DATA=(SID=SALES)))";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ConnectionMode.EasyConnect, vm.Editor.Mode);
            Visible(editor, "EasyConnectBox", "WalletBox");
            Hidden(editor, "HostBox", "TnsAdminBox", "TnsAliasBox");
            Shots(window, "custom");
        }
        finally
        {
            window.Close();
        }
    });

    private static void Visible(ProfileEditorView editor, params string[] names)
    {
        foreach (var n in names)
        {
            Assert.True(editor.FindControl<Control>(n)!.IsEffectivelyVisible, n + " 가 보여야 한다");
        }
    }

    private static void Hidden(ProfileEditorView editor, params string[] names)
    {
        foreach (var n in names)
        {
            Assert.False(editor.FindControl<Control>(n)!.IsEffectivelyVisible, n + " 는 숨어야 한다");
        }
    }

    private static void Shots(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var size = new Avalonia.PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"conn-editor-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
