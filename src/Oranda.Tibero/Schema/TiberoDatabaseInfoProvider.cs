using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// DB 정보(인스턴스·데이터베이스·버전·옵션·파라미터·NLS) — Oracle <c>OracleDatabaseInfoProvider</c> 와 같은 구역·순서(T3c).
/// 구역 하나를 읽지 못해도(권한 부족 등) 나머지는 보여 준다 — 못 읽은 구역은 「오류」 한 줄로 채운다.
/// 「옵션」 은 Tibero 에 <c>V$OPTION</c> 이 없어 질의하지 않고 이유 한 줄을 보인다(docs/design/tibero-catalog-map.md, dbinfo.options).
/// </summary>
[OrandaService(typeof(IDatabaseInfoProvider), Dbms = Dbms.Tibero)]
internal sealed class TiberoDatabaseInfoProvider : IDatabaseInfoProvider
{
    internal const string OptionsNotSupported = "Tibero 에는 V$OPTION 이 없어 옵션 목록을 보여 주지 않습니다.";

    /// <summary>구역 순서. Id 가 null 이면 질의 없이 <see cref="OptionsNotSupported"/> 를 보인다.</summary>
    private static readonly (string Title, string? Id, bool Transpose)[] Plan =
    [
        ("인스턴스", "dbinfo.instance", true),
        ("데이터베이스", "dbinfo.database", true),
        ("버전", "dbinfo.version", false),
        ("옵션", null, false),
        ("파라미터", "dbinfo.parameters", false),
        ("NLS", "dbinfo.nls", false),
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

    public async Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sections = new List<DetailSection>();
        foreach (var (title, id, transpose) in Plan)
        {
            ct.ThrowIfCancellationRequested();
            if (id is null)
            {
                sections.Add(new DetailSection(title, ["안내"], [[OptionsNotSupported]]));
                continue;
            }

            try
            {
                var table = await _run(s, id, null, ct).ConfigureAwait(false);
                sections.Add(transpose ? Transposed(title, table) : new DetailSection(title, table.Columns, [.. table.Rows.Select(r => (IReadOnlyList<object?>)r)]));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) // 구역 하나의 실패(권한 부족 등)는 그 구역의 「오류」 줄로 보이고 나머지 구역은 계속 읽는다(Oracle 과 같음).
            {
                sections.Add(new DetailSection(title, ["오류"], [[ex.Message.ReplaceLineEndings(" ")]]));
            }
        }

        return sections;
    }

    /// <summary>한 행짜리 결과를 「항목·값」 표로 세운다.</summary>
    private static DetailSection Transposed(string title, TiberoSessionQuery.Table table)
    {
        var rows = new List<IReadOnlyList<object?>>();
        if (table.Rows.Count > 0)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                rows.Add([table.Columns[i].ToLowerInvariant(), table.Rows[0][i]]);
            }
        }

        return new DetailSection(title, ["항목", "값"], rows);
    }
}
