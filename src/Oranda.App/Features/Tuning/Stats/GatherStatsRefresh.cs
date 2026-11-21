using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.SchemaBrowser;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>통계를 새로 모은 뒤(SG-1): 스키마 브라우저 열 통계 요약 캐시에서 그 테이블을 버리고, 그 테이블의 열린 「통계 보기」 탭을 다시 읽는다.</summary>
public static class GatherStatsRefresh
{
    public static async Task ApplyAsync(IReadOnlyCollection<TableStatsTarget> gathered, SchemaColumnStatsViewModel? cache,
        IEnumerable<TableStatsViewModel> openTabs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gathered);
        ArgumentNullException.ThrowIfNull(openTabs);
        foreach (var t in gathered)
        {
            cache?.Invalidate(t.Owner, t.Table);
        }

        foreach (var tab in openTabs.Where(v => v.Target is { } target && gathered.Contains(target)).ToList())
        {
            await tab.LoadAsync(ct).ConfigureAwait(true);
        }
    }
}
