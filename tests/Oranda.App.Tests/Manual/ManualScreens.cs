using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.ConnectionManager;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 화면 캡처 생성기(쿼리가 아닌 화면). 실제 View 를 그리되 데이터는 지어낸 예시(접속 이름 <c>dev-example</c>, 호스트 <c>db.example.com</c>)만 쓴다.
/// <c>ORANDA_MANUAL_CAPTURE=1</c> 과 <c>ORANDA_RENDER_DIR</c> 가 모두 있을 때만 PNG 를 쓰고, 아니면 바로 끝난다(<see cref="ManualCapture"/>).
/// 캡처마다 번호 표시는 컨트롤 위치에서 계산한다. 번호의 뜻은 본문(<c>site/src/content/docs</c>)의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualScreens
{
    /// <summary>예시 접속 4개를 담은 접속 관리자 창을 연다(첫 접속을 고른 상태). 호출한 쪽이 창을 닫는다.</summary>
    private static async Task<(ConnectionManagerWindow Window, ServiceFixture Fixture)> OpenConnectionManagerAsync()
    {
        var fx = new ServiceFixture();
        foreach (var (name, user, source, group, env) in new[]
        {
            ("dev-example", "app_dev", "db.example.com:1521/devsvc", "개발", ConnectionEnvironment.Development),
            ("test-example", "app_test", "db.example.com:1521/testsvc", "개발", ConnectionEnvironment.Test),
            ("sample", "sample", "localhost:1521/sample", "예시", ConnectionEnvironment.Development),
            ("prod-example", "app_read", "prod.example.com:1521/prodsvc", "운영", ConnectionEnvironment.Production),
        })
        {
            await fx.Service.SaveProfileAsync(new ConnectionProfile(name, user, source, Environment: env, Group: group, Comment: name == "dev-example" ? "개발 서버 예시" : null), "pw", true, CancellationToken.None);
        }

        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        vm.Selected = vm.Tree[0].Children[0];
        var window = new ConnectionManagerWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, fx);
    }

    private static T In<T>(Window window, string name)
        where T : Control =>
        ManualCapture.Find<T>(window, c => c.Name == name);

    [Fact]
    public Task Connection_manager() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (window, fx) = await OpenConnectionManagerAsync();
        using (fx)
        {
            ManualCapture.Save(window, "connection-manager",
            [
                new(In<Button>(window, "AddButton"), 1),
                new(In<TextBox>(window, "SearchBox"), 2),
                new(In<TreeView>(window, "ConnectionTree"), 3),
                new(In<ProfileEditorView>(window, "EditorView"), 4),
                new(In<TextBox>(window, "PreviewBox"), 5),
                new(In<Button>(window, "TestButton"), 6),
                new(In<Button>(window, "SaveButton"), 7),
                new(In<Button>(window, "ConnectButton"), 8),
            ]);
            window.Close();
        }
    });

    /// <summary>접속 편집 영역의 변형(같은 창, 다른 상태). 번호는 본문의 번호별 설명과 같아야 한다.</summary>
    [Fact]
    public Task Connection_editor_variants() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (window, fx) = await OpenConnectionManagerAsync();
        using (fx)
        {
            var editorModel = ((ConnectionManagerViewModel)window.DataContext!).Editor!;
            T E<T>(string name)
                where T : Control => In<T>(window, name);

            ManualCapture.Save(window, "connection-editor",
            [
                new(E<TextBox>("NameBox"), 1),
                new(E<ListBox>("ModeBox"), 2),
                new(E<TextBox>("HostBox"), 3),
                new(E<TextBox>("ServiceBox"), 4),
                new(E<CheckBox>("TlsBox"), 5),
                new(E<TextBox>("UserBox"), 6),
                new(E<ComboBox>("AuthBox"), 7),
                new(E<TextBox>("PasswordBox"), 8),
            ]);

            ManualCapture.Save(window, "connection-editor-general",
            [
                new(E<AutoCompleteBox>("GroupBox"), 1),
                new(E<ComboBox>("EnvironmentBox"), 2),
                new(E<DropDownButton>("ColorButton"), 3),
                new(E<TextBox>("CommentBox"), 4),
            ]);

            editorModel.Mode = ConnectionMode.TnsAlias;
            editorModel.TnsAdmin = "/opt/oracle/network/admin";
            editorModel.TnsAlias = "DEVDB_EXAMPLE";
            editorModel.TnsAliasDetail = "→ db.example.com:1521/devsvc";
            Dispatcher.UIThread.RunJobs();
            ManualCapture.Save(window, "connection-editor-tns",
            [
                new(E<TextBox>("TnsAdminBox"), 1),
                new(E<Button>("TnsAdminBrowseButton"), 2),
                new(E<ComboBox>("TnsAliasBox"), 3),
                new(E<TextBlock>("TnsAliasDetail"), 4),
                new(E<TextBox>("WalletBox"), 5),
                new(E<Button>("WalletBrowseButton"), 6),
            ]);

            editorModel.Mode = ConnectionMode.HostPortService;
            E<Expander>("AdvancedExpander").IsExpanded = true;
            window.Height = 980;
            Dispatcher.UIThread.RunJobs();
            ManualCapture.Save(window, "connection-editor-advanced",
            [
                new(E<Expander>("AdvancedExpander"), 1),
                new(E<TextBox>("ProxyBox"), 2),
                new(E<CheckBox>("ReadOnlyBox"), 3),
                new(E<ComboBox>("PackBox"), 4),
                new(E<NumericUpDown>("TimeoutBox"), 5),
                new(E<NumericUpDown>("KeepAliveBox"), 6),
                new(E<TextBox>("NlsDateBox"), 7),
                new(E<TextBox>("NlsLanguageBox"), 8),
            ]);
            window.Close();
        }
    });

    [Fact]
    public Task Settings_general() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var settings = new SettingsService(new MemorySettingsStore());
        var text = new ShellText(language: "ko");
        var vm = new SettingsViewModel(settings, text);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();

        var tabs = window.FindControl<TabControl>("Categories")!;
        ManualCapture.Save(window, "settings-general",
        [
            new(tabs, 1),
            new(window.FindControl<ComboBox>("ThemeBox")!, 2),
            new(window.FindControl<ComboBox>("LanguageBox")!, 3),
            new(window.FindControl<NumericUpDown>("UiScaleBox")!, 4),
        ]);
        window.Close();
    });

    /// <summary>쿼리 화면 시험 생성(스파이크): 샘플 DB 에서 실제로 실행한 결과가 들어간 주 화면.</summary>
    [Fact]
    public Task Main_query() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await ManualShell.OpenAsync();
        await shell.ShowOnlySampleSchemaAsync();
        await shell.RunAsync("SELECT d.dept_name, COUNT(*) AS emp_count, ROUND(AVG(e.salary)) AS avg_salary\nFROM departments d\nJOIN employees e ON e.dept_id = d.dept_id\nGROUP BY d.dept_name\nORDER BY avg_salary DESC");
        var window = shell.Harness.Window;
        window.Width = 1280;
        window.Height = 800;
        Dispatcher.UIThread.RunJobs();
        var path = ManualCapture.Save(window, "main-query");
        // README 의 주 화면 그림도 같은 캡처다(기본 출력 폴더로 쓸 때만 저장소 안 파일을 갱신한다).
        if (Environment.GetEnvironmentVariable("ORANDA_MANUAL_OUT") is not { Length: > 0 })
        {
            var readmeImage = Path.Combine(ManualSite.RepoRoot, "docs", "images", "screenshot-main.png");
            Directory.CreateDirectory(Path.GetDirectoryName(readmeImage)!);
            File.Copy(path, readmeImage, overwrite: true);
        }
    });
}
