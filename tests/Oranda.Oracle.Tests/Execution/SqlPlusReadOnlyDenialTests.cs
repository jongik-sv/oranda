using System.Collections.Concurrent;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Oracle.SqlPlus;

namespace Oranda.Oracle.Tests.Execution;

// 소유: TSK-07-10. SET SERVEROUTPUT·EXEC 의 안쪽 PL/SQL 블록이 정책 관문에서 거부될 때(조회 전용 Role·읽기 전용 프로필)
// 거부 문구가 내부 「BEGIN」 이 아니라 사용자가 친 명령을 말하고, SET SERVEROUTPUT 상태는 바뀌지 않는다. DB 없이 가짜 엔진 + 실제 정책 관문.
public class SqlPlusReadOnlyDenialTests
{
    private static SqlStatement Stmt(string text, string verb) => new(text, default, StatementKind.SqlPlus) { Verb = verb };

    private static Rig NewRig(bool viewerRole, bool readOnlyProfile) => new(viewerRole, readOnlyProfile);

    [Theory]
    [InlineData("SET SERVEROUTPUT ON", true, false)]
    [InlineData("SET SERVEROUTPUT ON SIZE 100000", true, false)]
    [InlineData("SET SERVEROUTPUT OFF", true, false)]
    [InlineData("SET SERVEROUTPUT ON", false, true)]
    [InlineData("SET SERVEROUTPUT OFF", true, true)]
    public async Task Set_serveroutput_denial_names_the_typed_command_and_keeps_the_state(string text, bool viewerRole, bool readOnlyProfile)
    {
        var rig = NewRig(viewerRole, readOnlyProfile);

        var r = await rig.Processor.ExecuteAsync(rig.Session, Stmt(text, "SET"), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Equal(text, r.Statement.Text); // 결과는 사용자가 친 문장에 붙는다
        var message = r.Policy!.Message;
        Assert.Contains("SET SERVEROUTPUT", message, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", message, StringComparison.Ordinal);
        Assert.False(rig.Processor.IsServerOutputOn(rig.Session)); // 거부되면 상태를 켜지 않는다
        Assert.Empty(rig.Sent); // 서버로 가지 않았다
    }

    [Fact]
    public async Task Denied_set_serveroutput_off_leaves_the_state_that_an_earlier_on_set()
    {
        var rig = NewRig(viewerRole: false, readOnlyProfile: false);
        var on = await rig.Processor.ExecuteAsync(rig.Session, Stmt("SET SERVEROUTPUT ON", "SET"), CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, on.Status);
        Assert.True(rig.Processor.IsServerOutputOn(rig.Session));

        // 같은 세션에서 Role 이 조회 전용으로 바뀌었다(게이트 값은 문장마다 다시 읽는다): OFF 가 거부되고 켜진 채로 남는다.
        rig.Gate.ReadOnlyRole = true;
        var off = await rig.Processor.ExecuteAsync(rig.Session, Stmt("SET SERVEROUTPUT OFF", "SET"), CancellationToken.None);
        Assert.Equal(ExecutionStatus.Denied, off.Status);
        Assert.True(rig.Processor.IsServerOutputOn(rig.Session));
        Assert.Single(rig.Sent); // 처음 ON 하나만 갔다

        // 되돌림: 다른 Role 을 함께 고르면(조회 전용 해제) 같은 명령이 돈다.
        rig.Gate.ReadOnlyRole = false;
        var again = await rig.Processor.ExecuteAsync(rig.Session, Stmt("SET SERVEROUTPUT OFF", "SET"), CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, again.Status);
        Assert.False(rig.Processor.IsServerOutputOn(rig.Session));
    }

    [Fact]
    public async Task Set_serveroutput_runs_normally_when_nothing_blocks_it()
    {
        var rig = NewRig(viewerRole: false, readOnlyProfile: false);

        var on = await rig.Processor.ExecuteAsync(rig.Session, Stmt("SET SERVEROUTPUT ON SIZE 5000", "SET"), CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, on.Status);
        Assert.True(rig.Processor.IsServerOutputOn(rig.Session));
        Assert.Contains("DBMS_OUTPUT.ENABLE(5000)", Assert.Single(rig.Sent).Text, StringComparison.Ordinal);

        var off = await rig.Processor.ExecuteAsync(rig.Session, Stmt("SET SERVEROUTPUT OFF", "SET"), CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, off.Status);
        Assert.False(rig.Processor.IsServerOutputOn(rig.Session));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Exec_denial_names_exec_not_begin(bool viewerRole, bool readOnlyProfile)
    {
        var rig = NewRig(viewerRole, readOnlyProfile);

        var r = await rig.Processor.ExecuteAsync(rig.Session, Stmt("EXEC my_proc", "EXEC"), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Contains("EXEC", r.Policy!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", r.Policy.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public async Task Invalid_size_still_fails_before_anything_is_sent_or_changed()
    {
        var rig = NewRig(viewerRole: false, readOnlyProfile: false);

        var r = await rig.Processor.ExecuteAsync(rig.Session, Stmt("SET SERVEROUTPUT ON SIZE abc", "SET"), CancellationToken.None);

        Assert.NotEqual(ExecutionStatus.Succeeded, r.Status);
        Assert.False(rig.Processor.IsServerOutputOn(rig.Session));
        Assert.Empty(rig.Sent);
    }

    private sealed class Rig
    {
        public Rig(bool viewerRole, bool readOnlyProfile)
        {
            Gate = new Gate { ReadOnlyRole = viewerRole };
            Session = new FakeSession(new ConnectionProfile("p", "scott", "h:1521/s", ReadOnly: readOnlyProfile));
            var engine = new PolicyEngine(new ExecutionPolicy(Gate), Sent);
            Processor = new SqlPlusCommandProcessor(() => engine, () => null!);
        }

        public Gate Gate { get; }

        public FakeSession Session { get; }

        public ConcurrentQueue<SqlStatement> Sent { get; } = new();

        public SqlPlusCommandProcessor Processor { get; }
    }

    /// <summary>OracleExecutionService 2단계처럼 정책 관문을 먼저 보고, 거부가 아니면 「드라이버」(목록)에 넘긴다.</summary>
    private sealed class PolicyEngine(ExecutionPolicy policy, ConcurrentQueue<SqlStatement> sent) : IExecutionService
    {
        public async Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            var d = await policy.EvaluateAsync(new PolicyContext(s.Profile, s.Server, HistorySource.Script), stmt, ct);
            if (d.Kind == PolicyDecisionKind.Deny)
            {
                return new ExecutionResult(stmt, ExecutionStatus.Denied) { Policy = d };
            }

            sent.Enqueue(stmt);
            return new ExecutionResult(stmt, ExecutionStatus.Succeeded);
        }

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class Gate : IFeatureGate
    {
        public bool ReadOnlyRole { get; set; }

        public IReadOnlyCollection<string> PendingRestart => [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsEnabled(string featureId) => true;

        public bool IsCommandEnabled(string commandId, Type handlerType) => true;

        public bool IsPanelEnabled(string panelId, Type viewModelType) => true;

        public bool IsTypeEnabled(Type registeredType) => true;
    }

    private sealed class FakeSession(ConnectionProfile profile) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = profile;

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), DbUniqueName: "FREE", SessionUser: "SCOTT");

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public Oranda.Core.Sessions.SessionState State => Oranda.Core.Sessions.SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
