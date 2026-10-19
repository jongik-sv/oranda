namespace Oranda.Core.Events;

/// <summary>약한 참조 이벤트 버스(TRD 7). 앱 조립이 단일 인스턴스로 등록한다(Core 는 스캔 대상이 아니다). 소유: TSK-03-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
public sealed class EventBus : IEventBus
{
    public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
        where TEvent : OrandaEvent
        => throw new NotImplementedException("TSK-03-01");

    public void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent
        => throw new NotImplementedException("TSK-03-01");
}
