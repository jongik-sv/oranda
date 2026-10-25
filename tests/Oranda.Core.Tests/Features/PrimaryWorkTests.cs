using Oranda.Core.Features;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Features;

/// <summary>
/// TSK-09-08 주 업무 변환(TRD 11.4 표, D-017): 설정 → 화면 값(<see cref="PrimaryWork.FromSettings"/>), 화면 값 → 저장 형식(<see cref="PrimaryWork.Apply"/>),
/// 기존 features 모양 왕복(해석이 바뀌지 않음), 덧붙임 규칙·문구 키.
/// </summary>
public sealed class PrimaryWorkTests
{
    private static FeaturesSettings Roles(params string[] roles) => new() { Roles = roles };

    private static void AssertSet(IEnumerable<string> expected, IEnumerable<string>? actual) =>
        Assert.True(new HashSet<string>(expected).SetEquals(actual ?? []), $"expected [{string.Join(",", expected)}] actual [{string.Join(",", actual ?? [])}]");

    // ---- 설정 → 화면 ----

    [Fact]
    public void Nothing_chosen_reads_as_sql_development_without_addons()
    {
        Assert.Equal(new PrimaryWorkChoice(PrimaryWorkKind.SqlDev), PrimaryWork.FromSettings(null));
        Assert.Equal(new PrimaryWorkChoice(PrimaryWorkKind.SqlDev), PrimaryWork.FromSettings(new FeaturesSettings()));
    }

    [Fact]
    public void All_features_and_the_legacy_all_features_file_read_as_all()
    {
        Assert.Equal(PrimaryWorkKind.All, PrimaryWork.FromSettings(new FeaturesSettings { AllFeatures = true }).Primary);
        Assert.Equal(PrimaryWorkKind.All, PrimaryWork.FromSettings(new FeaturesSettings { AllFeatures = true, Roles = [RoleIds.Viewer] }).Primary); // allFeatures 가 이긴다
        Assert.Equal(PrimaryWorkKind.All, PrimaryWork.FromSettings(new FeaturesSettings { Disabled = [FeatureIds.Trace] }).Primary); // 옛 「모든 기능 쓰기」
        Assert.Equal(PrimaryWorkKind.All, PrimaryWork.FromSettings(new FeaturesSettings { Enabled = ["futureFeature"] }).Primary);
    }

    [Theory]
    [InlineData(new[] { "sqlDev" }, PrimaryWorkKind.SqlDev, false, false)]
    [InlineData(new[] { "tuner" }, PrimaryWorkKind.Tuner, false, false)]
    [InlineData(new[] { "dba" }, PrimaryWorkKind.Dba, false, false)]
    [InlineData(new[] { "viewer" }, PrimaryWorkKind.Viewer, false, false)]
    [InlineData(new[] { "sqlDev", "plsqlDev" }, PrimaryWorkKind.SqlDev, true, false)]
    [InlineData(new[] { "myBatisDev", "sqlDev" }, PrimaryWorkKind.SqlDev, false, true)]
    [InlineData(new[] { "tuner", "plsqlDev", "myBatisDev" }, PrimaryWorkKind.Tuner, true, true)]
    [InlineData(new[] { "dba", "plsqlDev" }, PrimaryWorkKind.Dba, true, false)]
    [InlineData(new[] { "sqlDev", "sqlDev", "plsqlDev" }, PrimaryWorkKind.SqlDev, true, false)] // 중복은 하나로 본다
    public void One_primary_role_with_a_subset_of_the_addons_reads_as_that_choice(string[] roles, PrimaryWorkKind primary, bool plsql, bool myBatis)
    {
        var choice = PrimaryWork.FromSettings(Roles(roles));

        Assert.Equal(primary, choice.Primary);
        Assert.Equal(plsql, choice.Plsql);
        Assert.Equal(myBatis, choice.MyBatis);
        Assert.Empty(choice.CustomRoles);
    }

    [Theory]
    [InlineData("tuner,dba")] // 주 업무 2개
    [InlineData("sqlDev,tuner,plsqlDev")]
    [InlineData("plsqlDev")] // 주 업무 0개
    [InlineData("myBatisDev,plsqlDev")]
    [InlineData("")] // Role 0개(공통만)
    [InlineData("sqlDev,futureRole")] // 모르는 id
    [InlineData("futureRole")]
    [InlineData("viewer,plsqlDev")] // 조회만 + 덧붙임(차단이 풀린 조합)
    [InlineData("viewer,sqlDev")]
    public void Any_other_combination_reads_as_custom_and_keeps_the_roles_as_they_are(string list)
    {
        var roles = list.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var choice = PrimaryWork.FromSettings(Roles(roles));

        Assert.Equal(PrimaryWorkKind.Custom, choice.Primary);
        Assert.Equal(roles, choice.CustomRoles);
        Assert.Equal(roles.Contains(RoleIds.PlsqlDev), choice.Plsql);
        Assert.Equal(roles.Contains(RoleIds.MyBatisDev), choice.MyBatis);
        Assert.Equal(roles, PrimaryWork.RolesFor(choice)); // 그대로 두기
    }

    // ---- 화면 → 저장 형식 ----

    [Fact]
    public void The_save_table_matches_the_decision()
    {
        // D-017: SQL 개발 + PL/SQL → ["sqlDev","plsqlDev"], 모든 기능 → allFeatures: true.
        var sqlPlsql = PrimaryWork.Apply(null, new PrimaryWorkChoice(PrimaryWorkKind.SqlDev, Plsql: true));
        Assert.Equal([RoleIds.SqlDev, RoleIds.PlsqlDev], sqlPlsql.Roles);
        Assert.False(sqlPlsql.AllFeatures);

        var all = PrimaryWork.Apply(null, new PrimaryWorkChoice(PrimaryWorkKind.All, Plsql: true, MyBatis: true));
        Assert.True(all.AllFeatures);
        Assert.Null(all.Roles);

        Assert.Equal([RoleIds.Tuner, RoleIds.PlsqlDev, RoleIds.MyBatisDev], PrimaryWork.Apply(null, new PrimaryWorkChoice(PrimaryWorkKind.Tuner, true, true)).Roles);
        Assert.Equal([RoleIds.Dba, RoleIds.MyBatisDev], PrimaryWork.Apply(null, new PrimaryWorkChoice(PrimaryWorkKind.Dba, MyBatis: true)).Roles);
        Assert.Equal([RoleIds.SqlDev], PrimaryWork.Apply(null, new PrimaryWorkChoice(PrimaryWorkKind.SqlDev)).Roles);

        // 조회만에는 덧붙임을 섞지 않는다(D-006 차단 유지).
        var viewer = PrimaryWork.Apply(null, new PrimaryWorkChoice(PrimaryWorkKind.Viewer, true, true));
        Assert.Equal([RoleIds.Viewer], viewer.Roles);
        Assert.True(FeatureCatalog.IsReadOnly(viewer));
    }

    [Fact]
    public void Choosing_sql_development_on_a_fresh_install_is_a_choice_not_the_default()
    {
        // 시작 화면 카드의 「SQL 개발」 단추: 해석은 같지만 「고른 적 없음」 에서 벗어난다(카드가 사라진다, D-018).
        var saved = PrimaryWork.Apply(new FeaturesSettings(), new PrimaryWorkChoice(PrimaryWorkKind.SqlDev));

        Assert.False(FeatureCatalog.IsUnchosen(saved));
        AssertSet(FeatureCatalog.Resolve(new FeaturesSettings()), FeatureCatalog.Resolve(saved));
    }

    [Fact]
    public void Custom_keeps_the_settings_untouched()
    {
        var current = new FeaturesSettings { Roles = [RoleIds.Tuner, RoleIds.Dba, "futureRole"], Enabled = [FeatureIds.MyBatisLog, FeatureIds.Trace], Disabled = ["x"] };

        Assert.Same(current, PrimaryWork.Apply(current, PrimaryWork.FromSettings(current)));
    }

    [Fact]
    public void Overrides_that_equal_the_new_defaults_are_dropped_and_unknown_ids_are_kept()
    {
        var current = new FeaturesSettings
        {
            Roles = [RoleIds.SqlDev],
            Enabled = [FeatureIds.Trace, FeatureIds.MyBatisLog, "futureOn"],
            Disabled = [FeatureIds.GridEdit, FeatureIds.Flashback, "futureOff"],
        };

        // 튜닝: trace 는 기본 켬이라 직접 켬에서 빠지고, gridEdit·flashback 은 기본 꺼짐이라 직접 끔에서 빠진다.
        var saved = PrimaryWork.Apply(current, new PrimaryWorkChoice(PrimaryWorkKind.Tuner));

        AssertSet([FeatureIds.MyBatisLog, "futureOn"], saved.Enabled);
        AssertSet(["futureOff"], saved.Disabled);

        // 모든 기능: 직접 켬은 모두 기본과 같아 빠지고 직접 끔은 남는다.
        var all = PrimaryWork.Apply(current, new PrimaryWorkChoice(PrimaryWorkKind.All));
        AssertSet(["futureOn"], all.Enabled);
        AssertSet([FeatureIds.GridEdit, FeatureIds.Flashback, "futureOff"], all.Disabled);
    }

    [Fact]
    public void Addons_are_only_saved_for_primary_works_that_allow_them()
    {
        Assert.True(PrimaryWork.AllowsAddons(PrimaryWorkKind.SqlDev));
        Assert.True(PrimaryWork.AllowsAddons(PrimaryWorkKind.Tuner));
        Assert.True(PrimaryWork.AllowsAddons(PrimaryWorkKind.Dba));
        Assert.False(PrimaryWork.AllowsAddons(PrimaryWorkKind.All));
        Assert.False(PrimaryWork.AllowsAddons(PrimaryWorkKind.Viewer));
        Assert.False(PrimaryWork.AllowsAddons(PrimaryWorkKind.Custom));
        Assert.Null(PrimaryWork.RolesFor(new PrimaryWorkChoice(PrimaryWorkKind.All, true, true)));
    }

    // ---- 왕복 ----

    public static TheoryData<FeaturesSettings?> ExistingShapes()
    {
        var data = new TheoryData<FeaturesSettings?>
        {
            null,
            new FeaturesSettings(),
            new FeaturesSettings { AllFeatures = true },
            new FeaturesSettings { AllFeatures = true, Disabled = [FeatureIds.Trace] },
            new FeaturesSettings { Disabled = [FeatureIds.Trace] },
            new FeaturesSettings { Roles = [] },
            new FeaturesSettings { Roles = ["futureRole"], Enabled = ["futureOn"] },
            new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.Tuner], Enabled = [FeatureIds.MyBatis], Disabled = [FeatureIds.GridEdit] },
        };

        // Role 1~6개 조합 전부(64가지 중 빈 목록 제외).
        var all = RoleIds.All;
        for (var mask = 1; mask < 1 << all.Count; mask++)
        {
            data.Add(new FeaturesSettings { Roles = [.. all.Where((_, i) => (mask & (1 << i)) != 0)] });
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExistingShapes))]
    public void Reading_and_saving_an_untouched_choice_keeps_what_turns_on(FeaturesSettings? shape)
    {
        var choice = PrimaryWork.FromSettings(shape);
        var saved = PrimaryWork.Apply(shape, choice);

        AssertSet(FeatureCatalog.Resolve(shape), FeatureCatalog.Resolve(saved));
        Assert.Equal(FeatureCatalog.IsReadOnly(shape), FeatureCatalog.IsReadOnly(saved));
        Assert.Equal(choice, PrimaryWork.FromSettings(saved)); // 다시 읽어도 같은 화면

        if (shape is { Roles: not null, AllFeatures: false })
        {
            AssertSet(shape.Roles, saved.Roles); // 고른 Role 조합은 바뀌지 않는다
        }
    }

    // ---- 문구 키 ----

    [Fact]
    public void Every_choice_has_a_name_key_and_a_description_key()
    {
        Assert.Equal([PrimaryWorkKind.All, PrimaryWorkKind.SqlDev, PrimaryWorkKind.Tuner, PrimaryWorkKind.Dba, PrimaryWorkKind.Viewer], PrimaryWork.Choices);
        Assert.Equal(PrimaryWork.Choices.Count, PrimaryWork.Choices.Select(PrimaryWork.NameKey).Distinct().Count());
        Assert.All(PrimaryWork.Choices, k => Assert.NotNull(PrimaryWork.DescriptionKey(k)));
        Assert.Equal("Role.tuner.desc", PrimaryWork.DescriptionKey(PrimaryWorkKind.Tuner));
        Assert.Equal(FeatureTexts.PrimaryAllDesc, PrimaryWork.DescriptionKey(PrimaryWorkKind.All));
        Assert.Equal(FeatureTexts.PrimaryCustom, PrimaryWork.NameKey(PrimaryWorkKind.Custom));
        Assert.Null(PrimaryWork.DescriptionKey(PrimaryWorkKind.Custom));
        Assert.Null(PrimaryWork.RoleOf(PrimaryWorkKind.All));
        Assert.Equal(RoleIds.Viewer, PrimaryWork.RoleOf(PrimaryWorkKind.Viewer));
    }
}
