namespace Oranda.Core.Events;

/// <summary>
/// 앱 안 이벤트 버스(TRD 7). 구독은 <b>약한 참조</b>다: 버스는 <paramref name="owner"/> 를 약하게 잡고,
/// 처리기는 owner 가 살아 있는 동안만 살아 있게 묶는다(ConditionalWeakTable 방식). 그래서 람다 처리기도
/// owner(보통 ViewModel·서비스 자신)가 수거되면 자동으로 끊긴다. 명시적으로 끊으려면 반환값을 Dispose.
/// <para>
/// 발행은 발행한 스레드에서 동기로 처리기를 부른다. UI 스레드 전달은 구독 쪽이 고른다.
/// 처리기 예외는 다른 구독자에게 전파하지 않고 로그로 남긴다.
/// </para>
/// 구현: Oranda.Core <c>EventBus</c>(TSK-03-01). 시험은 Oranda.Testing <c>FakeEventBus</c>.
/// </summary>
public interface IEventBus
{
    IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
        where TEvent : OrandaEvent;

    void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent;
}

/// <summary>모든 이벤트의 바탕 형식.</summary>
public abstract record OrandaEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
