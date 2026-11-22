using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Manual;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Testing;

namespace Oranda.Core.Tests.Ai;

/// <summary>
/// 외부 연결 2단계 공유 계약(agent-cli-1122 C0): 새 도구 5개·오류 코드 6개·허가의 로컬 모델 칸·운영 스위치와 정책 함수·매뉴얼 색인 껍데기.
/// 정책 시험은 스위치 값에 맞춰 기대를 정한다 — m-gate-prod 가 스위치를 켜도 이 시험은 그대로 통한다(<see cref="Switches_start_off_in_the_contract_lane"/> 만 바꾼다).
/// </summary>
public class AppToolContract2Tests
{
    // ---- 도구 5개 ----

    [Theory]
    [InlineData(AppToolNames.RunTab, AgentToolSafety.Execute, AiAccessLevel.Off)]
    [InlineData(AppToolNames.ExportResult, AgentToolSafety.New, AiAccessLevel.Off)]
    [InlineData(AppToolNames.TuneStart, AgentToolSafety.New, AiAccessLevel.Plan)]
    [InlineData(AppToolNames.ListCommands, AgentToolSafety.Read, AiAccessLevel.Off)]
    [InlineData(AppToolNames.RunCommand, AgentToolSafety.Ui, AiAccessLevel.Off)]
    public void Phase_two_tools_are_app_tools_with_the_planned_grade_and_level(string name, AgentToolSafety safety, AiAccessLevel level)
    {
        var tool = AppTools.Find(name)!;
        Assert.Equal(AgentToolHost.App, tool.Host);
        Assert.Equal(safety, tool.Safety);
        Assert.Equal(level, tool.MinimumLevel);
        Assert.False(tool.DevTestOnly); // 운영 판단은 처리자가 AgentGrantPolicy 로 한다(스위치 한 곳)
        Assert.Null(AgentTools.Find(name)); // 앱 안 AI 실행에는 나오지 않는다
    }

    [Fact]
    public void Phase_two_tools_follow_the_existing_ones_in_order()
    {
        string[] added = [AppToolNames.RunTab, AppToolNames.ExportResult, AppToolNames.TuneStart, AppToolNames.ListCommands, AppToolNames.RunCommand];
        Assert.Equal(added, AppTools.All.Skip(23).Select(t => t.Name));
        Assert.Equal(added, AppTools.External.TakeLast(5).Select(t => t.Name));
    }

    [Theory]
    [InlineData(AppToolNames.RunTab, new[] { "tab" }, new[] { "tab", "base_version", "range", "timeout_s" })]
    [InlineData(AppToolNames.ExportResult, new[] { "tab", "format", "out" }, new[] { "tab", "format", "out", "index", "sheet" })]
    [InlineData(AppToolNames.TuneStart, new[] { "tab" }, new[] { "tab", "range" })]
    [InlineData(AppToolNames.ListCommands, new string[0], new[] { "category", "grade" })]
    [InlineData(AppToolNames.RunCommand, new[] { "id" }, new[] { "id", "tab" })]
    public void Phase_two_schemas_declare_the_planned_arguments(string name, string[] required, string[] properties)
    {
        var schema = JsonNode.Parse(AppTools.Find(name)!.InputSchemaJson)!;
        Assert.Equal(properties, schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(required, schema["required"]?.AsArray().Select(r => (string)r!) ?? []);
    }

    [Fact]
    public void Enum_arguments_use_the_shared_values()
    {
        static string[] Enum(string tool, string field) =>
            [.. JsonNode.Parse(AppTools.Find(tool)!.InputSchemaJson)!["properties"]![field]!["enum"]!.AsArray().Select(n => (string)n!)];

        Assert.Equal(AppToolValues.ExportFormats.All, Enum(AppToolNames.ExportResult, AppFields.Format));
        Assert.Equal(AppToolValues.CommandGrades.All, Enum(AppToolNames.ListCommands, AppFields.Grade));
        Assert.Equal([AppToolValues.Scopes.Document, AppToolValues.Scopes.Statement], Enum(AppToolNames.RunTab, AppFields.Range));
        Assert.Equal([AppToolValues.Scopes.Statement], Enum(AppToolNames.TuneStart, AppFields.Range));

        var timeout = JsonNode.Parse(AppTools.Find(AppToolNames.RunTab)!.InputSchemaJson)!["properties"]![AppFields.TimeoutSeconds]!;
        Assert.Equal(AppToolValues.DefaultTimeoutSeconds, (int)timeout["default"]!);
        Assert.Equal(AppToolValues.MaxTimeoutSeconds, (int)timeout["maximum"]!);
    }

    [Fact]
    public void Status_and_field_values_are_the_documented_words()
    {
        Assert.Equal(["format", "out", "sheet", "id", "grade", "category"], [AppFields.Format, AppFields.Out, AppFields.Sheet, AppFields.Id, AppFields.Grade, AppFields.Category]);
        Assert.Equal(["succeeded", "canceled", "failed", "stale"], AppToolValues.RunStatus.All);
        Assert.Equal(["written", "canceled"], AppToolValues.ExportStatus.All);
        Assert.Equal(["xlsx", "csv"], AppToolValues.ExportFormats.All);
        Assert.Equal(["now", "confirm", "deny"], AppToolValues.CommandGrades.All);
        Assert.Equal(["done", "confirmed", "rejected", "timeout", "disabled"], AppToolValues.CommandStatus.All);
        Assert.Contains("row_values", AgentFields.RowValueFields); // export_result 는 행을 돌려주지 않는다 — rows 는 숫자 칸
        Assert.DoesNotContain("rows", AgentFields.RowValueFields);
    }

    [Fact]
    public void Handlers_hand_the_row_count_to_the_gate_apart_from_the_result()
    {
        var output = AppToolOutputs.OkWithRows(new JsonObject { ["rows"] = 1200 }, 1200);
        Assert.False(output.IsError);
        Assert.Equal(1200, output.RowCount);
        Assert.Equal("rows=1200", AgentToolOutput.RowsNote(output.RowCount!.Value));
        Assert.Null(AgentToolOutput.Ok(new JsonObject()).RowCount); // 행을 다루지 않는 도구
        Assert.Null(AppToolOutputs.Locked().RowCount);
    }

    // ---- 오류 코드 ----

    [Theory]
    [InlineData("file_exists", AgentCliExit.Denied)]
    [InlineData("path_invalid", AgentCliExit.Usage)]
    [InlineData("command_not_found", AgentCliExit.ToolFailed)]
    [InlineData("command_denied", AgentCliExit.Denied)]
    [InlineData("command_disabled", AgentCliExit.Denied)]
    [InlineData("tab_busy", AgentCliExit.ToolFailed)]
    public void Phase_two_error_codes_are_listed_and_map_to_exit_codes(string code, int exit)
    {
        Assert.Contains(code, AgentErrorCodes.All);
        Assert.Equal(exit, AgentCliExit.For(code));
    }

    [Fact]
    public void Phase_two_error_constants_spell_the_codes()
    {
        Assert.Equal(
            ["file_exists", "path_invalid", "command_not_found", "command_denied", "command_disabled", "tab_busy"],
            [AgentErrorCodes.FileExists, AgentErrorCodes.PathInvalid, AgentErrorCodes.CommandNotFound, AgentErrorCodes.CommandDenied, AgentErrorCodes.CommandDisabled, AgentErrorCodes.TabBusy]);
    }

    // ---- 허가의 로컬 모델 칸·운영 스위치 ----

    private static readonly Guid DevId = Guid.NewGuid();
    private static readonly Guid TstId = Guid.NewGuid();
    private static readonly Guid PrdId = Guid.NewGuid();

    private static AgentGrantConnection Dev(AiAccessLevel level = AiAccessLevel.QueryRows) => new(DevId, "dev", ConnectionEnvironment.Development, Dbms.Oracle, level);

    private static AgentGrantConnection Tst(AiAccessLevel level = AiAccessLevel.Query) => new(TstId, "tst", ConnectionEnvironment.Test, Dbms.Oracle, level);

    private static AgentGrantConnection Prd(AiAccessLevel level = AiAccessLevel.Metadata) => new(PrdId, "prd", ConnectionEnvironment.Production, Dbms.Tibero, level);

    private static AgentGrantView Grant(bool localModel, params AgentGrantConnection[] connections) =>
        new("g-1", connections, DateTimeOffset.UnixEpoch, null, []) { LocalModel = localModel };

    [Fact]
    public void Switches_start_off_in_the_contract_lane()
    {
        // C0 는 동작을 바꾸지 않는다. m-gate-prod(G3)가 두 값을 켜며 이 시험을 바꾼다.
        Assert.False(AgentGrantPolicy.ProductionRuns);
        Assert.False(AgentGrantPolicy.LocalModelCoversProduction);
    }

    [Fact]
    public void Local_model_is_off_unless_the_request_says_so()
    {
        Assert.False(new AgentGrantRequest([], null).LocalModel);
        Assert.True(new AgentGrantRequest([], null, LocalModel: true).LocalModel);
        Assert.False(new AgentGrantView("g", [], DateTimeOffset.UnixEpoch, null, []).LocalModel);
    }

    [Fact]
    public void Environment_cap_keeps_production_at_metadata_until_production_runs()
    {
        var production = AgentGrantPolicy.ProductionRuns ? AiAccessLevel.QueryRows : AiAccessLevel.Metadata;
        Assert.Equal(production, AgentGrantPolicy.EnvironmentCap(ConnectionEnvironment.Production));
        Assert.Equal(AiAccessLevel.QueryRows, AgentGrantPolicy.EnvironmentCap(ConnectionEnvironment.Development));
        Assert.Equal(AiAccessLevel.QueryRows, AgentGrantPolicy.EnvironmentCap(ConnectionEnvironment.Test));
        Assert.All(Enum.GetValues<ConnectionEnvironment>(), e => Assert.Equal(AgentGrantPolicy.EnvironmentCap(e), AgentGrantPolicy.DefaultCap(e)));
    }

    [Fact]
    public void Execution_needs_query_level_and_production_needs_the_switch()
    {
        Assert.True(AgentGrantPolicy.MayExecute(Dev(AiAccessLevel.Query)));
        Assert.True(AgentGrantPolicy.MayExecute(Tst(AiAccessLevel.QueryRows)));
        Assert.False(AgentGrantPolicy.MayExecute(Dev(AiAccessLevel.Plan)));
        Assert.False(AgentGrantPolicy.MayExecute(Dev(AiAccessLevel.Off)));
        Assert.Equal(AgentGrantPolicy.ProductionRuns, AgentGrantPolicy.MayExecute(Prd(AiAccessLevel.QueryRows)));
        Assert.False(AgentGrantPolicy.MayExecute(Prd(AiAccessLevel.Metadata)));
        Assert.True(AgentGrantPolicy.ExecutesIn(ConnectionEnvironment.Development));
        Assert.Equal(AgentGrantPolicy.ProductionRuns, AgentGrantPolicy.ExecutesIn(ConnectionEnvironment.Production));
        Assert.Throws<ArgumentNullException>(() => AgentGrantPolicy.MayExecute(null!));
    }

    [Fact]
    public void Cloud_model_masks_everywhere_and_literals_only_in_production()
    {
        var cloud = Grant(false, Dev(), Prd());
        foreach (var env in Enum.GetValues<ConnectionEnvironment>())
        {
            Assert.False(AgentGrantPolicy.UsesLocalModel(cloud, env));
            Assert.True(AgentGrantPolicy.MasksSensitive(cloud, env));
            Assert.Equal(env == ConnectionEnvironment.Production, AgentGrantPolicy.MasksLiterals(cloud, env));
        }

        Assert.False(AgentGrantPolicy.CanReadProductionTabText(cloud)); // E-18: 클라우드 모델은 운영 탭 글을 받지 않는다
        Assert.False(cloud.CanReadTabText(PrdId));
        Assert.True(cloud.MasksSensitive(DevId));
        Assert.True(cloud.MasksLiterals(PrdId));
        Assert.False(cloud.MasksLiterals(DevId));
    }

    [Fact]
    public void Local_model_shows_values_outside_production_and_in_production_only_with_the_switch()
    {
        var local = Grant(true, Dev(), Prd());
        Assert.True(AgentGrantPolicy.UsesLocalModel(local, ConnectionEnvironment.Development));
        Assert.True(AgentGrantPolicy.UsesLocalModel(local, ConnectionEnvironment.Test));
        Assert.False(AgentGrantPolicy.MasksSensitive(local, ConnectionEnvironment.Development));
        Assert.False(AgentGrantPolicy.MasksLiterals(local, ConnectionEnvironment.Development));

        var covers = AgentGrantPolicy.LocalModelCoversProduction;
        Assert.Equal(covers, AgentGrantPolicy.UsesLocalModel(local, ConnectionEnvironment.Production));
        Assert.Equal(!covers, AgentGrantPolicy.MasksSensitive(local, ConnectionEnvironment.Production));
        Assert.Equal(!covers, AgentGrantPolicy.MasksLiterals(local, ConnectionEnvironment.Production));
        Assert.Equal(covers, AgentGrantPolicy.CanReadProductionTabText(local));
        Assert.Equal(covers, local.CanReadTabText(PrdId));
        Assert.False(local.MasksSensitive(DevId));
        Assert.Equal(!covers, local.MasksLiterals(PrdId));
    }

    [Fact]
    public void Unknown_or_unbound_connections_keep_the_safe_side()
    {
        var local = Grant(true, Dev());
        Assert.True(local.MasksSensitive(null)); // 접속을 모르면 가린다
        Assert.True(local.MasksSensitive(Guid.NewGuid()));
        Assert.False(local.MasksLiterals(null)); // 리터럴 가림은 운영 접속에만(지금과 같음)
        Assert.False(Grant(true, Prd(AiAccessLevel.Off)).CanReadTabText(PrdId)); // 수준 Off 는 로컬 모델이어도 거부
        Assert.Throws<ArgumentNullException>(() => AgentGrantPolicy.UsesLocalModel(null!, ConnectionEnvironment.Development));
    }

    [Fact]
    public void Grant_store_carries_the_local_model_choice()
    {
        var store = new AgentGrantStore(new ManualTimeProvider());
        store.AttachWriter(new NullWriter());
        var profile = new ConnectionProfile("dev", "u", "h/s", Id: DevId, AiAccess: AiAccessLevel.QueryRows);

        var local = store.Create(new AgentGrantRequest([new AgentGrantTarget(profile, AiAccessLevel.QueryRows)], null, LocalModel: true));
        Assert.True(local.Grant.LocalModel);
        Assert.True(store.Extend(local.Grant.GrantId, TimeSpan.FromHours(1))!.LocalModel); // 새 사본에도 남는다

        var cloud = store.Create(new AgentGrantRequest([new AgentGrantTarget(profile, AiAccessLevel.QueryRows)], null));
        Assert.False(cloud.Grant.LocalModel);
    }

    private sealed class NullWriter : IAgentEndpointWriter
    {
        public void Publish(AgentGrantIssue issue)
        {
        }

        public void Update(AgentGrantView grant)
        {
        }

        public void Remove(AgentGrantView grant)
        {
        }
    }

    // ---- 매뉴얼 색인 껍데기 ----

    [Fact]
    public void Manual_index_uses_the_site_base()
    {
        // 표는 m-skill-docs(S1)가 site 에서 만들어 채운다. site 와 맞는지는 Agent.Cli.Tests 의 ManualIndexTests 가 본다.
        Assert.Equal("https://jongik-sv.github.io/oranda", OrandaManual.BaseUrl);
        Assert.NotEmpty(OrandaManual.All);
        Assert.All(OrandaManual.All, e => Assert.StartsWith(OrandaManual.BaseUrl + "/", e.Url, StringComparison.Ordinal));
        Assert.Equal(10, OrandaManual.Search(null).Count);
    }

    [Fact]
    public void Manual_search_matches_every_word_in_title_description_or_section()
    {
        OrandaManual.Entry[] entries =
        [
            new("Execution plan", "Read the plan of a statement", OrandaManual.BaseUrl + "/plan/explain/", "plan"),
            new("Export", "Save a result grid to xlsx or csv", OrandaManual.BaseUrl + "/transfer/export/", "transfer"),
        ];

        Assert.Equal("Execution plan", Assert.Single(OrandaManual.Search("PLAN statement", entries)).Title);
        Assert.Equal("Export", Assert.Single(OrandaManual.Search("xlsx", entries)).Title);
        Assert.Equal("Export", Assert.Single(OrandaManual.Search("transfer", entries)).Title);
        Assert.Empty(OrandaManual.Search("plan xlsx", entries));
        Assert.Equal(2, OrandaManual.Search("  ", entries).Count); // 빈 검색어 = 전부
        Assert.Single(OrandaManual.Search(null, entries, limit: 1));
    }
}
