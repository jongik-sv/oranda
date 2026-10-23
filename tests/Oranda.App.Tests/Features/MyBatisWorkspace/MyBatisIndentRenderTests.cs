using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 실제 MyBatis 쿼리 편집기에서 들여 쓴 매퍼(<see cref="MyBatisIndentTests.Indented"/>)의 delete·select 탭: 여는 태그와 닫는 태그가 같은 칸(0칸)이다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 탭 화면을 라이트·다크 PNG 로 뽑는다(렌더 점검용, 저장소 밖 폴더).
/// </summary>
public sealed class MyBatisIndentRenderTests
{
    [Theory]
    [InlineData("deleteMoSpc", "delete")]
    [InlineData("selectMoSpc", "select")]
    public Task Indented_mapper_tab_shows_open_and_close_tags_in_the_same_column(string id, string name) => HeadlessHost.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "oranda-mbindent-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "MoMapper.xml");
        await File.WriteAllTextAsync(path, MyBatisIndentTests.Indented);
        var factory = new MyBatisEditorViewFactory();
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(editorViewFactory: factory) };
        Window? window = null;
        try
        {
            Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
            var session = Assert.IsType<MyBatisWorkspaceSession>(host.ActiveWorkspace!.MyBatis);
            var tab = session.OpenStatement(id)!;
            host.Active = tab;
            var view = Assert.IsType<MyBatisStatementEditorView>(factory.CreateEditor(tab, session));
            window = new Window { Width = 900, Height = 420, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var lines = tab.Text.Split('\n');
            Assert.StartsWith("<" + name + " id=\"" + id + "\"", lines[0], StringComparison.Ordinal);
            Assert.Equal("</" + name + ">", lines[^1]);
            Assert.All(lines[1..^1], l => Assert.StartsWith("    ", l, StringComparison.Ordinal)); // 본문은 닫는 태그보다 한 단 안쪽
            Assert.False(tab.IsDirty);

            Render(window, Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR"), name);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            window?.Close();
            Directory.Delete(root, recursive: true);
        }
    });

    private static void Render(Window window, string? dir, string stage)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (variant, theme) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var bmp = new RenderTargetBitmap(new PixelSize(900, 420));
            bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, $"mybatis-indent-{stage}-{theme}.png"));
#pragma warning restore CS0618
        }
    }
}
