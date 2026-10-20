using System.Runtime.CompilerServices;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>GRID-09 「전체 행 수 세기」 실행 쪽: COUNT(*) 감싸기, 바인드 값, 세션 바쁨 표시, 실패·취소·거부 안내.</summary>
public sealed class GridRowCounterTests
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
            ResultSet = new CountResult(42m),
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

    /// <summary>COUNT(*) 한 칸짜리 결과.</summary>
    private sealed class CountResult(object? value) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new ResultColumn(0, "COUNT(*)", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => true;

        public long? KnownRowCount => 1;

        public bool Disposed { get; private set; }

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new ResultPage(pageIndex, 0, 1, [new object?[] { value }]) { IsLast = true });

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
            Counter = new GridRowCounter(State, Binds, () => Session, () => Engine);
        }

        public EventBus Bus { get; } = new();

        public FakeSession? Session { get; }

        public ExecutionState State { get; }

        public BindVariableStore Binds { get; } = new();

        public RecordingEngine Engine { get; } = new();

        public GridRowCounter Counter { get; }
    }

    [Fact]
    public void Count_statement_wraps_the_query_and_keeps_bind_and_substitution_names()
    {
        var count = GridRowCounter.BuildCountStatement(Query);

        // 끝의 「-- 부서」 주석이 닫는 괄호를 먹지 않게 줄을 바꿔 감싼다.
        Assert.Equal("SELECT COUNT(*) FROM (\nSELECT * FROM emp WHERE deptno = :d -- 부서\n)", count.Text);
        Assert.Equal(StatementKind.Query, count.Kind);
        Assert.Equal("SELECT", count.Verb);
        Assert.Equal("d", Assert.Single(count.BindNames));
        Assert.Equal("owner", Assert.Single(count.SubstitutionNames));
        Assert.Equal(default(Oranda.Core.Text.TextRange), count.Range);
    }

    [Fact]
    public async Task Count_runs_through_the_engine_with_current_bind_values_and_skips_history()
    {
        var t = new Setup();
        CountResult? rs = null;
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { ResultSet = rs = new CountResult(12345m) };
        var busyDuringRun = false;
        t.Engine.During = () => busyDuringRun = t.State.Busy;

        var total = await t.Counter.CountAsync(Query, CancellationToken.None);

        Assert.Equal(12345, total);
        var call = Assert.Single(t.Engine.Calls);
        Assert.StartsWith("SELECT COUNT(*) FROM (", call.Stmt.Text, StringComparison.Ordinal);
        Assert.Equal("10", call.Binds["d"].Value); // 「바인드 변수」 목록의 지금 값
        Assert.True(call.Options!.SkipHistory);
        Assert.True(busyDuringRun); // 세는 동안 세션은 「실행 중」(다른 실행과 겹치지 않게)
        Assert.False(t.State.Busy);
        Assert.True(rs!.Disposed);
    }

    [Fact]
    public async Task Count_value_may_arrive_as_a_raw_number_string()
    {
        var t = new Setup();
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Succeeded) { ResultSet = new CountResult("9876543210") };

        Assert.Equal(9876543210L, await t.Counter.CountAsync(Query, CancellationToken.None));
    }

    [Fact]
    public async Task Server_error_policy_denial_and_cancel_become_exceptions_with_reasons()
    {
        var t = new Setup();
        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Failed)
        {
            Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942 },
        };
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Counter.CountAsync(Query, CancellationToken.None));
        Assert.Equal("ORA-00942: table or view does not exist", failed.Message);
        Assert.False(t.State.Busy);

        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Denied)
        {
            Policy = new PolicyDecision(PolicyDecisionKind.Deny, Message: "읽기 전용 접속입니다."),
        };
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Counter.CountAsync(Query, CancellationToken.None));
        Assert.Equal("읽기 전용 접속입니다.", denied.Message);

        t.Engine.Result = s => new ExecutionResult(s, ExecutionStatus.Cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Counter.CountAsync(Query, CancellationToken.None));
        Assert.False(t.State.Busy);
    }

    [Fact]
    public async Task Nothing_runs_without_a_session_while_busy_or_with_a_missing_bind()
    {
        var offline = new Setup(connected: false);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => offline.Counter.CountAsync(Query, CancellationToken.None));
        Assert.Contains("접속이 없습니다", none.Message, StringComparison.Ordinal);

        var busy = new Setup();
        busy.State.BeginRun();
        var running = await Assert.ThrowsAsync<InvalidOperationException>(() => busy.Counter.CountAsync(Query, CancellationToken.None));
        Assert.Contains("실행하는 중", running.Message, StringComparison.Ordinal);
        Assert.True(busy.State.Busy); // 남의 실행 상태는 건드리지 않는다
        Assert.Empty(busy.Engine.Calls);
        busy.State.EndRun();

        var missing = new Setup();
        var stmt = Query with { BindNames = ["d", "job"] };
        var bind = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Counter.CountAsync(stmt, CancellationToken.None));
        Assert.Contains(":job", bind.Message, StringComparison.Ordinal);
        Assert.Single(missing.Binds.Rows); // 세기는 바인드 목록에 줄을 더하지 않는다
        Assert.Empty(missing.Engine.Calls);

        var plsql = new SqlStatement("BEGIN NULL; END;", default, StatementKind.PlSql);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Counter.CountAsync(plsql, CancellationToken.None));
    }
}
