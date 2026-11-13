using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Rollback;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Rollback;

/// <summary>
/// 되돌리기 지점 정리(설계 7.4·12.3, RD-7). 첫 화면 뒤 한 번: 「열림」으로 남은 묶음 → 「확인 필요」(<see cref="IRollbackStore.MarkOrphansAsync"/>),
/// 보관 기간(<c>rollback.retentionDays</c>)이 지난 묶음과 표에 없는 <c>.orb</c> 파일 지우기(<see cref="IRollbackStore.PruneAsync"/>).
/// 그 뒤 묶음 저장(<see cref="RollbackGroupSaved"/>) 100번마다 보관 정리를 다시 한다. 실패는 앱 동작에 영향이 없어 삼킨다.
/// SQLite 만 쓰므로 Oracle 어셈블리를 깨우지 않는다. 소유: RB-22.
/// </summary>
[OrandaService(typeof(IStartupTask))]
internal sealed class RollbackMaintenance : IStartupTask
{
    internal const int PruneEverySaves = 100;

    private readonly IEventBus _events;
    private readonly IRollbackStore _store;
    private readonly ISettingsStore _settingsStore;
    private readonly Lock _chain = new();
    private IDisposable? _subscription;
    private Task _tail = Task.CompletedTask;
    private int _saves;

    public RollbackMaintenance(IEventBus events, IRollbackStore store, ISettingsStore settingsStore)
    {
        _events = events;
        _store = store;
        _settingsStore = settingsStore;
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public async Task RunAsync(CancellationToken ct)
    {
        _subscription ??= _events.Subscribe<RollbackGroupSaved>(this, _ =>
        {
            if (Interlocked.Increment(ref _saves) % PruneEverySaves == 0)
            {
                Enqueue();
            }
        });

        try
        {
            await _store.MarkOrphansAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("되돌리기 묶음 시작 정리 실패: {0}", ex.Message);
        }

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
            await _store.PruneAsync(doc.Value.Rollback.RetentionDays, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 다음 시작·다음 100번째 저장 때 다시 한다.
            System.Diagnostics.Trace.TraceWarning("되돌리기 묶음 보관 정리 실패: {0}", ex.Message);
        }
    }
}
