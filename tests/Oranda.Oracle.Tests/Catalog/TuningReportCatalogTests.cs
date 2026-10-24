using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Tuning;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-06-07. 튜닝 보고서가 더한 카탈로그(tuning.sql_stats·tuning.report_env)와 행 → 값 바꾸기.
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

    [Fact]
    public void Sql_stats_row_maps_sums_and_an_absent_cursor_is_null()
    {
        var last = new DateTime(2026, 10, 24, 14, 0, 0);
        var row = OracleTuningService.ToSqlStats([4m, 2m, 18220m, 33561000000m, 9402000000m, 768519600m, 565220840m, 22009760m, 6040000000m, 1m, 2m, 3m, 120m, 36000m, "MESADM", "MES-WEB", last, "2026-10-20/08:02:11"]);

        Assert.NotNull(row);
        Assert.Equal(4, row.Children);
        Assert.Equal(2, row.Instances);
        Assert.Equal(18220, row.Executions);
        Assert.Equal(768519600, row.BufferGets);
        Assert.Equal(6040000000, row.ApplicationWaitMicros);
        Assert.Equal(3, row.UserIoWaitMicros);
        Assert.Equal(36000, row.Fetches);
        Assert.Equal("MESADM", row.ParsingSchema);
        Assert.Equal(last, row.LastActiveTime);
        Assert.Equal("2026-10-20/08:02:11", row.FirstLoadTime);
        Assert.Null(OracleTuningService.ToSqlStats([0m, 0m, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null]));
    }

    [Fact]
    public void Environment_rows_keep_known_kinds_only()
    {
        var rows = OracleTuningService.ToEnvRows([("VERSION", "banner", "Oracle Database 11g"), ("PARAMETER", "optimizer_mode", "ALL_ROWS"), ("NLS", "NLS_SORT", null), ("OTHER", "x", "y"), ("NLS", null, "z")]);

        Assert.Equal([TuningEnvKind.Version, TuningEnvKind.Parameter, TuningEnvKind.Nls], rows.Select(r => r.Kind));
        Assert.Null(rows[2].Value);
    }
}
