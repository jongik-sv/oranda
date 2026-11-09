using Oranda.Core.Catalog;
using Oranda.Tibero.Catalog;

namespace Oranda.Tibero.Tests.Catalog;

/// <summary>
/// 모니터 카탈로그(M0): Tibero V$SQL 의 숫자 열은 음수가 부호 없는 64비트로 넘어간 값을 줄 수 있어(P5b H3 실측, win144 #49) Int64 로 읽으면 넘친다.
/// 그 열을 읽는 파일은 0 ~ 2^62-1 밖을 NULL 로 바꾼다(tuning/sql_stats 와 같은 규칙). 서버 없이 돈다.
/// </summary>
public class MonitorCatalogOverflowTests
{
    private const string Guard = "BETWEEN 0 AND 4611686018427387903";

    public static TheoryData<string, string> GuardedColumns() => new()
    {
        { "sql.top", "executions" },
        { "sql.top", "elapsed_time" },
        { "sql.top", "cpu_time" },
        { "sql.top", "buffer_gets" },
        { "sql.top", "disk_reads" },
        { "sql.top", "rows_processed" },
    };

    [Theory]
    [MemberData(nameof(GuardedColumns))]
    public void Vsql_number_column_is_nulled_outside_the_int64_safe_range(string id, string column)
    {
        var sql = Sql(id);

        Assert.Contains($"CASE WHEN r.{column} {Guard} THEN r.{column} END AS {column}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Top_sql_puts_null_values_last()
    {
        Assert.Contains("END DESC NULLS LAST", Sql("sql.top"), StringComparison.Ordinal);
    }

    private static string Sql(string id)
    {
        var query = TiberoEmbeddedCatalog.Sources
            .Select(s => CatalogParser.Parse(s).Query)
            .OfType<CatalogQuery>()
            .Single(q => q.Header.Id == id);
        return query.Sql;
    }
}
