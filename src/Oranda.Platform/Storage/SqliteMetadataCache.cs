using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>메타데이터 캐시(meta_source·meta_object). 소유: TSK-04-04. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IMetadataCache))]
internal sealed class SqliteMetadataCache : IMetadataCache
{
    public Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task InvalidateAsync(string dbKey, IReadOnlyList<DbObjectRef> objects, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");

    public Task PruneAsync(MetadataCacheSettings settings, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-04");
}
