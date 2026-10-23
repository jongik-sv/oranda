using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Format;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;
using Oranda.Core.Shell;
using Oranda.Language;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 실제 MyBatis 쿼리 편집기에서 실포매터로 「쿼리 포맷」 을 돌린다: 동적 태그가 든 문장이 바뀌고 태그 원문은 그대로이며, 실행 취소 한 번에 돌아온다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 포맷 전후 편집기 화면을 라이트·다크 PNG 로 뽑는다(렌더 점검용, 저장소 밖 폴더).
/// </summary>
public sealed class MyBatisFormatRenderTests
{
    private const string Id = "selectEmpList";

    // 포맷 뒤에도 원문 그대로 남아야 하는 태그·자리 글.
    private static readonly string[] Markup =
    [
        "<select id=\"selectEmpList\" parameterType=\"map\" resultMap=\"empMap\">",
        "<include refid=\"empCols\"/>",
        "<where>",
        "<if test=\"ename != null and ename != ''\">",
        "<foreach collection=\"deptIds\" item=\"id\" open=\"(\" separator=\",\" close=\")\">",
        "#{ename}",
        "${orderBy}",
        "</select>",
    ];

    [Fact]
    public Task Real_formatter_formats_the_statement_in_the_mybatis_editor() => HeadlessHost.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "oranda-mbfmt-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "EmpMapper.xml");
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
        var factory = new MyBatisEditorViewFactory();
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(editorViewFactory: factory) };
        Window? window = null;
        try
        {
            Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
            var session = Assert.IsType<MyBatisWorkspaceSession>(host.ActiveWorkspace!.MyBatis);
            var tab = session.OpenStatement(Id)!;
            host.Active = tab;
            var view = Assert.IsType<MyBatisStatementEditorView>(factory.CreateEditor(tab, session));
            window = new Window { Width = 900, Height = 620, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
            Render(window, dir, "before");

            var before = tab.Text;
            var notices = new List<string>();
            var handler = new MyBatisFormatCommands
            {
                Host = host,
                Formatter = RealFormatter(),
                OptionsProvider = _ => Task.FromResult(FormatterOptions.OrandaDefault),
                Notify = m =>
                {
                    notices.Add(m);
                    return Task.CompletedTask;
                },
            };

            await handler.ExecuteAsync(new CommandContext(MyBatisFormatCommands.FormatStatement, FakeContextKeys.AllTrue), default);
            Dispatcher.UIThread.RunJobs();

            Assert.True(notices.Count == 0, string.Join(Environment.NewLine, notices));
            Assert.NotEqual(before, tab.Text);
            foreach (var markup in Markup)
            {
                Assert.Contains(markup, tab.Text, StringComparison.Ordinal);
            }

            Render(window, dir, "after");

            tab.Document.UndoStack.Undo();
            Assert.Equal(before, tab.Text);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            window?.Close();
            Directory.Delete(root, recursive: true);
        }
    });

    // 시험 어셈블리는 Language 를 참조하지만 구현 형식 이름에 묶이지 않게 계약으로 찾는다.
    private static IMyBatisStatementFormatter RealFormatter()
    {
        var type = typeof(LanguageAssembly).Assembly.GetTypes()
            .Single(t => !t.IsAbstract && typeof(IMyBatisStatementFormatter).IsAssignableFrom(t));
        return (IMyBatisStatementFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private static void Render(Window window, string? dir, string stage)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var bmp = new RenderTargetBitmap(new PixelSize(900, 620));
            bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, $"mybatis-format-{stage}-{name}.png"));
#pragma warning restore CS0618
        }
    }
}
