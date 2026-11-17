using System.Collections.Concurrent;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;

namespace Oranda.Platform.ChangeBundle;

/// <summary>
/// <see cref="IChangeBundleService"/> 구현(테이블 편집기 설계 9.3). 넣기는 한 줄로 세워(같은 해시 판정과 넣기 사이에 다른 넣기가 끼지 않게) 하고,
/// 저장 실패는 결과(<see cref="ChangeBundleAddOutcome.NotWritable"/>)로 돌려준다. 사용자의 취소만 밖으로 전파한다.
/// 세션마다의 현재 스키마(<c>ALTER SESSION SET CURRENT_SCHEMA</c>)를 여기서 따라가 자동 넣기와 앱의 「묶음에 넣기」가 같은 소유자를 붙이게 한다.
/// </summary>
[OrandaService(typeof(IChangeBundleService))]
internal sealed class ChangeBundleService : IChangeBundleService, IDisposable
{
    private readonly IChangeBundleStore _store;
    private readonly IOwnerRewriter _rewriter;
    private readonly IEventBus _events;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ChangeBundleOrigin> _sessions = new();
    private readonly List<IDisposable> _subscriptions = [];

    public ChangeBundleService(IChangeBundleStore store, IOwnerRewriter rewriter, IEventBus events)
    {
        _store = store;
        _rewriter = rewriter;
        _events = events;
        _subscriptions.Add(events.Subscribe<SessionOpened>(this, e => _sessions[e.SessionId] = ChangeBundleOrigin.From(e.Profile, e.Server)));
        _subscriptions.Add(events.Subscribe<SessionClosed>(this, e => _sessions.TryRemove(e.SessionId, out _)));
        _subscriptions.Add(events.Subscribe<StatementExecuted>(this, OnStatementExecuted));
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _gate.Dispose();
    }

    public ChangeBundleOrigin? OriginOf(Guid sessionId) => _sessions.TryGetValue(sessionId, out var origin) ? origin : null;

    public async Task<ChangeBundleInfo?> GetCurrentAsync(CancellationToken ct)
    {
        try
        {
            return await _store.GetCurrentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<ChangeBundleInfo?> CreateAsync(string name, ChangeBundleOrigin? origin, bool makeCurrent, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            IReadOnlyList<string> owners = origin is null ? [] : [origin.CurrentSchema];
            var created = await _store.CreateAsync(name, null, owners, makeCurrent, ct).ConfigureAwait(false);
            if (created is not null)
            {
                _events.Publish(new ChangeBundleChanged(null, 0, 0));
            }

            return created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public Task<ChangeBundleAddResult> AddAsync(ChangeBundleOrigin origin, IReadOnlyList<ChangeBundleDraft> statements, ChangeBundleItemSource source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(statements);
        return AddCoreAsync(origin, statements, source, executed: false, ct);
    }

    public Task<ChangeBundleAddResult> AddExecutedAsync(ChangeBundleOrigin origin, SqlStatement statement, ChangeBundleItemSource source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement.Text);
        var hint = statement.ChangeTarget?.Owner;
        return AddCoreAsync(origin, [new ChangeBundleDraft(statement.Text, null, hint)], source, executed: true, ct);
    }

    public async Task RecordRunAsync(long itemId, ChangeBundleOrigin origin, bool succeeded, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        try
        {
            await _store.AddRunAsync(new ChangeBundleRun(itemId, origin.Environment, origin.ConnectionName, DateTimeOffset.UtcNow, succeeded), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 실행 표시는 부가 기능이다. 묶음 실행을 막지 않는다.
        }
    }

    /// <summary>성공한 <c>ALTER SESSION SET CURRENT_SCHEMA</c> 를 그 세션의 현재 스키마로 기억한다.</summary>
    private void OnStatementExecuted(StatementExecuted e)
    {
        if (e.Result.Status == ExecutionStatus.Succeeded && _sessions.TryGetValue(e.SessionId, out var origin)
            && _rewriter.CurrentSchemaSwitch(e.Statement.Text) is { } schema)
        {
            _sessions[e.SessionId] = origin with { CurrentSchema = schema };
        }
    }

    private async Task<ChangeBundleAddResult> AddCoreAsync(ChangeBundleOrigin origin, IReadOnlyList<ChangeBundleDraft> statements,
        ChangeBundleItemSource source, bool executed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        long? bundleId = null;
        int added = 0, marked = 0;
        try
        {
            var bundle = await _store.GetCurrentAsync(ct).ConfigureAwait(false);
            if (bundle is null)
            {
                return ChangeBundleAddResult.Of(_store.IsWritable ? ChangeBundleAddOutcome.NoCurrentBundle : ChangeBundleAddOutcome.NotWritable);
            }

            bundleId = bundle.Id;
            if (!_store.IsWritable)
            {
                return ChangeBundleAddResult.Of(ChangeBundleAddOutcome.NotWritable, bundle.Id);
            }

            if (bundle.Dbms is { } dbms && dbms != origin.Dbms)
            {
                if (executed)
                {
                    _events.Publish(new ChangeBundleDbmsMismatch(bundle.Id, bundle.Name, dbms, origin.Dbms));
                }

                return ChangeBundleAddResult.Of(ChangeBundleAddOutcome.DbmsMismatch, bundle.Id);
            }

            int skipped = 0, present = 0;
            foreach (var draft in statements)
            {
                if (string.IsNullOrWhiteSpace(draft.Text))
                {
                    continue;
                }

                var q = _rewriter.Qualify(draft.Text, origin.CurrentSchema);
                var owner = q.TargetOwner ?? draft.TargetOwner ?? origin.CurrentSchema; // 대상을 읽지 못하면 분류기 대상, 그다음 세션의 현재 스키마(설계 9.3)
                if (ChangeBundleRules.Decide(bundle, origin.Dbms, owner) != ChangeBundleDecision.Add)
                {
                    skipped++;
                    continue;
                }

                if (await _store.FindSameAsync(bundle.Id, q.Text, ct).ConfigureAwait(false) is { } same)
                {
                    if (executed)
                    {
                        await _store.AddRunAsync(Run(same.Id, origin), ct).ConfigureAwait(false);
                        marked++;
                    }
                    else
                    {
                        present++;
                    }

                    continue;
                }

                if (bundle.Dbms is null || bundle.TargetOwners.Count == 0)
                {
                    // 빈 묶음은 첫 문장의 DBMS 를, 대상 소유자가 빈 묶음은 첫 문장의 소유자를 따른다.
                    IReadOnlyList<string> owners = bundle.TargetOwners.Count == 0 ? [owner] : bundle.TargetOwners;
                    await _store.UpdateTargetAsync(bundle.Id, origin.Dbms, owners, ct).ConfigureAwait(false);
                    bundle = bundle with { Dbms = origin.Dbms, TargetOwners = owners };
                }

                var cautions = q.Understood ? ChangeBundleCautions.None : ChangeBundleCautions.OwnerNotQualified;
                var item = await _store.AddItemAsync(bundle.Id, new NewChangeBundleItem(q.Text, source, owner, q.TargetName, cautions, draft.Warning), ct)
                    .ConfigureAwait(false);
                if (item is null)
                {
                    continue;
                }

                added++;
                if (executed)
                {
                    await _store.AddRunAsync(Run(item.Id, origin), ct).ConfigureAwait(false);
                }
            }

            if (added + marked > 0)
            {
                return new ChangeBundleAddResult(ChangeBundleAddOutcome.Done, bundle.Id, added, marked, skipped, present);
            }

            var outcome = present > 0 ? ChangeBundleAddOutcome.AlreadyPresent
                : skipped > 0 ? ChangeBundleAddOutcome.OwnerNotTarget
                : ChangeBundleAddOutcome.Done;
            return new ChangeBundleAddResult(outcome, bundle.Id, 0, 0, skipped, present);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ChangeBundleAddResult(ChangeBundleAddOutcome.NotWritable, bundleId, added, marked, 0, 0);
        }
        finally
        {
            _gate.Release();
            if (added + marked > 0)
            {
                _events.Publish(new ChangeBundleChanged(bundleId, added, marked)); // 중간에 실패해도 넣은 것은 창에 보인다(문 밖에서 알림)
            }
        }
    }

    private static ChangeBundleRun Run(long itemId, ChangeBundleOrigin origin)
        => new(itemId, origin.Environment, origin.ConnectionName, DateTimeOffset.UtcNow, true);
}
