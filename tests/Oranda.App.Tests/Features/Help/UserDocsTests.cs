using Oranda.App.Features.Help;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Manual;
using Oranda.App.Tests.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Help;

/// <summary>F1(<c>oranda.help.docs</c>): 포커스 화면에 맞는 매뉴얼 절을 연다. 화면 → 경로 표의 경로는 본문에 실제로 있어야 한다.</summary>
public sealed class UserDocsTests
{
    public static TheoryData<string> Presets
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in KeymapLoader.PresetIds)
            {
                data.Add(id);
            }

            return data;
        }
    }

    [Fact]
    public void Every_screen_path_exists_in_the_manual_body()
    {
        var missing = ManualCatalog.ScreenPaths.Values.Append(ManualCatalog.StartPath)
            .Distinct()
            .Where(p => ManualSite.FindPage(p) is not { } file || (!ManualSite.AllowDraftPages && ManualSite.IsDraft(file)))
            .ToList();

        Assert.True(missing.Count == 0, "본문에 없는 F1 경로: " + string.Join(", ", missing));
    }

    [Fact]
    public void Default_site_matches_the_astro_config()
    {
        var config = File.ReadAllText(Path.Combine(ManualSite.RepoRoot, "site", "astro.config.mjs"));
        var site = System.Text.RegularExpressions.Regex.Match(config, @"site:\s*'([^']+)'").Groups[1].Value;
        var basePath = System.Text.RegularExpressions.Regex.Match(config, @"base:\s*'([^']+)'").Groups[1].Value;

        Assert.Equal(ManualCatalog.DefaultSite, site + basePath);
    }

    [Fact]
    public void Every_focus_slot_has_a_screen_path()
    {
        var unmapped = FocusScopes.SlotsOf(null).Where(s => !ManualCatalog.ScreenPaths.ContainsKey(s)).ToList();

        Assert.True(unmapped.Count == 0, "화면 → 경로 테이블에 없는 포커스 자리: " + string.Join(", ", unmapped));
    }

    [Theory]
    [InlineData("sqlGrid", "https://jongik-sv.github.io/oranda/run/result-grid/")]
    [InlineData("schema", "https://jongik-sv.github.io/oranda/schema/browser/")]
    [InlineData(null, "https://jongik-sv.github.io/oranda/start/overview/")]
    [InlineData("unknownSlot", "https://jongik-sv.github.io/oranda/start/overview/")]
    public async Task F1_opens_the_page_of_the_focused_screen(string? slot, string expected)
    {
        var keys = new ContextKeyService();
        keys.SetFocusSlot(slot);
        var opened = new List<string>();
        var handler = new UserDocsCommandHandler { OpenUrl = url => { opened.Add(url); return true; } };

        await handler.ExecuteAsync(new CommandContext("oranda.help.docs", keys), CancellationToken.None);

        Assert.Equal([expected], opened);
    }

    [Fact]
    public void Command_is_registered_in_the_help_menu()
    {
        var command = ShellTestHelpers.AppContributions.Commands.Single(c => c.Definition.Id == "oranda.help.docs");

        Assert.Equal(CommandCategory.Help, command.Definition.Category);
        Assert.Contains("menu:help/docs@870", command.Definition.Placements);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void F1_is_bound_only_to_the_user_docs_command_in_every_preset(string presetId)
    {
        var keymap = KeymapLoader.LoadPreset(presetId);

        foreach (var macOS in new[] { false, true })
        {
            var onF1 = keymap.Bindings.Where(b => b.KeysFor(macOS).Contains("F1")).Select(b => b.Command).ToList();

            Assert.Equal(["oranda.help.docs"], onF1);
        }
    }
}
