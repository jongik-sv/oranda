using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Plans;

// 소유: TSK-06-04. 계획 창 보강의 서버 쪽 길(11.2): 직전 커서 읽기, 힌트 사본으로 A-Rows 받기, 10053 파싱만, 내 세션 트레이스 경로, 보고서 관문.

/// <summary>11g 서버에서 계획 창 보강(TSK-06-04)이 쓰는 길을 확인한다.</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class PlanBoost11gTests : IAsyncLifetime
{
    private const string Hinted = "SELECT /*+ GATHER_PLAN_STATISTICS */ COUNT(*) FROM all_objects WHERE object_id > 0";

    private readonly Oracle11gFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private OracleSessionManager _manager = null!;
    private OracleExecutionService _exec = null!;

    public PlanBoost11gTests(Oracle11gFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        _exec = new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }
    }

    private async Task<OracleSession> OpenAsync(LicensePack pack = LicensePack.None)
    {
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(new ConnectionProfile("ora11-system", "system", _db.DataSource, LicensePack: pack), cred, SessionPurpose.Work, default);
    }

    private static readonly ExecutionOptions Quiet = new() { SkipHistory = true };

    private async Task<ExecutionResult> RunAsync(ISession s, string sql, StatementKind kind, string? verb = null, IReadOnlyDictionary<string, BindValue>? binds = null)
    {
        var result = await _exec.ExecuteAsync(s, new SqlStatement(sql, default, kind) { Verb = verb, BindNames = binds is null ? [] : [.. binds.Keys] },
            binds ?? new Dictionary<string, BindValue>(), Quiet, default);
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{sql}: {result.Status} {result.Error?.Message}");
        return result;
    }

    [Fact]
    public async Task Hinted_copy_fills_a_rows_and_the_last_cursor_is_read_from_display_cursor_null()
    {
        await using var session = await OpenAsync();
        var plans = new OracleExecutionPlanService(_exec);

        var result = await RunAsync(session, Hinted, StatementKind.Query, "SELECT");
        await using (var rs = result.ResultSet!)
        {
            for (var p = 0; ; p++)
            {
                var page = await rs.GetPageAsync(p, default);
                if (page.IsLast || page.RowCount == 0)
                {
                    break;
                }
            }
        }

        var cursor = await plans.GetLastCursorAsync(session, default);
        Assert.NotNull(cursor);
        Assert.Equal(13, cursor!.SqlId.Length);

        var plan = await plans.GetCursorPlanAsync(session, cursor, "ALLSTATS LAST", default);
        Assert.Contains("GATHER_PLAN_STATISTICS", plan.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.Steps, s => s.ActualRows is not null); // 힌트 사본은 A-Rows 가 채워진다
    }

    [Fact]
    public async Task Optimizer_trace_parses_only_and_my_trace_path_is_known_but_unreadable_on_11g()
    {
        await using var session = await OpenAsync();
        await RunAsync(session, "ALTER SESSION SET EVENTS '10053 trace name context forever, level 1'", StatementKind.Ddl, "ALTER SESSION");
        try
        {
            // 앱의 OptimizerTraceRunner.ParseBlock 과 같은 블록: 파싱만 하고 실행하지 않는다.
            const string block = "DECLARE\n  c INTEGER := DBMS_SQL.OPEN_CURSOR;\nBEGIN\n  DBMS_SQL.PARSE(c, :oranda_sql, DBMS_SQL.NATIVE);\n  DBMS_SQL.CLOSE_CURSOR(c);\n"
                + "EXCEPTION\n  WHEN OTHERS THEN\n    IF DBMS_SQL.IS_OPEN(c) THEN\n      DBMS_SQL.CLOSE_CURSOR(c);\n    END IF;\n    RAISE;\nEND;";
            var marker = "ORANDA_10053_" + Guid.NewGuid().ToString("N")[..8];
            await RunAsync(session, block, StatementKind.PlSql, "DECLARE",
                new Dictionary<string, BindValue> { ["oranda_sql"] = new(BindDataType.Varchar2, $"/* {marker} */ SELECT COUNT(*) FROM dual WHERE 1 = :x") });

            // 파싱만 했으니 실행 횟수는 0 이다.
            var check = await RunAsync(session, $"SELECT NVL(SUM(executions), -1) FROM v$sql WHERE sql_text LIKE '/* {marker} */%'", StatementKind.Query, "SELECT");
            await using var rs = check.ResultSet!;
            var page = await rs.GetPageAsync(0, default);
            Assert.Equal(0m, Convert.ToDecimal(page.Columns[0][0], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await RunAsync(session, "ALTER SESSION SET EVENTS '10053 trace name context off'", StatementKind.Ddl, "ALTER SESSION");
        }

        var tuning = new OracleTuningService(_exec, new OracleExecutionPlanService(_exec), new LanguageService());
        var file = await tuning.GetTraceFileAsync(session, default(SessionKey), default);
        Assert.EndsWith(".trc", file.Path, StringComparison.Ordinal);
        Assert.False(file.Readable); // 11.2: 본문은 SQL 로 못 읽고 경로와 이유만
        Assert.Contains("12.2", file.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_monitor_report_is_blocked_without_the_tuning_pack()
    {
        await using var session = await OpenAsync(LicensePack.Diagnostics);
        IExecutionPlanService plans = new OracleExecutionPlanService(_exec);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => plans.GetSqlMonitorReportAsync(session, new CursorRef("0000000000000"), html: true, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => plans.GetSqlMonitorReportAsync(session, new CursorRef("0000000000000"), html: false, default));
    }
}
