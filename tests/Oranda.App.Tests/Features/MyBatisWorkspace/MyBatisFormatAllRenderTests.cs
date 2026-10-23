using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Format;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;
using Oranda.Core.Shell;
using Oranda.Language;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 실제 MyBatis 쿼리 편집기의 문서 도구 막대에 「매퍼 전체 포맷」 아이콘 단추(툴팁 = 제목)가 「쿼리 포맷」 바로 뒤에 있고,
/// 실포매터로 매퍼 전체를 정리한 결과 알림이 일반 포맷과 같은 메시지 상자로 뜬다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 편집기(포맷 전후)와 결과 메시지 상자를 라이트·다크 PNG 로 뽑는다(렌더 점검용, 저장소 밖 폴더).
/// </summary>
public sealed class MyBatisFormatAllRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbfmtall-render-" + Guid.NewGuid().ToString("N"));

    public MyBatisFormatAllRenderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public Task Format_all_button_follows_format_query_and_its_result_reads_in_the_message_box() => Ui.Run(async () =>
    {
        using var harness = new ShellHarness();
        var hooks = new MyBatisHooks(harness.Services);
        var path = Path.Combine(_root, "EmpMapper.xml");
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
        var host = new SqlEditorHost { MyBatisHooks = hooks };
        Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
        var session = Assert.IsType<MyBatisWorkspaceSession>(host.ActiveWorkspace!.MyBatis);
        var tab = session.OpenStatement("selectEmpList")!;
        host.Active = tab;
        var view = Assert.IsType<MyBatisStatementEditorView>(hooks.EditorViewFactory!.CreateEditor(tab, session));
        var window = new Window { Width = 900, Height = 620, Content = view };
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var buttons = view.Toolbar!.Buttons.Select(b => (string)b.Tag!).ToList();
            Assert.Equal(buttons.IndexOf(MyBatisFormatCommands.FormatStatement) + 1, buttons.IndexOf(MyBatisFormatAllCommands.FormatAll));
            var button = view.Toolbar.Buttons.Single(b => (string)b.Tag! == MyBatisFormatAllCommands.FormatAll);
            Assert.IsType<Viewbox>(button.Content); // 글자 단추가 아니라 아이콘
            var title = harness.Get<IShellText>().Title(harness.Get<ICommandRegistry>().Find(MyBatisFormatAllCommands.FormatAll)!);
            Assert.Equal("매퍼 전체 포맷", title);
            Assert.Equal(title, ToolTip.GetTip(button));
            Render(window, dir, "mybatis-formatall-before", 900, 620);

            var before = tab.Text;
            var notices = new List<string>();
            var handler = new MyBatisFormatAllCommands
            {
                Host = host,
                Formatter = new FailSome(RealFormatter(), ["insertEmp", "updateEmpSal"]),
                OptionsProvider = _ => Task.FromResult(FormatterOptions.OrandaDefault),
                Notify = m =>
                {
                    notices.Add(m);
                    return Task.CompletedTask;
                },
            };
            await handler.ExecuteAsync(new CommandContext(MyBatisFormatAllCommands.FormatAll, FakeContextKeys.AllTrue), default);
            Dispatcher.UIThread.RunJobs();

            var notice = Assert.Single(notices);
            Assert.StartsWith("매퍼 전체 포맷을 마쳤습니다: ", notice, StringComparison.Ordinal);
            Assert.Contains("원문 유지.\n\n원문을 유지한 쿼리:\n· insertEmp: 포맷할 수 없는 MyBatis 태그 모양입니다", notice, StringComparison.Ordinal);
            Assert.NotEqual(before, tab.Text);
            Assert.True(tab.IsDirty);
            Assert.True(session.IsDirty);
            Render(window, dir, "mybatis-formatall-after", 900, 620);

            // 결과는 일반 포맷 알림과 같은 메시지 상자로 뜬다.
            var shown = ConnectionDialogs.MessageAsync(window, notice);
            Dispatcher.UIThread.RunJobs();
            var box = Assert.Single(window.OwnedWindows);
            Assert.Contains(box.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == notice);
            Render(box, dir, "mybatis-formatall-message", (int)Math.Ceiling(box.Bounds.Width), (int)Math.Ceiling(box.Bounds.Height));
            box.Close();
            await shown;
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            window.Close();
        }
    });

    // 시험 어셈블리는 Language 를 참조하지만 구현 형식 이름에 묶이지 않게 계약으로 찾는다.
    private static IMyBatisStatementFormatter RealFormatter()
    {
        var type = typeof(LanguageAssembly).Assembly.GetTypes()
            .Single(t => !t.IsAbstract && typeof(IMyBatisStatementFormatter).IsAssignableFrom(t));
        return (IMyBatisStatementFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    /// <summary>고른 id 의 요소만 실패(태그 모양)로 돌려주고 나머지는 실포매터에 맡긴다(결과 알림의 사유 목록을 보이려고).</summary>
    private sealed class FailSome(IMyBatisStatementFormatter inner, string[] ids) : IMyBatisStatementFormatter
    {
        public FormatResult Format(string text, FormatterOptions o, Oranda.Core.Text.TextRange? range = null) =>
            ids.Any(id => text.Contains($"id=\"{id}\"", StringComparison.Ordinal))
                ? new FormatResult(text, new Oranda.Core.Text.TextRange(0, text.Length), FormatFailure.MarkupUnsupported)
                : inner.Format(text, o, range);
    }

    private static void Render(Window window, string? dir, string stage, int width, int height)
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
            using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(width, 1), Math.Max(height, 1)));
            bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, $"{stage}-{name}.png"));
#pragma warning restore CS0618
        }
    }
}
