using Oranda.Core.Connection;

namespace Oranda.Core.Ai.Access;

/// <summary>
/// 접속의 AI 허용 수준 계산·검증(연구서 §6.4).
/// 운영 라벨은 기본·최대 모두 <see cref="AiAccessLevel.Query"/>(SELECT 실행은 되지만 행 값은 운영에서 보내지 않는다). 접속별로 <see cref="AiAccessLevel.Off"/> 로 끌 수 있다.
/// 운영 접속에 저장된 <see cref="AiAccessLevel.QueryRows"/>(옛 파일·손으로 고친 파일)는 한 칸이 아니라 <see cref="AiAccessLevel.Plan"/> 으로 줄여 읽는다: 행 값을 못 보내는 쪽으로 닫는다.
/// 개발·시험은 기본 <see cref="AiAccessLevel.Plan"/>, 최대 <see cref="AiAccessLevel.QueryRows"/>.
/// 프로필의 <see cref="ConnectionProfile.AiAccess"/> 가 null 이면 환경 기본값이고, 한도를 넘는 값은 한도로 줄여 읽는다.
/// </summary>
public static class AiAccessPolicy
{
    /// <summary>환경 라벨의 기본 수준.</summary>
    public static AiAccessLevel DefaultLevel(ConnectionEnvironment environment) =>
        environment == ConnectionEnvironment.Production ? AiAccessLevel.Query : AiAccessLevel.Plan;

    /// <summary>환경 라벨이 허용하는 최대 수준.</summary>
    public static AiAccessLevel MaxLevel(ConnectionEnvironment environment) =>
        environment == ConnectionEnvironment.Production ? AiAccessLevel.Query : AiAccessLevel.QueryRows;

    /// <summary>환경 한도를 넘는 수준을 읽을 때의 값: 운영의 <see cref="AiAccessLevel.QueryRows"/> 는 <see cref="AiAccessLevel.Plan"/>, 그 밖에는 그대로.</summary>
    private static AiAccessLevel Limit(ConnectionEnvironment environment, AiAccessLevel level) =>
        level > MaxLevel(environment) ? AiAccessLevel.Plan : level;

    /// <summary>실제로 적용되는 수준: (프로필 값 ?? 환경 기본값) 을 환경 한도로 줄인 값. 도구 호출은 항상 이 값으로 검사한다.</summary>
    public static AiAccessLevel Effective(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var level = profile.AiAccess ?? DefaultLevel(profile.Environment);

        // 정의되지 않은 값(손으로 고친 파일의 숫자 등)은 가장 좁은 수준으로 본다.
        if (!Enum.IsDefined(level))
        {
            return AiAccessLevel.Off;
        }

        return Limit(profile.Environment, level);
    }

    /// <summary>AI 의 EXPLAIN PLAN 허용 여부: 프로필 값 ?? 켬(운영도 기본 켬, 접속별로 끌 수 있다). 수준이 <see cref="AiAccessLevel.Plan"/> 미만이면 어차피 거부된다.</summary>
    public static bool ExplainPlanEnabled(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.AllowExplainPlan ?? true;
    }

    /// <summary>
    /// SQL 글의 리터럴 가림이 필요한가: 운영 접속이면 항상 참(끌 수 없다, 접속별 칸 없음), 아니면 설정 <paramref name="setting"/>
    /// (<c>AiSettings.MaskLiterals</c>).
    /// </summary>
    public static bool MaskLiterals(ConnectionProfile profile, bool setting)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Environment == ConnectionEnvironment.Production || setting;
    }

    /// <summary>수준이 <paramref name="required"/> 이상이고 <see cref="AiAccessLevel.Off"/> 가 아닌가.</summary>
    public static bool Allows(ConnectionProfile profile, AiAccessLevel required) =>
        required != AiAccessLevel.Off && Effective(profile) >= required;

    /// <summary>
    /// 접속 편집에서 저장할 때 접속 이름을 다시 입력받아야 하는가(SAFE-08 과 같은 방식).
    /// 사용자가 수준을 직접 골랐을 때만 묻는다: 저장 뒤 운영 접속에 명시한 수준(<see cref="ConnectionProfile.AiAccess"/> 가 null 이 아님)이 있고
    /// 그 <b>실효 수준</b>이 <see cref="AiAccessLevel.Off"/> 가 아니며, 저장 전에는 운영이 아니었거나 실효 수준이 지금보다 낮았을 때 참이다.
    /// 환경 기본값 때문에 새 운영 접속을 저장할 때마다 묻지는 않는다. 환경 라벨을 운영으로 바꾸는 편집도 수준이 명시돼 있으면 잡는다
    /// (개발에서 Plan 으로 켜 둔 접속의 라벨을 바꿔도 묻는다).
    /// </summary>
    public static bool NeedsNameConfirmation(ConnectionProfile before, ConnectionProfile after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return after.Environment == ConnectionEnvironment.Production
            && after.AiAccess is not null
            && Effective(after) != AiAccessLevel.Off
            && (before.Environment != ConnectionEnvironment.Production || Effective(after) > Effective(before));
    }

    /// <summary>저장하기 전에 한도를 넘는 값을 한도로 줄인다(옛 파일·손으로 고친 파일 방어).</summary>
    public static ConnectionProfile Clamp(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.AiAccess is { } level && level > MaxLevel(profile.Environment)
            ? profile with { AiAccess = Limit(profile.Environment, level) }
            : profile;
    }

    /// <summary>프로필의 AI 설정 문제 목록(사람이 읽는 문장). 비었으면 정상.</summary>
    public static IReadOnlyList<string> Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>();
        var label = string.IsNullOrWhiteSpace(profile.Name) ? "(이름 없음)" : profile.Name;

        if (profile.AiAccess is { } level)
        {
            if (!Enum.IsDefined(level))
            {
                errors.Add($"'{label}': 알 수 없는 AI 허용 수준이다.");
            }
            else if (level > MaxLevel(profile.Environment))
            {
                errors.Add($"'{label}': {profile.Environment} 접속의 AI 허용 수준은 {MaxLevel(profile.Environment)} 까지다.");
            }
        }

        return errors;
    }
}
