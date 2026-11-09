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
/// P4 실측(읽기 전용): Tibero 모니터 서비스의 Top SQL·SQL 상세(전문·바인드 캡처)를 실서버로 확인한다. 카탈로그 수준 시험은 <see cref="TiberoTopSqlCatalogIt"/>(M0).
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 측정값은 시험 출력에 <c>[Pn] …</c> 줄로 남긴다(접속 정보·스키마 이름은 싣지 않는다).
/// 표식(<c>/* ORT_P4_… */</c>)이 든 SELECT 를 작업 세션으로 돌려 V$SQL 에서 그 문장만 찾는다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTopSqlIt(ITestOutputHelper output)
{
    // P1: 정렬 기준 여섯 가지가 모두 돌고, 해당 지표에 대해 비증가 순서다(NULL 로 바뀐 값은 0 이 되어 끝에 온다).
    [Theory]
    [InlineData(TopSqlOrder.ElapsedTime)]
    [InlineData(TopSqlOrder.CpuTime)]
    [InlineData(TopSqlOrder.BufferGets)]
    [InlineData(TopSqlOrder.DiskReads)]
    [InlineData(TopSqlOrder.Executions)]
    [InlineData(TopSqlOrder.ElapsedPerExecution)]
    public async Task P1_top_sql_runs_for_every_order_and_is_sorted_non_increasing(TopSqlOrder order)
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);

        var rows = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { OrderBy = order, Top = 50 }, CancellationToken.None);
        output.WriteLine($"[P1] {order}: {rows.Count} rows");
        Assert.NotEmpty(rows); // 공유 풀이 비지 않았다
        Assert.True(rows.Count <= 50, "Top=50 인데 " + rows.Count + " 행");

        if (order == TopSqlOrder.ElapsedPerExecution)
        {
            // 서버는 elapsed / GREATEST(executions, 1) 로 정렬한다. 실행 횟수가 NULL(→0)인 행은 서버에서는 값 없음이라 끝에 가므로 비교에서 뺀다.
            var comparable = rows.Where(r => r.Executions > 0).ToArray();
            output.WriteLine($"[P1] {order}: comparable rows = {comparable.Length} (executions > 0)");
            AssertNonIncreasing(comparable, r => r.ElapsedSeconds / r.Executions, order);
        }
        else
        {
            AssertNonIncreasing(rows, Metric(order), order);
        }

        foreach (var r in rows)
        {
            Assert.False(string.IsNullOrEmpty(r.Cursor.SqlId));
            Assert.NotNull(r.Cursor.InstanceId);
            Assert.True(r.Executions >= 0 && r.ElapsedSeconds >= 0 && r.CpuSeconds >= 0 && r.BufferGets >= 0 && r.DiskReads >= 0);
        }
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // P2: 표식 SELECT 를 돌리면 Top SQL 에서 그 문장만 찾아진다(자식 여러 개일 수 있다).
    [Fact]
    public async Task P2_marker_statement_is_found_by_text_with_its_counters_and_schema()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);

        var marker = NewMarker("ORT_P4_");
        await RunMarkerAsync(work, marker, 3);

        var rows = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = marker, Top = 10 }, CancellationToken.None);
        output.WriteLine($"[P2] rows = {rows.Count}, children = {string.Join(",", rows.Select(r => r.Cursor.ChildNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"))}");
        Assert.NotEmpty(rows);
        foreach (var r in rows)
        {
            Assert.Contains(marker, r.SqlTextPreview, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Single(rows.Select(r => r.Cursor.SqlId).Distinct(StringComparer.Ordinal)); // 같은 문장 = 같은 sql_id

        var totalExecutions = rows.Sum(r => r.Executions);
        output.WriteLine($"[P2] executions = {totalExecutions}, sql_id = {rows[0].Cursor.SqlId}, instance = {rows[0].Cursor.InstanceId}, module = {rows[0].Module ?? "-"}");
        Assert.True(totalExecutions >= 1, "실행 횟수가 1 보다 작다: " + totalExecutions);
        foreach (var r in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Cursor.SqlId));
            Assert.NotNull(r.Cursor.InstanceId);
            Assert.NotNull(r.ParsingSchema);
            Assert.True(TiberoItSessions.Same(it.Env.User, r.ParsingSchema, ignoreCase: true), "파싱 스키마가 작업 세션 계정과 다르다");
        }
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // P3: 조건 — 큰 최소 실행 횟수는 빈 결과, 파싱 스키마는 그 스키마 행만, 최근 1시간은 방금 돌린 표식을 포함한다.
    [Fact]
    public async Task P3_filters_by_executions_schema_and_recent_activity()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);

        var marker = NewMarker("ORT_P4_");
        await RunMarkerAsync(work, marker, 2);

        var none = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { MinExecutions = long.MaxValue / 2 }, CancellationToken.None);
        output.WriteLine($"[P3] min_executions huge: {none.Count} rows");
        Assert.Empty(none);

        var bySchema = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { ParsingSchema = it.Env.User, Top = 50 }, CancellationToken.None);
        output.WriteLine($"[P3] test schema: {bySchema.Count} rows");
        Assert.NotEmpty(bySchema); // 방금 표식을 돌렸다
        foreach (var r in bySchema)
        {
            Assert.True(TiberoItSessions.Same(it.Env.User, r.ParsingSchema, ignoreCase: true), "다른 스키마 행이 섞였다");
        }

        var recent = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = marker, ActiveWithin = TimeSpan.FromHours(1), Top = 10 }, CancellationToken.None);
        output.WriteLine($"[P3] marker within 1h: {recent.Count} rows, last active = {(recent.Count > 0 ? recent[0].LastActiveTime : null)?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}");
        Assert.NotEmpty(recent);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // P4: SQL 상세 — 전문에 표식이 들어 있고, 바인드 문장은 바인드 캡처를 읽는다(캡처는 드물 수 있어 개수는 단정하지 않는다).
    [Fact]
    public async Task P4_sql_detail_returns_full_text_and_reads_bind_capture()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var service = NewService(it);

        var marker = NewMarker("ORT_P4_");
        await RunMarkerAsync(work, marker, 2);
        var row = (await service.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = marker, Top = 10 }, CancellationToken.None)) is { Count: > 0 } found ? found[0] : null;
        Assert.NotNull(row);

        var detail = await service.GetSqlDetailAsync(monitor, row.Cursor, CancellationToken.None);
        output.WriteLine($"[P4] plain: full text {detail.FullText.Length} chars, binds = {detail.Binds.Count}");
        Assert.Contains(marker, detail.FullText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(row.Cursor, detail.Cursor);

        // 바인드 문장: 같은 값으로 두 번.
        var bindMarker = NewMarker("ORT_P4B_");
        var bindSql = $"SELECT /* {bindMarker} */ COUNT(*) FROM dual WHERE 1 = :b1";
        for (var i = 0; i < 2; i++)
        {
            await TiberoSessionQuery.RunAsync(work, bindSql, cmd => TiberoSessionQuery.Bind(cmd, "b1", 1));
        }

        var bound = (await service.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = bindMarker, Top = 10 }, CancellationToken.None)) is { Count: > 0 } boundRows ? boundRows[0] : null;
        Assert.NotNull(bound);
        var boundDetail = await service.GetSqlDetailAsync(monitor, bound.Cursor, CancellationToken.None);
        output.WriteLine($"[P4] bound: full text {boundDetail.FullText.Length} chars, binds = {boundDetail.Binds.Count}");
        Assert.Contains(bindMarker, boundDetail.FullText, StringComparison.OrdinalIgnoreCase);
        foreach (var b in boundDetail.Binds)
        {
            output.WriteLine($"[P4]   bind {b.Name} #{b.Position} {b.DataType ?? "-"} = {b.Value ?? "-"} (captured {b.CapturedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-"})");
        }

        foreach (var b in boundDetail.Binds)

        {

            Assert.True(b.Position >= 0);

        }
        Assert.Equal(boundDetail.Binds.Count, boundDetail.Binds.Select(b => b.Position).Distinct().Count()); // 위치당 하나(자식 여럿이면 최신 것)
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // P5: 공유 풀에 없는 sql_id 는 InvalidOperationException.
    [Fact]
    public async Task P5_unknown_sql_id_throws_invalid_operation()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetSqlDetailAsync(monitor, new CursorRef("0000000000000", 0, null), CancellationToken.None));
        output.WriteLine("[P5] message = " + ex.Message);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    private static string NewMarker(string prefix) => prefix + Guid.NewGuid().ToString("N");

    /// <summary>표식 SELECT 를 times 번 돌린다(실행 횟수가 쌓이게).</summary>
    private static async Task RunMarkerAsync(TiberoSession work, string marker, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await TiberoSessionQuery.RunAsync(work, $"SELECT /* {marker} */ COUNT(*) FROM dual");
        }
    }

    private static Func<TopSqlRow, double> Metric(TopSqlOrder order) => order switch
    {
        TopSqlOrder.ElapsedTime => r => r.ElapsedSeconds,
        TopSqlOrder.CpuTime => r => r.CpuSeconds,
        TopSqlOrder.BufferGets => r => r.BufferGets,
        TopSqlOrder.DiskReads => r => r.DiskReads,
        TopSqlOrder.Executions => r => r.Executions,
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    private static void AssertNonIncreasing(IReadOnlyList<TopSqlRow> rows, Func<TopSqlRow, double> metric, TopSqlOrder order)
    {
        for (var i = 1; i < rows.Count; i++)
        {
            var prev = metric(rows[i - 1]);
            var cur = metric(rows[i]);
            // 부동소수 나눗셈 오차는 허용한다.
            Assert.True(cur <= prev * (1 + 1e-9) + 1e-12, $"{order}: {i - 1} 번째({prev.ToString("R", CultureInfo.InvariantCulture)}) 보다 {i} 번째({cur.ToString("R", CultureInfo.InvariantCulture)}) 가 크다");
        }
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
