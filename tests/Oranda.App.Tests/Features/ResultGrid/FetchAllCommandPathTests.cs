using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Features.GridEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>
/// 「모두 가져오기」의 실제 경로: 주 창이 연결한 <see cref="ShellCommandRunner.Runner"/>·문맥 키·명령 레지스트리·도킹 결과 창을 그대로 지난다.
/// 실행 뒤 포커스는 편집기에 남으므로(ExecutionController) 단추는 포커스가 편집기에 있어도 동작해야 한다.
/// </summary>
[Collection("GridEditStatics")]
public sealed class FetchAllCommandPathTests
{
    private static readonly ResultColumn[] Cols = [new(0, "ID", "NUMBER", ColumnDataKind.Number)];

    /// <summary>쪽 번호대로 주는 결과. Gate 가 있으면 1쪽부터 문이 열릴 때까지 기다린다.</summary>
    private sealed class PagedSet(int rows, int pageSize = 500, Task? gate = null) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => false;

        public long? KnownRowCount => null;

        public int? PageSize => pageSize;

        public async Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            if (pageIndex > 0 && gate is not null)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }

            var first = pageIndex * pageSize;
            var count = Math.Clamp(rows - first, 0, pageSize);
            var ids = new object?[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = (decimal)(first + i);
            }

            return new ResultPage(pageIndex, first, count, [ids]) { IsLast = first + count >= rows };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>1쪽이 처음 한 번만 실패하는 결과(끊겼다 이어지는 가져오기).</summary>
    private sealed class FlakySet(int rows) : IResultSet
    {
        private int _failed;

        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => false;

        public long? KnownRowCount => null;

        public int? PageSize => 500;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            if (pageIndex == 1 && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                return Task.FromException<ResultPage>(new InvalidOperationException("ORA-03113"));
            }

            var first = pageIndex * 500;
            var count = Math.Clamp(rows - first, 0, 500);
            var ids = new object?[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = (decimal)(first + i);
            }

            return Task.FromResult(new ResultPage(pageIndex, first, count, [ids]) { IsLast = first + count >= rows });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>편집 세션을 붙일 EMPNO·ENAME 결과.</summary>
    private sealed class TwoRowSet(object?[][] rows) : IResultSet
    {
        private static readonly ResultColumn[] EmpCols =
        [
            new(0, "EMPNO", "NUMBER", ColumnDataKind.Number),
            new(1, "ENAME", "TEXT", ColumnDataKind.Text),
        ];

        public IReadOnlyList<ResultColumn> Columns => EmpCols;

        public bool IsComplete => true;

        public long? KnownRowCount => rows.Length;

        public int? PageSize => 500;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new ResultPage(pageIndex, 0, rows.Length, [rows.Select(r => r[0]).ToArray(), rows.Select(r => r[1]).ToArray()]) { IsLast = true });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>앱과 같은 도킹·등록 경로로 결과 창까지 띄운 장비.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task>? _oldRunFetch = ResultGridCommands.RunFetch;
        private readonly ResultGridModel? _oldActive = ResultGridCommands.Active;

        private Rig(ShellHarness harness) => Harness = harness;

        public ShellHarness Harness { get; }

        public ContextKeyService Keys => Harness.Get<ContextKeyService>();

        public SqlEditorViewModel Tab { get; private set; } = null!;

        public ResultTabsViewModel Tabs { get; private set; } = null!;

        public SqlResultPaneViewModel Pane { get; private set; } = null!;

        public ResultGridModel Model => Pane.Model!;

        public SqlResultPaneView View { get; private set; } = null!;

        public ResultGridControl Grid => View.FindControl<ResultGridControl>("GridControl")!;

        public Button FetchButton => View.FindControl<Button>("FilterFetchAllButton")!;

        public Button CancelButton => View.FindControl<Button>("FetchCancelButton")!;

        public static async Task<Rig> StartAsync(IResultSet result)
        {
            ResultGridCommands.RunFetch = null; // 앞 시험의 컨테이너에 묶인 처리기를 쓰지 않는다(이 셸이 다시 건다)
            ResultGridCommands.Active = null;
            var rig = new Rig(new ShellHarness());
            var wiring = rig.Harness.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
            await wiring.RunAsync(default);
            Dispatcher.UIThread.RunJobs();
            var host = SqlEditorHost.Current;
            rig.Tab = host.NewDocument("select id from t");
            host.Active = rig.Tab;
            Dispatcher.UIThread.RunJobs();
            rig.Tabs = new ResultTabsViewModel();
            rig.Tab.ResultPane = rig.Tabs;
            rig.Pane = rig.Tabs.AddQuery(result, "결과", null).Pane!;
            await rig.Model.LoadFirstPageAsync();
            rig.Harness.Get<Oranda.App.Shell.Docking.DockWorkspace>().ActivateTool(ExecutionController.ResultsPanelId);
            Dispatcher.UIThread.RunJobs();
            await HeadlessHost.WaitUntil(() => rig.Harness.Window.GetVisualDescendants().OfType<SqlResultPaneView>().Any(v => v.DataContext == rig.Pane));
            rig.View = rig.Harness.Window.GetVisualDescendants().OfType<SqlResultPaneView>().First(v => v.DataContext == rig.Pane);
            rig.Harness.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            return rig;
        }

        /// <summary>Ctrl+Enter 뒤처럼 포커스를 편집기에 둔다.</summary>
        public void FocusEditor()
        {
            var area = Harness.Window.GetVisualDescendants().OfType<SqlEditorView>().First().TextEditorControl.TextArea;
            area.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("sqlText", Keys.FocusSlot);
            Assert.False(View.IsKeyboardFocusWithin);
        }

        public void FocusGrid()
        {
            Grid.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("sqlGrid", Keys.FocusSlot);
        }

        public void Dispose()
        {
            Pane.Clear();
            Harness.Dispose();
            ResultGridCommands.RunFetch = _oldRunFetch;
            ResultGridCommands.Active = _oldActive;
        }
    }

    [Fact]
    public Task Button_fetches_all_rows_when_the_focus_is_in_the_editor() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        rig.FocusEditor();
        Assert.True(rig.FetchButton.IsEffectivelyVisible);

        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        Assert.Equal(1200, rig.Model.LoadedRowCount);
    });

    [Fact]
    public Task Button_fetches_all_rows_when_the_focus_is_in_the_grid() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        rig.FocusGrid();

        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        Assert.Equal(1200, rig.Model.LoadedRowCount);
    });

    [Fact]
    public Task Shortcut_fetches_all_rows_from_the_grid() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        rig.FocusGrid();

        rig.Harness.Window.KeyPress(Key.End, RawInputModifiers.Alt, PhysicalKey.End, null);

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        Assert.Equal(1200, rig.Model.LoadedRowCount);
    });

    [Fact]
    public Task Context_menu_item_is_enabled_and_fetches_after_a_right_click_from_the_editor() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        rig.FocusEditor();
        var window = rig.Harness.Window;
        var point = rig.Grid.TranslatePoint(new Point(60, 60), window)!.Value;

        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        var item = FindCommand(ShellContextMenus.Factory!("grid"), "oranda.grid.fetchAll");
        Assert.True(item.CanExecute(null));
        item.Execute(null);

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        Assert.Equal(1200, rig.Model.LoadedRowCount);
        rig.Grid.ContextMenu?.Close();
    });

    [Fact]
    public Task Button_fetches_every_page_of_a_large_result_from_the_editor() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(100_000));
        rig.FocusEditor();

        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 30000);
        Assert.Equal(100_000, rig.Model.LoadedRowCount);
        Assert.DoesNotContain("더 있음", rig.Pane.StatusText, StringComparison.Ordinal);
        Assert.False(rig.FetchButton.IsVisible);
    });

    [Fact]
    public Task Cancel_button_keeps_the_rows_and_the_button_can_fetch_again() => HeadlessHost.Run(async () =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = await Rig.StartAsync(new PagedSet(1200, gate: gate.Task));
        rig.FocusEditor();

        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => rig.CancelButton.IsVisible, 10000);

        rig.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => !rig.Model.IsFetchingAll, 10000);
        Assert.Equal(500, rig.Model.LoadedRowCount);
        Assert.True(rig.Pane.CanFetchAll);
        Assert.Null(rig.Model.FetchAllError);

        gate.SetResult();
        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        Assert.Equal(1200, rig.Model.LoadedRowCount);
    });

    [Fact]
    public Task Failure_shows_on_the_status_line_and_the_button_can_try_again() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new FlakySet(1200));
        rig.FocusEditor();

        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => rig.Model.FetchAllError is not null && !rig.Model.IsFetchingAll, 10000);
        Assert.Contains("모두 가져오기 실패: ORA-03113", rig.Pane.StatusText, StringComparison.Ordinal);
        await HeadlessHost.WaitUntil(() => rig.FetchButton.IsVisible, 10000);
        Assert.True(rig.Pane.CanFetchAll);

        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        Assert.Equal(1200, rig.Model.LoadedRowCount);
        Assert.Null(rig.Model.FetchAllError);
        await HeadlessHost.WaitUntil(() => !rig.Pane.StatusText.Contains("모두 가져오기 실패", StringComparison.Ordinal), 10000);
    });

    [Fact]
    public Task Failure_text_clears_when_scrolling_fetches_the_next_rows() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new FlakySet(1700));
        rig.FocusEditor();
        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => rig.Model.FetchAllError is not null && !rig.Model.IsFetchingAll, 10000);
        Assert.Contains("모두 가져오기 실패", rig.Pane.StatusText, StringComparison.Ordinal);

        // 다음 쪽이 성공하면 아직 행이 남아 있어도 지운다.
        rig.Model.Prefetch(0, rig.Model.LoadedRowCount);
        await HeadlessHost.WaitUntil(() => rig.Model.LoadedRowCount == 1000, 10000);
        Assert.True(rig.Model.HasMoreRows);
        Assert.Null(rig.Model.FetchAllError);
        await HeadlessHost.WaitUntil(() => !rig.Pane.StatusText.Contains("모두 가져오기 실패", StringComparison.Ordinal), 10000);
    });

    [Fact]
    public Task Failure_text_clears_when_scrolling_reaches_the_last_row() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new FlakySet(800));
        rig.FocusEditor();
        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => rig.Model.FetchAllError is not null && !rig.Model.IsFetchingAll, 10000);

        rig.Model.Prefetch(0, rig.Model.LoadedRowCount);

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        await HeadlessHost.WaitUntil(() => !rig.Pane.StatusText.Contains("모두 가져오기 실패", StringComparison.Ordinal), 10000);
        Assert.False(rig.Pane.CanFetchAll);
    });

    [Fact]
    public Task Results_swap_does_not_take_the_target_from_a_focused_grid_without_a_slot() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        rig.FocusGrid();
        var (y, yPane) = await HostGridWithoutSlotAsync(rig, new PagedSet(1200));
        y.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("sqlGrid", rig.Keys.FocusSlot); // 자리 없는 곳으로 옮겨도 직전 자리가 남는다
        Assert.Same(yPane.Model, ResultGridCommands.Active);

        var second = rig.Tabs.AddQuery(new PagedSet(1200), "결과2", null, closable: true);
        await second.Pane!.Model!.LoadFirstPageAsync();
        rig.Tabs.Selected = second;
        Dispatcher.UIThread.RunJobs();
        rig.Harness.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(y.IsFocused);
        Assert.Same(yPane.Model, ResultGridCommands.Active);
        yPane.Clear();
    });

    [Fact]
    public Task Delete_key_in_a_focused_grid_without_a_slot_does_not_change_the_results_session() => HeadlessHost.Run(async () =>
    {
        object?[][] rows = [[1m, "A"], [2m, "B"]];
        using var rig = await Rig.StartAsync(new TwoRowSet(rows));
        var previous = GridEditCommands.Active;
        try
        {
            var inResults = await AttachEditAsync(rig.Pane);
            rig.FocusGrid();
            rig.Model.SelectCell(new GridCell(0, 0));
            var (y, yPane) = await HostGridWithoutSlotAsync(rig, new TwoRowSet(rows));
            var inY = await AttachEditAsync(yPane);
            y.Focus();
            Dispatcher.UIThread.RunJobs();
            yPane.Model!.SelectCell(new GridCell(1, 0));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(inY, GridEditCommands.Active);

            // 결과 창이 가려졌다 다시 붙는다(결과 그리드의 View 가 다시 시각 트리에 들어온다).
            var dock = rig.Harness.Get<Oranda.App.Shell.Docking.DockWorkspace>();
            dock.ActivateTool("oranda.panel.messages");
            Dispatcher.UIThread.RunJobs();
            rig.Harness.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            dock.ActivateTool(ExecutionController.ResultsPanelId);
            Dispatcher.UIThread.RunJobs();
            rig.Harness.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(y.IsFocused);

            PressShortcut(rig, "oranda.grid.edit.deleteRow");
            await HeadlessHost.WaitUntil(() => inResults.IsDirty || inY.IsDirty, 5000);

            Assert.False(inResults.IsDirty);
            Assert.True(inY.IsDirty);
            yPane.Clear();
        }
        finally
        {
            GridEditCommands.Active = previous;
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Row_delete_key_read_from_the_keymap_runs_the_command_on_each_os(bool macOS)
    {
        var table = KeyBindingTable.Build(KeymapLoader.LoadPreset("default"), macOS);
        var keys = new ContextKeyService();
        keys.SetFocusSlot("sqlGrid");

        var gesture = Assert.IsType<KeyGesture>(table.SingleGesture("oranda.grid.edit.deleteRow"));

        Assert.Equal("oranda.grid.edit.deleteRow", table.Find([(gesture.Key, gesture.KeyModifiers)], keys).CommandId);
    }

    /// <summary>
    /// 명령에 묶인 지금 OS 의 첫 단축키를 창에 보낸다. 키맵의 mac 목록은 keys 를 대신하므로(Keymap.KeysFor) 키를 시험에 박으면 다른 OS 에서 명령이 돌지 않는다.
    /// KeyModifiers 와 RawInputModifiers 의 Alt·Control·Shift·Meta 는 값이 같다.
    /// </summary>
    private static void PressShortcut(Rig rig, string commandId)
    {
        var gesture = rig.Harness.Get<KeymapService>().Table.SingleGesture(commandId)
            ?? throw new Xunit.Sdk.XunitException($"키맵에 {commandId} 의 단축키가 없다.");
        rig.Harness.Window.KeyPress(gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
    }

    /// <summary>객체 상세 데이터 탭처럼 포커스 자리가 없는 곳에 결과 그리드를 하나 더 띄운다.</summary>
    private static async Task<(ResultGridControl Grid, SqlResultPaneViewModel Pane)> HostGridWithoutSlotAsync(Rig rig, IResultSet set)
    {
        var pane = new SqlResultPaneViewModel();
        pane.SetResult(set);
        await pane.Model!.LoadFirstPageAsync();
        var view = new SqlResultPaneView { DataContext = pane, Height = 200 };
        DockPanel.SetDock(view, Avalonia.Controls.Dock.Top);
        rig.Harness.Window.GetVisualDescendants().OfType<DockPanel>().First().Children.Insert(0, view);
        rig.Harness.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(Oranda.App.Shell.Keymaps.FocusSlot.Find(view));
        return (view.FindControl<ResultGridControl>("GridControl")!, pane);
    }

    private static async Task<GridEditSession> AttachEditAsync(SqlResultPaneViewModel pane)
    {
        var m = pane.Model!;
        var meta = new GridEditTests.OneTableMetadata(new TableInfo(new DbObjectRef("SCOTT", "EMP", "TABLE"), [new ColumnInfo("EMPNO", 1, "NUMBER") { IsNullable = false }])
        {
            Constraints = [new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["EMPNO"])],
        });
        var session = await GridEditSession.OpenAsync(new RowEditor(new GridEditTests.RecordingExecution()), meta, new GridEditTests.TestSession(false),
            new SqlStatement("SELECT empno, ename FROM emp", default, StatementKind.Query) { Verb = "SELECT" }, m.Columns,
            GridEditBinding.OriginalValues(m), () => m.LoadedRowCount, target: null, default);
        GridEditBinding.Attach(m, session);
        pane.SetEditSession(session);
        Assert.True(session.CanEdit);
        return session;
    }

    [Fact]
    public Task Button_targets_its_own_grid_while_another_grid_is_the_active_one() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        await using var other = new ResultGridModel(new PagedSet(1200));
        await other.LoadFirstPageAsync();
        ResultGridCommands.Active = other;
        rig.FocusEditor();
        ResultGridCommands.Active = other;

        rig.FetchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await HeadlessHost.WaitUntil(() => !rig.Model.HasMoreRows, 10000);
        Assert.Same(rig.Model, ResultGridCommands.Active);
        Assert.Equal(500, other.LoadedRowCount);
    });

    [Fact]
    public Task Edit_toolbar_button_sees_its_grid_keys_when_the_focus_is_in_the_editor() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        rig.FocusEditor();
        Assert.False(rig.Keys.Get("gridHasResult"));
        var seen = new List<(string Id, bool HasResult)>();
        var real = ShellCommandRunner.Runner;
        ShellCommandRunner.Runner = id =>
        {
            seen.Add((id, rig.Keys.Get("gridHasResult")));
            return Task.CompletedTask;
        };
        try
        {
            var button = rig.View.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Tag, "oranda.grid.edit.pasteRows"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => seen.Count == 1, 10000);
            Assert.Equal(("oranda.grid.edit.pasteRows", true), seen[0]);
        }
        finally
        {
            ShellCommandRunner.Runner = real;
        }
    });

    [Fact]
    public Task Shortcut_after_switching_the_result_tab_fetches_the_tab_on_screen() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync(new PagedSet(1200));
        rig.FocusGrid();
        var second = rig.Tabs.AddQuery(new PagedSet(1200), "결과2", null, closable: true);
        await second.Pane!.Model!.LoadFirstPageAsync();
        rig.Tabs.Selected = second;
        Dispatcher.UIThread.RunJobs();
        rig.Harness.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        rig.Harness.Window.KeyPress(Key.End, RawInputModifiers.Alt, PhysicalKey.End, null);

        await HeadlessHost.WaitUntil(() => !second.Pane.Model.HasMoreRows, 10000);
        Assert.Equal(500, rig.Model.LoadedRowCount);
    });

    [Fact]
    public async Task Bound_runner_records_a_refusal_and_keeps_a_handler_failure_out_of_the_button()
    {
        var registry = new CommandRegistry([]);
        using var _ = registry.Register(
            new CommandDefinition { Id = "test.boom", Title = "Cmd.test.boom", Category = CommandCategory.Result },
            new ThrowingHandler());
        var keys = new ContextKeyService();
        var run = ShellCommandRunner.Bind(registry, keys);
        using var capture = new TraceCapture();

        await run("test.missing").WaitAsync(TimeSpan.FromSeconds(10));
        await run("test.boom").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(capture.Lines, l => l.Contains("test.missing", StringComparison.Ordinal));
        Assert.Contains(capture.Lines, l => l.Contains("test.boom", StringComparison.Ordinal) && l.Contains("부러짐", StringComparison.Ordinal));
    }

    [Fact]
    public Task Fetch_failure_without_a_run_state_goes_to_the_messages() => HeadlessHost.Run(async () =>
    {
        // 세션이 실행 중이면 실행 상태 없이 그냥 돌린다: 이 길의 실패도 단추의 async void 까지 가면 안 된다.
        using var rig = new Oranda.App.Tests.Features.Execution.Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = rig.Controller.FetchAsync(_ => release.Task);
        Assert.True(rig.State.Busy);

        await rig.Controller.FetchAsync(_ => throw new InvalidOperationException("ORA-03113")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(rig.Log.Messages, m => m.Text.Contains("ORA-03113", StringComparison.Ordinal));

        release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    });

    private sealed class ThrowingHandler : ICommandHandler
    {
        public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("부러짐");
    }

    private sealed class TraceCapture : System.Diagnostics.TraceListener
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public TraceCapture() => System.Diagnostics.Trace.Listeners.Add(this);

        public IEnumerable<string> Lines => _lines;

        public override void Write(string? message) => _lines.Enqueue(message ?? string.Empty);

        public override void WriteLine(string? message) => _lines.Enqueue(message ?? string.Empty);

        protected override void Dispose(bool disposing)
        {
            System.Diagnostics.Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }

    private static RegistryCommand FindCommand(IEnumerable<object> items, string id)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Command is RegistryCommand c && c.CommandId == id)
            {
                return c;
            }

            if (item.ItemsSource is IEnumerable<object> children && FindOrNull(children, id) is { } found)
            {
                return found;
            }
        }

        throw new Xunit.Sdk.XunitException($"메뉴에 {id} 이 없다.");
    }

    private static RegistryCommand? FindOrNull(IEnumerable<object> items, string id)
    {
        try
        {
            return FindCommand(items, id);
        }
        catch (Xunit.Sdk.XunitException)
        {
            return null;
        }
    }
}
