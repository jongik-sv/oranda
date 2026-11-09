using Oranda.Core.Catalog;

namespace Oranda.Core.Tests.Catalog;

// 소유: tb-base K3. @KEY@ 채움(EmbeddedCatalog.Text 에서 옮김).
public class CatalogTextTests
{
    [Fact]
    public void Fill_matches_keys_case_insensitively_and_leaves_unknown_placeholders()
    {
        var sql = CatalogText.Fill("KILL '@ID@' @MODE@ @OTHER@", ("id", "7,8"), ("Mode", "IMMEDIATE"));

        Assert.Equal("KILL '7,8' IMMEDIATE @OTHER@", sql);
    }

    [Fact]
    public void Fill_does_not_substitute_inside_values()
    {
        var sql = CatalogText.Fill("owner = '@OWNER@' AND object = '@NAME@'", ("owner", "@NAME@"), ("name", "X"));

        Assert.Equal("owner = '@NAME@' AND object = 'X'", sql);
    }

    [Fact]
    public void Fill_without_arguments_returns_the_text_as_is()
        => Assert.Equal("SELECT '@A@' FROM dual", CatalogText.Fill("SELECT '@A@' FROM dual"));
}
