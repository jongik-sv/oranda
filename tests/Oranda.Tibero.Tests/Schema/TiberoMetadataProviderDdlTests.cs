using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>DDL 추출(표 = <c>meta.ddl</c>, PSM 객체 = <c>meta.source</c> 글)을 가짜 실행기로 본다.</summary>
public class TiberoMetadataProviderDdlTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static TiberoMetadataProvider Provider(
        List<(string Id, IReadOnlyDictionary<string, object?>? Binds)> calls,
        Func<string, TiberoSessionQuery.Table> answer,
        Func<IReadOnlyDictionary<string, object?>, string?>? table = null) =>
        new(Recording(calls, answer))
        {
            ReadTableDdl = (_, binds, _) => Task.FromResult(table?.Invoke(binds)),
        };

    [Fact]
    public async Task Table_ddl_sends_exactly_the_binds_meta_ddl_expects_and_trims_the_result()
    {
        IReadOnlyDictionary<string, object?>? sent = null;
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, ById(), binds =>
        {
            sent = binds;
            return "\r\n  CREATE TABLE \"SCOTT\".\"EMP\" (A NUMBER);\n\n";
        });
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, Emp,
            new DdlOptions { SqlTerminator = true, IncludeStorage = true, IncludeDependentObjects = true }, CancellationToken.None);

        Assert.Equal("CREATE TABLE \"SCOTT\".\"EMP\" (A NUMBER);", ddl);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["p_type"] = "TABLE",
            ["name"] = "EMP",
            ["owner"] = "SCOTT",
            ["terminator"] = 1,
            ["storage"] = 1,
            ["segment_attributes"] = 0,
            ["tablespace"] = 0,
            ["dependents"] = 1,
        }, sent);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Table_ddl_without_the_terminator_binds_zero()
    {
        IReadOnlyDictionary<string, object?>? sent = null;
        var provider = Provider([], ById(), binds =>
        {
            sent = binds;
            return "CREATE TABLE T (A NUMBER)";
        });
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, Emp, new DdlOptions { SqlTerminator = false }, CancellationToken.None);

        Assert.Equal("CREATE TABLE T (A NUMBER)", ddl);
        Assert.Equal(0, sent!["terminator"]);
    }

    [Fact]
    public async Task A_procedure_becomes_create_or_replace_with_the_qualified_name_and_a_slash()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, _ => Table(["TEXT"], ["PROCEDURE p_hello (a NUMBER)\n"], ["IS\n"], ["BEGIN NULL; END;\n"]));
        await using var session = Session();

        var withSlash = await provider.GetDdlAsync(session, new DbObjectRef("HR", "P_HELLO", "PROCEDURE"), new DdlOptions(), CancellationToken.None);
        var bare = await provider.GetDdlAsync(session, new DbObjectRef("HR", "P_HELLO", "PROCEDURE"), new DdlOptions { SqlTerminator = false }, CancellationToken.None);

        Assert.Equal("CREATE OR REPLACE PROCEDURE \"HR\".\"P_HELLO\" (a NUMBER)\nIS\nBEGIN NULL; END;\n/", withSlash);
        Assert.Equal("CREATE OR REPLACE PROCEDURE \"HR\".\"P_HELLO\" (a NUMBER)\nIS\nBEGIN NULL; END;", bare);
        Assert.Equal(["meta.source", "meta.source"], calls.Select(c => c.Id));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "HR", ["name"] = "P_HELLO", ["type"] = "PROCEDURE" }, calls[0].Binds);
    }

    [Theory]
    [InlineData("PACKAGE", "PACKAGE pkg AS\nEND;")]
    [InlineData("PACKAGE BODY", "PACKAGE BODY pkg AS\nEND;")]
    [InlineData("FUNCTION", "FUNCTION pkg RETURN NUMBER IS BEGIN RETURN 1; END;")]
    [InlineData("TYPE", "TYPE pkg AS OBJECT (a NUMBER)")]
    [InlineData("TYPE BODY", "TYPE BODY pkg AS\nEND;")]
    public async Task Each_psm_type_asks_its_own_source_type_and_keeps_spec_and_body_apart(string type, string source)
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, _ => Table(["TEXT"], [source]));
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, new DbObjectRef("HR", "PKG", type), new DdlOptions { SqlTerminator = false }, CancellationToken.None);

        Assert.StartsWith("CREATE OR REPLACE " + type + " \"HR\".\"PKG\"", ddl, StringComparison.Ordinal);
        Assert.Equal(type, calls.Single().Binds!["type"]);
    }

    [Fact]
    public async Task A_trigger_is_built_from_its_source_lines()
    {
        var provider = Provider([], _ => Table(["TEXT"], ["TRIGGER trg_emp_bi\n"], ["BEFORE INSERT ON emp\n"], ["BEGIN NULL; END;"]));
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, new DbObjectRef("SCOTT", "TRG_EMP_BI", "TRIGGER"), new DdlOptions(), CancellationToken.None);

        Assert.Equal("CREATE OR REPLACE TRIGGER \"SCOTT\".\"TRG_EMP_BI\"\nBEFORE INSERT ON emp\nBEGIN NULL; END;\n/", ddl);
    }

    [Theory]
    [InlineData("VIEW")]
    [InlineData("SEQUENCE")]
    [InlineData("SYNONYM")]
    [InlineData("INDEX")]
    [InlineData("MATERIALIZED VIEW")]
    public async Task Types_built_later_say_which_type_is_not_supported(string type)
    {
        var provider = Provider([], ById());
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetDdlAsync(session, new DbObjectRef("S", "X", type), new DdlOptions(), CancellationToken.None));

        Assert.Contains(type, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_objects_throw_invalid_operation_like_the_source_reader()
    {
        var noSource = Provider([], ById());
        var emptyDdl = Provider([], ById(), _ => " \r\n");
        var nullDdl = Provider([], ById(), _ => null);
        var notPsm = Provider([], _ => Table(["TEXT"], ["FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;"]));
        await using var session = Session();

        await Assert.ThrowsAsync<InvalidOperationException>(() => noSource.GetDdlAsync(session, new DbObjectRef("S", "P", "PROCEDURE"), new DdlOptions(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => emptyDdl.GetDdlAsync(session, Emp, new DdlOptions(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => nullDdl.GetDdlAsync(session, Emp, new DdlOptions(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notPsm.GetDdlAsync(session, new DbObjectRef("S", "P", "PROCEDURE"), new DdlOptions(), CancellationToken.None));
    }
}
