using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Transfer.Copy;

/// <summary>
/// Tibero 실행기가 되살린 트리거의 컴파일 상태를 보고 INVALID 이면 끄는지(서버 없음, 가짜 실행·가짜 상태 조회).
/// Tibero 는 없는 열을 가리키는 트리거 CREATE 를 오류 없이 받아 INVALID+ENABLED 로 남기므로(#79) Oracle 처럼 오류(ORA-24344)로 알 수 없다.
/// 서버로 도는 확인은 <c>TiberoCopyTriggerStatusIt</c>.
/// </summary>
public sealed class TiberoCopyRunnerTriggerTests
{
    private const string Owner = "SCOTT";
    private const string Table = "EMP";
    private const string Create = "CREATE OR REPLACE TRIGGER \"SCOTT\".\"TRG_BAD\" BEFORE INSERT ON \"SCOTT\".\"EMP\" FOR EACH ROW BEGIN :NEW.GONE := 1; END;";
    private const string DisableText = "ALTER TRIGGER \"SCOTT\".\"TRG_BAD\" DISABLE";

    private static readonly DisabledObject Trigger = new(DisabledKind.Trigger, Owner, Table, "TRG_BAD");

    [Fact]
    public async Task Invalid_trigger_is_found_by_the_status_query_and_disabled_with_the_same_notice_wording()
    {
        var f = new Fixture("INVALID");

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]));

        Assert.True(failed); // 되살리지 못한 것이 있는 결과 = 실패
        Assert.Equal([Create, DisableText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        var notice = Assert.Single(notices);
        Assert.Contains("SCOTT.TRG_BAD", notice, StringComparison.Ordinal);
        Assert.Contains("다시 만든 열 구조와 맞지 않아 컴파일 오류로 INVALID 입니다. 꺼 두었으니 고친 뒤 켜세요.", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("ORA-", notice, StringComparison.Ordinal); // Tibero 는 번호를 모른다(번호 없는 일반 문구)
        Assert.DoesNotContain("TBR-", notice, StringComparison.Ordinal);

        // 상태 조회는 대상 세션에 한 번, 트리거 소유자·이름을 바인드로 보낸다.
        var query = Assert.Single(f.Bulk.Queries);
        Assert.Same(f.Target, query.Session);
        Assert.StartsWith("SELECT", query.Sql, StringComparison.Ordinal);
        Assert.Equal(Owner, query.Binds["OWN"].Value);
        Assert.Equal("TRG_BAD", query.Binds["NAME"].Value);
    }

    /// <summary>위험 1: 지우기 전부터 INVALID 였던 트리거는 그대로 되살리고 꺼 두되, 다시 만들기가 만든 문제가 아니라 결과를 실패로 두지 않고 알림만 한다.</summary>
    [Fact]
    public async Task Trigger_that_was_invalid_before_the_drop_is_disabled_with_a_notice_and_does_not_fail_the_run()
    {
        var f = new Fixture("INVALID");

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]) with { InvalidTriggers = [Trigger] });

        Assert.False(failed);
        Assert.Equal([Create, DisableText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        var notice = Assert.Single(notices);
        Assert.Contains("SCOTT.TRG_BAD 은 다시 만들기 전부터 컴파일 오류로 INVALID 였습니다. 그대로 다시 만들고 꺼 두었습니다", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("다시 만든 열 구조와 맞지 않아", notice, StringComparison.Ordinal);
    }

    /// <summary>지우기 전부터 INVALID 였어도 끄지 못하면 DML 이 막히므로 결과는 실패다.</summary>
    [Fact]
    public async Task Trigger_that_was_invalid_before_but_cannot_be_disabled_still_fails()
    {
        var f = new Fixture("INVALID");
        f.Execution.Responder = call => call.Statement.Text == DisableText
            ? new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("TBR-1", "boom") }
            : new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]) with { InvalidTriggers = [Trigger] });

        Assert.True(failed);
        Assert.Contains("끄지도 못했습니다", Assert.Single(notices), StringComparison.Ordinal);
    }

    /// <summary>지우기 전부터 INVALID 였지만 다시 만든 표에서는 컴파일되면(VALID) 켜진 채 두고 알리지 않는다.</summary>
    [Fact]
    public async Task Trigger_that_was_invalid_before_but_compiles_after_the_recreate_stays_enabled()
    {
        var f = new Fixture("VALID");

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]) with { InvalidTriggers = [Trigger] });

        Assert.False(failed);
        Assert.Empty(notices);
        Assert.Equal([Create], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
    }

    [Fact]
    public async Task Valid_trigger_is_left_alone()
    {
        var f = new Fixture("VALID");

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]));

        Assert.False(failed);
        Assert.Empty(notices);
        Assert.Equal([Create], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        Assert.Single(f.Bulk.Queries);
    }

    [Fact]
    public async Task Status_text_is_compared_without_case_or_padding()
    {
        var f = new Fixture(" invalid ");

        var (failed, _) = await f.RestoreAsync(Kept([Create], [Trigger]));

        Assert.True(failed);
        Assert.Equal(DisableText, f.Execution.Calls[^1].Statement.Text);
    }

    [Fact]
    public async Task Trigger_that_was_disabled_before_is_not_checked_because_it_is_disabled_again_anyway()
    {
        // 받아 둔 목록: 생성 문장 뒤에 같은 트리거의 DISABLE 문장이 따른다(TiberoRecreateStep.CaptureAsync).
        var f = new Fixture("INVALID");

        var (failed, notices) = await f.RestoreAsync(Kept([Create, DisableText], [Trigger, Trigger]));

        Assert.False(failed);
        Assert.Empty(notices);
        Assert.Equal([Create, DisableText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        Assert.Empty(f.Bulk.Queries);
    }

    [Fact]
    public async Task Only_the_enabled_trigger_among_several_is_queried_and_disabled()
    {
        const string otherCreate = "CREATE OR REPLACE TRIGGER \"SCOTT\".\"TRG_OK\" BEFORE UPDATE ON \"SCOTT\".\"EMP\" FOR EACH ROW BEGIN NULL; END;";
        var other = new DisabledObject(DisabledKind.Trigger, Owner, Table, "TRG_OK");
        var f = new Fixture("VALID", "INVALID");

        var (failed, notices) = await f.RestoreAsync(Kept([otherCreate, Create], [other, Trigger]));

        Assert.True(failed);
        Assert.Equal([otherCreate, Create, DisableText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        Assert.Equal(["TRG_OK", "TRG_BAD"], f.Bulk.Queries.Select(q => q.Binds["NAME"].Value).ToList());
        Assert.Contains("SCOTT.TRG_BAD", Assert.Single(notices), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_status_is_reported_and_nothing_is_disabled()
    {
        var f = new Fixture(); // 행이 없다(트리거를 못 찾음)

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]));

        Assert.True(failed); // 막힌 DML 을 못 알아챌 수 있으니 조용히 지나가지 않는다
        Assert.Equal([Create], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        Assert.Contains("컴파일 상태를 확인하지 못했습니다", Assert.Single(notices), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_trigger_that_cannot_be_disabled_says_so()
    {
        var f = new Fixture("INVALID");
        f.Execution.Responder = call => call.Statement.Text == DisableText
            ? new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("TBR-1", "boom") }
            : new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]));

        Assert.True(failed);
        Assert.Contains("끄지도 못했습니다", Assert.Single(notices), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_that_fails_for_another_reason_is_reported_without_a_status_query()
    {
        var f = new Fixture("INVALID");
        f.Execution.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("TBR-2", "denied") };

        var (failed, notices) = await f.RestoreAsync(Kept([Create], [Trigger]));

        Assert.True(failed);
        Assert.Contains("트리거를 되살리지 못했습니다", Assert.Single(notices), StringComparison.Ordinal);
        Assert.Empty(f.Bulk.Queries);
    }

    private static PreservedDdl Kept(string[] triggers, DisabledObject[] objects) =>
        new(triggers, [], []) { TriggerObjects = objects };

    private sealed class Fixture
    {
        public Fixture(params string[] statuses)
        {
            Target = new FakeSession(TestData.Profile() with { Dbms = Dbms.Tibero }, TestData.Server("6") with { Dbms = Dbms.Tibero, SessionUser = Owner });
            Bulk = new FakeStatusBulk(statuses);
            Execution = new FakeExecutionService();
            Runner = new TiberoCopyRunner(Bulk, Execution, new NoRunLog());
        }

        public FakeSession Target { get; }

        public FakeStatusBulk Bulk { get; }

        public FakeExecutionService Execution { get; }

        private TiberoCopyRunner Runner { get; }

        public Task<(bool Failed, IReadOnlyList<string> Notices)> RestoreAsync(PreservedDdl kept) =>
            Runner.RestoreTriggersForTestAsync(Target, Owner, Table, kept);
    }

    /// <summary>상태 조회에 미리 정한 상태를 차례로 돌려준다(없으면 행 없음). 받은 조회를 기록한다.</summary>
    private sealed class FakeStatusBulk(string[] statuses) : IBulkExecutionService
    {
        private int _next;

        public List<(ISession Session, string Sql, IReadOnlyDictionary<string, BindValue> Binds)> Queries { get; } = [];

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Queries.Add((s, query.Text, binds));
            await Task.CompletedTask;
            if (_next < statuses.Length)
            {
                yield return [statuses[_next++]];
            }
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }
}
