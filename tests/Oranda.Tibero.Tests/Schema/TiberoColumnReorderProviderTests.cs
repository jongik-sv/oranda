using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoColumnReorderProviderTests
{
    private static readonly string[] TraitColumns = ["TEMPORARY", "CLUSTER_NAME", "EXTERNAL_COUNT"];
    private static readonly string[] SourceColumns = ["OWNER", "NAME", "TYPE", "LINE", "TEXT"];
    private static readonly DbObjectRef Emp = new("HR", "EMP", "TABLE");

    private static List<(string Id, IReadOnlyDictionary<string, object?>? Binds)> NewCalls() => [];

    [Fact]
    public async Task Every_catalog_id_the_provider_uses_resolves_for_tibero()
    {
        await using var session = Session();

        foreach (var id in TiberoColumnReorderProvider.CatalogIds)
        {
            var sql = TiberoCatalogQuery.Sql(session, id);

            Assert.False(string.IsNullOrWhiteSpace(sql), id);
            Assert.DoesNotContain(TiberoCatalogQuery.PrefixToken, sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Y", 0L, true, false)]
    [InlineData("N", 1L, false, true)]
    [InlineData("N", 0L, false, false)]
    public async Task Traits_map_temporary_and_external_count_and_never_find_a_cluster(string temporary, long external, bool isTemporary, bool isExternal)
    {
        var calls = NewCalls();
        var provider = new TiberoColumnReorderProvider(Recording(calls, _ => Table(TraitColumns, [temporary, null, external])));
        await using var session = Session();

        var traits = await provider.GetTraitsAsync(session, Emp, CancellationToken.None);

        Assert.Equal(isTemporary, traits.IsTemporary);
        Assert.Equal(isExternal, traits.IsExternal);
        Assert.False(traits.IsClustered);
        var call = Assert.Single(calls);
        Assert.Equal(TiberoColumnReorderProvider.TraitsId, call.Id);
        Assert.Equal("HR", call.Binds!["owner"]);
        Assert.Equal("EMP", call.Binds["name"]);
    }

    [Fact]
    public async Task A_table_with_no_row_has_all_traits_off()
    {
        var provider = new TiberoColumnReorderProvider(Recording(NewCalls(), _ => Table(TraitColumns)));
        await using var session = Session();

        var traits = await provider.GetTraitsAsync(session, Emp, CancellationToken.None);

        Assert.Equal(new ColumnReorderTraits(false, false, null), traits);
    }

    [Fact]
    public async Task Referencing_source_lines_map_the_rows_trim_line_ends_and_bind_the_row_limit()
    {
        var calls = NewCalls();
        var provider = new TiberoColumnReorderProvider(Recording(calls, _ => Table(SourceColumns,
            ["HR", "P_RAISE", "PROCEDURE", 12L, "  SELECT * FROM emp;\r\n"],
            ["HR", "TRG_EMP", "TRIGGER", 3L, null])));
        await using var session = Session();

        var lines = await provider.FindReferencingSourceLinesAsync(session, Emp, CancellationToken.None);

        Assert.Equal(2, lines.Count);
        Assert.Equal(new DbObjectRef("HR", "P_RAISE", "PROCEDURE"), lines[0].Ref);
        Assert.Equal(12, lines[0].Line);
        Assert.Equal("  SELECT * FROM emp;", lines[0].Text);
        Assert.Equal(string.Empty, lines[1].Text);
        var call = Assert.Single(calls);
        Assert.Equal(TiberoColumnReorderProvider.SourceId, call.Id);
        Assert.Equal("HR", call.Binds!["owner"]);
        Assert.Equal("EMP", call.Binds["name"]);
        Assert.Equal(TiberoColumnReorderProvider.MaxSourceLines, call.Binds["max_rows"]);
    }
}
