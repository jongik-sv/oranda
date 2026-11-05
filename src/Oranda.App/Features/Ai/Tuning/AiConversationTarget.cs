using System;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 대화가 시작될 때의 보낼 곳: 프로필 id·주소·형식·모델(Claude Code 면 id 가 null). 이어 말할 때 지금 설정의 보낼 곳과 하나라도 다르면 앞 대화의 <c>ResumeId</c> 를
/// 쓸 수 없고, 앞서 보낸 이미지가 경고 없이 새로운 곳으로 가지 않도록 그 대화는 끝낸다(설계 §6.6). 같은 프로필의 주소를 고친 경우도 다른 곳이다.
/// <see cref="ToExpectation"/> 은 라우터에 넘기는 기대값이다(확인 창에 적은 곳과 실제 전송처가 같게).
/// </summary>
/// <param name="ProfileId">활성 프로필 id. Claude Code 면 null.</param>
/// <param name="Label">사용자에게 보일 받는 곳 이름(프로필 이름, 「Claude Code」).</param>
/// <param name="BaseUrl">프로필의 서버 주소(앞뒤 공백 제거, 라우터와 같은 비교). Claude Code 면 null.</param>
/// <param name="Format">프로필의 API 형식. Claude Code 면 null.</param>
/// <param name="Model">프로필의 모델. Claude Code 면 null(CLI 모델은 대화마다 고른 값이라 비교하지 않는다).</param>
/// <param name="SystemCliId">Claude Code 가 아닌 시스템 CLI 의 id(쓸 수 없는 줄 포함). 기타 CLI 면 <c>custom:&lt;id&gt;</c>. 프로필·Claude Code 면 null.</param>
/// <param name="CustomCliCommandHash">기타 CLI 의 명령 해시(<see cref="AiCustomCliTarget.CommandHash"/>). 명령만 고쳐도 다른 곳이다. 쓸 수 없는 줄·다른 종류면 null.</param>
/// <param name="LocalAi">프로필이 「로컬 AI」 로 지정되어 있다(<see cref="AiLlmProfile.IsLocalAi"/>, D-045). 값을 가릴지는 대화마다 이 값으로 정하므로, 판단과 실제 전송처가 어긋나지 않는다. 지정을 바꾸면 다른 곳이다.</param>
public sealed record AiConversationTarget(Guid? ProfileId, string Label, string? BaseUrl, AiApiFormat? Format, string? Model, string? SystemCliId = null, string? CustomCliCommandHash = null, bool LocalAi = false)
{
    /// <summary>설정의 활성 대상으로 만든다. 프로필을 찾지 못하거나 모르는 프로필이어도 id 는 그대로 남겨 라우터가 거부하게 한다.</summary>
    public static AiConversationTarget From(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var active = AiActiveProfile.Find(settings);
        return active switch
        {
            { Kind: AiActiveKind.Profile or AiActiveKind.ProfileInvalid, Profile: { } profile } =>
                new AiConversationTarget(profile.Id, profile.Name, profile.BaseUrl.Trim(), profile.Format, profile.Model.Trim(), LocalAi: active.Kind == AiActiveKind.Profile && profile.IsLocalAi),
            { Kind: AiActiveKind.ClaudeCode } => new AiConversationTarget(null, "Claude Code", null, null, null),
            { Kind: AiActiveKind.SystemCli, SystemCli: { } cli } => new AiConversationTarget(null, cli.DisplayName, null, null, null, cli.Id),
            { Kind: AiActiveKind.CustomCli, CustomCli: { } custom } =>
                new AiConversationTarget(null, custom.Name.Trim().Length > 0 ? custom.Name.Trim() : custom.Id, null, null, null, active.SystemCliId, AiCustomCliTarget.CommandHash(custom.Command)),
            { Kind: AiActiveKind.CustomCliUnavailable } => new AiConversationTarget(null, "(사용할 수 없는 기타 CLI)", null, null, null, active.SystemCliId),
            { Kind: AiActiveKind.SystemCliUnavailable } => new AiConversationTarget(null, "(사용할 수 없는 시스템 CLI)", null, null, null, active.SystemCliId),
            { Kind: AiActiveKind.None } => new AiConversationTarget(settings.ActiveProfileId, "(AI 선택 안 함)", null, null, null),
            _ => new AiConversationTarget(settings.ActiveProfileId, "(찾을 수 없는 프로필)", null, null, null),
        };
    }

    public AiProfileExpectation ToExpectation() => new(ProfileId, BaseUrl, Format, SystemCliId, CustomCliCommandHash, LocalAi);

    /// <summary>
    /// 확인 창·감사 기록에 보일 주소(Claude Code 는 null). 사용자 정보(<c>user:pw@</c>)·쿼리·조각은 빼고 형식에 맞게 정규화한 기준 주소만 쓴다:
    /// 설정에 키가 섞인 주소가 있어도(라우터가 거부하기 전에) 화면·기록으로 새지 않게 한다. 주소를 읽을 수 없으면 null.
    /// </summary>
    public string? RecipientUrl => Format is { } format && !string.IsNullOrWhiteSpace(BaseUrl) ? AiProfileUrl.BaseFor(format, BaseUrl) : null;

    /// <summary>같은 곳이다: id·주소·형식·모델(기타 CLI 는 명령 해시)과 「로컬 AI」 지정이 모두 같다.</summary>
    public bool IsSameAs(AiConversationTarget other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ProfileId == other.ProfileId
            && string.Equals(SystemCliId, other.SystemCliId, StringComparison.Ordinal)
            && string.Equals(CustomCliCommandHash, other.CustomCliCommandHash, StringComparison.Ordinal)
            && string.Equals(BaseUrl, other.BaseUrl, StringComparison.Ordinal)
            && Format == other.Format
            && string.Equals(Model, other.Model, StringComparison.Ordinal)
            && LocalAi == other.LocalAi;
    }
}
