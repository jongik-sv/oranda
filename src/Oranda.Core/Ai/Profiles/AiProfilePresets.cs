using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Profiles;

/// <summary>프리셋 한 종류의 기본값(설계 §6.4).</summary>
/// <param name="DisplayName">카드에 보이는 이름. 제공자 고유 이름은 그대로, 「직접 입력」 만 한국어 문구다.</param>
/// <param name="Format">기본 API 형식.</param>
/// <param name="FormatSelectable">사용자가 형식을 바꿀 수 있다(직접 입력만). 호환 서버의 형식은 URL 로 판별할 수 없기 때문이다.</param>
/// <param name="DefaultBaseUrl">기본 URL. 직접 입력은 빈 문자열.</param>
/// <param name="KeyRequirement">API 키 필요 여부.</param>
public sealed record AiProfilePresetInfo(
    AiProfilePreset Preset,
    string DisplayName,
    AiApiFormat Format,
    bool FormatSelectable,
    string DefaultBaseUrl,
    AiKeyRequirement KeyRequirement);

public enum AiKeyRequirement
{
    /// <summary>키 없이 연결할 수 없다.</summary>
    Required,

    /// <summary>키 없이도 연결된다(로컬 서버).</summary>
    NotNeeded,

    /// <summary>서버에 따라 다르다. 비워 둘 수 있다.</summary>
    Optional,
}

/// <summary>시스템 CLI 한 줄(프로필 드롭다운의 「시스템 CLI」 묶음).</summary>
/// <param name="Id">안정적인 구분 값.</param>
/// <param name="Available">false 면 「준비 중」 비활성 줄이다.</param>
public sealed record AiSystemCliInfo(string Id, string DisplayName, bool Available);

/// <summary>프리셋 표와 시스템 CLI 표시 목록. 표시 순서가 곧 카드·줄 순서다.</summary>
public static class AiProfilePresets
{
    public const string ClaudeCodeId = "claude-code";

    public static IReadOnlyList<AiProfilePresetInfo> All { get; } =
    [
        new(AiProfilePreset.Anthropic, "Anthropic", AiApiFormat.Anthropic, false, "https://api.anthropic.com", AiKeyRequirement.Required),
        new(AiProfilePreset.ZAi, "Z.AI", AiApiFormat.Anthropic, false, "https://api.z.ai/api/anthropic", AiKeyRequirement.Required),
        new(AiProfilePreset.Gemini, "Google Gemini", AiApiFormat.OpenAi, false, "https://generativelanguage.googleapis.com/v1beta/openai", AiKeyRequirement.Required),
        new(AiProfilePreset.OpenAi, "OpenAI", AiApiFormat.OpenAi, false, "https://api.openai.com/v1", AiKeyRequirement.Required),
        new(AiProfilePreset.Ollama, "Ollama", AiApiFormat.OpenAi, false, "http://localhost:11434/v1", AiKeyRequirement.NotNeeded),
        new(AiProfilePreset.LmStudio, "LM Studio", AiApiFormat.OpenAi, false, "http://localhost:1234/v1", AiKeyRequirement.NotNeeded),
        new(AiProfilePreset.Custom, "직접 입력", AiApiFormat.Anthropic, true, "", AiKeyRequirement.Optional),
    ];

    /// <summary>시스템 CLI 표시 목록. Claude Code 만 실행할 수 있고 나머지는 「준비 중」 이다.</summary>
    public static IReadOnlyList<AiSystemCliInfo> SystemClis { get; } =
    [
        new(ClaudeCodeId, "Claude Code", true),
        new("antigravity-cli", "Antigravity CLI", false),
        new("codex-cli", "Codex CLI", false),
    ];

    public static AiProfilePresetInfo Get(AiProfilePreset preset) =>
        All.FirstOrDefault(p => p.Preset == preset) ?? throw new ArgumentOutOfRangeException(nameof(preset), preset, "알 수 없는 프리셋입니다.");

    /// <summary>
    /// 프리셋 고르기 직후의 새 프로필 초안(이름·모델은 사용자가 채운다). 직접 입력이 아니면 형식·URL 이 프리셋 기본값이다.
    /// </summary>
    public static AiLlmProfile NewDraft(AiProfilePreset preset)
    {
        var info = Get(preset);
        return new AiLlmProfile
        {
            Id = Guid.NewGuid(),
            Preset = preset,
            Format = info.Format,
            BaseUrl = info.DefaultBaseUrl,
        };
    }
}
