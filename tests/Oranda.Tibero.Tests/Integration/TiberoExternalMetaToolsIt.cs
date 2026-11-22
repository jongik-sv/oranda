using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Support;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// agent-cli-1122 레인 d-lang: 외부 연결 메타 도구(<c>find_objects</c>·<c>get_ddl</c>·<c>get_source</c>·<c>resolve_names</c>·<c>get_procedure_args</c>)를 Tibero 실서버로 부른다.
/// 쓰기(<c>ORANDA_IT_TIBERO_WRITE=1</c>)일 때만 ORT_AC_ 표·패키지를 만들고 읽은 뒤 지운다. 접속 값은 출력하지 않는다(객체 이름·건수만).
/// 처리자는 Oranda.Language 의 internal 형식이라 이름으로 만든다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoExternalMetaToolsIt(ITestOutputHelper output)
{
    private static readonly string Table = TiberoItSessions.ObjectName("AC_EXT_T");
    private static readonly string Package = TiberoItSessions.ObjectName("AC_EXT_PK");

    [TiberoWriteFact]
    public async Task Metadata_tools_read_table_and_package_on_tibero()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        await DropAsync(session);
        try
        {
            await ExecAsync(session, $"CREATE TABLE {Table} (ID NUMBER PRIMARY KEY, NM VARCHAR2(20))");
            var hasPackage = await CreateOrNoPrivilegeAsync(session, $"""
                CREATE OR REPLACE PACKAGE {Package} AS
                  PROCEDURE add_one(p_id IN NUMBER, p_nm IN VARCHAR2 DEFAULT NULL);
                  FUNCTION total(p_id IN NUMBER) RETURN NUMBER;
                END {Package};
                """);
            if (hasPackage)
            {
                await ExecAsync(session, $"""
                    CREATE OR REPLACE PACKAGE BODY {Package} AS
                      PROCEDURE add_one(p_id IN NUMBER, p_nm IN VARCHAR2 DEFAULT NULL) IS
                      BEGIN
                        NULL;
                      END add_one;
                      FUNCTION total(p_id IN NUMBER) RETURN NUMBER IS
                      BEGIN
                        RETURN p_id;
                      END total;
                    END {Package};
                    """);
            }

            output.WriteLine($"[external-meta] package={hasPackage}");
            var monitor = await it.OpenAsync(SessionPurpose.Monitor);
            var metadata = new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync);
            var owner = it.Env.User.ToUpperInvariant();

            var found = Ok(await Run("FindObjectsHandler", metadata, monitor, it, new JsonObject { ["name_like"] = "ORT_AC_EXT_%", ["owner"] = owner }));
            Assert.Contains(found["objects"]!.AsArray(), o => o!["name"]!.GetValue<string>() == Table && o["type"]!.GetValue<string>() == "TABLE");

            var ddl = Ok(await Run("GetDdlHandler", metadata, monitor, it, new JsonObject { ["object"] = Table }));
            Assert.Contains(Table, ddl["sql_text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

            var names = Ok(await Run("ResolveNamesHandler", metadata, monitor, it, new JsonObject { ["names"] = new JsonArray(Table.ToLowerInvariant(), "ORT_AC_NONE_X") }));
            Assert.True(names["names"]![0]!["found"]!.GetValue<bool>());
            Assert.False(names["names"]![1]!["found"]!.GetValue<bool>());

            if (hasPackage)
            {
                var source = Ok(await Run("GetSourceHandler", metadata, monitor, it, new JsonObject { ["object"] = Package, ["member"] = "total" }));
                Assert.Contains("RETURN p_id;", source["sql_text"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.DoesNotContain("add_one", source["sql_text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

                var args = Ok(await Run("GetProcedureArgsHandler", metadata, monitor, it, new JsonObject { ["object"] = Package }));
                var members = args["members"]!.AsArray();
                output.WriteLine("[external-meta] members=" + string.Join(",", members.Select(m => m!["member"]!.GetValue<string>())));
                var add = Assert.Single(members, m => m!["member"]!.GetValue<string>() == "ADD_ONE")!;
                Assert.Equal(["P_ID", "P_NM"], add["args"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
                Assert.Equal("NUMBER", Assert.Single(members, m => m!["member"]!.GetValue<string>() == "TOTAL")!["returns"]!.GetValue<string>());
            }
        }
        finally
        {
            await DropAsync(session);
        }
    }

    private static JsonNode Ok(AgentToolOutput o)
    {
        Assert.False(o.IsError, o.ErrorCode + ": " + o.ErrorMessage);
        return o.Result!;
    }

    private static async Task<AgentToolOutput> Run(string handler, IMetadataProvider metadata, ISession db, TiberoItSessions it, JsonObject args)
    {
        var type = Type.GetType("Oranda.Language.Ai.Handlers.External." + handler + ", Oranda.Language", throwOnError: true)!;
        var tool = (IAgentToolHandler)Activator.CreateInstance(type, metadata)!;
        var run = new AgentRunScope("g1:it", it.Profile(), new AiContext("it", ConnectionEnvironment.Development, AiAccessLevel.Metadata));
        return await tool.ExecuteAsync(
            new AgentToolInvocation(new AgentSession(run), db, AiAccessLevel.Metadata, args, new Dictionary<string, AgentObjectRef>()), CancellationToken.None);
    }

    private static async Task DropAsync(TiberoSession session)
    {
        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{Package}' AND object_type = 'PACKAGE'") > 0)
        {
            await ExecAsync(session, $"DROP PACKAGE {Package}");
        }

        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'") > 0)
        {
            await ExecAsync(session, $"DROP TABLE {Table}");
        }
    }

    private static async Task<bool> CreateOrNoPrivilegeAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("17004", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static async Task<long> CountAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0;

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
