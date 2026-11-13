using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Events;
using Oranda.Core.Journal;

namespace Oranda.App.Tests.Shell.Controls;

/// <summary>표 열 폭 끌기(TableColumns): 머리글·줄이 함께 바뀜, 최소 폭, * 열 경계, 두 번 누르기, 저장 왕복, 변경 일지·바인드 변수 창.</summary>
public sealed class TableColumnsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-table-cols-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string?> _oldProvider = TableColumns.DataDirectoryProvider;

    public TableColumnsTests() => TableColumns.DataDirectoryProvider = () => _dir;

    public void Dispose()
    {
        TableColumns.DataDirectoryProvider = _oldProvider;
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>머리글 "60,*,80" + 줄 3개짜리 표. 줄 칸마다 Border 를 둬 칸 자리를 잰다.</summary>
    private static (DockPanel Root, Grid Header, ListBox List) MakeTable(string key)
    {
        static Grid Cells(Grid grid)
        {
            for (var i = 0; i < 3; i++)
            {
                var cell = new Border { Name = "c" + i };
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }

            return grid;
        }

        var header = Cells(new Grid { ColumnDefinitions = ColumnDefinitions.Parse("60,*,80"), Height = 24 });
        TableColumns.SetHeader(header, true);
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        var list = new ListBox
        {
            ItemsSource = new[] { "a", "b", "c" },
            ItemTemplate = new FuncDataTemplate<string>((_, _) =>
            {
                var row = Cells(new Grid { ColumnDefinitions = ColumnDefinitions.Parse("60,*,80"), Height = 20 });
                TableColumns.SetRow(row, true);
                return row;
            }),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var root = new DockPanel { Children = { header, list } };
        // 표 스타일처럼 줄 안쪽 여백 없음: 머리글과 줄의 폭이 같다.
        root.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
        {
            Setters = { new Avalonia.Styling.Setter(TemplatedControl.PaddingProperty, new Thickness(0)) },
        });
        TableColumns.SetScope(root, key);
        return (root, header, list);
    }

    private static IEnumerable<Grid> Rows(ListBox list) =>
        list.GetVisualDescendants().OfType<Grid>().Where(TableColumns.GetRow);

    private static double X(Visual v, Visual window) => v.TranslatePoint(default, window)!.Value.X;

    private static Point Center(Visual v, Visual window) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window)!.Value;

    private static void Drag(Window window, Point from, double dx)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(dx / 2, 0));
        window.MouseMove(from + new Point(dx, 0));
        window.MouseUp(from + new Point(dx, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void DoubleClick(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static TableColumnGrip Grip(Grid header, int boundary) =>
        header.Children.OfType<TableColumnGrip>().Single(g => g.Boundary == boundary);

    [Fact]
    public Task Dragging_a_header_edge_moves_header_and_rows_together_keeps_min_and_double_click_resets() => HeadlessHost.Run(() =>
    {
        var key = "t-" + Guid.NewGuid().ToString("N");
        var (root, header, list) = MakeTable(key);
        var window = new Window { Content = root, Width = 500, Height = 300 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            // 픽셀 열 0·2 와 * 열(1)의 오른쪽 경계(뒤 픽셀 열을 반대로 바꿈)에 손잡이. 마지막 열 2 도 끌 수 있다.
            Assert.Equal([0, 1, 2], header.Children.OfType<TableColumnGrip>().Select(g => g.Boundary).Order());
            Assert.All(header.Children.OfType<TableColumnGrip>(), g => Assert.NotNull(g.Cursor)); // 좌우 화살표 커서

            Drag(window, Center(Grip(header, 0), window), 40);
            Assert.Equal(100, header.ColumnDefinitions[0].ActualWidth, 1);
            Assert.All(Rows(list), r => Assert.Equal(100, r.ColumnDefinitions[0].ActualWidth, 1));
            var headCell2 = header.Children.OfType<Border>().Single(b => b.Name == "c2");
            Assert.All(Rows(list), r => Assert.Equal(X(headCell2, header), X(r.Children.OfType<Border>().Single(b => b.Name == "c2"), r), 1));

            // 최소 폭(MinWidth 없음 = 24) 아래로는 줄지 않는다.
            Drag(window, Center(Grip(header, 0), window), -300);
            Assert.Equal(TableColumnState.DefaultMin, header.ColumnDefinitions[0].ActualWidth, 1);
            Assert.All(Rows(list), r => Assert.Equal(TableColumnState.DefaultMin, r.ColumnDefinitions[0].ActualWidth, 1));

            // * 열 오른쪽 경계를 오른쪽으로 끌면 뒤 픽셀 열이 그만큼 좁아진다(* 열이 넓어짐).
            Drag(window, Center(Grip(header, 1), window), 30);
            Assert.Equal(50, header.ColumnDefinitions[2].ActualWidth, 1);
            Assert.All(Rows(list), r => Assert.Equal(50, r.ColumnDefinitions[2].ActualWidth, 1));

            // 머리글 오른쪽 클릭 「열 폭 처음으로」 = 모든 열(손잡이가 화면 밖일 때).
            var reset = Assert.IsType<MenuItem>(Assert.Single(header.ContextMenu!.Items));
            reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(80, header.ColumnDefinitions[2].ActualWidth, 1);
            Assert.All(Rows(list), r => Assert.Equal(80, r.ColumnDefinitions[2].ActualWidth, 1));
            Drag(window, Center(Grip(header, 2), window), -30); // 마지막 픽셀 열은 자기 경계로 줄인다(같은 자리 연속 누름은 두 번 누르기로 셈)
            Assert.Equal(50, header.ColumnDefinitions[2].ActualWidth, 1);

            // 두 번 누르면 처음 폭.
            DoubleClick(window, Center(Grip(header, 0), window));
            Assert.Equal(60, header.ColumnDefinitions[0].ActualWidth, 1);
            Assert.All(Rows(list), r => Assert.Equal(60, r.ColumnDefinitions[0].ActualWidth, 1));
        }
        finally
        {
            window.Close();
        }

        // 저장 왕복: 기본과 다른 열(2 = 50)만 남고, 같은 키로 새로 연 표가 그 폭으로 시작한다.
        var json = File.ReadAllText(Path.Combine(_dir, TableColumns.FileName));
        Assert.Contains(key, json, StringComparison.Ordinal);
        var (root2, header2, list2) = MakeTable(key);
        var window2 = new Window { Content = root2, Width = 500, Height = 300 };
        window2.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(60, header2.ColumnDefinitions[0].ActualWidth, 1);
            Assert.Equal(50, header2.ColumnDefinitions[2].ActualWidth, 1);
            Assert.All(Rows(list2), r => Assert.Equal(50, r.ColumnDefinitions[2].ActualWidth, 1));
        }
        finally
        {
            window2.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void Broken_file_means_default_widths()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, TableColumns.FileName), "{ not json");
        Assert.Empty(TableColumns.Load("any"));
        TableColumns.Save("k", new Dictionary<string, double> { ["0"] = 77 }); // 손상 파일은 새로 쓴다
        Assert.Equal(77, TableColumns.Load("k")["0"]);
    }

    [Fact]
    public Task Change_journal_columns_follow_the_header_drag() => HeadlessHost.Run(async () =>
    {
        var entry = new JournalEntry
        {
            Id = 1, CapturedAt = DateTimeOffset.UnixEpoch, ProfileId = Guid.NewGuid(), ProfileName = "개발", DbKey = "FREE/FREEPDB1/APP",
            Owner = "APP", Name = "EMP", ObjectType = "TABLE", Cause = JournalCause.Ddl, StatementSummary = "ALTER TABLE EMP ADD x NUMBER",
            PreviousDdl = "CREATE TABLE emp (a NUMBER)",
        };
        using var vm = ChangeJournalViewModel.ForTests(new OneStore(entry));
        await vm.ReloadAsync();
        var view = new ChangeJournalView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 500 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var header = view.FindControl<Grid>("JournalHeader")!;
            var list = view.FindControl<ListBox>("List")!;
            Assert.NotEmpty(Rows(list));
            var before = header.ColumnDefinitions[2].ActualWidth; // 객체 열 200

            Drag(window, Center(Grip(header, 2), window), 60);
            Assert.Equal(before + 60, header.ColumnDefinitions[2].ActualWidth, 1);
            Assert.All(Rows(list), r => Assert.Equal(before + 60, r.ColumnDefinitions[2].ActualWidth, 1));
            var headTitle = header.Children.OfType<TextBlock>().Single(t => t.Text == "원인");
            Assert.All(Rows(list), r => Assert.Equal(X(headTitle, header), X(r.Children.OfType<Control>().Single(c => Grid.GetColumn(c) == 3), r), 1));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Bind_variable_columns_follow_the_header_drag_and_the_value_column_takes_the_rest() => HeadlessHost.Run(() =>
    {
        var vm = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        vm.Store.Ensure(["A", "B"]);
        var view = new BindVariablesView { DataContext = vm };
        var window = new Window { Width = 520, Height = 300, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var header = view.FindControl<Grid>("HeaderGrid")!;
            var list = view.FindControl<ListBox>("RowList")!;
            Assert.Equal(2, Rows(list).Count());
            var value = header.ColumnDefinitions[2].ActualWidth;

            Drag(window, Center(Grip(header, 1), window), 50); // 변수 열 48 → 98, 값 열이 그만큼 좁아짐
            Assert.Equal(98, header.ColumnDefinitions[1].ActualWidth, 1);
            Assert.Equal(value - 50, header.ColumnDefinitions[2].ActualWidth, 1);
            Assert.All(Rows(list), r =>
            {
                Assert.Equal(98, r.ColumnDefinitions[1].ActualWidth, 1);
                Assert.Equal(value - 50, r.ColumnDefinitions[2].ActualWidth, 1);
            });
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Shared_size_auto_columns_start_at_content_width_follow_the_drag_below_content_and_return_on_double_click() => HeadlessHost.Run(() =>
    {
        // 정보 표(DetailTable, Styles/GridC): 칸 = Auto + SharedSizeGroup, 숨은 폭 재기 줄(Probe)이 전체 행 기준 내용 폭을 정한다.
        string[] columns = ["이름-" + Guid.NewGuid().ToString("N")[..6], "값", "메모"];
        IReadOnlyList<object?>[] rows = [["SGA_TARGET_SIZE_PARAMETER", "1024", "가"], ["PGA", "2", "나"]];
        static Grid HeaderOf(DetailTable t) => t.GetVisualDescendants().OfType<Grid>().Single(TableColumns.GetHeader);
        var table = new DetailTable { Columns = columns, Rows = rows };
        var window = new Window { Content = table, Width = 700, Height = 300 };
        window.Show();
        double content;
        try
        {
            Dispatcher.UIThread.RunJobs();
            var header = HeaderOf(table);
            Assert.True(header.ColumnDefinitions[1].Width.IsAuto);
            Assert.Equal([0, 1, 2], header.Children.OfType<TableColumnGrip>().Select(g => g.Boundary).Order()); // 마지막 * 칸은 끌지 않음
            content = header.ColumnDefinitions[1].ActualWidth;
            Assert.True(content > 100, "처음 폭 = 긴 이름의 내용 폭: " + content);
            var all = TableColumnsDrag.Rows(table); // 화면 줄 + 숨은 폭 재기 줄
            Assert.True(all.Count > rows.Length);

            // 내용 폭보다 좁게: 숨은 줄도 따라가므로 공유 묶음이 막지 않는다.
            Drag(window, Center(Grip(header, 1), window), -60);
            Assert.Equal(content - 60, header.ColumnDefinitions[1].ActualWidth, 1);
            Assert.Null(header.ColumnDefinitions[1].SharedSizeGroup);
            Assert.All(TableColumnsDrag.Rows(table), r => Assert.Equal(content - 60, r.ColumnDefinitions[1].Width.Value, 1));
            Assert.All(TableColumnsDrag.Rows(table).Where(r => r.IsArrangeValid && r.Bounds.Height > 0), r => Assert.Equal(content - 60, r.ColumnDefinitions[1].ActualWidth, 1));
        }
        finally
        {
            window.Close();
        }

        // 끈 폭은 남는다: 같은 열의 새 표가 그 폭으로 시작하고, 두 번 누르면 다시 내용 폭(Auto + 묶음 이름).
        var table2 = new DetailTable { Columns = columns, Rows = rows };
        var window2 = new Window { Content = table2, Width = 700, Height = 300 };
        window2.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var header = HeaderOf(table2);
            Assert.Equal(content - 60, header.ColumnDefinitions[1].ActualWidth, 1);
            DoubleClick(window2, Center(Grip(header, 1), window2));
            Assert.True(header.ColumnDefinitions[1].Width.IsAuto);
            Assert.NotNull(header.ColumnDefinitions[1].SharedSizeGroup);
            Assert.Equal(content, header.ColumnDefinitions[1].ActualWidth, 1);
            Assert.All(TableColumnsDrag.Rows(table2), r => Assert.True(r.ColumnDefinitions[1].Width.IsAuto));
        }
        finally
        {
            TableColumnsDrag.ResetAll(window2);
            window2.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Auto_columns_without_a_shared_group_stay_fixed() => HeadlessHost.Run(() =>
    {
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("60,Auto,*") };
        TableColumns.SetHeader(header, true);
        var root = new DockPanel { Children = { header } };
        TableColumns.SetScope(root, "t-" + Guid.NewGuid().ToString("N"));
        var window = new Window { Content = root, Width = 400, Height = 200 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([0], header.Children.OfType<TableColumnGrip>().Select(g => g.Boundary)); // 내용이 폭을 정하는 Auto 칸(조합 검사 조건 칸 등)은 그대로
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private sealed class OneStore(JournalEntry entry) : IChangeJournalStore
    {
        public Task<long?> AddAsync(JournalEntry e, CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery q, CancellationToken ct) => Task.FromResult<IReadOnlyList<JournalEntry>>([entry]);

        public Task<JournalEntry?> GetAsync(long id, CancellationToken ct) => Task.FromResult<JournalEntry?>(id == entry.Id ? entry : null);

        public Task PruneAsync(JournalRetention retention, CancellationToken ct) => Task.CompletedTask;
    }
}
