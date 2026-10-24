using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

/// <summary>
/// 소유: TSK-03-07. 11.2 서버용 카탈로그(min-version 11): 모든 id 가 11 에서 고를 쿼리를 갖고, 11 변형은 12c 이후 기능을 쓰지 않으며,
/// 19·23 선택은 11 변형을 더하기 전과 같다.
/// </summary>
[Trait("Category", "Catalog")]
public partial class Oracle11CatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    /// <summary>11g 사전 점검(D-026)에서 실패한 15개 파일의 id. 19 이상은 원래 파일, 11 은 .11 변형을 받는다.</summary>
    public static readonly TheoryData<string> VariantIds = new()
    {
        "meta.objects", "dbinfo.object_search", "dbinfo.source_search", "dbinfo.column_search",
        "session.open_cursors", "session.sql_text", "sql.full_text", "sql.top",
        "dbinfo.database", "dbinfo.instance", "dbinfo.version", "meta.columns", "meta.constraints", "meta.detail_general",
    };

    [GeneratedRegex(@":([a-z_][a-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex BindName();

    /// <summary>
    /// 일부러 19 이상에서만 쓰는 id. 11.2 에서는 기능을 숨긴다(로더가 <see cref="CatalogQueryNotFoundException"/> 로 알린다).
    /// session.my_blocking: MAX_IDLE_BLOCKER_TIME 은 19c 이후 파라미터라 11.2 에서 뜻이 없다(TSK-13-02).
    /// lock.deadlocks: 알림 로그 뷰 v$diag_alert_ext 는 12.2 이후(TSK-05-02, 11.2 는 빈 목록).
    /// </summary>
    private static readonly string[] NineteenOnly = ["session.my_blocking", "lock.deadlocks"];

    [Fact]
    public void Every_catalog_id_has_a_query_for_11_2()
    {
        foreach (var id in Catalog.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Except(NineteenOnly))
        {
            var query = Catalog.Get(id, 11);
            Assert.Equal(11, query.Header.MinVersion);
        }
    }

    [Fact]
    public void Nineteen_only_ids_are_not_found_on_11_2()
    {
        foreach (var id in NineteenOnly)
        {
            Assert.Throws<CatalogQueryNotFoundException>(() => Catalog.Get(id, 11));
            Assert.Equal(19, Catalog.Get(id, 19).Header.MinVersion);
        }
    }

    [Fact]
    public void Eleven_queries_use_no_12c_features()
    {
        foreach (var header in Catalog.Entries.Where(h => h.MinVersion < 12))
        {
            var sql = Catalog.Get(header.Id, 11).Sql;
            Assert.True(CatalogParser.FindPre12cFeatures(sql).Count == 0, header.Path);
        }
    }

    [Theory]
    [MemberData(nameof(VariantIds))]
    public void Nineteen_and_later_keep_the_original_file(string id)
    {
        Assert.Equal(19, Catalog.Get(id, 19).Header.MinVersion);
        Assert.Equal(19, Catalog.Get(id, 21).Header.MinVersion);
        Assert.False(Catalog.Get(id, 19).Header.Path.EndsWith(".11.sql", StringComparison.Ordinal));
        Assert.EndsWith(".11.sql", Catalog.Get(id, 11).Header.Path, StringComparison.Ordinal);
        Assert.NotEqual(Catalog.Get(id, 19).Sql, Catalog.Get(id, 11).Sql);
    }

    [Theory]
    [MemberData(nameof(VariantIds))]
    public void Eleven_variant_keeps_the_binds_license_and_rac_of_the_original(string id)
    {
        var original = Catalog.Get(id, 19);
        var variant = Catalog.Get(id, 11);

        Assert.Equal(original.Header.License, variant.Header.License);
        Assert.Equal(original.Header.Rac, variant.Header.Rac);
        Assert.Equal(Binds(original.Sql), Binds(variant.Sql));
    }

    [Theory]
    [InlineData("meta.objects", ":max_rows")]
    [InlineData("dbinfo.object_search", ":max_rows")]
    [InlineData("dbinfo.source_search", ":max_rows")]
    [InlineData("dbinfo.column_search", ":max_rows")]
    [InlineData("sql.top", ":top")]
    [InlineData("session.open_cursors", "200")]
    [InlineData("session.sql_text", "1")]
    [InlineData("sql.full_text", "1")]
    public void Row_limit_uses_rownum_in_11_variant(string id, string limit)
    {
        var sql = Catalog.Get(id, 11).Sql;

        Assert.Contains("ROWNUM <= " + limit, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FETCH", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constraints_11_reads_long_search_condition_without_filtering_it_in_sql()
    {
        var sql = Catalog.Get("meta.constraints", 11).Sql;

        Assert.Contains("c.search_condition,", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_11_keeps_column_order_and_picks_visible_user_columns()
    {
        var sql = Catalog.Get("meta.columns", 11).Sql;

        Assert.Contains("c.hidden_column = 'NO'", sql, StringComparison.Ordinal);
        Assert.Equal(SelectListLength(Catalog.Get("meta.columns", 19).Sql), SelectListLength(sql));
    }

    [Fact]
    public void Explain_steps_reads_every_plan_table_column_by_name()
    {
        var query = Catalog.Get("plan.explain_steps", 11);

        Assert.Same(query, Catalog.Get("plan.explain_steps", 26));
        Assert.Contains("SELECT p.*", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("plan_id", query.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> Binds(string sql) =>
        BindName().Matches(sql).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>바깥 SELECT 목록의 열 수(최상위 쉼표 수 + 1).</summary>
    private static int SelectListLength(string sql)
    {
        var from = sql.IndexOf("\n  FROM", StringComparison.Ordinal);
        var list = sql[..from];
        var depth = 0;
        var count = 1;
        foreach (var ch in list)
        {
            depth += ch switch { '(' => 1, ')' => -1, _ => 0 };
            if (ch == ',' && depth == 0)
            {
                count++;
            }
        }

        return count;
    }
}
