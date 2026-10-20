using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>
/// 행 복사·INSERT 문 복사(행 전체)·행 붙여넣기·행 복제(GRID-13): 값 형식 보존, 새 행이 끝에 붙어 DML 미리보기에 INSERT 로 나옴,
/// 열 수 불일치, ROWID 비움·키 열 경고, 편집 모드가 아닐 때 안내 막대, 키맵·명령 목록 정합.
/// </summary>
public sealed partial class GridEditTests
{
    private const string EmpRowsSql = "SELECT e.*, e.ROWID FROM emp e";

    private static readonly ResultColumn[] EmpRowCols =
    [
        .. Columns(("EMPNO", ColumnDataKind.Number), ("ENAME", ColumnDataKind.Text), ("HIREDATE", ColumnDataKind.Date),
                   ("SAL", ColumnDataKind.Number), ("COMM", ColumnDataKind.Number), ("ROWID", ColumnDataKind.RowId))
            .Select(c => c with { BaseOwner = "SCOTT", BaseTable = "EMP" }),
    ];

    private static object?[][] EmpRows() =>
    [
        [7369m, "SMITH", new DateTime(1980, 12, 17), 800m, null, "AAAR3sAAEAAAACXAAA"],
        [7499m, "O'NEIL", new DateTime(1981, 2, 20), 1600.5m, 300m, "AAAR3sAAEAAAACXAAB"],
    ];

    /// <summary>클립보드 흉내: 쓴 글을 그대로 읽는다(<see cref="Text"/> 를 바꾸면 다른 곳에서 복사한 셈).</summary>
    private sealed class FakeClipboard
    {
        public string? Text { get; set; }

        public void Attach(ResultGridModel m)
        {
            m.ClipboardWriter = t =>
            {
                Text = t;
                return Task.CompletedTask;
            };
            m.ClipboardReader = () => Task.FromResult(Text);
        }
    }

    /// <summary>결과 영역 + 그리드 모델(첫 페이지까지) + 클립보드. <paramref name="edit"/> 면 실행 화면처럼 편집 세션을 붙인다.</summary>
    private async Task<(SqlResultPaneViewModel Pane, ResultGridModel Grid, GridEditSession? Session, FakeClipboard Clip)> RowsPaneAsync(
        bool edit, string sql = EmpRowsSql, ResultColumn[]? cols = null)
    {
        cols ??= EmpRowCols;
        var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" };
        var pane = new SqlResultPaneViewModel { Statement = stmt };
        pane.SetResult(new MemoryResult(cols, EmpRows()));
        var m = pane.Model!;
        await m.LoadFirstPageAsync();
        var clip = new FakeClipboard();
        clip.Attach(m);
        GridEditSession? s = null;
        if (edit)
        {
            var meta = new OneTableMetadata(new Oranda.Core.Metadata.TableInfo(Emp, [new Oranda.Core.Metadata.ColumnInfo("EMPNO", 1, "NUMBER") { IsNullable = false }])
            {
                Constraints = [new Oranda.Core.Metadata.ConstraintInfo("PK_EMP", Oranda.Core.Metadata.ConstraintKind.PrimaryKey, ["EMPNO"])],
            });
            s = await GridEditSession.OpenAsync(new RowEditor(_exec), meta, new TestSession(false), stmt, m.Columns,
                GridEditBinding.OriginalValues(m), () => m.LoadedRowCount, target: null, default);
            GridEditBinding.Attach(m, s);
            pane.SetEditSession(s);
            GridEditCommands.Active = s;
        }

        ResultGridCommands.Active = m;
        return (pane, m, s, clip);
    }

    /// <summary>
    /// 그리드를 Parameter 로 넘겨 명령을 부른다. 활성 그리드(정적)는 다른 시험의 헤드리스 창이 포커스를 옮길 때 UI 스레드에서 바뀔 수 있어
    /// 그것에 기대면 가끔 다른 그리드에 실행된다.
    /// </summary>
    private static Task RunGrid(string id, ResultGridModel grid) =>
        new ResultGridCommandHandler().ExecuteAsync(new Oranda.Core.Shell.CommandContext(id, new NoKeys(), grid), default);

    private static Task RunOn(string id, ResultGridModel grid) =>
        new GridEditCommandHandler().ExecuteAsync(new Oranda.Core.Shell.CommandContext(id, new NoKeys(), grid), default);

    [Fact]
    public async Task Copy_rows_copies_every_visible_column_of_the_spanned_rows_and_keeps_raw_values()
    {
        var (pane, m, _, clip) = await RowsPaneAsync(edit: false);
        try
        {
            m.SelectCell(new GridCell(0, 1));
            m.ExtendTo(new GridCell(1, 1)); // ENAME 두 칸만 골라도 행 전체

            await RunGrid("oranda.grid.copyRows", m);

            Assert.Equal("7369\tSMITH\t1980-12-17 00:00:00\t800\t\tAAAR3sAAEAAAACXAAA\n"
                         + "7499\tO'NEIL\t1981-02-20 00:00:00\t1600.5\t300\tAAAR3sAAEAAAACXAAB", clip.Text);
            var copy = Assert.IsType<GridRowsCopy>(ResultGridCommands.CopiedRows);
            Assert.Equal(clip.Text, copy.Text);
            Assert.Equal(7369m, copy.Rows[0][0]);
            Assert.Equal(new DateTime(1980, 12, 17), copy.Rows[0][2]); // 날짜는 글자가 아니라 DateTime 그대로
            Assert.Null(copy.Rows[0][4]); // NULL 은 빈 글자가 아니라 null
            Assert.True(copy.Matches(clip.Text!.Replace("\n", "\r\n", StringComparison.Ordinal))); // 운영체제가 CRLF 로 바꿔도 같은 사본

            await RunGrid("oranda.grid.copyRowsWithHeaders", m);
            Assert.StartsWith("EMPNO\tENAME\tHIREDATE\tSAL\tCOMM\tROWID\n7369\t", clip.Text, StringComparison.Ordinal);

            m.HideColumn(4); // 숨긴 열은 기존 복사처럼 뺀다
            await RunGrid("oranda.grid.copyRows", m);
            Assert.StartsWith("7369\tSMITH\t1980-12-17 00:00:00\t800\tAAAR3sAAEAAAACXAAA\n", clip.Text, StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Copy_as_insert_uses_every_column_of_the_spanned_rows_except_hidden_and_pseudo_columns()
    {
        var (pane, m, _, clip) = await RowsPaneAsync(edit: false);
        try
        {
            m.SelectCell(new GridCell(0, 3));
            m.ExtendTo(new GridCell(1, 3)); // SAL 열만 골랐다

            await RunGrid("oranda.grid.copyAsInsert", m);

            Assert.Equal(
                "INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, SAL, COMM) VALUES (7369, 'SMITH', TO_DATE('1980-12-17 00:00:00', 'YYYY-MM-DD HH24:MI:SS'), 800, NULL);\n"
                + "INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, SAL, COMM) VALUES (7499, 'O''NEIL', TO_DATE('1981-02-20 00:00:00', 'YYYY-MM-DD HH24:MI:SS'), 1600.5, 300);",
                clip.Text);

            m.HideColumn(3); // 숨긴 SAL 은 빠지고 순서는 결과 열 순서
            m.SelectCell(new GridCell(1, 0));
            await RunGrid("oranda.grid.copyAsInsert", m);
            Assert.Equal(
                "INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, COMM) VALUES (7499, 'O''NEIL', TO_DATE('1981-02-20 00:00:00', 'YYYY-MM-DD HH24:MI:SS'), 300);",
                clip.Text);
            Assert.True(ResultGridModel.IsPseudoColumn(new ResultColumn(0, "rownum", "NUMBER", ColumnDataKind.Number)));
            Assert.False(ResultGridModel.IsPseudoColumn(new ResultColumn(0, "ROW_ID", "NUMBER", ColumnDataKind.Number)));
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Paste_rows_from_the_in_app_copy_adds_typed_new_rows_at_the_end_and_previews_inserts()
    {
        var (pane, m, s, clip) = await RowsPaneAsync(edit: true);
        try
        {
            m.SelectCell(new GridCell(0, 0));
            m.ExtendTo(new GridCell(1, 0));
            await RunGrid("oranda.grid.copyRows", m);

            await RunOn("oranda.grid.edit.pasteRows", m);

            Assert.Equal(4, m.RowCount);
            Assert.Equal(GridRowMark.Inserted, m.RowMarkOf(2));
            Assert.Equal(GridRowMark.Inserted, m.RowMarkOf(3));
            Assert.Equal(new GridCell(2, 0), m.Active); // 셀 커서는 첫 새 행으로
            Assert.True(m.TryGetValue(2, 2, out var hired) && hired is DateTime d && d == new DateTime(1980, 12, 17));
            Assert.True(m.TryGetValue(3, 3, out var sal) && Equals(sal, 1600.5m));
            Assert.True(m.TryGetValue(2, 5, out var rowid) && rowid is null); // ROWID 는 비운다
            Assert.True(m.TryGetValue(2, 4, out var comm) && comm is null);
            Assert.Contains("2행을 새 행으로 끝에 붙여 넣었습니다", pane.StatusText, StringComparison.Ordinal);
            Assert.Contains("ROWID·읽기 전용 열 1개는 비웠습니다", pane.StatusText, StringComparison.Ordinal);

            var preview = s!.BuildPreview();
            // NULL(COMM) 은 넣지 않아 INSERT 에서 빠진다(열 DEFAULT), 값은 바인드로만.
            Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, SAL) VALUES (:v1, :v2, :v3, :v4);", preview, StringComparison.Ordinal);
            Assert.Contains(":v1 = 7369, :v2 = 'SMITH', :v3 = 1980-12-17 00:00:00, :v4 = 800", preview, StringComparison.Ordinal);
            Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, SAL, COMM) VALUES (:v1, :v2, :v3, :v4, :v5);", preview, StringComparison.Ordinal);
            Assert.Contains(":v2 = 'O''NEIL'", preview, StringComparison.Ordinal);
            Assert.DoesNotContain("ROWID", preview, StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Paste_rows_reads_external_tsv_by_column_kind_and_skips_a_header_line()
    {
        var (pane, m, s, clip) = await RowsPaneAsync(edit: true);
        try
        {
            m.SelectCell(new GridCell(0, 0));
            await RunGrid("oranda.grid.copyRows", m); // 앱 안 사본이 있어도
            clip.Text = "EMPNO\tENAME\tHIREDATE\tSAL\r\n8001\t\"A\tB\"\t2026-01-02 03:04:05\t1,000\r\n"; // 그 사이 Excel 에서 복사했다

            await RunOn("oranda.grid.edit.pasteRows", m);

            Assert.Equal(3, m.RowCount);
            Assert.True(m.TryGetValue(2, 0, out var no) && Equals(no, 8001m));
            Assert.True(m.TryGetValue(2, 1, out var name) && Equals(name, "A\tB"));
            Assert.True(m.TryGetValue(2, 2, out var hired) && Equals(hired, new DateTime(2026, 1, 2, 3, 4, 5)));
            Assert.True(m.TryGetValue(2, 3, out var sal) && Equals(sal, 1000m));
            Assert.Contains("1행을 새 행으로 끝에 붙여 넣었습니다 · 값이 4열뿐이라 나머지 2열은 비웠습니다.", pane.StatusText, StringComparison.Ordinal);
            Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, SAL) VALUES (:v1, :v2, :v3, :v4);", s!.BuildPreview(), StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Paste_rows_with_more_values_than_columns_keeps_the_leading_ones_and_says_so()
    {
        var (pane, m, s, clip) = await RowsPaneAsync(edit: true);
        try
        {
            clip.Text = "9001\tX\t2026-01-01\t10\t20\tROWIDX\textra1\textra2\n9002\tY\n";

            await RunOn("oranda.grid.edit.pasteRows", m);

            Assert.Equal(4, m.RowCount);
            Assert.True(m.TryGetValue(2, 4, out var comm) && Equals(comm, 20m));
            Assert.True(m.TryGetValue(2, 5, out var rowid) && rowid is null); // ROWID 칸의 값은 버린다
            Assert.True(m.TryGetValue(3, 1, out var y) && Equals(y, "Y"));
            Assert.Contains("2행을 새 행으로 끝에 붙여 넣었습니다 · 값이 8열이라 앞 6열만 맞추고 남는 2열은 버렸습니다 · ROWID·읽기 전용 열 1개는 비웠습니다.",
                pane.StatusText, StringComparison.Ordinal);
            var preview = s!.BuildPreview();
            Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, SAL, COMM) VALUES (:v1, :v2, :v3, :v4, :v5);", preview, StringComparison.Ordinal);
            Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME) VALUES (:v1, :v2);", preview, StringComparison.Ordinal);
            Assert.DoesNotContain("extra", preview, StringComparison.Ordinal);

            clip.Text = "\r\n";
            await RunOn("oranda.grid.edit.pasteRows", m);
            Assert.Equal(4, m.RowCount);
            Assert.Contains("클립보드에 붙여 넣을 행이 없습니다.", pane.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Duplicate_row_copies_below_the_result_clears_rowid_and_flags_the_key_until_changed()
    {
        var (pane, m, s, _) = await RowsPaneAsync(edit: true);
        try
        {
            m.SelectCell(new GridCell(1, 1));

            await RunOn("oranda.grid.edit.duplicateRow", m);

            Assert.Equal(3, m.RowCount);
            Assert.Equal(new GridCell(2, 1), m.Active); // 새 행으로, 같은 열
            Assert.Equal(GridRowMark.Inserted, m.RowMarkOf(2));
            Assert.True(m.TryGetValue(2, 1, out var name) && Equals(name, "O'NEIL"));
            Assert.True(m.TryGetValue(2, 4, out var comm) && Equals(comm, 300m));
            Assert.True(m.TryGetValue(2, 5, out var rowid) && rowid is null);
            Assert.True(m.NeedsChange(2, 0)); // 기본키는 값은 그대로, 경고 표시
            Assert.False(m.NeedsChange(2, 1));
            Assert.False(m.NeedsChange(1, 0));
            Assert.Contains("1행을 복제해 끝에 더했습니다 · 키 열(EMPNO) 값을 바꾼 뒤 반영하세요.", pane.StatusText, StringComparison.Ordinal);

            var added = GridEditBinding.ToEditRow(m, s!, 2);
            Assert.True(s!.SetCell(added, 0, 7999m));
            Assert.False(m.NeedsChange(2, 0)); // 바꾸면 경고가 사라진다
            var preview = s.BuildPreview();
            Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME, HIREDATE, SAL, COMM) VALUES (:v1, :v2, :v3, :v4, :v5);", preview, StringComparison.Ordinal);
            Assert.Contains(":v1 = 7999, :v2 = 'O''NEIL', :v3 = 1981-02-20 00:00:00, :v4 = 1600.5, :v5 = 300", preview, StringComparison.Ordinal);

            m.SelectCell(new GridCell(0, 0));
            m.ExtendTo(new GridCell(1, 0));
            await RunOn("oranda.grid.edit.duplicateRow", m); // 여러 행은 그 행들을 끝에, 커서는 첫 새 행
            Assert.Equal(5, m.RowCount);
            Assert.Equal(new GridCell(3, 0), m.Active);
            Assert.True(m.TryGetValue(3, 1, out var first) && Equals(first, "SMITH"));
            Assert.True(m.NeedsChange(3, 0) && m.NeedsChange(4, 0));

            s.ResetAll();
            Assert.False(m.NeedsChange(2, 0));
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Paste_and_duplicate_on_a_read_only_result_show_the_rerun_in_edit_mode_bar()
    {
        var (pane, m, _, clip) = await RowsPaneAsync(edit: false);
        try
        {
            m.SelectCell(new GridCell(0, 0));
            clip.Text = "1\tA";

            await RunOn("oranda.grid.edit.pasteRows", m);

            Assert.True(pane.HasEditHint);
            Assert.True(pane.OffersRerunForEdit);
            Assert.Contains("F9", pane.EditHint, StringComparison.Ordinal);
            Assert.Equal(2, m.RowCount);

            pane.HideEditHint();
            await RunOn("oranda.grid.edit.duplicateRow", m);
            Assert.True(pane.HasEditHint);
            Assert.Equal(2, m.RowCount);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Paste_on_a_keyless_edit_result_explains_why_instead_of_adding_rows()
    {
        var cols = Columns(("ENAME", ColumnDataKind.Text), ("SAL", ColumnDataKind.Number), ("COMM", ColumnDataKind.Number),
            ("A", ColumnDataKind.Text), ("B", ColumnDataKind.Text), ("C", ColumnDataKind.Text));
        var (pane, m, s, clip) = await RowsPaneAsync(edit: true, sql: "SELECT ename, sal, comm, a, b, c FROM emp", cols: cols);
        try
        {
            Assert.False(s!.CanEdit);
            pane.HideEditHint();
            clip.Text = "X\t1";

            await RunOn("oranda.grid.edit.pasteRows", m);

            Assert.True(pane.HasEditHint);
            Assert.True(pane.OffersRowIdRerun);
            Assert.Equal(2, m.RowCount);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public void Tsv_reader_handles_quotes_tabs_newlines_crlf_and_trailing_newline()
    {
        var rows = TsvReader.Parse("a\t\"b\tc\"\t\"d\"\"e\"\r\n\"line1\nline2\"\t\t\n\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b\tc", "d\"e"], rows[0]);
        Assert.Equal(["line1\nline2", "", ""], rows[1]);
        Assert.Empty(TsvReader.Parse(""));
        Assert.Equal(["x"], Assert.Single(TsvReader.Parse("x")));
    }

    [Fact]
    public void Every_preset_binds_the_row_commands_in_the_grid()
    {
        foreach (var id in KeymapLoader.PresetIds)
        {
            var bindings = KeymapLoader.LoadPreset(id).Bindings;
            foreach (var (command, key) in new[]
                     {
                         ("oranda.grid.copyRows", "Primary+Alt+C"),
                         ("oranda.grid.edit.duplicateRow", "Primary+Shift+D"),
                         ("oranda.grid.edit.pasteRows", "Primary+Shift+V"),
                     })
            {
                var b = Assert.Single(bindings, x => x.Command == command);
                Assert.Equal("gridFocus", b.When);
                Assert.Equal([key], b.KeysFor(macOS: false));
                Assert.Equal([key], b.KeysFor(macOS: true));
            }
        }
    }

    [Fact]
    public void Row_commands_match_commands_md_rows()
    {
        var doc = FindCommandsDoc();
        string[] ids = ["oranda.grid.copyRows", "oranda.grid.copyRowsWithHeaders", "oranda.grid.edit.duplicateRow", "oranda.grid.edit.pasteRows"];
        var rows = File.ReadAllLines(doc)
            .Where(l => ids.Any(i => l.StartsWith("| `" + i + "` |", StringComparison.Ordinal)))
            .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToDictionary(r => r[0].Trim('`'));
        var registered = ShellTestHelpers.AppContributions.Commands.ToDictionary(c => c.Definition.Id);
        Assert.Equal(ids.Order(), rows.Keys.Order());
        foreach (var id in ids)
        {
            var r = rows[id];
            Assert.True(registered.TryGetValue(id, out var reg), $"미등록 명령: {id}");
            Assert.Equal(r[3].Trim('`'), reg.Definition.When);
            Assert.Equal(r[4].Trim('`'), reg.Definition.Enablement);
            Assert.Contains(reg.Definition.Placements, p => p.StartsWith("context:grid/", StringComparison.Ordinal));
        }

        Assert.Equal(typeof(GridEditCommandHandler), registered["oranda.grid.edit.pasteRows"].HandlerType);
        Assert.Equal(typeof(ResultGridCommandHandler), registered["oranda.grid.copyRows"].HandlerType);
    }

    private static string FindCommandsDoc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", "design", "commands.md");
            if (File.Exists(p))
            {
                return p;
            }
        }

        throw new FileNotFoundException("docs/design/commands.md 를 찾지 못했다.");
    }
}
