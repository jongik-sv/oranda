using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.GridEdit;

public sealed partial class GridEditTests
{
    /// <summary>결과 탭 하나를 가진 결과 영역 묶음: 편집기 탭이 소유하고, 결과는 읽기 결과 또는 편집 모드(편집 세션 붙음).</summary>
    private async Task<(ResultTabsViewModel Tabs, SqlResultPaneViewModel Pane, SqlStatement Statement)> TabsWithResultAsync(
        bool edit, StatementKind kind = StatementKind.Query, bool uncertain = false, ResultColumn[]? cols = null, object?[][]? rows = null, string sql = EmpRowsSql)
    {
        var tabs = new ResultTabsViewModel { OwnerTab = new SqlEditorViewModel(Guid.NewGuid(), EmpRowsSql) };
        var stmt = new SqlStatement(sql, default, kind) { Verb = kind == StatementKind.Query ? "SELECT" : "BEGIN", IsClassificationUncertain = uncertain };
        var item = tabs.AddQuery(new MemoryResult(cols ?? EmpRowCols, rows ?? EmpRows()), "결과 1", stmt);
        await RowsPaneAsync(edit, sql, into: item.Pane, cols: cols, rows: rows);
        item.Pane!.Statement = stmt; // RowsPaneAsync 가 조회 문장으로 덮은 것을 되돌린다
        tabs.Selected = item;
        return (tabs, item.Pane!, stmt);
    }

    [Fact]
    public Task The_edit_mode_button_is_on_in_edit_mode_off_for_read_only_connections_and_while_running() => HeadlessHost.Run(async () =>
    {
        GridEditCommands.Rerun = (_, _) => Task.CompletedTask;
        var (tabs, pane, _) = await TabsWithResultAsync(edit: false);
        var view = new ResultTabsView { DataContext = tabs };
        var window = new Window { Content = view, Width = 700, Height = 300 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<ToggleButton>("EditModeButton")!;
            Assert.True(button.IsEnabled);
            Assert.False(button.IsChecked); // 읽기 결과: 꺼진 모양

            tabs.IsRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEnabled); // 실행 중에는 못 누른다
            tabs.IsRunning = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEnabled);

            tabs.SetProfile(new Oranda.Core.Connection.ConnectionProfile("ro", "scott", "db.example:1521/svc", ReadOnly: true));
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEnabled); // 읽기 전용 접속
            tabs.SetProfile(null);
            Dispatcher.UIThread.RunJobs();

            await RowsPaneAsync(edit: true, into: pane); // 같은 영역에 편집 세션이 붙는다
            Dispatcher.UIThread.RunJobs();
            Assert.True(pane.IsEditMode);
            Assert.True(button.IsChecked); // 편집 모드: 켜진 모양
            Assert.True(button.IsEnabled);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Pressing_the_button_reruns_the_same_statement_for_edit_and_pressing_again_runs_it_as_a_plain_read() => HeadlessHost.Run(async () =>
    {
        var forEdit = new List<SqlStatement?>();
        var plain = new List<SqlStatement?>();
        GridEditCommands.Rerun = (s, _) =>
        {
            forEdit.Add(s);
            return Task.CompletedTask;
        };
        GridEditCommands.RerunReadOnly = (s, _) =>
        {
            plain.Add(s);
            return Task.CompletedTask;
        };
        var (tabs, pane, stmt) = await TabsWithResultAsync(edit: false);
        var view = new ResultTabsView { DataContext = tabs };
        var window = new Window { Content = view, Width = 700, Height = 300 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<ToggleButton>("EditModeButton")!;

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => forEdit.Count == 1);
            Assert.Same(stmt, forEdit[0]); // 편집기 커서 문장이 아니라 이 결과를 만든 문장
            Assert.Empty(plain);

            await RowsPaneAsync(edit: true, into: pane); // 다시 실행한 결과가 편집 모드로 열렸다고 본다
            Dispatcher.UIThread.RunJobs();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => plain.Count == 1);
            Assert.Same(stmt, plain[0]);
            Assert.Single(forEdit);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Leaving_edit_mode_with_unsaved_changes_asks_first_and_stays_when_declined() => HeadlessHost.Run(async () =>
    {
        var plain = 0;
        GridEditCommands.RerunReadOnly = (_, _) =>
        {
            plain++;
            return Task.CompletedTask;
        };
        var (tabs, pane, _) = await TabsWithResultAsync(edit: true);
        var session = GridEditBinding.Of(pane.Model!)!;
        session.SetCell(0, 1, "CHANGED"); // 반영하지 않은 변경
        Assert.True(pane.IsEditDirty);
        var asked = 0;
        var answer = false;
        pane.ConfirmDiscardEdits = _ =>
        {
            asked++;
            return Task.FromResult(answer);
        };

        await tabs.ToggleEditModeAsync();
        Assert.Equal(1, asked);
        Assert.Equal(0, plain); // 아니라고 하면 편집 모드 그대로
        Assert.True(pane.IsEditMode);

        answer = true;
        await tabs.ToggleEditModeAsync();
        Assert.Equal(2, asked);
        Assert.Equal(1, plain);

        pane.ConfirmDiscardEdits = null; // 물을 수 없으면 조용히 버리지 않는다
        await tabs.ToggleEditModeAsync();
        Assert.Equal(1, plain);
        pane.Clear();
    });

    [Fact]
    public Task Leaving_edit_mode_without_changes_does_not_ask() => HeadlessHost.Run(async () =>
    {
        var plain = 0;
        GridEditCommands.RerunReadOnly = (_, _) =>
        {
            plain++;
            return Task.CompletedTask;
        };
        var (tabs, pane, _) = await TabsWithResultAsync(edit: true);
        var asked = 0;
        pane.ConfirmDiscardEdits = _ =>
        {
            asked++;
            return Task.FromResult(true);
        };

        await tabs.ToggleEditModeAsync();

        Assert.Equal(0, asked);
        Assert.Equal(1, plain);
        pane.Clear();
    });

    [Fact]
    public Task An_area_that_does_not_belong_to_an_editor_tab_cannot_toggle_edit_mode() => HeadlessHost.Run(async () =>
    {
        GridEditCommands.Rerun = (_, _) => Task.CompletedTask;
        var (tabs, pane, _) = await TabsWithResultAsync(edit: false);
        Assert.True(tabs.CanToggleEditMode);

        tabs.OwnerTab = null; // PL/SQL 실행 창 같은 결과 영역: 편집기 호스트에도 없다
        Assert.False(tabs.CanToggleEditMode);
        pane.Clear();
    });

    [Theory]
    [InlineData(StatementKind.PlSql, false)]
    [InlineData(StatementKind.Dml, false)]
    [InlineData(StatementKind.Other, false)]
    [InlineData(StatementKind.Query, true)] // 분류가 불확실한데 행을 돌려준 문장(스크립트의 마지막 결과 등)
    public Task Side_effect_or_uncertain_statements_never_enable_the_button_nor_rerun(StatementKind kind, bool uncertain) => HeadlessHost.Run(async () =>
    {
        var reruns = 0;
        GridEditCommands.Rerun = (_, _) =>
        {
            reruns++;
            return Task.CompletedTask;
        };
        GridEditCommands.RerunReadOnly = (_, _) =>
        {
            reruns++;
            return Task.CompletedTask;
        };
        var (tabs, pane, _) = await TabsWithResultAsync(edit: false, kind, uncertain);
        Assert.False(tabs.CanToggleEditMode); // 켜기

        await tabs.ToggleEditModeAsync();
        Assert.Equal(0, reruns);

        await RowsPaneAsync(edit: true, into: pane); // 편집 세션이 붙어 있어도(돌아가기) 같다
        pane.Statement = new SqlStatement(EmpRowsSql, default, kind) { Verb = "BEGIN", IsClassificationUncertain = uncertain };
        Assert.False(tabs.CanToggleEditMode);
        await tabs.ToggleEditModeAsync();
        Assert.Equal(0, reruns);
        pane.Clear();
    });

    [Fact]
    public Task The_other_rerun_entrances_also_need_a_plain_query_statement() => HeadlessHost.Run(async () =>
    {
        GridEditCommands.Rerun = (_, _) => Task.CompletedTask;
        var (_, pane, _) = await TabsWithResultAsync(edit: false, StatementKind.PlSql);
        pane.ShowEditHint(); // PL/SQL 블록의 REF CURSOR 결과에서 셀을 고치려 했다

        Assert.True(pane.HasEditHint);
        Assert.False(pane.CanStartEditMode);
        Assert.False(pane.OffersRerunForEdit); // 안내 막대에 「편집 모드로 다시 실행」 이 나오지 않는다 — 블록 전체가 다시 실행되기 때문

        pane.Statement = new SqlStatement(EmpRowsSql, default, StatementKind.Query) { Verb = "SELECT" };
        pane.ShowEditHint();
        Assert.True(pane.OffersRerunForEdit);
        pane.Clear();
    });

    [Fact]
    public Task A_result_that_cannot_be_edited_after_the_rerun_can_still_be_left_with_the_button() => HeadlessHost.Run(async () =>
    {
        var plain = 0;
        GridEditCommands.RerunReadOnly = (_, _) =>
        {
            plain++;
            return Task.CompletedTask;
        };
        var keyless = EmpRowCols.Where(c => c.Name is "ENAME" or "SAL").ToArray(); // 키(EMPNO)가 없는 결과: 편집 불가
        var (tabs, pane, _) = await TabsWithResultAsync(edit: true, cols: keyless, rows: [["SMITH", 800m]], sql: "SELECT ename, sal FROM emp");
        Assert.True(pane.HasEditSession);
        Assert.False(pane.IsEditMode); // 고칠 수 없다

        Assert.True(tabs.CanToggleEditMode); // 그래도 읽기 결과로 돌아갈 길은 열어 둔다
        await tabs.ToggleEditModeAsync();

        Assert.Equal(1, plain);
        pane.Clear();
    });

    [Fact]
    public Task Leaving_edit_mode_clears_the_active_edit_session_so_apply_commands_cannot_reach_it() => HeadlessHost.Run(async () =>
    {
        var (pane, _, session, _) = await RowsPaneAsync(edit: true);
        Assert.Same(session, GridEditCommands.Active);

        pane.Clear(); // 읽기 결과로 돌아가면 옛 결과 영역이 정리된다

        Assert.Null(GridEditCommands.Active);
    });

    [Fact]
    public Task The_button_commits_an_open_cell_editor_first_so_typed_text_is_asked_about_not_lost() => HeadlessHost.Run(async () =>
    {
        var plain = 0;
        GridEditCommands.RerunReadOnly = (_, _) =>
        {
            plain++;
            return Task.CompletedTask;
        };
        var (pane, m, _, _) = await RowsPaneAsync(edit: true);
        var asked = 0;
        pane.ConfirmDiscardEdits = _ =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        try
        {
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            var editor = view.FindControl<TextBox>("CellEditor")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0 && m.LoadedRowCount == 2);
            Dispatcher.UIThread.RunJobs();
            var p = grid.TranslatePoint(new Point(grid.RowHeaderWidth + m.GetColumnWidth(0) + 12, grid.HeaderHeight + (grid.RowHeight / 2)), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsVisible);
            editor.Text = "TYPED"; // 아직 확정하지 않은 입력: 단추는 포커스를 받지 않아 LostFocus 확정이 일어나지 않는다
            Assert.False(pane.IsEditDirty);

            await pane.ToggleEditModeAsync();

            Assert.False(editor.IsVisible);
            Assert.Equal(1, asked); // 친 값이 변경으로 잡혀 먼저 물었다
            Assert.Equal(0, plain); // 아니라고 했으니 돌아가지 않았다
            Assert.True(m.TryGetValue(0, m.VisibleColumns[1], out var kept));
            Assert.Equal("TYPED", kept);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Turning_the_column_filter_off_commits_an_open_cell_editor_first() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true);
        pane.UseColumnFilterDefault();
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        try
        {
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            var editor = view.FindControl<TextBox>("CellEditor")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0 && m.LoadedRowCount == 2);
            Dispatcher.UIThread.RunJobs();
            var p = grid.TranslatePoint(new Point(grid.RowHeaderWidth + m.GetColumnWidth(0) + 12, grid.HeaderHeight + (grid.RowHeight / 2)), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            editor.Text = "TYPED";

            await pane.SetColumnFilterEnabledAsync(false);

            Assert.False(editor.IsVisible);
            Assert.True(session!.IsDirty);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });
}
