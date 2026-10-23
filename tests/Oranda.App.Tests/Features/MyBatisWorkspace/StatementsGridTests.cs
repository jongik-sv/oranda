using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>TSK-03-08: 「매퍼 쿼리」 창을 공용 격자 양식(Styles/GridC)으로 보이고 이름을 바꾼 것.</summary>
public sealed class StatementsGridTests : IDisposable
{
    private const string UserMapper = "C:/Users/jongik.jang/project/oranda/docs/oranda1/CrGalExeRstMapper.xml";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbgrid-" + Guid.NewGuid().ToString("N"));

    public StatementsGridTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly int[] RenderWidths = [340, 480];

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(SqlEditorHost Host, MyBatisStatementsViewModel Vm, MyBatisStatementsView View, Window Window)> ShowAsync(string path)
    {
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var vm = new MyBatisStatementsViewModel { Host = host, Text = new ShellText(), FilterDebounceMilliseconds = 0 };
        var view = new MyBatisStatementsView { DataContext = vm };
        var window = new Window { Width = 340, Height = 560, Content = view };
        window.Show();
        Pump();
        await host.OpenWorkspaceFileAsync(path, default);
        Pump();
        window.UpdateLayout();
        return (host, vm, view, window);
    }

    private string WriteEmp()
    {
        var path = Path.Combine(_root, "EmpMapper.xml");
        File.WriteAllText(path, MapperSamples.EmpMapper);
        return path;
    }

    [Fact]
    public void Kind_badge_text_is_short_per_kind()
    {
        var kinds = new[]
        {
            (MyBatisStatementKind.Select, "SEL"), (MyBatisStatementKind.Insert, "INS"), (MyBatisStatementKind.Update, "UPD"),
            (MyBatisStatementKind.Delete, "DEL"), (MyBatisStatementKind.Sql, "SQL"), (MyBatisStatementKind.ResultMap, "MAP"),
        };
        foreach (var (kind, text) in kinds)
        {
            Assert.Equal(text, new MyBatisStatementNode("x", kind, entry: null).KindText);
        }

        Assert.Equal("···", new MyBatisStatementNode("x", MyBatisStatementKind.Other, entry: null).KindText); // 경계: 그 밖 요소
    }

    [Fact]
    public Task The_list_uses_the_shared_grid_style_with_a_header_and_24_dip_rows() => Ui.Run(async () =>
    {
        var (_, vm, view, window) = await ShowAsync(WriteEmp());
        try
        {
            var list = view.FindControl<ListBox>("Tree")!;
            Assert.Contains("grid-c", list.Classes);
            var headerBorder = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("grid-c-header"));
            var headers = headerBorder.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(["종류", "id", "상태"], headers);

            var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.True(i.MinHeight >= 24));
            Assert.Equal(vm.Rows.Count, vm.Groups.Count + vm.Groups.Sum(g => g.Children.Count)); // 묶음 머리 + 항목, 접힘 없음
            var texts = list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("SEL", texts);
            Assert.Contains("SQL", texts);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Selection_open_and_group_collapse_still_work_in_the_grid() => Ui.Run(async () =>
    {
        var (_, vm, _, window) = await ShowAsync(WriteEmp());
        try
        {
            var select = vm.Groups[0];
            vm.SelectNode(select.Children[1]);
            Assert.Equal(select.Children[1].StatementId, vm.SelectedNode?.StatementId);

            vm.OpenNode(select.Children[1]); // 두 번 누르기·Enter 가 부르는 곳
            Assert.Equal(select.Children[1].StatementId, vm.Host.ActiveWorkspace!.ActiveTab!.Title);

            var before = vm.Rows.Count;
            select.IsExpanded = false; // 묶음 접기, 다시 펼치면 되돌아온다
            Assert.Equal(before - select.Children.Count, vm.Rows.Count);
            select.IsExpanded = true;
            Assert.Equal(before, vm.Rows.Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Window_and_command_titles_are_renamed_to_mapper_query()
    {
        Assert.Equal("매퍼 쿼리", StringCatalog.Get("ko", "Panel.oranda.panel.mybatisStatements"));
        Assert.Equal("Mapper Queries", StringCatalog.Get("en", "Panel.oranda.panel.mybatisStatements"));
        Assert.Equal("새 쿼리…", StringCatalog.Get("ko", "Cmd.oranda.mybatis.newStatement"));
        Assert.Equal("쿼리 id 바꾸기…", StringCatalog.Get("ko", "Cmd.oranda.mybatis.renameStatement"));
        Assert.Equal("쿼리 지우기", StringCatalog.Get("ko", "Cmd.oranda.mybatis.deleteStatement"));
        Assert.Equal("MyBatis 쿼리 찾기…", StringCatalog.Get("ko", "Cmd.oranda.mybatis.findStatement"));
        Assert.Equal("New Query…", StringCatalog.Get("en", "Cmd.oranda.mybatis.newStatement"));
        Assert.Equal("Delete Query", StringCatalog.Get("en", "Cmd.oranda.mybatis.deleteStatement"));
    }

    [Fact]
    public Task The_user_mapper_opens_lists_and_renders_in_both_themes() => Ui.Run(async () =>
    {
        if (!File.Exists(UserMapper))
        {
            return; // 사내 파일이라 저장소에 없다: 없으면 건너뛴다
        }

        var (host, vm, _, window) = await ShowAsync(UserMapper);
        try
        {
            var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
            Assert.True(session.Entries.Count > 100);
            Assert.Equal(session.Entries.Count, vm.Groups.Sum(g => g.Children.Count));
            Assert.True(vm.Rows.Count > 0);

            var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
            foreach (var width in RenderWidths)
            {
                window.Width = width;
                foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    Pump();
                    window.UpdateLayout();
                    Render(window, dir, $"stmt-grid-{width}-{name}.png", width);
                }
            }

            window.Width = 340;
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            vm.SearchText = "exe rst"; // 거른 상태 렌더(낱말 둘 모두 포함)
            Pump();
            window.UpdateLayout();
            Assert.True(vm.Rows.Count > 0);
            Render(window, dir, "stmt-grid-340-filtered.png", 340);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            window.Close();
        }
    });

    private static void Render(Window window, string? dir, string file, int width)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        using var bmp = new RenderTargetBitmap(new PixelSize(width, 560));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드 가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, file));
#pragma warning restore CS0618
    }
}
