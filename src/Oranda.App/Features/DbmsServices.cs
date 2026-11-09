using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.App.Features;

/// <summary>
/// 화면이 DBMS 별 구현(<c>ISchemaStatements</c>·<c>IDdlBulkExporter</c>·<c>IColumnReorderScripts</c>·<c>IConnectionProbe</c> …)을 꺼내는 한 곳.
/// 서비스 공급자에서 <see cref="IDbmsServiceSelector"/> 를 얻어 세션·프로필의 DBMS 로 고른다. 선택기가 없거나 그 DBMS 용 구현이 없으면
/// null — 부르는 쪽이 기능을 끄고 이유를 보인다(드라이버 어셈블리가 빠진 빌드·아직 안 만든 기능).
/// </summary>
public static class DbmsServices
{
    /// <summary>선택기. 앱 조립이 등록하지 않았으면 null.</summary>
    public static IDbmsServiceSelector? SelectorOf(IServiceProvider? services)
        => services?.GetService(typeof(IDbmsServiceSelector)) as IDbmsServiceSelector;

    /// <summary><paramref name="dbms"/> 용 <typeparamref name="T"/> 구현. 없으면 null.</summary>
    public static T? Find<T>(IServiceProvider? services, Dbms dbms)
        where T : class
        => SelectorOf(services)?.Find<T>(dbms);

    /// <summary>세션 프로필의 DBMS 용 구현. 세션이 없거나 구현이 없으면 null.</summary>
    public static T? For<T>(IServiceProvider? services, ISession? session)
        where T : class
        => session is null ? null : Find<T>(services, session.Profile.Dbms);

    /// <summary>
    /// 세션 서버의 DBMS·버전에 맞는 카탈로그 문장 <paramref name="id"/> 를 꺼내 <c>@KEY@</c> 자리를 채운다(<see cref="Oranda.Core.Catalog.CatalogText.Fill"/>).
    /// App 이 <c>EmbeddedCatalog.Text</c> 를 직접 쓰던 자리의 대체. 세션이 없거나 카탈로그 구현이 없거나 그 DBMS·버전에 id 가 없으면 null —
    /// 부르는 쪽이 기능을 끄고 이유를 보인다. 카탈로그 원문 형식 오류(<see cref="CatalogFormatException"/>)는 null 이 아니라 그대로 던진다(빌드 검사가 먼저 잡는 결함).
    /// 구현은 프로필의 DBMS 로, 파일은 <see cref="ServerInfo.Dbms"/> 로 고른다 — 세션 관리자가 둘을 같게 채운다(다르면 id 를 못 찾아 null).
    /// </summary>
    public static string? CatalogText(IServiceProvider? services, ISession? session, string id, params (string Key, string Value)[] args)
    {
        var catalog = For<ISqlCatalog>(services, session);
        if (catalog is null || session is null)
        {
            return null;
        }

        try
        {
            return Oranda.Core.Catalog.CatalogText.Fill(catalog.Get(id, session.Server).Sql, args);
        }
        catch (CatalogQueryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>프로필의 DBMS 용 구현. 없으면 null.</summary>
    public static T? For<T>(IServiceProvider? services, ConnectionProfile profile)
        where T : class
        => Find<T>(services, profile.Dbms);
}
