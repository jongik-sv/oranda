using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>결과 탭 줄의 필터 단추: 끄면 열 필터가 풀리고 ▾ 가 사라진다. 마지막 상태는 새 결과가 이어받는다. 편집기 결과 탭이 아니면 바뀌지 않는다.</summary>
[Collection("ColumnFilterStatics")]
public sealed class ColumnFilterToggleTests : IDisposable
{
    private readonly Func<bool>? _oldProvider = SqlResultPaneViewModel.ColumnFilterDefaultProvider;
    private readonly Func<bool, Task>? _oldSaver = SqlResultPaneViewModel.ColumnFilterDefaultSaver;

    /// <summary>
    /// 기본(켬, 저장 안 함)에서 시작한다. 이 정적 값은 앱 전체를 띄우는 다른 시험의 ResultGridSettingsStartup 이 그 시험의 설정으로 바꿔 두고
    /// 되돌리지 않는다. 그 설정에 다른 시험(GridEditTests 등)이 「끔」 을 저장해 두면 여기 시험이 꺼진 채로 시작해 전체 실행에서만 매번 깨졌다(ci-flaky F10).
    /// </summary>
    public ColumnFilterToggleTests()
    {
        SqlResultPaneViewModel.ColumnFilterDefaultProvider = null;
        SqlResultPaneViewModel.ColumnFilterDefaultSaver = null;
    }

    public void Dispose()
    {
        SqlResultPaneViewModel.ColumnFilterDefaultProvider = _oldProvider;
        SqlResultPaneViewModel.ColumnFilterDefaultSaver = _oldSaver;
    }

    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "DEPT", "VARCHAR2", ColumnDataKind.Text),
    ];

    private sealed class Rows(int rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => true;

        public long? KnownRowCount => rows;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var ids = new object?[rows];
            var depts = new object?[rows];
            for (var i = 0; i < rows; i++)
            {
                ids[i] = (decimal)i;
                depts[i] = "D" + (i % 3);
            }

            return Task.FromResult(new ResultPage(pageIndex, 0, pageIndex == 0 ? rows : 0, pageIndex == 0 ? [ids, depts] : [[], []]) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<SqlResultPaneViewModel> OpenPaneAsync(int rows = 9, bool editorTab = true)
    {
        var pane = new SqlResultPaneViewModel();
        if (editorTab)
        {
            pane.UseColumnFilterDefault();
        }

        pane.SetResult(new Rows(rows));
        await pane.Model!.LoadFirstPageAsync();
        return pane;
    }

    private static ColumnFilter OnlyD1() => new() { Values = new HashSet<string> { "D1" }, IncludeNull = false };

    [Fact]
    public async Task Turning_off_clears_column_filters_keeps_the_quick_text_and_remembers_the_state()
    {
        var saved = new List<bool>();
        SqlResultPaneViewModel.ColumnFilterDefaultSaver = on =>
        {
            saved.Add(on);
            return Task.CompletedTask;
        };
        var pane = await OpenPaneAsync();
        var m = pane.Model!;
        await pane.SetColumnFilterAsync(1, OnlyD1());
        await m.SetLocalFilterAsync(m.LocalFilter with { QuickText = "1" });
        Assert.True(m.RowCount < 9);

        Assert.True(await pane.SetColumnFilterEnabledAsync(false));

        Assert.False(pane.ColumnFilterEnabled);
        Assert.False(m.ColumnFilterEnabled);
        Assert.Empty(m.LocalFilter.Columns); // 걸려 있던 열 필터가 풀린다
        Assert.Equal("1", m.LocalFilter.QuickText); // 빠른 거르기 글은 그대로
        Assert.Equal([false], saved);

        Assert.True(await pane.SetColumnFilterEnabledAsync(true));
        Assert.True(m.ColumnFilterEnabled);
        Assert.Equal([false, true], saved);
        pane.Clear();
    }

    [Fact]
    public async Task Turning_off_is_refused_while_unsaved_edits_block_clearing_the_filter()
    {
        var pane = await OpenPaneAsync();
        var m = pane.Model!;
        await pane.SetColumnFilterAsync(1, OnlyD1());
        m.EditOverlay = new PendingOverlay();

        Assert.False(await pane.SetColumnFilterEnabledAsync(false));

        Assert.True(pane.ColumnFilterEnabled); // 필터를 지울 길이 사라진 채로 남기지 않는다
        Assert.True(m.ColumnFilterEnabled);
        Assert.NotEmpty(m.LocalFilter.Columns);
        pane.Clear();
    }

    [Fact]
    public async Task A_new_result_starts_from_the_last_saved_state_and_a_reload_keeps_the_panes_state()
    {
        SqlResultPaneViewModel.ColumnFilterDefaultProvider = () => false;
        var pane = new SqlResultPaneViewModel();
        pane.UseColumnFilterDefault();
        pane.SetResult(new Rows(3));

        Assert.False(pane.ColumnFilterEnabled);
        Assert.False(pane.Model!.ColumnFilterEnabled);

        SqlResultPaneViewModel.ColumnFilterDefaultProvider = () => true;
        pane.SetResult(new Rows(4)); // 같은 영역에서 결과를 바꿔도(서버 거르기·정렬 다시 조회) 영역의 상태를 잇는다
        Assert.False(pane.Model!.ColumnFilterEnabled);
        await Task.CompletedTask;
        pane.Clear();
    }

    [Fact]
    public async Task Panes_outside_the_editor_result_tabs_never_change_or_save()
    {
        var saved = new List<bool>();
        SqlResultPaneViewModel.ColumnFilterDefaultSaver = on =>
        {
            saved.Add(on);
            return Task.CompletedTask;
        };
        var pane = await OpenPaneAsync(editorTab: false); // 객체 상세 등: UseColumnFilterDefault 를 부르지 않는다

        Assert.False(pane.ColumnFilterToggleable);
        Assert.False(await pane.SetColumnFilterEnabledAsync(false));
        pane.Model!.RequestToggleColumnFilter();
        await Task.Delay(50);

        Assert.True(pane.ColumnFilterEnabled);
        Assert.True(pane.Model.ColumnFilterEnabled);
        Assert.Empty(saved);
        pane.Clear();
    }

    [Fact]
    public async Task The_toggle_command_event_flips_the_state()
    {
        var pane = await OpenPaneAsync();

        pane.Model!.RequestToggleColumnFilter();
        await HeadlessHost.WaitUntil(() => !pane.ColumnFilterEnabled);

        Assert.False(pane.Model.ColumnFilterEnabled);
        pane.Clear();
    }

    [Fact]
    public Task With_the_filter_off_the_header_arrow_does_nothing_and_the_button_follows_the_selected_tab() => HeadlessHost.Run(async () =>
    {
        var vm = new ResultTabsViewModel();
        vm.AddQuery(new Rows(3), "결과 1", null);
        vm.AddQuery(new Rows(3), "결과 2", null);
        vm.AddMessage("메시지", "끝", isError: false);
        vm.Selected = vm.Tabs[0];
        var view = new ResultTabsView { DataContext = vm };
        var window = new Window { Width = 700, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var button = view.FindControl<ToggleButton>("ColumnFilterButton")!;
            Assert.True(button.IsEnabled);
            Assert.True(button.IsChecked);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => !vm.Tabs[0].Pane!.ColumnFilterEnabled);
            Assert.False(button.IsChecked);

            var grid = new ResultGridControl { Model = vm.Tabs[0].Pane!.Model };
            var raised = 0;
            grid.ColumnFilterRequested += (_, _) => raised++;
            grid.RequestColumnFilter(0);
            Assert.Equal(0, raised); // 꺼진 결과는 ▾ 팝업 요청을 내지 않는다

            vm.Selected = vm.Tabs[1]; // 다른 탭은 자기 상태: 켬
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsChecked);

            vm.Selected = vm.Tabs[2]; // 메시지 탭
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class PendingOverlay : IGridEditOverlay
    {
        public int ExtraRows => 0;

        public bool HasPendingChanges => true;

        public bool TryGetValue(long viewRow, int ordinal, out object? value)
        {
            value = null;
            return false;
        }

        public bool IsEdited(long viewRow, int ordinal) => false;

        public GridRowMark RowMark(long viewRow) => GridRowMark.None;

        public long BaseRowOf(long viewRow) => viewRow;

        public bool NeedsChange(long viewRow, int ordinal) => false;
    }
}
