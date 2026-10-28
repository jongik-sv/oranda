using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Features;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Testing;

namespace Oranda.Core.Tests.Features;

/// <summary>
/// TSK-07-01 계약: 기능 25개·Role 6개 카탈로그 데이터(PRD 12.2 표와 같음), 꼬리표 규칙(TRD 10.2), 해석 1~4단계(TRD 10.5),
/// <c>features</c> 섹션 JSON 왕복·옛 파일, 시험용 <see cref="FakeFeatureGate"/>.
/// </summary>
public sealed class FeatureCatalogTests
{
    private static readonly string[] StartupBoundIds = ["plsqlWorkspace", "myBatis", "changeJournal", "blockerAlert"];
    private static readonly string[] WritesOnlyIds = ["gridEdit", "dataImport", "plsqlEditor", "plsqlWorkspace", "plsqlRun", "columnReorder"];
    private static readonly string[] ViewerDefaults = [FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.TableSpec];
    private static readonly string[] PlanAdvancedCommands = ["oranda.plan.compare", "oranda.plan.pinForCompare", "oranda.plan.displayCursorOptions"];

    // ---- 카탈로그 데이터 ----

    [Fact]
    public void There_are_26_features_and_6_roles_with_unique_ids_in_table_order()
    {
        Assert.Equal(26, FeatureIds.All.Count);
        Assert.Equal(26, FeatureIds.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(FeatureIds.All, FeatureCatalog.Features.Select(f => f.Id));

        Assert.Equal(["sqlDev", "plsqlDev", "myBatisDev", "tuner", "dba", "viewer"], RoleIds.All);
        Assert.Equal(RoleIds.All, FeatureCatalog.Roles.Select(r => r.Id));
    }

    [Fact]
    public void Feature_constants_match_the_ids_written_in_settings_files()
    {
        // 설정 파일·문맥 키에 저장되는 문자열이라 값이 바뀌면 안 된다.
        string[] expected =
        [
            "gridEdit", "lobRowView", "dataExport", "dataImport", "dedicatedSession", "flashback", "changeJournal", "tableSpec", "myBatisLog",
            "plsqlEditor", "plsqlWorkspace", "plsqlRun", "myBatis",
            "planAdvanced", "sqlMonitor", "tuningAssistant", "childCursors", "tableStats", "trace", "tuningReport", "aiTuning",
            "sessionMonitor", "lockMonitor", "blockerAlert", "dbTools", "columnReorder",
        ];
        Assert.Equal(expected, FeatureIds.All);
        Assert.Equal("feature.myBatis", FeatureIds.ContextKey(FeatureIds.MyBatis));
        Assert.Throws<ArgumentException>(() => FeatureIds.ContextKey(" "));
    }

    [Fact]
    public void Role_default_table_is_the_same_as_prd_12_2()
    {
        var prd = ReadPrdRoleTable();
        Assert.Equal(26, prd.Count);
        Assert.Equal(FeatureIds.All, prd.Keys);
        foreach (var role in FeatureCatalog.Roles)
        {
            var column = RoleIds.All.ToList().IndexOf(role.Id);
            var expected = FeatureIds.All.Where(f => prd[f][column]).ToList();
            Assert.Equal(expected, role.DefaultFeatures);
        }
    }

    [Fact]
    public void Groups_flags_and_requires_follow_trd_10_4()
    {
        Assert.Equal(9, Count(FeatureGroup.DevAssist));
        Assert.Equal(3, Count(FeatureGroup.Plsql));
        Assert.Equal(1, Count(FeatureGroup.MyBatis));
        Assert.Equal(8, Count(FeatureGroup.Tuning));
        Assert.Equal(5, Count(FeatureGroup.Operations));

        Assert.Equal(StartupBoundIds.Order(), Ids(f => f.StartupBound).Order());
        Assert.Equal(WritesOnlyIds.Order(), Ids(f => f.WritesOnly).Order());

        var requiring = Assert.Single(FeatureCatalog.Features, f => f.Requires.Count > 0);
        Assert.Equal(FeatureIds.PlsqlWorkspace, requiring.Id);
        Assert.Equal([FeatureIds.PlsqlEditor], requiring.Requires);

        static int Count(FeatureGroup g) => FeatureCatalog.Features.Count(f => f.Group == g);
        static IEnumerable<string> Ids(Func<FeatureDefinition, bool> p) => FeatureCatalog.Features.Where(p).Select(f => f.Id);
    }

    [Fact]
    public void Catalog_never_refers_to_an_unknown_feature_or_role()
    {
        foreach (var f in FeatureCatalog.Features)
        {
            Assert.All(f.Requires, r => Assert.True(FeatureCatalog.IsKnownFeature(r), f.Id + " → " + r));
            Assert.DoesNotContain(f.Id, f.Requires);
        }

        foreach (var r in FeatureCatalog.Roles)
        {
            Assert.All(r.DefaultFeatures, id => Assert.True(FeatureCatalog.IsKnownFeature(id), r.Id + " → " + id));
            Assert.Equal(r.DefaultFeatures.Count, r.DefaultFeatures.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.All(FeatureCatalog.NamespaceTags, t => Assert.True(FeatureCatalog.IsKnownFeature(t.FeatureId), t.Prefix));
        Assert.All(FeatureCatalog.IdOverrides.Values.OfType<string>(), id => Assert.True(FeatureCatalog.IsKnownFeature(id), id));
        Assert.All(FeatureCatalog.TypeOverrides.Values.OfType<string>(), id => Assert.True(FeatureCatalog.IsKnownFeature(id), id));
        Assert.Equal(FeatureCatalog.NamespaceTags.Count, FeatureCatalog.NamespaceTags.Select(t => t.Prefix).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>「모든 기능」·Role 합집합이 켜는 기능: 직접 켜는 기능(OptIn)을 뺀 것.</summary>
    private static IReadOnlyList<string> OnByDefault { get; } = [.. FeatureIds.All.Where(id => !FeatureCatalog.Find(id)!.OptIn)];

    [Fact]
    public void Ai_tuning_is_opt_in_so_only_an_explicit_enable_turns_it_on()
    {
        var ai = FeatureCatalog.Find(FeatureIds.AiTuning)!;
        Assert.True(ai.OptIn);
        Assert.Equal(FeatureGroup.Tuning, ai.Group);
        Assert.Equal(FeatureIds.AiTuning, FeatureCatalog.FeatureFor(null, "Oranda.App.Features.Ai.AiTuningCommands", "Oranda.App.Features.Ai"));
        Assert.Equal(FeatureIds.AiTuning, FeatureCatalog.FeatureFor(null, "Oranda.App.Features.Ai.Chat.X", "Oranda.App.Features.Ai.Chat"));
        Assert.Equal(FeatureIds.TuningAssistant, FeatureCatalog.FeatureFor(null, "Oranda.App.Features.Tuning.Assistant.X", "Oranda.App.Features.Tuning.Assistant"));
        Assert.DoesNotContain(FeatureCatalog.Roles, r => r.DefaultFeatures.Contains(FeatureIds.AiTuning));

        Assert.DoesNotContain(FeatureIds.AiTuning, FeatureCatalog.Resolve(new FeaturesSettings { AllFeatures = true }));
        Assert.DoesNotContain(FeatureIds.AiTuning, FeatureCatalog.Resolve(new FeaturesSettings { Roles = [.. RoleIds.All] }));
        Assert.DoesNotContain(FeatureIds.AiTuning, FeatureCatalog.Resolve(null));
        Assert.Contains(FeatureIds.AiTuning, FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.Tuner], Enabled = [FeatureIds.AiTuning] }));
        Assert.Contains(FeatureIds.AiTuning, FeatureCatalog.Resolve(new FeaturesSettings { AllFeatures = true, Enabled = [FeatureIds.AiTuning] }));
        Assert.DoesNotContain(FeatureIds.AiTuning, FeatureCatalog.Resolve(new FeaturesSettings { AllFeatures = true, Enabled = [FeatureIds.AiTuning], Disabled = [FeatureIds.AiTuning] }));
    }

    [Fact]
    public void Every_feature_is_reachable_from_some_role_and_every_role_has_defaults()
    {
        var union = FeatureCatalog.RoleDefaults(RoleIds.All);
        Assert.Equal(OnByDefault.Order(), union.Order());
        Assert.All(FeatureCatalog.Roles, r => Assert.NotEmpty(r.DefaultFeatures));
    }

    [Fact]
    public void Text_keys_follow_the_reserved_prefixes()
    {
        Assert.Equal("Feature.gridEdit", FeatureCatalog.Find(FeatureIds.GridEdit)!.NameKey);
        Assert.Equal("Role.viewer", FeatureCatalog.FindRole(RoleIds.Viewer)!.NameKey);
        Assert.Equal("Role.viewer.desc", FeatureCatalog.FindRole(RoleIds.Viewer)!.DescriptionKey);
        Assert.Equal("FeatureGroup.Tuning", FeatureCatalog.GroupNameKey(FeatureGroup.Tuning));
        Assert.Null(FeatureCatalog.Find("nope"));
        Assert.Null(FeatureCatalog.Find(null));
        Assert.Null(FeatureCatalog.FindRole("nope"));
        Assert.False(FeatureCatalog.IsKnownRole(null));
        Assert.Contains("{0}", FeatureTexts.ReadOnlyRoleDeniedMessage, StringComparison.Ordinal);
    }

    // ---- 꼬리표 (TRD 10.2) ----

    [Theory]
    [InlineData("Oranda.App.Features.GridEdit", "gridEdit")]
    [InlineData("Oranda.App.Features.DataTransfer", "dataExport")]
    [InlineData("Oranda.App.Features.MyBatisWorkspace.Save", "myBatis")]
    [InlineData("Oranda.App.Features.MyBatisLog", "myBatisLog")]
    [InlineData("Oranda.App.Features.PlsqlWorkspace", "plsqlEditor")]
    [InlineData("Oranda.App.Features.Tuning.Assistant", "tuningAssistant")]
    [InlineData("Oranda.App.Features.Tuning.Report.Html", "tuningReport")]
    [InlineData("Oranda.App.Features.BlockerAlert", "blockerAlert")]
    [InlineData("Oranda.App.Features.Tuning", null)] // 뿌리는 표에 없어 공통
    [InlineData("Oranda.App.Features.SqlEditor", null)]
    [InlineData("Oranda.App.Features.ExecutionPlan", null)]
    [InlineData("Oranda.App.Features.GridEditor", null)] // 접두는 마디 단위로만 맞는다
    [InlineData("Oranda.App.Shell", null)]
    [InlineData("Oranda.App.FeaturesX.GridEdit", null)]
    [InlineData(null, null)]
    public void Namespace_prefix_decides_the_feature_with_whole_segment_matching(string? ns, string? expected) =>
        Assert.Equal(expected, FeatureCatalog.FeatureFor(null, ns is null ? null : ns + ".Thing", ns));

    [Fact]
    public void Id_override_wins_over_type_and_namespace_and_can_map_to_common()
    {
        const string plsqlNs = "Oranda.App.Features.PlsqlEditor";
        Assert.Equal(FeatureIds.PlsqlEditor, FeatureCatalog.FeatureFor("oranda.plsql.compile", plsqlNs + ".Handler", plsqlNs));
        Assert.Null(FeatureCatalog.FeatureFor("oranda.plsql.openSource", plsqlNs + ".Handler", plsqlNs));
        Assert.Equal(FeatureIds.PlsqlWorkspace, FeatureCatalog.FeatureFor("oranda.plsql.saveWorkspaceFile", plsqlNs + ".Handler", plsqlNs));
        Assert.Equal(FeatureIds.PlsqlWorkspace, FeatureCatalog.FeatureFor("oranda.plsql.addToWorkspace", plsqlNs + ".Handler", plsqlNs));

        const string transferNs = "Oranda.App.Features.DataTransfer";
        Assert.Equal(FeatureIds.DataImport, FeatureCatalog.FeatureFor("oranda.tools.import", transferNs + ".H", transferNs));
        Assert.Equal(FeatureIds.DataExport, FeatureCatalog.FeatureFor("oranda.grid.exportExcel", transferNs + ".H", transferNs));

        const string planNs = "Oranda.App.Features.ExecutionPlan";
        foreach (var id in PlanAdvancedCommands)
        {
            Assert.Equal(FeatureIds.PlanAdvanced, FeatureCatalog.FeatureFor(id, planNs + ".H", planNs));
        }

        Assert.Null(FeatureCatalog.FeatureFor("oranda.plan.explain", planNs + ".H", planNs));
    }

    [Fact]
    public void Type_override_tags_startup_tasks_outside_the_app_assembly()
    {
        Assert.Equal(FeatureIds.ChangeJournal, FeatureCatalog.FeatureFor(null, "Oranda.Platform.Journal.ChangeJournalRecorder", "Oranda.Platform.Journal"));
        Assert.Null(FeatureCatalog.FeatureFor(null, "Oranda.Platform.History.HistoryRecorder", "Oranda.Platform.History"));
        Assert.Null(FeatureCatalog.FeatureFor(null, "Oranda.Language.Parsing.PlSqlWarmUpTask", "Oranda.Language.Parsing"));
    }

    [Fact]
    public void Type_override_with_null_makes_the_save_notice_item_common_although_it_lives_in_the_grid_edit_namespace()
    {
        // 저장 알림 칸은 내보내기·튜닝 보고서·대체 열기 안내까지 실어 gridEdit 가 꺼져도 필요하다(TSK-07-09).
        const string type = "Oranda.App.Features.GridEdit.SaveStatusViewModel";
        Assert.Null(FeatureCatalog.FeatureFor(null, type, "Oranda.App.Features.GridEdit"));
        Assert.Equal(FeatureIds.GridEdit, FeatureCatalog.FeatureFor(null, "Oranda.App.Features.GridEdit.GridEditCommands", "Oranda.App.Features.GridEdit")); // 같은 폴더의 다른 형은 그대로
    }

    [Fact]
    public void Type_overload_reads_the_namespace_of_real_and_nested_types()
    {
        Assert.Equal(FeatureIds.MyBatis, FeatureCatalog.FeatureFor(null, typeof(Oranda.App.Features.MyBatisWorkspace.Save.SampleStartup)));
        Assert.Equal(FeatureIds.MyBatis, FeatureCatalog.FeatureFor(null, typeof(Oranda.App.Features.MyBatisWorkspace.Save.SampleStartup.Nested)));
        Assert.Equal(FeatureIds.TuningAssistant, FeatureCatalog.FeatureFor("oranda.tuning.assistant", typeof(Oranda.App.Features.Tuning.Assistant.SampleHandler)));
        Assert.Null(FeatureCatalog.FeatureFor(null, typeof(FeatureCatalogTests)));
        Assert.Throws<ArgumentNullException>(() => FeatureCatalog.FeatureFor(null, (Type)null!));
    }

    // ---- 해석 1~4 (TRD 10.5) ----

    [Fact]
    public void No_section_or_nothing_chosen_means_the_sql_developer_role()
    {
        // D-013: 아무것도 고른 적 없으면 기본 Role = SQL 개발자(D-011 의 「모든 기능」을 뒤집음).
        var r1 = FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.SqlDev] });
        Assert.Equal(r1.Order(), FeatureCatalog.Resolve(null).Order());
        Assert.Equal(r1.Order(), FeatureCatalog.Resolve(new FeaturesSettings()).Order());
        Assert.Equal(10, r1.Count);
        Assert.DoesNotContain(FeatureIds.SqlMonitor, r1);
        Assert.DoesNotContain(FeatureIds.MyBatis, r1);
        Assert.DoesNotContain(FeatureIds.PlsqlEditor, r1);
        Assert.Contains(FeatureIds.BlockerAlert, r1);
        Assert.True(FeatureCatalog.IsUnchosen(null));
        Assert.True(FeatureCatalog.IsUnchosen(new FeaturesSettings()));
        Assert.Equal([RoleIds.SqlDev], FeatureCatalog.EffectiveRoles(new FeaturesSettings()));
        Assert.False(FeatureCatalog.IsReadOnly(new FeaturesSettings()));

        // RoleDefaults(null) 는 그대로 「모든 기능 바탕」이다(EffectiveRoles 가 null 을 낼 때).
        Assert.Equal(OnByDefault.Order(), FeatureCatalog.RoleDefaults(null).Order());
    }

    [Fact]
    public void Use_all_features_is_stored_apart_and_wins_over_roles()
    {
        var all = new FeaturesSettings { AllFeatures = true };
        Assert.False(FeatureCatalog.IsUnchosen(all));
        Assert.Null(FeatureCatalog.EffectiveRoles(all));
        Assert.Equal(OnByDefault.Order(), FeatureCatalog.Resolve(all).Order());

        // 손으로 roles 를 같이 적어도 allFeatures 가 이긴다(조회 전용도 아니다).
        var withViewer = new FeaturesSettings { AllFeatures = true, Roles = [RoleIds.Viewer] };
        Assert.Equal(OnByDefault.Order(), FeatureCatalog.Resolve(withViewer).Order());
        Assert.False(FeatureCatalog.IsReadOnly(withViewer));

        // 직접 끔은 그대로 적용된다.
        var minus = FeatureCatalog.Resolve(all with { Disabled = [FeatureIds.BlockerAlert] });
        Assert.Equal(OnByDefault.Count - 1, minus.Count);
        Assert.DoesNotContain(FeatureIds.BlockerAlert, minus);
    }

    [Fact]
    public void Legacy_use_all_features_with_overrides_and_no_roles_keeps_every_feature_as_the_base()
    {
        // D-013 전 「모든 기능 쓰기」 + 직접 끔·켬은 roles 없이 저장됐다: 고른 것으로 보고 모든 기능 바탕을 지킨다.
        var off = new FeaturesSettings { Disabled = [FeatureIds.Trace] };
        Assert.False(FeatureCatalog.IsUnchosen(off));
        Assert.Null(FeatureCatalog.EffectiveRoles(off));
        Assert.Equal(OnByDefault.Count - 1, FeatureCatalog.Resolve(off).Count);

        var on = new FeaturesSettings { Enabled = ["futureFeature"] };
        Assert.False(FeatureCatalog.IsUnchosen(on));
        Assert.Equal(OnByDefault.Count, FeatureCatalog.Resolve(on).Count);

        // 고른 Role 은 기본값과 무관하다(빈 목록 = 공통만, 조회 전용 = 조회 전용).
        Assert.False(FeatureCatalog.IsUnchosen(new FeaturesSettings { Roles = [] }));
        Assert.True(FeatureCatalog.IsReadOnly(new FeaturesSettings { Roles = [RoleIds.Viewer] }));
    }

    [Fact]
    public void Empty_roles_means_common_only()
    {
        Assert.Empty(FeatureCatalog.Resolve(new FeaturesSettings { Roles = [] }));
        Assert.Empty(FeatureCatalog.RoleDefaults([]));
    }

    [Fact]
    public void Roles_are_unioned_and_adding_a_role_only_adds_features()
    {
        var r1 = FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.SqlDev] });
        var r4 = FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.Tuner] });
        var both = FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.Tuner] });

        Assert.Equal(r1.Union(r4).Order(), both.Order());
        Assert.True(r1.IsSubsetOf(both));
        Assert.True(r4.IsSubsetOf(both));
        Assert.DoesNotContain(FeatureIds.MyBatis, both);
        Assert.Contains(FeatureIds.SqlMonitor, both);
        Assert.Contains(FeatureIds.GridEdit, both);
    }

    [Fact]
    public void Unknown_roles_and_unknown_feature_ids_are_ignored()
    {
        var on = FeatureCatalog.Resolve(new FeaturesSettings { Roles = ["future", RoleIds.Viewer, "viewer"], Enabled = ["aiChat"], Disabled = ["other"] });
        Assert.Equal(ViewerDefaults.Order(), on.Order());
        Assert.Empty(FeatureCatalog.Resolve(new FeaturesSettings { Roles = ["future"] }));
    }

    [Fact]
    public void Enabled_and_disabled_override_role_defaults_and_disabled_wins()
    {
        var s = new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace, FeatureIds.Flashback], Disabled = [FeatureIds.Flashback, FeatureIds.TableSpec] };
        var on = FeatureCatalog.Resolve(s);

        Assert.Contains(FeatureIds.Trace, on);          // 직접 켬
        Assert.DoesNotContain(FeatureIds.Flashback, on); // 둘 다 → 끔이 이김
        Assert.DoesNotContain(FeatureIds.TableSpec, on); // 직접 끔
        Assert.Contains(FeatureIds.GridEdit, on);        // Role 기본 그대로

        // Role 을 고른 적 없어도 덮어쓰기는 먹는다.
        var all = FeatureCatalog.Resolve(new FeaturesSettings { Disabled = [FeatureIds.MyBatis] });
        Assert.Equal(24, all.Count);
        Assert.DoesNotContain(FeatureIds.MyBatis, all);
    }

    [Fact]
    public void Viewer_alone_is_read_only_and_cannot_turn_on_writes_only_features()
    {
        Assert.True(FeatureCatalog.IsReadOnlyRole([RoleIds.Viewer]));
        Assert.True(FeatureCatalog.IsReadOnlyRole([RoleIds.Viewer, RoleIds.Viewer, ""]));
        Assert.False(FeatureCatalog.IsReadOnlyRole([RoleIds.Viewer, RoleIds.SqlDev]));
        Assert.False(FeatureCatalog.IsReadOnlyRole([]));
        Assert.False(FeatureCatalog.IsReadOnlyRole(null));

        var hand = new FeaturesSettings { Roles = [RoleIds.Viewer], Enabled = [FeatureIds.GridEdit, FeatureIds.DataImport, FeatureIds.PlsqlEditor, FeatureIds.Trace] };
        var on = FeatureCatalog.Resolve(hand);
        Assert.DoesNotContain(FeatureIds.GridEdit, on);
        Assert.DoesNotContain(FeatureIds.DataImport, on);
        Assert.DoesNotContain(FeatureIds.PlsqlEditor, on);
        Assert.Contains(FeatureIds.Trace, on); // 쓰기 전용이 아니면 켤 수 있다

        // 다른 Role 과 함께면 막지 않는다(D-006).
        var mixed = FeatureCatalog.Resolve(hand with { Roles = [RoleIds.Viewer, RoleIds.Tuner] });
        Assert.Contains(FeatureIds.GridEdit, mixed);
        Assert.Contains(FeatureIds.PlsqlEditor, mixed);
    }

    [Fact]
    public void Requires_turns_off_a_feature_whose_prerequisite_is_off()
    {
        var r2 = FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.PlsqlDev] });
        Assert.Contains(FeatureIds.PlsqlWorkspace, r2);

        var noEditor = FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.PlsqlDev], Disabled = [FeatureIds.PlsqlEditor] });
        Assert.DoesNotContain(FeatureIds.PlsqlEditor, noEditor);
        Assert.DoesNotContain(FeatureIds.PlsqlWorkspace, noEditor);

        var onlyWorkspace = FeatureCatalog.Resolve(new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.PlsqlWorkspace] });
        Assert.DoesNotContain(FeatureIds.PlsqlWorkspace, onlyWorkspace);
    }

    // ---- features 섹션 (TRD 10.3) ----

    [Fact]
    public void Features_section_round_trips_through_json_with_camel_case_keys()
    {
        var settings = new AppSettings
        {
            Features = new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.Tuner], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.Flashback] },
        };

        var json = JsonSerializer.Serialize(settings, OrandaJsonContext.Default.AppSettings);
        var node = JsonNode.Parse(json)!["features"]!;
        Assert.Equal("""["sqlDev","tuner"]""", node["roles"]!.ToJsonString());
        Assert.Equal("""["trace"]""", node["enabled"]!.ToJsonString());
        Assert.Equal("""["flashback"]""", node["disabled"]!.ToJsonString());

        var back = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(settings.Features, back.Features);
        Assert.Equal(["sqlDev", "tuner"], back.Features.Roles);
    }

    [Fact]
    public void Null_roles_are_omitted_and_read_back_as_null_while_empty_roles_stay_empty()
    {
        var json = JsonSerializer.Serialize(new AppSettings(), OrandaJsonContext.Default.AppSettings);
        Assert.Null(JsonNode.Parse(json)!["features"]!["roles"]);
        Assert.Null(JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Features.Roles);

        var empty = JsonSerializer.Serialize(new AppSettings { Features = new FeaturesSettings { Roles = [] } }, OrandaJsonContext.Default.AppSettings);
        Assert.Equal("[]", JsonNode.Parse(empty)!["features"]!["roles"]!.ToJsonString());
        var back = JsonSerializer.Deserialize(empty, OrandaJsonContext.Default.AppSettings)!.Features;
        Assert.NotNull(back.Roles);
        Assert.Empty(back.Roles);
        Assert.Empty(FeatureCatalog.Resolve(back));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "ui": { "theme": "Dark" } }""")]
    [InlineData("""{ "schemaVersion": 1, "features": null }""")]
    [InlineData("""{ "schemaVersion": 1, "features": {} }""")]
    [InlineData("""{ "schemaVersion": 1, "features": { "roles": null, "enabled": null, "disabled": null } }""")]
    public void Old_settings_without_features_read_as_the_default_sql_developer_role(string json)
    {
        var value = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

        Assert.NotNull(value.Features);
        Assert.Null(value.Features.Roles);
        Assert.Empty(value.Features.Enabled);
        Assert.Empty(value.Features.Disabled);
        Assert.Equal(new FeaturesSettings(), value.Features);
        Assert.False(value.Features.AllFeatures);
        Assert.True(FeatureCatalog.IsUnchosen(value.Features));
        Assert.Equal(FeatureCatalog.RoleDefaults([RoleIds.SqlDev]).Order(), FeatureCatalog.Resolve(value.Features).Order());
    }

    [Fact]
    public void All_features_flag_round_trips_through_json_and_takes_part_in_equality()
    {
        var all = new FeaturesSettings { AllFeatures = true, Disabled = [FeatureIds.Trace] };
        var json = JsonSerializer.Serialize(new AppSettings { Features = all }, OrandaJsonContext.Default.AppSettings);
        Assert.Equal("true", JsonNode.Parse(json)!["features"]!["allFeatures"]!.ToJsonString());
        Assert.Equal(all, JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Features);

        Assert.NotEqual(new FeaturesSettings(), new FeaturesSettings { AllFeatures = true });
        Assert.NotEqual(new FeaturesSettings().GetHashCode(), new FeaturesSettings { AllFeatures = true }.GetHashCode());
        Assert.False(JsonSerializer.Deserialize("""{ "features": { "allFeatures": false } }""", OrandaJsonContext.Default.AppSettings)!.Features.AllFeatures);
    }

    [Fact]
    public void Unknown_ids_in_the_file_survive_a_round_trip()
    {
        var json = """{ "schemaVersion": 1, "features": { "roles": ["sqlDev", "nextRole"], "enabled": ["aiChat"], "disabled": [] } }""";
        var value = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;
        var again = JsonSerializer.Deserialize(JsonSerializer.Serialize(value, OrandaJsonContext.Default.AppSettings), OrandaJsonContext.Default.AppSettings)!;

        Assert.Equal(["sqlDev", "nextRole"], again.Features.Roles);
        Assert.Equal(["aiChat"], again.Features.Enabled);
    }

    [Fact]
    public void Section_equality_ignores_order_and_duplicates_but_not_null_versus_empty_roles()
    {
        var a = new FeaturesSettings { Roles = ["tuner", "sqlDev"], Enabled = ["trace", "flashback"], Disabled = ["myBatis"] };
        var b = new FeaturesSettings { Roles = ["sqlDev", "tuner", "tuner"], Enabled = ["flashback", "trace"], Disabled = ["myBatis"] };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);

        Assert.NotEqual(new FeaturesSettings(), new FeaturesSettings { Roles = [] });
        Assert.NotEqual(a, a with { Disabled = [] });
        Assert.NotEqual(a, a with { Enabled = ["trace"] });
        Assert.NotEqual(a, a with { Roles = ["sqlDev"] });
        Assert.False(a.Equals(null));

        // AppSettings 의 섹션 비교(ChangedSections)도 순서를 무시한다.
        Assert.Equal(new AppSettings { Features = a }, new AppSettings { Features = b });
        Assert.Equal("features", FeaturesSettings.SectionName);
    }

    [Fact]
    public void Null_lists_given_in_code_become_empty()
    {
        var s = new FeaturesSettings { Enabled = null!, Disabled = null! };
        Assert.Empty(s.Enabled);
        Assert.Empty(s.Disabled);
        Assert.NotNull(new AppSettings { Features = null! }.Features);
    }

    // ---- FakeFeatureGate (공용 시험 헬퍼) ----

    [Fact]
    public void Fake_gate_is_all_on_by_default_and_answers_by_tag()
    {
        var gate = new FakeFeatureGate();
        Assert.All(FeatureIds.All, id => Assert.True(gate.IsEnabled(id)));
        Assert.False(gate.ReadOnlyRole);
        Assert.Empty(gate.PendingRestart);
        Assert.True(gate.IsTypeEnabled(typeof(Oranda.App.Features.MyBatisWorkspace.Save.SampleStartup)));

        gate.Disable(FeatureIds.MyBatis);
        Assert.False(gate.IsTypeEnabled(typeof(Oranda.App.Features.MyBatisWorkspace.Save.SampleStartup)));
        Assert.False(gate.IsCommandEnabled("oranda.mybatis.save", typeof(Oranda.App.Features.MyBatisWorkspace.Save.SampleStartup)));
        Assert.True(gate.IsPanelEnabled("oranda.panel.x", typeof(FeatureCatalogTests))); // 공통
        Assert.True(gate.IsEnabled("unknownFeature"));
        Assert.Equal(2, gate.TypeQueries.Count);

        gate.Enable(FeatureIds.MyBatis);
        Assert.True(gate.IsEnabled(FeatureIds.MyBatis));
    }

    [Fact]
    public void Fake_gate_follows_settings_and_raises_changed_on_demand()
    {
        var gate = FakeFeatureGate.ForRoles(RoleIds.Viewer);
        Assert.True(gate.ReadOnlyRole);
        Assert.Equal(ViewerDefaults.Order(), gate.EnabledFeatures.Order());
        Assert.False(gate.IsCommandEnabled("oranda.plsql.compile", typeof(Oranda.App.Features.Tuning.Assistant.SampleHandler)));
        Assert.True(gate.IsCommandEnabled("oranda.plsql.openSource", typeof(Oranda.App.Features.Tuning.Assistant.SampleHandler))); // id 덮어쓰기 = 공통

        var raised = 0;
        gate.Changed += (_, _) => raised++;
        gate.SetEnabled(FeatureIds.All, raiseChanged: false);
        Assert.Equal(0, raised);
        gate.SetEnabled([FeatureIds.TuningAssistant]);
        Assert.Equal(1, raised);
        Assert.True(gate.IsCommandEnabled("oranda.tuning.assistant", typeof(Oranda.App.Features.Tuning.Assistant.SampleHandler)));

        gate.PendingRestart = [FeatureIds.MyBatis];
        gate.ReadOnlyRole = false;
        gate.RaiseChanged();
        Assert.Equal(2, raised);
        Assert.Equal([FeatureIds.MyBatis], gate.PendingRestart);
        Assert.False(gate.ReadOnlyRole);
    }

    // PRD 12.2 표(docs/oranda1/PRD.md)를 읽어 기능 id → Role 6개 ○ 여부.
    private static Dictionary<string, bool[]> ReadPrdRoleTable()
    {
        var prd = Path.Combine(FindRepoRoot(), "docs", "oranda1", "PRD.md");
        var lines = File.ReadAllLines(prd);
        var start = Array.FindIndex(lines, l => l.StartsWith("### 12.2", StringComparison.Ordinal));
        Assert.True(start >= 0, "PRD 12.2 절을 찾지 못했다");

        var table = new Dictionary<string, bool[]>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("### ", StringComparison.Ordinal)))
        {
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 10 || !cells[1].StartsWith('`'))
            {
                continue;
            }

            var id = cells[1].Trim('`');
            var marks = cells[3..9];
            Assert.All(marks, m => Assert.True(m is "○" or "·", id + ": " + m));
            table.Add(id, [.. marks.Select(m => m == "○")]);
        }

        return table;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.GetFiles(dir.FullName, "Oranda.sln*").Length > 0)
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("저장소 루트를 찾지 못했다.");
    }
}
