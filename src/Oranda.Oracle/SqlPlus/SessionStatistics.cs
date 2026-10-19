using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.SqlPlus;

/// <summary>
/// 문장별 실행 통계(EXEC-07 보조): 실행 전후 <c>v$mystat</c> 값의 차이. 읽기 전용 동적 뷰만 쓰며 라이선스 팩이 필요 없다.
/// 소유: TSK-06-06.
/// </summary>
internal static class SessionStatistics
{
    private const string Query =
        "SELECT n.name, s.value FROM v$mystat s JOIN v$statname n ON n.statistic# = s.statistic# WHERE s.value <> 0";

    /// <summary>화면에 보여 줄 통계(이름 순서 유지). 나머지는 값이 변했어도 숨긴다.</summary>
    internal static readonly string[] Interesting =
    [
        "session logical reads",
        "db block gets",
        "consistent gets",
        "physical reads",
        "redo size",
        "user commits",
        "parse count (total)",
        "parse count (hard)",
        "execute count",
        "sorts (memory)",
        "sorts (disk)",
        "bytes sent via SQL*Net to client",
        "bytes received via SQL*Net from client",
        "SQL*Net roundtrips to/from client",
    ];

    /// <summary>지금 세션의 통계 값. 읽지 못하면(권한·연결) null.</summary>
    public static async Task<IReadOnlyDictionary<string, decimal>?> CaptureAsync(IExecutionService exec, ISession s, CancellationToken ct)
    {
        var stmt = new SqlStatement(Query, default, StatementKind.Query) { Verb = "SELECT" };
        var options = new ExecutionOptions { Source = HistorySource.Script, SkipHistory = true };
        var result = await exec.ExecuteAsync(s, stmt, new Dictionary<string, BindValue>(), options, ct).ConfigureAwait(false);
        if (result.Status != ExecutionStatus.Succeeded || result.ResultSet is null)
        {
            if (result.ResultSet is not null)
            {
                await result.ResultSet.DisposeAsync().ConfigureAwait(false);
            }

            return null;
        }

        await using var rs = result.ResultSet;
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            var p = await rs.GetPageAsync(page, ct).ConfigureAwait(false);
            for (var i = 0; i < p.RowCount; i++)
            {
                if (p.Columns[0][i]?.ToString() is { } name && ToDecimal(p.Columns[1][i]) is { } value)
                {
                    values[name] = value;
                }
            }

            if (p.IsLast || p.RowCount == 0)
            {
                break;
            }
        }

        return values;
    }

    /// <summary>값이 늘어난 통계(after - before &gt; 0).</summary>
    public static IReadOnlyDictionary<string, decimal> Diff(IReadOnlyDictionary<string, decimal> before, IReadOnlyDictionary<string, decimal> after)
    {
        var diff = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (name, value) in after)
        {
            var delta = value - (before.TryGetValue(name, out var b) ? b : 0m);
            if (delta > 0)
            {
                diff[name] = delta;
            }
        }

        return diff;
    }

    /// <summary>출력 창에 보일 줄(보여 줄 통계만, 변한 것이 없으면 줄 없음).</summary>
    public static IEnumerable<string> Describe(IReadOnlyDictionary<string, decimal> diff)
    {
        var parts = Interesting.Where(diff.ContainsKey)
            .Select(n => string.Create(CultureInfo.InvariantCulture, $"{n}={diff[n]:0.##}"))
            .ToList();
        if (parts.Count > 0)
        {
            yield return "통계: " + string.Join(", ", parts);
        }
    }

    private static decimal? ToDecimal(object? v) => v switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        double db => (decimal)db,
        string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };
}
