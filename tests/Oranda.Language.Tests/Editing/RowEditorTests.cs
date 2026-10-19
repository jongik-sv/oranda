using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;
using Oranda.Testing;

namespace Oranda.Language.Tests.Editing;

// 소유: TSK-06-04. 편집 가능 판단·DML 생성·세이브포인트 반영(가짜 실행 엔진). 실제 DB 시나리오는
// tests/Oranda.Oracle.IntegrationTests/Editing/RowEditorIntegrationTests.cs.
public class RowEditorTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private static readonly DbObjectRef Dept = new("SCOTT", "DEPT", "TABLE");
    private static readonly TableKeyInfo EmpPk = new(Emp, TableKeyKind.PrimaryKey, ["EMPNO"]);
    private static readonly TableKeyInfo EmpNoKey = new(Emp, TableKeyKind.None, []);

    private static SqlStatement Q(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static ResultColumn Col(int i, string name, ColumnDataKind kind = ColumnDataKind.Text) => new(i, name, kind.ToString(), kind);

    private static ResultColumn[] Cols(params string[] names) =>
        [.. names.Select((n, i) => n == "ROWID" ? Col(i, n, ColumnDataKind.RowId) : Col(i, n, n == "EMPNO" || n == "SAL" ? ColumnDataKind.Number : ColumnDataKind.Text))];

    private static EditCapability Analyze(string sql, TableKeyInfo keys, params string[] cols) =>
        new RowEditor(new FakeExecutionService()).Analyze(Q(sql), Cols(cols), keys);

    // ---------- 편집 가능 판단 ----------

    [Fact]
    public void Result_without_pk_or_rowid_is_read_only_with_reason()
    {
        var cap = Analyze("SELECT ename, sal FROM emp", EmpPk, "ENAME", "SAL");

        Assert.False(cap.CanEdit);
        Assert.Equal(EditBlockReason.NoKeyColumns, cap.Reason);
        Assert.Contains("ROWID", cap.Message, StringComparison.Ordinal);
        Assert.True(cap.CanAddRowId);
        Assert.Equal("SELECT ename, sal, emp.ROWID FROM emp", cap.QueryWithRowId!.Text);
    }

    [Fact]
    public void Table_without_any_key_and_no_rowid_is_read_only()
    {
        var cap = Analyze("SELECT * FROM emp e WHERE deptno = 10", EmpNoKey, "EMPNO", "ENAME");

        Assert.False(cap.CanEdit);
        Assert.Equal(EditBlockReason.NoKeyColumns, cap.Reason);
        Assert.Equal("SELECT e.*, e.ROWID FROM emp e WHERE deptno = 10", cap.QueryWithRowId!.Text);
    }

    [Fact]
    public void Pk_in_result_enables_editing_of_table_columns()
    {
        var cap = Analyze("SELECT empno, ename, sal * 2 AS double_sal, rownum FROM emp", EmpPk, "EMPNO", "ENAME", "DOUBLE_SAL", "ROWNUM");

        Assert.True(cap.CanEdit);
        Assert.Equal([0], cap.KeyColumnOrdinals);
        Assert.Equal([0, 1], cap.EditableColumnOrdinals);
        Assert.Equal(Emp, cap.Target);
    }

    [Fact]
    public void Aliased_key_column_is_found_by_source_name()
    {
        var plan = RowEditPlanner.Plan(Q("SELECT e.empno AS id, e.ename name FROM scott.emp e"), Cols("ID", "NAME"), EmpPk);

        Assert.True(plan.Capability.CanEdit);
        Assert.Equal(["EMPNO", "ENAME"], plan.ColumnNames);
    }

    [Fact]
    public void Rowid_alone_is_enough_and_preferred_only_without_pk()
    {
        var withRowId = RowEditPlanner.Plan(Q("SELECT ename, ROWID FROM emp"), Cols("ENAME", "ROWID"), EmpPk);
        Assert.True(withRowId.Capability.CanEdit);
        Assert.True(withRowId.UsesRowId);
        Assert.Equal([1], withRowId.Capability.KeyColumnOrdinals);
        Assert.Equal([0], withRowId.Capability.EditableColumnOrdinals);

        var both = RowEditPlanner.Plan(Q("SELECT t.*, t.ROWID FROM emp t"), Cols("EMPNO", "ENAME", "ROWID"), EmpPk);
        Assert.True(both.Capability.CanEdit);
        Assert.False(both.UsesRowId);
        Assert.Equal([0], both.Capability.KeyColumnOrdinals);
    }

    [Fact]
    public void Composite_key_needs_every_column()
    {
        var keys = new TableKeyInfo(Emp, TableKeyKind.UniqueNotNull, ["DEPTNO", "ENAME"]);
        Assert.False(Analyze("SELECT deptno, sal FROM emp", keys, "DEPTNO", "SAL").CanEdit);
        Assert.True(Analyze("SELECT ename, deptno, sal FROM emp", keys, "ENAME", "DEPTNO", "SAL").CanEdit);
    }

    [Theory]
    [InlineData("SELECT DISTINCT empno, ename FROM emp", EditBlockReason.Distinct)]
    [InlineData("SELECT UNIQUE empno FROM emp", EditBlockReason.Distinct)]
    [InlineData("SELECT empno, COUNT(*) FROM emp GROUP BY empno", EditBlockReason.GroupBy)]
    [InlineData("SELECT COUNT(*) AS n FROM emp", EditBlockReason.Aggregate)]
    [InlineData("SELECT MAX(empno) AS empno FROM emp", EditBlockReason.Aggregate)]
    [InlineData("SELECT empno FROM emp GROUP BY empno", EditBlockReason.GroupBy)]
    [InlineData("SELECT empno FROM emp UNION SELECT empno FROM emp", EditBlockReason.SetOperation)]
    [InlineData("SELECT empno FROM emp MINUS SELECT empno FROM emp WHERE 1=0", EditBlockReason.SetOperation)]
    [InlineData("WITH x AS (SELECT * FROM emp) SELECT empno FROM x", EditBlockReason.NotAnalyzable)]
    public void Aggregate_distinct_group_and_set_results_are_not_editable(string sql, EditBlockReason reason)
    {
        var cap = Analyze(sql, EmpPk, "EMPNO", "X");

        Assert.False(cap.CanEdit);
        Assert.Equal(reason, cap.Reason);
        Assert.False(cap.CanAddRowId);
        Assert.NotNull(cap.Message);
    }

    [Fact]
    public void Analytic_function_and_subquery_aggregate_do_not_block()
    {
        var cap = Analyze(
            "SELECT empno, SUM(sal) OVER (PARTITION BY deptno) total, (SELECT COUNT(*) FROM dept) n /* MAX(x) */ FROM emp",
            EmpPk, "EMPNO", "TOTAL", "N");

        Assert.True(cap.CanEdit);
        Assert.Equal([0], cap.EditableColumnOrdinals);
    }

    [Fact]
    public void Join_edits_only_target_columns_and_needs_target_key()
    {
        const string sql = "SELECT e.empno, e.ename, d.dname, d.deptno FROM emp e JOIN dept d ON d.deptno = e.deptno";
        var plan = RowEditPlanner.Plan(Q(sql), Cols("EMPNO", "ENAME", "DNAME", "DEPTNO"), EmpPk);

        Assert.True(plan.Capability.CanEdit);
        Assert.True(plan.IsJoin);
        Assert.Equal([0, 1], plan.Capability.EditableColumnOrdinals);
        Assert.Equal(["EMP", "DEPT"], plan.Candidates.Select(c => c.Name));
        Assert.Contains("SCOTT.EMP", plan.Capability.Message, StringComparison.Ordinal);

        // DEPT 를 대상으로 고르면 DEPT 열만.
        var deptPlan = RowEditPlanner.Plan(Q(sql), Cols("EMPNO", "ENAME", "DNAME", "DEPTNO"),
            new TableKeyInfo(Dept, TableKeyKind.PrimaryKey, ["DEPTNO"]));
        Assert.True(deptPlan.Capability.CanEdit);
        Assert.Equal([2, 3], deptPlan.Capability.EditableColumnOrdinals);
    }

    [Fact]
    public void Join_without_target_key_cannot_add_rowid_automatically()
    {
        var cap = Analyze("SELECT e.ename, d.dname FROM emp e, dept d WHERE d.deptno = e.deptno", EmpPk, "ENAME", "DNAME");

        Assert.False(cap.CanEdit);
        Assert.Equal(EditBlockReason.NoKeyColumns, cap.Reason);
        Assert.False(cap.CanAddRowId);
    }

    [Fact]
    public void Unqualified_columns_in_join_use_driver_base_table()
    {
        var cols = new[]
        {
            Col(0, "EMPNO", ColumnDataKind.Number) with { BaseOwner = "SCOTT", BaseTable = "EMP", BaseColumn = "EMPNO" },
            Col(1, "DNAME") with { BaseOwner = "SCOTT", BaseTable = "DEPT", BaseColumn = "DNAME" },
        };
        var cap = new RowEditor(new FakeExecutionService()).Analyze(
            Q("SELECT empno, dname FROM emp LEFT OUTER JOIN dept USING (deptno)"), cols, EmpPk);

        Assert.True(cap.CanEdit);
        Assert.Equal([0], cap.EditableColumnOrdinals);
    }

    [Fact]
    public void Target_missing_from_from_clause_is_not_analyzable()
    {
        var cap = Analyze("SELECT empno FROM emp", new TableKeyInfo(Dept, TableKeyKind.PrimaryKey, ["DEPTNO"]), "EMPNO");

        Assert.False(cap.CanEdit);
        Assert.Equal(EditBlockReason.NotAnalyzable, cap.Reason);
    }

    [Fact]
    public void Lob_and_rowid_columns_are_not_cell_editable()
    {
        var cols = new[] { Col(0, "EMPNO", ColumnDataKind.Number), Col(1, "NOTES", ColumnDataKind.Clob), Col(2, "ROWID", ColumnDataKind.RowId) };
        var cap = new RowEditor(new FakeExecutionService()).Analyze(Q("SELECT empno, notes, rowid FROM emp"), cols, EmpPk);

        Assert.True(cap.CanEdit);
        Assert.Equal([0], cap.EditableColumnOrdinals);
    }

    [Fact]
    public void Key_preservation_detects_duplicate_keys()
    {
        object?[][] rows = [[1m, "a"], [2m, "b"], [1m, "c"], [null, "d"]];
        Assert.Equal(2, KeyPreservation.FindDuplicate([0], rows.Length, (r, c) => rows[r][c]));
        Assert.Null(KeyPreservation.FindDuplicate([0, 1], rows.Length, (r, c) => rows[r][c]));
    }

    [Fact]
    public void Table_keys_prefer_pk_then_not_null_unique()
    {
        var cols = new[] { new ColumnInfo("A", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("B", 2, "NUMBER") };
        var withPk = new TableInfo(Emp, cols)
        {
            Constraints = [new("UK_A", ConstraintKind.Unique, ["A"]), new("PK", ConstraintKind.PrimaryKey, ["A", "B"])],
        };
        Assert.Equal(TableKeyKind.PrimaryKey, TableKeys.From(withPk).Kind);

        var ukOnly = withPk with { Constraints = [new("UK_B", ConstraintKind.Unique, ["B"]), new("UK_A", ConstraintKind.Unique, ["A"])] };
        var k = TableKeys.From(ukOnly);
        Assert.Equal(TableKeyKind.UniqueNotNull, k.Kind);
        Assert.Equal(["A"], k.KeyColumns);

        var nullableUk = withPk with { Constraints = [new("UK_B", ConstraintKind.Unique, ["B"])] };
        Assert.Equal(TableKeyKind.None, TableKeys.From(nullableUk).Kind);
    }

    // ---------- DML 생성 ----------

    [Fact]
    public void Update_uses_bound_pk_condition_and_no_literal_values()
    {
        var change = new RowChange(RowChangeKind.Update, 3, Emp)
        {
            Key = new Dictionary<string, object?> { ["EMPNO"] = 7369m },
            NewValues = new Dictionary<string, object?> { ["ENAME"] = "O'Brien; DROP TABLE emp", ["SAL"] = 800.5m },
        };

        var d = RowDmlBuilder.Build(change)!;

        Assert.Equal("UPDATE SCOTT.EMP SET ENAME = :v1, SAL = :v2 WHERE EMPNO = :k1", d.Statement.Text);
        Assert.Equal(StatementKind.Dml, d.Statement.Kind);
        Assert.Equal("UPDATE", d.Statement.Verb);
        Assert.False(d.Statement.IsUnfilteredDml);
        Assert.Equal(["v1", "v2", "k1"], d.Statement.BindNames);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "O'Brien; DROP TABLE emp"), d.Binds["v1"]);
        Assert.Equal(new BindValue(BindDataType.Number, 7369m), d.Binds["k1"]);
        Assert.DoesNotContain("7369", d.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_by_rowid_and_optimistic_lock()
    {
        var change = new RowChange(RowChangeKind.Delete, 0, Emp)
        {
            Key = new Dictionary<string, object?> { ["ROWID"] = "AAAR3sAAEAAAACXAAA" },
            OriginalValues = new Dictionary<string, object?> { ["ENAME"] = "SMITH", ["COMM"] = null, ["HIREDATE"] = new DateTime(1980, 12, 17) },
        };

        var d = RowDmlBuilder.Build(change)!;

        Assert.Equal("DELETE FROM SCOTT.EMP WHERE ROWID = :rid AND ENAME = :o1 AND COMM IS NULL AND HIREDATE = :o2", d.Statement.Text);
        Assert.Equal(["rid", "o1", "o2"], d.Statement.BindNames);
        Assert.Equal(BindDataType.Timestamp, d.Binds["o2"].Type);
    }

    [Fact]
    public void Insert_lists_columns_and_quotes_unusual_identifiers()
    {
        var change = new RowChange(RowChangeKind.Insert, 10, new DbObjectRef("App", "order", "TABLE"))
        {
            NewValues = new Dictionary<string, object?> { ["ID"] = 1, ["Mixed Case"] = null, ["DATE"] = DateTimeOffset.UnixEpoch, ["RAW_C"] = new byte[] { 1, 2 } },
        };

        var d = RowDmlBuilder.Build(change)!;

        Assert.Equal("INSERT INTO \"App\".\"order\" (ID, \"Mixed Case\", \"DATE\", RAW_C) VALUES (:v1, :v2, :v3, :v4)", d.Statement.Text);
        Assert.Equal(BindDataType.Number, d.Binds["v1"].Type);
        Assert.Null(d.Binds["v2"].Value);
        Assert.Equal(BindDataType.TimestampTz, d.Binds["v3"].Type);
        Assert.Equal(BindDataType.Raw, d.Binds["v4"].Type);
    }

    [Fact]
    public void Update_or_delete_without_key_is_never_generated()
    {
        var noKey = new RowChange(RowChangeKind.Delete, 1, Emp);
        Assert.Throws<ArgumentException>(() => RowDmlBuilder.Build(noKey));

        var nullKey = new RowChange(RowChangeKind.Update, 1, Emp)
        {
            Key = new Dictionary<string, object?> { ["EMPNO"] = null },
            NewValues = new Dictionary<string, object?> { ["ENAME"] = "X" },
        };
        Assert.Throws<ArgumentException>(() => RowDmlBuilder.Build(nullKey));
        Assert.Throws<ArgumentException>(() => RowDmlBuilder.QuoteIdentifier("A\"B"));
    }

    [Fact]
    public void Preview_shows_statements_with_bind_values()
    {
        var editor = new RowEditor(new FakeExecutionService());
        var changes = new[]
        {
            new RowChange(RowChangeKind.Update, 0, Emp)
            {
                Key = new Dictionary<string, object?> { ["EMPNO"] = 1m },
                NewValues = new Dictionary<string, object?> { ["ENAME"] = "it's" },
            },
            new RowChange(RowChangeKind.Update, 1, Emp) { Key = new Dictionary<string, object?> { ["EMPNO"] = 2m } }, // 바뀐 값 없음
        };

        var stmts = editor.BuildDml(changes);
        Assert.Single(stmts);

        var text = RowDmlBuilder.FormatPreview(changes.Select(RowDmlBuilder.Build).OfType<RowDml>());
        Assert.Contains("UPDATE SCOTT.EMP SET ENAME = :v1 WHERE EMPNO = :k1;", text, StringComparison.Ordinal);
        Assert.Contains(":v1 = 'it''s', :k1 = 1", text, StringComparison.Ordinal);
    }

    // ---------- 반영(세이브포인트·영향 행 수·정책) ----------

    private static RowChange Upd(long row, decimal key) => new(RowChangeKind.Update, row, Emp)
    {
        Key = new Dictionary<string, object?> { ["EMPNO"] = key },
        NewValues = new Dictionary<string, object?> { ["ENAME"] = "N" + row },
    };

    private static FakeExecutionService Engine(Func<ExecutionCall, ExecutionResult> dml)
    {
        var fake = new FakeExecutionService();
        fake.Responder = call => call.Statement.Kind == StatementKind.Dml
            ? dml(call)
            : new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);
        return fake;
    }

    private static ExecutionResult Rows(ExecutionCall c, long n) => new(c.Statement, ExecutionStatus.Succeeded) { RowsAffected = n };

    [Fact]
    public async Task Apply_runs_each_row_under_savepoint_through_execution_service_and_commits()
    {
        var engine = Engine(c => Rows(c, 1));
        var session = new FakeSession();

        var r = await new RowEditor(engine).ApplyAsync(session, [Upd(0, 1), Upd(1, 2)], commit: true, default);

        Assert.Equal(2, r.AppliedCount);
        Assert.True(r.Committed);
        Assert.Empty(r.Failures);
        Assert.Equal(1, session.CommitCount);
        Assert.Equal(
            ["SAVEPOINT ORANDA_EDIT", "SAVEPOINT ORANDA_EDIT_ROW", "UPDATE SCOTT.EMP SET ENAME = :v1 WHERE EMPNO = :k1",
             "SAVEPOINT ORANDA_EDIT_ROW", "UPDATE SCOTT.EMP SET ENAME = :v1 WHERE EMPNO = :k1"],
            engine.Calls.Select(c => c.Statement.Text));
        Assert.All(engine.Calls, c => Assert.Equal(Oranda.Core.History.HistorySource.GridEdit, c.Options.Source));
        Assert.Equal(2m, engine.Calls[4].Binds["k1"].Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Affected_rows_other_than_one_rolls_back_that_row_and_skips_commit(long affected)
    {
        var engine = Engine(c => Equals(c.Binds["k1"].Value, 2m) ? Rows(c, affected) : Rows(c, 1));
        var session = new FakeSession();

        var r = await new RowEditor(engine).ApplyAsync(session, [Upd(0, 1), Upd(5, 2), Upd(6, 3)], commit: true, default);

        Assert.Equal(2, r.AppliedCount);
        Assert.False(r.Committed);
        Assert.Equal(0, session.CommitCount);
        var f = Assert.Single(r.Failures);
        Assert.Equal(5, f.RowIndex);
        Assert.Equal(affected, f.AffectedRows);
        Assert.Equal("ORANDA-ROWCOUNT", f.Error!.Code);
        var texts = engine.Calls.Select(c => c.Statement.Text).ToList();
        var rollback = texts.IndexOf("ROLLBACK TO SAVEPOINT ORANDA_EDIT_ROW");
        Assert.Equal(5, rollback); // 두 번째 UPDATE 바로 뒤
        Assert.Equal(StatementKind.TransactionControl, engine.Calls[rollback].Statement.Kind);
    }

    [Fact]
    public async Task Server_error_rolls_back_that_row_and_reports_error()
    {
        var dup = new DbError("ORA-00001", "unique constraint violated") { Number = 1 };
        var engine = Engine(c => new ExecutionResult(c.Statement, ExecutionStatus.Failed) { Error = dup });

        var r = await new RowEditor(engine).ApplyAsync(new FakeSession(), [Upd(0, 1)], commit: false, default);

        Assert.Equal(0, r.AppliedCount);
        Assert.Same(dup, Assert.Single(r.Failures).Error);
        Assert.Contains(engine.Calls, c => c.Statement.Text == "ROLLBACK TO SAVEPOINT ORANDA_EDIT_ROW");
    }

    [Theory]
    [InlineData(ExecutionStatus.Denied, "ORANDA-DENIED")]
    [InlineData(ExecutionStatus.Cancelled, "ORANDA-CANCELLED")]
    public async Task Policy_denial_or_declined_confirmation_rolls_back_whole_batch(ExecutionStatus status, string code)
    {
        var engine = Engine(c => Equals(c.Binds["k1"].Value, 2m)
            ? new ExecutionResult(c.Statement, status)
            : Rows(c, 1));
        var session = new FakeSession();

        var r = await new RowEditor(engine).ApplyAsync(session, [Upd(0, 1), Upd(1, 2), Upd(2, 3)], commit: true, default);

        Assert.Equal(0, r.AppliedCount);
        Assert.False(r.Committed);
        Assert.Equal(3, r.Failures.Count);
        Assert.Equal(code, r.Failures[1].Error!.Code);
        Assert.Equal("ORANDA-ABORTED", r.Failures[2].Error!.Code);
        Assert.Equal("ROLLBACK TO SAVEPOINT ORANDA_EDIT", engine.Calls[^1].Statement.Text);
        Assert.DoesNotContain(engine.Calls, c => Equals(c.Binds.GetValueOrDefault("k1")?.Value, 3m));
        Assert.Equal(0, session.CommitCount);
    }

    [Fact]
    public async Task Read_only_profile_never_reaches_execution()
    {
        var engine = Engine(c => Rows(c, 1));

        var r = await new RowEditor(engine).ApplyAsync(new FakeSession(TestData.Profile(readOnly: true)), [Upd(0, 1)], commit: true, default);

        Assert.Empty(engine.Calls);
        Assert.Equal("ORANDA-READONLY", Assert.Single(r.Failures).Error!.Code);
    }

    [Fact]
    public async Task Keyless_change_fails_before_any_server_call()
    {
        var engine = Engine(c => Rows(c, 1));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new RowEditor(engine).ApplyAsync(new FakeSession(), [Upd(0, 1), new RowChange(RowChangeKind.Delete, 1, Emp)], true, default));
        Assert.Empty(engine.Calls);
    }
}
