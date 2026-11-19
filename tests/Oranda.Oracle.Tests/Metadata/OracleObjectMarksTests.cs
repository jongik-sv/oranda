using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Metadata;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Metadata;

namespace Oranda.Oracle.Tests.Metadata;

/// <summary>
/// 스키마 브라우저 표식(Oracle): 보조 질의 카탈로그(머리·바인드·requires·11g), 테이블 펼침 질의에 더한 끝 열(인덱스 status·partitioned, 제약 validated,
/// table_props 의 객체 테이블)을 읽는 규칙.
/// </summary>
[Trait("Category", "Catalog")]
public partial class OracleObjectMarksTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData(11)]
    [InlineData(19)]
    [InlineData(26)]
    public void Mark_queries_run_from_11g_bind_only_the_owner_and_read_the_dictionary_prefix(int version)
    {
        foreach (var id in OracleMetadataProvider.MarkIds)
        {
            var query = Catalog.Get(id, version);

            Assert.Equal(11, query.Header.MinVersion);
            Assert.Equal(CatalogLicense.None, query.Header.License);
            Assert.Equal(["owner"], BindName().Matches(Body(query.Sql)).Select(m => m.Groups[1].Value).Distinct());
            Assert.Contains("@PFX@_", query.Sql, StringComparison.Ordinal);
            Assert.All(Regex.Matches(Body(query.Sql), @"@PFX@_([a-z_]+)").Select(m => m.Groups[1].Value).Distinct(),
                view => Assert.Contains("all_" + view, query.Header.Requires)); // 쓰는 뷰는 모두 requires 에(dba_/all_ 둘 다)
            Assert.All(Regex.Matches(Body(query.Sql), @"@PFX@_([a-z_]+)").Select(m => m.Groups[1].Value).Distinct(),
                view => Assert.Contains("dba_" + view, query.Header.Requires));
        }
    }

    [Fact]
    public void Mark_queries_return_only_marked_objects_where_a_default_exists()
    {
        var tables = Catalog.Get(ObjectMarkRows.Tables, 26).Sql;
        Assert.Contains("t.temporary = 'Y' OR t.iot_type = 'IOT' OR t.cluster_name IS NOT NULL OR t.partitioned = 'YES'", tables, StringComparison.Ordinal);
        Assert.Contains("c.column_name = 'SYS_NC_ROWINFO$'", tables, StringComparison.Ordinal); // all_object_tables 는 소유자 하나도 느려(실측) 숨은 열로 찾는다
        Assert.DoesNotContain("JOIN", tables, StringComparison.Ordinal); // 파티션·외부 사전을 JOIN 하지 않는다(실측 all_ 2~4초)
        Assert.Contains("(j.enabled <> 'TRUE' OR j.state <> 'SCHEDULED')", Catalog.Get(ObjectMarkRows.Jobs, 26).Sql, StringComparison.Ordinal);
        Assert.Contains("s.owner <> 'PUBLIC'", Catalog.Get(ObjectMarkRows.Synonyms, 26).Sql, StringComparison.Ordinal); // PUBLIC 동의어 수천 개는 읽지 않는다
        Assert.DoesNotContain("trigger_body", Catalog.Get(ObjectMarkRows.Triggers, 26).Sql, StringComparison.OrdinalIgnoreCase); // LONG 은 읽지 않는다
        Assert.Contains("@PFX@_coll_types", Catalog.Get(ObjectMarkRows.Types, 26).Sql, StringComparison.Ordinal); // 객체 타입은 기본(빠짐)
    }

    [Theory]
    [InlineData(11)]
    [InlineData(19)]
    public void Table_expand_queries_gained_only_trailing_columns(int version)
    {
        Assert.Contains("c.index_name, c.validated\n", Catalog.Get("meta.constraints", version).Sql, StringComparison.Ordinal);
        Assert.Contains("i.tablespace_name, i.status, i.partitioned\n", Catalog.Get("meta.indexes", version).Sql, StringComparison.Ordinal);
        var props = Catalog.Get("meta.table_props", version).Sql;
        Assert.Contains("t.degree, 'NO' AS object_table", props, StringComparison.Ordinal);
        Assert.Contains("FROM @PFX@_object_tables o", props, StringComparison.Ordinal);
        Assert.Contains("all_object_tables", Catalog.Get("meta.table_props", version).Header.Requires);
    }

    [Fact]
    public void Index_status_and_partitioned_are_read_and_old_rows_mean_unknown()
    {
        object?[][] rows =
        [
            ["IX_A", "NONUNIQUE", "NORMAL", "A", 1m, "ASC", null, "USERS", "UNUSABLE", "NO"],
            ["IX_P", "UNIQUE", "NORMAL", "B", 1m, "ASC", null, null, "N/A", "YES"],
        ];

        var indexes = OracleTableEditRows.BuildIndexes(rows, []).ToDictionary(i => i.Name);

        Assert.Equal((true, false), (indexes["IX_A"].IsUnusable, indexes["IX_A"].IsPartitioned));
        Assert.Equal((false, true), (indexes["IX_P"].IsUnusable, indexes["IX_P"].IsPartitioned)); // 파티션 인덱스의 N/A 는 모름
        var old = OracleTableEditRows.BuildIndexes([["IX_A", "NONUNIQUE", "NORMAL", "A", 1m, "ASC", null, "USERS"]], []).Single();
        Assert.Equal((false, false), (old.IsUnusable, old.IsPartitioned));
    }

    [Fact]
    public void Constraint_validated_is_read_from_the_twelfth_column()
    {
        object?[] Row(string name, string? validated) => [name, "R", "ENABLED", null, "S", "P", "A", 1m, "PK", "NO ACTION", "A", null, validated];

        var cons = MetadataRows.BuildConstraints([Row("FK_N", "NOT VALIDATED"), Row("FK_V", "VALIDATED")]).ToDictionary(c => c.Name);

        Assert.False(cons["FK_N"].IsValidated);
        Assert.True(cons["FK_V"].IsValidated);
        Assert.True(MetadataRows.BuildConstraints([["FK", "R", "ENABLED", null, "S", "P", "A", 1m, "PK"]]).Single().IsValidated); // 예전 행 모양
    }

    [Fact]
    public void Object_tables_come_back_as_their_own_kind_and_restrict_the_editor()
    {
        var table = new TableInfo(new DbObjectRef("SAMPLE", "PEOPLE", "TABLE"), []);
        object?[][] rows = [["USERS", "N", null, null, "NO", null, "NO", 10m, 1m, "DISABLED", null, "YES", "         1", "YES"]];

        var result = OracleTableEditRows.WithTableProps(table, rows);

        Assert.Equal(TableKind.ObjectTable, result.Kind);
        Assert.Equal(TableKind.Heap, OracleTableEditRows.WithTableProps(table, [[.. rows[0][..13], "NO"]]).Kind);
    }

    private static string Body(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [GeneratedRegex(@":([a-z_]+)")]
    private static partial Regex BindName();
}
