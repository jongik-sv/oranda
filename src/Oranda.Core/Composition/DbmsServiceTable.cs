using Oranda.Core.Connection;

namespace Oranda.Core.Composition;

/// <summary>
/// (서비스 형식, DBMS) → 구현 표. 앱 조립이 <see cref="OrandaServiceAttribute.ForDbms"/> 가 있는 등록을 여기에 넣는다.
/// 같은 키를 다시 넣으면 나중 것이 이긴다(컨테이너의 「마지막 등록」 규칙과 같음). 스레드 안전.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>싱글턴은 처음 꺼낼 때 한 번 만든다. 만들다 예외가 나면 그 예외를 기억해 다음 꺼냄에도 같은 예외를 낸다(설정 오류 = 고칠 때까지 실패).</item>
/// <item>만드는 함수 안에서 같은 키를 다시 꺼내면 <see cref="InvalidOperationException"/>(순환).</item>
/// <item><c>ensureLoaded</c> 는 답하기 전에 부른다 — 컨테이너가 지연 어셈블리(<c>Oranda.Tibero</c> 등)를 훑어 이 표를 채우게 하는 자리다.</item>
/// </list>
/// </remarks>
public sealed class DbmsServiceTable : IDbmsServiceSelector
{
    private readonly object _gate = new();
    private readonly Dictionary<(Type Service, Dbms Dbms), Entry> _entries = [];
    private readonly Action? _ensureLoaded;

    /// <param name="ensureLoaded">
    /// <see cref="Find{T}"/>·<see cref="AvailableDbms"/> 가 답하기 전에 부르는 준비(예: 컨테이너의 지연 어셈블리 훑기).
    /// 여러 번 불려도 되게(두 번째부터는 아무것도 하지 않게) 만든다. null = 준비 없음.
    /// </param>
    public DbmsServiceTable(Action? ensureLoaded = null)
    {
        _ensureLoaded = ensureLoaded;
    }

    public IReadOnlyCollection<Dbms> AvailableDbms
    {
        get
        {
            _ensureLoaded?.Invoke();
            lock (_gate)
            {
                return [.. _entries.Keys.Select(k => k.Dbms).Distinct().Order()];
            }
        }
    }

    /// <summary>구현을 만드는 방법을 넣는다. <paramref name="factory"/> 결과는 <paramref name="serviceType"/> 이어야 한다.</summary>
    public DbmsServiceTable Add(Type serviceType, Dbms dbms, Func<object> factory, ServiceLifetimeKind lifetime = ServiceLifetimeKind.Singleton)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            _entries[(serviceType, dbms)] = new Entry(factory, lifetime);
        }

        return this;
    }

    public DbmsServiceTable Add<T>(Dbms dbms, Func<T> factory, ServiceLifetimeKind lifetime = ServiceLifetimeKind.Singleton)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Add(typeof(T), dbms, factory, lifetime);
    }

    public T? Find<T>(Dbms dbms)
        where T : class
    {
        _ensureLoaded?.Invoke();
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue((typeof(T), dbms), out entry);
        }

        if (entry is null)
        {
            return null;
        }

        var value = entry.Get();
        return value as T
            ?? throw new InvalidOperationException($"{dbms} 용 '{typeof(T).Name}' 등록이 그 형식이 아닌 '{value.GetType().Name}' 를 만들었다.");
    }

    private sealed class Entry(Func<object> factory, ServiceLifetimeKind lifetime)
    {
        private readonly Lazy<object> _singleton = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

        public object Get() => lifetime == ServiceLifetimeKind.Transient ? factory() : _singleton.Value;
    }
}
