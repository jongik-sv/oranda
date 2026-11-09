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
/// P2 실측(읽기 전용): Tibero 모니터 서비스의 세션 목록·델타·상세·블로킹·미지원 기능을 실서버로 확인한다. Kill 은 쓰기 시험이라 여기에 없다.
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 측정값은 시험 출력에 <c>[Mn] …</c> 줄로 남긴다(접속 정보는 싣지 않는다).
/// 작업 세션의 SID/SERIAL# 은 <c>v$mystat</c> 로 얻는다(Tibero 는 <c>SYS_CONTEXT('USERENV','SID')</c> 를 못 쓴다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoMonitorServiceIt(ITestOutputHelper output)
{
    private const string WorkProbeSql = "SELECT COUNT(*) FROM all_tables";

    private static readonly string[] ExpectedSections = ["트랜잭션", "잠금 객체", "통계", "I/O", "긴 작업", "열린 커서"];

    // M1: 목록에 작업 세션이 있고 상태는 ACTIVE/INACTIVE 만. 두 번째 조회는 델타를 채운다.
    [Fact]
    public async Task M1_sessions_list_contains_the_work_session_with_normalized_status_and_deltas()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);
        var key = await WorkKeyAsync(work);
        await TiberoSessionQuery.RunAsync(work, WorkProbeSql);
        output.WriteLine($"[M1] work = inst {key.InstanceId}, sid {key.Sid}, serial {key.Serial}");

        var first = await service.GetSessionsAsync(monitor, new SessionFilter(), CancellationToken.None);
        output.WriteLine($"[M1] sessions = {first.Count}, statuses = {string.Join(",", first.Select(r => r.Status).Distinct(StringComparer.Ordinal))}");
        var mines = first.Where(r => r.Key == key).ToList(); // 컬렉션 단정은 실패 때 행(계정 이름)을 찍는다
        Assert.True(mines.Count == 1, $"목록에 작업 세션이 한 번이어야 한다: {mines.Count}");
        var mine = mines[0];
        output.WriteLine($"[M1] work row: status {mine.Status}, event {mine.Event ?? "-"}, class {mine.WaitClass ?? "-"}, last_call_et {mine.LastCallElapsedSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        foreach (var r in first)
        {
            Assert.True(r.Status is "ACTIVE" or "INACTIVE", "상태가 ACTIVE/INACTIVE 가 아니다: " + r.Status);
        }
        Assert.Empty(mine.Deltas); // 첫 조회는 비교할 이전 값이 없다

        await TiberoSessionQuery.RunAsync(work, WorkProbeSql); // 델타가 0 이 아니도록 일을 시킨다
        var second = await service.GetSessionsAsync(monitor, new SessionFilter(), CancellationToken.None);
        var agains = second.Where(r => r.Key == key).ToList();
        Assert.True(agains.Count == 1, $"두 번째 목록에 작업 세션이 한 번이어야 한다: {agains.Count}");
        var again = agains[0];
        output.WriteLine($"[M1] deltas = {string.Join(", ", again.Deltas.Select(d => $"{d.Key}:{d.Value}"))}");
        Assert.NotEmpty(again.Deltas);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // M2: 상세 구획·대기 이력 미지원 이유·오류 꼴 구획 없음.
    [Fact]
    public async Task M2_session_detail_has_sections_and_marks_wait_history_unavailable()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);
        var key = await WorkKeyAsync(work);
        await TiberoSessionQuery.RunAsync(work, WorkProbeSql);

        var detail = await service.GetSessionDetailAsync(monitor, key, CancellationToken.None);
        var titles = detail.Sections.Select(s => s.Title).ToArray();
        output.WriteLine($"[M2] sections = {string.Join(" | ", titles)}");
        foreach (var s in detail.Sections)
        {
            output.WriteLine($"[M2]   {s.Title}: {s.Rows.Count} rows, columns = {string.Join(",", s.Columns)}");
        }

        output.WriteLine($"[M2] current sql = {Preview(detail.CurrentSqlText)}");
        output.WriteLine($"[M2] previous sql = {Preview(detail.PreviousSqlText)}");
        output.WriteLine($"[M2] sql_id = {detail.Session.SqlId ?? "-"}, prev_sql_id = {detail.Session.PrevSqlId ?? "-"}");

        foreach (var title in ExpectedSections)
        {
            Assert.Contains(title, titles);
        }

        var history = Assert.Single(detail.Sections, s => s.Title == "대기 이력");
        Assert.Empty(history.Rows);
        Assert.Equal(TiberoMonitorService.WaitHistoryUnavailable, detail.UnavailableSections["대기 이력"]);

        // 「실제 계획」(DISPLAY_CURSOR)은 커서가 밀려나면 오류 구획이 될 수 있어 뺀다(기록만).
        var errors = detail.Sections.Where(s => s.Columns.Count == 1 && s.Columns[0] == "오류" && s.Title != "실제 계획").ToArray();
        foreach (var e in errors)
        {
            output.WriteLine($"[M2] 오류 구획 {e.Title}: {(e.Rows.Count > 0 ? e.Rows[0][0] : null)}");
        }

        Assert.Empty(errors);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // M3: 블로킹 트리·내가 막는 수. 이 시험의 작업 세션은 아무도 막지 않는다.
    [Fact]
    public async Task M3_blocking_tree_runs_and_my_blocking_is_zero()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);
        var key = await WorkKeyAsync(work);

        var tree = await service.GetBlockingTreeAsync(monitor, CancellationToken.None);
        output.WriteLine($"[M3] blocking roots = {tree.Roots.Count}");

        var mine = await service.GetMyBlockingAsync(monitor, key.Sid, CancellationToken.None);
        output.WriteLine($"[M3] my blocking: count {mine.BlockedCount}, tx age {mine.TransactionAgeSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-"}, idle blocker minutes {mine.MaxIdleBlockerMinutes}");
        Assert.Equal(0, mine.BlockedCount);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // M4: 트레이스는 미지원, 데드락 기록은 빈 목록, 잠긴 행은 null(행 위치를 알 수 있는 뷰가 없다).
    [Fact]
    public async Task M4_unsupported_features_answer_without_throwing_server_errors()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);
        var key = await WorkKeyAsync(work);

        output.WriteLine($"[M4] capabilities: performance views = {monitor.Server.Capabilities.CanQueryPerformanceViews}"); // false 면 아래 두 조회는 앞에서 돌아간다
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SetTraceAsync(monitor, key, true, new TraceOptions(), CancellationToken.None));

        var deadlocks = await service.GetRecentDeadlocksAsync(monitor, 10, CancellationToken.None);
        output.WriteLine($"[M4] deadlocks = {deadlocks.Count}");
        Assert.Empty(deadlocks);

        var locked = await service.GetLockedRowAsync(monitor, key, CancellationToken.None);
        output.WriteLine($"[M4] locked row = {(locked is null ? "null" : "있음")}");
        Assert.Null(locked);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    private static string Preview(string? text) =>
        text is null ? "null" : text.Length <= 80 ? text.ReplaceLineEndings(" ") : text[..80].ReplaceLineEndings(" ") + "…";

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
