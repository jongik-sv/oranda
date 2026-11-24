using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;

namespace Oranda.Core.Ai.Access;

/// <summary>
/// 접속 하나의 AI 허용 수준을 두 쪽(Oranda 안의 AI·밖의 AI 에이전트)에서 본 값(ai-settings-one-place 2026-11-24).
/// 두 쪽은 <b>같은 값</b>(<see cref="ConnectionProfile.AiAccess"/>)을 쓴다: 안의 AI 는 실효 수준 그대로, 밖의 에이전트는 min(실효 수준, 외부 상한)
/// (<see cref="AgentGrantPolicy.LevelFor"/>). 화면은 이 값을 보이기만 하고 다시 계산하지 않는다.
/// </summary>
/// <param name="ProfileId">접속 id.</param>
/// <param name="Name">접속 이름.</param>
/// <param name="Environment">환경 라벨.</param>
/// <param name="Level">접속의 실효 수준(<see cref="AiAccessPolicy.Effective"/>) = Oranda 안의 AI 가 받는 수준.</param>
/// <param name="MaxLevel">이 환경에서 고를 수 있는 가장 높은 수준(<see cref="AiAccessPolicy.MaxLevel"/>).</param>
/// <param name="FollowsDefault">값을 고르지 않아 환경 기본값을 따른다(<see cref="ConnectionProfile.AiAccess"/> 가 null).</param>
/// <param name="ExternalCap">외부 상한(<see cref="AgentGrantPolicy.DefaultCap"/>).</param>
/// <param name="ExternalLevel">밖의 AI 에이전트가 받는 수준 = min(<paramref name="Level"/>, <paramref name="ExternalCap"/>).</param>
public sealed record AiConnectionAccess(
    Guid ProfileId,
    string Name,
    ConnectionEnvironment Environment,
    AiAccessLevel Level,
    AiAccessLevel MaxLevel,
    bool FollowsDefault,
    AiAccessLevel ExternalCap,
    AiAccessLevel ExternalLevel)
{
    /// <summary>운영 접속.</summary>
    public bool IsProduction => Environment == ConnectionEnvironment.Production;

    /// <summary>외부 상한 때문에 밖의 에이전트가 접속 수준보다 낮게 받는다(화면에 「(외부 상한)」).</summary>
    public bool ExternalCapped => ExternalLevel < Level;

    /// <summary>운영이거나 상한이 걸렸을 때 화면에 외부 상한을 보인다.</summary>
    public bool ShowsExternalCap => IsProduction || ExternalCapped;

    /// <summary>이 접속에서 고를 수 있는 수준(끔 ~ <see cref="MaxLevel"/>, 순서대로).</summary>
    public IReadOnlyList<AiAccessLevel> Choices => AiConnectionAccessRules.Choices(Environment);

    /// <summary>접속 프로필에서 두 쪽 값을 만든다.</summary>
    public static AiConnectionAccess Of(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var cap = AgentGrantPolicy.DefaultCap(profile.Environment);
        return new AiConnectionAccess(
            profile.Id,
            profile.Name,
            profile.Environment,
            AiAccessPolicy.Effective(profile),
            AiAccessPolicy.MaxLevel(profile.Environment),
            profile.AiAccess is null,
            cap,
            AgentGrantPolicy.LevelFor(profile, cap));
    }
}

/// <summary>공유 허용 수준을 바꿀 때의 결과.</summary>
public enum AiAccessChange
{
    /// <summary>저장했다(같은 값이면 저장하지 않고도 이 값).</summary>
    Saved,

    /// <summary>운영 접속의 수준을 직접 올린다: 접속 이름을 다시 입력해야 한다(접속 편집과 같은 규칙, <see cref="AiAccessPolicy.NeedsNameConfirmation"/>).</summary>
    NeedsNameConfirmation,

    /// <summary>이 환경에서 고를 수 없는 수준이다(운영의 「조회 + 행 값」 등).</summary>
    AboveMaximum,

    /// <summary>접속을 찾지 못했다(그사이 지워짐).</summary>
    NotFound,
}

/// <summary>공유 허용 수준의 규칙(순수 함수). 화면·연결 창·도우미 단계가 같은 규칙을 쓴다.</summary>
public static class AiConnectionAccessRules
{
    private static readonly AiAccessLevel[] All = [AiAccessLevel.Off, AiAccessLevel.Metadata, AiAccessLevel.Plan, AiAccessLevel.Query, AiAccessLevel.QueryRows];

    /// <summary>그 환경에서 고를 수 있는 수준(끔부터 환경 최대까지).</summary>
    public static IReadOnlyList<AiAccessLevel> Choices(ConnectionEnvironment environment) =>
        [.. All.Where(l => l <= AiAccessPolicy.MaxLevel(environment))];

    /// <summary>
    /// <paramref name="level"/> 을 고른 뒤 저장할 프로필. 환경 기본값과 같으면 null(기본값을 따름 — 도우미의 켬과 같은 저장 꼴), 아니면 그 값.
    /// 실효 수준이 이미 같으면 프로필을 그대로 돌려준다(직접 고른 값을 건드리지 않는다).
    /// </summary>
    public static ConnectionProfile Apply(ConnectionProfile profile, AiAccessLevel level)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (AiAccessPolicy.Effective(profile) == level)
        {
            return profile;
        }

        return profile with { AiAccess = level == AiAccessPolicy.DefaultLevel(profile.Environment) ? null : level };
    }

    /// <summary>
    /// 바꿀 수 있는가. <paramref name="confirmName"/> 은 운영 접속의 수준을 직접 올릴 때 다시 입력한 접속 이름(그 밖에는 보지 않는다).
    /// </summary>
    public static AiAccessChange Check(ConnectionProfile profile, AiAccessLevel level, string? confirmName)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(level) || level > AiAccessPolicy.MaxLevel(profile.Environment))
        {
            return AiAccessChange.AboveMaximum;
        }

        var after = Apply(profile, level);
        return AiAccessPolicy.NeedsNameConfirmation(profile, after) && !string.Equals(confirmName, profile.Name, StringComparison.Ordinal)
            ? AiAccessChange.NeedsNameConfirmation
            : AiAccessChange.Saved;
    }
}
