using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Storage;

namespace Oranda.Core.Tests.Ai;

/// <summary>외부 연결 공유 계약(agent-cli A1): 새 도구 23개·오류 코드·연결 파일·데이터 폴더·허가 읽기 모델·처리자 도우미.</summary>
public class AppToolContractTests
{
    private static readonly string[] SchemaTypes = ["string", "integer", "boolean", "array", "object"];

    [Fact]
    public void Twenty_three_new_tools_with_unique_names_apart_from_the_existing_twelve()
    {
        Assert.Equal(23, AppTools.All.Count);
        Assert.Equal(35, AppTools.External.Count);
        Assert.Equal(35, AppTools.External.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(AppTools.All, t => Assert.Null(AgentTools.Find(t.Name))); // 앱 안 AI 실행에는 나오지 않는다
        Assert.Equal(12, AgentTools.All.Count);
        Assert.Same(AgentTools.All[0], AppTools.External[0]);
        Assert.NotNull(AppTools.Find(AgentToolNames.DescribeObject));
        Assert.NotNull(AppTools.Find(AppToolNames.ProposeEdit));
        Assert.Null(AppTools.Find("run_dml"));
        Assert.All(AppTools.All, t => Assert.Matches("^[a-z][a-z_]*$", t.Name));
    }

    [Fact]
    public void Every_schema_is_an_object_the_gate_can_read()
    {
        foreach (var tool in AppTools.External)
        {
            var schema = JsonNode.Parse(tool.InputSchemaJson)!.AsObject();
            Assert.Equal("object", (string?)schema["type"]);
            Assert.False((bool)schema["additionalProperties"]!, tool.Name);
            var properties = schema["properties"]!.AsObject();
            foreach (var (name, node) in properties)
            {
                Assert.Matches("^[a-z][a-z_]*$", name);
                var type = (string?)node!["type"];
                Assert.True(type is not null && SchemaTypes.Contains(type), $"{tool.Name}.{name} needs a plain type (the gate does not read oneOf)");
                if (node["enum"] is JsonArray values && node["default"] is { } fallback)
                {
                    Assert.Contains((string?)fallback, values.Select(v => (string?)v));
                }

                if (type == "integer" && node["default"] is { } d)
                {
                    var value = (long)d;
                    Assert.True(node["minimum"] is null || value >= (long)node["minimum"]!, $"{tool.Name}.{name}");
                    Assert.True(node["maximum"] is null || value <= (long)node["maximum"]!, $"{tool.Name}.{name}");
                }
            }

            foreach (var required in schema["required"]?.AsArray() ?? [])
            {
                Assert.True(properties.ContainsKey((string)required!), $"{tool.Name}: required {required} is not declared");
            }

            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.NotNull(AgentToolInfo.From(tool).InputSchema);
        }
    }

    [Theory]
    [InlineData(AppToolNames.GetStatus, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.ListConnections, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.ListTabs, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.GetTab, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.NewTab, AgentToolHost.App, AgentToolSafety.New, AiAccessLevel.Off)]
    [InlineData(AppToolNames.ProposeEdit, AgentToolHost.App, AgentToolSafety.Propose, AiAccessLevel.Off)]
    [InlineData(AppToolNames.GetProposal, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.FocusTab, AgentToolHost.App, AgentToolSafety.Ui, AiAccessLevel.Off)]
    [InlineData(AppToolNames.SaveTab, AgentToolHost.App, AgentToolSafety.Confirm, AiAccessLevel.Off)]
    [InlineData(AppToolNames.GetBinds, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.RevealObject, AgentToolHost.App, AgentToolSafety.Ui, AiAccessLevel.Metadata)]
    [InlineData(AppToolNames.CheckSql, AgentToolHost.Lang, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.FormatSql, AgentToolHost.Lang, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.FindObjects, AgentToolHost.Db, AgentToolSafety.Read, AiAccessLevel.Metadata)]
    [InlineData(AppToolNames.ListSchemas, AgentToolHost.Db, AgentToolSafety.Read, AiAccessLevel.Metadata)]
    [InlineData(AppToolNames.GetDdl, AgentToolHost.Db, AgentToolSafety.Read, AiAccessLevel.Metadata)]
    [InlineData(AppToolNames.GetSource, AgentToolHost.Db, AgentToolSafety.Read, AiAccessLevel.Metadata)]
    [InlineData(AppToolNames.ResolveNames, AgentToolHost.Db, AgentToolSafety.Read, AiAccessLevel.Metadata)]
    [InlineData(AppToolNames.GetProcedureArgs, AgentToolHost.Db, AgentToolSafety.Read, AiAccessLevel.Metadata)]
    [InlineData(AppToolNames.Query, AgentToolHost.Db, AgentToolSafety.Execute, AiAccessLevel.Query)]
    [InlineData(AppToolNames.GetResult, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.GetLastError, AgentToolHost.App, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.GetHistory, AgentToolHost.Lang, AgentToolSafety.Read, AiAccessLevel.Metadata)]
    public void New_tool_host_safety_and_level_follow_the_design(string name, AgentToolHost host, AgentToolSafety safety, AiAccessLevel level)
    {
        var tool = AppTools.Find(name)!;
        Assert.Equal(host, tool.Host);
        Assert.Equal(safety, tool.Safety);
        Assert.Equal(level, tool.MinimumLevel);
    }

    [Fact]
    public void Existing_tools_stay_db_tools_and_describe_object_takes_parts()
    {
        Assert.All(AgentTools.All, t => Assert.Equal(AgentToolHost.Db, t.Host));
        Assert.Equal(AgentToolSafety.Execute, AgentTools.Find(AgentToolNames.ExplainPlan)!.Safety);
        Assert.Equal(AgentToolSafety.Execute, AgentTools.Find(AgentToolNames.BenchmarkQueries)!.Safety);
        Assert.Equal(AgentToolSafety.Read, AgentTools.Find(AgentToolNames.GetCursorPlan)!.Safety);

        var parts = JsonNode.Parse(AgentTools.Find(AgentToolNames.DescribeObject)!.InputSchemaJson)!["properties"]![AgentFields.Parts]!;
        Assert.Equal("array", (string?)parts["type"]);
        Assert.Equal(["columns", "keys", "indexes"], parts["items"]!["enum"]!.AsArray().Select(n => (string)n!));
        Assert.Equal([AgentFields.ObjectName], JsonNode.Parse(AgentTools.Find(AgentToolNames.DescribeObject)!.InputSchemaJson)!["required"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Row_values_table_is_masked_but_the_benchmark_row_count_is_not()
    {
        Assert.Contains(AgentFields.RowValues, AgentFields.RowValueFields);
        Assert.DoesNotContain("rows", AgentFields.RowValueFields); // benchmark_queries 의 rows 는 행 수(숫자)
        Assert.Contains("name", AgentFields.ColumnIdentityFields); // query 의 columns[].name 이 열 이름 칸
        Assert.Contains("row_values", AppTools.Find(AppToolNames.Query)!.Description, StringComparison.Ordinal);
        Assert.Contains("row_values", AppTools.Find(AppToolNames.GetResult)!.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("tool_failed", 1)]
    [InlineData("tab_not_found", 1)]
    [InlineData("busy", 1)]
    [InlineData("something_new", 1)]
    [InlineData("usage", 2)]
    [InlineData("invalid_arguments", 2)]
    [InlineData("unknown_tool", 2)]
    [InlineData("oranda_not_running", 3)]
    [InlineData("no_endpoint", 3)]
    [InlineData("grant_expired", 3)]
    [InlineData("run_revoked", 3)]
    [InlineData("oranda_run_invalid", 3)]
    [InlineData("level", 4)]
    [InlineData("connection_required", 4)]
    [InlineData("locked", 4)]
    [InlineData("read_only", 4)]
    [InlineData("rate_limited", 4)]
    [InlineData("multiple_instances", 5)]
    [InlineData("instance_not_found", 5)]
    public void Error_codes_map_to_cli_exit_codes(string? code, int exit) => Assert.Equal(exit, AgentCliExit.For(code));

    [Fact]
    public void Error_code_list_is_unique_and_covers_the_gate_and_bridge_codes()
    {
        Assert.Equal(AgentErrorCodes.All.Count, AgentErrorCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(AgentErrorCodes.All, c => Assert.Matches("^[a-z][a-z_]*$", c));

        // 게이트(AgentGate)와 다리(AgentBridgeHost)가 지금 내는 코드가 모두 목록에 있다.
        string[] existing =
        [
            "connection", "environment", "explain_plan_disabled", "invalid_arguments", "level", "literal_mask_unavailable", "no_statement",
            "run_revoked", "scope", "session_mismatch", "sql_id_scope", "sql_not_single_query", "sql_schema_unresolved", "sql_unsafe",
            "unknown_tool", "unsafe_result", "canceled", "internal_error", "not_implemented", "tool_failed", "tool_exception", "busy", "result_too_large",
        ];
        Assert.All(existing, c => Assert.Contains(c, AgentErrorCodes.All));

        Assert.Equal(AgentErrorCodes.RunInvalid, AgentRpc.ErrorCodes.ToErrorCode(AgentRpc.ErrorCodes.TokenInvalid));
        Assert.Equal(AgentErrorCodes.GrantExpired, AgentRpc.ErrorCodes.ToErrorCode(AgentRpc.ErrorCodes.GrantExpired));
        Assert.Equal(AgentErrorCodes.BridgeError, AgentRpc.ErrorCodes.ToErrorCode(-1));
        Assert.Equal(AgentCliExit.Unreachable, AgentCliExit.For(AgentRpc.ErrorCodes.ToErrorCode(AgentRpc.ErrorCodes.ShuttingDown)));
    }

    // ---- 연결 파일 ----

    private static AgentEndpointFile SampleFile(int v = AgentEndpointFile.CurrentVersion, string token = "q2V8tokenvalue") => new(
        v,
        23140,
        new DateTimeOffset(2026, 11, 22, 9, 12, 3, TimeSpan.FromHours(9)),
        "0.1.32",
        "기본 작업 공간",
        new AgentEndpoint(AgentEndpoint.PipeKind, "oranda-agent-3f9c0a1b2c3d4e5f"),
        token,
        new DateTimeOffset(2026, 11, 22, 17, 12, 3, TimeSpan.FromHours(9)),
        [
            new AgentEndpointConnection("dev-erp", ConnectionEnvironment.Development, AiAccessLevel.QueryRows),
            new AgentEndpointConnection("prd-erp", ConnectionEnvironment.Production, AiAccessLevel.Metadata),
        ]);

    [Fact]
    public void Endpoint_file_round_trips_in_the_documented_shape()
    {
        var file = SampleFile();
        var json = file.Serialize();
        var node = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(
            ["v", "pid", "started_at", "app_version", "label", "endpoint", "token", "expires_at", "connections"],
            node.Select(p => p.Key));
        Assert.Equal("pipe", (string?)node["endpoint"]!["kind"]);
        Assert.Equal("QueryRows", (string?)node["connections"]![0]!["level"]);
        Assert.Equal("Production", (string?)node["connections"]![1]!["environment"]);
        Assert.Contains("기본 작업 공간", json, StringComparison.Ordinal); // 한글을 \u 로 바꾸지 않아도 읽힌다

        var back = AgentEndpointFile.TryParse(json)!;
        Assert.Equal(file.Pid, back.Pid);
        Assert.Equal(file.Token, back.Token);
        Assert.Equal(file.Endpoint, back.Endpoint);
        Assert.Equal(file.ExpiresAt, back.ExpiresAt);
        Assert.Equal(file.Connections, back.Connections);
        Assert.True(back.IsExpired(file.ExpiresAt!.Value));
        Assert.False(back.IsExpired(file.StartedAt));
        Assert.DoesNotContain(file.Token, file.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"v":2,"pid":1,"endpoint":{"kind":"pipe","name":"p"},"token":"t"}""")]
    [InlineData("""{"v":1,"pid":0,"endpoint":{"kind":"pipe","name":"p"},"token":"t"}""")]
    [InlineData("""{"v":1,"pid":1,"endpoint":{"kind":"tcp","name":"p"},"token":"t"}""")]
    [InlineData("""{"v":1,"pid":1,"endpoint":{"kind":"unix","name":""},"token":"t"}""")]
    [InlineData("""{"v":1,"pid":1,"endpoint":{"kind":"pipe","name":"p"}}""")]
    [InlineData("""{"v":1,"pid":1,"token":"t"}""")]
    public void Broken_or_foreign_endpoint_files_are_skipped(string json) => Assert.Null(AgentEndpointFile.TryParse(json));

    [Fact]
    public void Minimal_endpoint_file_reads_with_empty_defaults()
    {
        var file = AgentEndpointFile.TryParse("""{"v":1,"pid":7,"started_at":"2026-11-22T00:00:00Z","endpoint":{"kind":"unix","name":"/u/Oranda/run/agent.sock"},"token":"t"}""")!;
        Assert.Equal(7, file.Pid);
        Assert.Equal(AgentEndpoint.UnixKind, file.Endpoint.Kind);
        Assert.Empty(file.Connections);
        Assert.Equal(string.Empty, file.Label);
        Assert.Null(file.ExpiresAt);
        Assert.False(file.IsExpired(DateTimeOffset.MaxValue));
    }

    // ---- 데이터 폴더 ----

    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Data_directory_follows_the_os_rules_and_the_override()
    {
        Assert.Equal("/x/data", AppDataLocation.Resolve(Env(("ORANDA_DATA_DIR", "/x/data"), ("APPDATA", "/a")), AppOs.Windows));
        Assert.Equal(Path.Combine("/a", "Oranda"), AppDataLocation.Resolve(Env(("APPDATA", "/a")), AppOs.Windows));
        Assert.Equal(Path.Combine("/h", "Library", "Application Support", "Oranda"), AppDataLocation.Resolve(Env(("HOME", "/h")), AppOs.MacOS));
        Assert.Equal(Path.Combine("/xdg", "Oranda"), AppDataLocation.Resolve(Env(("XDG_DATA_HOME", "/xdg"), ("HOME", "/h")), AppOs.Linux));
        Assert.Equal(Path.Combine("/h", ".local", "share", "Oranda"), AppDataLocation.Resolve(Env(("HOME", "/h")), AppOs.Linux));
        Assert.Equal("Oranda", Path.GetFileName(AppDataLocation.Resolve(Env(), AppOs.Windows))); // 환경 변수가 없으면 특수 폴더
    }

    [Fact]
    public void Endpoint_paths_live_under_run_agents()
    {
        Assert.Equal(Path.Combine("/d", "run"), AppDataLocation.RunDirectory("/d"));
        Assert.Equal(Path.Combine("/d", "run", "agents"), AppDataLocation.AgentsDirectory("/d"));
        Assert.Equal(Path.Combine("/d", "run", "agents", "23140.json"), AppDataLocation.EndpointFile("/d", 23140));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppDataLocation.EndpointFile("/d", 0));
        Assert.Equal(23140, AppDataLocation.PidFromEndpointFileName("23140.json"));
        Assert.Null(AppDataLocation.PidFromEndpointFileName("23140.json.tmp"));
        Assert.Null(AppDataLocation.PidFromEndpointFileName("-1.json"));
        Assert.Null(AppDataLocation.PidFromEndpointFileName("abc.json"));
    }

    // ---- 허가 읽기 모델 ----

    private static readonly Guid DevId = Guid.NewGuid();
    private static readonly Guid PrdId = Guid.NewGuid();

    private static AgentGrantView Grant(params AgentGrantConnection[] connections) =>
        new("g-1", connections, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(8), ["claude-code"]);

    private static AgentGrantConnection Dev => new(DevId, "dev-erp", ConnectionEnvironment.Development, Dbms.Oracle, AiAccessLevel.QueryRows);

    private static AgentGrantConnection Prd => new(PrdId, "prd-erp", ConnectionEnvironment.Production, Dbms.Tibero, AiAccessLevel.Metadata);

    [Fact]
    public void Connection_argument_resolves_by_alias_or_the_only_connection()
    {
        var two = Grant(Dev, Prd);
        Assert.Equal(PrdId, two.ResolveConnection("PRD-ERP", out var code)!.ProfileId);
        Assert.Null(code);
        Assert.Null(two.ResolveConnection(null, out code));
        Assert.Equal(AgentErrorCodes.ConnectionRequired, code);
        Assert.Null(two.ResolveConnection("other", out code));
        Assert.Equal(AgentErrorCodes.Connection, code);

        Assert.Equal(DevId, Grant(Dev).ResolveConnection(" ", out code)!.ProfileId);
        Assert.Null(code);
        Assert.Null(Grant().ResolveConnection(null, out code));
        Assert.Equal(AgentErrorCodes.ConnectionRequired, code);
    }

    [Fact]
    public void Tab_text_is_readable_only_for_allowed_non_production_connections()
    {
        var grant = Grant(Dev, Prd);
        Assert.True(grant.CanReadTabText(DevId));
        Assert.False(grant.CanReadTabText(PrdId)); // 운영 탭 글은 「메타만」 연결이어도 주지 않는다
        Assert.False(grant.CanReadTabText(Guid.NewGuid()));
        Assert.True(grant.CanReadTabText(null));
        Assert.False((grant with { AllowUnboundTabText = false }).CanReadTabText(null));
        Assert.False(Grant(Dev with { Level = AiAccessLevel.Off }).CanReadTabText(DevId));

        Assert.True(grant.IsLocked(Guid.NewGuid()));
        Assert.False(grant.IsLocked(null));
        Assert.False(grant.IsLocked(DevId));
        Assert.Equal(AiAccessLevel.QueryRows, grant.LevelFor(DevId));
        Assert.Equal(AiAccessLevel.Off, grant.LevelFor(null));
        Assert.True(grant.IsExpired(DateTimeOffset.UnixEpoch.AddHours(8)));
        Assert.False((grant with { ExpiresAt = null }).IsExpired(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void External_level_is_the_smaller_of_connection_level_and_cap()
    {
        var dev = new ConnectionProfile("dev-erp", "u", "h/s", Id: DevId);
        var devRows = dev with { AiAccess = AiAccessLevel.QueryRows };
        var prd = new ConnectionProfile("prd-erp", "u", "h/s", Id: PrdId, Environment: ConnectionEnvironment.Production);

        Assert.Equal(AiAccessLevel.Plan, AgentGrantPolicy.LevelFor(dev, AgentGrantPolicy.DefaultCap(dev.Environment))); // 개발 기본 Plan
        Assert.Equal(AiAccessLevel.QueryRows, AgentGrantPolicy.LevelFor(devRows, AiAccessLevel.QueryRows));
        Assert.Equal(AiAccessLevel.Metadata, AgentGrantPolicy.LevelFor(prd, AiAccessLevel.QueryRows)); // 운영은 상한을 넘겨도 Metadata
        Assert.Equal(AiAccessLevel.Off, AgentGrantPolicy.LevelFor(prd with { AiAccess = AiAccessLevel.Off }, AiAccessLevel.Metadata));
        Assert.Equal(AiAccessLevel.Metadata, AgentGrantPolicy.LevelFor(devRows, AiAccessLevel.Metadata));
        Assert.True(AgentGrantPolicy.CheckedByDefault(ConnectionEnvironment.Development));
        Assert.False(AgentGrantPolicy.CheckedByDefault(ConnectionEnvironment.Test));
        Assert.Equal(TimeSpan.FromHours(8), AgentGrantPolicy.DefaultLifetime);

        var connection = AgentGrantPolicy.ToConnection(new AgentGrantTarget(prd with { Dbms = Dbms.Tibero, ReadOnly = true }, AiAccessLevel.Metadata));
        Assert.Equal(new AgentGrantConnection(PrdId, "prd-erp", ConnectionEnvironment.Production, Dbms.Tibero, AiAccessLevel.Metadata, true, true), connection);
    }

    [Fact]
    public void Grant_issue_hides_the_token_when_printed()
    {
        var issue = new AgentGrantIssue(Grant(Dev), "secret-token-value");
        Assert.DoesNotContain("secret-token-value", issue.ToString(), StringComparison.Ordinal);
        Assert.Contains("g-1", issue.ToString(), StringComparison.Ordinal);
    }

    // ---- 처리자 도우미 ----

    [Theory]
    [InlineData("document", "document", 0, 0)]
    [InlineData(" Statement ", "statement", 0, 0)]
    [InlineData("caret", "caret", 0, 0)]
    [InlineData("120:35", null, 120, 35)]
    [InlineData("0:0", null, 0, 0)]
    public void Range_argument_reads_keywords_and_start_length(string text, string? keyword, int start, int length)
    {
        Assert.True(AppTextRange.TryParse(text, out var range));
        Assert.Equal(new AppTextRange(keyword, start, length), range);
    }

    [Theory]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("-1:3")]
    [InlineData("1:")]
    [InlineData(":3")]
    [InlineData("1:2:3")]
    public void Bad_ranges_are_refused(string text) => Assert.False(AppTextRange.TryParse(text, out _));

    [Fact]
    public void Argument_helpers_apply_defaults_and_refuse_wrong_values()
    {
        var args = JsonNode.Parse("""{"tab":"t1","max_chars":500,"open":true,"names":["A","B"],"wait_s":601,"bad":5,"range":"statement"}""")!.AsObject();
        Assert.Equal("t1", AppToolArgs.Text(args, AppFields.Tab));
        Assert.Null(AppToolArgs.Text(args, AppFields.Title));
        Assert.Equal(500, AppToolArgs.Number(args, AppFields.MaxChars, 20000, 1, 50000));
        Assert.Equal(20000, AppToolArgs.Number(args, "missing", 20000, 1, 50000));
        Assert.True(AppToolArgs.Flag(args, AppFields.Open, false));
        Assert.Equal(["A", "B"], AppToolArgs.Texts(args, AppFields.Names));
        Assert.Empty(AppToolArgs.Texts(args, AppFields.Types));
        Assert.Equal("statement", AppToolArgs.Range(args, AppFields.Range, "document").Keyword);
        Assert.Equal("document", AppToolArgs.Range([], AppFields.Range, "document").Keyword);

        Assert.Throws<AppToolArgumentException>(() => AppToolArgs.Number(args, AppFields.WaitSeconds, 120, 0, 600));
        Assert.Throws<AppToolArgumentException>(() => AppToolArgs.Text(args, "bad"));
        Assert.Throws<AppToolArgumentException>(() => AppToolArgs.RequiredText(args, AppFields.NewText));
    }

    [Fact]
    public async Task Guard_turns_argument_errors_into_invalid_arguments_and_keeps_other_results()
    {
        var bad = await AppToolOutputs.Guard(() => ValueTask.FromResult(AppToolOutputs.Deny("x", AppToolArgs.RequiredText([], AppFields.Tab))));
        Assert.True(bad.IsError);
        Assert.Equal(AgentErrorCodes.InvalidArguments, bad.ErrorCode);
        Assert.True(bad.AppWritten);
        Assert.Contains("'tab'", bad.ErrorMessage, StringComparison.Ordinal);

        var ok = await AppToolOutputs.Guard(() => ValueTask.FromResult(AgentToolOutput.Ok(new JsonObject { ["ok"] = true })));
        Assert.False(ok.IsError);
        Assert.Null(ok.ErrorCode);

        Assert.Equal(AgentErrorCodes.Locked, AppToolOutputs.Locked().ErrorCode);
        Assert.Equal(AgentErrorCodes.ReadOnly, AppToolOutputs.ReadOnly().ErrorCode);
        Assert.Equal(AgentErrorCodes.TabNotFound, AppToolOutputs.TabNotFound(null).ErrorCode);
        Assert.Equal(AgentCliExit.Denied, AgentCliExit.For(AppToolOutputs.Locked().ErrorCode));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AppToolOutputs.Guard(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void App_invocation_level_comes_from_the_resolved_connection()
    {
        var grant = Grant(Dev);
        Assert.Equal(AiAccessLevel.QueryRows, new AppToolInvocation(grant, "codex", [], Dev).Level);
        Assert.Equal(AiAccessLevel.Off, new AppToolInvocation(grant, "codex", []).Level);
    }
}
