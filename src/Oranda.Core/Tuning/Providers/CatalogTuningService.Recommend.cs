using Oranda.Core.Catalog.Providers;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Tuning.Providers.TuningRows;

namespace Oranda.Core.Tuning.Providers;

/// <summary>
/// 튜닝 추천 후보 조회(TSK-14-17, D-051). 카탈로그 <c>tuning.recommend_candidates</c>·<c>tuning.recommend_literals</c>·<c>tuning.recommend_plan</c>,
/// 모두 SELECT·팩 없음. 부하 제한: 후보는 gv$sqlstats 상위 N 줄만, 파싱 스키마는 그 줄에만, 계획은 sql_id·plan_hash 하나씩.
/// 리터럴 묶음이 없는 DBMS(<see cref="Connection.DbmsFeature.LiteralGroups"/>)는 화면이 <see cref="TuningCandidateQuery.IncludeLiteralGroups"/> 를 끈다.
/// </summary>
public abstract partial class CatalogTuningService
{
    /// <summary>한 번에 읽는 후보 줄 상한.</summary>
    public const int MaxCandidates = 500;

    public async Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var ids = SqlIdList(query.SqlIds);
        if (query.SqlIds is not null && ids is null)
        {
            return new TuningCandidateSet([], 0);
        }

        var rows = (await QueryAsync(s, "tuning.recommend_candidates",
        [
            CatalogBinds.Int("top", Math.Clamp(query.Top, 1, MaxCandidates)),
            CatalogBinds.Long("min_executions", Math.Max(0, query.MinExecutions)),
            CatalogBinds.Trimmed("sql_ids", ids),
        ], TuningQueryOptions, ct)).Rows;

        var samples = rows.Select(ToCandidate).ToList();
        var total = rows.Count > 0 && ids is null ? Long(rows[0][18]) ?? 0 : 0; // sql_id 로 좁힌 조회는 비중 분모가 뜻이 없다
        IReadOnlyList<LiteralSqlGroup> groups = [];
        if (query.IncludeLiteralGroups && ids is null)
        {
            var g = (await QueryAsync(s, "tuning.recommend_literals",
            [
                CatalogBinds.Int("min_count", TuningRecommendationRules.LiteralGroupMinCount),
                CatalogBinds.Int("top", TuningRecommendationRules.LiteralGroupMaxCount),
            ], TuningQueryOptions, ct)).Rows;
            groups = [.. g.Select(ToLiteralGroup)];
        }

        return new TuningCandidateSet(samples, total) { LiteralGroups = groups };
    }

    public async Task<IReadOnlyList<PlanFindingLine>> GetPlanFindingsAsync(ISession s, int? instanceId, string sqlId, long planHashValue, CancellationToken ct)
    {
        var id = CheckSqlId(sqlId);
        var rows = (await QueryAsync(s, "tuning.recommend_plan",
        [
            CatalogBinds.Int("inst_id", instanceId ?? 1),
            CatalogBinds.Trimmed("sql_id", id),
            CatalogBinds.Long("plan_hash", planHashValue),
        ], TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(ToPlanFinding)];
    }
}
