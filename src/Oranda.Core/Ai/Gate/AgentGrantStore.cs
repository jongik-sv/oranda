using System.Security.Cryptography;
using System.Text;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Events;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 다리·게이트가 쓰는 허가 쪽 기능(A2). 연결 창이 보는 <see cref="IAgentGrants"/> 와 나눠 둔 까닭: 처리자·화면은 하위 세션과 토큰 상태를 몰라도 된다.
/// </summary>
public interface IAgentGrantSessions
{
    /// <summary>토큰을 푼다. 살아 있으면 허가, 기한이 지났으면 <see cref="AgentGrantLookup.Expired"/>, 모르면 둘 다 없음.</summary>
    AgentGrantLookup Lookup(string token);

    /// <summary>허가 id 로 지금 사본을 찾는다. 끝난(폐기·만료) 허가면 null.</summary>
    AgentGrantView? Find(string grantId);

    /// <summary>
    /// 허가 안 접속 하나의 하위 세션(설계 §3.2). 접속 × 클라이언트마다 하나를 처음 쓸 때 만든다 — 여러 CLI 가 한 허가를 같이 써도 감사의 <c>client</c> 칸이 섞이지 않는다(§10-10).
    /// 허가가 끝났거나 접속이 허가 밖이면 null.
    /// </summary>
    AgentSession? SessionFor(string grantId, Guid profileId, string? client);

    /// <summary>hello 의 클라이언트 이름을 더한다(<see cref="IAgentGrants.AddClient"/> 와 같다).</summary>
    AgentGrantView? AddClient(string grantId, string clientName);

    /// <summary>연결 파일을 쓰는 쪽을 붙인다(다리가 열릴 때). null 이면 뗀다. 붙지 않은 동안 <see cref="IAgentGrants.Create"/> 는 실패한다(다리가 없으면 붙을 곳이 없다).</summary>
    void AttachWriter(IAgentEndpointWriter? writer);

    /// <summary>모든 허가를 폐기한다(앱 종료·다리 닫기).</summary>
    void RevokeAll();
}

/// <summary><see cref="IAgentGrantSessions.Lookup"/> 결과.</summary>
public readonly record struct AgentGrantLookup(AgentGrantView? Grant, bool Expired);

/// <summary>
/// 연결 파일 쓰기(A4, Platform 구현). 허가 저장소가 만들기·바뀜·끝남 때 부른다. 토큰 원문은 <see cref="Publish"/> 에만 지나간다.
/// 실패는 예외로 알린다(<see cref="Publish"/> 실패면 허가를 만들지 않는다).
/// </summary>
public interface IAgentEndpointWriter
{
    void Publish(AgentGrantIssue issue);

    /// <summary>기한·접속 수준이 바뀌었다. 이미 쓴 파일의 토큰은 그대로 두고 나머지를 고쳐 쓴다.</summary>
    void Update(AgentGrantView grant);

    void Remove(AgentGrantView grant);
}

/// <summary>
/// 메모리 외부 허가 저장소(설계 §3.2·§6.4, A2). 토큰은 SHA-256 해시로만 둔다(<see cref="AgentRunTokenStore"/> 와 같은 방식). 스레드 안전.
/// 창(프로세스)마다 허가는 하나다 — 다시 켜면 옛 허가를 폐기하고 새 토큰을 준다. 기한이 되면 스스로 폐기한다.
/// 접속 설정이 저장되면(<see cref="ConnectionProfileSaved"/>) 그 접속의 수준을 다시 계산하고 하위 세션을 폐기한다(다음 호출이 새 설정으로 다시 만든다).
/// </summary>
public sealed class AgentGrantStore : IAgentGrants, IAgentGrantSessions, IDisposable
{
    /// <summary>만료된 토큰을 「만료」 로 알려 줄 수(오래된 것부터 잊는다).</summary>
    private const int ExpiredMemory = 16;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly IDisposable? _subscription;
    private readonly Queue<string> _expiredKeys = new();
    private Entry? _entry;
    private IAgentEndpointWriter? _writer;

    public AgentGrantStore(TimeProvider? time = null, IEventBus? events = null)
    {
        _time = time ?? TimeProvider.System;
        _subscription = events?.Subscribe<ConnectionProfileSaved>(this, e => ProfileSaved(e.Profile));
    }

    public event EventHandler<AgentGrantChanged>? Changed;

    public IReadOnlyList<AgentGrantView> Active
    {
        get
        {
            lock (_gate)
            {
                return _entry is { } entry && !IsExpired(entry) ? [entry.View] : [];
            }
        }
    }

    public AgentGrantIssue Create(AgentGrantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Targets);
        var now = _time.GetUtcNow();
        var token = AgentRunToken.Generate();
        var entry = new Entry(
            "ext-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            Key(token),
            now,
            request.Lifetime is { } life ? now + life : null,
            request.AllowUnboundTabText,
            request.Label ?? string.Empty);
        foreach (var target in request.Targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (entry.Targets.All(t => t.Profile.Id != target.Profile.Id))
            {
                entry.Targets.Add(target);
            }
        }

        entry.Refresh();
        var issue = new AgentGrantIssue(entry.View, token);

        Entry? old;
        IAgentEndpointWriter writer;
        lock (_gate)
        {
            writer = _writer ?? throw new InvalidOperationException("The agent bridge is not open; turn on AI tuning first.");
            old = _entry;
            _entry = null;
        }

        if (old is not null)
        {
            End(old, writer);
        }

        // 파일을 먼저 쓴다. 못 쓰면 허가를 만들지 않는다(밖에서 붙을 길이 없는 토큰을 남기지 않는다).
        writer.Publish(issue);
        lock (_gate)
        {
            _entry = entry;
            Schedule(entry);
        }

        Raise(entry.View, ended: false);
        return issue;
    }

    public AgentGrantView? Resolve(string token) => Lookup(token).Grant;

    public AgentGrantLookup Lookup(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return default;
        }

        var key = Key(token);
        Entry? expired = null;
        lock (_gate)
        {
            if (_entry is { } entry && string.Equals(entry.TokenKey, key, StringComparison.Ordinal))
            {
                if (!IsExpired(entry))
                {
                    return new AgentGrantLookup(entry.View, false);
                }

                expired = entry;
                _entry = null;
            }
            else
            {
                return _expiredKeys.Contains(key) ? new AgentGrantLookup(null, true) : default;
            }
        }

        // 타이머보다 먼저 만료를 본 경우: 여기서 끝낸다.
        End(expired, CurrentWriter(), expiredNow: true);
        return new AgentGrantLookup(null, true);
    }

    public AgentGrantView? Find(string grantId)
    {
        lock (_gate)
        {
            return _entry is { } entry && entry.Id == grantId && !IsExpired(entry) ? entry.View : null;
        }
    }

    public AgentGrantView? Extend(string grantId, TimeSpan by)
    {
        AgentGrantView view;
        lock (_gate)
        {
            if (_entry is not { } entry || entry.Id != grantId || IsExpired(entry))
            {
                return null;
            }

            entry.ExpiresAt = _time.GetUtcNow() + by;
            entry.Refresh();
            Schedule(entry);
            view = entry.View;
        }

        TryWriter(w => w.Update(view));
        Raise(view, ended: false);
        return view;
    }

    public AgentGrantView? AddClient(string grantId, string clientName)
    {
        AgentGrantView view;
        lock (_gate)
        {
            if (_entry is not { } entry || entry.Id != grantId || IsExpired(entry))
            {
                return null;
            }

            var name = string.IsNullOrWhiteSpace(clientName) ? "unknown" : clientName.Trim();
            if (entry.Clients.Contains(name, StringComparer.Ordinal))
            {
                return entry.View;
            }

            entry.Clients.Add(name);
            entry.Refresh();
            view = entry.View;
        }

        Raise(view, ended: false);
        return view;
    }

    public void Revoke(string grantId)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = _entry is { } e && e.Id == grantId ? e : null;
            if (entry is not null)
            {
                _entry = null;
            }
        }

        if (entry is not null)
        {
            End(entry, CurrentWriter());
        }
    }

    public void RevokeAll()
    {
        Entry? entry;
        lock (_gate)
        {
            entry = _entry;
            _entry = null;
        }

        if (entry is not null)
        {
            End(entry, CurrentWriter());
        }
    }

    public AgentSession? SessionFor(string grantId, Guid profileId, string? client)
    {
        lock (_gate)
        {
            if (_entry is not { } entry || entry.Id != grantId || IsExpired(entry))
            {
                return null;
            }

            var target = entry.Targets.FirstOrDefault(t => t.Profile.Id == profileId);
            if (target is null)
            {
                return null;
            }

            var key = (profileId, client ?? string.Empty);
            if (entry.Sessions.TryGetValue(key, out var existing) && !existing.IsRevoked)
            {
                return existing;
            }

            var session = NewSession(entry, target, client);
            entry.Sessions[key] = session;
            return session;
        }
    }

    public void AttachWriter(IAgentEndpointWriter? writer)
    {
        lock (_gate)
        {
            _writer = writer;
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        RevokeAll();
    }

    /// <summary>
    /// 하위 세션 하나: 실행 id = <c>허가 id:접속 별칭</c>, 시작 문장 없음, 보낼 범위 = 그 접속 전체(E-17), 수준 = 허가의 수준.
    /// 운영 접속은 결과의 SQL 글을 가리도록 빈 리터럴 사전을 둔다(DDL·소스·이력 글의 값이 밖으로 나가지 않게).
    /// </summary>
    private static AgentSession NewSession(Entry entry, AgentGrantTarget target, string? client)
    {
        var profile = target.Profile;
        var level = AgentGrantPolicy.LevelFor(profile, target.Cap);
        var production = profile.Environment == ConnectionEnvironment.Production;
        var scope = new AgentRunScope(
            entry.Id + ":" + profile.Name,
            profile,
            new AiContext(profile.Name, profile.Environment, level),
            new AgentSendScope(true, []))
        {
            External = true,
            Literals = production ? new LiteralVault() : null,
            MaskLiteralsRequired = production,
        };
        return new AgentSession(scope) { Client = client };
    }

    private void ProfileSaved(ConnectionProfile profile)
    {
        AgentGrantView view;
        lock (_gate)
        {
            if (_entry is not { } entry)
            {
                return;
            }

            var index = entry.Targets.FindIndex(t => t.Profile.Id == profile.Id);
            if (index < 0)
            {
                return;
            }

            entry.Targets[index] = entry.Targets[index] with { Profile = profile };
            foreach (var (key, session) in entry.Sessions.ToList())
            {
                if (key.ProfileId == profile.Id)
                {
                    session.Revoke();
                    entry.Sessions.Remove(key);
                }
            }

            entry.Refresh();
            view = entry.View;
        }

        TryWriter(w => w.Update(view));
        Raise(view, ended: false);
    }

    private void End(Entry entry, IAgentEndpointWriter? writer, bool expiredNow = false)
    {
        lock (_gate)
        {
            entry.Timer?.Dispose();
            entry.Timer = null;
            foreach (var session in entry.Sessions.Values)
            {
                session.Revoke();
            }

            entry.Sessions.Clear();
            if (expiredNow || IsExpired(entry))
            {
                _expiredKeys.Enqueue(entry.TokenKey);
                while (_expiredKeys.Count > ExpiredMemory)
                {
                    _expiredKeys.Dequeue();
                }
            }
        }

        if (writer is not null)
        {
            try
            {
                writer.Remove(entry.View);
            }
            catch (Exception)
            {
                // 파일을 못 지워도 토큰은 폐기됐다. 남은 파일은 다음 시작의 청소와 orandactl 의 거부가 막는다.
            }
        }

        Raise(entry.View, ended: true);
    }

    private void Schedule(Entry entry)
    {
        entry.Timer?.Dispose();
        entry.Timer = null;
        if (entry.ExpiresAt is not { } at)
        {
            return;
        }

        var due = at - _time.GetUtcNow();
        entry.Timer = _time.CreateTimer(_ => Expire(entry), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private void Expire(Entry entry)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_entry, entry) || !IsExpired(entry))
            {
                return; // 연장됐거나 이미 끝났다
            }

            _entry = null;
        }

        End(entry, CurrentWriter(), expiredNow: true);
    }

    private bool IsExpired(Entry entry) => entry.ExpiresAt is { } at && _time.GetUtcNow() >= at;

    private IAgentEndpointWriter? CurrentWriter()
    {
        lock (_gate)
        {
            return _writer;
        }
    }

    private void TryWriter(Action<IAgentEndpointWriter> action)
    {
        if (CurrentWriter() is not { } writer)
        {
            return;
        }

        try
        {
            action(writer);
        }
        catch (Exception)
        {
            // 파일 고쳐 쓰기 실패는 허가를 막지 않는다(orandactl 은 낡은 만료 시각을 보고도 앱의 판단을 받는다).
        }
    }

    private void Raise(AgentGrantView view, bool ended)
    {
        try
        {
            Changed?.Invoke(this, new AgentGrantChanged(view, ended));
        }
        catch (Exception)
        {
            // 화면 구독자의 오류가 허가 처리를 막지 않는다.
        }
    }

    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class Entry(string id, string tokenKey, DateTimeOffset createdAt, DateTimeOffset? expiresAt, bool allowUnbound, string label)
    {
        public string Id { get; } = id;

        public string TokenKey { get; } = tokenKey;

        public DateTimeOffset? ExpiresAt { get; set; } = expiresAt;

        public List<AgentGrantTarget> Targets { get; } = [];

        public List<string> Clients { get; } = [];

        public Dictionary<(Guid ProfileId, string Client), AgentSession> Sessions { get; } = [];

        public ITimer? Timer { get; set; }

        public AgentGrantView View { get; private set; } = null!;

        public void Refresh() => View = new AgentGrantView(
            Id,
            [.. Targets.Select(AgentGrantPolicy.ToConnection)],
            createdAt,
            ExpiresAt,
            [.. Clients],
            allowUnbound,
            label);
    }
}
