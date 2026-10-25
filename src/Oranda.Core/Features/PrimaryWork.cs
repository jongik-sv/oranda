using Oranda.Core.Settings;

namespace Oranda.Core.Features;

/// <summary>설정 「기능」 탭·시작 화면 카드의 주 업무(D-017). <see cref="Custom"/> = 주 업무 모양에 맞지 않는 기존 Role 조합.</summary>
public enum PrimaryWorkKind
{
    /// <summary>모든 기능(<c>allFeatures: true</c>).</summary>
    All,

    /// <summary>SQL 개발(R1).</summary>
    SqlDev,

    /// <summary>튜닝(R4).</summary>
    Tuner,

    /// <summary>운영 DBA(R5).</summary>
    Dba,

    /// <summary>조회만(R6). 덧붙임을 섞지 않는다(D-006).</summary>
    Viewer,

    /// <summary>지금 조합 그대로(주 업무 0개·2개 이상·<c>[]</c>·모르는 id·조회만 + 덧붙임).</summary>
    Custom,
}

/// <summary>
/// 화면에서 고른 값: 주 업무 하나 + 덧붙임 2개(PL/SQL·MyBatis). <see cref="CustomRoles"/> 는 <see cref="PrimaryWorkKind.Custom"/> 일 때
/// 설정에 있던 Role 목록 그대로다(보존용). Custom 의 덧붙임 값은 그 목록에 plsqlDev·myBatisDev 가 있는지(표시용)다.
/// </summary>
public sealed record PrimaryWorkChoice(PrimaryWorkKind Primary, bool Plsql = false, bool MyBatis = false)
{
    public IReadOnlyList<string> CustomRoles { get; init; } = [];

    public bool Equals(PrimaryWorkChoice? other) =>
        other is not null && Primary == other.Primary && Plsql == other.Plsql && MyBatis == other.MyBatis && CustomRoles.SequenceEqual(other.CustomRoles);

    public override int GetHashCode() => HashCode.Combine(Primary, Plsql, MyBatis, CustomRoles.Count);
}

/// <summary>
/// 「주 업무 하나 + 덧붙임」 과 설정 <c>features</c> 사이의 순수 변환(TSK-09-08, TRD 11.4, D-017). 저장 형식(<c>roles</c>·<c>allFeatures</c>)과
/// 해석 규칙(<see cref="FeatureCatalog.EffectiveRoles"/>, D-002 합집합·D-013 기본 Role)은 그대로 쓰고 바꾸지 않는다.
/// </summary>
public static class PrimaryWork
{
    /// <summary>라디오·카드 단추에 보이는 주 업무 5개(순서 그대로).</summary>
    public static IReadOnlyList<PrimaryWorkKind> Choices { get; } =
        [PrimaryWorkKind.All, PrimaryWorkKind.SqlDev, PrimaryWorkKind.Tuner, PrimaryWorkKind.Dba, PrimaryWorkKind.Viewer];

    private static readonly string[] PrimaryRoles = [RoleIds.SqlDev, RoleIds.Tuner, RoleIds.Dba, RoleIds.Viewer];

    /// <summary>주 업무의 Role id. 모든 기능·Custom 은 null.</summary>
    public static string? RoleOf(PrimaryWorkKind kind) => kind switch
    {
        PrimaryWorkKind.SqlDev => RoleIds.SqlDev,
        PrimaryWorkKind.Tuner => RoleIds.Tuner,
        PrimaryWorkKind.Dba => RoleIds.Dba,
        PrimaryWorkKind.Viewer => RoleIds.Viewer,
        _ => null,
    };

    /// <summary>이름 문구 키(<see cref="FeatureTexts"/>). Custom 은 <see cref="FeatureTexts.PrimaryCustom"/>(형식 문자열).</summary>
    public static string NameKey(PrimaryWorkKind kind) => kind switch
    {
        PrimaryWorkKind.All => FeatureTexts.PrimaryAll,
        PrimaryWorkKind.SqlDev => FeatureTexts.PrimarySqlDev,
        PrimaryWorkKind.Tuner => FeatureTexts.PrimaryTuner,
        PrimaryWorkKind.Dba => FeatureTexts.PrimaryDba,
        PrimaryWorkKind.Viewer => FeatureTexts.PrimaryViewer,
        _ => FeatureTexts.PrimaryCustom,
    };

    /// <summary>한 줄 설명 문구 키. Role 이 있는 주 업무는 그 Role 의 설명(<c>Role.&lt;id&gt;.desc</c>)을 그대로 쓴다. Custom 은 null.</summary>
    public static string? DescriptionKey(PrimaryWorkKind kind) =>
        kind == PrimaryWorkKind.All ? FeatureTexts.PrimaryAllDesc
        : RoleOf(kind) is { } role ? FeatureCatalog.FindRole(role)!.DescriptionKey
        : null;

    /// <summary>덧붙임을 켤 수 있는 주 업무인가(모든 기능 = 이미 포함, 조회만 = 차단이 풀림, Custom = 조합 그대로).</summary>
    public static bool AllowsAddons(PrimaryWorkKind kind) => kind is PrimaryWorkKind.SqlDev or PrimaryWorkKind.Tuner or PrimaryWorkKind.Dba;

    /// <summary>
    /// 설정을 화면 값으로 읽는다(TRD 11.4 표): 모든 기능(<c>allFeatures</c>·옛 「모든 기능 쓰기」 저장) → All, 아무것도 고른 적 없음 → SqlDev(D-013),
    /// 주 업무 Role 정확히 1개 + 덧붙임 부분집합 → 그 값, 그 밖(조회만 + 덧붙임 포함) → Custom.
    /// </summary>
    public static PrimaryWorkChoice FromSettings(FeaturesSettings? settings)
    {
        if (FeatureCatalog.IsUnchosen(settings))
        {
            return new PrimaryWorkChoice(PrimaryWorkKind.SqlDev);
        }

        var roles = FeatureCatalog.EffectiveRoles(settings);
        if (roles is null)
        {
            return new PrimaryWorkChoice(PrimaryWorkKind.All);
        }

        var distinct = roles.Distinct(StringComparer.Ordinal).ToList();
        var plsql = distinct.Contains(RoleIds.PlsqlDev);
        var myBatis = distinct.Contains(RoleIds.MyBatisDev);
        var primaries = distinct.Where(r => PrimaryRoles.Contains(r)).ToList();
        var others = distinct.Count - primaries.Count - (plsql ? 1 : 0) - (myBatis ? 1 : 0);
        if (primaries.Count == 1 && others == 0 && !(primaries[0] == RoleIds.Viewer && (plsql || myBatis)))
        {
            var kind = Choices.First(k => RoleOf(k) == primaries[0]);
            return new PrimaryWorkChoice(kind, plsql, myBatis);
        }

        return new PrimaryWorkChoice(PrimaryWorkKind.Custom, plsql, myBatis) { CustomRoles = [.. roles] };
    }

    /// <summary>
    /// 고른 값이 저장할 Role 목록. null = 모든 기능. 주 업무 Role 다음에 덧붙임(plsqlDev·myBatisDev)을 붙인다.
    /// 덧붙임은 <see cref="AllowsAddons"/> 일 때만 들어간다. Custom = 설정에 있던 목록 그대로.
    /// </summary>
    public static IReadOnlyList<string>? RolesFor(PrimaryWorkChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.Primary == PrimaryWorkKind.All)
        {
            return null;
        }

        if (choice.Primary == PrimaryWorkKind.Custom)
        {
            return choice.CustomRoles;
        }

        var roles = new List<string> { RoleOf(choice.Primary)! };
        if (AllowsAddons(choice.Primary))
        {
            if (choice.Plsql)
            {
                roles.Add(RoleIds.PlsqlDev);
            }

            if (choice.MyBatis)
            {
                roles.Add(RoleIds.MyBatisDev);
            }
        }

        return roles;
    }

    /// <summary>
    /// 고른 값을 설정에 적는다(사용자가 직접 고른 것으로 본다). 모든 기능 → <c>allFeatures: true</c>·<c>roles: null</c>, Custom → 지금 설정 그대로,
    /// 그 밖 → <c>allFeatures: false</c>·<c>roles</c> = <see cref="RolesFor"/>. 직접 켬·끔은 새 Role 기본값과 같아진 것만 뺀다(최소 기록, TSK-07-04 규칙).
    /// 이 버전이 모르는 기능 id 는 그대로 둔다.
    /// </summary>
    public static FeaturesSettings Apply(FeaturesSettings? current, PrimaryWorkChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        current ??= new FeaturesSettings();
        if (choice.Primary == PrimaryWorkKind.Custom)
        {
            return current;
        }

        var roles = RolesFor(choice);
        var defaults = FeatureCatalog.RoleDefaults(roles);
        return new FeaturesSettings
        {
            AllFeatures = roles is null,
            Roles = roles,
            Enabled = [.. current.Enabled.Where(id => !FeatureCatalog.IsKnownFeature(id) || !defaults.Contains(id))],
            Disabled = [.. current.Disabled.Where(id => !FeatureCatalog.IsKnownFeature(id) || defaults.Contains(id))],
        };
    }
}
