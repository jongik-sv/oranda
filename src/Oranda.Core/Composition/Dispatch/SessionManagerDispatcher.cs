using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ISessionManager), Dbms = …)]</c>.
/// 세션을 열 때는 프로필의 DBMS(<see cref="ConnectionProfile.Dbms"/>)로 공급자를 고른다.
/// </summary>
/// <remarks>
/// <see cref="Sessions"/> 는 이 디스패처로 세션을 연 적이 있는 DBMS 공급자들의 세션을 DBMS 순서(Oracle 먼저)로 이어 붙인 것이다.
/// 고르기(<see cref="IDbmsServiceSelector.AvailableDbms"/>)를 묻지 않는다 — 묻는 순간 지연 어셈블리(<c>Oranda.Oracle</c>)를 로드하는데,
/// 시작 경로가 「지금 세션이 있나」만 물어도(예: 등록된 세션 관리자의 <c>Sessions</c>) Oracle 을 깨우게 된다(TRD 8).
/// 한 번도 열지 않은 공급자에는 세션이 없으므로 결과는 같다. 세션은 이 디스패처(앱이 보는 <see cref="ISessionManager"/>)로 연다.
/// </remarks>
[OrandaService(typeof(ISessionManager))]
public sealed class SessionManagerDispatcher : ISessionManager
{
    private readonly IDbmsServiceSelector _selector;
    private readonly object _gate = new();
    // DBMS 순서(Oracle 먼저), 같은 DBMS 안에서는 처음 연 순서. 같은 DBMS 의 관리자가 바뀌어도(표 항목 교체·Transient) 옛 관리자의 세션을 잃지 않는다.
    private readonly SortedDictionary<Dbms, List<ISessionManager>> _opened = [];

    public SessionManagerDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public IReadOnlyList<ISession> Sessions
    {
        get
        {
            ISessionManager[] managers;
            lock (_gate)
            {
                managers = [.. _opened.Values.SelectMany(l => l)];
            }

            return managers.Length switch
            {
                0 => [],
                1 => managers[0].Sessions,
                _ => [.. managers.SelectMany(m => m.Sessions)],
            };
        }
    }

    public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // 공급자가 없는 DBMS 프로필은 ArgumentException(예전 OracleSessionManager 가 Oracle 아닌 프로필을 거부하던 형식과 같다 —
        // 접속 화면이 이 형식을 받아 「접속 실패」로 보인다). DbmsNotSupportedException 은 안쪽 예외로 남긴다.
        ISessionManager manager;
        try
        {
            manager = _selector.Get<ISessionManager>(profile.Dbms);
        }
        catch (DbmsNotSupportedException ex)
        {
            throw new ArgumentException(ex.Message, nameof(profile), ex);
        }

        lock (_gate)
        {
            if (!_opened.TryGetValue(profile.Dbms, out var list))
            {
                _opened[profile.Dbms] = list = [];
            }

            if (!list.Contains(manager))
            {
                list.Add(manager);
            }
        }

        return manager.OpenAsync(profile, credential, purpose, ct);
    }
}
