using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.ObjectDetail;

/// <summary>
/// SCH-03 객체 상세 「데이터」 탭: 질의 모양, 「열」 바로 뒤 자리, 탭을 고를 때만 읽기(상세 문서의 세션·정책 관문 경로),
/// 거르기·행 수 세기가 같은 세션으로 감싸기, 「편집」 토글(현재 결과에 편집 모드, 반영 뒤 이 탭에서 다시 읽기, 읽기 전용·뷰 거절),
/// 닫기·늦은 결과 정리, 화면 바꾸기.
/// </summary>
[Collection("GridEditStatics")]
public sealed class ObjectDataTests : IDisposable
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private const string EmpQuery = "SELECT t.ROWID, t.* FROM SCOTT.EMP t";

    private static readonly ResultColumn[] EmpCols =
    [
        new(0, "ROWID", "ROWID", ColumnDataKind.RowId),
        new(1, "EMPNO", "NUMBER", ColumnDataKind.Number),
        new(2, "ENAME", "VARCHAR2", ColumnDataKind.Text),
    ];

    public ObjectDataTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        GridEditCommands.Active = null;
        GridEditCommands.ConfirmPreview = null;
        GridEditCommands.Rerun = null;
        ResultGridCommands.Filter = null;
        ResultGridCommands.CountFiltered = null;
        ResultGridCommands.CountRows = null;
        ResultGridCommands.Active = null;
    }

    // ---- 질의 모양 -------------------------------------------------------------------------

    [Fact]
    public void Query_reads_rowid_for_tables_and_all_columns_for_views_quoting_names_only_when_needed()
    {
        var table = ObjectDataViewModel.BuildQuery(Emp);
        Assert.Equal(EmpQuery, table.Text);
        Assert.Equal(StatementKind.Query, table.Kind);
        Assert.Equal("SELECT", table.Verb);
        Assert.Empty(table.BindNames);

        Assert.Equal("SELECT * FROM SCOTT.EMP_V", ObjectDataViewModel.BuildQuery(new DbObjectRef("SCOTT", "EMP_V", "VIEW")).Text);
        Assert.Equal("SELECT * FROM SCOTT.EMP_MV", ObjectDataViewModel.BuildQuery(new DbObjectRef("SCOTT", "EMP_MV", "MATERIALIZED VIEW")).Text);
        Assert.Equal("SELECT t.ROWID, t.* FROM \"app\".\"Order Lines\" t",
            ObjectDataViewModel.BuildQuery(new DbObjectRef("app", "Order Lines", "TABLE")).Text);
        Assert.Equal("SELECT t.ROWID, t.* FROM SCOTT.\"ORDER\" t", ObjectDataViewModel.BuildQuery(new DbObjectRef("SCOTT", "ORDER", "TABLE")).Text);

        Assert.True(ObjectDataViewModel.Supports(Emp));
        Assert.True(ObjectDataViewModel.Supports(new DbObjectRef("SCOTT", "EMP_V", "VIEW")));
        Assert.True(ObjectDataViewModel.Supports(new DbObjectRef("SCOTT", "EMP_MV", "MATERIALIZED VIEW")));
        Assert.False(ObjectDataViewModel.Supports(new DbObjectRef("SCOTT", "PKG", "PACKAGE")));
        Assert.False(ObjectDataViewModel.Supports(null));
    }

    // ---- 자리·지연 읽기 ----------------------------------------------------------------------

    [Fact]
    public async Task Data_tab_sits_right_after_columns_for_tables_and_views_and_nothing_is_read_on_open()
    {
        var h = new Harness();
        await h.Vm.OpenAsync(h.Session, Emp, showDdl: false);

        Assert.Equal(["일반", "열", "데이터", "제약"], h.Vm.Sections.Select(s => s.Title));
        Assert.Equal("열", h.Vm.SelectedSection!.Title);
        Assert.False(h.Vm.IsDataSelected);
        Assert.False(h.Vm.Data.IsLoaded);
        Assert.Empty(h.Engine.Calls); // 상세를 열 때는 데이터를 읽지 않는다

        await h.Vm.OpenAsync(h.Session, new DbObjectRef("SCOTT", "EMP_V", "VIEW"), showDdl: false);
        Assert.Contains(h.Vm.Sections, s => s.IsData);
        Assert.False(h.Vm.Data.IsTable);

        await h.Vm.OpenAsync(h.Session, new DbObjectRef("SCOTT", "PKG", "PACKAGE"), showDdl: false);
        Assert.DoesNotContain(h.Vm.Sections, s => s.IsData);
        Assert.Empty(h.Engine.Calls);
    }

    [Fact]
    public async Task Selecting_the_tab_reads_once_through_the_detail_session_and_refresh_reads_again()
    {
        var h = new Harness();
        await h.Vm.OpenAsync(h.Session, Emp, showDdl: false);
        var data = h.Vm.Sections.Single(s => s.IsData);

        h.Vm.SelectedSection = data;
        Assert.True(h.Vm.IsDataSelected);
        await h.Vm.Data.EnsureLoadedAsync();

        var call = Assert.Single(h.Engine.Calls);
        Assert.Equal(EmpQuery, call.Stmt.Text);
        Assert.Same(h.Session, call.Session); // 편집기의 활성 세션이 아니라 상세 문서의 세션
        Assert.True(call.Options!.SkipHistory);
        Assert.True(h.Vm.Data.IsLoaded);
        Assert.NotNull(h.Vm.Data.Pane.Model);
        Assert.Same(h.Vm.Data.Query, h.Vm.Data.Pane.Statement); // 거르기·행 수 세기가 이 문장을 감싼다
        Assert.Equal([1, 2], h.Vm.Data.Pane.Model!.VisibleColumns); // ROWID 열은 감춘다(값은 편집 조건에 쓴다)
        Assert.False(h.State.Busy);

        h.Vm.SelectedSection = h.Vm.Sections[0];
        Assert.False(h.Vm.IsDataSelected);
        h.Vm.SelectedSection = data;
        await h.Vm.Data.EnsureLoadedAsync();
        Assert.Single(h.Engine.Calls); // 다시 골라도 다시 읽지 않는다

        await h.Vm.Data.RefreshAsync();
        Assert.Equal(2, h.Engine.Calls.Count);
        Assert.All(h.Engine.Calls, c => Assert.Same(h.Session, c.Session));
    }

    [Fact]
    public async Task Load_failures_are_reported_in_the_tab_without_throwing()
    {
        var failing = new Harness();
        failing.Engine.Override = s => new ExecutionResult(s, ExecutionStatus.Failed)
        {
            Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942 },
        };
        await failing.LoadAsync();
        Assert.Contains("데이터를 읽지 못했습니다", failing.Vm.Data.StatusText, StringComparison.Ordinal);
        Assert.Contains("ORA-00942", failing.Vm.Data.StatusText, StringComparison.Ordinal);
        Assert.False(failing.Vm.Data.IsLoaded);
        Assert.Null(failing.Vm.Data.Pane.Model);

        var busy = new Harness();
        busy.State.BeginRun();
        await busy.LoadAsync();
        Assert.Contains("다른 문장을 실행하는 중", busy.Vm.Data.StatusText, StringComparison.Ordinal);
        Assert.Empty(busy.Engine.Calls);
        busy.State.EndRun();

        var offline = new Harness(runner: false);
        await offline.LoadAsync();
        Assert.Contains("실행 엔진이 연결되지 않았습니다", offline.Vm.Data.StatusText, StringComparison.Ordinal);
    }

    // ---- 거르기·행 수 --------------------------------------------------------------------------

    [Fact]
    public async Task Row_count_and_filter_wrap_the_data_query_on_the_detail_session_instead_of_the_editor_hooks()
    {
        ResultGridCommands.CountRows = (_, _) => throw new InvalidOperationException("편집기 연결");
        ResultGridCommands.Filter = (_, _) => throw new InvalidOperationException("편집기 연결");
        ResultGridCommands.CountFiltered = (_, _) => throw new InvalidOperationException("편집기 연결");
        var h = new Harness();
        await h.LoadAsync();
        Assert.Equal(string.Empty, h.Vm.Data.RowCountText);
        Assert.True(h.Vm.Data.CanCountRows);

        await h.Vm.Data.CountRowsAsync();
        Assert.Equal("전체 42행", h.Vm.Data.RowCountText);
        var count = h.Engine.Calls[^1];
        Assert.Equal("SELECT COUNT(*) FROM (\n" + EmpQuery + "\n)", count.Stmt.Text);
        Assert.Same(h.Session, count.Session);

        h.Vm.Data.Pane.WhereText = "ENAME LIKE 'S%'";
        await h.Vm.Data.Pane.ApplyFilterAsync();
        var filter = h.Engine.Calls[^1];
        Assert.Equal("SELECT * FROM (\n" + EmpQuery + "\n) WHERE (\nENAME LIKE 'S%'\n)", filter.Stmt.Text);
        Assert.Same(h.Session, filter.Session);
        Assert.True(h.Vm.Data.Pane.IsFiltered);
        Assert.Equal([1, 2], h.Vm.Data.Pane.Model!.VisibleColumns);
        Assert.False(h.Vm.Data.CanToggleEdit); // 거른 결과는 편집할 수 없다
        Assert.Equal(string.Empty, h.Vm.Data.RowCountText); // 결과가 바뀌면 센 값은 지운다

        await h.Vm.Data.CountRowsAsync();
        Assert.StartsWith("SELECT COUNT(*) FROM (\nSELECT * FROM (\n" + EmpQuery, h.Engine.Calls[^1].Stmt.Text, StringComparison.Ordinal);

        // 새로 고침은 같은 조건으로 다시 거른다.
        await h.Vm.Data.RefreshAsync();
        Assert.Equal(filter.Stmt.Text, h.Engine.Calls[^1].Stmt.Text);
        Assert.True(h.Vm.Data.Pane.IsFiltered);
    }

    // ---- 편집 ---------------------------------------------------------------------------------

    [Fact]
    public async Task Edit_toggle_opens_edit_mode_on_the_current_result_and_apply_rereads_here_not_in_the_editor()
    {
        var editorReruns = 0;
        GridEditCommands.Rerun = (_, _) =>
        {
            editorReruns++;
            return Task.CompletedTask;
        };
        GridEditCommands.ConfirmPreview = (_, _) => Task.FromResult(true);
        var h = new Harness();
        await h.LoadAsync();
        Assert.True(h.Vm.Data.IsTable);
        Assert.True(h.Vm.Data.CanToggleEdit);

        await h.Vm.Data.SetEditingAsync(true);

        Assert.True(h.Vm.Data.IsEditing);
        Assert.True(h.Vm.Data.Pane.IsEditMode);
        var es = GridEditCommands.Active;
        Assert.NotNull(es);
        Assert.Same(h.Session, es.Session);
        Assert.Same(h.Vm.Data.Query, es.Query);
        Assert.Equal(Emp, es.Capability.Target);
        Assert.Single(h.Engine.Calls); // 켜기는 다시 읽지 않는다

        Assert.True(es.SetCell(0, 2, "SCOTT")); // ENAME
        await new GridEditCommandHandler().ExecuteAsync(new CommandContext("oranda.grid.edit.apply", FakeContextKeys.AllTrue), default);

        var dml = Assert.Single(h.Engine.Calls, c => c.Stmt.Kind == StatementKind.Dml);
        Assert.Contains("UPDATE SCOTT.EMP SET ENAME = ", dml.Stmt.Text, StringComparison.Ordinal);
        Assert.Contains("WHERE ROWID = ", dml.Stmt.Text, StringComparison.Ordinal);
        Assert.Contains(dml.Binds.Values, b => Equals(b.Value, "AAAR1"));
        Assert.Same(h.Session, dml.Session);
        Assert.Equal(0, editorReruns); // 편집기에서 다시 실행하지 않는다
        Assert.Equal(2, h.Engine.Calls.Count(c => c.Stmt.Text == EmpQuery)); // 이 탭에서 다시 읽었다
        Assert.True(h.Vm.Data.IsEditing); // 다시 읽은 결과도 편집 모드
        Assert.NotNull(GridEditCommands.Active);
        Assert.NotSame(es, GridEditCommands.Active);
    }

    [Fact]
    public async Task Turning_edit_off_keeps_pending_changes_until_reverted_then_rereads_read_only()
    {
        var h = new Harness();
        await h.LoadAsync();
        await h.Vm.Data.SetEditingAsync(true);
        var es = GridEditCommands.Active!;
        Assert.True(es.SetCell(1, 2, "TURNER"));
        Assert.True(h.Vm.Data.Pane.IsEditDirty);

        await h.Vm.Data.SetEditingAsync(false);
        Assert.True(h.Vm.Data.IsEditing);
        Assert.Contains("반영하지 않은 편집", h.Vm.Data.StatusText, StringComparison.Ordinal);
        await h.Vm.Data.RefreshAsync();
        Assert.Single(h.Engine.Calls); // 고친 값을 버리지 않는다

        es.ResetAll();
        await h.Vm.Data.SetEditingAsync(false);
        Assert.False(h.Vm.Data.IsEditing);
        Assert.False(h.Vm.Data.Pane.IsEditMode);
        Assert.Equal(2, h.Engine.Calls.Count); // 편집 상태를 풀려고 읽기 전용으로 다시 읽었다
        Assert.Null(GridEditCommands.Active);
    }

    [Fact]
    public async Task Read_only_profiles_and_views_stay_read_only()
    {
        var ro = new Harness(readOnly: true);
        await ro.LoadAsync();
        await ro.Vm.Data.SetEditingAsync(true);
        Assert.False(ro.Vm.Data.IsEditing);
        Assert.False(ro.Vm.Data.Pane.IsEditMode);
        Assert.True(ro.Vm.Data.Pane.HasEditHint); // 편집 세션이 말하는 이유를 안내 막대에 보인다
        Assert.Contains("읽기 전용", ro.Vm.Data.Pane.EditHint, StringComparison.Ordinal);
        Assert.Contains("읽기 전용", ro.Vm.Data.StatusText, StringComparison.Ordinal);

        var view = new Harness();
        await view.LoadAsync(new DbObjectRef("SCOTT", "EMP_V", "VIEW"));
        Assert.Equal("SELECT * FROM SCOTT.EMP_V", view.Engine.Calls[0].Stmt.Text);
        Assert.False(view.Vm.Data.IsTable);
        Assert.False(view.Vm.Data.CanToggleEdit);
        await view.Vm.Data.SetEditingAsync(true);
        Assert.False(view.Vm.Data.IsEditing);
        Assert.Null(GridEditBinding.Of(view.Vm.Data.Pane.Model)); // 편집 세션을 열지 않는다
        Assert.Contains("뷰", view.Vm.Data.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_hint_rerun_turns_on_edit_mode_in_the_tab_instead_of_rerunning_in_the_editor()
    {
        var editorReruns = 0;
        GridEditCommands.Rerun = (_, _) =>
        {
            editorReruns++;
            return Task.CompletedTask;
        };
        var h = new Harness();
        await h.LoadAsync();

        h.Vm.Data.Pane.ShowEditHint(); // 읽기 전용 결과의 셀을 고치려 했다
        Assert.True(h.Vm.Data.Pane.OffersRerunForEdit);
        await h.Vm.Data.Pane.RunEditHintActionAsync();

        Assert.Equal(0, editorReruns);
        Assert.True(h.Vm.Data.IsEditing);
        Assert.Single(h.Engine.Calls);
    }

    // ---- 닫기 ---------------------------------------------------------------------------------

    [Fact]
    public async Task Closing_discards_the_result_and_a_result_that_arrives_after_reopening_is_closed()
    {
        var h = new Harness();
        await h.LoadAsync();
        var first = h.Engine.Results[0];
        h.Vm.Data.Close();
        Assert.Null(h.Vm.Data.Pane.Model);
        Assert.Null(h.Vm.Data.Pane.Statement);
        Assert.False(h.Vm.Data.IsLoaded);
        await WaitAsync(() => first.Disposed);

        var gate = new TaskCompletionSource();
        h.Engine.Gate = gate.Task;
        var pending = h.Vm.Data.EnsureLoadedAsync(); // 닫은 뒤 탭이 보이면 다시 읽는다
        Assert.True(h.Vm.Data.IsLoading);
        await h.Vm.OpenAsync(h.Session, new DbObjectRef("SCOTT", "DEPT", "TABLE"), showDdl: false); // 그새 다른 객체로
        gate.SetResult();
        await pending;

        var late = h.Engine.Results[^1];
        await WaitAsync(() => late.Disposed);
        Assert.Null(h.Vm.Data.Pane.Model);
        Assert.False(h.Vm.Data.IsLoading);
        Assert.Equal(new DbObjectRef("SCOTT", "DEPT", "TABLE"), h.Vm.Data.Target);
    }

    // ---- 화면 ---------------------------------------------------------------------------------

    [Fact]
    public Task Choosing_the_data_tab_swaps_the_detail_table_for_the_result_pane() => HeadlessHost.Run(async () =>
    {
        var h = new Harness();
        await h.Vm.OpenAsync(h.Session, Emp, showDdl: false);
        var view = new ObjectDetailView { DataContext = h.Vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var table = view.FindControl<Oranda.App.Shell.Controls.DetailTable>("SectionTable")!;
            var dataView = view.FindControl<ObjectDataView>("DataView")!;
            Assert.True(table.IsEffectivelyVisible);
            Assert.False(dataView.IsEffectivelyVisible);

            h.Vm.SelectedSection = h.Vm.Sections.Single(s => s.IsData);
            await h.Vm.Data.EnsureLoadedAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.False(table.IsEffectivelyVisible);
            Assert.True(dataView.IsEffectivelyVisible);
            var pane = Assert.Single(dataView.GetVisualDescendants().OfType<SqlResultPaneView>());
            Assert.Same(h.Vm.Data.Pane, pane.DataContext);
            var toggle = dataView.FindControl<ToggleButton>("EditToggle")!;
            Assert.True(toggle.IsVisible);
            Assert.True(toggle.IsEnabled);
            Assert.False(toggle.IsChecked);

            await h.Vm.Data.SetEditingAsync(true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(toggle.IsChecked);
        }
        finally
        {
            window.Close();
        }
    });

    private static async Task WaitAsync(Func<bool> cond)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "시간 초과");
            await Task.Delay(5);
        }
    }

    // ---- 가짜 --------------------------------------------------------------------------------

    private sealed class Harness
    {
        public Harness(bool readOnly = false, bool runner = true)
        {
            Session = new TestSession(readOnly);
            Vm = new ObjectDetailViewModel(Meta)
            {
                DataRunner = runner ? s => new ObjectDataRunner(State, Binds, s, () => Engine) : _ => null,
                RowEditor = new RowEditor(Engine),
            };
        }

        public TestSession Session { get; }

        public ExecutionState State { get; } = new();

        public BindVariableStore Binds { get; } = new();

        public Engine Engine { get; } = new();

        public Meta Meta { get; } = new();

        public ObjectDetailViewModel Vm { get; }

        /// <summary>상세를 열고 「데이터」 탭을 골라 첫 페이지까지 읽는다.</summary>
        public async Task LoadAsync(DbObjectRef? target = null)
        {
            await Vm.OpenAsync(Session, target ?? Emp, showDdl: false);
            Vm.SelectedSection = Vm.Sections.Single(s => s.IsData);
            await Vm.Data.EnsureLoadedAsync();
            if (Vm.Data.Pane.Model is { } model)
            {
                await model.LoadFirstPageAsync();
            }
        }
    }

    /// <summary>받은 세션·문장·바인드·옵션을 남기는 실행 엔진. 질의는 EMP 두 행(COUNT(*) 는 42), DML 은 1 행.</summary>
    private sealed class Engine : IExecutionService
    {
        public List<(ISession Session, SqlStatement Stmt, IReadOnlyDictionary<string, BindValue> Binds, ExecutionOptions? Options)> Calls { get; } = [];

        public List<TableResult> Results { get; } = [];

        public Func<SqlStatement, ExecutionResult?>? Override { get; set; }

        /// <summary>채우면 질의 결과를 이 작업이 끝날 때까지 늦춘다.</summary>
        public Task? Gate { get; set; }

        public async Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                        ExecutionOptions? options, CancellationToken ct)
        {
            Calls.Add((s, stmt, binds, options));
            if (Override?.Invoke(stmt) is { } overridden)
            {
                return overridden;
            }

            if (stmt.Kind != StatementKind.Query)
            {
                return new ExecutionResult(stmt, ExecutionStatus.Succeeded) { RowsAffected = stmt.Kind == StatementKind.Dml ? 1 : null };
            }

            if (Gate is { } gate)
            {
                await gate;
            }

            var rs = stmt.Text.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal)
                ? new TableResult([new ResultColumn(0, "COUNT(*)", "NUMBER", ColumnDataKind.Number)], [[42m]])
                : new TableResult(EmpCols, [["AAAR1", 7369m, "SMITH"], ["AAAR2", 7521m, "WARD"]]);
            Results.Add(rs);
            return new ExecutionResult(stmt, ExecutionStatus.Succeeded) { ResultSet = rs };
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                         ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>열과 행을 그대로 돌려주는 메모리 결과.</summary>
    private sealed class TableResult(ResultColumn[] columns, object?[][] rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => true;

        public long? KnownRowCount => rows.Length;

        public bool Disposed { get; private set; }

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var cols = new List<IReadOnlyList<object?>>();
            for (var c = 0; c < columns.Length; c++)
            {
                cols.Add(pageIndex == 0 ? [.. rows.Select(r => r[c])] : []);
            }

            return Task.FromResult(new ResultPage(pageIndex, 0, pageIndex == 0 ? rows.Length : 0, cols) { IsLast = true });
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestSession(bool readOnly) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev", "scott", "h:1521/s", ReadOnly: readOnly);

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, false, false, false, false));

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>상세는 일반·열·제약 세 구역, 표 정보는 키 없는 EMP(편집은 ROWID 로 행을 찾는다).</summary>
    private sealed class Meta : IMetadataProvider
    {
        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
            Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj,
            [
                new DetailSection("일반", ["항목", "값"], [["유형", obj.ObjectType]]),
                new DetailSection("열", ["이름", "형식"], [["EMPNO", "NUMBER"], ["ENAME", "VARCHAR2"]]),
                new DetailSection("제약", ["이름"], [["EMP_CK"]]),
            ]));

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Task.FromResult($"CREATE TABLE {obj.Name};");

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
            Task.FromResult(new TableInfo(table, [new ColumnInfo("EMPNO", 1, "NUMBER"), new ColumnInfo("ENAME", 2, "VARCHAR2")]));

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
