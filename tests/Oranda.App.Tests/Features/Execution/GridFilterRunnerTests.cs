using System.Runtime.CompilerServices;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>GRID-10 결과 거르기의 실행 쪽: 감싼 문장, 바인드(셀 값·목록, 문장에 나온 이름만), 세션 바쁨 표시, 결과 넘김, 실패·취소·거부 안내.</summary>
public sealed class GridFilterRunnerTests
{
    private static readonly SqlStatement Query = new("SELECT * FROM emp WHERE deptno = :d -- 부서", default, StatementKind.Query)
    {
        Verb = "SELECT",
        BindNames = ["d"],
        SubstitutionNames = ["owner"],
    };

    /// <summary>받은 문장·바인드·옵션을 남기고 정해 둔 결과를 돌려주는 실행 엔진.</summary>
    private sealed class RecordingEngine : IExecutionService
    {
        public Func<SqlStatement, ExecutionResult> Result { get; set; } = s => new ExecutionResult(s, ExecutionStatus.Succeeded)
        {
            ResultSet = new RowsResult(),
        };

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

    /// <summary>한 칸짜리 결과(값은 COUNT(*) 로도 쓴다).</summary>
    private sealed class RowsResult(object? value = null) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new ResultColumn(0, "N", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => true;

        public long? KnownRowCount => 1;

        public bool Disposed { get; private set; }

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new ResultPage(pageIndex, 0, 1, [new object?[] { value ?? 1m }]) { IsLast = true });

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
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

            Binds.Add("d").Text = "10";
            Binds.Add("minsal").Text = "1000";
            Runner = new GridFilterRunner(State, Binds, () => Session, () => Engine);
        }

        public EventBus Bus { get; } = new();

        public FakeSession? Session { get; }

        public ExecutionState State { get; }

        public BindVariableStore Binds { get; } = new();

        public RecordingEngine Engine { get; } = new();

        public GridFilterRunner Runner { get; }
    }

    private static ResultFilterRequest Request(string where, string orderBy = "", params (string Name, object Value)[] cells) =>
        new(Query, where, orderBy)
        {
            Binds = cells.ToDictionary(c => c.Name, c => new BindValue(BindDataType.Varchar2, c.Value), StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public void Filter_statement_wraps_the_query_and_collects_bind_and_substitution_names_from_the_new_text()
    {
        var stmt = GridFilterRunner.BuildStatement(Request("SAL > :minsal AND ENAME = :oranda_f1 AND JOB = '&x' OR MGR = &&boss", "SAL DESC"));

        // 원래 문장 끝의 「-- 부서」 주석이 닫는 괄호를 먹지 않게, 조건은 괄호·줄바꿈으로 감싼다(조건 안의 OR 우선순위도 지킨다).
        Assert.Equal(
            "SELECT * FROM (\nSELECT * FROM emp WHERE deptno = :d -- 부서\n) WHERE (\nSAL > :minsal AND ENAME = :oranda_f1 AND JOB = '&x' OR MGR = &&boss\n)\nORDER BY SAL DESC",
            stmt.Text);
        Assert.Equal(["d", "minsal", "oranda_f1"], stmt.BindNames);
        Assert.Equal(["owner", "boss"], stmt.SubstitutionNames); // 문자열 안의 &x 는 치환 변수가 아니다
        Assert.Equal(StatementKind.Query, stmt.Kind);
        Assert.Equal("SELECT", stmt.Verb);
        Assert.Equal(default(Oranda.Core.Text.TextRange), stmt.Range);
    }

    [Fact]
    public void Order_only_and_empty_requests()
    {
        var ordered = GridFilterRunner.BuildStatement(Request(string.Empty, "2 DESC"));
        Assert.Equal("SELECT * FROM (\nSELECT * FROM emp WHERE deptno = :d -- 부서\n)\nORDER BY 2 DESC", ordered.Text);
        Assert.Equal(["d"], ordered.BindNames);

        // 조건·정렬이 모두 비면(거르기 지우기) 원래 문장 그대로 다시 실행한다.
        var plain = GridFilterRunner.BuildStatement(Request(string.Empty));
        Assert.Equal(Query.Text, plain.Text);
        Assert.Equal(Query.BindNames, plain.BindNames);
    }

    [Fact]
    public async Task Run_binds_cell_values_and_list_values_only_for_names_in_the_statement_and_hands_over_the_result()
    {
        var t = new Setup();
        RowsResult? rs = null;
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { ResultSet = rs = new RowsResult() };
        var busyDuringRun = false;
        t.Engine.During = () => busyDuringRun = t.State.Busy;

        var result = await t.Runner.RunAsync(Request("ENAME = :oranda_f1 AND SAL > :minsal", "SAL", ("oranda_f1", "O'Brien"), ("oranda_f9", "unused")),
            CancellationToken.None);

        Assert.Same(rs, result);
        Assert.False(rs!.Disposed); // 결과 집합의 수명은 결과 영역이 가진다
        var call = Assert.Single(t.Engine.Calls);
        Assert.StartsWith("SELECT * FROM (", call.Stmt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("O'Brien", call.Stmt.Text, StringComparison.Ordinal); // 값은 본문에 이어 붙이지 않는다
        Assert.Equal(["d", "minsal", "oranda_f1"], call.Binds.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("O'Brien", call.Binds["oranda_f1"].Value);
        Assert.Equal("10", call.Binds["d"].Value); // 「바인드 변수」 목록의 지금 값
        Assert.Equal("1000", call.Binds["minsal"].Value);
        Assert.True(call.Options!.SkipHistory);
        Assert.True(busyDuringRun);
        Assert.False(t.State.Busy);
    }

    [Fact]
    public async Task Server_error_policy_denial_cancel_and_no_result_become_exceptions_and_close_any_result()
    {
        var t = new Setup();
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Failed)
        {
            Error = new DbError("ORA-00904", "\"SALX\": invalid identifier") { Number = 904 },
        };
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.RunAsync(Request("SALX > 1"), CancellationToken.None));
        Assert.Equal("ORA-00904: \"SALX\": invalid identifier", failed.Message);
        Assert.False(t.State.Busy);

        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Denied)
        {
            Policy = new PolicyDecision(PolicyDecisionKind.Deny, Message: "읽기 전용 접속입니다."),
        };
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.RunAsync(Request("SAL > 1"), CancellationToken.None));
        Assert.Equal("읽기 전용 접속입니다.", denied.Message);

        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Runner.RunAsync(Request("SAL > 1"), CancellationToken.None));

        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { RowsAffected = 0 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Runner.RunAsync(Request("SAL > 1"), CancellationToken.None));

        var stray = new RowsResult();
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Cancelled) { ResultSet = stray };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Runner.RunAsync(Request("SAL > 1"), CancellationToken.None));
        Assert.True(stray.Disposed);
        Assert.False(t.State.Busy);
    }

    [Fact]
    public async Task Nothing_runs_without_a_session_while_busy_with_a_missing_bind_or_for_a_non_query()
    {
        var offline = new Setup(connected: false);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => offline.Runner.RunAsync(Request("SAL > 1"), CancellationToken.None));
        Assert.Contains("접속이 없습니다", none.Message, StringComparison.Ordinal);

        var busy = new Setup();
        busy.State.BeginRun();
        var running = await Assert.ThrowsAsync<InvalidOperationException>(() => busy.Runner.RunAsync(Request("SAL > 1"), CancellationToken.None));
        Assert.Contains("실행하는 중", running.Message, StringComparison.Ordinal);
        Assert.Empty(busy.Engine.Calls);
        busy.State.EndRun();

        var missing = new Setup();
        var bind = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Runner.RunAsync(Request("JOB = :job"), CancellationToken.None));
        Assert.Contains(":job", bind.Message, StringComparison.Ordinal);
        Assert.Equal(2, missing.Binds.Rows.Count); // 거르기는 바인드 목록에 줄을 더하지 않는다
        Assert.Empty(missing.Engine.Calls);

        var plsql = new ResultFilterRequest(new SqlStatement("BEGIN OPEN :c FOR SELECT 1 FROM dual; END;", default, StatementKind.PlSql), "N = 1", string.Empty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Runner.RunAsync(plsql, CancellationToken.None));
    }

    [Fact]
    public async Task Count_of_a_filtered_result_wraps_the_filter_without_order_by_and_passes_cell_binds()
    {
        var t = new Setup();
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { ResultSet = new RowsResult(7m) };

        var total = await t.Runner.CountAsync(Request("ENAME = :oranda_f1", "SAL DESC", ("oranda_f1", "KING")), CancellationToken.None);

        Assert.Equal(7, total);
        var call = Assert.Single(t.Engine.Calls);
        Assert.StartsWith("SELECT COUNT(*) FROM (\nSELECT * FROM (\n", call.Stmt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", call.Stmt.Text, StringComparison.Ordinal);
        Assert.Equal("KING", call.Binds["oranda_f1"].Value);
        Assert.Equal("10", call.Binds["d"].Value);
        Assert.True(call.Options!.SkipHistory);
    }
}
