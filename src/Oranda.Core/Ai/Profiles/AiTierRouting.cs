using Oranda.Core.Ai.Contracts;
using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Profiles;

/// <summary>
/// 등급 고르기의 결과(설계 §1.2 「크기 올림」). <see cref="Target"/> 은 실제로 보낼 대상이고 <see cref="Tier"/> 는 그 대상의 등급이다.
/// 가림·확인 창·라우터 기대값은 이 대상으로 계산한다(크기 올림을 확인 뒤에 하면 확인한 곳과 다른 곳으로 나가므로, 호출한 쪽이 확인 전에 고른다).
/// </summary>
/// <param name="Requested">명령이 원한 등급.</param>
/// <param name="Tier">실제로 보낼 등급. 가벼운 일이라도 입력이 크면 <see cref="AiTier.Main"/> 이다.</param>
/// <param name="Target">보낼 대상(<see cref="AiActiveProfile.Find(AiSettings, AiTier)"/>).</param>
/// <param name="Limit">가벼운 일 모델의 입력 한도(토큰). 가벼운 일 모델을 쓰지 않거나 한도를 모르면 null.</param>
/// <param name="EstimatedTokens">입력 추정 토큰.</param>
public sealed record AiTierChoice(AiTier Requested, AiTier Tier, AiActiveProfile Target, int? Limit, int EstimatedTokens)
{
    /// <summary>가벼운 일을 입력 크기 때문에 주 모델로 올렸다.</summary>
    public bool Escalated => Requested == AiTier.Light && Tier == AiTier.Main;
}

/// <summary>
/// 등급별 대상 고르기·크기 올림·모델 이름(설계 §1.2·§1.3). 라우터와 화면이 같은 규칙을 쓰도록 여기 한곳에 둔다.
/// 한도는 사용자가 정하지 않는다: 프로필은 <see cref="AiLlmProfile.MaxInputTokens"/>(로컬 프리셋은 「연결 확인」 이 채운 값), Claude CLI·Codex 는 앱이 아는 모델별 표다.
/// </summary>
public static class AiTierRouting
{
    /// <summary>크기 올림 기준: 가벼운 일 모델 한도의 70%(AE-2).</summary>
    public const double EscalationRatio = 0.7;

    /// <summary>가벼운 일 모델(Claude Code)의 노력. 칸을 두지 않고 고정한다(설계 §1.3).</summary>
    public const string LightEffort = "low";

    /// <summary>크기 올림을 알리는 글(채팅창 답·AI 띠에 한 줄).</summary>
    public const string EscalatedText = "입력이 커서 주 모델로 보냈습니다.";

    /// <summary>프롬프트의 지시·규칙 글에 더해 잡는 토큰(문장 글만 세어 고를 때 쓴다).</summary>
    public const int PromptOverheadTokens = 2000;

    /// <summary>Claude 모델의 기본 입력 한도(opus·sonnet·haiku).</summary>
    public const int ClaudeInputTokens = 200_000;

    /// <summary>Claude 의 1M 문맥 모델(<c>[1m]</c> 접미사).</summary>
    public const int ClaudeLongInputTokens = 1_000_000;

    /// <summary>Codex CLI 의 입력 한도. 라우터가 모델 칸을 비워 보내므로 CLI 기본 모델 기준으로 낮게 잡는다.</summary>
    public const int CodexInputTokens = 200_000;

    /// <summary>
    /// 등급을 고른다. 주 모델 요청·가벼운 일 모델이 비어 있음 → 요청 등급 그대로(가벼운 일 모델이 비면 대상은 주 모델이다).
    /// 가벼운 일 모델이 있고 추정 토큰이 한도의 70% 를 넘으면 주 모델로 올린다. 한도를 모르면(기타 CLI) 올리지 않는다.
    /// </summary>
    public static AiTierChoice Choose(AiSettings settings, AiTier requested, int estimatedTokens, IReadOnlyList<AiSystemCliInfo>? systemClis = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clis = systemClis ?? AiProfilePresets.SystemClis;
        if (requested != AiTier.Light || settings.Light is not { HasTarget: true })
        {
            return new AiTierChoice(requested, requested, AiActiveProfile.Find(settings, requested, clis), null, estimatedTokens);
        }

        var light = AiActiveProfile.Find(settings, AiTier.Light, clis);
        var limit = InputLimit(light, settings.ForTier(AiTier.Light).Model);
        return limit is { } max && estimatedTokens > max * EscalationRatio
            ? new AiTierChoice(requested, AiTier.Main, AiActiveProfile.Find(settings, AiTier.Main, clis), limit, estimatedTokens)
            : new AiTierChoice(requested, AiTier.Light, light, limit, estimatedTokens);
    }

    /// <summary>요청이 가벼운 일 모델로 가는가: 등급이 Light 이고 가벼운 일 모델이 대상을 골랐다.</summary>
    public static bool UsesLightTarget(AiSettings settings, AiTier tier)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return tier == AiTier.Light && settings.Light is { HasTarget: true };
    }

    /// <summary>
    /// 대상의 입력 한도(토큰). 프로필 = <see cref="AiLlmProfile.MaxInputTokens"/>, Claude Code = 모델별 표, Codex = <see cref="CodexInputTokens"/>,
    /// 그 밖의 시스템 CLI = Claude 기본과 같은 값. 기타 CLI·쓸 수 없는 대상은 null(모름).
    /// </summary>
    public static int? InputLimit(AiActiveProfile target, string? claudeModel)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            AiActiveKind.Profile => target.Profile!.MaxInputTokens,
            AiActiveKind.ClaudeCode => ClaudeModelLimit(claudeModel),
            AiActiveKind.SystemCli when string.Equals(target.SystemCliId, AiProfilePresets.CodexCliId, StringComparison.Ordinal) => CodexInputTokens,
            AiActiveKind.SystemCli => ClaudeInputTokens,
            _ => null,
        };
    }

    /// <summary>Claude 모델 이름의 입력 한도. <c>[1m]</c> 접미사는 1M, 그 밖(별칭·전체 이름·빈 값)은 200K.</summary>
    public static int ClaudeModelLimit(string? model) =>
        model is not null && model.Trim().EndsWith("[1m]", StringComparison.OrdinalIgnoreCase) ? ClaudeLongInputTokens : ClaudeInputTokens;

    /// <summary>
    /// 입력 추정 토큰. 영문·SQL(ASCII)은 3자에 1토큰, 한글 등 그 밖의 글자는 1자에 1토큰으로 넉넉히 센다(실제보다 크게 잡아 잘리지 않게 한다).
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        long ascii = 0, other = 0;
        foreach (var c in text)
        {
            if (c < 128)
            {
                ascii++;
            }
            else if (!char.IsLowSurrogate(c))
            {
                other++;
            }
        }

        var tokens = ((ascii + 2) / 3) + other;
        return tokens > int.MaxValue ? int.MaxValue : (int)tokens;
    }

    /// <summary>
    /// 답한 모델 이름(답 머리·감사 기록). Claude Code = 모델 이름(없으면 「Claude Code」), 프로필 = 「이름 · 모델」(모델이 비면 이름),
    /// 시스템 CLI = 표시 이름, 기타 CLI = 이름(비면 id). 키·주소는 넣지 않는다.
    /// </summary>
    public static string ModelLabel(AiActiveProfile target, string? claudeModel)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (target.Kind)
        {
            case AiActiveKind.ClaudeCode:
                return string.IsNullOrWhiteSpace(claudeModel) ? "Claude Code" : claudeModel.Trim();
            case AiActiveKind.Profile or AiActiveKind.ProfileInvalid when target.Profile is { } profile:
                var name = profile.Name.Trim();
                var model = profile.Model.Trim();
                return model.Length == 0 ? name : name.Length == 0 ? model : $"{name} · {model}";
            case AiActiveKind.SystemCli or AiActiveKind.SystemCliUnavailable:
                return target.SystemCli?.DisplayName ?? target.SystemCliId ?? "CLI";
            case AiActiveKind.CustomCli when target.CustomCli is { } custom:
                return custom.Name.Trim().Length > 0 ? custom.Name.Trim() : custom.Id;
            default:
                return target.SystemCliId ?? "(알 수 없는 모델)";
        }
    }
}
