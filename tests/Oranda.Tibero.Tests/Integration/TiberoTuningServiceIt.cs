using System.Globalization;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Plans;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tuning;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// P5a 실측(읽기 전용): Tibero 튜닝 서비스의 통계·커서·대기·SQL 통계·환경·벤치마크를 실서버로 확인한다. DDL·DML·통계 수집 없음.
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 측정값은 시험 출력에 <c>[Tn] …</c> 줄로 남긴다(접속 정보는 싣지 않는다).
/// 표는 서버에서 통계가 있고 인덱스가 있는 것을 고른다(없으면 그 사실을 기록하고 통계 단정은 건너뛴다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTuningServiceIt(ITestOutputHelper output)
{
    private const string ProbeSql = "SELECT /*+ FULL(t) */ COUNT(*) FROM all_tables t";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(120);

    // T1: 표 통계·열 통계·인덱스 통계·인덱스 열.
    [TiberoItFact]
    public async Task T1_table_column_index_stats_and_index_columns()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        var (owner, table, analyzed) = await PickTableAsync(session);
        if (owner is null || table is null)
        {
            output.WriteLine("[T1] 인덱스가 있는 테이블을 찾지 못해 건너뜀");
            return;
        }

        output.WriteLine($"[T1] table = {owner}.{table} (all_tables.last_analyzed 있음 = {analyzed})");
        IReadOnlyList<CatalogBind> binds = [CatalogBinds.Trimmed("owner", owner), CatalogBinds.Trimmed("table_name", table)];

        var stats = await MeasureAsync(session, "tuning.table_stats", binds, () => service.GetTableStatsAsync(session, owner, table, CancellationToken.None));
        output.WriteLine($"[T1] table_stats = {stats?.ToString() ?? "null"}");
        await LogRawAsync(session, "tuning.table_stats", binds);
        if (analyzed)
        {
            Assert.NotNull(stats);
        }

        var columns = await MeasureAsync(session, "tuning.column_stats", binds, () => service.GetColumnStatsAsync(session, owner, table, CancellationToken.None));
        output.WriteLine($"[T1] column_stats rows = {columns.Count}");
        foreach (var c in columns.Take(20))
        {
            output.WriteLine("[T1]   " + c);
        }

        Assert.NotEmpty(columns);

        var indexes = await MeasureAsync(session, "tuning.index_stats", binds, () => service.GetIndexStatsAsync(session, owner, table, CancellationToken.None));
        output.WriteLine($"[T1] index_stats rows = {indexes.Count}");
        foreach (var i in indexes.Take(20))
        {
            output.WriteLine("[T1]   " + i);
        }

        var indexColumns = await MeasureAsync(session, "tuning.index_columns", binds, () => service.GetIndexColumnsAsync(session, owner, table, CancellationToken.None));
        output.WriteLine($"[T1] index_columns rows = {indexColumns.Count}");
        foreach (var ic in indexColumns.Take(20))
        {
            output.WriteLine($"[T1]   {ic.IndexName}: {string.Join(", ", ic.Columns)}");
        }

        // 고른 표는 인덱스가 있으므로 두 목록 모두 비어 있지 않아야 한다.
        Assert.NotEmpty(indexes);
        Assert.NotEmpty(indexColumns);
        Assert.False(session.CommandGate.IsHeld);
    }

    // T2: 히스토그램 한 열(히스토그램이 있는 열을 사전에서 찾고, 없으면 고른 표의 첫 열).
    [TiberoItFact]
    public async Task T2_histogram_of_one_column()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        string? owner = null, table = null, column = null;
        var fromHistograms = false;
        try
        {
            var found = await TiberoSessionQuery.RunAsync(session,
                "SELECT * FROM (SELECT owner, table_name, column_name FROM all_tab_histograms GROUP BY owner, table_name, column_name HAVING COUNT(*) > 2) WHERE ROWNUM <= 1");
            if (found.Rows.Count > 0)
            {
                owner = TiberoSessionQuery.Str(found.Rows[0][0]);
                table = TiberoSessionQuery.Str(found.Rows[0][1]);
                column = TiberoSessionQuery.Str(found.Rows[0][2]);
                fromHistograms = true;
            }
        }
#pragma warning disable CA1031 // 실측: 히스토그램 뷰 모양이 서버마다 다를 수 있다. 실패도 결과로 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[T2] all_tab_histograms 탐색 실패: {ex.GetType().Name}: {ex.Message}");
        }

        if (owner is null || table is null || column is null)
        {
            var (o, t, _) = await PickTableAsync(session);
            if (o is null || t is null)
            {
                output.WriteLine("[T2] 히스토그램 열도 테이블도 찾지 못해 건너뜀");
                return;
            }

            var first = await TiberoSessionQuery.ScalarAsync(session,
                "SELECT column_name FROM all_tab_columns WHERE owner = :o AND table_name = :t AND column_id = 1",
                cmd =>
                {
                    TiberoSessionQuery.Bind(cmd, "o", o);
                    TiberoSessionQuery.Bind(cmd, "t", t);
                });
            owner = o;
            table = t;
            column = TiberoSessionQuery.Str(first);
            output.WriteLine("[T2] 히스토그램이 있는 열이 없어 첫 열로 대신함");
            if (column is null)
            {
                output.WriteLine("[T2] 첫 열도 찾지 못해 건너뜀");
                return;
            }
        }

        output.WriteLine($"[T2] column = {owner}.{table}.{column}");
        IReadOnlyList<CatalogBind> binds =
            [CatalogBinds.Trimmed("owner", owner), CatalogBinds.Trimmed("table_name", table), CatalogBinds.Trimmed("column_name", column)];
        var buckets = await MeasureAsync(session, "tuning.histogram", binds, () => service.GetHistogramAsync(session, owner, table, column, CancellationToken.None));
        output.WriteLine($"[T2] buckets = {buckets.Count}");
        foreach (var b in buckets.Take(20))
        {
            output.WriteLine("[T2]   " + b);
        }

        await LogRawAsync(session, "tuning.histogram", binds);

        // 사전에서 버킷이 3개 넘는 열을 찾았으면 서비스도 버킷을 돌려줘야 한다(첫 열로 대신한 경우는 히스토그램이 없을 수 있어 기록만 한다).
        if (fromHistograms)
        {
            Assert.NotEmpty(buckets);
        }
    }

    // T3: 방금 실행한 문장의 자식 커서와 자식 계획.
    [TiberoItFact]
    public async Task T3_child_cursors_and_child_plan()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        var sqlId = await RunProbeAsync(session);
        output.WriteLine($"[T3] sql_id = {sqlId}");
        IReadOnlyList<CatalogBind> binds = [CatalogBinds.Trimmed("sql_id", sqlId)];

        var children = await MeasureAsync(session, "tuning.child_cursors", binds, () => service.GetChildCursorsAsync(session, sqlId, CancellationToken.None));
        output.WriteLine($"[T3] child_cursors rows = {children.Count}");
        foreach (var c in children)
        {
            output.WriteLine($"[T3]   {c}");
        }

        await LogRawAsync(session, "tuning.child_cursors", binds);
        Assert.NotEmpty(children);

        var child = children[0].ChildNumber;
        try
        {
            var plan = await service.GetChildPlanAsync(session, new CursorRef(sqlId, child), "TYPICAL", CancellationToken.None);
            output.WriteLine($"[T3] child plan (child {child}): steps = {plan.Steps.Count}, plan_hash = {plan.PlanHashValue?.ToString(CultureInfo.InvariantCulture) ?? "null"}, cursor = {plan.Cursor}");
            foreach (var step in plan.Steps.Take(20))
            {
                output.WriteLine("[T3]   " + step);
            }

            output.WriteLine("[T3] plan text:");
            foreach (var line in plan.Text.Split('\n').Take(30))
            {
                output.WriteLine("[T3]   | " + line.TrimEnd('\r'));
            }

            Assert.NotEmpty(plan.Steps);
        }
#pragma warning disable CA1031 // 실측: V$SQL 자식 번호가 V$SQL_PLAN 자식 번호와 같은지 모른다. 실패 원인을 남기고 다시 던진다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[T3] child plan failed (child {child}): {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    // T4: 대기 표본 — 내 세션(null)과 sql_id 로.
    [TiberoItFact]
    public async Task T4_sample_waits_for_own_session_and_for_a_sql_id()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        var sqlId = await RunProbeAsync(session);

        // 내 세션(null): 내 SID 행이 늘 있어야 한다.
        var own = await service.SampleWaitsAsync(session, null, CancellationToken.None).WaitAsync(WaitLimit);
        output.WriteLine($"[T4] SampleWaits(null) rows = {own.Count}");
        foreach (var s in own.Take(20))
        {
            output.WriteLine("[T4]   " + s);
        }

        await LogRawAsync(session, "tuning.wait_sample", [CatalogBinds.Trimmed("sql_id", null)]);
        Assert.NotEmpty(own);

        // sql_id: 끝난 문장이라 실행 중인 세션이 없을 수 있다(0행도 정상). 실패는 기록만 한다.
        try
        {
            var byId = await service.SampleWaitsAsync(session, sqlId, CancellationToken.None).WaitAsync(WaitLimit);
            output.WriteLine($"[T4] SampleWaits({sqlId}) rows = {byId.Count}");
            foreach (var s in byId.Take(20))
            {
                output.WriteLine("[T4]   " + s);
            }

            await LogRawAsync(session, "tuning.wait_sample", [CatalogBinds.Trimmed("sql_id", sqlId)]);
        }
#pragma warning disable CA1031 // 실측: sql_id 쪽 실패도 결과로 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[T4] SampleWaits({sqlId}) failed: {ex.GetType().Name}: {ex.Message}");
        }

        Assert.False(session.CommandGate.IsHeld);
    }

    // T5: sql_id 의 누적 실행 통계.
    [TiberoItFact]
    public async Task T5_sql_stats_of_the_probe_statement()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        var sqlId = await RunProbeAsync(session);
        output.WriteLine($"[T5] sql_id = {sqlId}");
        IReadOnlyList<CatalogBind> binds = [CatalogBinds.Trimmed("sql_id", sqlId)];

        var stats = await MeasureAsync(session, "tuning.sql_stats", binds, () => service.GetSqlStatsAsync(session, sqlId, CancellationToken.None));
        output.WriteLine($"[T5] sql_stats = {stats?.ToString() ?? "null"}");
        await LogRawAsync(session, "tuning.sql_stats", binds);

        Assert.NotNull(stats);
    }

    // T6: 보고서 환경 행(버전·매개변수·NLS).
    [TiberoItFact]
    public async Task T6_tuning_environment_rows()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);

        var rows = await MeasureAsync(session, "tuning.report_env", CatalogBinds.None, () => service.GetTuningEnvironmentAsync(session, CancellationToken.None));
        output.WriteLine($"[T6] env rows = {rows.Count}");
        foreach (var r in rows)
        {
            output.WriteLine("[T6]   " + r);
        }

        Assert.NotEmpty(rows);
    }

    // T7: 벤치마크 두 번. 읽기 수(통계 출처 연결)와 계획 해시를 기록한다.
    [TiberoItFact]
    public async Task T7_run_benchmark_two_iterations()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);

        var result = await service.RunBenchmarkAsync(session, new BenchmarkCase("t7", ProbeSql), 2, CancellationToken.None).WaitAsync(WaitLimit);
        output.WriteLine($"[T7] iterations = {result.Iterations}, elapsed(avg) = {result.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms, " +
                         $"logical reads = {result.LogicalReads}, physical reads = {result.PhysicalReads}, rows = {result.Rows}, plan_hash = {result.PlanHashValue?.ToString(CultureInfo.InvariantCulture) ?? "null"}");

        Assert.Equal(2, result.Iterations);
        Assert.Equal(1, result.Rows);
        Assert.True(result.LogicalReads > 0, "논리 읽기가 0 이다: 세션 통계 이름(logical reads)이 v$mystat 와 어긋났을 수 있다");
        Assert.False(session.CommandGate.IsHeld);
    }

    // T7b: 읽기 전용 벤치마크는 거절된다. Tibero 의 READ ONLY 트랜잭션은 쓰기를 막지 않아(쓰기 IT #28, main a0140964) 사용자 함수가 든 문장이 실제로 쓸 수 있다.
    // 거절 뒤에도 같은 세션이 정상이어야 한다(서버로 아무것도 보내지 않았다).
    [TiberoItFact]
    public async Task T7b_read_only_benchmark_is_refused_and_the_session_stays_usable()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.RunBenchmarkAsync(session, new BenchmarkCase("t7b", "SELECT COUNT(*) FROM dual") { ReadOnly = true }, 1, CancellationToken.None).WaitAsync(WaitLimit));
        output.WriteLine($"[T7b] read-only benchmark refused: {ex.Message}");

        Assert.False(session.HasPendingTransaction);
        Assert.False(session.CommandGate.IsHeld);
        var next = await TiberoSessionQuery.ScalarAsync(session, "SELECT 1 FROM dual");
        Assert.Equal(1L, TiberoSessionQuery.Long(next));
    }

    /// <summary>probe 문장을 실행하고 그 sql_id 를 tuning.prev_cursor 로 읽는다.</summary>
    private static async Task<string> RunProbeAsync(TiberoSession session)
    {
        await TiberoSessionQuery.RunAsync(session, ProbeSql);
        var prev = await TiberoCatalogPort.QueryAsync(session, "tuning.prev_cursor", CatalogBinds.None, new CatalogQueryOptions { ColumnNames = true }, CancellationToken.None);
        var sqlId = CatalogValues.Str(Assert.Single(prev.Rows)[0])?.Trim();
        Assert.False(string.IsNullOrEmpty(sqlId), "tuning.prev_cursor 가 SQL ID 를 주지 않았다");
        return sqlId;
    }

    /// <summary>
    /// 표 하나를 결정적으로 고른다: 소유자 순서 = 접속 사용자, SYS. 소유자마다 통계가 있는(last_analyzed) 표를 먼저, 없으면 통계 없는 표.
    /// 조건 = 인덱스 열(all_ind_columns)이 있는 표, 같은 조건 안에서는 표 이름 순 첫 번째. 모두 없으면 table = null.
    /// </summary>
    private static async Task<(string? Owner, string? Table, bool Analyzed)> PickTableAsync(TiberoSession session)
    {
        var owners = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.Server.SessionUser))
        {
            owners.Add(session.Server.SessionUser.Trim().ToUpperInvariant());
        }

        if (!owners.Contains("SYS"))
        {
            owners.Add("SYS");
        }

        foreach (var owner in owners)
        {
            foreach (var analyzed in new[] { true, false })
            {
                var sql = "SELECT table_name FROM (SELECT t.table_name FROM all_tables t WHERE t.owner = :owner "
                          + (analyzed ? "AND t.last_analyzed IS NOT NULL " : string.Empty)
                          + "AND EXISTS (SELECT 1 FROM all_ind_columns c WHERE c.table_owner = t.owner AND c.table_name = t.table_name) "
                          + "ORDER BY t.table_name) WHERE ROWNUM <= 1";
                var found = await TiberoSessionQuery.RunAsync(session, sql, cmd => TiberoSessionQuery.Bind(cmd, "owner", owner));
                if (found.Rows.Count > 0 && TiberoSessionQuery.Str(found.Rows[0][0]) is { } name)
                {
                    return (owner, name, analyzed);
                }
            }
        }

        return (null, null, false);
    }

    /// <summary>서비스 호출을 부르고, 변환 등에서 실패하면 같은 카탈로그의 원시 첫 행(열 이름·CLR 형식·값)을 남기고 다시 던진다.</summary>
    private async Task<T> MeasureAsync<T>(TiberoSession session, string catalogId, IReadOnlyList<CatalogBind> binds, Func<Task<T>> call)
    {
        try
        {
            return await call().WaitAsync(WaitLimit);
        }
#pragma warning disable CA1031 // 실측: 실패 원인(원시 행)을 남기고 다시 던진다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[{catalogId}] failed: {ex.GetType().Name}: {ex.Message}");
            await LogRawAsync(session, catalogId, binds);
            throw;
        }
    }

    /// <summary>카탈로그 질의의 열 이름과 첫 행의 CLR 형식·값을 남긴다(변환이 어긋났을 때 원인을 보기 위해).</summary>
    private async Task LogRawAsync(TiberoSession session, string catalogId, IReadOnlyList<CatalogBind> binds)
    {
        try
        {
            var raw = await TiberoCatalogPort.QueryAsync(session, catalogId, binds, new CatalogQueryOptions { ColumnNames = true, DictionaryPrefix = true }, CancellationToken.None);
            output.WriteLine($"[{catalogId}] raw rows = {raw.Rows.Count}, columns = {string.Join(",", raw.Names)}");
            if (raw.Rows.Count > 0)
            {
                var row = raw.Rows[0];
                for (var i = 0; i < row.Length; i++)
                {
                    output.WriteLine($"[{catalogId}]   {(i < raw.Names.Count ? raw.Names[i] : "#" + i)} : {row[i]?.GetType().Name ?? "null"} = {CatalogValues.Str(row[i]) ?? "NULL"}");
                }
            }
        }
#pragma warning disable CA1031 // 실측: 원시 행 기록이 실패해도 시험 결과는 서비스 호출이 정한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[{catalogId}] raw failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static TiberoTuningService NewService(TiberoItSessions it)
    {
        var exec = new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false);
        return new TiberoTuningService(exec, new TiberoExecutionPlanService(exec), NewLanguage());
    }

    // LanguageService 는 Oranda.Language 의 internal 형식이다(이 시험 어셈블리에 보이지 않는다) → 이름으로 만든다.
    private static ILanguageService NewLanguage()
    {
        var type = Type.GetType("Oranda.Language.LanguageService, Oranda.Language", throwOnError: true)!;
        return (ILanguageService)Activator.CreateInstance(type)!;
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
}
