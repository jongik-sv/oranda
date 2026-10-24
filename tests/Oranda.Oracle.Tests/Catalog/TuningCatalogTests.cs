using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-06-02. tuning/ 폴더 카탈로그: id 존재·11g 선택·license 머리·SELECT 전용.
[Trait("Category", "Catalog")]
public partial class TuningCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    /// <summary>팩 없이 11.2 부터 쓰는 id.</summary>
    public static readonly TheoryData<string> NoPackIds = new()
    {
        "tuning.table_stats", "tuning.column_stats", "tuning.histogram", "tuning.index_stats", "tuning.index_columns",
        "tuning.child_cursors", "tuning.shared_cursor", "tuning.wait_sample", "tuning.trace_file", "tuning.prev_cursor",
        "tuning.plan_controls",
    };

    /// <summary>Diagnostics Pack 이 필요한 id(AWR·ASH).</summary>
    public static readonly TheoryData<string> DiagnosticsIds = new()
    {
        "tuning.awr_plan_history", "tuning.awr_plan_steps", "tuning.ash_waits",
    };

    private static readonly string[] PackViews = ["dba_hist_", "active_session_history", "sql_monitor", "sqltune", "advisor"];

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CREATE|DROP|ALTER|GRANT|BEGIN|DECLARE|EXECUTE|CALL|LOCK)\b|DBMS_(STATS|SQLTUNE|SPM|MONITOR|SESSION)|FOR\s+UPDATE|GLOBAL\s+TEMPORARY", RegexOptions.IgnoreCase)]
    private static partial Regex Writes();

    [Theory]
    [MemberData(nameof(NoPackIds))]
    public void No_pack_query_exists_for_11_2_and_later_without_license(string id)
    {
        Assert.Equal(11, Catalog.Get(id, 11).Header.MinVersion);
        Assert.Same(Catalog.Get(id, 11), Catalog.Get(id, 26));
        Assert.Equal(CatalogLicense.None, Catalog.Get(id, 19).Header.License);
    }

    [Theory]
    [MemberData(nameof(DiagnosticsIds))]
    public void Awr_and_ash_queries_are_tagged_diagnostics(string id)
    {
        Assert.Equal(CatalogLicense.Diagnostics, Catalog.Get(id, 11).Header.License);
        Assert.Equal(CatalogLicense.Diagnostics, Catalog.Get(id, 26).Header.License);
    }

    [Fact]
    public void Pack_views_appear_only_in_diagnostics_tagged_files()
    {
        foreach (var header in Catalog.Entries.Where(h => h.Id.StartsWith("tuning.", StringComparison.Ordinal)))
        {
            var sql = Catalog.Get(header.Id, Math.Max(header.MinVersion, 19)).Sql;
            var usesPack = PackViews.Any(v => sql.Contains(v, StringComparison.OrdinalIgnoreCase));
            Assert.True(usesPack == (header.License == CatalogLicense.Diagnostics), header.Path);
            Assert.NotEqual(CatalogLicense.Tuning, header.License);
        }
    }

    [Fact]
    public void Every_tuning_query_is_a_select_and_writes_nothing()
    {
        var headers = Catalog.Entries.Where(h => h.Id.StartsWith("tuning.", StringComparison.Ordinal)).ToList();
        Assert.Equal(18, headers.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count()); // TSK-06-04: trace_file_self, TSK-06-07: sql_stats·report_env
        foreach (var header in headers)
        {
            var sql = Catalog.Get(header.Id, Math.Max(header.MinVersion, 11)).Sql.TrimStart();
            Assert.True(sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase), header.Path);
            Assert.False(Writes().IsMatch(sql), header.Path);
            Assert.DoesNotContain("FETCH FIRST", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("con_id", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Trace_contents_need_12_and_are_hidden_on_11_2()
    {
        Assert.Throws<CatalogQueryNotFoundException>(() => Catalog.Get("tuning.trace_lines", 11));
        var q = Catalog.Get("tuning.trace_lines", 19);
        Assert.Equal(12, q.Header.MinVersion);
        Assert.Contains("v$diag_trace_file_contents", q.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":file_name", q.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Dictionary_queries_use_the_prefix_and_cursor_queries_use_gv_views()
    {
        foreach (var id in new[] { "tuning.table_stats", "tuning.column_stats", "tuning.histogram", "tuning.index_stats", "tuning.index_columns" })
        {
            var sql = Catalog.Get(id, 19).Sql;
            Assert.Contains("@PFX@_", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(" dba_", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(":owner", sql, StringComparison.Ordinal);
            Assert.Contains(":table_name", sql, StringComparison.Ordinal);
        }

        Assert.Contains("@PFX@_tab_statistics", Catalog.Get("tuning.table_stats", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("stale_stats", Catalog.Get("tuning.table_stats", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("stale_stats", Catalog.Get("tuning.index_stats", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("is_bind_aware", Catalog.Get("tuning.child_cursors", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("gv$sql_shared_cursor", Catalog.Get("tuning.shared_cursor", 19).Sql, StringComparison.Ordinal);
        foreach (var id in new[] { "tuning.child_cursors", "tuning.shared_cursor", "tuning.wait_sample", "tuning.ash_waits" })
        {
            Assert.Equal(CatalogRac.Gv, Catalog.Get(id, 19).Header.Rac);
            Assert.Contains(":sql_id", Catalog.Get(id, 19).Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Plan_controls_read_profiles_baselines_and_patches()
    {
        var sql = Catalog.Get("tuning.plan_controls", 11).Sql;

        foreach (var view in new[] { "dba_sql_profiles", "dba_sql_plan_baselines", "dba_sql_patches", "exact_matching_signature" })
        {
            Assert.Contains(view, sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
