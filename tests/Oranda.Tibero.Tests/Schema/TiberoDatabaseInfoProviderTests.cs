using Oranda.Tibero.Schema;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoDatabaseInfoProviderTests
{
    [Fact]
    public async Task Sections_follow_the_oracle_order_with_options_as_a_reason_and_one_row_results_transposed()
    {
        var asked = new List<string>();
        var provider = new TiberoDatabaseInfoProvider((_, id, _, _) =>
        {
            asked.Add(id);
            return Task.FromResult(id switch
            {
                "dbinfo.instance" => Table(["INSTANCE_NAME", "VERSION"], ["tb", "6"]),
                "dbinfo.database" => Table(["NAME"], ["D"]),
                _ => Table(["A", "B"], ["1", "2"], ["3", "4"]),
            });
        });
        await using var session = Session();

        var sections = await provider.GetAsync(session, CancellationToken.None);

        Assert.Equal(["인스턴스", "데이터베이스", "버전", "옵션", "파라미터", "NLS"], sections.Select(x => x.Title));
        Assert.Equal(["dbinfo.instance", "dbinfo.database", "dbinfo.version", "dbinfo.parameters", "dbinfo.nls"], asked);
        Assert.Equal(["항목", "값"], sections[0].Columns);
        Assert.Equal([["instance_name", "tb"], ["version", "6"]], sections[0].Rows.Select(r => r.ToArray()));
        Assert.Equal(TiberoDatabaseInfoProvider.OptionsNotSupported, Assert.Single(sections[3].Rows)[0]);
        Assert.Equal(["A", "B"], sections[4].Columns);
        Assert.Equal(2, sections[4].Rows.Count);
    }

    [Fact]
    public async Task A_failing_section_becomes_an_error_row_and_the_rest_still_load()
    {
        var provider = new TiberoDatabaseInfoProvider((_, id, _, _) => id == "dbinfo.parameters"
            ? throw new InvalidOperationException("TBR-17004: no privilege\nline 2")
            : Task.FromResult(Table(["X"], ["1"])));
        await using var session = Session();

        var sections = await provider.GetAsync(session, CancellationToken.None);

        var failed = sections.Single(x => x.Title == "파라미터");
        Assert.Equal(["오류"], failed.Columns);
        Assert.Equal("TBR-17004: no privilege line 2", Assert.Single(failed.Rows)[0]);
        Assert.Equal(6, sections.Count);
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_an_error_row()
    {
        var provider = new TiberoDatabaseInfoProvider((_, _, _, _) => throw new OperationCanceledException());
        await using var session = Session();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(session, CancellationToken.None));
    }

    [Theory]
    [InlineData("dbinfo.instance")]
    [InlineData("dbinfo.database")]
    [InlineData("dbinfo.version")]
    [InlineData("dbinfo.parameters")]
    [InlineData("dbinfo.nls")]
    public async Task Every_section_query_resolves_in_the_tibero_catalog(string id)
    {
        await using var session = Session();

        var sql = TiberoCatalogQuery.Sql(session, id);

        Assert.StartsWith("SELECT", sql.Split('\n').First(l => !l.StartsWith("--", StringComparison.Ordinal)).TrimStart(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "FROM all_tab_cols c")]
    [InlineData(true, "FROM dba_tab_cols c")]
    public async Task Catalog_sql_replaces_the_view_prefix_by_dictionary_access(bool dictionary, string expected)
    {
        await using var session = Session(dictionary);

        var sql = TiberoCatalogQuery.Sql(session, "meta.columns");

        Assert.DoesNotContain(TiberoCatalogQuery.PrefixToken, sql, StringComparison.Ordinal);
        Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
    }
}
