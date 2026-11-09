using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition;

/// <summary>
/// DBMS 별 구현 고르기(D-046, docs/design/tibero.md 2장). <see cref="OrandaServiceAttribute.Dbms"/> 를 단 구현을
/// (서비스 형식, DBMS) 로 꺼낸다. 앱 조립이 구현하고(기본 = <see cref="DbmsServiceTable"/>), 디스패처가 세션의 DBMS 로 부른다.
/// 공급자 어셈블리가 없는 DBMS(예: <c>Oranda.Tibero</c> 미포함 빌드)는 <see cref="AvailableDbms"/> 에 없다.
/// </summary>
public interface IDbmsServiceSelector
{
    /// <summary>구현이 하나라도 등록된 DBMS. 접속 대화 상자의 DBMS 목록이 이것을 쓴다.</summary>
    IReadOnlyCollection<Dbms> AvailableDbms { get; }

    /// <summary><paramref name="dbms"/> 용 <typeparamref name="T"/> 구현. 없으면 null.</summary>
    T? Find<T>(Dbms dbms)
        where T : class;
}

/// <summary><see cref="IDbmsServiceSelector"/> 의 편의 호출.</summary>
public static class DbmsServiceSelectorExtensions
{
    /// <summary><paramref name="dbms"/> 용 구현. 없으면 <see cref="DbmsNotSupportedException"/>.</summary>
    public static T Get<T>(this IDbmsServiceSelector selector, Dbms dbms)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        return selector.Find<T>(dbms) ?? throw new DbmsNotSupportedException(dbms, typeof(T));
    }

    /// <summary>세션 프로필의 DBMS(<see cref="ConnectionProfile.Dbms"/>) 용 구현. 없으면 <see cref="DbmsNotSupportedException"/>.</summary>
    public static T For<T>(this IDbmsServiceSelector selector, ISession session)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        return selector.Get<T>(session.Profile.Dbms);
    }
}

/// <summary>이 DBMS 용 구현이 없다(공급자 어셈블리가 빠졌거나 그 기능을 아직 만들지 않음).</summary>
public sealed class DbmsNotSupportedException : NotSupportedException
{
    public DbmsNotSupportedException(Dbms dbms, Type serviceType)
        : base($"{dbms} 용 '{serviceType?.Name}' 구현이 없다.")
    {
        Dbms = dbms;
        ServiceType = serviceType;
    }

    public Dbms Dbms { get; }

    public Type? ServiceType { get; }
}
