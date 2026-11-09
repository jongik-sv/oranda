using Oranda.Core.Catalog.Providers;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Plans.Providers;
using Oranda.Core.Sessions;
using Oranda.Tibero.Catalog;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Tibero.Plans;

/// <summary>
/// Tibero 실행 계획: DBMS 공통 본문 <see cref="CatalogExecutionPlanService"/> 에 Tibero 질의 포트(<see cref="TiberoCatalogPort"/>)와 고리를 붙여 등록한다. 소유: P1.
/// 직전 커서는 DBMS_XPLAN 글이 아니라 <c>tuning.prev_cursor</c> 열에서 읽는다(Tibero 글 머리는 「SQL ID : …」 이고 자식 번호 줄이 없다).
/// </summary>
// 주의: Explain 이 실행 디스패처를 거치므로 E4(Tibero 실행 서비스 등록)가 main 에 들어가기 전에는 이 서비스를 main 에 올리지 않는다.
[OrandaService(typeof(IExecutionPlanService), Dbms = Dbms.Tibero)]
internal sealed class TiberoExecutionPlanService : CatalogExecutionPlanService
{
    public TiberoExecutionPlanService(IExecutionService execution)
        : base(execution)
    {
    }

    protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
        TiberoCatalogPort.QueryAsync(s, id, binds, options, ct);

    protected override RawPlanStep NormalizeStep(RawPlanStep raw) => SplitOperation(raw);

    /// <summary>tuning.prev_cursor(prev_sql_id, prev_child_number, plan_hash_value) → 커서(자식 번호 null, ParseLastCursor 주석). 행이 없거나 sql_id 가 없으면 null.</summary>
    protected override async Task<CursorRef?> ReadLastCursorAsync(ISession s, CancellationToken ct)
    {
        var rows = await QueryAsync(s, "tuning.prev_cursor", CatalogBinds.None, CatalogQueryOptions.Default, ct);
        return ParseLastCursor(rows);
    }

    // ExplainPlanHash 는 재정의하지 않는다(기본 = null): Tibero PLAN_TABLE 은 계획 해시를 주지 않고 DISPLAY 글에도 「Plan hash value」 가 없다(Q4, 결정42).

    /// <summary>Tibero 에는 HTML 보고서가 없다(<c>DbmsFeature.SqlMonitorReportHtml = false</c> 가 화면에서 막는다). 서비스는 마지막 방어선.</summary>
    public override Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, bool html, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);
        if (html)
        {
            throw new NotSupportedException("Tibero 는 SQL 모니터 HTML 보고서를 지원하지 않습니다(텍스트만).");
        }

        return base.GetSqlMonitorReportAsync(s, cursor, html, ct);
    }

    /// <summary>
    /// 행 하나 → 커서. 자식 번호는 항상 null 이다(p1-prep §1): PREV_CHILD_NUMBER 가 V$SQL_PLAN.CHILD_NUMBER 와 같은 번호인지 아직 재지 않았다(M6 이 정한다).
    /// null 이면 계획 조회가 전체 자식 중 첫 자식을 쓴다. sql_id 만 읽는다.
    /// </summary>
    internal static CursorRef? ParseLastCursor(CatalogRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Rows.Count == 0)
        {
            return null;
        }

        var row = rows.Rows[0];
        var sqlId = Str(row[0])?.Trim();
        if (string.IsNullOrEmpty(sqlId))
        {
            return null;
        }

        return new CursorRef(sqlId);
    }

    /// <summary>
    /// Tibero OPERATION 은 방식을 함께 담는다(「TABLE ACCESS (FULL)」·「index (range scan)」). 첫 「 (」 앞 = Operation, 괄호 안(앞뒤 공백 뗌) = Options(자세한 규칙은 TrySplit).
    /// 괄호가 없으면 그대로. 대소문자 그대로(ClassifyRisk 가 무시한다). 이미 Options 가 있으면(Oracle 식 행) 그대로 둔다.
    /// </summary>
    internal static RawPlanStep SplitOperation(RawPlanStep raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!string.IsNullOrWhiteSpace(raw.Options))
        {
            return raw;
        }

        return TrySplit(raw.Operation, out var operation, out var options)
            ? raw with { Operation = operation, Options = options }
            : raw;
    }

    /// <summary>
    /// 「이름 (방식)」 으로 끝나는 글을 나눈다(tuning/recommend_plan.sql 과 같이 첫 「 (」 에서). 첫 「 (」 앞(끝 공백 뗌) = 이름, 그 「(」 부터 끝까지 괄호가 균형이고
    /// 글이 「)」 로 끝나면 「(」 와 마지막 「)」 사이(앞뒤 공백 뗌) = 방식. 중첩·뒤따르는 괄호 묶음은 방식 안에 그대로 남는다(「a (full) (x)」 → 「a」/「full) (x」, 「full」 이 있으니 위험 판정은 산다).
    /// 「FOO(bar)」(앞 공백 없음)·이름 없음·괄호가 안 맞음 → 나누지 않는다.
    /// </summary>
    internal static bool TrySplit(string? text, out string operation, out string options)
    {
        operation = text ?? string.Empty;
        options = string.Empty;
        var t = operation.TrimEnd();
        if (t.Length == 0 || t[^1] != ')')
        {
            return false;
        }

        var open = t.IndexOf(" (", StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        var name = t[..open].TrimEnd();
        if (name.Length == 0)
        {
            return false;
        }

        var depth = 0;
        for (var i = open + 1; i < t.Length; i++)
        {
            if (t[i] == '(')
            {
                depth++;
            }
            else if (t[i] == ')' && --depth < 0)
            {
                return false;
            }
        }

        if (depth != 0)
        {
            return false;
        }

        operation = name;
        options = t[(open + 2)..^1].Trim();
        return true;
    }
}
