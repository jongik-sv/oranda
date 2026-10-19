using System.Runtime.CompilerServices;
using Oranda.App.Features.GridEdit;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>TSK-06-04 편집 모드 화면 논리: 편집 불가 표시, 변경 추적, 미리보기 확인 후 반영, 실패 행 표시.</summary>
[Collection("GridEditStatics")]
public sealed class GridEditTests : IDisposable
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private readonly RecordingExecution _exec = new();

    public void Dispose()
    {
        GridEditCommands.Active = null;
        GridEditCommands.ConfirmPreview = null;
        GridEditCommands.Rerun = null;
    }

    private static readonly object?[][] Data =
    [
        [7369m, "SMITH", 800m],
        [7499m, "ALLEN", 1600m],
    ];

    private static ResultColumn[] Columns(params (string Name, ColumnDataKind Kind)[] cols) =>
        [.. cols.Select((c, i) => new ResultColumn(i, c.Name, c.Kind.ToString(), c.Kind))];

    private static readonly ResultColumn[] EmpCols =
        Columns(("EMPNO", ColumnDataKind.Number), ("ENAME", ColumnDataKind.Text), ("SAL", ColumnDataKind.Number));

    private Task<GridEditSession> OpenAsync(string sql, ResultColumn[]? cols = null, object?[][]? data = null, bool readOnly = false,
                                            TableInfo? table = null)
    {
        data ??= Data;
        var meta = new OneTableMetadata(table ?? new TableInfo(Emp, [new ColumnInfo("EMPNO", 1, "NUMBER") { IsNullable = false }])
        {
            Constraints = [new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["EMPNO"])],
        });
        var session = new TestSession(readOnly);
        return GridEditSession.OpenAsync(new RowEditor(_exec), meta, session,
            new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" }, cols ?? EmpCols,
            (r, c) => data[r][c], () => data.Length, target: null, default);
    }

    [Fact]
    public async Task Keyless_result_shows_reason_and_offers_rowid_rerun()
    {
        var s = await OpenAsync("SELECT ename, sal FROM emp",
            Columns(("ENAME", ColumnDataKind.Text), ("SAL", ColumnDataKind.Number)), [["SMITH", 800m]]);

        Assert.False(s.CanEdit);
        Assert.Equal(EditBlockReason.NoKeyColumns, s.Capability.Reason);
        Assert.Contains("ROWID", s.Capability.Message, StringComparison.Ordinal);
        Assert.False(s.SetCell(0, 1, 1m));
        Assert.Equal(0, s.AddRow());

        SqlStatement? rerun = null;
        GridEditCommands.Active = s;
        GridEditCommands.Rerun = (q, _) => { rerun = q; return Task.CompletedTask; };
        await Run("oranda.grid.edit.addRowidAndRerun");
        Assert.Equal("SELECT ename, sal, emp.ROWID FROM emp", rerun?.Text);
    }

    [Fact]
    public async Task Read_only_result_pane_hints_and_reruns_the_same_statement_in_edit_mode()
    {
        var stmt = new SqlStatement("SELECT empno, ename, sal FROM emp", default, StatementKind.Query) { Verb = "SELECT" };
        var pane = new Oranda.App.Features.ResultGrid.SqlResultPaneViewModel { Statement = stmt };
        SqlStatement? rerun = null;
        GridEditCommands.Rerun = (q, _) => { rerun = q; return Task.CompletedTask; };

        pane.ShowEditHint(); // 일반 실행 결과에서 셀을 고치려 했다

        Assert.True(pane.HasEditHint);
        Assert.Contains("F9", pane.EditHint, StringComparison.Ordinal);
        Assert.Equal("편집 모드로 다시 실행", pane.EditHintAction);
        Assert.True(pane.OffersRerunForEdit);
        Assert.False(pane.OffersRowIdRerun);
        Assert.False(pane.IsEditMode);

        await pane.RunEditHintActionAsync();

        Assert.Same(stmt, rerun);
        Assert.False(pane.HasEditHint);
    }

    [Fact]
    public async Task Edit_mode_pane_shows_toolbar_state_and_no_hint()
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        var pane = new Oranda.App.Features.ResultGrid.SqlResultPaneViewModel();
        var changed = new List<string?>();
        pane.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pane.SetEditSession(s);

        Assert.True(pane.IsEditMode);
        Assert.False(pane.IsEditDirty);
        Assert.False(pane.HasEditHint);
        Assert.True(s.SetCell(0, 1, "SCOTT"));
        Assert.True(pane.IsEditDirty);
        Assert.Contains(nameof(pane.IsEditDirty), changed);
        pane.ShowEditHint(); // 고칠 수 있는 결과에서는 안내하지 않는다
        Assert.False(pane.HasEditHint);
    }

    [Fact]
    public async Task Keyless_edit_session_explains_why_and_offers_rowid_rerun()
    {
        var s = await OpenAsync("SELECT ename, sal FROM emp",
            Columns(("ENAME", ColumnDataKind.Text), ("SAL", ColumnDataKind.Number)), [["SMITH", 800m]]);
        var pane = new Oranda.App.Features.ResultGrid.SqlResultPaneViewModel();

        pane.SetEditSession(s);

        Assert.False(pane.IsEditMode);
        Assert.True(pane.HasEditHint);
        Assert.Contains("ROWID", pane.EditHint, StringComparison.Ordinal);
        Assert.Equal("ROWID 추가 후 다시 실행", pane.EditHintAction);
        Assert.True(pane.OffersRowIdRerun);
        Assert.False(pane.OffersRerunForEdit);
    }

    [Fact]
    public async Task Read_only_profile_is_not_editable()
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp", readOnly: true);

        Assert.False(s.CanEdit);
        Assert.Equal(EditBlockReason.ReadOnlyProfile, s.Capability.Reason);
    }

    [Fact]
    public async Task Join_with_repeated_target_key_is_not_key_preserved()
    {
        var cols = Columns(("EMPNO", ColumnDataKind.Number), ("DNAME", ColumnDataKind.Text));
        var s = await OpenAsync("SELECT e.empno, d.dname FROM emp e JOIN dept d ON d.deptno = e.deptno", cols,
            [[1m, "A"], [1m, "B"]]);

        Assert.False(s.CanEdit);
        Assert.Equal(EditBlockReason.NotKeyPreserved, s.Capability.Reason);
    }

    [Fact]
    public async Task Edits_are_tracked_previewed_and_applied_after_confirmation()
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        Assert.True(s.CanEdit);
        GridEditCommands.Active = s;

        Assert.True(s.SetCell(0, 2, 900m));
        Assert.False(s.SetCell(0, 99, 1m));
        Assert.True(s.SetCell(1, 1, "ALLEN")); // 원래 값과 같으면 변경 아님
        Assert.Equal(GridEditRowState.Unchanged, s.GetRowState(1));
        await Run("oranda.grid.edit.deleteRow", 1L);
        var added = s.AddRow();
        s.SetCell(added, 0, 9000m);
        s.SetCell(added, 1, "NEW");
        Assert.Equal(GridEditRowState.Modified, s.GetRowState(0));
        Assert.Equal(GridEditRowState.Deleted, s.GetRowState(1));
        Assert.Equal(GridEditRowState.Inserted, s.GetRowState(added));
        Assert.True(s.TryGetPendingValue(0, 2, out var pending) && Equals(pending, 900m));

        var preview = s.BuildPreview();
        Assert.Contains("DELETE FROM SCOTT.EMP WHERE EMPNO = :k1;", preview, StringComparison.Ordinal);
        Assert.Contains("UPDATE SCOTT.EMP SET SAL = :v1 WHERE EMPNO = :k1;", preview, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME) VALUES (:v1, :v2);", preview, StringComparison.Ordinal);

        // 미리보기 연결이 없거나 사용자가 거절하면 반영하지 않는다.
        await Run("oranda.grid.edit.applyAndCommit");
        Assert.Empty(_exec.Dml);
        GridEditCommands.ConfirmPreview = (_, _) => Task.FromResult(false);
        await Run("oranda.grid.edit.applyAndCommit");
        Assert.Empty(_exec.Dml);

        string? shown = null;
        GridEditCommands.ConfirmPreview = (text, _) => { shown = text; return Task.FromResult(true); };
        await Run("oranda.grid.edit.applyAndCommit");

        Assert.Equal(preview, shown);
        Assert.Equal(3, _exec.Dml.Count);
        Assert.All(_exec.Calls, c => Assert.Equal(Oranda.Core.History.HistorySource.GridEdit, c.Options!.Source));
        Assert.False(s.IsDirty);
        Assert.Contains("커밋함", s.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_row_stays_dirty_and_is_marked()
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        _exec.RowsFor = stmt => stmt.Text.Contains("UPDATE", StringComparison.Ordinal) ? 0 : 1;
        s.SetCell(0, 1, "X");
        s.SetCell(1, 1, "Y");

        var r = await s.ApplyAsync(commit: true, default);

        Assert.Equal(0, r.AppliedCount);
        Assert.Equal(GridEditRowState.Failed, s.GetRowState(0));
        Assert.Contains("다른 세션", s.GetRowFailure(0), StringComparison.Ordinal);
        Assert.True(s.IsDirty);
        Assert.Contains("반영하지 않았습니다", s.Notice, StringComparison.Ordinal);

        s.ResetRow(0);
        Assert.Equal(GridEditRowState.Unchanged, s.GetRowState(0));
        s.ResetAll();
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task Optimistic_lock_adds_original_values()
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        GridEditCommands.Active = s;
        await Run("oranda.grid.edit.toggleOptimisticLock");
        s.SetCell(0, 2, 1m);

        var change = Assert.Single(s.BuildChanges());

        Assert.True(s.OptimisticLock);
        Assert.Equal(["ENAME", "SAL"], change.OriginalValues.Keys);
        Assert.Contains("AND ENAME = :o1 AND SAL = :o2", s.BuildPreview(), StringComparison.Ordinal);
    }

    [Fact]
    public void Handler_is_registered_for_every_grid_edit_command()
    {
        var ids = typeof(GridEditCommandHandler).GetCustomAttributes(typeof(OrandaCommandAttribute), false)
            .Cast<OrandaCommandAttribute>().Select(a => a.Id).ToList();

        Assert.Contains("oranda.exec.runForEdit", ids);
        Assert.Equal(11, ids.Count);
        Assert.All(ids.Where(i => i != "oranda.exec.runForEdit"), i => Assert.StartsWith("oranda.grid.edit.", i, StringComparison.Ordinal));
    }

    private static Task Run(string id, object? parameter = null) =>
        new GridEditCommandHandler().ExecuteAsync(new CommandContext(id, new NoKeys(), parameter), default);

    private sealed class NoKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }

    private sealed class RecordingExecution : IExecutionService
    {
        public List<(SqlStatement Statement, ExecutionOptions? Options)> Calls { get; } = [];

        public List<SqlStatement> Dml => [.. Calls.Select(c => c.Statement).Where(x => x.Kind == StatementKind.Dml)];

        public Func<SqlStatement, long> RowsFor { get; set; } = _ => 1;

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                  ExecutionOptions? options, CancellationToken ct)
        {
            Calls.Add((stmt, options));
            return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded)
            {
                RowsAffected = stmt.Kind == StatementKind.Dml ? RowsFor(stmt) : null,
            });
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                         ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
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

        public bool HasPendingTransaction { get; private set; }

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OneTableMetadata(TableInfo table) : IMetadataProvider
    {
        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef t, CancellationToken ct) =>
            Task.FromResult(table with { Table = t });

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}

[CollectionDefinition("GridEditStatics", DisableParallelization = true)]
public sealed class GridEditStaticsGroup
{
}
