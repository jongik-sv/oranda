using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-06-07. 튜닝 보고서가 더한 카탈로그(tuning.sql_stats·tuning.report_env). 행 → 값 바꾸기는 Core.Tests TuningRowsTests 로 옮겼다.
[Trait("Category", "Catalog")]
public partial class TuningReportCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CREATE|DROP|ALTER|GRANT|BEGIN|DECLARE|EXECUTE|CALL|LOCK)\b|DBMS_|FOR\s+UPDATE|FETCH\s+FIRST|con_id", RegexOptions.IgnoreCase)]
    private static partial Regex WritesOr12Only();

    [Theory]
    [InlineData("tuning.sql_stats")]
    [InlineData("tuning.report_env")]
    public void Report_queries_are_select_only_no_pack_and_shared_from_11_2(string id)
    {
        var q = Catalog.Get(id, 11);

        Assert.Equal(11, q.Header.MinVersion);
        Assert.Same(q, Catalog.Get(id, 26));
        Assert.Equal(CatalogLicense.None, q.Header.License);
        Assert.StartsWith("SELECT", q.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(WritesOr12Only(), q.Sql);
    }
}
