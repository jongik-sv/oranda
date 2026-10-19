using System.Collections.Concurrent;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.History;

/// <summary>
/// <c>StatementExecuted</c> 를 구독해 이력에 남긴다(EXEC-07). 시작 직후 설정을 읽어 보존 정책을 한 번 적용하고,
/// 이후 기록 1,000건마다 다시 적용한다(data-model 3.4). <c>history.enabled=false</c> 나 <c>SkipHistory</c> 는 기록하지 않는다.
/// 바인드 값은 <c>history.saveBindValues</c> 가 켜져 있고 호출 쪽이 <see cref="BindValuesProvider"/> 로 알려 준 경우에만 저장한다
/// (이름이 pass·pwd·secret·token 인 바인드는 <c>***</c>). 소유: TSK-06-06.
/// </summary>
[OrandaService(typeof(IStartupTask))]
internal sealed class HistoryRecorder : IStartupTask
{
    private const int PruneEvery = 1000;

    private readonly IEventBus _events;
    private readonly IHistoryStore _store;
    private readonly ISettingsStore _settingsStore;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<Guid, (ConnectionProfile Profile, string? DbKey)> _sessions = new();
    private readonly object _chain = new();
    private readonly List<IDisposable> _subscriptions = [];
    private HistorySettings _settings = new();
    private Task _tail = Task.CompletedTask;
    private int _sincePrune;

    public HistoryRecorder(IEventBus events, IHistoryStore store, ISettingsStore settingsStore, IServiceProvider services)
    {
        _events = events;
        _store = store;
        _settingsStore = settingsStore;
        _services = services;
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    /// <summary>
    /// 사용자가 값을 정한 바인드(이름 → 값 JSON 조각)를 돌려주는 연결 지점. 실행 화면(TSK-06-02)이 채운다.
    /// 반환이 null 이면 binds_json 은 비운다.
    /// </summary>
    public static Func<StatementExecuted, IReadOnlyDictionary<string, string?>?>? BindValuesProvider { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        await ReloadSettingsAsync(ct).ConfigureAwait(false);
        _subscriptions.Add(_events.Subscribe<SessionOpened>(this, e => _sessions[e.SessionId] = (e.Profile, null)));
        _subscriptions.Add(_events.Subscribe<StatementExecuted>(this, OnStatementExecuted));
        _subscriptions.Add(_events.Subscribe<SettingsChanged>(this, e =>
        {
            if (string.Equals(e.Section, "history", StringComparison.OrdinalIgnoreCase))
            {
                Enqueue(t => ReloadSettingsAsync(CancellationToken.None));
            }
        }));
        try
        {
            await _store.PruneAsync(_settings, ct).ConfigureAwait(false);
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

    internal HistorySettings CurrentSettings => _settings;

    private async Task ReloadSettingsAsync(CancellationToken ct)
    {
        try
        {
            var doc = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
            _settings = doc.Value.History;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 설정을 못 읽으면 기본값(기록 켬)을 쓴다.
        }
    }

    private void OnStatementExecuted(StatementExecuted e)
    {
        if (e.Options.SkipHistory || !_settings.Enabled)
        {
            return;
        }

        var entry = Build(e);
        if (entry is null)
        {
            return;
        }

        Enqueue(async t =>
        {
            try
            {
                await _store.AddAsync(entry, CancellationToken.None).ConfigureAwait(false);
                if (Interlocked.Increment(ref _sincePrune) >= PruneEvery)
                {
                    Interlocked.Exchange(ref _sincePrune, 0);
                    await _store.PruneAsync(_settings, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 이력 기록 실패가 실행 흐름을 막으면 안 된다.
            }
        });
    }

    private void Enqueue(Func<Task, Task> work)
    {
        lock (_chain)
        {
            _tail = _tail.ContinueWith(work, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private HistoryEntry? Build(StatementExecuted e)
    {
        var (profile, dbKey) = Resolve(e.SessionId);
        if (profile is null)
        {
            return null;
        }

        var r = e.Result;
        string? code = r.Error?.Code;
        string? message = r.Error?.Message;
        if (r.Status == ExecutionStatus.Denied && r.Policy is { } p)
        {
            code ??= "POLICY";
            message ??= p.Message;
        }

        return new HistoryEntry
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            DbKey = dbKey,
            ExecutedAt = e.OccurredAt,
            SqlText = e.Statement.Text,
            StatementKind = e.Statement.Kind,
            Source = e.Options.Source,
            Status = (HistoryStatus)(int)r.Status,
            ElapsedMs = (long)r.Elapsed.TotalMilliseconds,
            RowsAffected = r.RowsAffected,
            ErrorCode = code,
            ErrorMessage = message,
            BindsJson = BuildBindsJson(e),
        };
    }

    private string? BuildBindsJson(StatementExecuted e)
    {
        if (!_settings.SaveBindValues || BindValuesProvider?.Invoke(e) is not { Count: > 0 } values)
        {
            return null;
        }

        var redactor = _services.GetService(typeof(Oranda.Core.Security.ISecretRedactor)) as Oranda.Core.Security.ISecretRedactor;
        var masked = values.ToDictionary(
            kv => kv.Key,
            kv => redactor?.IsSensitiveBindName(kv.Key) == true ? "***" : kv.Value,
            StringComparer.Ordinal);
        return System.Text.Json.JsonSerializer.Serialize(masked);
    }

    private (ConnectionProfile? Profile, string? DbKey) Resolve(Guid sessionId)
    {
        var session = (_services.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is not null)
        {
            _sessions[sessionId] = (session.Profile, session.DbKey);
            return (session.Profile, session.DbKey);
        }

        return _sessions.TryGetValue(sessionId, out var known) ? (known.Profile, known.DbKey) : (null, null);
    }
}
