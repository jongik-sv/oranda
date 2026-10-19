using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Oranda.Core.Events;

/// <summary>
/// 약한 참조 이벤트 버스(TRD 7). 앱 조립이 단일 인스턴스로 등록한다(Core 는 스캔 대상이 아니다).
/// 처리기는 owner 에 <see cref="ConditionalWeakTable{TKey,TValue}"/> 로 묶여 owner 가 살아 있는 동안만 산다.
/// 버스 자신은 owner·처리기를 약하게만 잡으므로 owner 가 수거되면 구독이 저절로 끊긴다.
/// 발행은 발행 스레드에서 동기로 처리하고, 처리기 예외는 다른 구독자에게 전파하지 않는다.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<object, KeepAlive> _keepAlive = new();
    private readonly Action<Exception, OrandaEvent>? _onHandlerError;
    private Entry[] _entries = [];

    /// <param name="onHandlerError">처리기 예외 통지(로그). null 이면 <see cref="Trace"/> 에 남긴다.</param>
    public EventBus(Action<Exception, OrandaEvent>? onHandlerError = null) => _onHandlerError = onHandlerError;

    /// <summary>살아 있는 구독 수(시험·진단용). 수거된 owner 의 구독은 세지 않는다.</summary>
    public int SubscriptionCount => Volatile.Read(ref _entries).Count(e => e.IsAlive);

    public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
        where TEvent : OrandaEvent
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(handler);

        var holder = new Holder(e => handler((TEvent)e));
        _keepAlive.GetOrCreateValue(owner).Add(holder);
        var entry = new Entry(typeof(TEvent), new WeakReference<object>(owner), new WeakReference<Holder>(holder));
        lock (_gate)
        {
            _entries = [.. _entries.Where(e => e.IsAlive), entry];
        }

        return new Subscription(() => Remove(entry, holder));
    }

    public void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent
    {
        ArgumentNullException.ThrowIfNull(e);
        var snapshot = Volatile.Read(ref _entries);
        var sawDead = false;
        foreach (var entry in snapshot)
        {
            if (!entry.TryGetHolder(out var holder))
            {
                sawDead = true;
                continue;
            }

            if (!entry.EventType.IsInstanceOfType(e))
            {
                continue;
            }

            try
            {
                holder.Invoke(e);
            }
            catch (Exception ex)
            {
                Report(ex, e);
            }
        }

        if (sawDead)
        {
            lock (_gate)
            {
                _entries = [.. _entries.Where(x => x.IsAlive)];
            }
        }
    }

    private void Report(Exception ex, OrandaEvent e)
    {
        try
        {
            if (_onHandlerError is not null)
            {
                _onHandlerError(ex, e);
                return;
            }

            Trace.TraceError("이벤트 처리기 예외 ({0}): {1}", e.GetType().Name, ex);
        }
        catch (Exception)
        {
            // 로그 실패는 발행자에게 전파하지 않는다.
        }
    }

    private void Remove(Entry entry, Holder holder)
    {
        lock (_gate)
        {
            _entries = [.. _entries.Where(e => !ReferenceEquals(e, entry) && e.IsAlive)];
        }

        if (entry.Owner.TryGetTarget(out var owner) && _keepAlive.TryGetValue(owner, out var keep))
        {
            keep.Remove(holder);
        }
    }

    private sealed class Holder(Action<OrandaEvent> invoke)
    {
        public void Invoke(OrandaEvent e) => invoke(e);
    }

    private sealed class KeepAlive
    {
        private readonly List<Holder> _holders = [];

        public void Add(Holder h)
        {
            lock (_holders)
            {
                _holders.Add(h);
            }
        }

        public void Remove(Holder h)
        {
            lock (_holders)
            {
                _holders.Remove(h);
            }
        }
    }

    private sealed class Entry(Type eventType, WeakReference<object> owner, WeakReference<Holder> holder)
    {
        public Type EventType { get; } = eventType;

        public WeakReference<object> Owner { get; } = owner;

        public bool IsAlive => Owner.TryGetTarget(out _) && holder.TryGetTarget(out _);

        public bool TryGetHolder([NotNullWhen(true)] out Holder? h)
        {
            h = null;
            return Owner.TryGetTarget(out _) && holder.TryGetTarget(out h);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
