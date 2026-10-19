using Oranda.Core.Composition;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>settings.json 읽기·쓰기. 소유: TSK-03-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ISettingsStore))]
internal sealed class SettingsStore : ISettingsStore
{
    public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct)
        => throw new NotImplementedException("TSK-03-01");

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
        => throw new NotImplementedException("TSK-03-01");
}
