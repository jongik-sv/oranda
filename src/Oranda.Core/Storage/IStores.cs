using Oranda.Core.Connection;
using Oranda.Core.Formatting;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Core.Workspaces;

namespace Oranda.Core.Storage;

/// <summary>
/// JSON 파일 하나를 읽은 상태. 미래 버전·변환 실패 파일은 <see cref="IsReadOnly"/>(저장하지 않음, data-model 4.1).
/// </summary>
public sealed record StoredDocument<T>(T Value, bool IsReadOnly = false)
{
    /// <summary>사용자에게 알릴 말(손상 백업, 읽기 전용 이유 등). 없으면 null.</summary>
    public string? Notice { get; init; }
}

/// <summary><c>settings.json</c>. 저장하면 바뀐 섹션마다 <c>SettingsChanged</c> 이벤트. 구현: Platform(TSK-03-01).</summary>
public interface ISettingsStore
{
    Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct);

    Task SaveAsync(AppSettings settings, CancellationToken ct);
}

/// <summary><c>connections.json</c>. 저장 시 data-model 2.2 검증(비밀 문자열 거부, 이름 중복). 구현: Platform(TSK-03-04).</summary>
public interface IConnectionStore
{
    Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct);

    Task SaveAsync(ConnectionsDocument document, CancellationToken ct);
}

/// <summary>포매터 프로필(기본 제공 3종 + <c>formatter/*.json</c>). 구현: Platform(TSK-05-03).</summary>
public interface IFormatterProfileStore
{
    /// <summary>기본 제공 프로필이 앞, 사용자 프로필이 뒤.</summary>
    Task<IReadOnlyList<FormatterProfile>> ListAsync(CancellationToken ct);

    Task SaveAsync(FormatterProfile profile, CancellationToken ct);

    /// <summary>기본 제공 프로필은 지울 수 없다.</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    Task ExportAsync(string id, string path, CancellationToken ct);

    Task<FormatterProfile> ImportAsync(string path, CancellationToken ct);
}

/// <summary>작업 공간과 복구용 자동 저장(data-model 2.6). 구현: Platform(TSK-04-02).</summary>
public interface IWorkspaceStore
{
    Task<StoredDocument<Workspace>?> LoadAsync(string id, CancellationToken ct);

    Task SaveAsync(Workspace workspace, CancellationToken ct);

    Task AutosaveAsync(Workspace workspace, CancellationToken ct);

    /// <summary>남은 자동 저장(= 비정상 종료 흔적). 없으면 null.</summary>
    Task<Workspace?> LoadAutosaveAsync(string id, CancellationToken ct);

    /// <summary>정상 종료 때 자동 저장 파일을 지운다.</summary>
    Task DeleteAutosaveAsync(string id, CancellationToken ct);
}

/// <summary>이력 검색 조건(EXEC-07). <see cref="Text"/> 는 trigram 부분 문자열 검색.</summary>
public sealed record HistoryQuery
{
    public Guid? ProfileId { get; init; }

    public string? Text { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public HistoryStatus? Status { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>쪽 넘기기: 이 Id 보다 작은 것만.</summary>
    public long? BeforeId { get; init; }
}

/// <summary><c>oranda.db</c> <c>history</c>. 기록 전 가림은 호출자가 아니라 저장소가 <c>ISecretRedactor</c> 로 한다. 구현: Platform(TSK-06-06).</summary>
public interface IHistoryStore
{
    Task<long> AddAsync(HistoryEntry entry, CancellationToken ct);

    Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query, CancellationToken ct);

    Task DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct);

    /// <summary>null = 전부. 「이력 비우기」 뒤 백그라운드 VACUUM.</summary>
    Task ClearAsync(Guid? profileId, CancellationToken ct);

    /// <summary>보존 정책(data-model 3.4) 적용.</summary>
    Task PruneAsync(HistorySettings settings, CancellationToken ct);
}

/// <summary><c>oranda.db</c> <c>meta_*</c> 캐시. 언제든 다시 만들 수 있다. 구현: Platform(TSK-04-04).</summary>
public interface IMetadataCache
{
    Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct);

    Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct);

    /// <summary>이름 앞부분 검색(자동완성).</summary>
    Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct);

    Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct);

    /// <summary>빈 목록 = 그 <paramref name="dbKey"/> 전체.</summary>
    Task InvalidateAsync(string dbKey, IReadOnlyList<DbObjectRef> objects, CancellationToken ct);

    Task PruneAsync(MetadataCacheSettings settings, CancellationToken ct);
}
