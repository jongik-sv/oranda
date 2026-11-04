using System.Text.Json;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>기타 CLI 설정 칸의 읽기·쓰기 내성과, 선택 값이 실행 대상으로 풀리는 규칙.</summary>
public class AiCustomCliSettingsTests
{
    private static readonly AiCustomCli Mine = new() { Id = "c1", Name = "사내 CLI", Command = "mycli --quiet" };

    private static AppSettings Read(string json) => JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

    private static AiActiveProfile Find(string? selection, params AiCustomCli[] clis) =>
        AiActiveProfile.Find(new AiSettings { SystemCliId = selection, CustomClis = clis });

    [Fact]
    public void Custom_clis_round_trip_through_the_settings_file()
    {
        var json = JsonSerializer.Serialize(new AppSettings { Ai = new AiSettings { SystemCliId = AiCustomCliTarget.SelectionOf("c1"), CustomClis = [Mine] } }, OrandaJsonContext.Default.AppSettings);

        var ai = Read(json).Ai;

        Assert.Equal([Mine], ai.CustomClis);
        Assert.Equal("custom:c1", ai.SystemCliId);
    }

    [Fact]
    public void A_file_without_the_key_has_an_empty_list() =>
        Assert.Empty(Read("""{"ai":{"driver":"ClaudeCode"}}""").Ai.CustomClis);

    [Theory]
    [InlineData("""{"ai":{"customClis":null}}""")]
    [InlineData("""{"ai":{"customClis":"x"}}""")]
    [InlineData("""{"ai":{"customClis":{"id":"a"}}}""")]
    [InlineData("""{"ai":{"customClis":[1,"x",null,[2],true]}}""")]
    public void Unreadable_lists_do_not_break_the_whole_settings_file(string json) =>
        Assert.Empty(Read(json).Ai.CustomClis);

    [Fact]
    public void A_broken_field_empties_only_that_field_and_the_file_survives()
    {
        var ai = Read("""{"ai":{"customClis":[{"id":"a","name":5,"command":{"x":1}},{"id":"b","name":"둘째","command":"b-cli"}],"joinSyntax":"ansi"}}""").Ai;

        Assert.Equal(2, ai.CustomClis.Count);
        Assert.Equal(new AiCustomCli { Id = "a", Name = "", Command = "" }, ai.CustomClis[0]);
        Assert.Equal("b-cli", ai.CustomClis[1].Command);
        Assert.Equal(AiJoinSyntax.Ansi, ai.JoinSyntax);
    }

    [Fact]
    public void Settings_with_different_lists_are_not_equal()
    {
        var a = new AiSettings { CustomClis = [Mine] };

        Assert.Equal(a, new AiSettings { CustomClis = [Mine with { }] });
        Assert.NotEqual(a, new AiSettings { CustomClis = [Mine with { Command = "mycli" }] });
        Assert.NotEqual(a, new AiSettings());
    }

    [Fact]
    public void A_registered_custom_cli_is_selected_by_its_prefixed_id()
    {
        var found = Find("custom:c1", Mine);

        Assert.Equal(AiActiveKind.CustomCli, found.Kind);
        Assert.Equal(Mine, found.CustomCli);
        Assert.Equal("custom:c1", found.SystemCliId);
    }

    [Theory]
    [InlineData("custom:nope")]
    [InlineData("custom:")]
    [InlineData("custom:empty")]
    [InlineData("custom:blank")]
    public void A_missing_id_or_an_empty_command_is_unavailable_never_claude_code(string selection)
    {
        var found = Find(selection, Mine, new AiCustomCli { Id = "empty", Name = "x", Command = "" }, new AiCustomCli { Id = "blank", Name = "x", Command = "  " });

        Assert.Equal(AiActiveKind.CustomCliUnavailable, found.Kind);
        Assert.Null(found.CustomCli);
        Assert.Equal(selection, found.SystemCliId);
    }

    [Fact]
    public void A_duplicate_id_uses_the_first_row() =>
        Assert.Equal("first", Find("custom:d", new AiCustomCli { Id = "d", Command = "first" }, new AiCustomCli { Id = "d", Command = "second" }).CustomCli!.Command);

    [Fact]
    public void Profile_and_driver_none_win_over_a_custom_selection()
    {
        var withProfile = new AiSettings { SystemCliId = "custom:c1", CustomClis = [Mine], ActiveProfileId = Guid.NewGuid() };

        Assert.Equal(AiActiveKind.ProfileMissing, AiActiveProfile.Find(withProfile).Kind);
        Assert.Equal(AiActiveKind.None, AiActiveProfile.Find(new AiSettings { Driver = AiDriverKind.None, SystemCliId = "custom:c1", CustomClis = [Mine] }).Kind);
    }

    [Fact]
    public void The_command_hash_follows_the_command_only_and_ignores_surrounding_blanks()
    {
        Assert.Equal(AiCustomCliTarget.CommandHash("mycli --quiet"), AiCustomCliTarget.CommandHash("  mycli --quiet\n"));
        Assert.NotEqual(AiCustomCliTarget.CommandHash("mycli --quiet"), AiCustomCliTarget.CommandHash("mycli --verbose"));
        Assert.Equal(16, AiCustomCliTarget.CommandHash("x").Length);
    }
}
