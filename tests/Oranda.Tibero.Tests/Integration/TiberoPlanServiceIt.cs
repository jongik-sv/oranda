using System.Globalization;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Plans;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// P1b 실측(읽기 전용): 실제 <see cref="TiberoExecutionPlanService"/>(실제 <see cref="TiberoExecutionService"/> 위)로 Explain(M3), PLAN_TABLE.SQL_ID(M4),
/// SQL 모니터 보고서(M5), 자식 번호(M6), DISPLAY_CURSOR 글(M7), 실제 통계 열(M8)을 잰다. EXPLAIN PLAN 은 PLAN_TABLE(SYS 소유 세션 임시 표)에만 쓰므로 정리가 없다(M0).
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 측정만 하는 시험은 실패를 기록하고 넘어간다. 결과는 시험 출력에 `[Mx] …` 로 남긴다(접속 정보는 싣지 않는다).
/// 직전 커서(GetLastCursorAsync)는 문장 바로 뒤, 같은 세션에서 다른 문장 없이 불러야 한다(CatalogExecutionPlanService 주석).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoPlanServiceIt(ITestOutputHelper output)
{
    private const string FullScanQuery = "SELECT /*+ FULL(t) */ COUNT(*) FROM all_tables t";

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private static readonly string[] SqlIdColumns = ["SQL_ID", "CHILD_NUMBER", "PLAN_HASH_VALUE"];

    private static readonly string[] CursorFormats = ["BASIC", "ALLSTATS LAST"];

    // M3: 예상 계획. EXPLAIN PLAN → plan.explain_steps·explain_text → NormalizeStep 뒤 TABLE ACCESS 의 FULL 이 Options 로 간다. 계획 해시는 null(Q4).
    [Fact]
    public async Task M3_explain_returns_steps_text_and_normalized_full_access()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewPlanService(it);

        var plan = await service.ExplainAsync(session, Query(FullScanQuery), CancellationToken.None);

        output.WriteLine($"[M3] steps = {plan.Steps.Count}, text length = {plan.Text.Length}, PlanHashValue = {plan.PlanHashValue?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
        foreach (var step in plan.Steps)
        {
            output.WriteLine($"[M3] step id={step.Id} parent={step.ParentId?.ToString(CultureInfo.InvariantCulture) ?? "null"} depth={step.Depth} " +
                             $"operation='{step.Operation}' options='{step.Options}' object={step.ObjectOwner}.{step.ObjectName} cost={step.Cost} rows={step.EstimatedRows} risk={step.Risk}");
        }

        LogLines("M3 text", plan.Text, 15);

        Assert.NotEmpty(plan.Steps);
        Assert.False(string.IsNullOrWhiteSpace(plan.Text));
        Assert.Contains(plan.Steps, s =>
            s.Operation.Contains("TABLE ACCESS", StringComparison.OrdinalIgnoreCase)
            && (s.Options?.Contains("FULL", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.NotNull(plan.PlanHashValue); // plan.explain_hash: PLAN_TABLE.SQL_ID·CHILD_NUMBER → V$SQL(M4 실측)
        Assert.False(session.CommandGate.IsHeld);
    }

    // M4: PLAN_TABLE 에 SQL_ID·PLAN_HASH_VALUE 열이 있는지, 있으면 EXPLAIN 뒤 그 값이 V$SQL 로 이어지는지(Q4). 측정만 한다 — 열 질의가 도는지만 단정한다.
    [Fact]
    public async Task M4_plan_table_sql_id_columns_are_measured()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewExecutionService(it);

        var columns = await TiberoSessionQuery.RunAsync(session,
            "SELECT column_name, data_type FROM all_tab_columns WHERE owner = 'SYS' AND table_name = 'PLAN_TABLE' ORDER BY column_id");
        var names = columns.Rows.Select(r => CatalogValues.Str(r[0]) ?? string.Empty).ToList();
        output.WriteLine($"[M4] PLAN_TABLE columns ({names.Count}): " +
                         string.Join(", ", columns.Rows.Select(r => $"{CatalogValues.Str(r[0])}:{CatalogValues.Str(r[1])}")));
        Assert.NotEmpty(names);

        var interesting = SqlIdColumns
            .Where(c => names.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        output.WriteLine($"[M4] columns present among SQL_ID/CHILD_NUMBER/PLAN_HASH_VALUE: {(interesting.Count == 0 ? "none" : string.Join(", ", interesting))}");
        if (!interesting.Contains("SQL_ID") && !interesting.Contains("PLAN_HASH_VALUE"))
        {
            return;
        }

        await MeasureAsync("M4", async () =>
        {
            var statementId = "ORT_M4_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var explain = new SqlStatement($"EXPLAIN PLAN SET STATEMENT_ID = '{statementId}' FOR {FullScanQuery}", default, StatementKind.Other) { Verb = "EXPLAIN" };
            var result = await service.ExecuteAsync(session, explain, NoBinds, null, CancellationToken.None);
            output.WriteLine($"[M4] EXPLAIN status = {result.Status}, error = {result.Error?.Code}");
            if (result.Status != ExecutionStatus.Succeeded)
            {
                return;
            }

            var rows = await TiberoSessionQuery.RunAsync(session,
                $"SELECT id, {string.Join(", ", interesting)} FROM plan_table WHERE statement_id = :s ORDER BY id",
                cmd => TiberoSessionQuery.Bind(cmd, "s", statementId));
            output.WriteLine($"[M4] plan_table rows for statement_id = {rows.Rows.Count}, columns = {string.Join(",", rows.Columns)}");
            foreach (var row in rows.Rows)
            {
                output.WriteLine("[M4]   " + string.Join(" | ", row.Select(v => TiberoSessionQuery.Str(v) ?? "NULL")));
            }

            var sqlIdIndex = rows.Columns.ToList().FindIndex(c => string.Equals(c, "SQL_ID", StringComparison.OrdinalIgnoreCase));
            if (sqlIdIndex < 0)
            {
                return;
            }

            foreach (var sqlId in rows.Rows.Select(r => CatalogValues.Str(r[sqlIdIndex])).Where(v => !string.IsNullOrEmpty(v)).Distinct())
            {
                var v = await TiberoSessionQuery.RunAsync(session,
                    "SELECT child_number, plan_hash_value FROM v$sql WHERE sql_id = :sql_id ORDER BY child_number",
                    cmd => TiberoSessionQuery.Bind(cmd, "sql_id", sqlId));
                output.WriteLine($"[M4] v$sql rows for plan_table sql_id '{sqlId}' = {v.Rows.Count}");
                foreach (var row in v.Rows.Take(10))
                {
                    output.WriteLine($"[M4]   child_number={TiberoSessionQuery.Str(row[0])} plan_hash_value={TiberoSessionQuery.Str(row[1])}");
                }
            }
        });
    }

    // M5: SQL 모니터 보고서(텍스트). 측정만 한다 — 예외는 형식·메시지를 기록하고 넘어간다.
    [Fact]
    public async Task M5_sql_monitor_report_text_is_measured()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewPlanService(it);
        await TiberoSessionQuery.RunAsync(session, FullScanQuery);
        var cursor = await service.GetLastCursorAsync(session, CancellationToken.None);
        output.WriteLine($"[M5] last cursor = {Describe(cursor)}");
        if (cursor is null)
        {
            return;
        }

        await MeasureAsync("M5", async () =>
        {
            var report = await service.GetSqlMonitorReportAsync(session, cursor, html: false, CancellationToken.None);
            output.WriteLine($"[M5] report length = {report.Length}");
            LogLines("M5 report", report, 20);
        });
        Assert.False(session.CommandGate.IsHeld);
    }

    // M6: 자식 번호. tuning.prev_cursor 의 PREV_CHILD_NUMBER 가 V$SQL_PLAN·V$SQL 의 CHILD_NUMBER 와 같은 번호인지 잰다.
    // prev_cursor 를 두 번 부르면 두 번째는 첫 번째 질의를 보므로, 같은 문장(같은 SQL ID)을 두 번 실행해 GetLastCursorAsync 와 prev_cursor 를 한 번씩 부른다.
    [Fact]
    public async Task M6_child_number_of_the_last_cursor_is_measured()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewPlanService(it);

        await TiberoSessionQuery.RunAsync(session, FullScanQuery);
        var cursor = await service.GetLastCursorAsync(session, CancellationToken.None);
        output.WriteLine($"[M6] GetLastCursorAsync = {Describe(cursor)}");
        Assert.NotNull(cursor);

        await TiberoSessionQuery.RunAsync(session, FullScanQuery);
        var prev = await TiberoCatalogPort.QueryAsync(session, "tuning.prev_cursor", CatalogBinds.None, CatalogQueryOptions.Default, CancellationToken.None);
        var row = Assert.Single(prev.Rows);
        var prevSqlId = CatalogValues.Str(row[0]);
        var prevChild = CatalogValues.Int(row[1]);
        output.WriteLine($"[M6] prev_cursor: prev_sql_id = {prevSqlId}, PREV_CHILD_NUMBER = {prevChild?.ToString(CultureInfo.InvariantCulture) ?? "null"}, plan_hash_value = {TiberoSessionQuery.Str(row[2]) ?? "NULL"}");
        Assert.Equal(prevSqlId, cursor!.SqlId);
        Assert.Equal(prevChild, cursor.ChildNumber); // 같은 문장 두 번 → 같은 커서(M6 실측: PREV_CHILD_NUMBER = V$SQL_PLAN 자식 번호)

        await MeasureAsync("M6", async () =>
        {
            var planChildren = await DistinctChildrenAsync(session, "v$sql_plan", prevSqlId!);
            var sqlChildren = await DistinctChildrenAsync(session, "v$sql", prevSqlId!);
            output.WriteLine($"[M6] v$sql_plan child_number values ({planChildren.Count}): {string.Join(", ", planChildren)}");
            output.WriteLine($"[M6] v$sql child_number values ({sqlChildren.Count}): {string.Join(", ", sqlChildren)}");
            output.WriteLine($"[M6] PREV_CHILD_NUMBER in v$sql_plan values = {prevChild is { } c && planChildren.Contains(c)}");
            output.WriteLine($"[M6] PREV_CHILD_NUMBER in v$sql values = {prevChild is { } d && sqlChildren.Contains(d)}");
        });
    }

    // M7: DISPLAY_CURSOR 글 세 가지 형식. TYPICAL 은 단계·글이 있어야 하고, BASIC·ALLSTATS LAST 는 실패해도 기록하고 넘어간다.
    [Fact]
    public async Task M7_cursor_plan_text_formats_are_measured()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewPlanService(it);
        await TiberoSessionQuery.RunAsync(session, FullScanQuery);
        var cursor = await service.GetLastCursorAsync(session, CancellationToken.None);
        output.WriteLine($"[M7] last cursor = {Describe(cursor)}");
        Assert.NotNull(cursor);

        var typical = await service.GetCursorPlanAsync(session, cursor!, "TYPICAL", CancellationToken.None);
        LogCursorPlan("M7 TYPICAL", typical);
        Assert.NotEmpty(typical.Steps);
        Assert.False(string.IsNullOrWhiteSpace(typical.Text));

        foreach (var format in CursorFormats)
        {
            await MeasureAsync("M7 " + format, async () =>
            {
                var plan = await service.GetCursorPlanAsync(session, cursor!, format, CancellationToken.None);
                LogCursorPlan("M7 " + format, plan);
            });
        }

        Assert.False(session.CommandGate.IsHeld);
    }

    // M8: GATHER_SQL_PLAN_STAT 설정과 실제 행·시간 열(ActualRows·Starts·ActualTime) 값. 측정만 한다.
    [Fact]
    public async Task M8_actual_statistics_columns_and_gather_parameter_are_measured()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewPlanService(it);
        await TiberoSessionQuery.RunAsync(session, FullScanQuery);
        var cursor = await service.GetLastCursorAsync(session, CancellationToken.None);
        output.WriteLine($"[M8] last cursor = {Describe(cursor)}");
        if (cursor is null)
        {
            return;
        }

        var typical = await service.GetCursorPlanAsync(session, cursor, "TYPICAL", CancellationToken.None);
        var withActual = typical.Steps.Where(s => s.ActualRows is not null || s.Starts is not null || s.ActualTime is not null).ToList();
        output.WriteLine($"[M8] steps = {typical.Steps.Count}, steps with non-null ActualRows/Starts/ActualTime = {withActual.Count}");
        foreach (var step in typical.Steps)
        {
            output.WriteLine($"[M8] step id={step.Id} operation='{step.Operation}' options='{step.Options}' " +
                             $"ActualRows={step.ActualRows?.ToString(CultureInfo.InvariantCulture) ?? "null"} Starts={step.Starts?.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
                             $"ActualTime={step.ActualTime?.ToString() ?? "null"}");
        }

        await MeasureAsync("M8", async () =>
        {
            var param = await TiberoSessionQuery.RunAsync(session, "SELECT name, value FROM v$parameter WHERE name = 'GATHER_SQL_PLAN_STAT'");
            output.WriteLine($"[M8] v$parameter GATHER_SQL_PLAN_STAT rows = {param.Rows.Count}");
            foreach (var row in param.Rows)
            {
                output.WriteLine($"[M8]   {TiberoSessionQuery.Str(row[0])} = {TiberoSessionQuery.Str(row[1]) ?? "NULL"}");
            }
        });
    }

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query);

    // 실제 실행 서비스(E4) 위에 실제 계획 서비스를 얹는다. 정책은 모두 허용, 질문은 없다.
    private static TiberoExecutionService NewExecutionService(TiberoItSessions it) =>
        new(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false);

    private static TiberoExecutionPlanService NewPlanService(TiberoItSessions it) => new(NewExecutionService(it));

    private static string Describe(CursorRef? cursor) =>
        cursor is null
            ? "null"
            : $"sql_id={cursor.SqlId} child={cursor.ChildNumber?.ToString(CultureInfo.InvariantCulture) ?? "null"} inst={cursor.InstanceId?.ToString(CultureInfo.InvariantCulture) ?? "null"}";

    private static async Task<List<int>> DistinctChildrenAsync(ISession session, string view, string sqlId)
    {
        var table = await TiberoSessionQuery.RunAsync(session,
            $"SELECT DISTINCT child_number FROM {view} WHERE sql_id = :sql_id ORDER BY child_number",
            cmd => TiberoSessionQuery.Bind(cmd, "sql_id", sqlId));
        return table.Rows.Select(r => CatalogValues.Int(r[0]) ?? -1).ToList();
    }

    private void LogCursorPlan(string label, ExecutionPlan plan)
    {
        output.WriteLine($"[{label}] steps = {plan.Steps.Count}, text length = {plan.Text.Length}, Cursor = {Describe(plan.Cursor)}, " +
                         $"PlanHashValue = {plan.PlanHashValue?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
        LogLines(label, plan.Text, 10);
    }

    private void LogLines(string label, string text, int count)
    {
        var lines = text.Split('\n');
        output.WriteLine($"[{label}] first {Math.Min(count, lines.Length)} of {lines.Length} lines:");
        foreach (var line in lines.Take(count))
        {
            output.WriteLine("  " + line.TrimEnd('\r'));
        }
    }

    // 측정만 하는 단계: 예외는 형식·메시지를 기록하고 넘어간다(서버·버전마다 다를 수 있다).
    private async Task MeasureAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // 실측: 실패도 결과로 기록한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"[{label}] failed: {ex.GetType().Name}: {ex.Message}");
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
}
