using Oranda.Core.Events;

namespace Oranda.Testing;

/// <summary>
/// 발행된 이벤트를 기록하고 구독자에게 동기로 전달하는 버스. 시험에서는 수거를 기다리지 않도록 처리기를 강하게 잡는다.
/// </summary>
public sealed class FakeEventBus : IEventBus
{
    private readonly List<OrandaEvent> _published = [];
    private readonly List<(Type Type, Delegate Handler)> _handlers = [];

    public IReadOnlyList<OrandaEvent> Published => _published;

    public IEnumerable<TEvent> PublishedOf<TEvent>()
        where TEvent : OrandaEvent => _published.OfType<TEvent>();

    public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
        where TEvent : OrandaEvent
    {
        var entry = (typeof(TEvent), (Delegate)handler);
        _handlers.Add(entry);
        return new Subscription(() => _handlers.Remove(entry));
    }

    public void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent
    {
        ArgumentNullException.ThrowIfNull(e);
        _published.Add(e);
        foreach (var (type, handler) in _handlers.ToList())
        {
            if (type.IsInstanceOfType(e))
            {
                handler.DynamicInvoke(e);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
