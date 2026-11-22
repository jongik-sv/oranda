using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>통계 수집 뒤 새로 읽을 화면들(<see cref="GatherStatsRefresh"/>). 없는 것은 비워 둔다.</summary>
public sealed class GatherStatsViews
{
    /// <summary>통계를 모은 접속의 세션(상세 캐시 키 = <see cref="ISession.DbKey"/>). 그 접속의 창만 다시 읽는다.</summary>
    public ISession? Session { get; init; }

    /// <summary>메타데이터 공급자(상세 캐시를 비운다).</summary>
    public IMetadataProvider? Meta { get; init; }

    /// <summary>스키마 브라우저 열 통계 요약.</summary>
    public SchemaColumnStatsViewModel? ColumnStats { get; init; }

    /// <summary>스키마 브라우저 속성 창.</summary>
    public SchemaPropertiesViewModel? Properties { get; init; }

    /// <summary>열린 「통계 보기」 탭.</summary>
    public IEnumerable<TableStatsViewModel> StatsTabs { get; init; } = [];

    /// <summary>열린 테이블 창(객체 상세).</summary>
    public IEnumerable<ObjectDetailViewModel> Details { get; init; } = [];
}

/// <summary>
/// 통계를 새로 모은 뒤(SG-1, 결함 2026-11-22): 그 테이블만 새로 읽는다 — 스키마 브라우저 트리 전체는 다시 그리지 않는다.
/// <list type="number">
/// <item>메타데이터 상세 캐시(테이블 정보 payload)를 비운다: 닫고 다시 연 테이블 창이 옛 값을 받지 않게.</item>
/// <item>스키마 브라우저 열 통계 요약 캐시를 버리고, 그 테이블 열을 보이고 있으면 다시 읽는다. 속성 창이 그 테이블이면 다시 읽는다.</item>
/// <item>열린 「통계 보기」 탭과 테이블 창(통계·파티션·크기 탭)을 다시 읽는다.</item>
/// </list>
/// </summary>
public static class GatherStatsRefresh
{
    public static Task ApplyAsync(IReadOnlyCollection<TableStatsTarget> gathered, SchemaColumnStatsViewModel? cache,
        IEnumerable<TableStatsViewModel> openTabs, CancellationToken ct)
        => ApplyAsync(gathered, new GatherStatsViews { ColumnStats = cache, StatsTabs = openTabs }, ct);

    public static async Task ApplyAsync(IReadOnlyCollection<TableStatsTarget> gathered, GatherStatsViews views, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gathered);
        ArgumentNullException.ThrowIfNull(views);
        if (gathered.Count == 0)
        {
            return;
        }

        var session = views.Session;
        if (views.Meta is { } meta && session is not null)
        {
            try
            {
                // 유형은 비운다: 통계 수집 대상(TABLE)과 같은 이름의 행을 모두(소유자·이름) 맞춘다.
                await meta.ForgetObjectDetailsAsync(session, [.. gathered.Select(t => new DbObjectRef(t.Owner, t.Table, string.Empty))], ct)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning("통계 수집 뒤 상세 캐시 비우기 실패: {0}", ex.Message);
            }
        }

        foreach (var t in gathered)
        {
            if (views.ColumnStats is { } columns)
            {
                await columns.RefreshAsync(t.Owner, t.Table).ConfigureAwait(true);
            }
        }

        if (views.Properties is { Target: { } shown } props && Matches(gathered, shown) && views.ColumnStats is not { IsVisible: true })
        {
            await props.ShowAsync(shown).ConfigureAwait(true);
        }

        foreach (var tab in views.StatsTabs.Where(v => v.Target is { } target && gathered.Contains(target)).ToList())
        {
            await tab.LoadAsync(ct).ConfigureAwait(true);
        }

        foreach (var detail in views.Details.Where(d => d.Target is { } target && Matches(gathered, target) && SameDb(d.Session, session)).ToList())
        {
            await detail.ReloadStatsAsync().ConfigureAwait(true);
        }
    }

    private static bool Matches(IReadOnlyCollection<TableStatsTarget> gathered, DbObjectRef obj)
        => obj.ObjectType is "TABLE" or "" && gathered.Contains(new TableStatsTarget(obj.Owner, obj.Name));

    /// <summary>세션을 모르면(시험·옛 길) 같은 접속으로 본다.</summary>
    private static bool SameDb(ISession? open, ISession? gathered)
        => open is null || gathered is null || string.Equals(open.DbKey, gathered.DbKey, StringComparison.Ordinal);
}
