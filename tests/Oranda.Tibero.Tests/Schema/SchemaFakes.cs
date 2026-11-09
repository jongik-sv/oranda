using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Sessions;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>스키마 서비스 단위 시험의 가짜: 서버 없는 Tibero 세션, 카탈로그 질의를 기록하는 실행기.</summary>
internal static class SchemaFakes
{
    /// <summary>명령을 보내지 않는 Tibero 6 세션. <paramref name="dictionary"/> = 사전 조회 권한(뷰 앞머리 dba/all).</summary>
    public static TiberoSession Session(bool dictionary = false)
    {
        var server = new ServerInfo("6", "i", "h", null, "D", null, false, 7,
            new ServerCapabilities(false, dictionary, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero);
        return new TiberoSession(Guid.NewGuid(), profile, server, SessionPurpose.Work, "D/-/U", new FakeDbConnection(), null, new EventBus());
    }

    public static TiberoSessionQuery.Table Table(string[] columns, params object?[][] rows) => new(columns, rows);

    /// <summary>질의 하나(id·바인드)를 기록하고 <paramref name="answer"/> 를 돌려주는 실행기.</summary>
    public static CatalogRunner Recording(List<(string Id, IReadOnlyDictionary<string, object?>? Binds)> calls, Func<string, TiberoSessionQuery.Table> answer) =>
        (_, id, binds, _) =>
        {
            calls.Add((id, binds));
            return Task.FromResult(answer(id));
        };

    /// <summary>늘 <paramref name="exception"/> 을 던지는 실행기.</summary>
    public static CatalogRunner Throwing(Exception exception) => (_, _, _, _) => Task.FromException<TiberoSessionQuery.Table>(exception);
}
