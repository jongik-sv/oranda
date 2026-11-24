using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// DB 정보(인스턴스·데이터베이스·버전·옵션·파라미터·NLS) — Oracle <c>OracleDatabaseInfoProvider</c> 와 같은 구역·순서(T3c).
/// 구역 하나를 읽지 못해도(권한 부족 등) 나머지는 보여 준다(<see cref="DatabaseInfoSections"/>).
/// V$ 뷰를 읽을 권한이 없는 계정(Tibero 는 「객체 없음」 ODP-8033 으로 답한다)이면 인스턴스·데이터베이스는 SYS_CONTEXT 대신 질의로,
/// 버전은 접속 때 얻은 버전으로, NLS 는 세션 값만으로 채운다.
/// 「옵션」 은 Tibero 에 <c>V$OPTION</c> 이 없어 질의하지 않고 이유 한 줄을 보인다(docs/design/tibero-catalog-map.md, dbinfo.options).
/// </summary>
[OrandaService(typeof(IDatabaseInfoProvider), Dbms = Dbms.Tibero)]
internal sealed class TiberoDatabaseInfoProvider : IDatabaseInfoProvider
{
    internal const string OptionsNotSupported = "Tibero 에는 V$OPTION 이 없어 옵션 목록을 보여 주지 않습니다.";

    /// <summary>구역 순서.</summary>
    private static readonly DatabaseInfoSections.Plan[] Plan =
    [
        new("인스턴스", "dbinfo.instance", true, FallbackId: "dbinfo.instance_basic"),
        new("데이터베이스", "dbinfo.database", true, FallbackId: "dbinfo.database_basic"),
        new("버전", "dbinfo.version", false, VersionFromSession: true),
        new("옵션", null, false, Unsupported: OptionsNotSupported),
        new("파라미터", "dbinfo.parameters", false),
        new("NLS", "dbinfo.nls", false, FallbackId: "dbinfo.nls_session"),
    ];

    private readonly CatalogRunner _run;

    public TiberoDatabaseInfoProvider()
        : this(TiberoCatalogQuery.RunAsync)
    {
    }

    /// <summary>시험용: 카탈로그 실행기를 바꿔 끼운다.</summary>
    internal TiberoDatabaseInfoProvider(CatalogRunner run)
    {
        _run = run;
    }

    public Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return DatabaseInfoSections.LoadAsync(s, Plan, async (id, token) =>
        {
            var table = await _run(s, id, null, token).ConfigureAwait(false);
            return new DatabaseInfoSections.Table(table.Columns, table.Rows);
        }, ct);
    }
}
