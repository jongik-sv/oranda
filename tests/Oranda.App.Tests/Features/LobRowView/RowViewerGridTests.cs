using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.LobRowView;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.LobRowView;

/// <summary>
/// TSK-14-16: 행 보기의 필드 목록이 조회 결과와 같은 그리드(번호·열 이름·타입·값)로 보이고, 결과 그리드의 현재 행이 바뀌어 다시 채워도
/// 정렬·열 순서·너비·열 필터·고른 칸(열 키)·스크롤이 남는다. 복사(머리글 포함)·LOB 값 보기·값 고치기도 그대로 된다.
/// </summary>
[Collection("GridEditStatics")]
public class RowViewerGridTests
{
    private static RawInputModifiers Primary
        => Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
        new(2, "NOTE", "VARCHAR2", ColumnDataKind.Text),
        new(3, "DOC", "CLOB", ColumnDataKind.Clob),
    ];

    private static object?[][] TwoRows() =>
    [
        [1m, "a", null, new LobValue(LobKind.Clob, 3) { TextPreview = "abc" }],
        [2m, "b", "x", new LobValue(LobKind.Clob, 3) { TextPreview = "def" }],
    ];

    private static async Task<ResultGridModel> LoadAsync(ResultColumn[] cols, object?[][] rows)
    {
        var m = new ResultGridModel(new MemoryResultSet(cols, [.. rows.Select(r => (IReadOnlyList<object?>)r)]));
        await m.LoadFirstPageAsync();
        m.SelectCell(new GridCell(0, 0));
        return m;
    }

    private static async Task<(Window Window, RowViewerView View, RowViewerViewModel Vm, ResultGridModel Result)> OpenAsync(ResultGridModel? result = null)
    {
        result ??= await LoadAsync(Cols, TwoRows());
        var vm = new RowViewerViewModel();
        vm.Attach(result);
        var view = new RowViewerView { DataContext = vm };
        var window = new Window { Content = view, Width = 700, Height = 500 };
        window.Show();
        await HeadlessHost.WaitUntil(() => view.Grid.Model is { } m && m.RowCount == vm.Fields.Count && view.Grid.GridControl.Bounds.Height > 0);
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm, result);
    }

    // 결과 그리드에서 행을 옮기고 행 보기 그리드가 새 결과로 바뀔 때까지 기다린다.
    private static async Task MoveAsync(RowViewerView view, RowViewerViewModel vm, int delta)
    {
        var before = view.Grid.Model;
        var row = vm.Row + delta;
        vm.MoveRow(delta);
        await HeadlessHost.WaitUntil(() => vm.Row == row && !ReferenceEquals(view.Grid.Model, before));
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Cell(Window w, RowViewerView v, long row, int visibleCol)
    {
        var c = v.Grid.GridControl;
        var m = v.Grid.Model!;
        var x = c.RowHeaderWidth + m.VisibleColumns.Take(visibleCol).Sum(m.GetColumnWidth) + 8 - c.OffsetX;
        var y = c.HeaderHeight + (row * c.RowHeight) + (c.RowHeight / 2) - c.OffsetY;
        return c.TranslatePoint(new Point(x, y), w)!.Value;
    }

    private static Point Header(Window w, RowViewerView v, int visibleCol)
    {
        var c = v.Grid.GridControl;
        var m = v.Grid.Model!;
        var x = c.RowHeaderWidth + m.VisibleColumns.Take(visibleCol).Sum(m.GetColumnWidth) + 8;
        return c.TranslatePoint(new Point(x, c.HeaderHeight / 2), w)!.Value;
    }

    private static void Click(Window w, Point p, MouseButton button = MouseButton.Left)
    {
        w.MouseDown(p, button);
        w.MouseUp(p, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Column(ResultGridModel m, int ordinal) => [.. Enumerable.Range(0, (int)m.RowCount).Select(r => m.GetCellText(r, ordinal))];

    private static long RowOf(ResultGridModel m, string name) => Array.IndexOf(Column(m, 1), name);

    private static async Task<string?> ClipboardAsync(Window w, Func<string, bool> done)
    {
        string? text = null;
        for (var i = 0; i < 100 && (text is null || !done(text.ReplaceLineEndings("\n"))); i++)
        {
            await Task.Delay(20);
            text = await w.Clipboard!.TryGetTextAsync();
        }

        return text?.ReplaceLineEndings("\n");
    }

    [Fact]
    public Task Fields_are_shown_in_the_result_grid_as_number_name_type_and_value() => HeadlessHost.Run(async () =>
    {
        var (w, v, _, result) = await OpenAsync();
        try
        {
            var m = v.Grid.Model!;
            Assert.Equal(["#", "열 이름", "타입", "값"], m.Columns.Select(c => c.Name));
            Assert.Equal(["1", "2", "3", "4"], Column(m, 0));
            Assert.True(m.IsRightAligned(0)); // 번호는 숫자 열
            Assert.Equal(["ID", "NAME", "NOTE", "DOC"], Column(m, 1));
            Assert.Equal(["NUMBER", "VARCHAR2", "VARCHAR2", "CLOB"], Column(m, 2));
            Assert.Equal("1", m.GetCellText(0, 3));
            Assert.Equal("abc", m.GetCellText(3, 3));
            Assert.True(m.IsNull(2, 3)); // NULL 은 그리드의 NULL 표시(흐린 글자)로
            Assert.Equal(result.Display.NullText, m.GetCellText(2, 3));
            Assert.False(v.Grid.GridControl.IsCommandTarget); // 편집기 결과 명령의 대상이 되지 않는다
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Sort_column_order_width_filter_and_picked_field_survive_moving_to_the_next_row() => HeadlessHost.Run(async () =>
    {
        var (w, v, vm, result) = await OpenAsync();
        try
        {
            Click(w, Header(w, v, 1)); // 열 이름 오름차순
            await HeadlessHost.WaitUntil(() => v.Grid.Model!.SortColumn == 1);
            var m = v.Grid.Model!;
            Assert.Equal(["DOC", "ID", "NAME", "NOTE"], Column(m, 1));

            Assert.True(m.MoveColumn(2, 0)); // 타입을 맨 앞으로
            m.SetColumnWidth(3, 333);
            await m.SetLocalFilterAsync(m.LocalFilter.WithColumn(2, new ColumnFilter { Values = new HashSet<string> { "NUMBER", "VARCHAR2" } }));
            Assert.Equal(3, m.RowCount);
            m.SelectCell(new GridCell(RowOf(m, "NAME"), 3));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("NAME", vm.SelectedField!.Name);

            await MoveAsync(v, vm, 1);
            m = v.Grid.Model!;
            Assert.Equal(1, result.Active.Row);
            Assert.Equal(1, m.SortColumn);
            Assert.False(m.SortDescending);
            Assert.Equal([2, 0, 1, 3], m.VisibleColumns);
            Assert.Equal(333, m.GetColumnWidth(3));
            Assert.Equal(["ID", "NAME", "NOTE"], Column(m, 1)); // CLOB 는 걸러진 채
            Assert.Equal(RowOf(m, "NAME"), m.Active.Row); // 같은 열(키)을 계속 고른다
            Assert.Equal(3, m.Active.Col);
            Assert.Equal("NAME", vm.SelectedField!.Name);
            Assert.Equal("b", vm.SelectedField.Text);
            Assert.Equal("b", m.GetCellText(m.Active.Row, 3));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Scroll_position_survives_moving_to_the_next_row() => HeadlessHost.Run(async () =>
    {
        var cols = Enumerable.Range(0, 80).Select(i => new ResultColumn(i, "C" + i, "NUMBER", ColumnDataKind.Number)).ToArray();
        object?[][] rows = [[.. Enumerable.Range(0, 80).Select(i => (object?)(decimal)i)], [.. Enumerable.Range(0, 80).Select(i => (object?)(decimal)(i + 100))]];
        var (w, v, vm, _) = await OpenAsync(await LoadAsync(cols, rows));
        try
        {
            v.Grid.GridControl.ScrollTo(0, v.Grid.GridControl.RowHeight * 40);
            var y = v.Grid.GridControl.OffsetY;
            Assert.True(y > 0);

            await MoveAsync(v, vm, 1);
            Assert.Equal(y, v.Grid.GridControl.OffsetY);
            Assert.Equal("100", v.Grid.Model!.GetCellText(0, 3));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Ctrl_C_copies_the_value_and_ctrl_shift_C_adds_headers_and_the_menu_keeps_the_row_view_items() => HeadlessHost.Run(async () =>
    {
        var (w, v, vm, _) = await OpenAsync();
        try
        {
            var m = v.Grid.Model!;
            Click(w, Cell(w, v, 1, 3)); // NAME 의 값 칸
            Assert.Equal("NAME", vm.SelectedField!.Name);
            v.Grid.GridControl.Focus();
            w.KeyPress(Key.C, Primary, PhysicalKey.C, "c");
            Assert.Equal("a", (await ClipboardAsync(w, t => t == "a"))?.TrimEnd('\n'));

            m.SelectCell(new GridCell(0, 1));
            m.ExtendTo(new GridCell(1, 3));
            w.KeyPress(Key.C, Primary | RawInputModifiers.Shift, PhysicalKey.C, "C");
            var text = await ClipboardAsync(w, t => t.Contains("열 이름", StringComparison.Ordinal));
            Assert.NotNull(text);
            Assert.Contains("열 이름\t타입\t값", text, StringComparison.Ordinal);
            Assert.Contains("ID\tNUMBER\t1", text, StringComparison.Ordinal);
            Assert.Contains("NAME\tVARCHAR2\ta", text, StringComparison.Ordinal);

            var headers = v.Grid.MenuItems().OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Assert.Contains("머리글 포함 복사", headers);
            Assert.Contains("값 보기", headers);
            Assert.Contains("값 복사", headers);
            Assert.Contains("이름 = 값 전체 복사", headers);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Double_click_or_enter_on_a_lob_value_opens_the_value_viewer() => HeadlessHost.Run(async () =>
    {
        var (w, v, vm, _) = await OpenAsync();
        try
        {
            var opened = new List<string>();
            vm.ValueHandler = new LobRowViewCommandHandler
            {
                OpenDocument = (id, panel, title) =>
                {
                    opened.Add(id + "|" + title);
                    return panel;
                },
                SessionProvider = () => new FakeSession(new ConnectionProfile("dev", "scott", "h:1521/s")),
                EditSessionProvider = () => null,
            };

            var doc = Cell(w, v, 3, 3);
            Click(w, doc);
            Click(w, doc); // 두 번 누르기
            await HeadlessHost.WaitUntil(() => opened.Count > 0);
            Assert.StartsWith("oranda.panel.lobEditor|DOC (행 1)", opened[0], StringComparison.Ordinal);
            Assert.False(v.FindControl<Border>("EditPanel")!.IsVisible); // LOB 는 아래 입력칸으로 고치지 않는다

            opened.Clear();
            v.Grid.GridControl.Focus();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await HeadlessHost.WaitUntil(() => opened.Count > 0);
            Assert.StartsWith("oranda.panel.lobEditor|DOC", opened[0], StringComparison.Ordinal);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task F2_on_an_editable_value_opens_the_editor_below_and_enter_writes_it_to_the_result() => HeadlessHost.Run(async () =>
    {
        var result = await LoadAsync(Cols[..2], [[1m, "a"], [2m, "b"]]);
        var emp = new DbObjectRef("SCOTT", "EMP", "TABLE");
        var meta = new OneTable(new TableInfo(emp, [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2")])
        {
            Constraints = [new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["ID"])],
        });
        var edit = await GridEditSession.OpenAsync(new RowEditor(new FakeExecution()), meta, new FakeSession(new ConnectionProfile("dev", "scott", "h:1521/s")),
            new SqlStatement("SELECT id, name FROM scott.emp", default, StatementKind.Query) { Verb = "SELECT" }, result.Columns,
            GridEditBinding.OriginalValues(result), () => result.LoadedRowCount, target: null, default);
        GridEditBinding.Attach(result, edit);
        var (w, v, vm, _) = await OpenAsync(result);
        try
        {
            vm.Refresh();
            await HeadlessHost.WaitUntil(() => vm.Fields.Count == 2 && vm.Fields[1].CanEdit && v.Grid.Model!.RowCount == 2);
            Dispatcher.UIThread.RunJobs();
            Click(w, Cell(w, v, 1, 3)); // NAME 값
            Assert.Equal("NAME", vm.SelectedField!.Name);
            v.Grid.GridControl.Focus();
            w.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
            Dispatcher.UIThread.RunJobs();

            var panel = v.FindControl<Border>("EditPanel")!;
            var box = v.FindControl<TextBox>("EditBox")!;
            Assert.True(panel.IsVisible);
            Assert.Equal("a", box.Text);
            box.Text = "zzz";
            box.Focus();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(result.IsEdited(0, 1));
            await HeadlessHost.WaitUntil(() => v.Grid.Model is { } m && m.GetCellText(1, 3) == "zzz");
            Assert.True(v.Grid.Model!.IsEmphasized(1, 3)); // 바꾼 값은 강조색
            Assert.False(v.Grid.Model.IsEmphasized(0, 3));
            Assert.False(panel.IsVisible);
        }
        finally
        {
            w.Close();
            GridEditCommands.Active = null;
        }
    });

    private sealed class OneTable(TableInfo table) : IMetadataProvider
    {
        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef t, CancellationToken ct) => Task.FromResult(table with { Table = t });

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
