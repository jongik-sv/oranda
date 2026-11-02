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
}

/// <summary>활성 프로필을 찾은 결과. <see cref="Profile"/> 은 <see cref="AiActiveKind.Profile"/>·<see cref="AiActiveKind.ProfileInvalid"/> 일 때만, <see cref="MissingId"/> 는 <see cref="AiActiveKind.ProfileMissing"/> 일 때만 있다.</summary>
public sealed record AiActiveProfile(AiActiveKind Kind, AiLlmProfile? Profile = null, Guid? MissingId = null)
{
    /// <summary>프로필을 찾지 못했을 때 <c>AiError.Code</c>.</summary>
    public const string ProfileMissingCode = "profile_missing";

    /// <summary>프로필의 프리셋·형식을 모를 때 <c>AiError.Code</c>.</summary>
    public const string ProfileInvalidCode = "profile_invalid";

    /// <summary>설정의 <c>Driver</c>·<c>ActiveProfileId</c>·<c>Profiles</c> 로 실행 대상을 정한다. 설정을 바꾸지 않는다.</summary>
    public static AiActiveProfile Find(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Driver == AiDriverKind.None)
        {
            return new AiActiveProfile(AiActiveKind.None);
        }

        if (settings.ActiveProfileId is not { } id)
        {
            return new AiActiveProfile(AiActiveKind.ClaudeCode);
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
}
