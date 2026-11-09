using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Monitoring;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// P3 실측(읽기 전용): Tibero Lock 모니터 조회(잠금 목록·보유자 영향·DDL 잠금·대기 바인드·방치 트랜잭션·데드락·잠긴 행)를 실서버로 확인한다.
/// 잠금을 만들지 않는다(두 세션이 잠그는 쓰기 시험은 따로). 시험 계정은 일반 계정일 수 있어, 능력(<c>CanQueryPerformanceViews</c>·<c>CanQueryDictionary</c>)이
/// 꺼져 있으면 빈 결과를, 켜져 있으면 쿼리가 돈 것을 확인한다. 측정값은 <c>[Ln] …</c> 줄로 남긴다(접속 정보는 싣지 않는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoLockMonitorIt(ITestOutputHelper output)
{
    // L1: 기본 필터로 잠금 목록이 돈다. Tibero 모드 번호 +1 변환은 SQL 에서 하므로 보유·요청 모드는 Oracle 의 0..6 안.
    [Fact]
    public async Task L1_locks_list_runs_and_modes_are_in_oracle_range()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        LogCapabilities("L1", monitor);

        var snapshot = await service.GetLocksAsync(monitor, new LockFilter(), CancellationToken.None);
        Assert.NotNull(snapshot.WaitTree);
        output.WriteLine($"[L1] lock rows = {snapshot.Rows.Count}, types = {string.Join(",", snapshot.Rows.Select(r => r.LockType).Distinct(StringComparer.Ordinal))}, tree roots = {snapshot.WaitTree.Roots.Count}");
        foreach (var r in snapshot.Rows.Take(5))
        {
            output.WriteLine($"[L1]   sid {r.Session.Sid} {r.LockType} id1 {r.Id1} id2 {r.Id2} held {r.HeldMode} req {r.RequestedMode} ctime {r.CtimeSeconds} block {r.IsBlocking} obj {Unowned(r.ObjectName)} status {r.Status ?? "-"}");
        }

        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            Assert.Empty(snapshot.Rows);
            Assert.Empty(snapshot.WaitTree.Roots);
        }
        else
        {
            foreach (var r in snapshot.Rows)
            {
                Assert.InRange(r.HeldMode, 0, 6);
                Assert.InRange(r.RequestedMode, 0, 6);
                Assert.False(string.IsNullOrEmpty(r.LockType), "잠금 종류가 비었다");
            }
        }

        Assert.False(monitor.CommandGate.IsHeld);
    }

    // L2: 보유자 영향 순위. 아무도 기다리지 않으면 빈 목록이 보통이지만 비어 있음을 단정하지 않는다(다른 사용자가 있을 수 있다).
    [Fact]
    public async Task L2_blocker_impact_runs()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        LogCapabilities("L2", monitor);

        var impact = await service.GetBlockerImpactAsync(monitor, CancellationToken.None);
        output.WriteLine($"[L2] blocker impact = {impact.Count}");
        foreach (var b in impact.Take(5))
        {
            output.WriteLine($"[L2]   holder sid {b.Holder.Sid} waiters {b.WaiterCount} max {b.MaxWaitSeconds}s total {b.TotalWaitSeconds}s status {b.Status ?? "-"}");
        }

        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            Assert.Empty(impact);
        }
        else
        {
            foreach (var b in impact)
            {
                Assert.True(b.WaiterCount >= 1);
                Assert.True(b.MaxWaitSeconds >= 0 && b.TotalWaitSeconds >= b.MaxWaitSeconds);
            }
        }

        Assert.False(monitor.CommandGate.IsHeld);
    }

    // L3: DDL 잠금은 딕셔너리 능력이 없으면 빈 목록(보내지 않음), 있으면 쿼리가 돈다.
    [Fact]
    public async Task L3_ddl_locks_empty_without_dictionary_else_runs()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        LogCapabilities("L3", monitor);

        var ddl = await service.GetDdlLocksAsync(monitor, CancellationToken.None);
        output.WriteLine($"[L3] ddl locks = {ddl.Count}");
        foreach (var d in ddl.Take(5))
        {
            output.WriteLine($"[L3]   sid {d.Session.Sid} {d.Owner}.{d.Name} ({d.Type}) held {d.ModeHeld ?? "-"} req {d.ModeRequested ?? "-"}");
        }

        var caps = monitor.Server.Capabilities;
        if (!caps.CanQueryDictionary || !caps.CanQueryPerformanceViews)
        {
            Assert.Empty(ddl);
        }
        else
        {
            foreach (var d in ddl)
            {
                Assert.False(string.IsNullOrEmpty(d.Name));
            }
        }

        Assert.False(monitor.CommandGate.IsHeld);
    }

    // L4: 바인드 있는 문장을 작업 세션이 돌린 뒤 그 커서·세션의 캡처 바인드를 읽는다. Tibero 는 캡처가 꺼져 있거나 비어 있을 수 있어 비어 있음을 단정하지 않는다.
    [Fact]
    public async Task L4_waiting_binds_by_cursor_and_session_run()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);
        LogCapabilities("L4", monitor);
        var key = await WorkKeyAsync(work);

        var marker = "ORT_P3_" + Guid.NewGuid().ToString("N");
        await TiberoSessionQuery.RunAsync(work, $"SELECT /* {marker} */ COUNT(*) FROM dual WHERE 1 = :b1", cmd => TiberoSessionQuery.Bind(cmd, "b1", 1));

        // 찾는 문장이 아닌 모니터 쪽 질의가 v$sql 에 같이 잡히지 않게 마커는 바인드로 넘긴다.
        var found = await TiberoSessionQuery.RunAsync(monitor,
            "SELECT sql_id, child_number FROM v$sql WHERE sql_text LIKE :pat AND sql_text NOT LIKE '%v$sql%' ORDER BY child_number",
            cmd => TiberoSessionQuery.Bind(cmd, "pat", $"%{marker}%"));
        string? sqlId = null;
        int? child = null;
        if (found.Rows.Count > 0)
        {
            sqlId = TiberoSessionQuery.Str(found.Rows[0][0]);
            child = (int?)TiberoSessionQuery.Long(found.Rows[0][1]);
        }
        else
        {
            // 주석이 잘리는 등 마커로 못 찾으면 작업 세션의 직전 SQL 로 대신한다.
            var prev = await TiberoSessionQuery.ScalarAsync(monitor, "SELECT prev_sql_id FROM v$session WHERE sid = :sid AND serial# = :serial",
                cmd =>
                {
                    TiberoSessionQuery.Bind(cmd, "sid", key.Sid);
                    TiberoSessionQuery.Bind(cmd, "serial", key.Serial);
                });
            sqlId = TiberoSessionQuery.Str(prev);
        }

        output.WriteLine($"[L4] marker found in v$sql = {found.Rows.Count > 0}, sql_id = {sqlId ?? "-"}, child = {child?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        Assert.False(string.IsNullOrEmpty(sqlId), "L4: 방금 실행한 문장의 sql_id 를 찾지 못했다(v$sql·prev_sql_id 측정 문제이며 바인드 조회 문제가 아니다)");

        var byCursor = await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Cursor = new CursorRef(sqlId!, null, key.InstanceId) }, CancellationToken.None);
        LogBinds("L4 cursor", byCursor);
        var byCursorChild = await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Cursor = new CursorRef(sqlId!, child, key.InstanceId) }, CancellationToken.None);
        LogBinds("L4 cursor+child", byCursorChild);
        var bySession = await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Session = key }, CancellationToken.None);
        LogBinds("L4 session", bySession);

        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            Assert.Empty(byCursor);
            Assert.Empty(bySession);
        }

        Assert.Empty(await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget(), CancellationToken.None)); // 대상이 없으면 보내지 않는다
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // L5: 방치 트랜잭션. 작업 세션은 트랜잭션을 열지 않았으므로 목록에 없다.
    [Fact]
    public async Task L5_idle_transactions_runs_and_work_session_is_not_listed()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);
        LogCapabilities("L5", monitor);
        var key = await WorkKeyAsync(work);
        await TiberoSessionQuery.RunAsync(work, "SELECT COUNT(*) FROM all_tables");

        var idle = await service.GetIdleTransactionsAsync(monitor, 0, CancellationToken.None);
        output.WriteLine($"[L5] idle transactions = {idle.Count}");
        foreach (var t in idle.Take(5))
        {
            output.WriteLine($"[L5]   sid {t.Session.Sid} idle {t.IdleSeconds}s undo {t.UndoBlocks?.ToString(CultureInfo.InvariantCulture) ?? "-"} blocked {t.BlockedCount} locks {t.LockCount}");
        }

        Assert.False(idle.Any(t => t.Session.Sid == key.Sid && t.Session.Serial == key.Serial), "방치 트랜잭션에 작업 세션이 있다"); // 컬렉션 단정은 실패 때 행(계정 이름)을 찍는다
        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            Assert.Empty(idle);
        }

        Assert.False(monitor.CommandGate.IsHeld);
    }

    // L6: 데드락 기록은 Tibero 에 원본이 없어 빈 목록, 잠긴 행은 대기 중이 아니면 null.
    [Fact]
    public async Task L6_deadlocks_empty_and_locked_row_null_for_idle_session()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);
        LogCapabilities("L6", monitor);
        var key = await WorkKeyAsync(work);

        var deadlocks = await service.GetRecentDeadlocksAsync(monitor, 10, CancellationToken.None);
        output.WriteLine($"[L6] deadlocks = {deadlocks.Count}");
        Assert.Empty(deadlocks);

        var locked = await service.GetLockedRowAsync(monitor, key, CancellationToken.None);
        output.WriteLine($"[L6] locked row = {(locked is null ? "null" : "있음")}");
        Assert.Null(locked);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    private void LogCapabilities(string tag, TiberoSession monitor) =>
        output.WriteLine($"[{tag}] capabilities: performance views = {monitor.Server.Capabilities.CanQueryPerformanceViews}, dictionary = {monitor.Server.Capabilities.CanQueryDictionary}");

    private void LogBinds(string tag, IReadOnlyList<WaitingBind> binds)
    {
        var first = binds.Count > 0 ? binds[0] : null;
        output.WriteLine($"[{tag}] binds = {binds.Count}" + (first is null ? string.Empty : $", first = {first.Name} pos {first.Position} type {first.DataType ?? "-"} value {first.Value ?? "-"}"));
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
