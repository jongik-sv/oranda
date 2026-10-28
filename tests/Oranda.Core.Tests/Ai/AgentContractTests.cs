using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Tests.Ai;

public class AgentContractTests
{
    [Fact]
    public void Nine_tools_with_valid_schemas_and_expected_minimum_levels()
    {
        Assert.Equal(9, AgentTools.All.Count);
        Assert.Equal(AgentTools.All.Count, AgentTools.All.Select(t => t.Name).Distinct().Count());

        foreach (var tool in AgentTools.All)
        {
            var schema = JsonNode.Parse(tool.InputSchemaJson)!.AsObject();
            Assert.Equal("object", (string?)schema["type"]);
            Assert.NotNull(AgentToolInfo.From(tool).InputSchema);
        }

        Assert.Equal(AiAccessLevel.Metadata, AgentTools.Find(AgentToolNames.DescribeObject)!.MinimumLevel);
        Assert.Equal(AiAccessLevel.Plan, AgentTools.Find(AgentToolNames.GetHistogram)!.MinimumLevel);
        Assert.True(AgentTools.Find(AgentToolNames.ExplainPlan)!.RequiresExplainPlanSetting);

        var bench = AgentTools.Find(AgentToolNames.BenchmarkQueries)!;
        Assert.Equal(AiAccessLevel.Query, bench.MinimumLevel);
        Assert.True(bench.DevTestOnly);
        Assert.Null(AgentTools.Find("drop_everything"));
    }

    [Fact]
    public void Object_arguments_use_the_shared_field_names_the_gate_scans()
    {
        string[] Required(string tool) =>
            [.. JsonNode.Parse(AgentTools.Find(tool)!.InputSchemaJson)!["required"]!.AsArray().Select(n => (string)n!)];

        Assert.Equal([AgentFields.ObjectName], Required(AgentToolNames.DescribeObject));
        Assert.Equal([AgentFields.Table], Required(AgentToolNames.GetIndexInfo));
        Assert.Equal([AgentFields.Table], Required(AgentToolNames.GetTableStats));
        Assert.Equal([AgentFields.Table, AgentFields.Column], Required(AgentToolNames.GetHistogram));
        Assert.Equal([AgentFields.SqlId], Required(AgentToolNames.GetCursorPlan));
        Assert.Equal([AgentFields.Sql], Required(AgentToolNames.ExplainPlan));
        Assert.Equal([AgentFields.Variants], Required(AgentToolNames.BenchmarkQueries));
    }

    [Fact]
    public void Tuning_result_schema_is_json_and_a_sample_round_trips()
    {
        Assert.Equal("object", Assert.IsType<string>(JsonNode.Parse(TuningResult.SchemaJson)!["type"]!.GetValue<string>()));
        Assert.Equal(JsonValueKind.Object, TuningResult.Schema().ValueKind);

        using var doc = JsonDocument.Parse("""
            { "summary": "full scan", "rewrites": [ { "label": "A", "sql": "select 1 from dual", "rationale": "r" } ],
              "index_suggestions": [ { "table": "APP.TB_ORDER", "columns": ["STAT_CD"], "ddl": "create index i on t(c)", "rationale": "r", "risk": "w" } ],
              "evidence": [ { "tool": "get_cursor_plan", "finding": "A-Rows 2.1M" } ] }
            """);
        var result = TuningResult.Parse(doc.RootElement);
        Assert.Equal("full scan", result.Summary);
        Assert.Equal("select 1 from dual", Assert.Single(result.Rewrites).Sql);
        Assert.Equal(["STAT_CD"], Assert.Single(result.IndexSuggestions).Columns.ToArray());
    }

    [Fact]
    public void Pipe_names_are_stable_per_user_and_hide_the_sid()
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        var name = AgentPipeName.Windows(sid);
        Assert.Matches("^oranda-agent-[0-9a-f]{16}$", name);
        Assert.DoesNotContain("1001", name, StringComparison.Ordinal);
        Assert.Equal(name, AgentPipeName.Windows(" " + sid.ToLowerInvariant() + " "));
        Assert.NotEqual(name, AgentPipeName.Windows("S-1-5-21-111-222-333-1002"));
        Assert.Equal(name + "-4242", AgentPipeName.Windows(sid, 4242));

        Assert.Equal(Path.Combine("/u/Oranda", "run", "agent.sock"), AgentPipeName.Unix("/u/Oranda"));
        Assert.Equal(Path.Combine("/u/Oranda", "run", "agent-7.sock"), AgentPipeName.Unix("/u/Oranda", 7));
    }

    [Fact]
    public void Run_tokens_are_unique_urlsafe_and_32_bytes()
    {
        var a = AgentRunToken.Generate();
        Assert.NotEqual(a, AgentRunToken.Generate());
        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    [Fact]
    public async Task Messages_travel_as_single_compact_lines_and_pair_by_id()
    {
        var wire = new MemoryStream();
        using var writer = new AgentRpcChannel(wire);
        var hello = new AgentHelloParams(AgentRpc.ProtocolVersion, "tok", new AgentClientInfo("claude-code", "2.1"));
        await writer.WriteAsync(
            AgentRpc.Request(1, AgentRpc.Methods.Hello, AgentRpc.ToNode(hello, AgentRpcJsonContext.Default.AgentHelloParams)), default);
        await writer.WriteAsync(AgentRpc.Success(1, AgentRpc.ToNode(new AgentHelloResult("run-1", "0.1.0"), AgentRpcJsonContext.Default.AgentHelloResult)), default);
        await writer.WriteAsync(AgentRpc.Failure(2, AgentRpc.ErrorCodes.TokenInvalid, "한글 사유"), default);

        var text = Encoding.UTF8.GetString(wire.ToArray());
        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", text, StringComparison.Ordinal);

        wire.Position = 0;
        using var reader = new AgentRpcChannel(wire);
        var req = AgentRpc.TryParse((await reader.ReadLineAsync(default))!)!;
        Assert.True(req.IsRequest);
        Assert.Equal(AgentRpc.Methods.Hello, req.Method);
        Assert.Equal("tok", req.Params!.Deserialize(AgentRpcJsonContext.Default.AgentHelloParams)!.RunToken);
        var ok = AgentRpc.TryParse((await reader.ReadLineAsync(default))!)!;
        Assert.Equal(1, ok.Id);
        Assert.Equal("run-1", ok.Result!.Deserialize(AgentRpcJsonContext.Default.AgentHelloResult)!.RunId);
        var fail = AgentRpc.TryParse((await reader.ReadLineAsync(default))!)!;
        Assert.Equal(AgentRpc.ErrorCodes.TokenInvalid, fail.Error!.Code);
        Assert.Equal("한글 사유", fail.Error.Message);
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task Reader_skips_blank_lines_rejects_oversized_lines_and_bad_json_parses_to_null()
    {
        using var small = new AgentRpcChannel(new MemoryStream(Encoding.UTF8.GetBytes("\r\n\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\"}\r\nnot json\n")));
        Assert.Equal("x", AgentRpc.TryParse((await small.ReadLineAsync(default))!)!.Method);
        Assert.Null(AgentRpc.TryParse((await small.ReadLineAsync(default))!));

        using var big = new AgentRpcChannel(new MemoryStream(new byte[AgentRpc.MaxMessageBytes + 10]));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await big.ReadLineAsync(default));

        Assert.Null(AgentRpc.TryParse("""{"jsonrpc":"1.0","id":1,"method":"x"}"""));
    }

    [Fact]
    public void Session_level_is_recomputed_from_the_current_profile_and_never_exceeds_it()
    {
        var prod = new ConnectionProfile("p", "u", "h/s", Environment: ConnectionEnvironment.Production, AiAccess: AiAccessLevel.Plan);
        var wide = new AiContext("p", ConnectionEnvironment.Production, AiAccessLevel.QueryRows);
        Assert.Equal(AiAccessLevel.Plan, new AgentSession(new AgentRunScope("r", prod, wide)).Level);

        var narrow = new AiContext("p", ConnectionEnvironment.Production, AiAccessLevel.Metadata);
        Assert.Equal(AiAccessLevel.Metadata, new AgentSession(new AgentRunScope("r", prod, narrow)).Level);

        var bogus = new AiContext("p", ConnectionEnvironment.Production, (AiAccessLevel)42);
        Assert.Equal(AiAccessLevel.Off, new AgentSession(new AgentRunScope("r", prod, bogus)).Level);
    }

    [Fact]
    public void Session_tracks_revocation_sequence_and_known_sql_ids()
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var session = new AgentSession(new AgentRunScope("r", profile, new AiContext("p", null, AiAccessLevel.Plan, SqlId: "abc123")));
        Assert.True(session.IsKnownSqlId("ABC123"));
        Assert.False(session.IsKnownSqlId("zzz"));
        session.AddSqlId("zzz");
        Assert.True(session.IsKnownSqlId("zzz"));
        Assert.Equal(1, session.NextSequence());
        Assert.Equal(2, session.NextSequence());
        Assert.False(session.IsRevoked);
        session.Revoke();
        Assert.True(session.IsRevoked);
    }

    [Fact]
    public void Send_scope_matches_wildcards_case_insensitively()
    {
        var scope = new AgentSendScope(false, ["APP.TB_*", "ref.code_?", " "]);
        Assert.True(scope.Matches("app", "tb_order"));
        Assert.True(scope.Matches("REF", "CODE_1"));
        Assert.False(scope.Matches("REF", "CODE_12"));
        Assert.False(scope.Matches("HR", "SALARY"));
        Assert.False(AgentSendScope.StatementOnly.Matches("APP", "TB_ORDER"));
        Assert.True(new AgentSendScope(true, []).Matches("HR", "SALARY"));
    }

    [Fact]
    public void Row_value_fields_cover_every_name_a_handler_could_leak_values_under()
    {
        Assert.Contains("endpoint", AgentFields.RowValueFields);
        Assert.Contains("endpoint_value", AgentFields.RowValueFields);
        Assert.Contains("low_value", AgentFields.RowValueFields);
        Assert.Contains("high_value", AgentFields.RowValueFields);
    }

    [Fact]
    public void Token_store_issues_resolves_and_revokes_whole_sessions_thread_safely()
    {
        var store = new AgentRunTokenStore();
        var profile = new ConnectionProfile("p", "u", "h/s");
        var session = new AgentSession(new AgentRunScope("r", profile, new AiContext("p", null, AiAccessLevel.Plan)));
        var other = new AgentSession(new AgentRunScope("r2", profile, new AiContext("p", null, AiAccessLevel.Plan)));

        var a = store.Issue(session);
        var b = store.Issue(session);
        var c = store.Issue(other);
        Assert.NotEqual(a, b);
        Assert.Same(session, store.Resolve(a));
        Assert.Null(store.Resolve("nope"));
        Assert.Null(store.Resolve(""));

        store.Revoke(a);
        Assert.True(session.IsRevoked);
        Assert.Null(store.Resolve(a));
        Assert.Null(store.Resolve(b));
        Assert.Same(other, store.Resolve(c));
        Assert.Throws<InvalidOperationException>(() => store.Issue(session));
        store.Revoke("unknown");

        var tokens = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 200, _ => tokens.Add(store.Issue(other)));
        Assert.Equal(200, tokens.Distinct().Count());
        Assert.Equal(201, store.Count);
    }
}
