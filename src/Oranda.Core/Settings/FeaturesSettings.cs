namespace Oranda.Core.Settings;

/// <summary>
/// 설정 <c>features</c> 섹션(PRD 12.3 RL-01, TRD 10.3). 아무것도 고른 적 없으면(섹션 없음, 또는 roles 없음·enabled/disabled 빔·allFeatures 아님)
/// 기본 Role = SQL 개발자(D-013). 「모든 기능 쓰기」는 <see cref="AllFeatures"/> 로 따로 적는다. 해석 규칙은 <c>FeatureCatalog.EffectiveRoles</c>.
/// 목록은 순서를 무시하고 내용으로 비교한다(<c>SettingsStore.ChangedSections</c> 가 레코드 <c>!=</c> 로 섹션 바뀜을 본다).
/// 정규화(중복 제거·enabled/disabled 겹침 정리)는 <c>SettingsStore.Normalize</c>. 모르는 id 는 지우지 않는다. 소유: TSK-07-01.
/// </summary>
public sealed record FeaturesSettings
{
    /// <summary><c>SettingsChanged.Section</c> 값.</summary>
    public const string SectionName = "features";

    /// <summary>
    /// 고른 Role id. null = Role 을 고른 적 없음 — enabled/disabled 도 비었으면 기본 Role(SQL 개발자), 직접 켬·끔이 있으면
    /// 옛 「모든 기능 쓰기」 저장(D-013 이전)으로 보고 모든 기능. 빈 목록 = 공통만.
    /// </summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>「모든 기능 쓰기」를 직접 골랐다(D-013). true 면 <see cref="Roles"/> 를 보지 않고 모든 기능이 바탕이다.</summary>
    public bool AllFeatures { get; init; }

    /// <summary>Role 기본값 위에 직접 켠 기능 id.</summary>
    public IReadOnlyList<string> Enabled { get => _enabled; init => _enabled = value ?? []; }

    /// <summary>Role 기본값 위에 직접 끈 기능 id. 같은 id 가 <see cref="Enabled"/> 에도 있으면 끔이 이긴다.</summary>
    public IReadOnlyList<string> Disabled { get => _disabled; init => _disabled = value ?? []; }

    // 키가 빠진 JSON 에서 직렬화기가 넣는 null 은 빈 목록으로 받는다.
    private readonly IReadOnlyList<string> _enabled = [];
    private readonly IReadOnlyList<string> _disabled = [];

    public bool Equals(FeaturesSettings? other) =>
        other is not null
        && AllFeatures == other.AllFeatures
        && SameSet(Roles, other.Roles)
        && SameSet(Enabled, other.Enabled)
        && SameSet(Disabled, other.Disabled);

    // 순서 무시 비교와 맞게 개수(중복 제외)만 섞는다.
    public override int GetHashCode() =>
        HashCode.Combine(AllFeatures, Roles is null ? -1 : Distinct(Roles), Distinct(Enabled), Distinct(Disabled));

    private static int Distinct(IReadOnlyList<string> list) => list.Distinct(StringComparer.Ordinal).Count();

    private static bool SameSet(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return new HashSet<string>(a, StringComparer.Ordinal).SetEquals(b);
    }
}
