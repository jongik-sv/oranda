using System.Runtime.CompilerServices;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// GRID-11 외래키 따라가기의 실행 쪽: 열이 속한 테이블 찾기(그리드 편집 규칙·드라이버 출처), 부모·자식 질의 모양,
/// 행 값은 바인드로만, 외래키 고르기, NULL·없는 열·외래키 아님 안내, 세션 바쁨 표시와 이력 건너뛰기.
/// </summary>
public sealed class RelatedRowsRunnerTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private static readonly DbObjectRef Dept = new("SCOTT", "DEPT", "TABLE");
    private static readonly DbObjectRef Orders = new("SALES", "ORDERS", "TABLE");
    private static readonly DbObjectRef Lines = new("SALES", "LINES", "TABLE");

    private static readonly ResultColumn[] EmpCols =
    [
        new(0, "EMPNO", "NUMBER", ColumnDataKind.Number),
        new(1, "ENAME", "VARCHAR2", ColumnDataKind.Text),
        new(2, "MGR", "NUMBER", ColumnDataKind.Number),
        new(3, "DEPTNO", "NUMBER", ColumnDataKind.Number),
    ];

    private static readonly ResultColumn[] DeptCols =
    [
        new(0, "DEPTNO", "NUMBER", ColumnDataKind.Number),
        new(1, "DNAME", "VARCHAR2", ColumnDataKind.Text),
    ];

    private static readonly TableInfo EmpTable = new(Emp, [Col("EMPNO", 1), Col("ENAME", 2), Col("MGR", 3), Col("DEPTNO", 4)])
    {
        Constraints =
        [
            new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["EMPNO"]),
            new ConstraintInfo("FK_EMP_DEPT", ConstraintKind.ForeignKey, ["DEPTNO"]) { ReferencedTable = Dept, ReferencedConstraint = "PK_DEPT" },
            new ConstraintInfo("FK_EMP_MGR", ConstraintKind.ForeignKey, ["MGR"]) { ReferencedTable = Emp, ReferencedConstraint = "PK_EMP" },
        ],
    };

    private static readonly TableInfo DeptTable = new(Dept, [Col("DEPTNO", 1), Col("DNAME", 2)])
    {
        Constraints = [new ConstraintInfo("PK_DEPT", ConstraintKind.PrimaryKey, ["DEPTNO"])],
    };

    /// <summary>기본키와 열 수가 같은 유일 키를 가진 부모: 외래키는 유일 키를 참조한다.</summary>
    private static readonly TableInfo OrdersTable = new(Orders, [Col("ID", 1), Col("REV", 2), Col("LINE_NO", 3)])
    {
        Constraints =
        [
            new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID", "REV"]),
            new ConstraintInfo("UK_ORDER_SLOT", ConstraintKind.Unique, ["ID", "LINE_NO"]),
        ],
    };

    private static readonly TableInfo LinesTable = new(Lines, [Col("LINE_ID", 1), Col("ORD_ID", 2), Col("ORD_LINE", 3)])
    {
        Constraints =
        [
            new ConstraintInfo("FK_LINE_SLOT", ConstraintKind.ForeignKey, ["ORD_ID", "ORD_LINE"])
            {
                ReferencedTable = Orders,
                ReferencedConstraint = "UK_ORDER_SLOT",
            },
        ],
    };

    private static ColumnInfo Col(string name, int position) => new(name, position, "NUMBER");

    private static SqlStatement Q(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static RelatedRowsRequest EmpRow(RelatedRowsKind kind, int ordinal, object? deptno = null, object? mgr = null) =>
        new(kind, Q("SELECT * FROM emp WHERE sal > 1000"), EmpCols, ordinal, [7369m, "SMITH", mgr ?? 7902m, deptno ?? 20m]);

    private static DetailSection ReferencedBy(params string[][] rows) =>
        new("참조됨", ["소유자", "테이블", "외래키", "열", "참조 키", "삭제 규칙", "상태"],
            [.. rows.Select(r => (IReadOnlyList<object?>)[r[0], r[1], r[2], r[3], r[4], "NO ACTION", "ENABLED"])]);

    /// <summary>받은 문장·바인드·옵션을 남기고 빈 결과를 돌려주는 실행 엔진.</summary>
    private sealed class Engine : IExecutionService
    {
        public Func<SqlStatement, ExecutionResult> Result { get; set; } = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { ResultSet = new EmptyResult() };

        public List<(SqlStatement Stmt, IReadOnlyDictionary<string, BindValue> Binds, ExecutionOptions? Options)> Calls { get; } = [];

        public Action? During { get; set; }

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                  ExecutionOptions? options, CancellationToken ct)
        {
            Calls.Add((stmt, binds, options));
            During?.Invoke();
            return Task.FromResult(Result(stmt));
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                         ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EmptyResult : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new ResultColumn(0, "X", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => true;

        public long? KnownRowCount => 0;

        public bool Disposed { get; private set; }

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new ResultPage(pageIndex, 0, 0, [Array.Empty<object?>()]) { IsLast = true });

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>테이블 정보와 객체 상세를 이름으로 돌려주는 메타데이터 제공자. 읽은 객체를 남긴다.</summary>
    private sealed class Metadata : IMetadataProvider
    {
        public Dictionary<DbObjectRef, TableInfo> Tables { get; } = new()
        {
            [Emp] = EmpTable,
            [Dept] = DeptTable,
            [Orders] = OrdersTable,
            [Lines] = LinesTable,
        };

        public Dictionary<DbObjectRef, Oranda.Core.Metadata.ObjectDetail> Details { get; } = [];

        public List<string> Reads { get; } = [];

        public Action? During { get; set; }

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
        {
            Reads.Add("table " + table.Owner + "." + table.Name);
            During?.Invoke();
            return Tables.TryGetValue(table, out var info)
                ? Task.FromResult(info)
                : Task.FromException<TableInfo>(new InvalidOperationException($"테이블 또는 뷰를 찾을 수 없습니다: {table.Owner}.{table.Name}"));
        }

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            Reads.Add("detail " + obj.Owner + "." + obj.Name);
            return Task.FromResult(Details.TryGetValue(obj, out var d) ? d : new Oranda.Core.Metadata.ObjectDetail(obj, []));
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Setup
    {
        public Setup(bool connected = true)
        {
            var profile = new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1");
            Session = connected ? new FakeSession(profile) : null;
            State = new ExecutionState(Bus);
            if (Session is not null)
            {
                Bus.Publish(new SessionOpened(Session.Id, profile, Session.Server, SessionPurpose.Work));
            }

            Runner = new RelatedRowsRunner(State, () => Session, () => Engine, () => Meta, (title, items, _) =>
            {
                Asked.Add((title, items));
                return Task.FromResult(Pick(items));
            });
        }

        public EventBus Bus { get; } = new();

        public FakeSession? Session { get; }

        public ExecutionState State { get; }

        public Engine Engine { get; } = new();

        public Metadata Meta { get; } = new();

        /// <summary>외래키 고르기 대화상자가 받은 제목·목록.</summary>
        public List<(string Title, IReadOnlyList<string> Items)> Asked { get; } = [];

        /// <summary>대화상자의 답. 기본은 첫 항목.</summary>
        public Func<IReadOnlyList<string>, string?> Pick { get; set; } = items => items[0];

        public RelatedRowsRunner Runner { get; }
    }

    // ---- 부모 행 ---------------------------------------------------------------------------

    [Fact]
    public async Task Parent_query_selects_the_referenced_table_by_its_key_with_the_row_value_bound()
    {
        var t = new Setup();
        var busyDuringRun = false;
        t.Engine.During = () => busyDuringRun = t.State.Busy;

        var opened = await t.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 3), CancellationToken.None);

        Assert.NotNull(opened);
        Assert.Equal("부모: DEPT", opened.Title);
        var call = Assert.Single(t.Engine.Calls);
        Assert.Equal("SELECT * FROM \"SCOTT\".\"DEPT\" WHERE \"DEPTNO\" = :v1", call.Stmt.Text);
        Assert.Same(call.Stmt, opened.Statement);
        Assert.Equal(StatementKind.Query, opened.Statement.Kind);
        Assert.Equal("SELECT", opened.Statement.Verb);
        Assert.Equal(["v1"], opened.Statement.BindNames);
        Assert.Equal(new BindValue(BindDataType.Number, 20m), call.Binds["v1"]); // 값은 바인드로만, 본문에 이어 붙이지 않는다
        Assert.Equal(call.Binds, opened.Binds);
        Assert.True(call.Options!.SkipHistory);
        Assert.True(busyDuringRun);
        Assert.False(t.State.Busy);
        Assert.IsType<EmptyResult>(opened.Result);
        Assert.Empty(t.Asked);
        Assert.Equal(["table SCOTT.EMP", "table SCOTT.DEPT"], t.Meta.Reads); // 스키마를 적지 않은 emp 는 현재 사용자
    }

    [Fact]
    public async Task Self_referencing_foreign_key_follows_to_the_same_table()
    {
        var t = new Setup();

        var opened = await t.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 2, mgr: 7902m), CancellationToken.None);

        Assert.Equal("부모: EMP", opened!.Title);
        Assert.Equal("SELECT * FROM \"SCOTT\".\"EMP\" WHERE \"EMPNO\" = :v1", opened.Statement.Text);
        Assert.Equal(new BindValue(BindDataType.Number, 7902m), opened.Binds["v1"]);
    }

    [Fact]
    public async Task Multi_column_foreign_key_pairs_columns_by_position_with_the_referenced_unique_key()
    {
        var t = new Setup();
        ResultColumn[] cols =
        [
            new(0, "LINE_ID", "NUMBER", ColumnDataKind.Number),
            new(1, "ORD_LINE", "NUMBER", ColumnDataKind.Number),
            new(2, "ORD_ID", "NUMBER", ColumnDataKind.Number),
        ];
        var request = new RelatedRowsRequest(RelatedRowsKind.Parent, Q("SELECT l.line_id, l.ord_line, l.ord_id FROM sales.lines l"),
            cols, 2, [1m, 3m, 100m]);

        var opened = await t.Runner.OpenAsync(request, CancellationToken.None);

        // 기본키 (ID, REV) 가 아니라 외래키가 참조하는 유일 키 (ID, LINE_NO) 로, 외래키 열 순서(ORD_ID, ORD_LINE)대로 짝짓는다.
        Assert.Equal("SELECT * FROM \"SALES\".\"ORDERS\" WHERE \"ID\" = :v1 AND \"LINE_NO\" = :v2", opened!.Statement.Text);
        Assert.Equal(["v1", "v2"], opened.Statement.BindNames);
        Assert.Equal(100m, opened.Binds["v1"].Value);
        Assert.Equal(3m, opened.Binds["v2"].Value);
        Assert.Equal("부모: ORDERS", opened.Title);
    }

    [Fact]
    public async Task Null_in_a_foreign_key_column_means_there_is_no_parent_and_nothing_runs()
    {
        var t = new Setup();
        var request = new RelatedRowsRequest(RelatedRowsKind.Parent, Q("SELECT * FROM emp"), EmpCols, 3, [7839m, "KING", null, DBNull.Value]);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(request, CancellationToken.None));

        Assert.Contains("NULL", e.Message, StringComparison.Ordinal);
        Assert.Contains("FK_EMP_DEPT", e.Message, StringComparison.Ordinal);
        Assert.Empty(t.Engine.Calls);
        Assert.False(t.State.Busy);
    }

    [Fact]
    public async Task Column_outside_every_foreign_key_is_refused_with_the_reason()
    {
        var t = new Setup();

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 1), CancellationToken.None));

        Assert.Contains("ENAME", e.Message, StringComparison.Ordinal);
        Assert.Contains("외래키 열이 아닙니다", e.Message, StringComparison.Ordinal);
        Assert.Empty(t.Engine.Calls);
    }

    [Fact]
    public async Task Foreign_key_column_missing_from_the_result_is_named()
    {
        var t = new Setup();
        ResultColumn[] cols = [new(0, "ORD_ID", "NUMBER", ColumnDataKind.Number)];
        var request = new RelatedRowsRequest(RelatedRowsKind.Parent, Q("SELECT ord_id FROM sales.lines"), cols, 0, [100m]);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(request, CancellationToken.None));

        Assert.Contains("ORD_LINE", e.Message, StringComparison.Ordinal);
        Assert.Contains("없습니다", e.Message, StringComparison.Ordinal);
        Assert.Empty(t.Engine.Calls);
    }

    [Fact]
    public async Task Column_in_two_foreign_keys_asks_which_one_and_cancel_runs_nothing()
    {
        var t = new Setup();
        var audit = new DbObjectRef("SCOTT", "AUDIT_DEPT", "TABLE");
        t.Meta.Tables[audit] = new TableInfo(audit, [Col("ID", 1)])
        {
            Constraints = [new ConstraintInfo("PK_AUDIT_DEPT", ConstraintKind.PrimaryKey, ["ID"])],
        };
        t.Meta.Tables[Emp] = EmpTable with
        {
            Constraints =
            [
                .. EmpTable.Constraints,
                new ConstraintInfo("FK_EMP_AUDIT", ConstraintKind.ForeignKey, ["DEPTNO"]) { ReferencedTable = audit, ReferencedConstraint = "PK_AUDIT_DEPT" },
            ],
        };

        t.Pick = _ => null;
        Assert.Null(await t.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 3), CancellationToken.None));
        Assert.Empty(t.Engine.Calls);
        Assert.False(t.State.Busy); // 고르는 동안·취소한 뒤에는 세션이 바쁘지 않다

        t.Pick = items => items[1];
        var opened = await t.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 3), CancellationToken.None);

        Assert.Equal(2, t.Asked.Count);
        Assert.Equal(["FK_EMP_DEPT → SCOTT.DEPT (DEPTNO)", "FK_EMP_AUDIT → SCOTT.AUDIT_DEPT (DEPTNO)"], t.Asked[1].Items);
        Assert.Equal("SELECT * FROM \"SCOTT\".\"AUDIT_DEPT\" WHERE \"ID\" = :v1", opened!.Statement.Text);
        Assert.Equal("부모: AUDIT_DEPT", opened.Title);
    }

    // ---- 자식 행 ---------------------------------------------------------------------------

    [Fact]
    public async Task Children_query_selects_the_child_table_by_its_foreign_key_after_choosing_one()
    {
        var t = new Setup();
        t.Meta.Details[Dept] = new Oranda.Core.Metadata.ObjectDetail(Dept,
        [
            ReferencedBy(["SCOTT", "EMP", "FK_EMP_DEPT", "DEPTNO", "PK_DEPT"], ["HR", "PROJ", "FK_PROJ_DEPT", "OWNER_DEPT", "PK_DEPT"]),
        ]);
        var request = new RelatedRowsRequest(RelatedRowsKind.Children, Q("SELECT d.deptno, d.dname FROM dept d"), DeptCols, 1, [30m, "SALES"]);
        t.Pick = items => items[1];

        var opened = await t.Runner.OpenAsync(request, CancellationToken.None);

        var asked = Assert.Single(t.Asked);
        Assert.Equal(["SCOTT.EMP(FK_EMP_DEPT): DEPTNO", "HR.PROJ(FK_PROJ_DEPT): OWNER_DEPT"], asked.Items);
        Assert.Equal("자식: PROJ(FK_PROJ_DEPT)", opened!.Title);
        var call = Assert.Single(t.Engine.Calls);
        Assert.Equal("SELECT * FROM \"HR\".\"PROJ\" WHERE \"OWNER_DEPT\" = :v1", call.Stmt.Text);
        Assert.Equal(new BindValue(BindDataType.Number, 30m), call.Binds["v1"]);
        Assert.True(call.Options!.SkipHistory);
        Assert.False(t.State.Busy);
    }

    [Fact]
    public async Task Single_referencing_foreign_key_opens_without_asking()
    {
        var t = new Setup();
        t.Meta.Details[Dept] = new Oranda.Core.Metadata.ObjectDetail(Dept,
        [
            new DetailSection("의존", ["방향", "소유자", "이름", "유형"], [["참조됨", "SCOTT", "DEPT_V", "VIEW"]]),
            ReferencedBy(["SCOTT", "EMP", "FK_EMP_DEPT", "DEPTNO", "PK_DEPT"]),
        ]);
        var request = new RelatedRowsRequest(RelatedRowsKind.Children, Q("SELECT * FROM dept"), DeptCols, 0, [20m, "RESEARCH"]);

        var opened = await t.Runner.OpenAsync(request, CancellationToken.None);

        Assert.Empty(t.Asked);
        Assert.Equal("자식: EMP(FK_EMP_DEPT)", opened!.Title);
        Assert.Equal("SELECT * FROM \"SCOTT\".\"EMP\" WHERE \"DEPTNO\" = :v1", opened.Statement.Text);
    }

    [Fact]
    public async Task Multi_column_child_key_pairs_the_referenced_key_with_the_foreign_key_by_position()
    {
        var t = new Setup();
        t.Meta.Details[Orders] = new Oranda.Core.Metadata.ObjectDetail(Orders,
        [
            ReferencedBy(["SALES", "LINES", "FK_LINE_SLOT", "ORD_ID, ORD_LINE", "UK_ORDER_SLOT"]),
        ]);
        ResultColumn[] cols =
        [
            new(0, "LINE_NO", "NUMBER", ColumnDataKind.Number),
            new(1, "ID", "NUMBER", ColumnDataKind.Number),
            new(2, "REV", "NUMBER", ColumnDataKind.Number),
        ];
        var request = new RelatedRowsRequest(RelatedRowsKind.Children, Q("SELECT line_no, id, rev FROM sales.orders"), cols, 2, [7m, 100m, 1m]);

        var opened = await t.Runner.OpenAsync(request, CancellationToken.None);

        Assert.Equal("SELECT * FROM \"SALES\".\"LINES\" WHERE \"ORD_ID\" = :v1 AND \"ORD_LINE\" = :v2", opened!.Statement.Text);
        Assert.Equal(100m, opened.Binds["v1"].Value); // UK_ORDER_SLOT 첫 열 ID
        Assert.Equal(7m, opened.Binds["v2"].Value); // 둘째 열 LINE_NO
    }

    [Fact]
    public async Task Table_nobody_references_says_so()
    {
        var t = new Setup();
        var request = new RelatedRowsRequest(RelatedRowsKind.Children, Q("SELECT * FROM dept"), DeptCols, 0, [20m, "RESEARCH"]);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(request, CancellationToken.None));

        Assert.Contains("SCOTT.DEPT", e.Message, StringComparison.Ordinal);
        Assert.Contains("참조하는 외래키가 없습니다", e.Message, StringComparison.Ordinal);
        Assert.Empty(t.Engine.Calls);
    }

    [Fact]
    public async Task Referenced_key_missing_from_the_result_is_named_for_children()
    {
        var t = new Setup();
        t.Meta.Details[Dept] = new Oranda.Core.Metadata.ObjectDetail(Dept, [ReferencedBy(["SCOTT", "EMP", "FK_EMP_DEPT", "DEPTNO", "PK_DEPT"])]);
        ResultColumn[] cols = [new(0, "DNAME", "VARCHAR2", ColumnDataKind.Text)];
        var request = new RelatedRowsRequest(RelatedRowsKind.Children, Q("SELECT dname FROM dept"), cols, 0, ["SALES"]);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(request, CancellationToken.None));

        Assert.Contains("키 열 DEPTNO", e.Message, StringComparison.Ordinal);
        Assert.Empty(t.Engine.Calls);
    }

    // ---- 열의 테이블 찾기 ------------------------------------------------------------------

    [Fact]
    public void Join_result_resolves_the_table_of_the_active_column()
    {
        ResultColumn[] cols =
        [
            new(0, "ENAME", "VARCHAR2", ColumnDataKind.Text),
            new(1, "DNAME", "VARCHAR2", ColumnDataKind.Text),
            new(2, "DEPTNO", "NUMBER", ColumnDataKind.Number),
        ];
        var request = new RelatedRowsRequest(RelatedRowsKind.Children,
            Q("SELECT e.ename, d.dname, d.deptno FROM scott.emp e JOIN dept d ON d.deptno = e.deptno"), cols, 1, ["SMITH", "RESEARCH", 20m]);

        var source = RelatedRowsRunner.ResolveTable(request, "SCOTT");

        Assert.Equal(Dept, source.Table);
        Assert.Equal([null, "DNAME", "DEPTNO"], source.Names);
    }

    [Fact]
    public void Query_the_analyzer_cannot_read_falls_back_to_the_driver_base_columns()
    {
        ResultColumn[] cols =
        [
            new(0, "DEPTNO", "NUMBER", ColumnDataKind.Number) { BaseOwner = "SCOTT", BaseTable = "EMP", BaseColumn = "DEPTNO" },
            new(1, "N", "NUMBER", ColumnDataKind.Number),
        ];
        var request = new RelatedRowsRequest(RelatedRowsKind.Parent, Q("SELECT DISTINCT deptno, 1 AS n FROM emp"), cols, 0, [20m, 1m]);

        var source = RelatedRowsRunner.ResolveTable(request, "OTHER");

        Assert.Equal(Emp, source.Table);
        Assert.Equal(["DEPTNO", null], source.Names);
    }

    [Fact]
    public async Task Expression_column_of_a_join_has_no_table()
    {
        var t = new Setup();
        ResultColumn[] cols =
        [
            new(0, "ENAME", "VARCHAR2", ColumnDataKind.Text),
            new(1, "TOTAL", "NUMBER", ColumnDataKind.Number),
        ];
        var request = new RelatedRowsRequest(RelatedRowsKind.Parent,
            Q("SELECT e.ename, e.sal + d.bonus AS total FROM emp e JOIN dept d ON d.deptno = e.deptno"), cols, 1, ["SMITH", 900m]);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(request, CancellationToken.None));

        Assert.Contains("TOTAL", e.Message, StringComparison.Ordinal);
        Assert.Contains("어느 테이블", e.Message, StringComparison.Ordinal);
        Assert.Empty(t.Meta.Reads);
    }

    // ---- 접속·정책·형식 ---------------------------------------------------------------------

    [Fact]
    public async Task No_session_or_a_busy_session_runs_nothing_and_leaves_the_run_state_alone()
    {
        var none = new Setup(connected: false);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => none.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 3), CancellationToken.None));
        Assert.Contains("접속", e.Message, StringComparison.Ordinal);

        var busy = new Setup();
        busy.State.BeginRun();
        e = await Assert.ThrowsAsync<InvalidOperationException>(() => busy.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 3), CancellationToken.None));
        Assert.Contains("실행하는 중", e.Message, StringComparison.Ordinal);
        Assert.True(busy.State.Busy); // 남의 실행 상태는 건드리지 않는다
        Assert.Empty(busy.Engine.Calls);
        Assert.Empty(busy.Meta.Reads);
    }

    [Fact]
    public async Task Policy_denial_is_reported_and_the_session_is_idle_again()
    {
        var t = new Setup();
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Denied)
        {
            Policy = new PolicyDecision(PolicyDecisionKind.Deny, Message: "읽기 전용 접속입니다."),
        };

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(EmpRow(RelatedRowsKind.Parent, 3), CancellationToken.None));

        Assert.Equal("읽기 전용 접속입니다.", e.Message);
        Assert.False(t.State.Busy);
    }

    [Fact]
    public void Query_quotes_dictionary_names_exactly_and_refuses_names_with_quotes()
    {
        var stmt = RelatedRowsRunner.BuildQuery(new DbObjectRef("App", "Order Lines", "TABLE"), ["Line No", "ID"]);

        Assert.Equal("SELECT * FROM \"App\".\"Order Lines\" WHERE \"Line No\" = :v1 AND \"ID\" = :v2", stmt.Text);
        Assert.Equal(["v1", "v2"], stmt.BindNames);
        Assert.Throws<InvalidOperationException>(() => RelatedRowsRunner.BuildQuery(new DbObjectRef("A", "B\"C", "TABLE"), ["X"]));
    }

    [Fact]
    public void Referenced_key_without_its_name_falls_back_to_the_primary_key_then_a_single_unique_key()
    {
        var fk = new ConstraintInfo("FK", ConstraintKind.ForeignKey, ["A", "B"]) { ReferencedTable = Orders };

        Assert.Equal("PK_ORDERS", RelatedRowsRunner.ReferencedKey(OrdersTable, fk)!.Name); // 이름을 모르면(옛 캐시) 기본키
        Assert.Equal("UK_ORDER_SLOT", RelatedRowsRunner.ReferencedKey(OrdersTable, fk with { ReferencedConstraint = "UK_ORDER_SLOT" })!.Name);
        Assert.Null(RelatedRowsRunner.ReferencedKey(OrdersTable, fk with { ReferencedConstraint = "GONE" }));

        var uniqueOnly = OrdersTable with { Constraints = [OrdersTable.Constraints[1]] };
        Assert.Equal("UK_ORDER_SLOT", RelatedRowsRunner.ReferencedKey(uniqueOnly, fk)!.Name);
    }

    [Fact]
    public async Task Values_that_cannot_be_bound_are_refused()
    {
        var t = new Setup();
        ResultColumn[] cols = [new(0, "DEPTNO", "CLOB", ColumnDataKind.Clob)];
        var request = new RelatedRowsRequest(RelatedRowsKind.Parent, Q("SELECT deptno FROM emp"), cols, 0, ["20"]);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.OpenAsync(request, CancellationToken.None));

        Assert.Contains("바인드로 넘길 수 없습니다", e.Message, StringComparison.Ordinal);
        Assert.Empty(t.Engine.Calls);
    }
}
