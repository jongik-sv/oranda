using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>
/// 스키마 브라우저 표·뷰 하위 폴더(Triggers · Snapshot Log)가 펼칠 때 읽는 목록(TSK-13-08). 읽기 전용이다.
/// 열·인덱스·제약은 <see cref="IMetadataProvider.GetTableAsync"/>, 휴지통은 <see cref="ISchemaAdminProvider.ListRecycleBinAsync"/> 를 쓴다.
/// 구현: Oranda.Oracle <c>OracleMetadataProvider</c>(Tree 부분).
/// </summary>
public interface ISchemaTreeProvider
{
    /// <summary>표·뷰에 걸린 트리거(뷰는 INSTEAD OF). 뷰·권한 문제로 읽지 못하면 <see cref="SchemaFolderResult{T}.Denied"/>.</summary>
    Task<SchemaFolderResult<TreeTrigger>> ListTriggersAsync(ISession s, DbObjectRef table, CancellationToken ct);

    /// <summary>표를 마스터로 하는 MView log. 없으면 빈 목록.</summary>
    Task<SchemaFolderResult<SnapshotLogRef>> ListSnapshotLogsAsync(ISession s, DbObjectRef table, CancellationToken ct);
}

/// <summary>폴더 하나를 읽은 결과. <paramref name="Denied"/> 는 권한이 없어 읽지 못했다는 뜻(빈 목록과 구별한다).</summary>
public sealed record SchemaFolderResult<T>(IReadOnlyList<T> Items, bool Denied = false);

public sealed record TreeTrigger(string Owner, string Name, string Status);

public sealed record SnapshotLogRef(string LogOwner, string LogTable);
