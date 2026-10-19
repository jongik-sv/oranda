using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>
/// DB 정보(인스턴스·데이터베이스·버전·옵션·파라미터·NLS). 소유: TSK-07-04.
/// 구역 하나를 읽지 못해도(권한 부족 등) 나머지는 보여 준다 — 못 읽은 구역은 「오류」 한 줄로 채운다.
/// </summary>
[OrandaService(typeof(IDatabaseInfoProvider))]
internal sealed class OracleDatabaseInfoProvider : IDatabaseInfoProvider
{
    private static readonly (string Title, string Id, bool Transpose)[] Plan =
    [
        ("인스턴스", "dbinfo.instance", true),
        ("데이터베이스", "dbinfo.database", true),
        ("버전", "dbinfo.version", false),
        ("옵션", "dbinfo.options", false),
        ("파라미터", "dbinfo.parameters", false),
        ("NLS", "dbinfo.nls", false),
    ];

    public async Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sections = new List<DetailSection>();
        foreach (var (title, id, transpose) in Plan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var table = await SchemaQuery.RunAsync(s, id, null, _ => { }, ct);
                sections.Add(transpose ? Transposed(title, table) : new DetailSection(title, table.Columns, [.. table.Rows.Select(r => (IReadOnlyList<object?>)r)]));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sections.Add(new DetailSection(title, ["오류"], [[ex.Message.ReplaceLineEndings(" ")]]));
            }
        }

        return sections;
    }

    /// <summary>한 행짜리 결과를 「항목·값」 표로 세운다.</summary>
    private static DetailSection Transposed(string title, SchemaQuery.Table table)
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
