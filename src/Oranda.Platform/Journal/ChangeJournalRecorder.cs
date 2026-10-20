using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Journal;

namespace Oranda.Platform.Journal;

/// <summary>
/// <see cref="ChangeSnapshotCaptured"/> 를 구독해 변경 일지에 쌓는다(TSK-13-07). 저장은 직렬 큐에서 하고, 저장·정리 실패는
/// 삼킨다: 일지 때문에 실행 흐름이 막히면 안 된다. 시작 직후 보존 정책을 한 번, 이후 기록 100건마다 적용한다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
internal sealed class ChangeJournalRecorder : IStartupTask
{
    private const int PruneEvery = 100;

    private readonly IEventBus _events;
    private readonly IChangeJournalStore _store;
    private readonly JournalRetention _retention = new();
    private readonly object _chain = new();
    private readonly List<IDisposable> _subscriptions = [];
    private Task _tail = Task.CompletedTask;
    private int _sincePrune;

    public ChangeJournalRecorder(IEventBus events, IChangeJournalStore store)
    {
        _events = events;
        _store = store;
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public async Task RunAsync(CancellationToken ct)
    {
        _subscriptions.Add(_events.Subscribe<ChangeSnapshotCaptured>(this, OnCaptured));
        try
        {
            await _store.PruneAsync(_retention, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 보존 정리 실패는 앱 동작에 영향이 없다.
        }
    }

    /// <summary>지금까지 요청한 기록이 끝날 때까지 기다린다(시험·종료용).</summary>
    internal Task DrainAsync()
    {
        lock (_chain)
        {
            return _tail;
        }
    }

    private void OnCaptured(ChangeSnapshotCaptured e)
    {
        lock (_chain)
        {
            _tail = _tail.ContinueWith(_ => SaveAsync(e.Entry), CancellationToken.None, TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SaveAsync(JournalEntry entry)
    {
        try
        {
            if (await _store.AddAsync(entry, CancellationToken.None).ConfigureAwait(false) is { } id)
            {
                _events.Publish(new ChangeJournalSaved(id));
            }

            if (Interlocked.Increment(ref _sincePrune) >= PruneEvery)
            {
                Interlocked.Exchange(ref _sincePrune, 0);
                await _store.PruneAsync(_retention, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 일지 기록 실패가 실행 흐름을 막으면 안 된다.
        }
    }
}
