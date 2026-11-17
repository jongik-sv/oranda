using Oranda.Core.Catalog.Providers;

namespace Oranda.Core.Metadata;

/// <summary>
/// 파티션 카탈로그 행 → <see cref="PartitioningInfo"/>(테이블 편집기 T14, 설계 18.4). Oracle·Tibero 공급자가 같은 열 모양으로 읽는다:
/// <list type="bullet">
/// <item><c>meta.part_scheme</c>: partitioning_type, subpartitioning_type, interval, level_kind(PART|SUB), column_name, column_position.</item>
/// <item><c>meta.detail_partitions</c>: partition_position, partition_name, partitioning_type, subpartitioning_type, high_value(Tibero BOUND), tablespace_name, num_rows, last_analyzed.</item>
/// <item><c>meta.part_indexes</c>: index_name, locality(LOCAL·GLOBAL).</item>
/// </list>
/// 통계를 모은 적 없으면(<c>last_analyzed</c> NULL) 행 수는 모름(Tibero 는 0 을 준다, 실측).
/// </summary>
public static class PartitionRows
{
    /// <summary>방식 행이 없으면 null(파티션 테이블이 아님).</summary>
    public static PartitioningInfo? Build(IReadOnlyList<object?[]> schemeRows, IReadOnlyList<object?[]> partitionRows, IReadOnlyList<object?[]> indexRows)
    {
        ArgumentNullException.ThrowIfNull(schemeRows);
        ArgumentNullException.ThrowIfNull(partitionRows);
        ArgumentNullException.ThrowIfNull(indexRows);
        if (schemeRows.Count == 0)
        {
            return null;
        }

        var first = schemeRows[0];
        List<string> Keys(string level) =>
        [
            .. schemeRows.Where(r => string.Equals(CatalogValues.Str(r[3]), level, StringComparison.Ordinal))
                .OrderBy(r => CatalogValues.Int(r[5]) ?? 0)
                .Select(r => CatalogValues.Str(r[4])!),
        ];

        var sub = CatalogValues.Str(first[1]);
        return new PartitioningInfo(CatalogValues.Str(first[0]) ?? string.Empty, Keys("PART"),
            [
                .. partitionRows.Select(r => new PartitionInfo(CatalogValues.Str(r[1])!, CatalogValues.Int(r[0]) ?? 0, Trim(CatalogValues.Str(r[4])))
                {
                    Tablespace = CatalogValues.Str(r[5]),
                    NumRows = r.Length > 7 && r[7] is null ? null : CatalogValues.Long(r[6]),
                }),
            ])
        {
            Interval = Trim(CatalogValues.Str(first[2])),
            SubpartitionMethod = sub is null or "NONE" ? null : sub,
            SubpartitionKeyColumns = Keys("SUB"),
            PartitionedIndexes = [.. indexRows.Select(r => CatalogValues.Str(r[0])).OfType<string>()],
        };
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
