using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>
/// 메타데이터 캐시 보관 일수(<c>metadataCache.retentionDays</c>, 설정 「이력·캐시」)를 적용한다: 첫 화면 뒤 한 번, 설정을 바꿀 때마다
/// 그 일수보다 오래 쓰지 않은 DB 의 캐시를 지운다(<see cref="IMetadataCache.PruneAsync"/>). SQLite 만 쓰므로 Oracle 어셈블리를 깨우지 않는다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
internal sealed class MetadataCachePruner : IStartupTask
{
    private readonly IEventBus _events;
    private readonly IMetadataCache _cache;
    private readonly ISettingsStore _settingsStore;
    private readonly object _chain = new();
    private IDisposable? _subscription;
    private Task _tail = Task.CompletedTask;

    public MetadataCachePruner(IEventBus events, IMetadataCache cache, ISettingsStore settingsStore)
    {
        _events = events;
        _cache = cache;
        _settingsStore = settingsStore;
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public async Task RunAsync(CancellationToken ct)
    {
        _subscription ??= _events.Subscribe<SettingsChanged>(this, e =>
        {
            if (string.Equals(e.Section, "metadataCache", StringComparison.OrdinalIgnoreCase))
            {
                Enqueue();
            }
        });
        await PruneAsync(ct).ConfigureAwait(false);
    }

    /// <summary>지금까지 요청한 정리가 끝날 때까지 기다린다(시험용).</summary>
    internal Task DrainAsync()
    {
        lock (_chain)
        {
            return _tail;
        }
    }

    private void Enqueue()
    {
        lock (_chain)
        {
            _tail = _tail.ContinueWith(_ => PruneAsync(CancellationToken.None), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var doc = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
            await _cache.PruneAsync(doc.Value.MetadataCache, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 캐시 정리 실패는 앱 동작에 영향이 없다(다음 시작·설정 변경 때 다시 한다).
            System.Diagnostics.Trace.TraceWarning("메타데이터 캐시 정리 실패: {0}", ex.Message);
        }
    }
}
