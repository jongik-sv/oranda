using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Ai.Agent;

/// <summary>
/// 에이전트 세션의 출처(앱 구현). 진행 중인 AI 요청(<see cref="AgentSession"/>)을 실행 id 로 찾아 주고,
/// 도구 조회에 쓸 DB 세션을 고른다. 이번 회차는 모니터 세션(<see cref="SessionPurpose.Monitor"/>)을 접속마다 하나씩 재사용한다 —
/// 사용자 작업 세션과 섞이지 않는다. 전용 읽기 전용 에이전트 세션은 다음 회차다.
/// 비밀번호가 저장돼 있지 않으면 모니터와 같이 비밀번호를 묻는다.
/// </summary>
/// <param name="container">모니터 세션 여는 도구(<see cref="SessionMonitorCommands.CreateOpener"/>)를 만들 때 쓴다.</param>
public sealed class AppAgentSessionSource(ServiceContainer container) : IAgentSessionSource, IDisposable
{
    private const string OpenerId = "aiAgent";

    private readonly ConcurrentDictionary<string, AgentSession> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Lazy<Task<ISession?>>> _dbSessions = new();
    private MonitorSessionOpener? _opener;

    /// <summary>앱에 하나 있는 출처(접속 편집 저장이 진행 중인 요청을 폐기할 때 찾는다). 없으면 null.</summary>
    public static AppAgentSessionSource? Current { get; private set; }

    /// <summary>
    /// 접속 설정(환경 라벨·AI 허용 수준)이 바뀐 접속의 진행 중인 요청을 모두 폐기한다.
    /// 실행 범위의 프로필은 시작 때 사본이라, 이미 시작된 실행의 허용 수준은 따라 바뀌지 않기 때문이다.
    /// </summary>
    public void RevokeProfile(Guid profileId)
    {
        foreach (var (runId, session) in _runs)
        {
            if (session.Profile.Id == profileId)
            {
                session.Revoke();
                _runs.TryRemove(runId, out _);
            }
        }

        if (_dbSessions.TryRemove(profileId, out var old))
        {
            CloseLater(old); // 바뀐 접속 정보로 다시 열게 한다
        }
    }

    private static void CloseLater(Lazy<Task<ISession?>> entry)
    {
        if (entry.IsValueCreated && entry.Value.IsCompletedSuccessfully && entry.Value.Result is { } db)
        {
            _ = db.DisposeAsync().AsTask();
        }
    }

    /// <summary>이 출처를 앱 전체에서 찾을 수 있게 한다(컨테이너가 만든 뒤 한 번).</summary>
    public AppAgentSessionSource Publish()
    {
        Current = this;
        return this;
    }

    /// <summary>AI 요청을 시작하는 쪽이 만든 세션을 등록한다. 폐기(<see cref="AgentSession.Revoke"/>)되면 찾을 수 없다.</summary>
    public AgentSession Track(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _runs[session.RunId] = session;
        return session;
    }

    public AgentSession? Find(string runId)
    {
        if (runId is not null && _runs.TryGetValue(runId, out var session))
        {
            if (!session.IsRevoked)
            {
                return session;
            }

            _runs.TryRemove(runId, out _); // 끝난 실행은 목록에서 뺀다
        }

        return null;
    }

    public async ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var profile = session.Profile;
        var key = profile.Id;

        // 못 열었거나(실패 포함) 닫힌 세션은 캐시에서 빼서 다음 시도가 다시 열게 한다.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var lazy = _dbSessions.GetOrAdd(key, _ => new Lazy<Task<ISession?>>(() => OpenOnUiThreadAsync(profile)));
            ISession? opened;
            try
            {
                opened = await lazy.Value.WaitAsync(ct).ConfigureAwait(true);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _dbSessions.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, Lazy<Task<ISession?>>>(key, lazy));
                if (attempt == 1)
                {
                    throw;
                }

                continue;
            }

            if (opened is { State: not (SessionState.Broken or SessionState.Closed) })
            {
                return opened;
            }

            _dbSessions.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, Lazy<Task<ISession?>>>(key, lazy));
        }

        throw new InvalidOperationException("AI 조회에 쓸 데이터베이스 세션을 열지 못했습니다(비밀번호 입력을 취소했거나 접속에 실패했습니다).");
    }

    /// <summary>
    /// 모니터 세션 여는 도구는 비밀번호 창을 띄울 수 있으므로 UI 스레드에서 연다(도구 호출은 스레드 풀에서 들어온다).
    /// </summary>
    private async Task<ISession?> OpenOnUiThreadAsync(ConnectionProfile profile) =>
        await Dispatcher.UIThread.InvokeAsync<ISession?>(
            () => (_opener ??= SessionMonitorCommands.CreateOpener(container, OpenerId)).OpenAsync(profile, CancellationToken.None)).ConfigureAwait(false);

    public void Dispose()
    {
        foreach (var entry in _dbSessions.Values)
        {
            CloseLater(entry);
        }

        _dbSessions.Clear();
        _runs.Clear();
    }
}
