using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Oracle.Tuning;

/// <summary>SQL 튜닝 HTML 보고서가 더 읽는 값(TSK-06-07, TN-17). 카탈로그 <c>tuning.sql_stats</c>·<c>tuning.report_env</c>, 모두 SELECT·팩 없음.</summary>
internal sealed partial class OracleTuningService
{
    public async Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.sql_stats", c => AddText(c, "sql_id", CheckSqlId(sqlId)), ct)).Rows;
        return rows.Count == 0 ? null : ToSqlStats(rows[0]);
    }

    public async Task<IReadOnlyList<TuningEnvRow>> GetTuningEnvironmentAsync(ISession s, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.report_env", _ => { }, ct)).Rows;
        return ToEnvRows(rows.Select(r => (Str(r[0]), Str(r[1]), Str(r[2]))));
    }

    /// <summary>gv$sql 합계 한 행. 자식 수가 0 이면(공유 풀에 없음) null.</summary>
    internal static SqlStatsRow? ToSqlStats(object?[] r)
    {
        if ((Int(r[0]) ?? 0) == 0)
        {
            return null;
        }

        return new SqlStatsRow(Int(r[0]) ?? 0, Int(r[1]) ?? 0, Long(r[2]) ?? 0, Long(r[3]) ?? 0, Long(r[4]) ?? 0, Long(r[5]) ?? 0, Long(r[6]) ?? 0, Long(r[7]) ?? 0)
        {
            ApplicationWaitMicros = Long(r[8]) ?? 0,
            ConcurrencyWaitMicros = Long(r[9]) ?? 0,
            ClusterWaitMicros = Long(r[10]) ?? 0,
            UserIoWaitMicros = Long(r[11]) ?? 0,
            ParseCalls = Long(r[12]) ?? 0,
            Fetches = Long(r[13]) ?? 0,
            ParsingSchema = Str(r[14]),
            Module = Str(r[15]),
            LastActiveTime = Date(r[16]),
            FirstLoadTime = Str(r[17]),
        };
    }

    /// <summary>환경 행(kind·name·value). 모르는 kind 는 버린다.</summary>
    internal static IReadOnlyList<TuningEnvRow> ToEnvRows(IEnumerable<(string? Kind, string? Name, string? Value)> rows)
    {
        var result = new List<TuningEnvRow>();
        foreach (var (kind, name, value) in rows)
        {
            TuningEnvKind? k = kind switch
            {
                "VERSION" => TuningEnvKind.Version,
                "PARAMETER" => TuningEnvKind.Parameter,
                "NLS" => TuningEnvKind.Nls,
                _ => null,
            };
            if (k is { } known && !string.IsNullOrEmpty(name))
            {
                result.Add(new TuningEnvRow(known, name, value));
            }
        }

        return result;
    }
}
