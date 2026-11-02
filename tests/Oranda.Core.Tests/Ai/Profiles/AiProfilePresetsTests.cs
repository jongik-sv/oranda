using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>프리셋 표(설계 §6.4)·시스템 CLI 목록·키 이름.</summary>
public class AiProfilePresetsTests
{
    [Fact]
    public void Table_matches_the_design()
    {
        var rows = AiProfilePresets.All.Select(p => (p.Preset, p.Format, p.DefaultBaseUrl, p.KeyRequirement, p.FormatSelectable)).ToList();

        Assert.Equal(
            [
                (AiProfilePreset.Anthropic, AiApiFormat.Anthropic, "https://api.anthropic.com", AiKeyRequirement.Required, false),
                (AiProfilePreset.ZAi, AiApiFormat.Anthropic, "https://api.z.ai/api/anthropic", AiKeyRequirement.Required, false),
                (AiProfilePreset.Gemini, AiApiFormat.OpenAi, "https://generativelanguage.googleapis.com/v1beta/openai", AiKeyRequirement.Required, false),
                (AiProfilePreset.OpenAi, AiApiFormat.OpenAi, "https://api.openai.com/v1", AiKeyRequirement.Required, false),
                (AiProfilePreset.Ollama, AiApiFormat.OpenAi, "http://localhost:11434/v1", AiKeyRequirement.NotNeeded, false),
                (AiProfilePreset.LmStudio, AiApiFormat.OpenAi, "http://localhost:1234/v1", AiKeyRequirement.NotNeeded, false),
                (AiProfilePreset.Custom, AiApiFormat.Anthropic, "", AiKeyRequirement.Optional, true),
            ],
            rows);
    }

    [Fact]
    public void Every_preset_value_has_exactly_one_row() =>
        Assert.Equal(Enum.GetValues<AiProfilePreset>().Where(p => p != AiProfilePreset.Unknown).Order(), AiProfilePresets.All.Select(p => p.Preset).Order());

    [Fact]
    public void System_cli_list_has_only_claude_code_available() =>
        Assert.Equal(
            [("Claude Code", true), ("Antigravity CLI", false), ("Codex CLI", false)],
            AiProfilePresets.SystemClis.Select(c => (c.DisplayName, c.Available)));

    [Fact]
    public void New_draft_takes_format_and_url_from_the_preset()
    {
        var draft = AiProfilePresets.NewDraft(AiProfilePreset.Ollama);

        Assert.NotEqual(Guid.Empty, draft.Id);
        Assert.Equal((AiProfilePreset.Ollama, AiApiFormat.OpenAi, "http://localhost:11434/v1"), (draft.Preset, draft.Format, draft.BaseUrl));
    }

    [Fact]
    public void Credential_key_is_oranda_ai_plus_the_guid()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("oranda:ai:11111111-2222-3333-4444-555555555555", AiProfileCredentials.KeyFor(id));
    }
}
