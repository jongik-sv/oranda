using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Testing;

/// <summary>저장하지 않고 메모리에만 두는 설정 저장소. <see cref="Current"/> 가 마지막으로 저장한(또는 처음 넣어 둔) 값이다.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Current { get; set; } = new();

    public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Current));

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}
