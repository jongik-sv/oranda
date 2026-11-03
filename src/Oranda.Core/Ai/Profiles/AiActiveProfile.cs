using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Profiles;

/// <summary>설정이 가리키는 실행 대상.</summary>
public enum AiActiveKind
{
    /// <summary>AI 를 쓰지 않는다(<see cref="AiDriverKind.None"/>).</summary>
    None,

    /// <summary>시스템 CLI 의 Claude Code(구독 인증).</summary>
    ClaudeCode,

    /// <summary>저장된 LLM 프로필.</summary>
    Profile,

    /// <summary><see cref="AiSettings.ActiveProfileId"/> 가 목록에 없다(프로필이 지워졌다).</summary>
    ProfileMissing,

    /// <summary>프로필은 있지만 이 빌드가 모르는 프리셋·형식이다(다음 버전이 만든 프로필 등). 실행하지 않는다.</summary>
    ProfileInvalid,

    /// <summary>Claude Code 가 아닌 시스템 CLI(<see cref="AiSettings.SystemCliId"/>)이고 이 플랫폼에서 쓸 수 있다.</summary>
    SystemCli,

    /// <summary>시스템 CLI id 가 목록에 없거나 이 플랫폼에서 쓸 수 없다(준비 중·손으로 고친 설정). 실행하지 않고 Claude Code 로도 넘기지 않는다.</summary>
    SystemCliUnavailable,
}

/// <summary>
/// 활성 프로필을 찾은 결과. <see cref="Profile"/> 은 <see cref="AiActiveKind.Profile"/>·<see cref="AiActiveKind.ProfileInvalid"/> 일 때만, <see cref="MissingId"/> 는 <see cref="AiActiveKind.ProfileMissing"/> 일 때만 있다.
/// <see cref="SystemCliId"/> 는 시스템 CLI 종류일 때 설정의 id, <see cref="SystemCli"/> 는 <see cref="AiActiveKind.SystemCli"/> 와 목록에 있는 사용 불가 줄일 때 그 목록 줄이다.
/// </summary>
public sealed record AiActiveProfile(AiActiveKind Kind, AiLlmProfile? Profile = null, Guid? MissingId = null, string? SystemCliId = null, AiSystemCliInfo? SystemCli = null)
{
    /// <summary>시스템 CLI 를 쓸 수 없을 때 <c>AiError.Code</c>.</summary>
    public const string SystemCliUnavailableCode = "system_cli_unavailable";

    /// <summary>프로필을 찾지 못했을 때 <c>AiError.Code</c>.</summary>
    public const string ProfileMissingCode = "profile_missing";

    /// <summary>프로필의 프리셋·형식을 모를 때 <c>AiError.Code</c>.</summary>
    public const string ProfileInvalidCode = "profile_invalid";

    /// <summary>설정의 <c>Driver</c>·<c>ActiveProfileId</c>·<c>Profiles</c> 로 실행 대상을 정한다. 설정을 바꾸지 않는다.</summary>
    public static AiActiveProfile Find(AiSettings settings) => Find(settings, AiProfilePresets.SystemClis);

    /// <summary>
    /// <see cref="Find(AiSettings)"/> 에 시스템 CLI 목록을 주입하는 형태(사용 가능한 줄을 시험하고, 플랫폼별 목록을 넘길 때 쓴다).
    /// 판단 순서: 선택 안 함 → 프로필 id 가 있으면 프로필 → <c>SystemCliId</c> 가 null·claude-code 면 Claude Code → 목록에서 찾아 사용 가능하면 <see cref="AiActiveKind.SystemCli"/>, 아니면 <see cref="AiActiveKind.SystemCliUnavailable"/>.
    /// </summary>
    public static AiActiveProfile Find(AiSettings settings, IReadOnlyList<AiSystemCliInfo> systemClis)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(systemClis);
        if (settings.Driver == AiDriverKind.None)
        {
            return new AiActiveProfile(AiActiveKind.None);
        }

        if (settings.ActiveProfileId is not { } id)
        {
            return FindSystemCli(settings.SystemCliId, systemClis);
        }

        // 빈 id 는 읽을 수 없는 activeProfileId 의 표지(TolerantActiveProfileIdConverter)이므로, id 칸이 빠진 프로필과 짝지어지지 않게 목록을 보지 않는다.
        var profile = id == Guid.Empty ? null : settings.Profiles.FirstOrDefault(p => p is not null && p.Id == id);
        if (profile is null)
        {
            return new AiActiveProfile(AiActiveKind.ProfileMissing, MissingId: id);
        }

        return profile.Preset == AiProfilePreset.Unknown || profile.Format == AiApiFormat.Unknown
            ? new AiActiveProfile(AiActiveKind.ProfileInvalid, profile)
            : new AiActiveProfile(AiActiveKind.Profile, profile);
    }

    private static AiActiveProfile FindSystemCli(string? cliId, IReadOnlyList<AiSystemCliInfo> systemClis)
    {
        if (cliId is null || string.Equals(cliId, AiProfilePresets.ClaudeCodeId, StringComparison.Ordinal))
        {
            return new AiActiveProfile(AiActiveKind.ClaudeCode);
        }

        var cli = systemClis.FirstOrDefault(c => c is not null && string.Equals(c.Id, cliId, StringComparison.Ordinal));
        return cli is { Available: true }
            ? new AiActiveProfile(AiActiveKind.SystemCli, SystemCliId: cliId, SystemCli: cli)
            : new AiActiveProfile(AiActiveKind.SystemCliUnavailable, SystemCliId: cliId, SystemCli: cli);
    }
}
