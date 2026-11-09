using Oranda.App.Features.Localization;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;

namespace Oranda.App.Features;

/// <summary>
/// App 이 SQL 카탈로그 문장(<c>app.*</c>·<c>meta.*</c>·<c>dbinfo.*</c>)을 꺼내는 길목(U5). 화면은 <c>EmbeddedCatalog</c> 를 직접 부르지 않고 여기를 거친다.
/// <list type="bullet">
/// <item>서비스 공급자가 있으면(앱 조립) <see cref="DbmsServices.CatalogText"/> — 세션 서버의 DBMS·버전에 맞는 카탈로그 파일. 세션·구현·id 가 없으면 null,
/// 부르는 쪽이 기능을 끄고 <see cref="Unavailable"/> 로 이유를 보인다.</item>
/// <item>서비스 공급자가 null 이면(조립 없이 화면 모델을 직접 만드는 시험·도구) 예전 Oracle 고정 경로(<see cref="OracleFixed"/>)다 —
/// 세션이 있고 Oracle 이 아니면 null. 운영 코드는 공급자를 늘 넘기므로 이 경로를 타지 않는다.</item>
/// </list>
/// 이 파일이 App 에서 <c>EmbeddedCatalog</c> 를 직접 부르는 유일한 곳이다(소스 스캔 시험이 지킨다).
/// </summary>
public static class CatalogRouting
{
    /// <summary>
    /// <paramref name="id"/> 의 문장에 <c>@KEY@</c> 를 채운 글. <paramref name="services"/> 가 있으면 세션 서버의 DBMS·버전으로 고르고(없으면 null),
    /// null 이면 Oracle 고정 경로(비 Oracle 세션이면 null).
    /// </summary>
    public static string? Text(IServiceProvider? services, ISession? session, string id, params (string Key, string Value)[] args)
    {
        if (services is not null)
        {
            return DbmsServices.CatalogText(services, session, id, args);
        }

        return session is not null && session.Profile.Dbms != Dbms.Oracle ? null : OracleFixed(id, args);
    }

    /// <summary>
    /// 세션·서비스 공급자 없이 Oracle 카탈로그에서 꺼낸다(기존 정적 도우미·시험 호환 멤버가 쓴다, 서버 버전과 무관한 문장). 프로덕션 호출처는 <see cref="Text"/> 를 쓴다.
    /// </summary>
    public static string OracleFixed(string id, params (string Key, string Value)[] args)
        => EmbeddedCatalog.Text(id, args);

    /// <summary>이 세션의 DBMS 에서는 쓸 SQL 이 없어 기능을 못 쓴다는 이유(현재 언어).</summary>
    public static string Unavailable(ISession? session)
        => UiText.Format("Catalog.text.unavailable", "{0} 서버에서는 이 기능에 쓸 SQL 이 없어 쓸 수 없습니다.",
            session is null ? "-" : session.Profile.Dbms.ToString());
}
