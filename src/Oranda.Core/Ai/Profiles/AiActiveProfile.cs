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

    /// <summary>사용자가 등록한 기타 CLI 이고 id 와 명령이 있다. DB 도구·실행 토큰 없이 글만 주고받는다.</summary>
    CustomCli,

    /// <summary>선택한 기타 CLI 가 목록에 없거나 명령이 비었다(지워졌다·손으로 고친 설정). 실행하지 않고 Claude Code 로도 넘기지 않는다.</summary>
    CustomCliUnavailable,
}

/// <summary>
/// 활성 프로필을 찾은 결과. <see cref="Profile"/> 은 <see cref="AiActiveKind.Profile"/>·<see cref="AiActiveKind.ProfileInvalid"/> 일 때만, <see cref="MissingId"/> 는 <see cref="AiActiveKind.ProfileMissing"/> 일 때만 있다.
/// <see cref="SystemCliId"/> 는 시스템 CLI 종류일 때 설정의 id, <see cref="SystemCli"/> 는 <see cref="AiActiveKind.SystemCli"/> 와 목록에 있는 사용 불가 줄일 때 그 목록 줄이다.
/// 기타 CLI 종류에서는 <see cref="SystemCliId"/> 가 <c>custom:&lt;id&gt;</c> 선택 값이고, <see cref="CustomCli"/> 는 <see cref="AiActiveKind.CustomCli"/> 일 때만 있다.
/// </summary>
public sealed record AiActiveProfile(AiActiveKind Kind, AiLlmProfile? Profile = null, Guid? MissingId = null, string? SystemCliId = null, AiSystemCliInfo? SystemCli = null, AiCustomCli? CustomCli = null)
{
    /// <summary>기타 CLI 를 쓸 수 없을 때 <c>AiError.Code</c>.</summary>
    public const string CustomCliUnavailableCode = "custom_cli_unavailable";

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
            return AiCustomCliTarget.TryGetId(settings.SystemCliId, out var customId)
                ? FindCustomCli(settings, customId)
                : FindSystemCli(settings.SystemCliId, systemClis);
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

    private static AiActiveProfile FindCustomCli(AiSettings settings, string id)
    {
        // 같은 id 가 둘이면 앞의 줄을 쓴다(손으로 고친 파일). 빈 id 는 어떤 줄과도 짝짓지 않는다.
        var cli = id.Length == 0 ? null : settings.CustomClis.FirstOrDefault(c => c is not null && string.Equals(c.Id, id, StringComparison.Ordinal));
        var selection = AiCustomCliTarget.SelectionOf(id);
        return AiCustomCliTarget.IsRunnable(cli)
            ? new AiActiveProfile(AiActiveKind.CustomCli, SystemCliId: selection, CustomCli: cli)
            : new AiActiveProfile(AiActiveKind.CustomCliUnavailable, SystemCliId: selection);
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
