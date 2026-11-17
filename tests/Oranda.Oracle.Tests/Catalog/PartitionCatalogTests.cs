using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

/// <summary>
/// 테이블 편집기 파티션 카탈로그(T14): Oracle 판이 11·19 모두 있고, 결과 열이 Tibero 판과 같은 모양이라 <c>PartitionRows</c> 를 같이 쓸 수 있다.
/// 파티션 목록은 기존 <c>meta.detail_partitions</c>(HIGH_VALUE·PARTITION_POSITION)를 쓴다.
/// </summary>
[Trait("Category", "Catalog")]
public partial class PartitionCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("meta.part_scheme")]
    [InlineData("meta.part_indexes")]
    [InlineData("meta.detail_partitions")]
    public void Partition_queries_exist_for_11_and_19_with_table_binds(string id)
    {
        foreach (var version in new[] { 11, 19 })
        {
            var query = Catalog.Get(id, version);
            Assert.Equal(CatalogLicense.None, query.Header.License);
            Assert.Contains("@PFX@_", query.Sql, StringComparison.Ordinal);
            Assert.Contains(":owner", query.Sql, StringComparison.Ordinal);
            Assert.Contains(":name", query.Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scheme_query_returns_the_columns_partition_rows_reads_in_order()
    {
        var sql = Catalog.Get("meta.part_scheme", 19).Sql;

        var select = Regex.Match(sql, @"SELECT\s+(.+?)\s+FROM", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal("pt.partitioning_type, pt.subpartitioning_type, pt.interval, 'PART' AS level_kind, k.column_name, k.column_position",
            Regex.Replace(select, @"\s+", " ").Trim());
        Assert.Contains("@PFX@_subpart_key_columns", sql, StringComparison.Ordinal);
        Assert.Contains("k.object_type = 'TABLE'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Partitioned_index_query_returns_name_and_locality() =>
        Assert.Contains("SELECT i.index_name, i.locality", Catalog.Get("meta.part_indexes", 19).Sql, StringComparison.Ordinal);
}
