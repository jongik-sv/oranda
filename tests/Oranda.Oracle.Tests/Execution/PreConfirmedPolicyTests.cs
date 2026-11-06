using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 소유: 데이터 복사 Task 6. 미리 받은 확인(ExecutionOptions.PreConfirmed)은 Confirm 만 건너뛰고 Deny 는 넘지 못한다.
// 두 실행 클래스(OracleExecutionService·OracleBulkExecutionService)가 같은 PolicyGate 를 쓰는지 함께 본다.
// DB 없음: 가짜 세션이라 정책을 지난 뒤 드라이버 단계에서 ArgumentException 이 나는 것은 기대한 동작이다.
public class PreConfirmedPolicyTests
{
    private const string Delete = "DELETE FROM T";

    private static SqlStatement Unfiltered(string text = Delete) =>
        new(text, default, StatementKind.Dml) { Verb = "DELETE", IsUnfilteredDml = true };

    private static ExecutionOptions Options(IReadOnlySet<string>? preConfirmed) =>
        new() { Source = HistorySource.DataCopy, PreConfirmed = preConfirmed };

    [Fact]
    public async Task Preconfirmed_unfiltered_delete_skips_interaction()
    {
        var rig = new Rig(readOnly: false);
        var options = Options(new HashSet<string>(StringComparer.Ordinal) { Delete });

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Exec.ExecuteAsync(rig.Session, Unfiltered(), new Dictionary<string, BindValue>(),
            options, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Bulk.ExecuteArrayAsync(rig.Session, Unfiltered(), [], [[]], options,
            CancellationToken.None));
        // 바인드 없는 문장은 행 목록이 비어도 드라이버 단계까지 간다(조용히 건너뛰지 않는다).
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Bulk.ExecuteArrayAsync(rig.Session, Unfiltered(), [], [], options,
            CancellationToken.None));

        Assert.Empty(rig.Ui.Confirmations);
        Assert.All(rig.Bus.Decisions, d => Assert.Equal(PolicyRule.UnfilteredDml, d.Rule)); // 확인 규칙은 그대로 걸렸다
        Assert.Equal(3, rig.Bus.Decisions.Count);
    }

    [Fact]
    public async Task Preconfirmed_does_not_override_deny()
    {
        var rig = new Rig(readOnly: true);
        var options = Options(new HashSet<string>(StringComparer.Ordinal) { Delete });

        var r = await rig.Exec.ExecuteAsync(rig.Session, Unfiltered(), new Dictionary<string, BindValue>(), options, CancellationToken.None);
        var bulk = await rig.Bulk.ExecuteArrayAsync(rig.Session, Unfiltered(), [], [[]], options, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Equal(ExecutionStatus.Denied, bulk.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, bulk.Policy!.Rule);
        Assert.Equal(0, bulk.RowsAffected);
        Assert.Empty(rig.Ui.Confirmations);
        // 호출마다 StatementExecuted 하나(거부도 이력에 남는다). 대량 경로의 것은 두 번째다.
        Assert.Equal(2, rig.Bus.Executed.Count);
        var executed = rig.Bus.Executed[1];
        Assert.Equal(ExecutionStatus.Denied, executed.Result.Status);
        Assert.Same(options, executed.Options);
        Assert.Equal(Delete, executed.Statement.Text);
    }

    [Fact]
    public async Task Confirm_without_preconfirmed_still_asks()
    {
        var rig = new Rig(readOnly: false);
        var options = Options(new HashSet<string>(StringComparer.Ordinal) { "DELETE FROM OTHER" });

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Exec.ExecuteAsync(rig.Session, Unfiltered(), new Dictionary<string, BindValue>(),
            options, CancellationToken.None));

        Assert.Single(rig.Ui.Confirmations);
    }

    [Fact]
    public async Task Preconfirmed_text_is_compared_ordinally_even_in_a_case_insensitive_set()
    {
        var rig = new Rig(readOnly: false) { Answer = false };
        var options = Options(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "delete from t" });

        var bulk = await rig.Bulk.ExecuteArrayAsync(rig.Session, Unfiltered(), [], [[]], options, CancellationToken.None);

        Assert.Single(rig.Ui.Confirmations); // 대소문자만 다른 글은 미리 받은 확인이 아니다
        Assert.Equal(ExecutionStatus.Cancelled, bulk.Status);
    }

    [Theory]
    [InlineData(Delete, 0)] // 치환 뒤 글 = 미리 받은 확인
    [InlineData("DELETE FROM &X", 1)] // 치환 전 글은 미리 받은 확인이 아니다
    public async Task Preconfirmed_is_matched_after_substitution(string confirmedText, int expectedAsks)
    {
        var rig = new Rig(readOnly: false) { Substitutions = new Dictionary<string, string> { ["X"] = "T" } };
        var options = Options(new HashSet<string>(StringComparer.Ordinal) { confirmedText });
        var stmt = Unfiltered("DELETE FROM &X") with { SubstitutionNames = ["X"] };

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Exec.ExecuteAsync(rig.Session, stmt, new Dictionary<string, BindValue>(),
            options, CancellationToken.None));

        Assert.Equal(expectedAsks, rig.Ui.Confirmations.Count);
    }

    [Fact]
    public async Task Readonly_profile_denies_array_binds_even_when_policy_allows()
    {
        var rig = new Rig(readOnly: true);
        // 분류가 잘못 붙은 쓰기: 정책은 Query 로 보고 허용하지만 바인드 배열은 보내지 않는다.
        var mislabeled = new SqlStatement("INSERT INTO T VALUES (:C1)", default, StatementKind.Query) { Verb = "SELECT", BindNames = ["C1"] };

        var r = await rig.Bulk.ExecuteArrayAsync(rig.Session, mislabeled, [new BulkColumn("C1", "NUMBER")], [[1m]], Options(null),
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, r.Policy!.Rule);
        Assert.Equal(PolicyDecisionKind.Allow, Assert.Single(rig.Bus.Decisions).Kind);
        Assert.Single(rig.Bus.Executed);
    }

    [Theory]
    [InlineData(StatementKind.Ddl, false)]
    [InlineData(StatementKind.SqlPlus, false)]
    [InlineData(StatementKind.Ddl, true)] // 불확실 분류는 EffectiveKind 가 DML 이지만 붙은 분류가 DDL 이면 거부
    public async Task Array_path_rejects_ddl_and_sqlplus_before_policy(StatementKind kind, bool uncertain)
    {
        var rig = new Rig(readOnly: false);
        var stmt = new SqlStatement("DROP TABLE T", default, kind) { Verb = "DROP", DangerousTarget = "T", IsClassificationUncertain = uncertain };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => rig.Bulk.ExecuteArrayAsync(rig.Session, stmt, [], [], Options(null),
            CancellationToken.None));

        Assert.Contains(kind.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Bus.Decisions);
        Assert.Empty(rig.Ui.Confirmations);
    }

    [Theory]
    [InlineData("SET TRANSACTION READ ONLY", StatementKind.TransactionControl, true)]
    [InlineData("  COMMIT", StatementKind.TransactionControl, true)]
    [InlineData("ROLLBACK TO SAVEPOINT A", StatementKind.TransactionControl, true)]
    [InlineData("DELETE FROM T", StatementKind.TransactionControl, false)] // 분류가 잘못 붙은 쓰기
    [InlineData("SET ROLE ALL", StatementKind.TransactionControl, false)]
    public async Task Readonly_profile_allows_only_transaction_control_text(string text, StatementKind kind, bool passes)
    {
        var rig = new Rig(readOnly: true);
        var stmt = new SqlStatement(text, default, kind) { Verb = text.Trim().Split(' ')[0] };

        if (passes)
        {
            // 거부 단계를 지나 드라이버 단계(가짜 세션이라 ArgumentException)까지 간다.
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Bulk.ExecuteArrayAsync(rig.Session, stmt, [], [], Options(null),
                CancellationToken.None));
        }
        else
        {
            var r = await rig.Bulk.ExecuteArrayAsync(rig.Session, stmt, [], [], Options(null), CancellationToken.None);
            Assert.Equal(ExecutionStatus.Denied, r.Status);
            Assert.Equal(PolicyRule.ReadOnlyProfile, r.Policy!.Rule);
        }
    }

    [Theory]
    [InlineData("DELETE FROM T", false)] // 분류만 Query 로 잘못 붙었다
    [InlineData("INSERT INTO T SELECT * FROM S", false)]
    [InlineData("/* 주석 */ -- 줄 주석\n (WITH X AS (SELECT 1 FROM DUAL) SELECT * FROM X)", true)]
    [InlineData("select * from t", true)]
    public async Task Stream_rows_checks_leading_keyword_not_only_kind(string text, bool passes)
    {
        var rig = new Rig(readOnly: false);
        var stmt = new SqlStatement(text, default, StatementKind.Query) { Verb = "SELECT" };

        async Task Drain()
        {
            await foreach (var _ in rig.Bulk.StreamRowsAsync(rig.Session, stmt, new Dictionary<string, BindValue>(), Options(null),
                               CancellationToken.None))
            {
            }
        }

        if (passes)
        {
            await Assert.ThrowsAsync<ArgumentException>(Drain); // 정책을 지나 세션 단계까지 간다
            Assert.Single(rig.Bus.Decisions);
        }
        else
        {
            await Assert.ThrowsAsync<BulkDeniedException>(Drain);
            Assert.Empty(rig.Bus.Decisions);
        }
    }

    [Fact]
    public async Task Stream_rows_rejects_non_query_before_policy()
    {
        var rig = new Rig(readOnly: false);
        var options = Options(new HashSet<string>(StringComparer.Ordinal) { Delete });

        var ex = await Assert.ThrowsAsync<BulkDeniedException>(async () =>
        {
            await foreach (var _ in rig.Bulk.StreamRowsAsync(rig.Session, Unfiltered(), new Dictionary<string, BindValue>(), options,
                               CancellationToken.None))
            {
            }
        });

        Assert.Equal(PolicyDecisionKind.Deny, ex.Decision.Kind);
        Assert.Empty(rig.Ui.Confirmations);
        Assert.Empty(rig.Bus.Decisions);
    }

    private sealed class Rig
    {
        public Rig(bool readOnly)
        {
            Session = new FakeSession(new ConnectionProfile("p", "scott", "db.example.com:1521/ORCL", ReadOnly: readOnly));
            Ui = new RecordingInteraction(this);
            Exec = new OracleExecutionService(new ExecutionPolicy(), Ui, Bus, null, pageSize: 500);
            Bulk = new OracleBulkExecutionService(new ExecutionPolicy(), Ui, Bus);
        }

        public bool Answer { get; init; } = true;

        public IReadOnlyDictionary<string, string>? Substitutions { get; init; }

        public FakeSession Session { get; }

        public RecordingInteraction Ui { get; }

        public RecordingBus Bus { get; } = new();

        public OracleExecutionService Exec { get; }

        public OracleBulkExecutionService Bulk { get; }
    }

    private sealed class RecordingInteraction(Rig rig) : IExecutionInteraction
    {
        public List<PolicyDecision> Confirmations { get; } = [];

        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct)
        {
            Confirmations.Add(decision);
            return Task.FromResult(rig.Answer);
        }

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult(rig.Substitutions);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }

    private sealed class RecordingBus : IEventBus
    {
        public List<PolicyDecision> Decisions { get; } = [];

        public List<StatementExecuted> Executed { get; } = [];

        public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
            where TEvent : OrandaEvent
            => throw new NotSupportedException();

        public void Publish<TEvent>(TEvent e)
            where TEvent : OrandaEvent
        {
            switch (e)
            {
                case PolicyDecisionMade d:
                    Decisions.Add(d.Decision);
                    break;
                case StatementExecuted x:
                    Executed.Add(x);
                    break;
            }
        }
    }

    private sealed class FakeSession(ConnectionProfile profile) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = profile;

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), DbUniqueName: "FREE", SessionUser: "SCOTT");

        public SessionPurpose Purpose => SessionPurpose.Dedicated;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
