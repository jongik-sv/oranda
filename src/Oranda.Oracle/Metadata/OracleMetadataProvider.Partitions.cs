using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>
/// 테이블 편집기 파티션 정보(T14, 설계 18.4): <c>meta.part_scheme</c>(방식·키·INTERVAL) → 있으면 <c>meta.detail_partitions</c>(HIGH_VALUE·PARTITION_POSITION)·
/// <c>meta.part_indexes</c> 를 읽어 <see cref="TableInfo.Partitioning"/> 에 담는다(행 모양은 Tibero 와 같아 <see cref="PartitionRows"/> 를 같이 쓴다).
/// 파티션 테이블(<c>meta.table_props</c> 의 partitioned)만 묻는다. 권한 등으로 못 읽으면 정보 null 로 두어 편집기가 파티션 편집도,
/// 일반 → 파티션 바꾸기도 내지 않게 한다(<see cref="Oranda.Core.Schema.TableEdit.Partitions.PartitionTableDraft.FromTable"/>).
/// </summary>
internal sealed partial class OracleMetadataProvider
{
    private static async Task<TableInfo> WithPartitioningAsync(ISession s, TableInfo info, Action<OracleCommand> bindTable, CancellationToken ct)
    {
        // 파티션 여부는 meta.table_props(TE-29)가 이미 읽었다 → 파티션 테이블만 더 묻는다.
        if (!info.IsPartitioned || !string.Equals(info.Table.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase))
        {
            return info;
        }

        var scheme = await TryQueryAsync(s, "meta.part_scheme", bindTable, ct);
        if (scheme is not { Count: > 0 })
        {
            // 못 읽음: 정보를 비운 채 둔다(파티션 테이블 + 정보 없음 → 편집기는 파티션 편집도, 일반 → 파티션 바꾸기도 내지 않는다).
            return info;
        }

        var partitions = await TryQueryAsync(s, "meta.detail_partitions", bindTable, ct);
        var indexes = await TryQueryAsync(s, "meta.part_indexes", bindTable, ct);
        return info with { Partitioning = partitions is null || indexes is null ? null : PartitionRows.Build(scheme, partitions, indexes) };
    }
}
