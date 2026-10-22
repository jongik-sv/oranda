using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Oranda.Core.Connection;
using Oranda.Core.Formatting;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Workspaces;

namespace Oranda.Core.Tests.Serialization;

/// <summary>
/// docs/design/examples 의 예제 파일 왕복: 읽기 → 쓰기 결과가 원본의 모든 필드를 같은 값으로 담는지(data-model 8장).
/// </summary>
public class OrandaJsonContextTests
{
    [Fact]
    public void Settings_example_round_trips()
    {
        var value = RoundTrip("settings.json", OrandaJsonContext.Default.AppSettings);

        Assert.Equal("D2Coding", value.Editor.FontFamily);
        Assert.Equal(Theme.System, value.Ui.Theme);
        var filter = Assert.Single(value.SchemaBrowser.Filters);
        Assert.Equal(SchemaFilterScope.Selected, filter.Scope);
        Assert.Equal(["EAIAPUSER", "M90APUSER", "MESAPUSER"], filter.Schemas);
        Assert.Equal("TB_M30*", filter.NamePatterns["TABLE"]);
        Assert.Equal(filter.Id, value.SchemaBrowser.ActiveFilters["7d0c3f6e-2b1a-4c5d-9e8f-0a1b2c3d4e5f"]);
    }

    [Fact]
    public void Settings_without_saved_filters_read_as_empty()
    {
        var value = JsonSerializer.Deserialize("""{ "schemaVersion": 1, "schemaBrowser": { "singleClickPreview": false } }""",
            OrandaJsonContext.Default.AppSettings)!;

        Assert.Empty(value.SchemaBrowser.Filters);
        Assert.Empty(value.SchemaBrowser.ActiveFilters);
        Assert.Equal(new SchemaBrowserSettings { SingleClickPreview = false }, value.SchemaBrowser);
    }

    [Fact]
    public void Saved_filter_schema_pattern_is_optional_and_round_trips()
    {
        var old = JsonSerializer.Deserialize(
            """{ "schemaVersion": 1, "schemaBrowser": { "filters": [ { "id": "f1", "name": "옛", "scope": "Selected", "schemas": ["HR"] } ] } }""",
            OrandaJsonContext.Default.AppSettings)!;
        Assert.Null(Assert.Single(old.SchemaBrowser.Filters).SchemaPattern);

        var pattern = new SchemaBrowserFilter { Id = "f2", Name = "AP", Scope = SchemaFilterScope.Pattern, SchemaPattern = "APUSER$" };
        var json = JsonSerializer.Serialize(old with { SchemaBrowser = old.SchemaBrowser with { Filters = [pattern] } }, OrandaJsonContext.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

        Assert.Equal(pattern, Assert.Single(back.SchemaBrowser.Filters));
        Assert.NotEqual(pattern, pattern with { SchemaPattern = "X" });
    }

    [Fact]
    public void Schema_browser_settings_compare_saved_filters_by_content()
    {
        static SchemaBrowserSettings Make(SchemaBrowserFilter f) => new()
        {
            Filters = [f],
            ActiveFilters = new Dictionary<string, string> { ["p1"] = f.Id },
        };

        var f = new SchemaBrowserFilter
        {
            Id = "f1",
            Name = "APUser",
            Scope = SchemaFilterScope.Selected,
            Schemas = ["A", "B"],
            HiddenTypes = ["JOB"],
            NamePatterns = new Dictionary<string, string> { ["TABLE"] = "TB*" },
        };
        Assert.Equal(Make(f), Make(f with { Schemas = ["A", "B"], NamePatterns = new Dictionary<string, string> { ["TABLE"] = "TB*" } }));
        Assert.Equal(Make(f).GetHashCode(), Make(f with { Schemas = ["A", "B"] }).GetHashCode());

        Assert.NotEqual(Make(f), Make(f with { Id = "f2" }));
        Assert.NotEqual(Make(f), Make(f with { Name = "X" }));
        Assert.NotEqual(Make(f), Make(f with { Scope = SchemaFilterScope.All }));
        Assert.NotEqual(Make(f), Make(f with { Schemas = ["A"] }));
        Assert.NotEqual(Make(f), Make(f with { HiddenTypes = [] }));
        Assert.NotEqual(Make(f), Make(f with { NamePatterns = new Dictionary<string, string> { ["TABLE"] = "X*" } }));
        Assert.NotEqual(Make(f), Make(f with { UnderscoreIsWildcard = true }));
        Assert.NotEqual(Make(f), Make(f with { WrapWithPercent = false }));
        Assert.NotEqual(Make(f), Make(f) with { ActiveFilters = new Dictionary<string, string>() });
        Assert.NotEqual(Make(f), Make(f) with { SingleClickPreview = false });
    }

    [Fact]
    public void Connections_example_round_trips()
    {
        var value = RoundTrip("connections.json", OrandaJsonContext.Default.ConnectionsDocument);

        Assert.Equal(3, value.Profiles.Count);
        Assert.Equal(ConnectionEnvironment.Production, value.Profiles[0].Environment);
        Assert.Equal(LicensePack.DiagnosticsAndTuning, value.Profiles[1].LicensePack);
        Assert.Equal(AuthMode.SysDba, value.Profiles[2].Auth);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), value.Profiles[0].LastConnectedAt);
        Assert.Equal("주문 조회 전용 계정.\n배치 시간(02~04시)에는 쓰지 않는다", value.Profiles[0].Comment);
        Assert.Null(value.Profiles[1].Comment); // 키가 없으면 null
    }

    [Fact]
    public void Formatter_example_round_trips()
    {
        var value = RoundTrip(Path.Combine("formatter", "team-default.json"), OrandaJsonContext.Default.FormatterProfile);

        Assert.Equal(CommaPosition.Leading, value.Options.CommaPosition);
        Assert.Equal(LetterCase.Lower, value.Options.Case.Identifier);
    }

    [Fact]
    public void Workspace_example_round_trips()
    {
        var value = RoundTrip(Path.Combine("workspaces", "default.json"), OrandaJsonContext.Default.Workspace);

        Assert.Equal(2, value.Tabs.Count);
        Assert.Equal(new TextPosition(42, 7), value.Tabs[0].Caret);
        Assert.True(value.Tabs[1].IsDirty);
        Assert.Null(value.Tabs[0].Binds);
        Assert.Equal("HR", Assert.Single(value.Tabs[1].Binds!).Value);
    }

    [Fact]
    public void Writes_camel_case_string_enums_without_nulls()
    {
        var json = JsonSerializer.Serialize(new ConnectionsDocument { Profiles = [new ConnectionProfile("a", "b", "c")] },
            OrandaJsonContext.Default.ConnectionsDocument);

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"environment\": \"Development\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialRef", json, StringComparison.Ordinal);
    }

    private static T RoundTrip<T>(string relativePath, JsonTypeInfo<T> info)
    {
        var original = File.ReadAllText(Path.Combine(ExamplesDirectory(), relativePath));
        var value = JsonSerializer.Deserialize(original, info)!;
        var written = JsonSerializer.Serialize(value, info);

        AssertContains(JsonNode.Parse(original)!, JsonNode.Parse(written)!, "$");
        Assert.Equal(written, JsonSerializer.Serialize(JsonSerializer.Deserialize(written, info)!, info));
        return value;
    }

    /// <summary><paramref name="expected"/> 의 모든 필드가 <paramref name="actual"/> 에 같은 값으로 있어야 한다.</summary>
    private static void AssertContains(JsonNode expected, JsonNode actual, string path)
    {
        switch (expected)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    var other = actual.AsObject()[key];
                    Assert.True(other is not null, $"{path}.{key} 가 다시 쓴 결과에 없다");
                    AssertContains(child!, other!, $"{path}.{key}");
                }

                break;
            case JsonArray array:
                var otherArray = actual.AsArray();
                Assert.Equal(array.Count, otherArray.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    AssertContains(array[i]!, otherArray[i]!, $"{path}[{i}]");
                }

                break;
            default:
                Assert.True(JsonNode.DeepEquals(expected, actual), $"{path}: {expected.ToJsonString()} ≠ {actual.ToJsonString()}");
                break;
        }
    }

    private static string ExamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다."),
            "docs", "design", "examples");
    }
}
