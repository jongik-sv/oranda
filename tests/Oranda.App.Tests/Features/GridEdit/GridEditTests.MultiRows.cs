using System.Diagnostics;
using Avalonia.Input;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>
/// TSK-11-01 결과 격자 여러 행: 고른 모든 행(칸 범위·행 머리 Shift·Ctrl)에 삭제·되돌리기·행 복사·행 복제,
/// 메뉴 이름의 개수, 편집 모드 자동 켜기, 조회 전용 막힘, 단축키, 1만 행 시간.
/// </summary>
public sealed partial class GridEditTests
{
    private async Task<(SqlResultPaneViewModel Pane, ResultGridModel Grid, GridEditSession? Session, FakeClipboard Clip)> ManyAsync(
        int rows, bool edit = true, bool readOnly = false, int seed = 0)
    {
        var data = new object?[rows][];
        for (var i = 0; i < rows; i++)
        {
            data[i] = [1000m + i + seed, "N" + i, new DateTime(1980, 1, 1).AddDays(i), 100m + (i * 7 % 13), null, "ROWID" + i];
        }

        var stmt = new SqlStatement(EmpRowsSql, default, StatementKind.Query) { Verb = "SELECT" };
        var pane = new SqlResultPaneViewModel { Statement = stmt };
        pane.SetResult(new Pages500Result(EmpRowCols, data));
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
            s = await GridEditSession.OpenAsync(new RowEditor(_exec), meta, new TestSession(readOnly), stmt, m.Columns,
                GridEditBinding.OriginalValues(m), () => m.LoadedRowCount, target: null, default);
            GridEditBinding.Attach(m, s);
            pane.SetEditSession(s);
            GridEditCommands.Active = s;
        }

        ResultGridCommands.Active = m;
        return (pane, m, s, clip);
    }

    /// <summary>결과 영역의 페이지 크기(500행)대로 나눠 돌려주는 메모리 결과(1만 행 시험에서 페이지를 여러 번 가져온다).</summary>
    private sealed class Pages500Result(ResultColumn[] columns, object?[][] rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => true;

        public long? KnownRowCount => rows.Length;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var first = pageIndex * ResultGridModel.DefaultPageSize;
            var n = Math.Clamp(rows.Length - first, 0, ResultGridModel.DefaultPageSize);
            var cols = new List<IReadOnlyList<object?>>();
            for (var c = 0; c < columns.Length; c++)
            {
                cols.Add([.. rows.Skip(first).Take(n).Select(r => r[c])]);
            }

            return Task.FromResult(new ResultPage(pageIndex, first, n, cols) { IsLast = first + n >= rows.Length });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static int Deleted(GridEditSession s, int rows) => Enumerable.Range(0, rows).Count(r => s.GetRowState(r) == GridEditRowState.Deleted);

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public async Task Range_of_three_rows_deletes_all_three_where_only_the_active_row_was_deleted_before()
    {
        // 바꾸기 전에 잰 동작: 행 3개를 골라 지우면 활성 행 하나만 삭제 표시됐다(Actual 1). 고친 뒤에는 3개.
        var (pane, m, s, _) = await ManyAsync(10);
        try
        {
            m.SelectRow(2);
            m.SelectRow(4, extend: true);
            Assert.Equal(3, m.SelectedRowCount);

            await RunOn("oranda.grid.edit.deleteRow", m);

            Assert.Equal(3, Deleted(s!, 10));
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Cell_range_across_three_rows_deletes_all_three_and_the_preview_has_three_deletes()
    {
        var (pane, m, s, _) = await ManyAsync(10);
        try
        {
            m.SelectCell(new GridCell(3, 1)); // 칸 범위(열 하나)가 걸친 행도 행 전체
            m.ExtendTo(new GridCell(5, 1));

            await RunOn("oranda.grid.edit.deleteRow", m);

            Assert.Equal([3, 4, 5], Enumerable.Range(0, 10).Where(r => s!.GetRowState(r) == GridEditRowState.Deleted));
            Assert.Equal(3, Count(s!.BuildPreview(), "DELETE FROM SCOTT.EMP"));
            Assert.Equal(new GridCell(5, 1), m.Active); // 삭제 표시한 행은 그대로 있어 선택이 남는다
            Assert.Contains("3행을 삭제 표시했습니다(반영하면 DELETE 3건이 실행됩니다).", pane.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Ctrl_picked_separate_rows_are_all_deleted_and_toggle_off_removes_one()
    {
        var (pane, m, s, _) = await ManyAsync(12);
        try
        {
            m.SelectRow(1);
            m.ToggleRowInSelection(5);
            m.ToggleRowInSelection(8);
            m.SelectRow(10, extend: true); // Shift 로 이어서 8~10
            Assert.Equal([(1L, 1L), (5L, 5L), (8L, 10L)], m.SelectedRowRanges());
            Assert.Equal(5, m.SelectedRowCount);
            Assert.True(m.IsSelected(5, 3) && m.IsSelected(9, 0) && !m.IsSelected(6, 0));

            m.ToggleRowInSelection(5); // 이미 고른 행을 다시 Ctrl+클릭하면 뺀다
            Assert.Equal([(1L, 1L), (8L, 10L)], m.SelectedRowRanges());
            m.ToggleRowInSelection(5);

            await RunOn("oranda.grid.edit.deleteRow", m);

            Assert.Equal([1, 5, 8, 9, 10], Enumerable.Range(0, 12).Where(r => s!.GetRowState(r) == GridEditRowState.Deleted));
            Assert.Equal(5, Count(s!.BuildPreview(), "DELETE FROM SCOTT.EMP"));

            m.SelectCell(new GridCell(0, 0)); // 일반 선택으로 돌아가면 떨어진 행이 지워진다
            Assert.Equal([(0L, 0L)], m.SelectedRowRanges());
            m.ToggleRowInSelection(0); // 하나뿐인 행은 빼지 않는다
            Assert.Equal(1, m.SelectedRowCount);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Mixed_new_and_existing_rows_remove_the_new_ones_and_mark_the_old_ones()
    {
        var (pane, m, s, _) = await ManyAsync(10);
        try
        {
            s!.AddRow();
            s.AddRow();
            Assert.Equal(12, m.RowCount);
            m.SelectRow(8); // 기존 8, 9 와 새 행 둘
            m.SelectRow(11, extend: true);

            await RunOn("oranda.grid.edit.deleteRow", m);

            Assert.Equal(10, m.RowCount); // 새 행은 바로 없어졌다
            Assert.Empty(s.InsertedRows);
            Assert.Equal([8, 9], Enumerable.Range(0, 10).Where(r => s.GetRowState(r) == GridEditRowState.Deleted));
            Assert.Equal(2, Count(s.BuildPreview(), "DELETE FROM SCOTT.EMP"));
            Assert.Equal(new GridCell(8, 5), m.Active); // 행이 당겨졌으니 선택은 첫 행 자리 하나로 접힌다
            Assert.Equal(1, m.SelectedRowCount);
            Assert.Contains("새 행 2개는 바로 없앴습니다", pane.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Deleting_new_rows_in_the_middle_does_not_shift_the_other_new_rows()
    {
        var (pane, m, s, _) = await ManyAsync(4);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                s!.AddRowWith([KeyValuePair.Create(0, (object?)(9000m + i))]);
            }

            m.SelectRow(4); // 새 행 0번
            m.ToggleRowInSelection(6); // 새 행 2번
            await RunOn("oranda.grid.edit.deleteRow", m);

            Assert.Equal(7, m.RowCount);
            var left = Enumerable.Range(4, 3).Select(r => m.TryGetValue(r, 0, out var v) ? v : null).ToList();
            Assert.Equal([9001m, 9003m, 9004m], left); // 0번·2번만 없어지고 1·3·4번이 남는다
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Reset_row_reverts_every_selected_row_and_single_row_still_works()
    {
        var (pane, m, s, _) = await ManyAsync(10);
        try
        {
            m.SelectRow(2);
            m.SelectRow(4, extend: true);
            m.ToggleRowInSelection(7);
            await RunOn("oranda.grid.edit.deleteRow", m);
            Assert.True(s!.SetCell(0, 1, "EDIT")); // 선택 밖 행의 수정은 남는다
            Assert.Equal(4, Deleted(s, 10));

            await RunOn("oranda.grid.edit.resetRow", m);

            Assert.Equal(0, Deleted(s, 10));
            Assert.Equal(GridEditRowState.Modified, s.GetRowState(0));
            Assert.Contains("4행을 되돌렸습니다.", pane.StatusText, StringComparison.Ordinal);

            m.SelectCell(new GridCell(3, 2)); // 단일 행: 같은 동작
            await RunOn("oranda.grid.edit.deleteRow", m);
            Assert.Equal([3], Enumerable.Range(0, 10).Where(r => s.GetRowState(r) == GridEditRowState.Deleted));
            Assert.Equal(1, Count(s.BuildPreview(), "DELETE FROM SCOTT.EMP"));
            await RunOn("oranda.grid.edit.resetRow", m);
            Assert.Equal(0, Deleted(s, 10));

            // 행 번호(long)를 주면 그 행 하나(기존 호출 방식)
            await new GridEditCommandHandler().ExecuteAsync(new Oranda.Core.Shell.CommandContext("oranda.grid.edit.deleteRow", new NoKeys(), 6L), default);
            Assert.Equal(GridEditRowState.Deleted, s.GetRowState(6));
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Delete_rows_raises_changed_once_and_skips_rows_already_deleted()
    {
        var (pane, _, s, _) = await ManyAsync(10);
        try
        {
            var changed = 0;
            s!.Changed += (_, _) => changed++;
            s.DeleteRow(2);
            changed = 0;

            var r = s.DeleteRows([1, 2, 3, 4, -9]);

            Assert.Equal(1, changed);
            Assert.Equal(new RowsDeleted(3, 0, 2), r); // 2번은 이미 지움, -9 는 없는 새 행
            changed = 0;
            s.ResetRows([1, 2, 3, 4]);
            Assert.Equal(1, changed);
            Assert.False(s.IsDirty);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Copy_rows_copies_every_selected_row_in_screen_order_with_and_without_headers()
    {
        var (pane, m, _, clip) = await ManyAsync(10, edit: false);
        try
        {
            m.SelectRow(1);
            m.ToggleRowInSelection(6);
            m.ToggleRowInSelection(3); // 누른 순서가 아니라 화면 순서
            await RunGrid("oranda.grid.copyRows", m);

            var lines = clip.Text!.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("1001\t", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("1003\t", lines[1], StringComparison.Ordinal);
            Assert.StartsWith("1006\t", lines[2], StringComparison.Ordinal);
            Assert.Equal(3, ResultGridCommands.CopiedRows!.Rows.Count);

            await RunGrid("oranda.grid.copyRowsWithHeaders", m);
            Assert.StartsWith("EMPNO\tENAME\tHIREDATE\tSAL\tCOMM\tROWID\n1001\t", clip.Text, StringComparison.Ordinal);
            Assert.Equal(4, clip.Text!.Split('\n').Length);

            m.SelectCell(new GridCell(2, 2)); // 칸 범위: 걸친 행 전체
            m.ExtendTo(new GridCell(4, 2));
            await RunGrid("oranda.grid.copyRows", m);
            Assert.Equal(3, clip.Text!.Split('\n').Length);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Copy_rows_follows_the_sorted_screen_order()
    {
        var (pane, m, _, clip) = await ManyAsync(10, edit: false);
        try
        {
            await m.ToggleSortAsync(0);
            await m.ToggleSortAsync(0); // 오름 → 내림
            Assert.True(m.TryGetValue(0, 0, out var top) && Equals(top, 1009m));
            m.SelectRow(0);
            m.SelectRow(2, extend: true);

            await RunGrid("oranda.grid.copyRows", m);

            var lines = clip.Text!.Split('\n');
            Assert.Equal(["1009", "1008", "1007"], lines.Select(l => l.Split('\t')[0]));
            m.ToggleRowInSelection(5);
            Assert.Equal(4, m.SelectedRowCount);
            await m.ToggleSortAsync(1); // 정렬이 바뀌면 떨어진 행 번호는 뜻이 없어 지운다
            Assert.Single(m.SelectedRowRanges());
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Duplicate_row_duplicates_every_selected_row_including_separate_ones()
    {
        var (pane, m, s, _) = await ManyAsync(10);
        try
        {
            m.SelectRow(1);
            m.ToggleRowInSelection(4);
            m.ToggleRowInSelection(7);

            await RunOn("oranda.grid.edit.duplicateRow", m);

            Assert.Equal(13, m.RowCount);
            Assert.Equal(3, s!.InsertedRows.Count());
            Assert.Equal([1001m, 1004m, 1007m], Enumerable.Range(10, 3).Select(r => m.TryGetValue(r, 0, out var v) ? v : null));
            Assert.Equal(new GridCell(10, 5), m.Active);
            Assert.Equal(3, Count(s.BuildPreview(), "INSERT INTO SCOTT.EMP"));
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Delete_and_duplicate_turn_edit_mode_on_first_and_act_on_the_same_rows_in_the_new_result()
    {
        var (plain, m0, _, _) = await ManyAsync(10, edit: false);
        var (edited, m1, s1, _) = await ManyAsync(10);
        var oldKeys = ContextKeyService.Shared;
        try
        {
            var reruns = 0;
            GridEditCommands.Active = null;
            GridEditCommands.Rerun = (_, _) =>
            {
                reruns++;
                GridEditCommands.Active = s1; // 실행 화면이 편집 모드 결과를 열고 활성 세션으로 둔다
                return Task.CompletedTask;
            };
            ContextKeyService.Shared = new ContextKeyService();
            Assert.True(plain.CanStartEditMode);

            m0.SelectRow(2);
            m0.SelectRow(4, extend: true);
            m0.ToggleRowInSelection(8);
            await RunOn("oranda.grid.edit.deleteRow", m0);

            Assert.Equal(1, reruns);
            Assert.Equal([2, 3, 4, 8], Enumerable.Range(0, 10).Where(r => s1!.GetRowState(r) == GridEditRowState.Deleted));
            Assert.Equal(4, edited.Model!.SelectedRowCount);
            Assert.False(plain.HasEditHint);

            s1!.ResetAll();
            GridEditCommands.Active = null;
            m0.SelectRow(1);
            await RunOn("oranda.grid.edit.duplicateRow", m0);
            Assert.Equal(11, m1.RowCount);
            Assert.Equal(2, reruns);
        }
        finally
        {
            ContextKeyService.Shared = oldKeys;
            plain.Clear();
            edited.Clear();
        }
    }

    [Fact]
    public async Task Auto_edit_mode_stops_when_the_reread_rows_differ_from_the_chosen_ones()
    {
        var (plain, m0, _, _) = await ManyAsync(10, edit: false);
        var (edited, _, s1, _) = await ManyAsync(10, seed: 500); // 다시 읽었더니 값이 다른 행
        var oldKeys = ContextKeyService.Shared;
        try
        {
            GridEditCommands.Active = null;
            GridEditCommands.Rerun = (_, _) =>
            {
                GridEditCommands.Active = s1;
                return Task.CompletedTask;
            };
            ContextKeyService.Shared = new ContextKeyService();
            m0.SelectRow(2);

            await RunOn("oranda.grid.edit.deleteRow", m0);

            Assert.Equal(0, Deleted(s1!, 10)); // 엉뚱한 행을 지우지 않는다
            Assert.Contains("행이 달라졌습니다", edited.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            ContextKeyService.Shared = oldKeys;
            plain.Clear();
            edited.Clear();
        }
    }

    [Fact]
    public async Task Edit_mode_start_is_refused_for_read_only_sessions_and_unrunnable_results_with_the_hint_bar()
    {
        var (plain, m0, _, _) = await ManyAsync(5, edit: false);
        var oldKeys = ContextKeyService.Shared;
        try
        {
            var reruns = 0;
            GridEditCommands.Rerun = (_, _) => { reruns++; return Task.CompletedTask; };
            ContextKeyService.Shared = new ContextKeyService();
            ContextKeyService.Shared.Set("readOnlySession", true); // R6·읽기 전용 접속
            m0.SelectRow(1);

            await RunOn("oranda.grid.edit.deleteRow", m0);
            await RunOn("oranda.grid.edit.duplicateRow", m0);

            Assert.Equal(0, reruns);
            Assert.True(plain.HasEditHint);
            Assert.Equal(5, m0.RowCount);

            GridEditCommands.Rerun = null; // 다시 실행할 방법이 없는 결과
            Assert.False(plain.CanStartEditMode);
        }
        finally
        {
            ContextKeyService.Shared = oldKeys;
            plain.Clear();
        }
    }

    [Fact]
    public async Task Not_editable_result_keeps_delete_blocked_with_the_reason_bar()
    {
        var (pane, m, s, _) = await ManyAsync(5, readOnly: true); // 읽기 전용 접속의 편집 세션 = 편집 불가
        try
        {
            Assert.False(s!.CanEdit);
            pane.HideEditHint();
            m.SelectRow(1);
            m.SelectRow(3, extend: true);

            await RunOn("oranda.grid.edit.deleteRow", m);

            Assert.False(s.IsDirty);
            Assert.True(pane.HasEditHint);
            Assert.Contains("읽기 전용", pane.EditHint, StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public void Enablement_allows_rows_in_edit_mode_or_startable_results_and_blocks_read_only_sessions()
    {
        var defs = ShellTestHelpers.AppContributions.Commands.ToDictionary(c => c.Definition.Id, c => c.Definition);
        foreach (var id in new[] { "oranda.grid.edit.deleteRow", "oranda.grid.edit.duplicateRow" })
        {
            var expr = defs[id].Enablement;
            bool Eval(params string[] on) => ContextKeyReasons.Evaluate(expr, k => on.Contains(k));
            Assert.True(Eval("gridHasSelection", "gridRowsEditable"));
            Assert.False(Eval("gridRowsEditable")); // 고른 칸이 없다
            Assert.False(Eval("gridHasSelection")); // 편집할 수 없는 결과
            Assert.False(Eval("gridHasSelection", "gridRowsEditable", "readOnlySession")); // R6·읽기 전용 접속

            var why = ContextKeyReasons.Explain(expr, k => k is "gridHasSelection" or "gridRowsEditable" or "readOnlySession", k => k);
            Assert.Equal("Menu.disabled.not.readOnlySession", why);
            Assert.Equal("Menu.disabled.gridRowsEditable", ContextKeyReasons.Explain(expr, k => k == "gridHasSelection", k => k));
        }

        Assert.Equal("gridDirty", defs["oranda.grid.edit.resetRow"].Enablement);
    }

    [Fact]
    public async Task Menu_titles_show_the_row_count_only_when_more_than_one_row_is_selected()
    {
        var (pane, m, _, _) = await ManyAsync(10);
        try
        {
            var ko = new ShellText(language: "ko");
            var en = new ShellText(language: "en");
            m.SelectCell(new GridCell(2, 1));
            Assert.Null(GridRowTitles.For("oranda.grid.edit.deleteRow", ko)); // 하나면 기본 제목 「행 삭제」
            Assert.Equal("행 삭제", ko.Title(Def("oranda.grid.edit.deleteRow")));

            m.SelectRow(2);
            m.SelectRow(4, extend: true);
            Assert.Equal("행 3개 삭제", GridRowTitles.For("oranda.grid.edit.deleteRow", ko));
            Assert.Equal("행 3개 되돌리기", GridRowTitles.For("oranda.grid.edit.resetRow", ko));
            Assert.Equal("행 3개 복제", GridRowTitles.For("oranda.grid.edit.duplicateRow", ko));
            Assert.Equal("행 3개 복사", GridRowTitles.For("oranda.grid.copyRows", ko));
            Assert.Equal("머리글 포함 행 3개 복사", GridRowTitles.For("oranda.grid.copyRowsWithHeaders", ko));
            Assert.Equal("Delete 3 Rows", GridRowTitles.For("oranda.grid.edit.deleteRow", en));
            Assert.Null(GridRowTitles.For("oranda.grid.find", ko));

            var old = ShellText.TitleOverride;
            ShellText.TitleOverride = GridRowTitles.For; // 시작 때 연결되는 경로
            try
            {
                Assert.Equal("행 3개 삭제", ko.Title(Def("oranda.grid.edit.deleteRow")));
                Assert.Equal("Delete 3 Rows", en.Title(Def("oranda.grid.edit.deleteRow")));
            }
            finally
            {
                ShellText.TitleOverride = old;
            }
        }
        finally
        {
            pane.Clear();
        }
    }

    private static Oranda.Core.Shell.CommandDefinition Def(string id) =>
        ShellTestHelpers.AppContributions.Commands.Single(c => c.Definition.Id == id).Definition;

    [Theory]
    [InlineData("default")]
    [InlineData("orange5")]
    [InlineData("plsqldev")]
    [InlineData("sqldev")]
    public void Ctrl_D_and_Ctrl_Delete_go_to_the_row_commands_only_when_the_grid_has_focus(string preset)
    {
        var map = KeymapLoader.LoadPreset(preset);
        var table = KeyBindingTable.Build(map, macOS: false);
        var ctrlD = new List<(Key, KeyModifiers)> { (Key.D, KeyModifiers.Control) };
        var ctrlDelete = new List<(Key, KeyModifiers)> { (Key.Delete, KeyModifiers.Control) };
        var keys = new ContextKeyService();

        keys.SetFocusSlot("sqlGrid");
        Assert.Equal("oranda.grid.edit.duplicateRow", table.Find(ctrlD, keys).CommandId);
        Assert.Equal("oranda.grid.edit.deleteRow", table.Find(ctrlDelete, keys).CommandId);

        keys.SetFocusSlot("sqlText"); // 편집기에서는 줄 복제(있는 프리셋) 또는 아무 일도 없음 — 행 명령으로 가지 않는다
        var inEditor = table.Find(ctrlD, keys).CommandId;
        Assert.NotEqual("oranda.grid.edit.duplicateRow", inEditor);
        Assert.NotEqual("oranda.grid.edit.deleteRow", table.Find(ctrlDelete, keys).CommandId);
        if (preset != "sqldev")
        {
            Assert.Equal("oranda.edit.duplicateLine", inEditor);
        }

        // 지금 있던 키는 그대로
        var alt = new List<(Key, KeyModifiers)> { (Key.Delete, KeyModifiers.Alt) };
        var shiftD = new List<(Key, KeyModifiers)> { (Key.D, KeyModifiers.Control | KeyModifiers.Shift) };
        keys.SetFocusSlot("sqlGrid");
        Assert.Equal("oranda.grid.edit.deleteRow", table.Find(alt, keys).CommandId);
        Assert.Equal("oranda.grid.edit.duplicateRow", table.Find(shiftD, keys).CommandId);
    }

    [Fact]
    public async Task Ten_thousand_rows_select_all_delete_reset_and_copy_stay_fast_and_update_once()
    {
        var (pane, m, s, clip) = await ManyAsync(10_000);
        try
        {
            m.SelectAll();
            Assert.Equal(10_000, m.SelectedRowCount);
            var changed = 0;
            s!.Changed += (_, _) => changed++;

            var sw = Stopwatch.StartNew();
            await RunOn("oranda.grid.edit.deleteRow", m);
            var deleteMs = sw.ElapsedMilliseconds;
            Assert.Equal(1, changed);
            Assert.Equal(10_000, Count(s.BuildPreview(), "DELETE FROM SCOTT.EMP"));

            sw.Restart();
            await RunOn("oranda.grid.edit.resetRow", m);
            var resetMs = sw.ElapsedMilliseconds;
            Assert.False(s.IsDirty);

            sw.Restart();
            await RunGrid("oranda.grid.copyRows", m);
            var copyMs = sw.ElapsedMilliseconds;
            Assert.Equal(10_000, clip.Text!.Split('\n').Length);

            sw.Restart();
            await RunOn("oranda.grid.edit.duplicateRow", m);
            var dupMs = sw.ElapsedMilliseconds;
            Assert.Equal(20_000, m.RowCount);

            Console.WriteLine($"[TSK-11-01] 1만 행: 삭제 {deleteMs} ms, 되돌리기 {resetMs} ms, 행 복사 {copyMs} ms, 복제 {dupMs} ms");
            Assert.True(deleteMs < 5000 && resetMs < 5000 && copyMs < 5000 && dupMs < 8000,
                $"삭제 {deleteMs} 되돌리기 {resetMs} 복사 {copyMs} 복제 {dupMs} ms");
        }
        finally
        {
            pane.Clear();
        }
    }
}
