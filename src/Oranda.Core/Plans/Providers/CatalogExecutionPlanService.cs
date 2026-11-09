using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Core.Plans.Providers;

/// <summary>
/// 실행 계획(EXPLAIN PLAN·DISPLAY_CURSOR, 위험 단계 표시)의 DBMS 공통 본문. 소유: TSK-07-01(본문), P0-b(기반 클래스로 옮김).
/// <para>예상 계획: <c>EXPLAIN PLAN</c> 문은 <see cref="IExecutionService"/>(정책 관문)로 보내고, 결과는 카탈로그 <c>plan.*</c>(모두 <c>license: none</c>)로 읽는다.
/// 실제 계획: <c>DBMS_XPLAN.DISPLAY_CURSOR</c>(팩 불필요). DISPLAY_AWR·ASH 는 쓰지 않는다.
/// 팩이 필요한 SQL 모니터 보고서는 <see cref="GetSqlMonitorReportAsync"/> 가 보유 선언·서버 설정과 대조해 모자라면 보내지 않는다(질의 포트의 라이선스 관문).</para>
/// <para>
/// DBMS 에 매이지 않는다(D2). 등록은 DBMS 쪽 하위 형식이 한다(<c>[OrandaService(typeof(IExecutionPlanService), Dbms = …)]</c>) — 이 형식 자체는 등록하지 않는다.
/// DBMS 차이는 질의 포트 <see cref="QueryAsync"/> 와 고리(<see cref="NormalizeStep"/>·<see cref="ReadLastCursorAsync"/>·<see cref="ExplainPlanHash"/>)로만 남긴다.
/// 카탈로그 계약(같은 id = 같은 열·바인드)이 이것을 가능하게 한다.
/// </para>
/// </summary>
public abstract class CatalogExecutionPlanService : IExecutionPlanService
{
    /// <summary>plan.* 읽기: PLAN_TABLE.other 는 LONG(p.* 로 읽는다)이라 끝까지 읽는다.</summary>
    protected static readonly CatalogQueryOptions PlanQueryOptions = new() { ReadLong = true };

    /// <summary>plan.explain_steps: PLAN_TABLE 판이 달라도 읽게 열 이름으로 읽는다(<see cref="PlanRows.ReadPlanTableRows"/>).</summary>
    private static readonly CatalogQueryOptions StepQueryOptions = PlanQueryOptions with { ColumnNames = true };

    private readonly IExecutionService _execution;

    protected CatalogExecutionPlanService(IExecutionService execution)
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
            var steps = await QueryAsync(s, "plan.explain_steps", [CatalogBinds.Text("stmt_id", statementId)], StepQueryOptions, ct);
            var text = await QueryAsync(s, "plan.explain_text",
                [CatalogBinds.Text("stmt_id", statementId), CatalogBinds.Text("format", "TYPICAL")], PlanQueryOptions, ct);

            var planSteps = PlanRows.BuildSteps(PlanRows.ReadPlanTableRows(steps.Names, steps.Rows).Select(NormalizeStep));
            // plan_table.plan_id 는 EXPLAIN 마다 새로 매기는 번호라 계획 해시가 아니다. DBMS 가 주는 계획 해시를 쓴다(PLAN-04 비교).
            var planText = PlanRows.JoinLines(text.Rows);
            return new ExecutionPlan(planSteps, planText)
            {
                PlanHashValue = ExplainPlanHash(planText),
            };
        }
    }

    public async Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);

        var fmt = string.IsNullOrWhiteSpace(format) ? "TYPICAL" : format.Trim();
        var rows = (await QueryAsync(s, "plan.cursor_steps",
            [CatalogBinds.Text("sql_id", cursor.SqlId), CatalogBinds.Int("inst_id", cursor.InstanceId), CatalogBinds.Int("child", cursor.ChildNumber)],
            PlanQueryOptions, ct)).Rows;
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"공유 풀에서 계획을 찾을 수 없습니다(밀려났을 수 있습니다): {cursor.SqlId}");
        }

        // 자식이 여럿이면 첫 (인스턴스, 자식) 의 단계만 쓴다.
        var inst = Int(rows[0][0]);
        var child = Int(rows[0][1]);
        var first = rows.Where(r => Int(r[0]) == inst && Int(r[1]) == child).ToList();
        var text = await QueryAsync(s, "plan.cursor_text",
            [CatalogBinds.Text("sql_id", cursor.SqlId), CatalogBinds.Int("child", child), CatalogBinds.Text("format", fmt)], PlanQueryOptions, ct);

        var steps = PlanRows.BuildSteps(first.Select(r => NormalizeStep(new RawPlanStep(
            Int(r[2]) ?? 0, Int(r[3]), Str(r[5]) ?? string.Empty, Str(r[6]), Str(r[7]), Str(r[8]),
            Long(r[9]), Long(r[10]), Long(r[11]), Long(r[12]), Long(r[13]), Str(r[14]), Str(r[15])))));
        return new ExecutionPlan(steps, PlanRows.JoinLines(text.Rows))
        {
            Cursor = new CursorRef(cursor.SqlId, child, cursor.InstanceId ?? inst),
            PlanHashValue = Long(first[0][16]),
        };
    }

    /// <summary>
    /// SQL 모니터 보고서 TEXT·HTML(Tuning Pack, 계획 창 「보고서」 탭 TSK-06-04). 보유 선언·서버 설정이 모자라면 드라이버로 보내지 않고 <see cref="UnauthorizedAccessException"/>.
    /// 형식이 없는 DBMS(<see cref="Connection.DbmsFeature.SqlMonitorReport"/>·<see cref="Connection.DbmsFeature.SqlMonitorReportHtml"/>)는 재정의해 <see cref="NotSupportedException"/>.
    /// </summary>
    public virtual async Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, bool html, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);

        var rows = (await QueryAsync(s, html ? "plan.monitor_report_html" : "plan.monitor_report",
            [CatalogBinds.Text("sql_id", cursor.SqlId)], PlanQueryOptions, ct)).Rows;
        return rows.Count > 0 ? Str(rows[0][0]) ?? string.Empty : string.Empty;
    }

    /// <summary>직전 문장의 커서. 앞 문장 뒤에 다른 문장을 보내지 않은 채 불러야 한다(DISPLAY_CURSOR(NULL) 은 세션의 prev_sql_id 를 본다).</summary>
    public async Task<CursorRef?> GetLastCursorAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return await ReadLastCursorAsync(s, ct);
    }

    /// <summary>
    /// 질의 포트: 카탈로그 <paramref name="id"/> 를 세션 연결로 읽는다. 공급자가 맡는 것 = 세션 형식 확인, 카탈로그 Get,
    /// 라이선스 관문(<see cref="CatalogLicenseGate"/>), 세션 명령 잠금(D-041), 드라이버 실패 보고. <paramref name="binds"/> 는 명령을 만든 뒤 읽는다(<see cref="CatalogBinds.Defer"/>).
    /// </summary>
    protected abstract Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct);

    /// <summary>
    /// 계획 한 줄을 위험 판정 전에 고친다(plan.explain_steps·plan.cursor_steps 둘 다). 기본 = 그대로.
    /// <see cref="PlanRows.ClassifyRisk"/> 는 Options 에서 FULL·CARTESIAN 을 찾으므로, OPERATION 에 방식까지 담는 DBMS 는 여기서 나눈다.
    /// </summary>
    protected virtual RawPlanStep NormalizeStep(RawPlanStep raw) => raw;

    /// <summary>직전 문장의 커서(<see cref="GetLastCursorAsync"/>). 모르면 null.</summary>
    protected abstract Task<CursorRef?> ReadLastCursorAsync(ISession s, CancellationToken ct);

    /// <summary>예상 계획의 계획 해시(<see cref="ExecutionPlan.PlanHashValue"/>). 기본 = 모름(null).</summary>
    protected virtual long? ExplainPlanHash(string planText) => null;
}
