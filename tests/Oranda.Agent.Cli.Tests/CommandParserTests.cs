using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Agent.Cli.Tests;

/// <summary>B2·B3: 명령줄 → 도구 이름 + 인자.</summary>
public sealed class CommandParserTests
{
    private string _stdin = string.Empty;
    private readonly Dictionary<string, string> _files = [];

    [Fact]
    public void Tab_propose_reads_new_text_from_stdin_and_maps_every_option()
    {
        _stdin = "SELECT 1\nFROM dual";

        var command = Parse("tab", "propose", "--tab", "6f1c", "--base-version", "41", "--range", "statement", "--title", "주문 상태 조건",
            "--reason", "why", "--wait", "180", "-");

        Assert.Equal(CommandKind.Tool, command.Kind);
        Assert.Equal(AppToolNames.ProposeEdit, command.Tool);
        AssertJson("""{"tab":"6f1c","base_version":41,"range":"statement","title":"주문 상태 조건","reason":"why","wait_s":180,"new_text":"SELECT 1\nFROM dual"}""", command.Arguments);
    }

    [Fact]
    public void Query_reads_sql_from_file_and_binds_json()
    {
        _files["new.sql"] = "SELECT * FROM SAMPLE.ORDERS WHERE ID = :ID";

        var command = Parse("query", "--conn", "dev-erp", "--max-rows", "20", "--binds-json", """{"ID":10}""", "--file", "new.sql");

        Assert.Equal(AppToolNames.Query, command.Tool);
        AssertJson("""{"connection":"dev-erp","max_rows":20,"binds":{"ID":10},"sql":"SELECT * FROM SAMPLE.ORDERS WHERE ID = :ID"}""", command.Arguments);
    }

    [Fact]
    public void Obj_commands_map_positionals_and_lists()
    {
        AssertJson("""{"connection":"dev","parts":["columns","keys"],"object":"SAMPLE.ORDERS"}""",
            Parse("obj", "describe", "SAMPLE.ORDERS", "--conn", "dev", "--parts", "columns,keys").Arguments);
        AssertJson("""{"types":["TABLE","VIEW","PACKAGE"],"limit":5,"name_like":"ORD%"}""",
            Parse("obj", "find", "ORD%", "--type", "TABLE,VIEW", "--type", "PACKAGE", "--limit", "5").Arguments);
        AssertJson("""{"names":["ORDERS","SAMPLE.ITEMS"]}""", Parse("obj", "resolve", "ORDERS", "SAMPLE.ITEMS").Arguments);
        AssertJson("""{"open":true,"object":"SAMPLE.PKG"}""", Parse("obj", "reveal", "SAMPLE.PKG", "--open").Arguments);
    }

    [Fact]
    public void Phase_two_commands_map_to_the_new_tools()
    {
        // 2단계 C0: tab run·result export·tune start·ui commands·ui run.
        Assert.Equal(AppToolNames.RunTab, Parse("tab", "run", "--tab", "6f1c").Tool);
        AssertJson("""{"tab":"6f1c","base_version":41,"range":"statement","timeout_s":60}""",
            Parse("tab", "run", "--tab", "6f1c", "--base-version", "41", "--range", "statement", "--timeout", "60").Arguments);
        AssertJson("""{"tab":"6f1c","format":"xlsx","out":"C:\\out\\orders.xlsx","index":1}""",
            Parse("result", "export", "--tab", "6f1c", "--format", "xlsx", "--out", "C:\\out\\orders.xlsx", "--index", "1").Arguments);
        Assert.Equal(AppToolNames.TuneStart, Parse("tune", "start", "--tab", "6f1c").Tool);
        AssertJson("""{"category":"view","grade":"now"}""", Parse("ui", "commands", "--category", "view", "--grade", "now").Arguments);
        Assert.Equal(AppToolNames.RunCommand, Parse("ui", "run", "oranda.view.toggleResults").Tool);
        AssertJson("""{"tab":"6f1c","id":"oranda.view.toggleResults"}""", Parse("ui", "run", "oranda.view.toggleResults", "--tab", "6f1c").Arguments);
    }

    [Fact]
    public void Tune_commands_map_to_existing_tools()
    {
        Assert.Equal(AgentToolNames.GetAwrPlan, Parse("tune", "awr-plan", "abc123", "42").Tool);
        AssertJson("""{"sql_id":"abc123","plan_hash_value":42}""", Parse("tune", "awr-plan", "abc123", "42").Arguments);
        AssertJson("""{"table":"SAMPLE.ORDERS","column":"STATUS"}""", Parse("tune", "histogram", "SAMPLE.ORDERS", "STATUS").Arguments);
        AssertJson("""{"sql_id":"x"}""", Parse("tune", "context", "--sql-id", "x").Arguments);
    }

    [Fact]
    public void Tune_bench_takes_a_variant_array_or_an_object()
    {
        _stdin = """[{"label":"a","sql":"SELECT 1 FROM dual"}]""";
        AssertJson("""{"repeat":2,"variants":[{"label":"a","sql":"SELECT 1 FROM dual"}]}""", Parse("tune", "bench", "--repeat", "2", "-").Arguments);

        _stdin = """{"variants":[{"label":"b"}],"repeat":1}""";
        AssertJson("""{"variants":[{"label":"b"}],"repeat":1}""", Parse("tune", "bench", "-").Arguments);
    }

    [Fact]
    public void Sql_check_takes_sql_or_tab_but_not_both_or_neither()
    {
        AssertJson("""{"tab":"t1","lint":false}""", Parse("sql", "check", "--tab", "t1", "--no-lint").Arguments);
        _stdin = "SELECT 1 FROM dual";
        AssertJson("""{"sql":"SELECT 1 FROM dual"}""", Parse("sql", "check", "-").Arguments);

        AssertUsage("sql", "check");
        AssertUsage("sql", "check", "--tab", "t1", "-");
    }

    [Fact]
    public void Call_passes_any_tool_with_json_and_empty_arguments()
    {
        var command = Parse("call", "get_status");
        Assert.Equal("get_status", command.Tool);
        AssertJson("{}", command.Arguments);

        AssertJson("""{"tab":"t1"}""", Parse("call", "get_binds", "--json", """{"tab":"t1"}""").Arguments);
        _stdin = """{"name_like":"A%"}""";
        AssertJson("""{"name_like":"A%"}""", Parse("call", "find_objects", "-").Arguments);
    }

    [Fact]
    public void Global_options_are_taken_from_anywhere()
    {
        var command = Parse("--instance", "200", "tabs", "--text");

        Assert.Equal(new GlobalOptions("200", null, true), command.Globals);
        Assert.Equal(AppToolNames.ListTabs, command.Tool);
        AssertJson("{}", command.Arguments);
    }

    [Fact]
    public void Special_commands_are_recognised()
    {
        Assert.Equal(CommandKind.Help, Parse().Kind);
        Assert.Equal(CommandKind.Help, Parse("help").Kind);
        Assert.Equal(CommandKind.Help, Parse("tab", "get", "--help").Kind);
        Assert.Equal(CommandKind.Tools, Parse("tools").Kind);
        Assert.Equal(CommandKind.StatusAll, Parse("status", "--all").Kind);
        Assert.Equal(AppToolNames.GetStatus, Parse("status").Tool);
        var mcp = Parse("mcp", "--pipe", "p", "--instance", "1");
        Assert.Equal(CommandKind.Mcp, mcp.Kind);
        Assert.Equal(new GlobalOptions("1", "p", false), mcp.Globals);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("tab")]
    [InlineData("tab", "get", "--bogus", "x")]
    [InlineData("tab", "get", "extra")]
    [InlineData("tab", "new")]
    [InlineData("tab", "propose", "--tab", "t", "--base-version", "seven", "-")]
    [InlineData("tab", "get", "--tab")]
    [InlineData("tabs", "-")]
    [InlineData("obj", "describe")]
    [InlineData("query", "--binds-json", "[1]", "-")]
    [InlineData("query", "--file", "missing.sql")]
    [InlineData("call")]
    [InlineData("call", "get_status", "--json", "[]")]
    [InlineData("call", "get_status", "--json", "{bad")]
    [InlineData("tune", "bench", "-")]
    [InlineData("mcp", "extra")]
    public void Bad_command_lines_are_usage_errors(params string[] args) => AssertUsage(args);

    private void AssertUsage(params string[] args)
    {
        _stdin = args.Contains("bench") ? "42" : _stdin;
        var ex = Assert.Throws<BridgeException>(() => Parse(args));
        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Equal(AgentCliExit.Usage, AgentCliExit.For(ex.Code));
    }

    private ParsedCommand Parse(params string[] args) =>
        CommandParser.Parse(args, path => _files.TryGetValue(path, out var text) ? text : throw new FileNotFoundException("missing", path), () => _stdin);

    private static void AssertJson(string expected, JsonObject? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"expected {expected}\nactual   {actual?.ToJsonString()}");
}
