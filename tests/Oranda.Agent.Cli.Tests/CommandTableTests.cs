using System.Text.Json.Nodes;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Agent.Cli.Tests;

/// <summary>B3: 설계 §4.2 명령 표가 계약의 도구·인자와 맞는가.</summary>
public sealed class CommandTableTests
{
    public static TheoryData<string> Commands()
    {
        var data = new TheoryData<string>();
        foreach (var spec in CommandTable.All)
        {
            data.Add(spec.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Every_argument_of_a_command_is_in_its_tool_schema(string name)
    {
        var spec = CommandTable.All.Single(s => s.Name == name);
        var tool = AppTools.Find(spec.Tool);
        Assert.NotNull(tool);
        var properties = JsonNode.Parse(tool.InputSchemaJson)!["properties"]!.AsObject();

        var fields = spec.Options.Select(o => o.Field)
            .Concat(spec.Positionals.Select(p => p.Field))
            .Concat(spec.BodyField is { } body && spec.Body != BodyRule.Variants ? [body] : [])
            .Concat(spec.Body == BodyRule.Variants ? [AgentFields.Variants] : []);

        Assert.All(fields, f => Assert.True(properties.ContainsKey(f), $"{name}: '{f}' 는 {spec.Tool} 스키마에 없다"));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Required_tool_arguments_have_a_way_in(string name)
    {
        var spec = CommandTable.All.Single(s => s.Name == name);
        var required = JsonNode.Parse(AppTools.Find(spec.Tool)!.InputSchemaJson)!["required"]?.AsArray().Select(r => (string)r!) ?? [];
        var given = spec.Options.Select(o => o.Field).Concat(spec.Positionals.Select(p => p.Field))
            .Concat(spec.BodyField is { } body ? [body] : []);

        Assert.All(required, r => Assert.Contains(r, given));
    }

    [Fact]
    public void Every_external_tool_is_reachable_by_a_command()
    {
        var covered = CommandTable.All.Select(s => s.Tool).ToHashSet(StringComparer.Ordinal);

        var missing = AppTools.External.Select(t => t.Name).Where(n => !covered.Contains(n)).ToList();

        Assert.Empty(missing);
        Assert.Equal(35, AppTools.External.Count);
    }

    [Fact]
    public void Command_names_are_unique()
    {
        Assert.Equal(CommandTable.All.Count, CommandTable.All.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Help_lists_every_command()
    {
        var help = CommandParser.HelpText();

        Assert.All(CommandTable.All, s => Assert.Contains("orandactl " + s.Name, help, StringComparison.Ordinal));
        Assert.Contains("orandactl call", help, StringComparison.Ordinal);
    }
}
