using System.Runtime.CompilerServices;
using Oranda.Core.Events;

namespace Oranda.Core.Tests.Events;

// 소유: TSK-03-01. 약한 참조 구독·발행.
public class EventBusTests
{
    private static SettingsChanged Ev(string section = "ui") => new(section);

    [Fact]
    public void Publish_calls_subscribers_in_order_on_the_same_thread()
    {
        var bus = new EventBus();
        var owner = new object();
        var calls = new List<string>();
        var thread = Environment.CurrentManagedThreadId;
        var sameThread = true;
        using var a = bus.Subscribe<SettingsChanged>(owner, e => { calls.Add("a:" + e.Section); sameThread &= Environment.CurrentManagedThreadId == thread; });
        using var b = bus.Subscribe<SettingsChanged>(owner, e => calls.Add("b:" + e.Section));

        bus.Publish(Ev());

        Assert.Equal(["a:ui", "b:ui"], calls);
        Assert.True(sameThread);
    }

    [Fact]
    public void Only_matching_event_types_are_delivered()
    {
        var bus = new EventBus();
        var owner = new object();
        var closed = 0;
        using var s = bus.Subscribe<SessionClosed>(owner, _ => closed++);

        bus.Publish(Ev());
        bus.Publish(new SessionClosed(Guid.NewGuid()));

        Assert.Equal(1, closed);
    }

    [Fact]
    public void Base_type_subscription_receives_all_events()
    {
        var bus = new EventBus();
        var owner = new object();
        var seen = new List<OrandaEvent>();
        using var s = bus.Subscribe<OrandaEvent>(owner, seen.Add);

        bus.Publish(Ev());
        bus.Publish(new SessionClosed(Guid.NewGuid()));

        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Dispose_stops_delivery_and_is_idempotent()
    {
        var bus = new EventBus();
        var owner = new object();
        var count = 0;
        var s = bus.Subscribe<SettingsChanged>(owner, _ => count++);
        bus.Publish(Ev());

        s.Dispose();
        s.Dispose();
        bus.Publish(Ev());

        Assert.Equal(1, count);
        Assert.Equal(0, bus.SubscriptionCount);
    }

    [Fact]
    public void Handler_exception_does_not_reach_other_subscribers_or_publisher()
    {
        var errors = new List<Exception>();
        var bus = new EventBus((ex, _) => errors.Add(ex));
        var owner = new object();
        var second = 0;
        using var a = bus.Subscribe<SettingsChanged>(owner, _ => throw new InvalidOperationException("boom"));
        using var b = bus.Subscribe<SettingsChanged>(owner, _ => second++);

        bus.Publish(Ev());

        Assert.Equal(1, second);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
    }

    [Fact]
    public void Handler_may_unsubscribe_itself_during_publish()
    {
        var bus = new EventBus();
        var owner = new object();
        IDisposable? sub = null;
        var count = 0;
        sub = bus.Subscribe<SettingsChanged>(owner, _ =>
        {
            count++;
            sub!.Dispose();
        });

        bus.Publish(Ev());
        bus.Publish(Ev());

        Assert.Equal(1, count);
    }

    [Fact]
    public void Subscription_ends_when_owner_is_collected()
    {
        var bus = new EventBus();
        var counter = new Counter();
        var weakOwner = Subscribe(bus, counter);

        bus.Publish(Ev());
        Assert.Equal(1, counter.Value);

        Collect(weakOwner);
        Assert.False(IsAlive(weakOwner));
        bus.Publish(Ev());

        Assert.Equal(1, counter.Value);
        Assert.Equal(0, bus.SubscriptionCount);
    }

    [Fact]
    public void Lambda_handler_stays_alive_while_owner_is_alive_even_after_gc()
    {
        var bus = new EventBus();
        var owner = new object();
        var count = 0;
        bus.Subscribe<SettingsChanged>(owner, _ => count++);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        bus.Publish(Ev());

        Assert.Equal(1, count);
        GC.KeepAlive(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> Subscribe(EventBus bus, Counter counter)
    {
        var owner = new Owner(counter);
        bus.Subscribe<SettingsChanged>(owner, _ => owner.Counter.Value++);
        return new WeakReference<object>(owner);
    }

    private static void Collect(WeakReference<object> weak)
    {
        for (var i = 0; i < 5 && IsAlive(weak); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<object> weak) => weak.TryGetTarget(out _);

    private sealed class Counter
    {
        public int Value { get; set; }
    }

    private sealed class Owner(Counter counter)
    {
        public Counter Counter { get; } = counter;
    }
}
