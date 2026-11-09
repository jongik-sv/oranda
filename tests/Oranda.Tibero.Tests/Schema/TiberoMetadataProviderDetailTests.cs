using Oranda.Core.Catalog;
using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>객체 상세·DDL·이름 풀이·소스·컴파일 오류를 가짜 카탈로그 실행기로 본다.</summary>
public class TiberoMetadataProviderDetailTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static readonly (string Id, TiberoSessionQuery.Table Table)[] TableAnswers =
    [
        ("meta.detail_general", Table(["OBJECT_TYPE", "STATUS", "CREATED", "LAST_DDL_TIME", "EDITION_FLAG", "OBJECT_ID"],
            ["TABLE", "VALID", new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), null, 1234m])),
        ("meta.columns", Table(["C0", "C1", "C2", "C3", "C4", "C5", "C6", "C7", "C8", "C9", "C10", "C11", "C12"],
            ["EMPNO", 1m, "NUMBER", null, 4m, 0m, "N", null, "NO", null, "NO", null, null])),
        ("meta.detail_triggers", Table(["A", "B", "C", "D", "E", "F"], ["EMP_TRG", "BEFORE EACH ROW", "INSERT", "ENABLED", null, "SCOTT"])),
        ("meta.detail_referenced_by", Table(["A", "B", "C", "D", "E", "F", "G"], ["HR", "BONUS", "BONUS_FK", "EMPNO", "EMP_PK", "CASCADE", "ENABLED"])),
        ("meta.detail_stats", Table(["A", "B", "C", "D", "E", "F", "G", "H", "I"], [14m, 5m, 40m, null, 14m, "NO", "N", "DISABLED", "USR"])),
        ("meta.detail_size_user", Table(["A", "B", "C", "D"], ["EMP", "TABLE", 65536m, 1m], ["EMP_PK", "INDEX", 65536m, 1m])),
        ("meta.detail_size", Table(["A", "B", "C", "D"], ["EMP", "TABLE", 8192m, 1m])),
        ("meta.detail_privileges_user", Table(["A", "B", "C", "D"], ["HR", "SELECT", "NO", "SCOTT"])),
        ("meta.detail_privileges", Table(["A", "B", "C", "D"], ["PUBLIC", "SELECT", "NO", "SCOTT"])),
        ("meta.detail_dependencies", Table(["A", "B", "C", "D"], ["USED_BY", "SCOTT", "EMP_V", "VIEW"])),
    ];

    [Fact]
    public async Task Table_detail_has_the_oracle_sections_in_order_with_tibero_reasons_and_user_views()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(TableAnswers)));
        await using var session = Session();

        var detail = await provider.GetObjectDetailAsync(session, Emp, CancellationToken.None);

        Assert.Equal(["일반", "열", "트리거", "참조됨", "통계", "크기", "권한", "의존"], detail.Sections.Select(x => x.Title));
        Assert.Equal(["유형", "TABLE"], detail.Sections[0].Rows[0]);
        Assert.Equal(["편집 가능(editionable)", "Tibero 에서는 지원하지 않음"], detail.Sections[0].Rows[4]);
        Assert.Equal(["#", "이름", "형식", "길이", "정밀도", "소수", "NULL 허용", "기본값", "가상", "주석"], detail.Sections[1].Columns);
        Assert.Equal(["합계", null, 131072L, null], detail.Sections[5].Rows[^1]);
        Assert.Equal(["HR", "SELECT", "NO", "SCOTT"], detail.Sections[6].Rows[0]);
        Assert.Equal(["참조됨", "SCOTT", "EMP_V", "VIEW"], detail.Sections[7].Rows[0]);
        Assert.Null(detail.SourceObject);
        Assert.Contains(calls, c => c.Id == "meta.detail_size_user");
        Assert.DoesNotContain(calls, c => c.Id is "meta.detail_size" or "meta.detail_privileges");
        var objectBinds = new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "EMP", ["type"] = "TABLE" };
        Assert.Equal(objectBinds, calls.Single(c => c.Id == "meta.detail_general").Binds);
        Assert.Equal(objectBinds, calls.Single(c => c.Id == "meta.detail_dependencies").Binds);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "EMP" }, calls.Single(c => c.Id == "meta.detail_partitions").Binds);
    }

    [Fact]
    public async Task Dictionary_access_reads_the_dba_size_and_privilege_variants()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(TableAnswers)));
        await using var session = Session(dictionary: true);

        var detail = await provider.GetObjectDetailAsync(session, Emp, CancellationToken.None);

        Assert.Equal(["PUBLIC", "SELECT", "NO", "SCOTT"], detail.Sections.Single(x => x.Title == "권한").Rows[0]);
        Assert.Contains(calls, c => c.Id == "meta.detail_size");
        Assert.DoesNotContain(calls, c => c.Id is "meta.detail_size_user" or "meta.detail_privileges_user");
    }

    [Fact]
    public async Task A_package_detail_asks_only_general_privileges_and_dependencies()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(TableAnswers)));
        await using var session = Session();

        var detail = await provider.GetObjectDetailAsync(session, new DbObjectRef("HR", "PKG", "PACKAGE"), CancellationToken.None);

        Assert.Equal(["meta.detail_general", "meta.detail_privileges_user", "meta.detail_dependencies"], calls.Select(c => c.Id));
        Assert.Equal(["일반", "권한", "의존"], detail.Sections.Select(x => x.Title));
    }

    [Fact]
    public async Task Server_errors_drop_only_the_optional_sections_and_disconnects_and_required_failures_are_thrown()
    {
        var denied = ServerError(-8033, "TBR-8033: Specified schema object was not found.");
        var other = ServerError(-10007, "TBR-10007: non-access server error");
        var disconnected = ServerError(-12001, "TBR-12001: session killed");
        var answer = ById(TableAnswers);
        var optional = new TiberoMetadataProvider((_, id, _, _) => id switch
        {
            "meta.detail_triggers" or "meta.detail_size_user" => Task.FromException<TiberoSessionQuery.Table>(denied),
            "meta.detail_referenced_by" => Task.FromException<TiberoSessionQuery.Table>(other),
            _ => Task.FromResult(answer(id)),
        });
        var dropped = new TiberoMetadataProvider((_, id, _, _) => id == "meta.detail_triggers"
            ? Task.FromException<TiberoSessionQuery.Table>(disconnected)
            : Task.FromResult(answer(id)));
        var required = new TiberoMetadataProvider((_, id, _, _) => id == "meta.detail_partitions"
            ? Task.FromException<TiberoSessionQuery.Table>(denied)
            : Task.FromResult(answer(id)));
        var missing = new TiberoMetadataProvider((_, id, _, _) => id == "meta.detail_triggers"
            ? Task.FromException<TiberoSessionQuery.Table>(new CatalogQueryNotFoundException(id, 6))
            : Task.FromResult(answer(id)));
        await using var session = Session();

        var detail = await optional.GetObjectDetailAsync(session, Emp, CancellationToken.None);

        Assert.Equal(["일반", "열", "통계", "권한", "의존"], detail.Sections.Select(x => x.Title));
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => required.GetObjectDetailAsync(session, Emp, CancellationToken.None));
        Assert.Same(denied, thrown);
        Assert.Same(disconnected, await Assert.ThrowsAnyAsync<Exception>(() => dropped.GetObjectDetailAsync(session, Emp, CancellationToken.None)));
        await Assert.ThrowsAsync<CatalogQueryNotFoundException>(() => missing.GetObjectDetailAsync(session, Emp, CancellationToken.None));
    }

    [Fact]
    public async Task Ddl_of_a_type_not_built_yet_is_not_supported_and_asks_nothing()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById()));
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetDdlAsync(session, new DbObjectRef("SCOTT", "EMP_V", "VIEW"), new DdlOptions(), CancellationToken.None));

        Assert.Equal(TiberoMetadataProvider.DdlNotSupported("VIEW"), ex.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Name_step_follows_a_synonym_and_stops_at_a_table()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var synonym = new TiberoMetadataProvider(Recording(calls, _ => Table(["KIND", "TABLE_OWNER", "TABLE_NAME", "DB_LINK"], ["SYNONYM", "SYS", "DUAL", null])));
        var table = new TiberoMetadataProvider(Recording([], _ => Table(["KIND", "TABLE_OWNER", "TABLE_NAME", "DB_LINK"], ["OBJECT", null, null, null])));
        var none = new TiberoMetadataProvider(Recording([], _ => Table(["KIND", "TABLE_OWNER", "TABLE_NAME", "DB_LINK"])));
        await using var session = Session();

        Assert.Equal(NameStep.Synonym("SYS", "DUAL"), await synonym.GetNameStepAsync(session, "PUBLIC", "DUAL", CancellationToken.None));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "PUBLIC", ["name"] = "DUAL" }, Assert.Single(calls).Binds);
        Assert.Equal(NameStep.TableOrView, await table.GetNameStepAsync(session, "SCOTT", "EMP", CancellationToken.None));
        Assert.Null(await none.GetNameStepAsync(session, "SCOTT", "NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task Name_step_is_null_on_no_access_and_other_server_errors_but_a_disconnect_is_thrown()
    {
        await using var session = Session();
        var disconnected = ServerError(-12001, "TBR-12001: session killed");
        var denied = new TiberoMetadataProvider(Throwing(ServerError(-8033, "TBR-8033: Specified schema object was not found.")));
        var nonAccess = new TiberoMetadataProvider(Throwing(ServerError(-10007, "TBR-10007: non-access server error")));
        var dropped = new TiberoMetadataProvider(Throwing(disconnected));
        var notDriver = new TiberoMetadataProvider(Throwing(new CatalogQueryNotFoundException("meta.name_step", 6)));

        Assert.Null(await denied.GetNameStepAsync(session, "SCOTT", "EMP", CancellationToken.None));
        Assert.Null(await nonAccess.GetNameStepAsync(session, "SCOTT", "EMP", CancellationToken.None));
        Assert.Same(disconnected, await Assert.ThrowsAnyAsync<Exception>(() => dropped.GetNameStepAsync(session, "SCOTT", "EMP", CancellationToken.None)));
        await Assert.ThrowsAsync<CatalogQueryNotFoundException>(() => notDriver.GetNameStepAsync(session, "SCOTT", "EMP", CancellationToken.None));
    }

    [Fact]
    public async Task Source_joins_lines_with_object_binds_and_is_not_found_when_empty()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, _ => Table(["TEXT"], ["PACKAGE BODY PKG AS\n"], ["END;"])));
        var empty = new TiberoMetadataProvider(Recording([], _ => Table(["TEXT"])));
        var body = new DbObjectRef("HR", "PKG", "PACKAGE BODY");
        await using var session = Session();

        Assert.Equal("PACKAGE BODY PKG AS\nEND;\n", await provider.GetSourceAsync(session, body, CancellationToken.None));
        Assert.Equal(("meta.source", "PACKAGE BODY"), (calls[0].Id, calls[0].Binds!["type"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.GetSourceAsync(session, body, CancellationToken.None));
    }

    [Fact]
    public async Task Compile_errors_map_line_column_message_warning_and_number()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, _ => Table(["LINE", "POSITION", "TEXT", "ATTRIBUTE", "MESSAGE_NUMBER"],
            [3m, 7m, " TBR-15146: undefined  \n", "ERROR", 15146m],
            [0m, null, "unused", "WARNING", null])));
        await using var session = Session();

        var errors = await provider.GetCompileErrorsAsync(session, new DbObjectRef("HR", "P", "PROCEDURE"), CancellationToken.None);

        Assert.Equal([new CompileError(3, 7, "TBR-15146: undefined", false) { MessageNumber = 15146 }, new CompileError(1, 1, "unused", true)], errors);
        Assert.Equal("meta.compile_errors", Assert.Single(calls).Id);
    }
}
