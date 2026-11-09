using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.Flashback;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.Tuning.Trace;
using Oranda.App.Tests.Features.PlsqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;

namespace Oranda.App.Tests.Features;

/// <summary>
/// 특성 시험(U5 선행): App 이 <c>EmbeddedCatalog</c> 로 만드는 Oracle SQL 문자열을 글자 그대로 고정한다.
/// App 의 카탈로그 접근을 <c>DbmsServices.CatalogText</c> 로 옮긴 뒤에도 이 시험이 그대로 통과해야 Oracle 동작이 같다.
/// 이미 같은 SQL 을 고정하는 시험은 중복하지 않는다: <c>app.locked_row_select</c>(LockMonitorTests), <c>app.ddl_locks_grant</c>(LockMonitorTests),
/// <c>app.kill_session</c> rac=false(LockMonitorTests), <c>app.disconnect_session</c> 두 조합(SessionMonitorTests).
/// </summary>
public sealed class EmbeddedCatalogCharacterizationTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private const string BodySource = "PACKAGE BODY pk AS\n  PROCEDURE a IS BEGIN\n    NULL;\n  END;\nEND pk;\n";

    private static readonly ServerInfo Server = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, false, false, false, false));

    // ---------- PlanStatsRerunner (app.statistics_level) ----------

    [Fact]
    public void Plan_statistics_level_sql_is_alter_session()
    {
        Assert.Equal("ALTER SESSION SET STATISTICS_LEVEL = ALL", PlanStatsRerunner.StatisticsLevelSql);
        Assert.Equal("ALTER SESSION SET STATISTICS_LEVEL = ALL", PlanStatsRerunner.StatisticsLevelStatement().Text);
    }

    // ---------- OptimizerTraceRunner (app.trace_event_on / trace_event_off / trace_parse) ----------

    [Fact]
    public void Optimizer_trace_event_sql_is_fixed()
    {
        Assert.Equal("ALTER SESSION SET EVENTS '10053 trace name context forever, level 1'", OptimizerTraceRunner.EventOnSql);
        Assert.Equal("ALTER SESSION SET EVENTS '10053 trace name context off'", OptimizerTraceRunner.EventOffSql);
    }

    [Fact]
    public void Optimizer_trace_parse_block_is_fixed()
    {
        const string expected =
            "DECLARE\n" +
            "  c INTEGER := DBMS_SQL.OPEN_CURSOR;\n" +
            "BEGIN\n" +
            "  DBMS_SQL.PARSE(c, :oranda_sql, DBMS_SQL.NATIVE);\n" +
            "  DBMS_SQL.CLOSE_CURSOR(c);\n" +
            "EXCEPTION\n" +
            "  WHEN OTHERS THEN\n" +
            "    IF DBMS_SQL.IS_OPEN(c) THEN\n" +
            "      DBMS_SQL.CLOSE_CURSOR(c);\n" +
            "    END IF;\n" +
            "    RAISE;\n" +
            "END;";
        Assert.Equal(expected, OptimizerTraceRunner.ParseBlock.ReplaceLineEndings("\n"));
    }

    // ---------- SessionMonitorCommands (app.disconnect_session): 기존 시험에 없는 두 조합 ----------

    [Theory]
    [InlineData(true, true, "ALTER SYSTEM DISCONNECT SESSION '12,345,@2' IMMEDIATE")]
    [InlineData(false, false, "ALTER SYSTEM DISCONNECT SESSION '12,345' POST_TRANSACTION")]
    public void Disconnect_statement_text_for_remaining_combinations(bool immediate, bool rac, string text)
    {
        Assert.Equal(text, SessionMonitorCommands.DisconnectStatement(new SessionKey(2, 12, 345), immediate, rac).Text);
    }

    // ---------- LockMonitorItems (app.kill_session rac=true, app.fk_index_check) ----------

    [Fact]
    public void Lock_kill_statement_with_rac_instance()
    {
        Assert.Equal("ALTER SYSTEM KILL SESSION '7,8,@1' IMMEDIATE", LockText.KillStatement(new SessionKey(1, 7, 8), rac: true));
    }

    [Fact]
    public void Lock_fk_index_check_with_owner()
    {
        const string expected =
            "SELECT c.constraint_name, cc.column_name, cc.position\n" +
            "  FROM all_constraints c\n" +
            "  JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name\n" +
            " WHERE c.constraint_type = 'R'\n" +
            "   AND c.owner = 'HR'\n" +
            "   AND c.table_name = 'EMP'\n" +
            "   AND NOT EXISTS (SELECT 1 FROM all_ind_columns ic\n" +
            "                    WHERE ic.table_owner = cc.owner AND ic.table_name = cc.table_name\n" +
            "                      AND ic.column_name = cc.column_name AND ic.column_position = cc.position)\n" +
            " ORDER BY c.constraint_name, cc.position";
        Assert.Equal(expected, LockHints.ForeignKeyIndexCheck("HR.EMP").ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Lock_fk_index_check_without_owner_and_with_quote()
    {
        const string expected =
            "SELECT c.constraint_name, cc.column_name, cc.position\n" +
            "  FROM all_constraints c\n" +
            "  JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name\n" +
            " WHERE c.constraint_type = 'R'\n" +
            "   AND c.table_name = 'O''N'\n" +
            "   AND NOT EXISTS (SELECT 1 FROM all_ind_columns ic\n" +
            "                    WHERE ic.table_owner = cc.owner AND ic.table_name = cc.table_name\n" +
            "                      AND ic.column_name = cc.column_name AND ic.column_position = cc.position)\n" +
            " ORDER BY c.constraint_name, cc.position";
        Assert.Equal(expected, LockHints.ForeignKeyIndexCheck("O'N").ReplaceLineEndings("\n"));
    }

    // ---------- FlashbackTrackSql (dbinfo.flashback_versions) ----------

    [Fact]
    public void Flashback_track_sql_default_recent_window()
    {
        const string expected =
            "-- SCOTT.EMP 의 변경 이력(키: EMPNO = 7369). 기간: 최근 1.5일(SYSDATE - 1.5)은 고쳐 다시 돌리거나 설정 > 결과 그리드 「변경 추적 기간」에서 바꿀 수 있습니다. " +
            "undo 보존 기간과 표 정의 변경 시점까지만 보입니다. ORA-08180·ORA-30052 가 나면 기간을 줄이세요.\n" +
            "SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, EMPNO, ENAME\n" +
            "FROM SCOTT.EMP VERSIONS BETWEEN TIMESTAMP SYSDATE - 1.5 AND MAXVALUE\n" +
            "WHERE EMPNO = 7369\n" +
            "ORDER BY VERSIONS_STARTSCN DESC NULLS LAST";
        Assert.Equal(expected, FlashbackTrackSql.Build(Emp, ["EMPNO", "ENAME"], ["EMPNO"], [7369m]).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Flashback_track_sql_full_range_other_schema()
    {
        const string expected =
            "-- SCOTT.EMP 의 변경 이력(키: EMPNO = 7369). 기간: 가능한 전체 기간(SCN MINVALUE). DB 에 따라 ORA-08180 이 날 수 있으며 " +
            "설정 > 결과 그리드 「변경 추적 기간」에서 최근 N일로 바꿀 수 있습니다. undo 보존 기간과 표 정의 변경 시점까지만 보입니다. " +
            "다른 스키마 표는 FLASHBACK 권한이 있어야 합니다.\n" +
            "SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, EMPNO\n" +
            "FROM SCOTT.EMP VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE\n" +
            "WHERE EMPNO = 7369\n" +
            "ORDER BY VERSIONS_STARTSCN DESC NULLS LAST";
        Assert.Equal(expected, FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [7369m], "HR", days: null).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Flashback_track_sql_recent_window_after_last_ddl_with_composite_key()
    {
        const string expected =
            "-- SCOTT.EMP 의 변경 이력(키: A = 'O''Neil', B = 5). 기간: 최근 0.25일(SYSDATE - 0.25)은 고쳐 다시 돌리거나 설정 > 결과 그리드 「변경 추적 기간」에서 바꿀 수 있습니다. " +
            "undo 보존 기간과 표 정의 변경 시점까지만 보입니다. ORA-08180·ORA-30052 가 나면 기간을 줄이세요. " +
            "표 정의가 마지막으로 바뀐 시각(2026-10-27 09:30:15) 10초 뒤부터 읽습니다(그 앞은 ORA-01466).\n" +
            "SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, A, B\n" +
            "FROM SCOTT.EMP VERSIONS BETWEEN TIMESTAMP GREATEST(SYSDATE - 0.25, TO_DATE('2026-10-27 09:30:15', 'YYYY-MM-DD HH24:MI:SS') + INTERVAL '10' SECOND) AND MAXVALUE\n" +
            "WHERE A = 'O''Neil' AND B = 5\n" +
            "ORDER BY VERSIONS_STARTSCN DESC NULLS LAST";
        var sql = FlashbackTrackSql.Build(Emp, ["A", "B"], ["A", "B"], ["O'Neil", 5], null, 0.25m, new DateTime(2026, 10, 27, 9, 30, 15));
        Assert.Equal(expected, sql.ReplaceLineEndings("\n"));
    }

    // ---------- FlashbackCommands 가 FlashbackRunner 에 넘기는 카탈로그 (dbinfo.flashback_ddl, dbinfo.flashback_env) ----------
    // FlashbackCommandHandler.Catalog 는 private 이고 세션·실행 엔진 없이는 닿지 않는다. 같은 카탈로그(EmbeddedCatalog.Load)가 이 두 번호에 주는 글을 고정한다.

    [Fact]
    public void Flashback_runner_catalog_ddl_and_env_queries_are_fixed()
    {
        var catalog = EmbeddedCatalog.Load();
        const string ddl =
            "SELECT o.last_ddl_time\n" +
            "  FROM all_objects o\n" +
            " WHERE o.owner = :owner\n" +
            "   AND o.object_name = :name\n" +
            "   AND o.object_type = 'TABLE'";
        const string env =
            "SELECT p.value AS undo_retention\n" +
            "  FROM v$parameter p\n" +
            " WHERE p.name = 'undo_retention'";
        Assert.Equal(ddl, catalog.Get("dbinfo.flashback_ddl", Server).Sql.ReplaceLineEndings("\n"));
        Assert.Equal(env, catalog.Get(FlashbackEnvironment.CatalogId, Server).Sql.ReplaceLineEndings("\n"));
    }

    // ---------- PlsqlEditorViewModel (meta.object_users) ----------

    [Fact]
    public async Task Plsql_editor_object_users_query_is_fixed()
    {
        var exec = new CapturingExec(new PlsqlEditorTests.FakeExec());
        var vm = new PlsqlEditorViewModel(new PlsqlEditorTests.FakeMeta { Source = BodySource }, exec);
        await vm.OpenAsync(new PlsqlEditorTests.FakeSession(), new DbObjectRef("SCOTT", "PK", "PACKAGE BODY"));

        await vm.CompileAsync();

        var query = Assert.Single(exec.Queries);
        Assert.Equal("SELECT COUNT(DISTINCT sid) FROM v$access WHERE owner = 'SCOTT' AND object = 'PK' AND sid <> TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))", query);
    }

    /// <summary>질의로 분류된 문장의 글을 모으고 나머지는 안쪽 가짜 실행 엔진에 맡긴다.</summary>
    private sealed class CapturingExec(IExecutionService inner) : IExecutionService
    {
        public List<string> Queries { get; } = [];

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
            ExecutionOptions? options, CancellationToken ct)
        {
            if (stmt.Kind == StatementKind.Query)
            {
                Queries.Add(stmt.Text);
            }

            return inner.ExecuteAsync(s, stmt, binds, options, ct);
        }

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
            ExecutionOptions? options, CancellationToken ct) => inner.ExecuteScriptAsync(s, stmts, mode, options, ct);
    }
}
