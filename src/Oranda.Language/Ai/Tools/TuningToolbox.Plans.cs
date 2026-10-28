using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;
using Oranda.Language.Ai.References;
using Oranda.Core.Sessions;

namespace Oranda.Language.Ai.Tools;

public sealed partial class TuningToolbox
{
    /// <summary><c>get_cursor_plan</c>: 공유 풀에 있는 실제 계획(A-Rows·Starts 포함).</summary>
    public async Task<PlanDescription> GetCursorPlanAsync(ISession s, CursorRef cursor, string? scopeSchema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);
        if (!await CursorVisibleAsync(s, cursor.SqlId, scopeSchema, ct).ConfigureAwait(false))
        {
            throw new AiToolException(CursorNotVisible);
        }

        var plan = await plans.GetCursorPlanAsync(s, cursor, "ALLSTATS LAST", ct).ConfigureAwait(false);
        return Describe(plan, cursor);
    }

    private const string CursorNotVisible = "이 sql_id 의 커서를 이 실행 범위(현재 스키마)에서 찾지 못했습니다. 공유 풀에서 밀려났거나 다른 스키마가 파싱한 문장일 수 있습니다.";

    /// <summary>
    /// 공유 풀에 커서가 있고 파싱 스키마가 실행 범위의 스키마(<paramref name="scopeSchema"/>)인가. 같은 글을 다른 사용자가 파싱한 커서의
    /// 계획·통계를 내지 않기 위한 검사다(<paramref name="scopeSchema"/> 가 null 이면 존재만 본다).
    /// 한계: 기존 후보 조회는 sql_id 당 파싱 스키마를 하나(MAX)만 주므로 자식 커서마다 스키마가 다르면 완전하지 않다.
    /// </summary>
    internal async Task<bool> CursorVisibleAsync(ISession s, string sqlId, string? scopeSchema, CancellationToken ct)
    {
        var set = await tuning.GetTuningCandidatesAsync(
            s, new TuningCandidateQuery { Top = 1, MinExecutions = 0, SqlIds = [sqlId], IncludeLiteralGroups = false }, ct).ConfigureAwait(false);
        var row = set.Rows.FirstOrDefault(r => string.Equals(r.SqlId, sqlId, StringComparison.OrdinalIgnoreCase));
        return row is not null && (scopeSchema is null || string.Equals(row.ParsingSchema, scopeSchema, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <see cref="CursorVisibleAsync"/> 와 같되 V$SQL 조회 권한이 없으면(ORA-00942·ORA-01031) 예외 대신 null 을 돌려준다.
    /// 호출자는 null 을 「볼 수 없음」으로 다뤄야 한다(sql_id 를 알리지 않는 fail-closed). 다른 오류는 그대로 던진다.
    /// </summary>
    internal async Task<bool?> TryCursorVisibleAsync(ISession s, string sqlId, string? scopeSchema, CancellationToken ct)
    {
        try
        {
            return await CursorVisibleAsync(s, sqlId, scopeSchema, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested && IsNoPrivilege(ex))
        {
            return null;
        }
    }

    private static bool IsNoPrivilege(Exception ex) =>
        ex.Message.Contains("ORA-00942", StringComparison.Ordinal) || ex.Message.Contains("ORA-01031", StringComparison.Ordinal);

    /// <summary>
    /// <c>explain_plan</c>: 예상 계획. 먼저 접속의 실효 허용(<paramref name="allowExplainPlan"/>: 개발 켬·운영 끔)과
    /// 문장 모양(언어 서비스로 확실히 분류된 SELECT/WITH 한 문장, FOR UPDATE·치환 변수 없음, 바인드는 허용)을 검사한다.
    /// 거부 메시지와 서버가 읽기 전용 접속이라 막는 경우(ORA-01456)는 <c>get_cursor_plan</c> 으로 안내한다.
    /// </summary>
    public async Task<PlanDescription> ExplainPlanAsync(ISession s, string sql, bool allowExplainPlan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!allowExplainPlan)
        {
            throw new AiToolException(CursorPlanHint("이 접속은 EXPLAIN PLAN 을 허용하지 않습니다."));
        }

        var statement = ExplainableStatement(sql);
        try
        {
            var plan = await plans.ExplainAsync(s, statement, ct).ConfigureAwait(false);
            return Describe(plan, null);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ORA-01456", StringComparison.Ordinal))
        {
            throw new AiToolException(CursorPlanHint("서버가 읽기 전용 접속의 EXPLAIN PLAN 을 막습니다(ORA-01456)."));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AiToolException(CursorPlanHint(ex.Message));
        }
    }

    private static string CursorPlanHint(string reason) =>
        reason + " 이미 실행된 문장이면 get_cursor_plan 에 sql_id 를 넘겨 실제 계획(DISPLAY_CURSOR)을 확인하십시오.";

    /// <summary>예상 계획을 낼 문장: 한 문장, 확실한 SELECT/WITH 질의, FOR UPDATE·치환 변수 없음. 바인드는 값 없이 분석되므로 허용.</summary>
    private SqlStatement ExplainableStatement(string sql) => SingleSelect(sql, allowBinds: true, "explain_plan");

    /// <summary>한 문장, 확실한 SELECT/WITH 질의, FOR UPDATE·치환 변수 없음(<paramref name="allowBinds"/> 가 거짓이면 바인드도 없음).</summary>
    private SqlStatement SingleSelect(string sql, bool allowBinds, string tool)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new AiToolException("분석할 문장이 비었습니다.");
        }

        var stmts = language.Split(sql, SplitOptions.Default);
        if (stmts.Count != 1)
        {
            throw new AiToolException($"{tool} 은 문장 하나만 받습니다(받은 문장 {stmts.Count.ToString(CultureInfo.InvariantCulture)}개).");
        }

        var stmt = stmts[0];
        if (stmt.Kind != StatementKind.Query || stmt.IsClassificationUncertain || stmt.Verb is not ("SELECT" or "WITH"))
        {
            throw new AiToolException($"{tool} 은 SELECT(WITH) 질의만 받습니다: {stmt.Verb ?? stmt.Kind.ToString()}");
        }

        // 주석·줄바꿈으로 갈라 쓴 FOR UPDATE 도 잡도록 글자가 아니라 토큰으로 판정한다.
        if (StatementReferenceExtractor.Extract(stmt.Text).HasLockingClause)
        {
            throw new AiToolException("SELECT … FOR UPDATE 는 분석하지 않습니다.");
        }

        if (stmt.SubstitutionNames.Count > 0)
        {
            throw new AiToolException("치환 변수(&)가 있는 문장은 분석하지 않습니다.");
        }

        if (!allowBinds && stmt.BindNames.Count > 0)
        {
            throw new AiToolException($"{tool} 은 바인드 변수가 있는 문장을 실행하지 않습니다. 값을 글자로 넣어 주십시오.");
        }

        return stmt;
    }

    /// <summary><c>get_child_cursors</c>: 자식 커서와 자식이 갈라진 사유.</summary>
    public async Task<IReadOnlyList<ChildCursorDescription>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        var children = await tuning.GetChildCursorsAsync(s, sqlId, ct).ConfigureAwait(false);
        var reasons = (await tuning.GetSharedCursorReasonsAsync(s, sqlId, ct).ConfigureAwait(false))
            .ToLookup(r => r.ChildNumber, r => r.Reason);
        return [.. children.Select(c => new ChildCursorDescription(
            c.ChildNumber, c.PlanHashValue, c.IsBindSensitive, c.IsBindAware, c.IsShareable, c.Executions, c.BufferGets,
            c.ElapsedMicros / 1000.0, [.. reasons[c.ChildNumber]]))];
    }

    /// <summary><c>get_sql_stats</c>: 누적 합을 실행당 값으로 바꾼다.</summary>
    public async Task<SqlStatsDescription> GetSqlStatsAsync(ISession s, string sqlId, string? scopeSchema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        var st = await CursorVisibleAsync(s, sqlId, scopeSchema, ct).ConfigureAwait(false)
            ? await tuning.GetSqlStatsAsync(s, sqlId, ct).ConfigureAwait(false)
            : null; // 이 범위에서 보이지 않는 커서는 없는 것과 같이 답한다(존재 여부를 알리지 않는다)
        if (st is null)
        {
            return new SqlStatsDescription(false, 0, 0, null, null, null, null, null);
        }

        if (st.Executions <= 0)
        {
            return new SqlStatsDescription(true, st.Children, 0, null, null, null, null, null);
        }

        double per = st.Executions;
        return new SqlStatsDescription(
            true, st.Children, st.Executions, st.ElapsedMicros / 1000.0 / per, st.CpuMicros / 1000.0 / per, st.BufferGets / per,
            st.DiskReads / per, st.RowsProcessed / per);
    }

    private static PlanDescription Describe(ExecutionPlan plan, CursorRef? asked)
    {
        var cursor = plan.Cursor ?? asked;
        return new PlanDescription(
            cursor?.SqlId,
            cursor?.ChildNumber,
            plan.PlanHashValue,
            [.. plan.Steps.Select(x => new PlanStepDescription(
                x.Id,
                x.Depth,
                string.IsNullOrEmpty(x.Options) ? x.Operation : x.Operation + " " + x.Options,
                x.ObjectName is null ? null : (x.ObjectOwner is null ? x.ObjectName : x.ObjectOwner + "." + x.ObjectName),
                x.Cost,
                x.EstimatedRows,
                x.ActualRows,
                x.Starts,
                x.AccessPredicates,
                x.FilterPredicates,
                x.Risk == PlanRisk.None ? null : x.Risk.ToString()))]);
    }
}
