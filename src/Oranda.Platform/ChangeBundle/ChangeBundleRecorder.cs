using Oranda.Core.ChangeBundle;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;

namespace Oranda.Platform.ChangeBundle;

/// <summary>
/// 반영 묶음 자동 넣기(테이블 편집기 설계 9.3). <see cref="StatementExecuted"/> 를 구독해 조건(<see cref="ChangeBundleRules.IsCandidate"/>)에 맞는
/// DDL 을 「지금 묶음」에 넣는다(<see cref="IChangeBundleService.AddExecutedAsync"/>: 소유자 붙이기·DBMS·대상 소유자·해시 중복 판정).
/// 소유자를 안 쓴 문장의 소유자 = 세션의 현재 스키마(<see cref="IChangeBundleService.OriginOf"/>, 서비스가 세션마다 따라간다).
/// 저장은 직렬 큐에서 하고 실패는 삼킨다(실행 흐름을 막지 않는다).
/// </summary>
[OrandaService(typeof(IStartupTask))]
internal sealed class ChangeBundleRecorder : IStartupTask
{
    private readonly IEventBus _events;
    private readonly IChangeBundleService _service;
    private readonly object _chain = new();
    private readonly List<IDisposable> _subscriptions = [];
    private Task _tail = Task.CompletedTask;

    public ChangeBundleRecorder(IEventBus events, IChangeBundleService service)
    {
        _events = events;
        _service = service;
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        _subscriptions.Add(_events.Subscribe<StatementExecuted>(this, OnStatementExecuted));
        return Task.CompletedTask;
    }

    /// <summary>지금까지 요청한 넣기가 끝날 때까지 기다린다(시험·종료용).</summary>
    internal Task DrainAsync()
    {
        lock (_chain)
        {
            return _tail;
        }
    }

    private void OnStatementExecuted(StatementExecuted e)
    {
        if (!ChangeBundleRules.IsCandidate(e.Statement, e.Result, e.Options) || _service.OriginOf(e.SessionId) is not { } origin)
        {
            return;
        }

        var statement = e.Statement;
        var source = ChangeBundleRules.SourceOf(e.Options.Source);
        lock (_chain)
        {
            _tail = _tail.ContinueWith(_ => SaveAsync(origin, statement, source), CancellationToken.None, TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SaveAsync(ChangeBundleOrigin origin, SqlStatement statement, ChangeBundleItemSource source)
    {
        try
        {
            await _service.AddExecutedAsync(origin, statement, source, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 묶음 넣기 실패가 실행 흐름을 막으면 안 된다.
        }
    }
}
