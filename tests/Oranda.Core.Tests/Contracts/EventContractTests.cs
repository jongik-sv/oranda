using Oranda.Core.Events;

namespace Oranda.Core.Tests.Contracts;

/// <summary>TRD 7장 이벤트 형식과 버스 계약.</summary>
public class EventContractTests
{
    public static TheoryData<Type, string[]> Events => new()
    {
        { typeof(SessionOpened), ["SessionId", "Profile", "Server", "Purpose"] },
        { typeof(SessionClosed), ["SessionId"] },
        { typeof(SessionBroken), ["SessionId", "HadPendingTransaction", "Error"] },
        { typeof(TransactionStateChanged), ["SessionId", "Pending"] },
        { typeof(StatementExecuted), ["SessionId", "Statement", "Result", "Options"] },
        { typeof(DbmsOutputReceived), ["SessionId", "Lines"] },
        { typeof(MetadataInvalidated), ["DbKey", "Objects"] },
        { typeof(PolicyDecisionMade), ["SessionId", "Statement", "Decision"] },
        { typeof(SettingsChanged), ["Section"] },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Event_is_sealed_record_with_contract_fields(Type type, string[] fields)
    {
        Assert.True(type.IsSealed);
        Assert.True(typeof(OrandaEvent).IsAssignableFrom(type));
        Assert.NotNull(type.GetMethod("<Clone>$")); // record

        var ctor = Assert.Single(type.GetConstructors(), c => c.GetParameters().Length == fields.Length);
        Assert.Equal(fields, ctor.GetParameters().Select(p => p.Name));
    }

    [Fact]
    public void Event_bus_subscription_takes_owner_for_weak_reference()
    {
        var subscribe = typeof(IEventBus).GetMethod(nameof(IEventBus.Subscribe))!;

        Assert.Equal(typeof(IDisposable), subscribe.ReturnType);
        Assert.Equal(typeof(object), subscribe.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(OrandaEvent), subscribe.GetGenericArguments()[0].GetGenericParameterConstraints().Single());
    }
}
