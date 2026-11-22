using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers.External;

public class MetadataToolHandlersTests
{
    /// <summary>이름 패턴·INVALID 거르기까지 하는 가짜 메타(기본 가짜는 패턴을 보지 않는다).</summary>
    private sealed class Meta : IMetadataProvider
    {
        public FakeMetadataProvider Inner { get; } = new();

        public HashSet<DbObjectRef> Invalid { get; } = [];

        public bool InvalidFails { get; set; }

        public List<ObjectQuery> Queries { get; } = [];

        public void Add(string owner, string name, string type) => Inner.Objects.Add(new DbObjectRef(owner, name, type));

        public async Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        {
            Queries.Add(q);
            if (q.InvalidOnly && InvalidFails)
            {
                throw new InvalidOperationException("ORA-00942: table or view does not exist");
            }

            var all = await Inner.ListObjectsAsync(s, q with { Limit = null }, ct);
            var pattern = q.NamePattern is null ? null : CachedMetadataProvider.LikeToRegex(q.NamePattern);
            return [.. all.Where(o => (pattern is null || pattern.IsMatch(o.Name)) && (!q.InvalidOnly || Invalid.Contains(o))).Take(q.Limit ?? int.MaxValue)];
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => Inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyDictionary<string, string>> ListTableCommentsAsync(ISession s, string owner, CancellationToken ct) => Inner.ListTableCommentsAsync(s, owner, ct);

        public Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct) => Inner.GetNameStepAsync(s, owner, name, ct);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => Inner.GetTableAsync(s, table, ct);

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Inner.GetProceduresAsync(s, obj, ct);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Inner.GetDdlAsync(s, obj, o, ct);

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Inner.GetSourceAsync(s, obj, ct);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Inner.GetCompileErrorsAsync(s, obj, ct);
    }

    private const string PackageSpec = """
        PACKAGE pk_order AS
          PROCEDURE add_order(p_id IN NUMBER);
          FUNCTION total(p_id IN NUMBER) RETURN NUMBER;
        END pk_order;
        """;

    private const string PackageBody = """
        PACKAGE BODY pk_order AS
          PROCEDURE add_order(p_id IN NUMBER) IS
          BEGIN
            INSERT INTO tb_order (id) VALUES (p_id);
          END add_order;

          FUNCTION total(p_id IN NUMBER) RETURN NUMBER IS
            v NUMBER;
          BEGIN
            SELECT COUNT(*) INTO v FROM tb_order WHERE id = p_id;
            RETURN v;
          END total;
        END pk_order;
        """;

    private static Meta Sample()
    {
        var meta = new Meta();
        meta.Add("APP", "TB_ORDER", "TABLE");
        meta.Add("APP", "TB_ORDER_ITEM", "TABLE");
        meta.Add("APP", "TB_ORDERX", "VIEW");
        meta.Add("APP", "PK_ORDER", "PACKAGE");
        meta.Add("APP", "PK_ORDER", "PACKAGE BODY");
        meta.Add("APP", "SQ_ORDER", "SEQUENCE");
        meta.Add("SCOTT", "EMP", "TABLE");
        meta.Add("SCOTT", "ORD", "SYNONYM");
        meta.Inner.Schemas.AddRange(["APP", "SCOTT"]);
        meta.Inner.Comments["APP"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["TB_ORDER"] = "주문" };
        meta.Inner.NameSteps[("SCOTT", "ORD")] = NameStep.Synonym("APP", "TB_ORDER");
        meta.Inner.NameSteps[("PUBLIC", "PKO")] = NameStep.Synonym("APP", "PK_ORDER");
        meta.Inner.NameSteps[("PUBLIC", "REMOTE_T")] = NameStep.Synonym("X", "T", "LNK");
        meta.Inner.Sources[new DbObjectRef("APP", "PK_ORDER", "PACKAGE")] = PackageSpec;
        meta.Inner.Sources[new DbObjectRef("APP", "PK_ORDER", "PACKAGE BODY")] = PackageBody;
        meta.Inner.Procedures[new DbObjectRef("APP", "PK_ORDER", "PACKAGE")] =
        [
            new ProcedureInfo("ADD_ORDER", false, [new ArgumentInfo("P_ID", 1, "NUMBER", ArgumentDirection.In)]),
            new ProcedureInfo("TOTAL", true, [new ArgumentInfo("P_ID", 1, "NUMBER", ArgumentDirection.In) { HasDefault = true }]) { ReturnType = "NUMBER", Overload = 1 },
            new ProcedureInfo("TOTAL", true, [new ArgumentInfo("P_CODE", 1, "VARCHAR2", ArgumentDirection.InOut)]) { ReturnType = "NUMBER", Overload = 2 },
        ];
        return meta;
    }

    private static JsonObject Obj(string name) => new() { ["object"] = name };

    [Fact]
    public async Task Find_objects_matches_pattern_marks_invalid_adds_comments_and_reports_truncation()
    {
        var meta = Sample();
        meta.Invalid.Add(new DbObjectRef("APP", "TB_ORDERX", "VIEW"));

        var output = await new FindObjectsHandler(meta).ExecuteAsync(
            ExternalFakes.Db(new JsonObject { ["name_like"] = "tb_order%", ["limit"] = 2 }), CancellationToken.None);

        var objects = output.Result!["objects"]!.AsArray();
        Assert.Equal(2, objects.Count);
        Assert.True(output.Result["truncated"]!.GetValue<bool>());
        Assert.Equal("TB_ORDER%", meta.Queries[0].NamePattern);
        Assert.Equal(3, meta.Queries[0].Limit);
        var order = objects.Single(o => o!["name"]!.GetValue<string>() == "TB_ORDER")!;
        Assert.Equal("VALID", order["status"]!.GetValue<string>());
        Assert.Equal("주문", order["comment"]!.GetValue<string>());
    }

    [Fact]
    public async Task Find_objects_filters_types_and_owner_and_drops_status_when_status_lookup_fails()
    {
        var meta = Sample();
        meta.InvalidFails = true;

        var output = await new FindObjectsHandler(meta).ExecuteAsync(
            ExternalFakes.Db(new JsonObject { ["name_like"] = "%ORDER%", ["owner"] = "app", ["types"] = new JsonArray("view", "package") }), CancellationToken.None);
        var bad = await new FindObjectsHandler(meta).ExecuteAsync(ExternalFakes.Db(new JsonObject { ["name_like"] = "X", ["limit"] = 500 }), CancellationToken.None);

        Assert.Equal(["TB_ORDERX", "PK_ORDER"], output.Result!["objects"]!.AsArray().Select(o => o!["name"]!.GetValue<string>()).Order().Reverse());
        Assert.All(output.Result["objects"]!.AsArray(), o => Assert.Null(o!["status"]));
        Assert.Equal("APP", meta.Queries[0].Owner);
        Assert.False(output.Result["truncated"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", bad.ErrorCode);
    }

    [Fact]
    public async Task List_schemas_returns_an_object_with_names()
    {
        var output = await new ListSchemasHandler(Sample()).ExecuteAsync(ExternalFakes.Db([], AiAccessLevel.Metadata), CancellationToken.None);

        Assert.Equal(["APP", "SCOTT"], output.Result!["schemas"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task Get_ddl_puts_ddl_in_sql_text_and_resolves_non_table_objects()
    {
        var meta = Sample();
        var table = await new GetDdlHandler(meta).ExecuteAsync(
            ExternalFakes.Db(Obj("APP.TB_ORDER"), resolved: ("object", new AgentObjectRef("APP", "TB_ORDER"))), CancellationToken.None);
        var sequence = await new GetDdlHandler(meta).ExecuteAsync(ExternalFakes.Db(Obj("app.sq_order")), CancellationToken.None);
        var missing = await new GetDdlHandler(meta).ExecuteAsync(ExternalFakes.Db(Obj("APP.NOPE")), CancellationToken.None);

        Assert.Equal("TABLE", table.Result!["type"]!.GetValue<string>());
        Assert.Equal("-- DDL APP.TB_ORDER", table.Result["sql_text"]!.GetValue<string>());
        Assert.Null(table.Result["ddl"]);
        Assert.Equal("SEQUENCE", sequence.Result!["type"]!.GetValue<string>());
        Assert.Equal("scope", missing.ErrorCode);
    }

    [Fact]
    public async Task Get_source_joins_package_spec_and_body_and_lists_members()
    {
        var output = await new GetSourceHandler(Sample()).ExecuteAsync(ExternalFakes.Db(Obj("APP.PK_ORDER")), CancellationToken.None);

        var text = output.Result!["sql_text"]!.GetValue<string>();
        Assert.Null(output.Result["text"]);
        Assert.Equal("PACKAGE", output.Result["type"]!.GetValue<string>());
        Assert.Contains("PACKAGE pk_order AS", text, StringComparison.Ordinal);
        Assert.Contains("PACKAGE BODY pk_order AS", text, StringComparison.Ordinal);
        Assert.Equal(["ADD_ORDER", "TOTAL"], output.Result["members"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    [Fact]
    public async Task Get_source_member_cuts_declaration_and_implementation_only()
    {
        var meta = Sample();

        var output = await new GetSourceHandler(meta).ExecuteAsync(ExternalFakes.Db(new JsonObject { ["object"] = "APP.PK_ORDER", ["member"] = "total" }), CancellationToken.None);
        var missing = await new GetSourceHandler(meta).ExecuteAsync(ExternalFakes.Db(new JsonObject { ["object"] = "APP.PK_ORDER", ["member"] = "nope" }), CancellationToken.None);
        var table = await new GetSourceHandler(meta).ExecuteAsync(ExternalFakes.Db(Obj("APP.TB_ORDER")), CancellationToken.None);

        var text = output.Result!["sql_text"]!.GetValue<string>();
        Assert.Contains("FUNCTION total(p_id IN NUMBER) RETURN NUMBER;", text, StringComparison.Ordinal);
        Assert.Contains("END total;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("add_order", text, StringComparison.Ordinal);
        Assert.Equal("invalid_arguments", missing.ErrorCode);
        Assert.Equal("invalid_arguments", table.ErrorCode);
    }

    [Fact]
    public async Task Resolve_names_follows_private_and_public_synonyms_and_hides_missing_and_remote()
    {
        var output = await new ResolveNamesHandler(Sample()).ExecuteAsync(
            ExternalFakes.Db(new JsonObject { ["names"] = new JsonArray("emp", "ord", "pko", "app.tb_order", "nope", "remote_t") }), CancellationToken.None);

        var names = output.Result!["names"]!.AsArray();
        Assert.False(names[0]!["via_synonym"]!.GetValue<bool>());
        Assert.Equal("SCOTT", names[0]!["owner"]!.GetValue<string>());
        Assert.Equal("APP.TB_ORDER", names[1]!["owner"]!.GetValue<string>() + "." + names[1]!["name"]!.GetValue<string>());
        Assert.True(names[1]!["via_synonym"]!.GetValue<bool>());
        Assert.Equal("PACKAGE", names[2]!["type"]!.GetValue<string>());
        Assert.True(names[2]!["via_synonym"]!.GetValue<bool>());
        Assert.False(names[3]!["via_synonym"]!.GetValue<bool>());
        Assert.False(names[4]!["found"]!.GetValue<bool>());
        Assert.Null(names[4]!["owner"]);
        Assert.False(names[5]!["found"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Resolve_names_uses_the_current_schema_and_refuses_empty_list()
    {
        var meta = Sample();

        var output = await new ResolveNamesHandler(meta).ExecuteAsync(
            ExternalFakes.Db(new JsonObject { ["names"] = new JsonArray("tb_order") }, currentSchema: "APP"), CancellationToken.None);
        var empty = await new ResolveNamesHandler(meta).ExecuteAsync(ExternalFakes.Db(new JsonObject { ["names"] = new JsonArray() }), CancellationToken.None);

        Assert.True(output.Result!["names"]![0]!["found"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", empty.ErrorCode);
    }

    [Fact]
    public async Task Get_procedure_args_lists_members_overloads_directions_and_returns()
    {
        var meta = Sample();

        var output = await new GetProcedureArgsHandler(meta).ExecuteAsync(ExternalFakes.Db(Obj("APP.PK_ORDER")), CancellationToken.None);
        var table = await new GetProcedureArgsHandler(meta).ExecuteAsync(ExternalFakes.Db(Obj("APP.TB_ORDER")), CancellationToken.None);

        var members = output.Result!["members"]!.AsArray();
        Assert.Equal(3, members.Count);
        Assert.Null(members[0]!["returns"]);
        Assert.Equal("NUMBER", members[1]!["returns"]!.GetValue<string>());
        Assert.Equal(2, members[2]!["overload"]!.GetValue<int>());
        Assert.Equal("IN OUT", members[2]!["args"]![0]!["in_out"]!.GetValue<string>());
        Assert.True(members[1]!["args"]![0]!["default"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", table.ErrorCode);
    }

    [Fact]
    public async Task Metadata_errors_carry_only_the_error_code()
    {
        var output = await new ListSchemasHandler(new Broken()).ExecuteAsync(ExternalFakes.Db([]), CancellationToken.None);

        Assert.Equal("tool_failed", output.ErrorCode);
        Assert.DoesNotContain("SECRET", output.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("SECRET", output.LogDetail, StringComparison.Ordinal);
    }

    private sealed class Broken : IMetadataProvider
    {
        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new InvalidOperationException("ORA-01722: 'SECRET'");

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
