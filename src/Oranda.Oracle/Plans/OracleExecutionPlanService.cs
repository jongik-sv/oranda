using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Plans;
using Oranda.Core.Plans.Providers;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Oracle.Plans;

/// <summary>
/// 실행 계획(EXPLAIN PLAN·DISPLAY_CURSOR, 위험 단계 표시). 소유: TSK-07-01.
/// <para>예상 계획: <c>EXPLAIN PLAN</c> 문은 <see cref="IExecutionService"/>(정책 관문)로 보내고, 결과는 카탈로그 <c>plan.*</c>(모두 <c>license: none</c>)로 읽는다.
/// 실제 계획: <c>DBMS_XPLAN.DISPLAY_CURSOR</c>(팩 불필요). DISPLAY_AWR·ASH 는 쓰지 않는다.
/// 팩이 필요한 SQL 모니터 보고서는 <see cref="GetSqlMonitorReportAsync"/> 가 보유 선언·서버 설정과 대조해 모자라면 보내지 않는다.</para>
/// </summary>
[OrandaService(typeof(IExecutionPlanService), Dbms = Dbms.Oracle)]
internal sealed class OracleExecutionPlanService : IExecutionPlanService
{
    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    private readonly IExecutionService _execution;

    public OracleExecutionPlanService(IExecutionService execution)
    {
        _execution = execution;
    }

    public async Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmt);
        if (stmt.Kind is StatementKind.PlSql or StatementKind.SqlPlus or StatementKind.TransactionControl)
        {
            throw new ArgumentException($"{stmt.Kind} 문장은 예상 계획을 볼 수 없습니다.", nameof(stmt));
        }

        var statementId = "ORANDA_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var wrapped = new SqlStatement($"EXPLAIN PLAN SET STATEMENT_ID = '{statementId}' FOR {stmt.Text}", stmt.Range, StatementKind.Other)
        {
            Verb = "EXPLAIN",
            BindNames = stmt.BindNames,
            SubstitutionNames = stmt.SubstitutionNames,
        };

        // EXPLAIN PLAN 은 값 없이 분석만 하므로 바인드는 null 로 채운다.
        var binds = stmt.BindNames.ToDictionary(n => n, _ => new BindValue(BindDataType.Varchar2, null), StringComparer.OrdinalIgnoreCase);
        var result = await _execution.ExecuteAsync(s, wrapped, binds, new ExecutionOptions { Source = HistorySource.Editor, SkipHistory = true }, ct);
        switch (result.Status)
        {
            case ExecutionStatus.Denied:
                throw new UnauthorizedAccessException(result.Policy?.Message ?? "정책에 따라 예상 계획을 만들 수 없습니다.");
            case ExecutionStatus.Cancelled:
                throw new OperationCanceledException("예상 계획 생성이 취소되었습니다.");
            case ExecutionStatus.Failed:
                throw new InvalidOperationException(result.Error switch
                {
                    { } e when e.Kind == DbErrorKind.ReadOnly || e.Message.Contains("ORA-01456", StringComparison.Ordinal) => $"{e.Code}: 읽기 전용 접속은 서버가 EXPLAIN PLAN 의 PLAN_TABLE 기록을 막습니다. 실제 계획(DISPLAY_CURSOR)을 쓰세요. {e.Message}",
                    { } e => $"{e.Code}: {e.Message}",
                    _ => "예상 계획 생성에 실패했습니다.",
                });
        }

        // PLAN_TABLE 행은 지우지 않는다: DB 접근 통제 도구가 DELETE 를 DML 로 보고 결재를 요구한다.
        // 세션 전용 임시 테이블이라 세션이 끝나면 사라지고, statement_id 가 매번 새 값이라 다음 조회에 섞이지 않는다.
        {
            var names = new List<string>();
            var steps = await QueryAsync(s, "plan.explain_steps", c => AddText(c, "stmt_id", statementId), ct, names);
            var text = await QueryAsync(s, "plan.explain_text", c =>
            {
                AddText(c, "stmt_id", statementId);
                AddText(c, "format", "TYPICAL");
            }, ct);

            var planSteps = PlanRows.BuildSteps(PlanRows.ReadPlanTableRows(names, steps));
            // plan_table.plan_id 는 EXPLAIN 마다 새로 매기는 번호라 계획 해시가 아니다. DBMS_XPLAN 글의 「Plan hash value」를 쓴다(PLAN-04 비교).
            var planText = PlanRows.JoinLines(text);
            return new ExecutionPlan(planSteps, planText)
            {
                PlanHashValue = ExecutionPlanText.ParsePlanHashValue(planText),
            };
        }
    }

    public async Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);

        var fmt = string.IsNullOrWhiteSpace(format) ? "TYPICAL" : format.Trim();
        var rows = await QueryAsync(s, "plan.cursor_steps", c =>
        {
            AddText(c, "sql_id", cursor.SqlId);
            AddInt(c, "inst_id", cursor.InstanceId);
            AddInt(c, "child", cursor.ChildNumber);
        }, ct);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"공유 풀에서 계획을 찾을 수 없습니다(밀려났을 수 있습니다): {cursor.SqlId}");
        }

        // 자식이 여럿이면 첫 (인스턴스, 자식) 의 단계만 쓴다.
        var inst = Int(rows[0][0]);
        var child = Int(rows[0][1]);
        var first = rows.Where(r => Int(r[0]) == inst && Int(r[1]) == child).ToList();
        var text = await QueryAsync(s, "plan.cursor_text", c =>
        {
            AddText(c, "sql_id", cursor.SqlId);
            AddInt(c, "child", child);
            AddText(c, "format", fmt);
        }, ct);

        var steps = PlanRows.BuildSteps(first.Select(r => new RawPlanStep(
            Int(r[2]) ?? 0, Int(r[3]), Str(r[5]) ?? string.Empty, Str(r[6]), Str(r[7]), Str(r[8]),
            Long(r[9]), Long(r[10]), Long(r[11]), Long(r[12]), Long(r[13]), Str(r[14]), Str(r[15]))));
        return new ExecutionPlan(steps, PlanRows.JoinLines(text))
        {
            Cursor = new CursorRef(cursor.SqlId, child, cursor.InstanceId ?? inst),
            PlanHashValue = Long(first[0][16]),
        };
    }

    /// <summary>SQL 모니터 보고서(Tuning Pack). 보유 선언·서버 설정이 모자라면 드라이버로 보내지 않고 <see cref="UnauthorizedAccessException"/>.</summary>
    internal static Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, CancellationToken ct) =>
        GetSqlMonitorReportAsync(s, cursor, html: false, ct);

    /// <summary>SQL 모니터 보고서 TEXT·HTML(계획 창 「보고서」 탭, TSK-06-04). 관문은 위와 같다.</summary>
    internal static async Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, bool html, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);

        var rows = await QueryAsync(s, html ? "plan.monitor_report_html" : "plan.monitor_report", c => AddText(c, "sql_id", cursor.SqlId), ct);
        return rows.Count > 0 ? Str(rows[0][0]) ?? string.Empty : string.Empty;
    }

    Task<string> IExecutionPlanService.GetSqlMonitorReportAsync(ISession s, CursorRef cursor, bool html, CancellationToken ct) =>
        GetSqlMonitorReportAsync(s, cursor, html, ct);

    /// <summary>직전 문장의 커서. 앞 문장 뒤에 다른 문장을 보내지 않은 채 불러야 한다(DISPLAY_CURSOR(NULL) 은 세션의 prev_sql_id 를 본다).</summary>
    public async Task<CursorRef?> GetLastCursorAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var rows = await QueryAsync(s, "plan.last_cursor_text", _ => { }, ct);
        return ExecutionPlanText.ParseCursor(PlanRows.JoinLines(rows));
    }

    private static async Task<List<object?[]>> QueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct, List<string>? columnNames = null)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = Catalog.Value.Get(id, s.Server);
        OracleMonitorService.EnsureLicense(s, query.Header.License);

        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLOBFetchSize = -1;
            cmd.InitialLONGFetchSize = -1; // PLAN_TABLE.other 는 LONG(p.* 로 읽는다)
            cmd.CommandText = query.Sql;
            bind(cmd);

            var rows = new List<object?[]>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (columnNames is not null)
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    columnNames.Add(reader.GetName(i));
                }
            }

            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void AddText(OracleCommand c, string name, string value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Varchar2) { Value = value });

    private static void AddInt(OracleCommand c, string name, int? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int32) { Value = value is { } v ? v : DBNull.Value });
}
