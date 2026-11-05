using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>머리글 더블클릭 = 컬럼 이름, 값 더블클릭 = SQL 리터럴을 이 결과를 만든 편집기에 넣는다. 첫 클릭 정렬은 더블클릭이면 하지 않는다.</summary>
public sealed class HeaderValueInsertTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "NAME", "VARCHAR2", ColumnDataKind.Text),
        new(1, "QTY", "NUMBER", ColumnDataKind.Number),
        new(2, "DOC", "CLOB", ColumnDataKind.Clob),
        new(3, "RAW1", "RAW", ColumnDataKind.Raw),
    ];

    private sealed class Rows : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => true;

        public long? KnownRowCount => 4;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            object?[] names = ["ABC", "O'BRIEN", null, "Z"];
            object?[] qty = [12m, -3.5m, null, 7m];
            object?[] docs = [new LobValue(LobKind.Clob, 5) { TextPreview = "hello" }, null, null, null];
            object?[] raw = [new byte[] { 1, 2 }, null, null, null];
            return Task.FromResult(new ResultPage(pageIndex, 0, 4, [names, qty, docs, raw]) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(ResultTabsViewModel Tabs, SqlResultPaneViewModel Pane, SqlEditorViewModel Tab, List<string> Notices)> OpenAsync(string sql = "select  from dual")
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), sql);
        tab.UpdateCaret(7, 7, 0);
        var tabs = new ResultTabsViewModel { OwnerTab = tab };
        var item = tabs.AddQuery(new Rows(), "결과 1", null);
        var pane = item.Pane!;
        await pane.Model!.LoadFirstPageAsync();
        var notices = new List<string>();
        pane.Model.NoticeRequested += (_, text) => notices.Add(text);
        return (tabs, pane, tab, notices);
    }

    [Fact]
    public Task Header_name_goes_in_as_is_at_the_caret_and_replaces_a_selection() => HeadlessHost.Run(async () =>
    {
        var (_, pane, tab, notices) = await OpenAsync();

        pane.InsertColumnName(1);
        Assert.Equal("select QTY from dual", tab.Text);
        Assert.Equal(10, tab.CaretOffset); // 넣은 글 끝

        tab.UpdateCaret(7, 7, 3); // 「QTY」 를 선택
        pane.InsertColumnName(0);
        Assert.Equal("select NAME from dual", tab.Text); // 선택을 바꿈
        Assert.Empty(notices);
        pane.Clear();
    });

    [Fact]
    public Task Values_go_in_as_sql_literals_text_quoted_numbers_bare_null_as_NULL() => HeadlessHost.Run(async () =>
    {
        var (_, pane, tab, notices) = await OpenAsync("");
        string Insert(int row, int col)
        {
            tab.UpdateCaret(tab.Text.Length, tab.Text.Length, 0);
            var before = tab.Text.Length;
            pane.InsertCellValue(new GridCell(row, col));
            return tab.Text[before..];
        }

        Assert.Equal("'ABC'", Insert(0, 0));
        Assert.Equal("'O''BRIEN'", Insert(1, 0)); // 작은따옴표는 두 번
        Assert.Equal("12", Insert(0, 1));
        Assert.Equal("-3.5", Insert(1, 1));
        Assert.Equal("NULL", Insert(2, 0));
        Assert.Equal("NULL", Insert(2, 1));
        Assert.Empty(notices);
        pane.Clear();
    });

    [Fact]
    public Task Lob_and_binary_values_are_refused_with_a_notice_and_the_editor_is_untouched() => HeadlessHost.Run(async () =>
    {
        var (_, pane, tab, notices) = await OpenAsync();
        var before = tab.Text;

        pane.InsertCellValue(new GridCell(0, 2)); // CLOB
        pane.InsertCellValue(new GridCell(0, 3)); // RAW

        Assert.Equal(before, tab.Text);
        Assert.Equal(2, notices.Count);
        pane.Clear();
    });

    [Fact]
    public Task One_insert_is_one_undo_step_and_a_read_only_editor_is_refused_with_a_notice() => HeadlessHost.Run(async () =>
    {
        var (_, pane, tab, notices) = await OpenAsync();
        var original = tab.Text;

        pane.InsertCellValue(new GridCell(0, 0));
        Assert.NotEqual(original, tab.Text);
        tab.Document.UndoStack.Undo(); // 실행 취소 한 번에 넣기 전으로
        Assert.Equal(original, tab.Text);

        tab.IsReadOnly = true;
        pane.InsertCellValue(new GridCell(0, 0));
        pane.InsertColumnName(0);
        Assert.Equal(original, tab.Text);
        Assert.Equal(2, notices.Count);
        pane.Clear();
    });

    [Fact]
    public async Task A_result_whose_editor_tab_is_gone_gives_a_notice_instead_of_inserting()
    {
        var tabs = new ResultTabsViewModel(); // 소유 탭을 정하지 않았고 편집기 호스트에도 없다
        var pane = tabs.AddQuery(new Rows(), "결과 1", null).Pane!;
        await pane.Model!.LoadFirstPageAsync();
        var notices = new List<string>();
        pane.Model.NoticeRequested += (_, text) => notices.Add(text);

        pane.InsertColumnName(0);

        Assert.Single(notices);
        pane.Clear();
    }

    [Fact]
    public async Task Panes_without_an_editor_target_do_not_insert()
    {
        var pane = new SqlResultPaneViewModel(); // 객체 상세 같은 결과 영역
        pane.SetResult(new Rows());
        await pane.Model!.LoadFirstPageAsync();

        Assert.False(pane.CanInsertToEditor);
        pane.InsertColumnName(0); // 아무 일도 없다(오류도 없다)
        pane.Clear();
    }

    private sealed class PiecesSurface(params TextRange[] pieces) : IEditorSurface
    {
        public int CursorCount => 1;

        public bool HasExtraSelection => false;

        public IReadOnlyList<TextRange> SelectionSegments { get; } = pieces;

        public int FirstVisibleLine => 1;

        public void Cut()
        {
        }

        public void Copy()
        {
        }

        public void Paste()
        {
        }

        public void SelectAll()
        {
        }

        public void SelectRange(int start, int length)
        {
        }

        public void ScrollToLine(int line)
        {
        }

        public void Fold(FoldAction action)
        {
        }

        public void AddCursor(int direction)
        {
        }

        public void ShowFind(bool replace)
        {
        }

        public void FocusEditor()
        {
        }
    }

    [Fact]
    public Task A_column_block_selection_in_the_editor_is_refused_and_the_text_is_untouched() => HeadlessHost.Run(async () =>
    {
        var (_, pane, tab, notices) = await OpenAsync("AAAAselect 1 BBBB\nAAAAfrom dual BBBB");
        tab.Surface = new PiecesSurface(new TextRange(4, 8), new TextRange(22, 9));
        tab.UpdateCaret(31, 4, 27); // 두 줄의 가운데 글을 열 블록으로 잡았다
        var before = tab.Text;

        pane.InsertCellValue(new GridCell(0, 0));
        pane.InsertColumnName(0);

        Assert.Equal(before, tab.Text);
        Assert.Equal([SqlEditorViewModel.BlockSelectionNotice, SqlEditorViewModel.BlockSelectionNotice], notices);
        pane.Clear();
    });

    [Fact]
    public Task The_owner_editor_tab_is_found_through_the_editor_host_when_not_set_directly() => HeadlessHost.Run(async () =>
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select  from dual");
        tab.UpdateCaret(7, 7, 0);
        var tabs = new ResultTabsViewModel();
        tab.ResultPane = tabs; // 실행 화면이 하는 것: 탭이 결과 영역을 가진다
        var host = SqlEditorHost.Current;
        host.Tabs.Add(tab);
        try
        {
            var pane = tabs.AddQuery(new Rows(), "결과 1", null).Pane!;
            await pane.Model!.LoadFirstPageAsync();

            pane.InsertColumnName(0);

            Assert.Equal("select NAME from dual", tab.Text);
            pane.Clear();
        }
        finally
        {
            host.Tabs.Remove(tab);
        }
    });

    [Fact]
    public async Task Result_areas_that_are_not_editor_documents_get_no_insert_target_or_filter_switch()
    {
        var tabs = new ResultTabsViewModel { IsEditorResults = false }; // PL/SQL 실행 창
        var pane = tabs.AddQuery(new Rows(), "결과 1", null).Pane!;
        await pane.Model!.LoadFirstPageAsync();

        Assert.False(pane.CanInsertToEditor);
        Assert.False(pane.ColumnFilterToggleable);
        Assert.False(await pane.SetColumnFilterEnabledAsync(false));
        Assert.True(pane.Model.ColumnFilterEnabled);
        pane.Clear();
    }

    private static async Task WithGrid(bool filterOn, Func<Window, ResultGridControl, ResultGridModel, Func<int, Point>, Task> body)
    {
        await using var m = new ResultGridModel(new Rows());
        m.SetColumnFilterEnabled(filterOn);
        var grid = new ResultGridControl { Model = m, HeaderDoubleClickInserts = true, HeaderSortDelayOverride = TimeSpan.FromMilliseconds(250) };
        var window = new Window { Content = grid, Width = 700, Height = 300 };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == 4 && grid.Bounds.Height > 0);
            Point Header(int col) => grid.TranslatePoint(new Point(grid.RowHeaderWidth + (m.GetColumnWidth(0) * col) + 20, grid.HeaderHeight / 2), window)!.Value;
            await body(window, grid, m, Header);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public Task Double_clicking_a_header_with_the_filter_on_raises_one_insert_and_never_sorts() => HeadlessHost.Run(() => WithGrid(true, async (window, grid, m, header) =>
    {
        var inserted = new List<int>();
        grid.HeaderInsertRequested += (_, ordinal) => inserted.Add(ordinal);
        var p = header(1);

        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);

        await Task.Delay(500); // 지연(250ms)이 지나도 정렬이 시작되지 않는다
        Assert.Equal([1], inserted);
        Assert.Equal(-1, m.SortColumn);
    }));

    [Fact]
    public Task A_single_header_click_with_the_filter_on_sorts_after_the_delay_and_Escape_cancels_it() => HeadlessHost.Run(() => WithGrid(true, async (window, grid, m, header) =>
    {
        var p = header(1);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Assert.Equal(-1, m.SortColumn); // 아직 미룬 상태
        await HeadlessHost.WaitUntil(() => m.SortColumn == 1);

        var q = header(0); // 다른 열: 눌렀다가 Esc 로 취소
        grid.Focus();
        window.MouseDown(q, MouseButton.Left);
        window.MouseUp(q, MouseButton.Left);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Task.Delay(500);
        Assert.Equal(1, m.SortColumn);
    }));

    [Fact]
    public Task With_the_filter_off_a_header_double_click_selects_the_column_and_raises_the_insert() => HeadlessHost.Run(() => WithGrid(false, async (window, grid, m, header) =>
    {
        var inserted = new List<int>();
        grid.HeaderInsertRequested += (_, ordinal) => inserted.Add(ordinal);
        var p = header(0);

        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);

        await Task.Delay(100);
        Assert.Equal([0], inserted);
        Assert.Equal(-1, m.SortColumn);
        Assert.True(m.IsSelected(0, 0));
    }));

    [Fact]
    public Task Double_clicking_the_empty_area_below_the_last_row_inserts_nothing() => HeadlessHost.Run(() => WithGrid(true, async (window, grid, m, header) =>
    {
        var cells = new List<GridCell>();
        grid.CellDoubleTapped += (_, c) => cells.Add(c);
        var below = grid.TranslatePoint(new Point(header(1).X, grid.HeaderHeight + (grid.RowHeight * 4) + 40), window)!.Value; // 4행 아래 빈 곳
        Assert.True(below.Y < window.Bounds.Height);

        window.MouseDown(below, MouseButton.Left);
        window.MouseUp(below, MouseButton.Left);
        window.MouseDown(below, MouseButton.Left);
        window.MouseUp(below, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);

        Assert.Empty(cells);
    }));

    [Fact]
    public Task Without_the_insert_switch_a_header_click_sorts_at_once_like_before() => HeadlessHost.Run(() => WithGrid(true, async (window, grid, m, header) =>
    {
        grid.HeaderDoubleClickInserts = false; // 모니터 표·객체 상세
        grid.HeaderSortDelayOverride = TimeSpan.FromSeconds(5); // 지연을 탔다면 이 안에 정렬되지 않는다
        var p = header(1);

        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);

        await HeadlessHost.WaitUntil(() => m.SortColumn == 1, 2000);
        Assert.Equal(1, m.SortColumn);
    }));

    [Fact]
    public Task Double_clicking_a_value_raises_the_cell_event_when_subscribed_otherwise_the_old_edit_request() => HeadlessHost.Run(() => WithGrid(true, async (window, grid, m, header) =>
    {
        var cells = new List<GridCell>();
        var edits = new List<GridCell>();
        grid.EditRequested += (_, c) => edits.Add(c);
        var p = new Point(header(1).X, grid.TranslatePoint(new Point(0, grid.HeaderHeight + (grid.RowHeight * 1.5)), window)!.Value.Y); // 둘째 행, QTY 열

        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([new GridCell(1, 1)], edits); // 구독이 없을 때는 예전처럼 셀 편집 요청

        grid.CellDoubleTapped += (_, c) => cells.Add(c);
        edits.Clear();
        await Task.Delay(700); // 앞 더블클릭과 묶이지 않게 시간을 둔다
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([new GridCell(1, 1)], cells);
        Assert.Empty(edits); // 알림 구독이 있으면 셀 편집 요청 대신 그것만 낸다
    }));
}
