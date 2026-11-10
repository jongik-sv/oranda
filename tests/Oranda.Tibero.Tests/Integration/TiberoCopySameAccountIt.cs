using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 복사 하위 형식(T5 TI1b)이 같은 계정·같은 표 복사를 실제 세션의 DbKey 로 막는지 잰다. 두 번째 Tibero 계정이 없어 다른 계정으로의 복사 IT 는 없다
/// (복사 키는 xdb-copy XC0 에서 사내용으로 켰다 — 같은 계정 차단은 이 시험이 계속 확인한다). 읽기 시험이다: 접속 계정이 볼 수 있는 표 하나를 골라 같은 계정·같은 표로 복사하는 작업을 세우고
/// 사전 점검과 실행기가 모두 거부하는지, 그동안 정책에 문장을 한 번도 묻지 않았는지(아무것도 보내지 않았는지) 본다. 정책은 모두 거부하고, 처리는 대상을 지우지 않는 AppendOnly 다.
/// 접속 계정에 일반 표가 하나도 없을 때만 쓰기 시험 설정(<c>ORANDA_IT_TIBERO_WRITE=1</c>)에서 <c>ORT_TI1B_T</c> 를 만들었다 <c>DROP … PURGE</c> 한다.
/// 접속 값(계정·DB·호스트)은 단언 메시지에 넣지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCopySameAccountIt
{
    private static readonly string TempTable = TiberoItSessions.ObjectName("TI1B_T");

    [TiberoItFact]
    public async Task Same_account_same_table_copy_is_blocked_by_the_precheck_and_refused_by_the_runner()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var source = await it.OpenAsync(SessionPurpose.Dedicated, it.Profile() with { ReadOnly = true });
        var target = await it.OpenAsync(SessionPurpose.Dedicated);
        var created = false;
        try
        {
            var user = source.Server.SessionUser;
            Assert.False(string.IsNullOrEmpty(user), "the session user of the connection is unknown");
            var table = await PickTableAsync(source);
            if (table is null)
            {
                if (TiberoItSessions.SkipWrite())
                {
                    // 접속 계정에 시험할 표가 없고 쓰기 시험도 꺼져 있다: ORANDA_IT_REQUIRE=1 이면 건너뛰지 않고 실패시킨다.
                    Assert.False(it.Env.RequireMode, "no table of the connected account to test and the write test is off (ORANDA_IT_TIBERO_WRITE=1)");
                    return;
                }

                await ExecAsync(target, $"CREATE TABLE {TempTable} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(20))");
                created = true;
                await ExecAsync(target, $"INSERT INTO {TempTable} VALUES (1, 'a')");
                await ExecAsync(target, "COMMIT");
                table = TempTable;
            }

            await RunAsync(it, source, target, user!, table);
        }
        finally
        {
            if (created)
            {
                await SilentAsync(target, "ROLLBACK");
                await SilentAsync(target, $"DROP TABLE {TempTable} CASCADE CONSTRAINTS PURGE");
            }
        }
    }

    private static async Task RunAsync(TiberoItSessions it, TiberoSession source, TiberoSession target, string owner, string table)
    {
        // 두 세션은 같은 계정이라 DbKey 가 같고, Core 판정이 같은 DB 로 본다(참·거짓만 본다).
        Assert.True(TiberoItSessions.Same(source.DbKey, target.DbKey));
        Assert.True(CopySameDatabase.IsSameDatabase(source.DbKey, source.Server, target.DbKey, target.Server));

        var sessions = new CopySessions(source, target);
        var job = Job(owner, table);
        var period = new ResolvedPeriod(null, null);
        var policy = new DenyAndCount(); // 거부가 깨져 문장을 보내려 하면 정책이 막고 호출 수로 드러난다.
        var bulk = new TiberoBulkExecutionService(policy, new NoQuestions(), it.Events);
        var execution = new TiberoExecutionService(policy, new NoQuestions(), it.Events, null, dbmsOutput: false);

        // 1. 사전 점검: 같은 DB 에서 대상 표가 원본으로 읽는 표라 오류 하나만, 계획 없음.
        var precheck = new TiberoCopyPrecheckService(bulk, new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync), policy);
        var report = await precheck.RunAsync(sessions, job, period, null, CancellationToken.None);
        var item = Assert.Single(report.Items);
        Assert.True(item.Code == "SameSourceTarget" && item.Level == CheckLevel.Error, "precheck: expected one SameSourceTarget error");
        Assert.True(item.Message.Contains("같은 DB", StringComparison.Ordinal), "precheck: the message does not name the same database");
        Assert.True(report.HasErrors && report.Order.Count == 0, "precheck: the blocked report must have errors and no plan");

        // 2. 실행기: 막힌 보고서는 점검 오류로 거부한다.
        var blockedLog = new SentinelRunLog();
        var runner = new TiberoCopyRunner(bulk, execution, blockedLog);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            sessions, job, report, Decisions(), period, null, new CopyRunControl(), CancellationToken.None));
        Assert.True(blocked.Message.Contains("사전 점검", StringComparison.Ordinal), "runner: the blocked report was not refused for its errors");
        Assert.False(blockedLog.Begun, "runner: the run log was started for the blocked report");

        // 3. 실행기: 점검 오류를 지우고 손으로 만든 계획(실제 표 구조)을 넣어도 같은 DB 가드가 따로 거부한다. 실행 기록도 시작하지 않는다.
        var info = await new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync).GetTableAsync(source, new DbObjectRef(owner, table, "TABLE"), CancellationToken.None);
        var first = info.Columns[0].Name;
        var forced = new PrecheckReport([], [new CopyTablePlan(job.Tables[0], info, info, [first], [first])], [], new Dictionary<string, long>());
        var log = new SentinelRunLog();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new TiberoCopyRunner(bulk, execution, log).RunAsync(
            sessions, job, forced, Decisions(), period, null, new CopyRunControl(), CancellationToken.None));
        Assert.True(refused.Message.Contains("원본과 대상이 같은 DB", StringComparison.Ordinal), "runner: the same database guard did not refuse the run");
        Assert.False(log.Begun, "runner: the run log was started before the refusal");

        // 4. 아무 문장도 보내지 않았다(정책에 한 번도 묻지 않았다).
        Assert.True(policy.Calls == 0, "no statement may be sent, but the policy was asked");
    }

    /// <summary>대상 지우기가 없는 처리(AppendOnly)로 같은 계정의 같은 표를 복사하는 작업(거부가 깨져도 원본 행을 지우지 않게).</summary>
    private static CopyJob Job(string owner, string table) => new()
    {
        Id = "ti1b-it", Name = "ti1b-it", Source = new ConnectionRef("a", owner), Target = new ConnectionRef("b", owner),
        SourceOwner = owner, TargetOwner = owner, MaskSalt = "salt",
        Tables = [new TableRule(table, RuleKind.All, TargetAction.AppendOnly)],
    };

    private static CopyDecisions Decisions() =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(), new HashSet<string>(StringComparer.Ordinal), false);

    /// <summary>접속 계정의 일반 표 하나(시험 표 ORT_·임시 표·휴지통 표는 뺀다). 없으면 null.</summary>
    private static async Task<string?> PickTableAsync(TiberoSession session)
    {
        var own = await TiberoSessionQuery.RunAsync(session,
            "SELECT table_name FROM (SELECT table_name FROM user_tables WHERE table_name NOT LIKE 'ORT\\_%' ESCAPE '\\' AND table_name NOT LIKE 'BIN$%' " +
            "AND temporary = 'N' AND table_name NOT IN (SELECT object_name FROM user_recyclebin) ORDER BY num_rows NULLS LAST, table_name) WHERE ROWNUM = 1");
        return own.Rows.Count > 0 ? TiberoSessionQuery.Str(own.Rows[0][0]) : null;
    }

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private static async Task SilentAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
#pragma warning disable CA1031 // 정리 단계: 실패해도 시험 결과를 가리지 않는다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>모든 문장을 거부하고 물어본 횟수를 센다(0 이면 아무 문장도 보내려 하지 않았다).</summary>
    private sealed class DenyAndCount : IExecutionPolicy
    {
        public int Calls { get; private set; }

        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.None, "denied by the test"));
        }
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }

    /// <summary>실행 기록을 시작하면 표시하고 멈추는 가짜(거부가 실행 기록보다 앞서는지 본다).</summary>
    private sealed class SentinelRunLog : ICopyRunLog
    {
        public bool Begun { get; private set; }

        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct)
        {
            Begun = true;
            throw new ReachedBeginException();
        }

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>실행 기록 시작에 닿았다는 표시(거부 예외와 구별한다).</summary>
    private sealed class ReachedBeginException : Exception;
}
