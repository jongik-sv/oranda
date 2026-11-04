using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>사용자 설정의 MCP 서버 이름 읽기. 읽지 못하거나 모양을 모르면 이름을 추측하지 않고 문제로 돌려준다(fail-closed).</summary>
public sealed class OpencodeGlobalConfigTests
{
    private const string Home = "/home/x";
    private const string RunDirectory = "/home/x/data/ai/runs/run";

    private static string GlobalFile(string name = "opencode.jsonc") => Path.Combine(Home, ".config", "opencode", name);

    private static (IReadOnlyList<string> Names, string? Problem) Read(Dictionary<string, string?> files, Func<string, string?>? environment = null)
        => OpencodeGlobalConfig.ReadMcpServerNames(environment ?? (_ => null), Home, RunDirectory, path => files.GetValueOrDefault(path));

    [Fact]
    public void No_config_files_means_no_servers_and_no_problem()
    {
        var (names, problem) = Read([]);

        Assert.Empty(names);
        Assert.Null(problem);
    }

    [Fact]
    public void Server_names_are_read_from_jsonc_with_comments_and_trailing_commas()
    {
        var files = new Dictionary<string, string?>
        {
            [GlobalFile()] = """
                {
                  // 주석
                  "$schema": "https://opencode.ai/config.json",
                  "mcp": {
                    "rea": { "type": "local", "command": ["x", "mcp"], "enabled": true, },
                    "z": { "type": "local", "command": ["y"] },
                  },
                }
                """,
        };

        var (names, problem) = Read(files);

        Assert.Null(problem);
        Assert.Equal(["rea", "z"], names);
    }

    [Fact]
    public void Names_from_the_xdg_config_home_and_project_folders_above_the_run_folder_are_included()
    {
        var files = new Dictionary<string, string?>
        {
            // 드라이버는 경로를 Path.Combine·GetFullPath 로 만들므로 열쇠도 같은 방식으로 만든다(Windows 는 구분자가 \\ 이고 드라이브 문자가 붙는다).
            [Path.Combine("/xdg", "opencode", "opencode.json")] = """{"mcp":{"a":{"type":"local","command":["a"]}}}""",
            [Path.Combine(Path.GetFullPath(Home), "opencode.json")] = """{"mcp":{"b":{"type":"local","command":["b"]}}}""",
            [Path.Combine(Path.GetFullPath(Home), "data", ".opencode", "opencode.jsonc")] = """{"mcp":{"c":{"type":"local","command":["c"]}}}""",
        };

        var (names, problem) = Read(files, name => name == "XDG_CONFIG_HOME" ? "/xdg" : null);

        Assert.Null(problem);
        Assert.Equal(["a", "b", "c"], names);
    }

    [Theory]
    [InlineData("""{"mcp":{"r":{"type":"remote","url":"https://x.example"}}}""")]
    [InlineData("""{"mcp":{"r":{"command":["x"]}}}""")]
    [InlineData("""{"mcp":{"r":"oops"}}""")]
    [InlineData("""{"mcp":[1]}""")]
    [InlineData("""[1]""")]
    [InlineData("""{ not json""")]
    public void A_server_shape_that_cannot_be_turned_off_or_a_broken_file_is_a_problem(string content)
    {
        var (_, problem) = Read(new Dictionary<string, string?> { [GlobalFile()] = content });

        Assert.NotNull(problem);
    }

    [Fact]
    public void An_unreadable_file_is_a_problem()
    {
        var (_, problem) = OpencodeGlobalConfig.ReadMcpServerNames(_ => null, Home, RunDirectory, _ => throw new UnauthorizedAccessException());

        Assert.NotNull(problem);
    }

    private static (System.Text.Json.Nodes.JsonObject Settings, string? Problem) Settings(Dictionary<string, string?> files)
        => OpencodeGlobalConfig.ReadUserSettings(_ => null, Home, path => files.GetValueOrDefault(path));

    [Fact]
    public void User_settings_keep_only_the_provider_and_model_choice()
    {
        var (settings, problem) = Settings(new()
        {
            [GlobalFile()] = """
                {
                  "provider": { "example": { "models": { "m": {} } } }, // 주석
                  "model": "example/m",
                  "enabled_providers": ["example"],
                  "plugin": ["x"], "mcp": {}, "agents": {}, "permissions": [], "share": "auto", "instructions": ["a.md"],
                }
                """,
        });

        Assert.Null(problem);
        Assert.Equal(["enabled_providers", "model", "provider"], settings.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("""{ "provider": [] }""")]
    [InlineData("""{ "model": 1 }""")]
    [InlineData("""{ "enabled_providers": "a" }""")]
    [InlineData("[]")]
    [InlineData("{ broken")]
    public void A_user_settings_file_with_an_unknown_shape_is_a_problem(string text)
        => Assert.NotNull(Settings(new() { [GlobalFile()] = text }).Problem);

    [Fact]
    public void A_missing_user_config_gives_empty_settings()
    {
        var (settings, problem) = Settings([]);

        Assert.Null(problem);
        Assert.Empty(settings);
    }

    [Fact]
    public void XDG_CONFIG_HOME_moves_the_user_config_folder()
        => Assert.Equal(
            Path.Combine("/custom", "opencode"),
            OpencodeGlobalConfig.GlobalDirectory(name => name == "XDG_CONFIG_HOME" ? "/custom" : null, Home));
}
