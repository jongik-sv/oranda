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
/// P5b 실측(읽기 전용): Tibero 튜닝 서비스의 이력(TPR)·ASH·추천 후보·계획 발견을 실서버로 확인한다. DDL·DML·스냅숏 생성·상태를 바꾸는 DBMS_* 호출 없음.
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 측정값은 시험 출력에 <c>[Hn] …</c> 줄로 남긴다(접속 정보는 싣지 않는다).
/// 실측 시험(H0·H1·H2·H4 의 일부)은 측정 호출이 실패해도 예외 형식·메시지·원시 첫 행을 남기고 계속한다.
/// 판정(win144 #49·#50): TPR 계획 이력·AWR 계획이 나온다 → <c>DbmsFeature.AwrReport</c> Tibero = true(그 키의 소비자 = 계획 이력 명령).
/// ASH 는 <c>ACTIVE_SESSION_HISTORY = NO</c>·표본 0행이라 잴 수 없다 → <c>Ash</c> Tibero = false 그대로. <c>LiteralGroups</c> 는 Tibero 파일이 없어 끈 채.
/// 알려진 서버 상태(Tibero 6.2.15 build 302172): <c>sql_stat_history</c>·<c>keep_stat_per_sql</c>·<c>gather_sql_plan_stat</c>·<c>active_session_history</c> = NO, TPR 스냅숏 169개(7일, 60분).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTuningHistoryIt(ITestOutputHelper output)
{
    private const string ProbeSql = "SELECT /*+ FULL(t) */ COUNT(*) FROM all_tables t";

    // 카탈로그(awr_plan_history·awr_plan_steps)가 sql_id → SQL_HASH_VALUE 로 바꿀 때 쓰는 base32 글자표(Oracle 과 같은 규칙).
    private const string Base32 = "0123456789abcdfghjkmnpqrstuvwxyz";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(120);

    // H0: 이력·ASH 환경. 개수·범위·매개변수만 남기고 단정하지 않는다.
    [Fact]
    public async Task H0_history_and_ash_environment()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);

        await LogQueryAsync(session, "H0", "dba_hist_snapshot",
            "SELECT COUNT(*) AS cnt, MIN(end_interval_time) AS min_time, MAX(end_interval_time) AS max_time, MIN(snap_id) AS min_snap, MAX(snap_id) AS max_snap FROM dba_hist_snapshot");
        await LogQueryAsync(session, "H0", "dba_hist_sqlstat",
            "SELECT COUNT(*) AS cnt, COUNT(DISTINCT sql_hash_value) AS hashes, MIN(snap_id) AS min_snap, MAX(snap_id) AS max_snap FROM dba_hist_sqlstat");
        await LogQueryAsync(session, "H0", "dba_hist_sql_plan",
            "SELECT COUNT(*) AS cnt, COUNT(DISTINCT sql_hash_value) AS hashes, COUNT(DISTINCT plan_hash_value) AS plans FROM dba_hist_sql_plan");
        await LogQueryAsync(session, "H0", "v$active_session_history",
            "SELECT COUNT(*) AS cnt, MIN(sample_time) AS min_time, MAX(sample_time) AS max_time, COUNT(DISTINCT sql_id) AS sql_ids FROM v$active_session_history");

        // 보고서 환경 절(tuning.report_env)이 읽는 v$parameters 에서 이력·ASH 와 관련된 이름을 넓게 찾는다(%ASH% 는 HASH 도 걸리지만 기록만 한다).
        await LogQueryAsync(session, "H0", "v$parameters(ash/tpr/sql_stat)",
            "SELECT name, value FROM v$parameters "
            + "WHERE UPPER(name) LIKE '%ASH%' OR UPPER(name) LIKE '%TPR%' OR UPPER(name) LIKE '%SQL_STAT%' OR UPPER(name) LIKE '%PLAN_STAT%' "
            + "OR UPPER(name) LIKE '%AWR%' OR UPPER(name) LIKE '%ACTIVE_SESSION_HISTORY%' "
            + "OR UPPER(name) IN ('GATHER_SQL_PLAN_STAT', 'KEEP_STAT_PER_SQL', 'SQL_STAT_HISTORY') "
            + "ORDER BY name");
    }

    // H1: 계획 이력. probe(방금 실행, 이력엔 없을 것)와 이력이 실제로 있는 sql_id 하나.
    [Fact]
    public async Task H1_awr_plan_history_of_probe_and_of_a_statement_with_history()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);

        var (probeId, _) = await RunProbeAsync(session);
        output.WriteLine($"[H1] probe sql_id = {probeId}");
        var probeRows = await TryAsync("H1", "tuning.awr_plan_history", session, [CatalogBinds.Trimmed("sql_id", probeId)],
            () => service.GetAwrPlanHistoryAsync(session, probeId, CancellationToken.None));
        LogRows("H1", $"probe history ({probeId})", probeRows);

        // 이력이 있는 sql_id: dba_hist_sqlstat 의 실행 수 1위 hash → V$SQL 의 sql_id 중 같은 규칙으로 계산한 hash 가 같은 것.
        var found = await FindSqlIdWithHistoryAsync(session);
        if (found is null)
        {
            return;
        }

        var (sqlId, hash) = found.Value;
        output.WriteLine($"[H1] history sql_id = {sqlId} (sql_hash_value {hash})");
        var rows = await TryAsync("H1", "tuning.awr_plan_history", session, [CatalogBinds.Trimmed("sql_id", sqlId)],
            () => service.GetAwrPlanHistoryAsync(session, sqlId, CancellationToken.None));
        LogRows("H1", $"history ({sqlId})", rows);

        // 이력에 hash 가 있는 sql_id 를 찾았으면 이력·계획이 나와야 한다(TPR 이 꺼진 서버는 위에서 found = null 로 끝난다).
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);

        var planHash = rows[0].PlanHashValue;
        output.WriteLine($"[H1] plan steps of ({sqlId}, plan_hash {planHash.ToString(CultureInfo.InvariantCulture)})");
        var plan = await TryAsync("H1", "tuning.awr_plan_steps", session, [CatalogBinds.Trimmed("sql_id", sqlId), CatalogBinds.Long("plan_hash_value", planHash)],
            () => service.GetAwrPlanAsync(session, sqlId, planHash, CancellationToken.None));
        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Steps);
        Assert.Equal(planHash, plan.PlanHashValue);

        output.WriteLine($"[H1] steps = {plan.Steps.Count}, plan_hash = {plan.PlanHashValue?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
        foreach (var step in plan.Steps.Take(30))
        {
            output.WriteLine("[H1]   " + step);
        }

        output.WriteLine("[H1] plan text:");
        foreach (var line in plan.Text.Split('\n').Take(30))
        {
            output.WriteLine("[H1]   | " + line.TrimEnd('\r'));
        }
    }

    // H2: ASH 대기. probe sql_id 와, ASH 표본이 있으면 표본이 가장 많은 sql_id.
    [Fact]
    public async Task H2_ash_waits_of_probe_and_of_the_busiest_sql_id()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        var (probeId, _) = await RunProbeAsync(session);
        var since = DateTime.Now.AddHours(-1);
        output.WriteLine($"[H2] probe sql_id = {probeId}, since = {since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");

        var probeRows = await TryAsync("H2", "tuning.ash_waits", session, [CatalogBinds.Trimmed("sql_id", probeId), CatalogBinds.Date("since", since)],
            () => service.GetAshWaitsAsync(session, probeId, since, CancellationToken.None));
        LogRows("H2", $"probe ash waits ({probeId})", probeRows);

        string? busiest = null;
        try
        {
            var found = await TiberoSessionQuery.RunAsync(session,
                "SELECT * FROM (SELECT sql_id, COUNT(*) AS samples FROM v$active_session_history WHERE sql_id IS NOT NULL GROUP BY sql_id ORDER BY samples DESC, sql_id) WHERE ROWNUM <= 1");
            if (found.Rows.Count > 0)
            {
                busiest = TiberoSessionQuery.Str(found.Rows[0][0])?.Trim();
                output.WriteLine($"[H2] busiest sql_id = {busiest} (samples {TiberoSessionQuery.Str(found.Rows[0][1])})");
            }
            else
            {
                output.WriteLine("[H2] v$active_session_history 에 sql_id 가 있는 표본이 없음");
            }
        }
#pragma warning disable CA1031 // 실측: ASH 뷰가 없거나 열이 다르면 실패 원인을 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H2] ASH 표본 탐색 실패: {ex.GetType().Name}: {ex.Message}");
        }

        if (string.IsNullOrEmpty(busiest))
        {
            return;
        }

        // 최근 1시간과, 서버에 남은 표본 전체(30일)를 모두 본다: 표본이 오래됐을 수도 있다.
        foreach (var window in new[] { since, DateTime.Now.AddDays(-30) })
        {
            var w = window;
            var rows = await TryAsync("H2", "tuning.ash_waits", session, [CatalogBinds.Trimmed("sql_id", busiest), CatalogBinds.Date("since", w)],
                () => service.GetAshWaitsAsync(session, busiest, w, CancellationToken.None));
            LogRows("H2", $"busiest ash waits ({busiest}, since {w.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)})", rows);
        }
    }

    // H3: 추천 후보. V$SQL 에서 읽으므로 실행된 문장이 있으면 비어 있지 않고, 시간 값은 음수가 아니다(V$SQLSTATS 의 부호 없는 64비트 넘침을 피했는지).
    [Fact]
    public async Task H3_tuning_candidates_are_returned_with_non_negative_times()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        await RunProbeAsync(session); // 실행된 문장이 하나는 있게 한다.

        // #49 실측: V$SQL 의 일부 값이 넘어간 값(음수 → 부호 없는 64비트)이라 합이 Int64 를 넘었다. 열마다 0 ~ 2^62-1 밖 행 수를 남긴다(카탈로그가 NULL 로 뺀다).
        try
        {
            var wrapped = await TiberoSessionQuery.RunAsync(session,
                "SELECT COUNT(CASE WHEN executions NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS executions, COUNT(CASE WHEN elapsed_time NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS elapsed_time, COUNT(CASE WHEN cpu_time NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS cpu_time, COUNT(CASE WHEN buffer_gets NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS buffer_gets, COUNT(CASE WHEN disk_reads NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS disk_reads, COUNT(CASE WHEN rows_processed NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS rows_processed, COUNT(CASE WHEN parse_calls NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS parse_calls, COUNT(CASE WHEN fetches NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS fetches, COUNT(CASE WHEN user_io_wait_time NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS user_io_wait_time, COUNT(CASE WHEN application_wait_time NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS application_wait_time, COUNT(CASE WHEN concurrency_wait_time NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS concurrency_wait_time, COUNT(CASE WHEN cluster_wait_time NOT BETWEEN 0 AND 4611686018427387903 THEN 1 END) AS cluster_wait_time FROM v$sql");
            output.WriteLine("[H3] v$sql rows outside 0..2^62-1 per column: " +
                string.Join(", ", wrapped.Columns.Select((c, i) => $"{c}={TiberoSessionQuery.Str(wrapped.Rows[0][i])}")));
        }
#pragma warning disable CA1031 // 실측 기록만
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H3] wrapped-value count failed: {ex.GetType().Name}: {ex.Message}");
        }

        var query = new TuningCandidateQuery { Top = 10, MinExecutions = 1, IncludeLiteralGroups = false };
        IReadOnlyList<CatalogBind> binds = [CatalogBinds.Int("top", 10), CatalogBinds.Long("min_executions", 1), CatalogBinds.Trimmed("sql_ids", null)];
        TuningCandidateSet set;
        try
        {
            set = await service.GetTuningCandidatesAsync(session, query, CancellationToken.None).WaitAsync(WaitLimit);
        }
#pragma warning disable CA1031 // 실측: 실패 원인(원시 행)을 남기고 다시 던진다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H3] failed: {ex.GetType().Name}: {ex.Message}");
            await LogRawAsync(session, "tuning.recommend_candidates", binds);
            throw;
        }

        output.WriteLine($"[H3] candidates = {set.Rows.Count}, total elapsed = {set.TotalElapsedMicros.ToString(CultureInfo.InvariantCulture)} us, literal groups = {set.LiteralGroups.Count}");
        foreach (var r in set.Rows)
        {
            output.WriteLine($"[H3]   {r with { SqlText = Shorten(r.SqlText) }}");
        }

        await LogRawAsync(session, "tuning.recommend_candidates", binds);

        Assert.NotEmpty(set.Rows);
        Assert.True(set.TotalElapsedMicros >= 0, "total_elapsed 가 음수다: 시간 열이 음수로 넘쳤다");
        foreach (var r in set.Rows)
        {
            Assert.True(r.ElapsedMicros >= 0, $"{r.SqlId}: elapsed_time 이 음수다 ({r.ElapsedMicros.ToString(CultureInfo.InvariantCulture)})");
            Assert.True(r.CpuMicros >= 0, $"{r.SqlId}: cpu_time 이 음수다 ({r.CpuMicros.ToString(CultureInfo.InvariantCulture)})");
        }

        Assert.False(session.CommandGate.IsHeld);
    }

    // H4: 계획 발견. 첫 후보(기록만)와 probe(표 전체 스캔 줄이 있어야 한다).
    [Fact]
    public async Task H4_plan_findings_of_first_candidate_and_probe()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        var (probeId, probePlanHash) = await RunProbeAsync(session);

        // 첫 후보: 기록만 한다.
        try
        {
            var set = await service.GetTuningCandidatesAsync(session, new TuningCandidateQuery { Top = 10, MinExecutions = 1, IncludeLiteralGroups = false }, CancellationToken.None).WaitAsync(WaitLimit);
            if (set.Rows.Count == 0)
            {
                output.WriteLine("[H4] 후보가 없어 첫 후보 계획 발견은 건너뜀");
            }
            else
            {
                var c = set.Rows[0];
                output.WriteLine($"[H4] first candidate = inst {c.InstanceId?.ToString(CultureInfo.InvariantCulture) ?? "null"}, sql_id {c.SqlId}, plan_hash {c.PlanHashValue.ToString(CultureInfo.InvariantCulture)}");
                var lines = await TryAsync("H4", "tuning.recommend_plan", session,
                    [CatalogBinds.Int("inst_id", c.InstanceId ?? 1), CatalogBinds.Trimmed("sql_id", c.SqlId), CatalogBinds.Long("plan_hash", c.PlanHashValue)],
                    () => service.GetPlanFindingsAsync(session, c.InstanceId, c.SqlId, c.PlanHashValue, CancellationToken.None));
                LogRows("H4", $"first candidate findings ({c.SqlId})", lines);
            }
        }
#pragma warning disable CA1031 // 실측: 후보 조회 실패는 H3 가 단정한다. 여기서는 기록만 한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H4] 후보 조회 실패: {ex.GetType().Name}: {ex.Message}");
        }

        // probe: FULL 힌트 문장은 TABLE ACCESS FULL 줄이 있다.
        var instance = TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, "SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) FROM dual")) ?? 1;
        output.WriteLine($"[H4] probe sql_id = {probeId}, plan_hash = {probePlanHash?.ToString(CultureInfo.InvariantCulture) ?? "null"}, inst = {instance.ToString(CultureInfo.InvariantCulture)}");
        Assert.NotNull(probePlanHash);

        var instanceId = (int)instance;
        IReadOnlyList<CatalogBind> binds = [CatalogBinds.Int("inst_id", instanceId), CatalogBinds.Trimmed("sql_id", probeId), CatalogBinds.Long("plan_hash", probePlanHash.Value)];
        IReadOnlyList<PlanFindingLine> probeLines;
        try
        {
            probeLines = await service.GetPlanFindingsAsync(session, instanceId, probeId, probePlanHash.Value, CancellationToken.None).WaitAsync(WaitLimit);
        }
#pragma warning disable CA1031 // 실측: 실패 원인(원시 행)을 남기고 다시 던진다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H4] probe findings failed: {ex.GetType().Name}: {ex.Message}");
            await LogRawAsync(session, "tuning.recommend_plan", binds);
            throw;
        }

        LogRows("H4", $"probe findings ({probeId})", probeLines);
        await LogRawAsync(session, "tuning.recommend_plan", binds);

        Assert.NotEmpty(probeLines);
        Assert.Contains(probeLines, l =>
            string.Equals(l.Operation, "TABLE ACCESS", StringComparison.Ordinal) && string.Equals(l.Options, "FULL", StringComparison.Ordinal));
        Assert.False(session.CommandGate.IsHeld);
    }

    // H5: 꺼진 기능. 자식 커서 사유·SQL 계획 고정은 NotSupportedException(마지막 방어선), 리터럴 묶음은 후보 조회의 IncludeLiteralGroups 가 막는다.
    [Fact]
    public async Task H5_unsupported_features_throw_and_literal_groups_are_logged()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var service = NewService(it);
        var (probeId, _) = await RunProbeAsync(session);

        var reasons = await Assert.ThrowsAsync<NotSupportedException>(() => service.GetSharedCursorReasonsAsync(session, probeId, CancellationToken.None));
        output.WriteLine($"[H5] shared cursor reasons: NotSupportedException: {reasons.Message}");
        var controls = await Assert.ThrowsAsync<NotSupportedException>(() => service.GetSqlPlanControlsAsync(session, probeId, CancellationToken.None));
        output.WriteLine($"[H5] sql plan controls: NotSupportedException: {controls.Message}");

        // DbmsFeature.LiteralGroups 가 막는 것 = GetTuningCandidatesAsync 의 IncludeLiteralGroups(tuning.recommend_literals). Tibero 카탈로그에 그 항목이 없으면 어떻게 되는지 기록한다.
        try
        {
            var set = await service.GetTuningCandidatesAsync(session, new TuningCandidateQuery { Top = 10, MinExecutions = 1, IncludeLiteralGroups = true }, CancellationToken.None).WaitAsync(WaitLimit);
            output.WriteLine($"[H5] literal groups with IncludeLiteralGroups = true: candidates = {set.Rows.Count}, literal groups = {set.LiteralGroups.Count}");
            foreach (var g in set.LiteralGroups.Take(10))
            {
                output.WriteLine($"[H5]   {g with { Sample = g.Sample with { SqlText = Shorten(g.Sample.SqlText) } }}");
            }
        }
#pragma warning disable CA1031 // 실측: 리터럴 묶음 질의가 없으면 실패한다. 그 형식·메시지를 남긴다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H5] literal groups with IncludeLiteralGroups = true failed: {ex.GetType().Name}: {ex.Message}");
        }

        Assert.False(session.CommandGate.IsHeld);
    }

    /// <summary>
    /// 이력(dba_hist_sqlstat)이 있는 문장의 sql_id 하나를 찾는다. 실행 수 1위 sql_hash_value 를 고르고,
    /// V$SQL 의 sql_id 마다 카탈로그와 같은 규칙(base32 → 아래 32비트)으로 hash 를 계산해 같은 것을 찾는다. 못 찾으면 사실만 남기고 null.
    /// </summary>
    private async Task<(string SqlId, string Hash)?> FindSqlIdWithHistoryAsync(TiberoSession session)
    {
        string? hash;
        try
        {
            var top = await TiberoSessionQuery.RunAsync(session,
                "SELECT * FROM (SELECT sql_hash_value, SUM(executions) AS execs FROM dba_hist_sqlstat WHERE sql_hash_value IS NOT NULL GROUP BY sql_hash_value ORDER BY execs DESC, sql_hash_value) WHERE ROWNUM <= 1");
            if (top.Rows.Count == 0)
            {
                output.WriteLine("[H1] dba_hist_sqlstat 에 행이 없어 이력이 있는 sql_id 를 못 고름(건너뜀)");
                return null;
            }

            hash = TiberoSessionQuery.Str(top.Rows[0][0]);
            output.WriteLine($"[H1] top history sql_hash_value = {hash} (executions {TiberoSessionQuery.Str(top.Rows[0][1])})");
        }
#pragma warning disable CA1031 // 실측: 이력 뷰 실패도 결과로 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H1] dba_hist_sqlstat 탐색 실패: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (!ulong.TryParse(hash, NumberStyles.None, CultureInfo.InvariantCulture, out var wanted))
        {
            output.WriteLine($"[H1] sql_hash_value 를 숫자로 못 읽음 ({hash}) → 건너뜀");
            return null;
        }

        try
        {
            var cursors = await TiberoSessionQuery.RunAsync(session, "SELECT DISTINCT sql_id FROM v$sql WHERE sql_id IS NOT NULL");
            output.WriteLine($"[H1] v$sql distinct sql_id = {cursors.Rows.Count}");
            foreach (var row in cursors.Rows)
            {
                var id = TiberoSessionQuery.Str(row[0])?.Trim();
                if (!string.IsNullOrEmpty(id) && HashOf(id) == wanted)
                {
                    return (id, hash!);
                }
            }

            output.WriteLine("[H1] 그 hash 의 sql_id 가 v$sql 에 없음(커서가 밀려났다) → 건너뜀");
        }
#pragma warning disable CA1031 // 실측: v$sql 탐색 실패도 결과로 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[H1] v$sql 탐색 실패: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    /// <summary>카탈로그 awr_plan_history 의 식과 같다: sql_id 를 base32(0123456789abcdfghjkmnpqrstuvwxyz) 수로 읽은 값의 아래 32비트.</summary>
    private static ulong HashOf(string sqlId)
    {
        ulong value = 0;
        foreach (var ch in sqlId)
        {
            var digit = Base32.IndexOf(ch, StringComparison.Ordinal);
            if (digit < 0)
            {
                return ulong.MaxValue; // 규칙 밖 글자 → 어떤 hash 와도 같지 않게(32비트 밖 값)
            }

            value = unchecked((value * 32) + (ulong)digit); // 65비트째는 넘쳐도 아래 32비트에 영향이 없다
        }

        return value & 0xFFFFFFFFUL;
    }

    /// <summary>질의 하나를 돌려 열 이름과 모든 행(최대 50)을 남긴다. 실패는 형식·메시지만 남긴다.</summary>
    private async Task LogQueryAsync(TiberoSession session, string tag, string label, string sql)
    {
        try
        {
            var table = await TiberoSessionQuery.RunAsync(session, sql, maxRows: 50);
            output.WriteLine($"[{tag}] {label}: rows = {table.Rows.Count}, columns = {string.Join(",", table.Columns)}");
            foreach (var row in table.Rows)
            {
                output.WriteLine($"[{tag}]   " + string.Join(" | ", row.Select((v, i) => $"{table.Columns[i]}={TiberoSessionQuery.Str(v) ?? "NULL"}")));
            }
        }
#pragma warning disable CA1031 // 실측: 뷰가 없거나 권한이 없을 수 있다. 실패도 결과로 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[{tag}] {label}: failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>서비스 호출을 부르고 실패하면 형식·메시지와 같은 카탈로그의 원시 첫 행을 남기고 null 을 돌려준다(실측용: 던지지 않는다).</summary>
    private async Task<T?> TryAsync<T>(string tag, string catalogId, TiberoSession session, IReadOnlyList<CatalogBind> binds, Func<Task<T>> call)
        where T : class
    {
        try
        {
            return await call().WaitAsync(WaitLimit);
        }
#pragma warning disable CA1031 // 실측: 측정 호출의 실패는 결과로 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[{tag}] {catalogId} failed: {ex.GetType().Name}: {ex.Message}");
            await LogRawAsync(session, catalogId, binds);
            return null;
        }
    }

    private void LogRows<T>(string tag, string label, IReadOnlyList<T>? rows)
    {
        if (rows is null)
        {
            output.WriteLine($"[{tag}] {label}: (호출 실패)");
            return;
        }

        output.WriteLine($"[{tag}] {label}: rows = {rows.Count}");
        foreach (var r in rows.Take(50))
        {
            output.WriteLine($"[{tag}]   {r}");
        }
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    /// <summary>probe 문장을 실행하고 그 sql_id 와 계획 해시를 tuning.prev_cursor(첫·세 번째 열)로 읽는다.</summary>
    private static async Task<(string SqlId, long? PlanHash)> RunProbeAsync(TiberoSession session)
    {
        await TiberoSessionQuery.RunAsync(session, ProbeSql);
        var prev = await TiberoCatalogPort.QueryAsync(session, "tuning.prev_cursor", CatalogBinds.None, new CatalogQueryOptions { ColumnNames = true }, CancellationToken.None);
        var row = Assert.Single(prev.Rows);
        var sqlId = CatalogValues.Str(row[0])?.Trim();
        Assert.False(string.IsNullOrEmpty(sqlId), "tuning.prev_cursor 가 SQL ID 를 주지 않았다");
        return (sqlId, CatalogValues.Long(row[2]));
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
