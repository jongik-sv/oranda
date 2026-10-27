using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-04-04. meta/ 폴더 카탈로그: id 존재, 라이선스 없음, 23 전용 열은 min-version 23 으로만.
[Trait("Category", "Catalog")]
public partial class MetaCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    [Theory]
    [InlineData("meta.schemas")]
    [InlineData("meta.objects")]
    [InlineData("meta.columns")]
    [InlineData("meta.constraints")]
    [InlineData("meta.indexes")]
    [InlineData("meta.table_comment")]
    [InlineData("meta.procedures")]
    [InlineData("meta.arguments")]
    [InlineData("meta.detail_triggers")]
    [InlineData("meta.detail_referenced_by")]
    [InlineData("meta.name_step")]
    public void Query_exists_for_19_and_needs_no_license_pack(string id)
    {
        var query = Catalog.Get(id, 19);

        Assert.Equal(CatalogLicense.None, query.Header.License);
        Assert.InRange(query.Header.MinVersion, 11, 19);
        Assert.Equal(id, Catalog.Get(id, 11).Header.Id); // 11.2 (TSK-03-07)
        Assert.Contains("@PFX@_", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Name_step_reads_the_synonym_target_and_whether_a_table_or_view_owns_the_name()
    {
        // 시노님 따라가기(플래시백·편집 대상): 한 단계 = 그 스키마의 표·뷰인지, 시노님이면 가리키는 소유자·이름·DB 링크.
        var sql = Catalog.Get("meta.name_step", 11).Sql;

        Assert.Contains("@PFX@_synonyms", sql, StringComparison.Ordinal);
        Assert.Contains("s.table_owner, s.table_name, s.db_link", sql, StringComparison.Ordinal);
        Assert.Contains("s.owner = :owner AND s.synonym_name = :name", sql, StringComparison.Ordinal);
        Assert.Contains("o.owner = :owner AND o.object_name = :name", sql, StringComparison.Ordinal);
        Assert.Contains("'TABLE', 'VIEW', 'MATERIALIZED VIEW'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_column_is_selected_only_on_23_and_later()
    {
        Assert.Equal(11, Catalog.Get("meta.columns", 11).Header.MinVersion);
        Assert.Equal(19, Catalog.Get("meta.columns", 19).Header.MinVersion);
        Assert.Equal(19, Catalog.Get("meta.columns", 21).Header.MinVersion);
        Assert.Equal(23, Catalog.Get("meta.columns", 23).Header.MinVersion);
        Assert.Equal(23, Catalog.Get("meta.columns", 26).Header.MinVersion);
        Assert.DoesNotContain("c.domain_name", Catalog.Get("meta.columns", 19).Sql, StringComparison.Ordinal);
        Assert.Contains("c.domain_name", Catalog.Get("meta.columns", 23).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Trigger_detail_binds_table_owner_and_name_and_skips_the_long_body()
    {
        var sql = Catalog.Get("meta.detail_triggers", 26).Sql;

        Assert.Contains("t.table_owner = :owner", sql, StringComparison.Ordinal);
        Assert.Contains("t.table_name = :name", sql, StringComparison.Ordinal);
        Assert.Contains("t.when_clause", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("trigger_body", sql, StringComparison.OrdinalIgnoreCase); // LONG 열은 읽지 않는다
    }

    [Fact]
    public void Referenced_by_detail_finds_foreign_keys_pointing_at_the_table_keys()
    {
        var sql = Catalog.Get("meta.detail_referenced_by", 26).Sql;

        Assert.Contains("c.constraint_type = 'R'", sql, StringComparison.Ordinal);
        Assert.Contains("r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name", sql, StringComparison.Ordinal);
        Assert.Contains("r.owner = :owner", sql, StringComparison.Ordinal);
        Assert.Contains("r.table_name = :name", sql, StringComparison.Ordinal);
        Assert.Contains("LISTAGG(cc.column_name, ', ') WITHIN GROUP (ORDER BY cc.position)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Constraints_select_the_referenced_key_name_last_after_column_position()
    {
        // 외래키 따라가기(GRID-11)가 부모의 어느 키를 참조하는지 안다. 앞 열 번호(0~7)는 그대로 두고 맨 뒤(8)에 더했다.
        var sql = Catalog.Get("meta.constraints", 26).Sql;

        Assert.Contains("cc.column_name, cc.position, c.r_constraint_name", sql, StringComparison.Ordinal);
    }

    /// <summary>상세 구역은 BindTable(owner, name)만 묶는다. 본문의 바인드 이름이 정확히 그 둘이어야 한다(이름은 글자로 잇지 않는다).</summary>
    [Theory]
    [InlineData("meta.detail_triggers")]
    [InlineData("meta.detail_referenced_by")]
    [InlineData("meta.name_step")]
    public void Detail_section_queries_bind_exactly_owner_and_name(string id)
    {
        var binds = BindName().Matches(Catalog.Get(id, 26).Sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(["name", "owner"], binds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Meta_queries_never_read_password_columns()
    {
        foreach (var header in Catalog.Entries.Where(h => h.Id.StartsWith("meta.", StringComparison.Ordinal)))
        {
            var sql = Catalog.Get(header.Id, 26).Sql;
            Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex(@":([a-z_]+)")]
    private static partial Regex BindName();
}
