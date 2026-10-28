using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Language.Ai.Tools;

/// <summary>
/// 튜닝 도구가 기존 조회 서비스를 불러 응답 모양으로 바꾸는 층(설계 §5.2). 허용 수준·범위·가림 판단은 하지 않는다(AgentGate 몫).
/// 조회 세션은 부르는 쪽이 모니터·에이전트 세션을 넘긴다.
/// </summary>
public sealed partial class TuningToolbox(
    ITuningService tuning, IMetadataProvider metadata, ITableKeyDetailProvider keys, Oranda.Core.Plans.IExecutionPlanService plans,
    Oranda.Core.Language.ILanguageService language,
    ToolRunCache? cache = null)
{
    private readonly ToolRunCache _cache = cache ?? new ToolRunCache();

    /// <summary><c>describe_object</c>: 열·제약·인덱스 이름과 열.</summary>
    public Task<ObjectDescription> DescribeObjectAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        Cached(s, "describe", obj, string.Empty, () => DescribeObjectCoreAsync(s, obj, ct), ct);

    /// <summary><c>get_index_info</c>: 인덱스마다 통계·열 구성(식·내림차순 포함)을 합친다.</summary>
    public Task<IReadOnlyList<IndexDescription>> GetIndexInfoAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        Cached(s, "index_info", table, string.Empty, () => GetIndexInfoCoreAsync(s, table, ct), ct);

    /// <summary><c>get_table_stats</c>: 표 통계와 열 통계(<paramref name="columns"/> 가 있으면 그 열만).</summary>
    public Task<TableStatsDescription> GetTableStatsAsync(ISession s, DbObjectRef table, IReadOnlyList<string>? columns, CancellationToken ct) =>
        Cached(s, "table_stats", table, columns is null ? string.Empty : string.Join(',', columns.Select(c => c.ToUpperInvariant()).Order(StringComparer.Ordinal)),
            () => GetTableStatsCoreAsync(s, table, columns, ct), ct);

    /// <summary>
    /// <c>get_histogram</c>: 버킷별 개수. 끝값(실제 열 값)은 <paramref name="includeEndpoints"/> 가 참일 때만 싣는다(허용 수준 「행 값」 이상에서 부르는 쪽이 켠다. 민감 컬럼 가림은 AgentGate 몫).
    /// </summary>
    public Task<HistogramDescription> GetHistogramAsync(ISession s, DbObjectRef table, string column, int maxBuckets, bool includeEndpoints, CancellationToken ct) =>
        Cached(s, "histogram", table, $"{column?.ToUpperInvariant()}/{maxBuckets}/{includeEndpoints}", () => GetHistogramCoreAsync(s, table, column!, maxBuckets, includeEndpoints, ct), ct);

    private Task<T> Cached<T>(ISession s, string tool, DbObjectRef obj, string args, Func<Task<T>> load, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);
        return _cache.GetOrAddAsync($"{s.DbKey}|{tool}|{Name(obj)}|{args}", load, ct);
    }

    private async Task<ObjectDescription> DescribeObjectCoreAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);
        var info = await metadata.GetTableAsync(s, obj, ct).ConfigureAwait(false);
        return new ObjectDescription(
            Name(info.Table),
            info.Table.ObjectType,
            info.Comment,
            [.. info.Columns.OrderBy(c => c.Position).Select(c => new ColumnDescription(c.Name, TypeText(c), c.IsNullable, c.DefaultValue, c.Comment))],
            [.. info.Constraints.Select(c => new ConstraintDescription(c.Name, ConstraintKindText(c.Kind), c.Columns, c.ReferencedTable is null ? null : Name(c.ReferencedTable)))],
            [.. info.Indexes.Select(i => new IndexSummary(i.Name, i.IsUnique, i.IndexType, i.Columns))]);
    }

    /// <summary><c>get_index_info</c>: 인덱스마다 통계·열 구성(식·내림차순 포함)을 합친다.</summary>
    private async Task<IReadOnlyList<IndexDescription>> GetIndexInfoCoreAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        var info = await metadata.GetTableAsync(s, table, ct).ConfigureAwait(false);
        var stats = (await tuning.GetIndexStatsAsync(s, table.Owner, table.Name, ct).ConfigureAwait(false))
            .ToDictionary(x => x.IndexName, StringComparer.Ordinal);
        var names = info.Indexes.Select(i => i.Name).Union(stats.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var result = new List<IndexDescription>(names.Count);
        foreach (var name in names)
        {
            var basic = info.Indexes.FirstOrDefault(i => i.Name == name);
            stats.TryGetValue(name, out var st);
            var detail = await keys.GetIndexColumnsAsync(s, table, name, ct).ConfigureAwait(false);
            IReadOnlyList<IndexColumnDescription> columns = detail.Items.Count > 0
                ? [.. detail.Items.OrderBy(d => d.Position).Select(d => new IndexColumnDescription(d.Column, d.Descending, d.Expression))]
                : [.. (basic?.Columns ?? []).Select(c => new IndexColumnDescription(c, false, null))];
            result.Add(new IndexDescription(
                name,
                basic?.IsUnique ?? st?.Uniqueness == "UNIQUE",
                basic?.IndexType,
                columns,
                Visibility: null,
                Partitioned: null,
                st?.BLevel,
                st?.LeafBlocks,
                st?.DistinctKeys,
                st?.ClusteringFactor,
                Day(st?.LastAnalyzed),
                st?.Stale ?? false));
        }

        return result;
    }

    /// <summary><c>get_table_stats</c>: 표 통계와 열 통계(<paramref name="columns"/> 가 있으면 그 열만).</summary>
    private async Task<TableStatsDescription> GetTableStatsCoreAsync(ISession s, DbObjectRef table, IReadOnlyList<string>? columns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        var ts = await tuning.GetTableStatsAsync(s, table.Owner, table.Name, ct).ConfigureAwait(false);
        var cols = await tuning.GetColumnStatsAsync(s, table.Owner, table.Name, ct).ConfigureAwait(false);
        var wanted = columns is { Count: > 0 } ? columns.Select(c => c.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal) : null;
        var described = cols
            .Where(c => wanted is null || wanted.Contains(c.ColumnName))
            .Select(c => new ColumnStatsDescription(c.ColumnName, c.NumDistinct, c.NumNulls, c.Density, c.Histogram, c.NumBuckets))
            .ToList();
        return new TableStatsDescription(
            Name(table), ts is not null, ts?.NumRows, ts?.Blocks, ts?.AvgRowLen, Day(ts?.LastAnalyzed), ts?.Stale ?? false, described);
    }

    /// <summary>
    /// <c>get_histogram</c>: 버킷별 개수. 끝값(실제 열 값)은 <paramref name="includeEndpoints"/> 가 참일 때만 싣는다(허용 수준 「행 값」 이상에서 부르는 쪽이 켠다. 민감 컬럼 가림은 AgentGate 몫).
    /// </summary>
    private async Task<HistogramDescription> GetHistogramCoreAsync(ISession s, DbObjectRef table, string column, int maxBuckets, bool includeEndpoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        var col = column.ToUpperInvariant();
        var buckets = await tuning.GetHistogramAsync(s, table.Owner, table.Name, col, ct).ConfigureAwait(false);
        var kind = (await tuning.GetColumnStatsAsync(s, table.Owner, table.Name, ct).ConfigureAwait(false))
            .FirstOrDefault(c => c.ColumnName == col)?.Histogram ?? "NONE";
        var counts = buckets.Select((b, i) => new HistogramBucketCount(i + 1, b.Frequency, includeEndpoints ? b.EndpointValue : null)).ToList();
        var kept = counts.Count > maxBuckets && maxBuckets > 0
            ? counts.OrderByDescending(b => b.Count).ThenBy(b => b.Bucket).Take(maxBuckets).OrderBy(b => b.Bucket).ToList()
            : counts;
        return new HistogramDescription(col, kind, counts.Count, counts.Sum(b => b.Count), kept, counts.Count - kept.Count);
    }

    internal static string Name(DbObjectRef o) => string.IsNullOrEmpty(o.Owner) ? o.Name : o.Owner + "." + o.Name;

    internal static string? Day(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string ConstraintKindText(ConstraintKind k) => k switch
    {
        ConstraintKind.PrimaryKey => "PRIMARY KEY",
        ConstraintKind.Unique => "UNIQUE",
        ConstraintKind.ForeignKey => "FOREIGN KEY",
        ConstraintKind.Check => "CHECK",
        _ => k.ToString().ToUpperInvariant(),
    };

    private static string TypeText(ColumnInfo c)
    {
        var t = c.DataType;
        if (t is "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" && c.Length is > 0)
        {
            return $"{t}({c.Length.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        if (t == "NUMBER" && c.Precision is > 0)
        {
            return c.Scale is > 0
                ? $"NUMBER({c.Precision.Value.ToString(CultureInfo.InvariantCulture)},{c.Scale.Value.ToString(CultureInfo.InvariantCulture)})"
                : $"NUMBER({c.Precision.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        return t;
    }
}
