using Oranda.Core.Connection;
using Oranda.Core.Metadata;
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
        Assert.Equal("TBR-17004: no privilege line 2", failed.Rows[0][0]);
        Assert.Equal(DatabaseInfoSections.PrivilegeHint(Dbms.Tibero), failed.Rows[1][0]); // 권한 오류면 필요한 권한을 안내한다
        Assert.Equal(6, sections.Count);
    }

    [Fact]
    public async Task Without_v_dollar_views_instance_and_database_fall_back_to_sys_context_and_version_to_the_session()
    {
        var asked = new List<string>();
        var provider = new TiberoDatabaseInfoProvider((_, id, _, _) =>
        {
            asked.Add(id);
            return id switch
            {
                "dbinfo.instance" or "dbinfo.database" or "dbinfo.version" or "dbinfo.parameters" or "dbinfo.nls"
                    => throw new InvalidOperationException("ODP-8033:Specified schema object was not found.\n at line 14, column 9 of null:\n FROM v$instance i"),
                "dbinfo.instance_basic" => Task.FromResult(Table(["INSTANCE_NAME", "DB_NAME"], ["tb", "D"])),
                "dbinfo.database_basic" => Task.FromResult(Table(["NAME"], ["D"])),
                "dbinfo.nls_session" => Task.FromResult(Table(["SCOPE", "PARAMETER", "VALUE"], ["SESSION", "NLS_SORT", "BINARY"])),
                _ => Task.FromResult(Table(["A"], ["1"])),
            };
        });
        await using var session = Session();

        var sections = await provider.GetAsync(session, CancellationToken.None);

        Assert.Equal(["dbinfo.instance", "dbinfo.instance_basic", "dbinfo.database", "dbinfo.database_basic", "dbinfo.version", "dbinfo.parameters", "dbinfo.nls", "dbinfo.nls_session"], asked);
        var note = DatabaseInfoSections.Note(Dbms.Tibero, new InvalidOperationException("ODP-8033: x"));
        Assert.Equal([["instance_name", "tb"], ["db_name", "D"], ["안내", note]], sections[0].Rows.Select(r => r.ToArray()));
        Assert.Equal([["name", "D"], ["안내", note]], sections[1].Rows.Select(r => r.ToArray()));
        Assert.Equal([["version", "Tibero 6"], ["안내", note]], sections[2].Rows.Select(r => r.ToArray()));
        Assert.Equal(["오류"], sections[4].Columns); // 파라미터는 대신할 것이 없다
        Assert.Equal(["SCOPE", "PARAMETER", "VALUE"], sections[5].Columns); // NLS 는 세션 값만, 끝에 안내 줄
        Assert.Equal(["SESSION", "NLS_SORT", "BINARY"], sections[5].Rows[0]);
        Assert.Equal("(안내) " + note, sections[5].Rows[1][0]);
        Assert.DoesNotContain(sections.Take(3), x => x.Columns is ["오류"]);
    }

    [Fact]
    public async Task A_failing_fallback_shows_the_original_error()
    {
        var provider = new TiberoDatabaseInfoProvider((_, id, _, _) => id.StartsWith("dbinfo.instance", StringComparison.Ordinal)
            ? throw new InvalidOperationException(id == "dbinfo.instance" ? "ODP-8033: first" : "ODP-11006: second")
            : Task.FromResult(Table(["X"], ["1"])));
        await using var session = Session();

        var sections = await provider.GetAsync(session, CancellationToken.None);

        Assert.Equal(["오류"], sections[0].Columns);
        Assert.Equal("ODP-8033: first", sections[0].Rows[0][0]);
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
    [InlineData("dbinfo.instance_basic")]
    [InlineData("dbinfo.database")]
    [InlineData("dbinfo.database_basic")]
    [InlineData("dbinfo.version")]
    [InlineData("dbinfo.parameters")]
    [InlineData("dbinfo.nls")]
    [InlineData("dbinfo.nls_session")]
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
