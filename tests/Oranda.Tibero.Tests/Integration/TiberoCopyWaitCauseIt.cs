using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// WH1 실측(쓰기, <c>ORANDA_IT_TIBERO_WRITE=1</c>): 복사 대기 원인 조회(<c>copy.wait_cause</c>, Tibero 판)의 열 이름(V$SESSION·V$EVENT_NAME·V$WAITER_SESSION)을
/// 실제 서버로 잰다. 첫 계정의 세 세션만 쓴다(둘째 계정에는 테이블을 만들지 않는다): 보유자 A 가 행을 UPDATE(미커밋)로 쥐고, 대기자 B 가 같은 행을
/// UPDATE 하며 막히고, 감시 세션 M 이 B 의 SID·인스턴스 이름으로 <c>copy.wait_cause</c> 를 읽는다(복사 세션 대신 B, <c>CopyWaitProbe</c> 와 같은 바인드).
/// 끝나면 A 롤백 → B 완료 → B 롤백 → 테이블 삭제(PURGE, finally). 모든 대기에 제한 시간이 있다.
/// 측정값은 <c>[WH1] …</c> 줄로 남기되 이벤트·대기 종류·대기 초·프로그램만 싣는다(계정·기계 이름은 싣지 않는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCopyWaitCauseIt(ITestOutputHelper output)
{
    private const string QueryId = "copy.wait_cause";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DoneTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [TiberoWriteFact]
    public async Task Wait_cause_reports_the_row_lock_holder_of_a_blocked_session_from_a_monitor_session()
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

        var a = await it.OpenAsync();
        var b = await it.OpenAsync();
        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var table = TiberoItSessions.ObjectName("WH1_WAIT_CAUSE");
        using var cts = new CancellationTokenSource();
        Task? waiting = null;
        var created = false;
        try
        {
            await DropIfExistsAsync(a, table);
            await ExecAsync(a, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v VARCHAR2(20))");
            created = true;
            await ExecAsync(a, $"INSERT INTO {table} (id, v) VALUES (1, 'init')");
            await a.CommitAsync(CancellationToken.None);

            var sidA = a.Server.SessionId;
            var sidB = b.Server.SessionId;
            Assert.True(sidA > 0 && sidB > 0 && monitor.Server.SessionId > 0, "접속 때 읽은 SID 가 없다");
            output.WriteLine($"[WH1] A = sid {sidA}, B = sid {sidB}, M = sid {monitor.Server.SessionId}, B instance name known = {b.Server.InstanceName is not null}");

            // A 가 행 잠금을 쥔다(커밋 없음). B 는 같은 행을 갱신하며 막힌다(배경 작업).
            var update = $"UPDATE {table} SET v = :v WHERE id = 1";
            await ExecAsync(a, update, "from-a");
            waiting = Task.Run(() => ExecAsync(b, update, "from-b", cts.Token), CancellationToken.None);

            var result = await WaitForBlockerAsync(monitor, b, waiting);
            output.WriteLine($"[WH1] columns = {string.Join(", ", result.Columns)}");
            Assert.True(result.Rows.Count == 1, string.Create(CultureInfo.InvariantCulture, $"B 의 대기 원인이 {result.Rows.Count}행이다"));
            Assert.True(result.Columns.Count == 7, string.Create(CultureInfo.InvariantCulture, $"열 {result.Columns.Count}개(기대 7)"));

            var row = result.Rows[0];
            var evt = TiberoSessionQuery.Str(row[0]);
            var waitClass = TiberoSessionQuery.Str(row[1]);
            var seconds = TiberoSessionQuery.Long(row[2]);
            var blocking = TiberoSessionQuery.Long(row[3]);
            var program = TiberoSessionQuery.Str(row[5]);
            output.WriteLine($"[WH1] event = {evt ?? "NULL"}, wait class = {waitClass ?? "NULL"}, seconds in wait = {seconds?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}");
            output.WriteLine($"[WH1] blocking sid = {blocking?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}, program = {program ?? "NULL"}, user present = {row[4] is not null}, machine present = {row[6] is not null}");

            Assert.True(blocking == sidA, string.Create(CultureInfo.InvariantCulture, $"막는 세션이 A 가 아니다: {blocking?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}(A = {sidA})"));
            Assert.True(TiberoItSessions.Same(a.Profile.User, TiberoSessionQuery.Str(row[4]), ignoreCase: true), "막는 세션의 USERNAME 이 접속 계정과 다르다");
            Assert.True(evt is not null, "막힌 B 의 EVENT 가 NULL 이다");
            Assert.True(seconds is >= 0, "대기 초가 음수이거나 NULL 이다");

            // 다른 인스턴스 이름이면 같은 SID 라도 0행(다른 인스턴스의 세션을 잘못 읽지 않는다).
            var otherInstance = await RunAsync(monitor, sidB, "ORT_NO_SUCH_INSTANCE");
            Assert.True(otherInstance.Rows.Count == 0, string.Create(CultureInfo.InvariantCulture, $"없는 인스턴스 이름에 {otherInstance.Rows.Count}행"));

            // 감시 세션 자기 SID 도 한 행(대기 없음·막는 세션 없음일 수 있다).
            var self = await RunAsync(monitor, monitor.Server.SessionId, monitor.Server.InstanceName);
            Assert.True(self.Rows.Count == 1, string.Create(CultureInfo.InvariantCulture, $"감시 세션 자기 SID 가 {self.Rows.Count}행"));
            output.WriteLine($"[WH1] monitor self: event = {TiberoSessionQuery.Str(self.Rows[0][0]) ?? "NULL"}, wait class = {TiberoSessionQuery.Str(self.Rows[0][1]) ?? "NULL"}, blocking = {TiberoSessionQuery.Str(self.Rows[0][3]) ?? "NULL"}");

            // A 롤백 → B 의 UPDATE 가 풀려 끝난다.
            await a.RollbackAsync(CancellationToken.None);
            Assert.True(await CompletesAsync(waiting, DoneTimeout), "A 롤백 뒤 B 의 UPDATE 가 제한 시간 안에 끝나지 않았다");
            await waiting; // B 의 오류가 있으면 여기서 드러난다
        }
        finally
        {
            await CleanupAsync(a, b, table, created, waiting, cts);
        }
    }

    /// <summary>B 의 대기 원인에 막는 세션이 보일 때까지 250ms 간격으로 읽는다(최대 15초). B 가 먼저 끝나거나 실패하면 곧바로 실패. 시한이 지나면 마지막 결과를 돌려준다.</summary>
    private static async Task<TiberoSessionQuery.Table> WaitForBlockerAsync(TiberoSession monitor, TiberoSession waiter, Task waiting)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (waiting.IsCompleted)
            {
                await waiting; // 오류면 던진다
                Assert.Fail("B 의 UPDATE 가 기다리지 않고 끝났다(A 의 잠금이 안 걸렸다)");
            }

            var result = await RunAsync(monitor, waiter.Server.SessionId, waiter.Server.InstanceName);
            if ((result.Rows.Count > 0 && result.Columns.Count > 3 && result.Rows[0][3] is not null) || clock.Elapsed > WaitTimeout)
            {
                return result;
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>감시 세션으로 <c>copy.wait_cause</c> 를 읽는다. 바인드는 <c>CopyWaitProbe</c> 와 같다(SID = 글자, INST = 인스턴스 이름, null = 이 인스턴스).</summary>
    private static async Task<TiberoSessionQuery.Table> RunAsync(TiberoSession monitor, long sid, string? instance)
    {
        using var timeout = new CancellationTokenSource(QueryTimeout);
        return await TiberoSessionQuery.RunAsync(monitor, TiberoCatalogQuery.Sql(monitor, QueryId), command =>
        {
            TiberoSessionQuery.Bind(command, "SID", sid.ToString(CultureInfo.InvariantCulture));
            TiberoSessionQuery.Bind(command, "INST", instance);
        }, ct: timeout.Token);
    }

    /// <summary>정리: A 롤백(B 를 풀어 준다) → B 완료 대기 → B 롤백 → 테이블 삭제. 단계마다 실패를 삼키고 남기며, 어느 대기도 제한 시간이 있다.</summary>
    private async Task CleanupAsync(TiberoSession a, TiberoSession b, string table, bool created, Task? waiting, CancellationTokenSource cts)
    {
        var finished = true;
        await TryAsync("A 롤백", () => a.RollbackAsync(CancellationToken.None));
        if (waiting is not null)
        {
            finished = await CompletesAsync(waiting, DoneTimeout);
            if (!finished)
            {
                output.WriteLine("[WH1] 정리: B 가 A 롤백 뒤에도 끝나지 않았다. 취소를 요청하고 테이블 삭제를 건너뛴다(세션은 하니스가 닫는다).");
                await cts.CancelAsync();
            }
            else
            {
                await TryAsync("B 완료 관찰", () => waiting);
            }
        }

        if (finished)
        {
            await TryAsync("B 롤백", () => b.RollbackAsync(CancellationToken.None));
            if (created)
            {
                await TryAsync("테이블 삭제", () => WithTimeoutAsync(DropIfExistsAsync(a, table), DoneTimeout));
            }
        }
    }

    private async Task TryAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // 정리 단계: 한 단계가 실패해도 나머지를 계속한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[WH1] 정리 실패({what}): {ex.GetType().Name}");
        }
    }

    private static async Task WithTimeoutAsync(Task task, TimeSpan timeout)
    {
        if (!await CompletesAsync(task, timeout))
        {
            throw new TimeoutException("제한 시간 안에 끝나지 않았다.");
        }

        await task;
    }

    /// <summary>작업이 제한 시간 안에 끝나는가(성공·실패 모두 끝난 것으로 본다). 오류는 던지지 않는다.</summary>
    private static async Task<bool> CompletesAsync(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    /// <summary>DDL·DML 하나를 보낸다(문자열 바인드 <c>:v</c> 하나까지). 명령 잠금을 쥐고 스레드 풀에서 돈다. 커밋하지 않는다(세션은 수동 커밋 모드).</summary>
    private static async Task ExecAsync(TiberoSession session, string sql, string? bind = null, CancellationToken ct = default)
    {
        await session.CommandGate.EnterAsync(ct);
        try
        {
            await Task.Run(
                async () =>
                {
                    await using var command = session.CreateCommand(sql);
                    if (bind is not null)
                    {
                        TiberoSessionQuery.Bind(command, "v", bind);
                    }

                    await command.ExecuteNonQueryAsync(ct);
                },
                CancellationToken.None);
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private static async Task DropIfExistsAsync(TiberoSession session, string table)
    {
        var exists = await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{table}'");
        if (TiberoSessionQuery.Long(exists) is > 0)
        {
            await ExecAsync(session, $"DROP TABLE {table} PURGE");
        }
    }
}
