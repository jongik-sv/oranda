using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Monitoring;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// P7 실측(쓰기, <c>ORANDA_IT_TIBERO_WRITE=1</c>): 같은 계정의 두 세션이 한 행을 놓고 막고 막히는 상태를 만들어 Lock 모니터를 확인한다.
/// 세션 A 가 UPDATE(미커밋)로 행 잠금을 쥐고, 세션 B 가 같은 행을 UPDATE 하며 기다린다. 모니터 세션이 블로킹 트리·잠금 목록·보유자 영향·내가 막는 수·
/// 대기 바인드·방치 트랜잭션·세션 상세를 읽는다. 끝나면 A 롤백 → B 완료 → B 롤백 → 표 삭제(finally, 실패해도).
/// 모든 대기에 제한 시간이 있다(트리 15초, B 완료 15초, 정리 15초). 측정값은 <c>[W1] …</c> 줄로 남긴다(접속 정보는 싣지 않는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoLockWriteIt(ITestOutputHelper output)
{
    private static readonly TimeSpan TreeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DoneTimeout = TimeSpan.FromSeconds(15);

    // W1: A 가 행 잠금을 쥐고 B 가 기다리는 동안 모든 Lock 모니터 조회가 그 모습을 보여 준다.
    [Fact]
    public async Task W1_blocked_update_shows_in_every_lock_monitor_read()
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

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var a = await it.OpenAsync();
        var b = await it.OpenAsync();
        var service = NewService(it);
        var table = TiberoItSessions.ObjectName("LKW_BLOCK");
        using var cts = new CancellationTokenSource();
        Task? waiting = null;
        var created = false;
        try
        {
            var capabilities = monitor.Server.Capabilities;
            output.WriteLine($"[W1] capabilities: performance views = {capabilities.CanQueryPerformanceViews}, dictionary = {capabilities.CanQueryDictionary}");

            await DropIfExistsAsync(a, table);
            await ExecAsync(a, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v VARCHAR2(20))");
            created = true;
            await ExecAsync(a, $"INSERT INTO {table} (id, v) VALUES (1, 'init')");
            await a.CommitAsync(CancellationToken.None);

            var keyA = await WorkKeyAsync(a);
            var keyB = await WorkKeyAsync(b);
            output.WriteLine($"[W1] A = sid {keyA.Sid} serial {keyA.Serial}, B = sid {keyB.Sid} serial {keyB.Serial}");

            // A 가 행 잠금을 쥔다(커밋 없음). B 는 같은 행을 갱신하며 막힌다(배경 작업).
            var update = $"UPDATE {table} SET v = :v WHERE id = 1";
            await ExecAsync(a, update, "from-a");
            waiting = Task.Run(() => ExecAsync(b, update, "from-b", cts.Token), CancellationToken.None);

            var tree = await WaitForWaiterAsync(service, monitor, waiting, keyA, keyB);
            var root = FindRoot(tree, keyA)!;
            var nodesB = root.Waiters.Where(w => SameKey(w.Session, keyB)).ToList();
            Assert.True(nodesB.Count == 1, $"A 아래 B 가 한 번이어야 한다: {nodesB.Count}");
            var nodeB = nodesB[0];
            output.WriteLine($"[W1] tree roots = {tree.Roots.Count}, A waiters = {root.Waiters.Count}, B event = {nodeB.Event ?? "-"}, wait = {nodeB.SecondsInWait?.ToString(CultureInfo.InvariantCulture) ?? "-"}s, object = {Unowned(nodeB.LockedObject)}");

            // 잠금 목록(성능 뷰 능력이 없으면 빈 목록이 계약).
            var snapshot = await service.GetLocksAsync(monitor, new LockFilter { WaitingOrBlockingOnly = true }, CancellationToken.None);
            output.WriteLine($"[W1] lock rows = {snapshot.Rows.Count}, snapshot tree roots = {snapshot.WaitTree.Roots.Count}");
            foreach (var r in snapshot.Rows.Take(10))
            {
                output.WriteLine($"[W1]   sid {r.Session.Sid} {r.LockType} held {r.HeldMode} req {r.RequestedMode} ctime {r.CtimeSeconds} block {r.IsBlocking} obj {Unowned(r.ObjectName)} status {r.Status ?? "-"}");
            }

            // 모드 번호 원값(lock.list 의 보정 근거: 보유 LMODE + 1, 요청은 원값). 서버 값만 찍는다.
            var raw = await TiberoSessionQuery.RunAsync(monitor,
                "SELECT 'L' AS src, l.sess_id, l.type, l.lmode, l.requested FROM v$lock l WHERE l.sess_id IN (:a, :b) AND l.type NOT IN ('WLOCK_SESS', 'WLOCK_USER') " +
                "UNION ALL SELECT 'W', w.wait_sid, w.type, w.hold_sid, w.mode_req FROM v$waiter_session w WHERE w.wait_sid = :b",
                cmd =>
                {
                    TiberoSessionQuery.Bind(cmd, "a", keyA.Sid);
                    TiberoSessionQuery.Bind(cmd, "b", keyB.Sid);
                });
            foreach (var row in raw.Rows)
            {
                output.WriteLine($"[W1] raw {string.Join(" | ", row.Select(v => TiberoSessionQuery.Str(v) ?? "NULL"))}");
            }

            var impact = await service.GetBlockerImpactAsync(monitor, CancellationToken.None);
            output.WriteLine($"[W1] blocker impact = {impact.Count}");
            foreach (var i in impact.Take(5))
            {
                output.WriteLine($"[W1]   holder sid {i.Holder.Sid} waiters {i.WaiterCount} max {i.MaxWaitSeconds}s total {i.TotalWaitSeconds}s status {i.Status ?? "-"}");
            }

            var idle = await service.GetIdleTransactionsAsync(monitor, 0, CancellationToken.None);
            output.WriteLine($"[W1] idle transactions = {idle.Count}");

            if (capabilities.CanQueryPerformanceViews)
            {
                // 컬렉션 단정은 실패 때 record(계정·기계 이름)를 찍으므로 Assert.True + 숫자만 쓴다.
                foreach (var r in snapshot.Rows)
                {
                    Assert.True(r.HeldMode is >= 0 and <= 6 && r.RequestedMode is >= 0 and <= 6,
                        $"모드가 Oracle 체계(0~6) 밖이다: sid {r.Session.Sid} {r.LockType} held {r.HeldMode} req {r.RequestedMode}");
                }

                Assert.True(snapshot.Rows.Any(r => SameKey(r.Session, keyA) && r.HeldMode > 0), "A 의 보유 잠금 행이 없다");
                Assert.True(snapshot.Rows.Any(r => SameKey(r.Session, keyB) && r.RequestedMode > 0), "B 의 요청 잠금 행이 없다");
                var snapshotRoot = FindRoot(snapshot.WaitTree, keyA);
                Assert.True(snapshotRoot is not null, "잠금 스냅숏 트리에 A 뿌리가 없다");
                Assert.True(snapshotRoot.Waiters.Any(w => SameKey(w.Session, keyB)), "잠금 스냅숏 트리에서 A 아래 B 가 없다");

                var mine = impact.Where(i => SameKey(i.Holder, keyA)).ToList();
                Assert.True(mine.Count == 1, $"보유자 영향에 A 가 한 번이어야 한다: {mine.Count}");
                Assert.True(mine[0].WaiterCount >= 1, "A 의 대기자 수가 1 이상이어야 한다");

                // A 는 SQL 을 끝내고 트랜잭션만 쥔 INACTIVE 세션이다.
                Assert.True(idle.Any(t => SameKey(t.Session, keyA)), "방치 트랜잭션에 A 가 없다");
            }
            else
            {
                Assert.Empty(snapshot.Rows);
                Assert.Empty(impact);
                Assert.Empty(idle);
            }

            var blocking = await service.GetMyBlockingAsync(monitor, keyA.Sid, CancellationToken.None);
            output.WriteLine($"[W1] A blocks = {blocking.BlockedCount}, tx age {blocking.TransactionAgeSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-"}s");
            Assert.True(blocking.BlockedCount >= 1, "A 가 막는 세션 수가 1 이상이어야 한다");

            // 대기 바인드: 캡처가 드물 수 있어 비어 있음을 단정하지 않는다.
            var binds = await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Session = keyB }, CancellationToken.None);
            output.WriteLine($"[W1] waiting binds = {binds.Count}" + (binds.Count > 0 ? $", first = {binds[0].Name} pos {binds[0].Position} type {binds[0].DataType ?? "-"} value {binds[0].Value ?? "-"}" : string.Empty));

            var detail = await service.GetSessionDetailAsync(monitor, keyB, CancellationToken.None);
            foreach (var title in new[] { "트랜잭션", "잠금 객체" })
            {
                var section = detail.Sections.FirstOrDefault(s => s.Title == title);
                output.WriteLine($"[W1] detail {title}: {(section is null ? "구획 없음" : section.Rows.Count.ToString(CultureInfo.InvariantCulture) + " rows")}");
            }

            Assert.False(monitor.CommandGate.IsHeld);

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

    /// <summary>모니터 트리에 A 아래 B 가 보일 때까지 250ms 간격으로 읽는다(최대 15초). B 가 먼저 끝나거나 실패하면 곧바로 실패.</summary>
    private static async Task<BlockingTree> WaitForWaiterAsync(TiberoMonitorService service, TiberoSession monitor, Task waiting, SessionKey keyA, SessionKey keyB)
    {
        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TreeTimeout + TimeSpan.FromSeconds(5));
        while (true)
        {
            if (waiting.IsCompleted)
            {
                await waiting; // 오류면 던진다
                Assert.Fail("B 의 UPDATE 가 기다리지 않고 끝났다(A 의 잠금이 안 걸렸다)");
            }

            var tree = await service.GetBlockingTreeAsync(monitor, cts.Token);
            if (FindRoot(tree, keyA) is { } root && root.Waiters.Any(w => SameKey(w.Session, keyB)))
            {
                return tree;
            }

            if (clock.Elapsed > TreeTimeout)
            {
                Assert.Fail($"{TreeTimeout.TotalSeconds:0}초 안에 블로킹 트리에 A 아래 B 가 나타나지 않았다(루트 {tree.Roots.Count}개)");
            }

            await Task.Delay(PollInterval, cts.Token);
        }
    }

    /// <summary>정리: A 롤백(B 를 풀어 준다) → B 완료 대기 → B 롤백 → 표 삭제. 단계마다 실패를 삼키고 남기며, 어느 대기도 제한 시간이 있다.</summary>
    private async Task CleanupAsync(TiberoSession a, TiberoSession b, string table, bool created, Task? waiting, CancellationTokenSource cts)
    {
        var finished = true;
        await TryAsync("A 롤백", () => a.RollbackAsync(CancellationToken.None));
        if (waiting is not null)
        {
            finished = await CompletesAsync(waiting, DoneTimeout);
            if (!finished)
            {
                output.WriteLine("[W1] 정리: B 가 A 롤백 뒤에도 끝나지 않았다. 취소를 요청하고 표 삭제를 건너뛴다(세션은 하니스가 닫는다).");
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
                await TryAsync("표 삭제", () => WithTimeoutAsync(DropIfExistsAsync(a, table), DoneTimeout));
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
            output.WriteLine($"[W1] 정리 실패({what}): {ex.GetType().Name}");
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

    private static BlockingNode? FindRoot(BlockingTree tree, SessionKey key) =>
        tree.Roots.FirstOrDefault(n => SameKey(n.Session, key));

    /// <summary>인스턴스 번호는 같은 서버라 비교에서 뺀다(카탈로그 쪽 표기와 어긋날 수 있다). SID + SERIAL# 으로 본다.</summary>
    private static bool SameKey(SessionKey x, SessionKey y) => x.Sid == y.Sid && x.Serial == y.Serial;

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
            await ExecAsync(session, $"DROP TABLE {table}");
        }
    }

    /// <summary>「소유자.이름」 에서 이름만(소유자 = 시험 계정이라 로그에 싣지 않는다).</summary>
    private static string Unowned(string? name) => name is null ? "-" : name[(name.LastIndexOf('.') + 1)..];

    /// <summary>작업 세션의 키(V$SESSION 원값 SERIAL#, 음수일 수 있다). 인스턴스 번호 = 카탈로그 SQL 과 같은 USERENV INSTANCE.</summary>
    private static async Task<SessionKey> WorkKeyAsync(TiberoSession work)
    {
        var table = await TiberoSessionQuery.RunAsync(work, "SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')), sid, serial# FROM v$session WHERE sid = (SELECT m.sid FROM v$mystat m WHERE ROWNUM = 1)");
        var row = Assert.Single(table.Rows);
        return new SessionKey((int)(TiberoSessionQuery.Long(row[0]) ?? 0), TiberoSessionQuery.Long(row[1]) ?? 0, TiberoSessionQuery.Long(row[2]) ?? 0);
    }

    private static TiberoMonitorService NewService(TiberoItSessions it) =>
        new(new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false));

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
}
