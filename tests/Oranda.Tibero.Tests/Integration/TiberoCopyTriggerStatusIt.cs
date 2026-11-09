using System.Runtime.ExceptionServices;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 다시 만들기가 되살린 트리거가 컴파일 오류로 INVALID 로 남는 경우를 Tibero 실행기 경로(<c>CopyRunner.RestoreTriggersAsync</c>)가 알아채고 끄는지 실제 서버로 잰다.
/// Tibero 는 없는 열을 가리키는 트리거 CREATE 를 오류 없이 받아 INVALID+ENABLED 로 남기므로(TB4 #79) Oracle 처럼 오류(ORA-24344)로 알 수 없다 —
/// 실행기가 만든 뒤 <c>ALL_OBJECTS.STATUS</c> 를 읽어 INVALID 이면 <c>ALTER TRIGGER … DISABLE</c> 한다. 같은 계정 복사는 막혀 있어 복사 전체를 돌릴 수 없으므로
/// 실행기의 트리거 되살리기 단계만 시험용 통로(<c>RestoreTriggersForTestAsync</c>)로 돈다(실제 실행기 코드·실제 실행 서비스·실제 상태 조회).
/// 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만, <c>ORT_</c> 이름만). 표를 <c>DROP … CASCADE CONSTRAINTS PURGE</c> 로 지워 트리거도 함께 없앤다
/// (<c>PURGE RECYCLEBIN</c> 금지, 자기 휴지통 항목만 BIN 이름으로 <c>PURGE TABLE</c>). 접속 값(계정·DB·호스트)은 단언 메시지·출력에 넣지 않는다 —
/// 안내 글에는 계정이 들어가므로 글 자체는 출력하지 않고 참·거짓과 건수만 본다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCopyTriggerStatusIt(ITestOutputHelper output)
{
    private static readonly string Table = TiberoItSessions.ObjectName("TS_T");
    private static readonly string BadTrigger = TiberoItSessions.ObjectName("TS_TRG_BAD");
    private static readonly string OkTrigger = TiberoItSessions.ObjectName("TS_TRG_OK");
    private static readonly string OffTrigger = TiberoItSessions.ObjectName("TS_TRG_OFF");

    [Fact]
    public async Task Runner_finds_a_trigger_that_compiled_with_errors_by_its_status_and_disables_it()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();
        await CleanupAsync(session);

        Exception? failure = null;
        try
        {
            await BodyAsync(it, session, owner);
        }
#pragma warning disable CA1031 // 본문 실패를 잡아 두고 정리한 뒤 그대로 다시 던진다(정리가 본문 실패를 가리지 않게).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        await CleanupAsync(session);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'") == 0, "ORT_ cleanup failed: the test table is left");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{Table}'") == 0, "ORT_ cleanup failed: the test table is left in the recycle bin");
    }

    private async Task BodyAsync(TiberoItSessions it, TiberoSession session, string owner)
    {
        await ExecAsync(session, $"CREATE TABLE {Table} (ID NUMBER PRIMARY KEY, A NUMBER)");

        // 받아 둔 DDL 은 TiberoRecreateStep.CaptureAsync 가 만드는 모양 그대로: 생성 문장 [+ 원래 꺼져 있던 트리거의 DISABLE]. 대상 = 없는 열을 가리키는 트리거.
        string Create(string name, string body) =>
            $"CREATE OR REPLACE TRIGGER {Qualified(owner, name)} BEFORE INSERT ON {Qualified(owner, Table)} FOR EACH ROW\nBEGIN\n  {body}\nEND;";
        var bad = new DisabledObject(DisabledKind.Trigger, owner, Table, BadTrigger);
        var ok = new DisabledObject(DisabledKind.Trigger, owner, Table, OkTrigger);
        var off = new DisabledObject(DisabledKind.Trigger, owner, Table, OffTrigger);
        var kept = new PreservedDdl(
            [
                Create(OkTrigger, ":NEW.A := 1;"),
                Create(BadTrigger, ":NEW.ORT_NO_SUCH_COLUMN := 1;"),
                Create(OffTrigger, ":NEW.A := 2;"),
                $"ALTER TRIGGER {Qualified(owner, OffTrigger)} DISABLE",
            ], [], [])
        {
            TriggerObjects = [ok, bad, off, off],
        };

        var policy = new AllowAll();
        var bulk = new TiberoBulkExecutionService(policy, new NoQuestions(), it.Events);
        var execution = new TiberoExecutionService(policy, new NoQuestions(), it.Events, null, dbmsOutput: false);
        var runner = new TiberoCopyRunner(bulk, execution, new NoRunLog());

        var (failed, notices) = await runner.RestoreTriggersForTestAsync(session, owner, Table, kept);

        // 1. 실행기 판정: 컴파일 오류 트리거 하나만 알아채고(안내 하나), 결과는 실패다.
        Assert.True(failed, "the runner did not mark the restore as failed for the trigger with a compile error");
        Assert.True(notices.Count == 1, "expected exactly one notice (for the invalid trigger), got " + notices.Count);
        Assert.True(notices[0].Contains(BadTrigger, StringComparison.Ordinal), "the notice does not name the invalid trigger");
        Assert.True(notices[0].Contains("INVALID", StringComparison.Ordinal), "the notice does not say INVALID");
        Assert.True(notices[0].Contains("꺼 두었으니", StringComparison.Ordinal), "the notice does not say the trigger was disabled");
        Assert.False(notices[0].Contains("ORA-", StringComparison.Ordinal) || notices[0].Contains("TBR-", StringComparison.Ordinal), "the notice carries an error number");

        // 2. 서버 상태: 컴파일 오류 트리거는 꺼졌다. 정상 트리거는 켜진 채 VALID, 원래 꺼져 있던 트리거는 꺼진 채다.
        var badObject = await TextAsync(session, $"SELECT status FROM user_objects WHERE object_name = '{BadTrigger}' AND object_type = 'TRIGGER'");
        output.WriteLine("compile-error trigger object status after the runner: " + badObject);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE trigger_name = '{BadTrigger}' AND status = 'DISABLED'") == 1,
            "the compile-error trigger is not DISABLED");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE trigger_name = '{OkTrigger}' AND status = 'ENABLED'") == 1,
            "the valid trigger is not ENABLED");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{OkTrigger}' AND object_type = 'TRIGGER' AND status = 'VALID'") == 1,
            "the valid trigger is not VALID");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE trigger_name = '{OffTrigger}' AND status = 'DISABLED'") == 1,
            "the trigger that was disabled before is not DISABLED");

        // 3. 이 표의 DML 이 막히지 않는다(켜진 채 INVALID 로 두면 막히는 것이 위험의 내용이다).
        await ExecAsync(session, $"INSERT INTO {Table} (ID) VALUES (1)");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {Table} WHERE ID = 1 AND A = 1") == 1, "the insert did not go through the valid trigger");
        await ExecAsync(session, "ROLLBACK");
    }

    private static string Qualified(string owner, string name) => SqlIdentifier.QuoteAlways(owner) + "." + SqlIdentifier.QuoteAlways(name);

    /// <summary>미커밋 변경을 되돌리고 시험 표를 지운 뒤(트리거도 함께 없어진다) 이 시험 표의 휴지통 항목만 BIN 이름으로 비운다. 오류는 무시한다.</summary>
    private static async Task CleanupAsync(TiberoSession session)
    {
        await IgnoreAsync(session, "ROLLBACK");
        await IgnoreAsync(session, $"DROP TABLE \"{Table}\" CASCADE CONSTRAINTS PURGE");
        TiberoSessionQuery.Table items;
        try
        {
            items = await TiberoSessionQuery.RunAsync(session, "SELECT object_name FROM user_recyclebin WHERE original_name = :t",
                c => TiberoSessionQuery.Bind(c, "t", Table));
        }
#pragma warning disable CA1031 // 정리 단계: 오류는 무시한다.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        foreach (var row in items.Rows)
        {
            if (TiberoSessionQuery.Str(row[0]) is { Length: > 0 } binName)
            {
                await IgnoreAsync(session, "PURGE TABLE " + SqlIdentifier.QuoteAlways(binName));
            }
        }
    }

    private static async Task<long> CountAsync(TiberoSession session, string sql)
    {
        try
        {
            return TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0;
        }
#pragma warning disable CA1031 // 시험 실패로 바꿔 던진다(메시지에서 접속 계정을 뺀다).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new Xunit.Sdk.XunitException("count query failed: " + ex.GetType().Name);
        }
    }

    /// <summary>값 하나를 글자로(실패하면 형식 이름). 출력에만 쓴다 — 계정 값이 나오는 질의에는 쓰지 않는다.</summary>
    private static async Task<string> TextAsync(TiberoSession session, string sql)
    {
        try
        {
            return TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? "(none)";
        }
#pragma warning disable CA1031 // 출력용: 실패를 글로 바꾼다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "unreadable " + ex.GetType().Name;
        }
    }

    private static async Task IgnoreAsync(TiberoSession session, string sql)
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

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 예외 메시지에는 접속 계정이 실릴 수 있다: 형식 이름과 서버 오류 번호만 담는다.
            var code = TiberoErrorClassifier.ToDbError(ex, null)?.Code;
            throw new Xunit.Sdk.XunitException("statement failed: " + ex.GetType().Name + (code is null ? string.Empty : " " + code));
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }

    /// <summary>실행기의 트리거 되살리기 단계는 실행 기록을 쓰지 않는다(시험용 통로는 기록 없이 돈다).</summary>
    private sealed class NoRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }
}
