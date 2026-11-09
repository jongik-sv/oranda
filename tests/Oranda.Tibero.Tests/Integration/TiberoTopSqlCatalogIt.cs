using Oranda.Core.Catalog.Providers;
using Oranda.Core.Sessions;
using Oranda.Tibero.Catalog;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// M0 실측(읽기 전용): sql.top 이 V$SQL 의 넘어간 숫자(음수가 부호 없는 64비트로 간 값)를 NULL 로 바꿔 Int64 로 읽히는지, 정렬 기준 여섯 가지가 모두 도는지.
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 결과는 시험 출력에 남긴다(접속 정보는 싣지 않는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTopSqlCatalogIt(ITestOutputHelper output)
{
    /// <summary>sql.top 의 숫자 열(executions·elapsed_time·cpu_time·buffer_gets·disk_reads·rows_processed).</summary>
    private static readonly int[] NumberColumns = [4, 5, 6, 7, 8, 9];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task M0_top_sql_numbers_fit_int64_for_every_order(int orderBy)
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var rows = await TiberoCatalogPort.QueryAsync(session, "sql.top",
        [
            CatalogBinds.Int("order_by", orderBy),
            CatalogBinds.Int("top", 1000),
            CatalogBinds.Double("min_elapsed_us", null),
            CatalogBinds.Long("min_buffer_gets", null),
            CatalogBinds.Long("min_executions", null),
            CatalogBinds.Trimmed("text", null),
            CatalogBinds.Trimmed("schema", null),
            CatalogBinds.Double("active_secs", null),
        ], CatalogQueryOptions.Default, CancellationToken.None);

        Assert.NotEmpty(rows.Rows); // 공유 풀이 비지 않았다(이 시험의 질의 자신도 있다)
        var nulls = 0;
        foreach (var row in rows.Rows)
        {
            foreach (var i in NumberColumns)
            {
                var value = CatalogValues.Long(row[i]); // 넘친 값이 남아 있으면 OverflowException
                if (value is null)
                {
                    nulls++;
                }
                else
                {
                    Assert.InRange(value.Value, 0, 4611686018427387903L);
                }
            }
        }

        output.WriteLine($"M0 order_by {orderBy}: {rows.Rows.Count} rows, NULL number cells = {nulls}");
    }
}
