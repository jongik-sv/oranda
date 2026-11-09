using Oranda.Core.Catalog;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Plans.Providers;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Plans;

/// <summary>
/// Oracle 실행 계획: DBMS 공통 본문 <see cref="CatalogExecutionPlanService"/> 에 Oracle 질의 포트(ODP.NET)와 고리를 붙여 등록한다. 소유: TSK-07-01.
/// 계획 해시·직전 커서는 DBMS_XPLAN 글(「Plan hash value」·「SQL_ID … child number …」)에서 읽는다.
/// </summary>
[OrandaService(typeof(IExecutionPlanService), Dbms = Dbms.Oracle)]
internal sealed class OracleExecutionPlanService : CatalogExecutionPlanService
{
    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    public OracleExecutionPlanService(IExecutionService execution)
        : base(execution)
    {
    }

    protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
        OracleCatalogQuery.QueryAsync(Catalog, s, id, binds, options, ct);

    /// <summary>DISPLAY_CURSOR(NULL) 글 머리의 SQL_ID·child.</summary>
    protected override async Task<CursorRef?> ReadLastCursorAsync(ISession s, CancellationToken ct)
    {
        var rows = await QueryAsync(s, "plan.last_cursor_text", CatalogBinds.None, PlanQueryOptions, ct);
        return ExecutionPlanText.ParseCursor(PlanRows.JoinLines(rows.Rows));
    }

    /// <summary>plan_table.plan_id 는 EXPLAIN 마다 새로 매기는 번호라 계획 해시가 아니다. DBMS_XPLAN 글의 「Plan hash value」를 쓴다(PLAN-04 비교).</summary>
    protected override long? ExplainPlanHash(string planText) => ExecutionPlanText.ParsePlanHashValue(planText);
}
