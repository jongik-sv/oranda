namespace Oranda.Core.Features;

/// <summary>설정 「기능 세부」 의 묶음 5개(PRD 12.2, TRD 10.4). 이름 문구 키는 <see cref="FeatureCatalog.GroupNameKey"/>.</summary>
public enum FeatureGroup
{
    /// <summary>개발 보조(D1~D8).</summary>
    DevAssist,

    /// <summary>PL/SQL(P1~P3).</summary>
    Plsql,

    /// <summary>MyBatis(M1).</summary>
    MyBatis,

    /// <summary>튜닝(T1~T7).</summary>
    Tuning,

    /// <summary>운영(A1~A5).</summary>
    Operations,
}

/// <summary>기능 하나의 카탈로그 데이터(TRD 10.4).</summary>
/// <param name="Id"><see cref="FeatureIds"/> 의 값.</param>
/// <param name="Group">설정 창 묶음.</param>
/// <param name="StartupBound">시작 때 도는 기능. 실제 켬 = 앱 시작 때 켬 ∧ 지금 켬, 다르면 PendingRestart(TRD 10.5-5).</param>
/// <param name="WritesOnly">조회 전용 Role 하나만 고르면 켤 수 없는 기능(D-006, RL-08).</param>
/// <param name="Requires">이 기능이 켜지려면 먼저 켜져 있어야 하는 기능 id(TRD 10.5-4).</param>
public sealed record FeatureDefinition(string Id, FeatureGroup Group, bool StartupBound, bool WritesOnly, IReadOnlyList<string> Requires)
{
    /// <summary>화면 이름 문구 키(<c>Feature.&lt;id&gt;</c>, strings-ko/en).</summary>
    public string NameKey => FeatureTexts.FeatureNamePrefix + Id;

    public bool Equals(FeatureDefinition? other) =>
        other is not null
        && Id == other.Id
        && Group == other.Group
        && StartupBound == other.StartupBound
        && WritesOnly == other.WritesOnly
        && Requires.SequenceEqual(other.Requires);

    public override int GetHashCode() => HashCode.Combine(Id, Group, StartupBound, WritesOnly, Requires.Count);
}

/// <summary>Role 하나의 카탈로그 데이터: 고르면 기본으로 켜지는 기능(PRD 12.2 의 ○).</summary>
/// <param name="Id"><see cref="RoleIds"/> 의 값.</param>
/// <param name="DefaultFeatures">기본 켬 기능 id(<see cref="FeatureIds.All"/> 순서).</param>
public sealed record RoleDefinition(string Id, IReadOnlyList<string> DefaultFeatures)
{
    /// <summary>화면 이름 문구 키(<c>Role.&lt;id&gt;</c>).</summary>
    public string NameKey => FeatureTexts.RoleNamePrefix + Id;

    /// <summary>한 줄 설명 문구 키(<c>Role.&lt;id&gt;.desc</c>).</summary>
    public string DescriptionKey => FeatureTexts.RoleNamePrefix + Id + ".desc";

    public bool Equals(RoleDefinition? other) =>
        other is not null && Id == other.Id && DefaultFeatures.SequenceEqual(other.DefaultFeatures);

    public override int GetHashCode() => HashCode.Combine(Id, DefaultFeatures.Count);
}

/// <summary>네임스페이스 꼬리표 한 줄(TRD 10.2 첫째 표).</summary>
/// <param name="Prefix"><c>Oranda.App.Features.</c> 다음 부분(예: <c>Tuning.Assistant</c>).</param>
/// <param name="FeatureId">그 네임스페이스(와 하위)의 등록 항목이 드는 기능.</param>
public sealed record FeatureNamespaceTag(string Prefix, string FeatureId)
{
    /// <summary>비교할 네임스페이스 전체(<c>Oranda.App.Features.Tuning.Assistant</c>).</summary>
    public string Namespace => FeatureCatalog.AppFeaturesNamespace + "." + Prefix;
}
