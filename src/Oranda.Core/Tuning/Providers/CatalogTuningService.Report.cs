using Oranda.Core.Catalog.Providers;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Tuning.Providers.TuningRows;

namespace Oranda.Core.Tuning.Providers;

/// <summary>SQL 튜닝 HTML 보고서가 더 읽는 값(TSK-06-07, TN-17). 카탈로그 <c>tuning.sql_stats</c>·<c>tuning.report_env</c>, 모두 SELECT·팩 없음.</summary>
public abstract partial class CatalogTuningService
{
    public async Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.sql_stats", SqlIdBinds(sqlId), TuningQueryOptions, ct)).Rows;
        return rows.Count == 0 ? null : ToSqlStats(rows[0]);
    }

    public async Task<IReadOnlyList<TuningEnvRow>> GetTuningEnvironmentAsync(ISession s, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.report_env", CatalogBinds.None, TuningQueryOptions, ct)).Rows;
        return ToEnvRows(rows.Select(r => (Str(r[0]), Str(r[1]), Str(r[2]))));
    }
}
