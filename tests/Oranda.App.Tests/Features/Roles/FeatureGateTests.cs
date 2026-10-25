using System.Reflection;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell;
using Oranda.Core.Composition;
using Oranda.Core.Features;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Roles;

/// <summary>
/// TSK-07-02: 게이트 해석 규칙 표(TRD 10.5 — Role 조합·덮어쓰기·겹침·조회 전용·Requires·StartupBound·모르는 id), 꼬리표 전수(TRD 10.2·10.11),
/// <c>SettingsChanged("features")</c> 재해석, <c>feature.&lt;id&gt;</c> 문맥 키.
/// </summary>
public sealed class FeatureGateTests
{
    private static FeaturesSettings Roles(params string[] roles) => new() { Roles = roles };

    /// <summary>「모든 기능 쓰기」(D-013 뒤에는 설정 없음이 아니라 이 값이 모든 기능이다).</summary>
    private static readonly FeaturesSettings All = new() { AllFeatures = true };

    private static FeatureGate Resolved(FeaturesSettings? settings)
    {
        var gate = new FeatureGate();
        gate.Resolve(settings);
        return gate;
    }

    private static ServiceContainer Compose(string? dir = null) =>
        AppComposition.Create(new AppCompositionOptions
        {
            DataDirectory = dir ?? Path.Combine(Path.GetTempPath(), "oranda-gate-" + Guid.NewGuid().ToString("N")),
            DevBuild = true,
        });

    // ── 해석 전·features 없음 ───────────────────────────────────────

    [Fact]
    public void Before_settings_are_read_every_feature_is_on_and_nothing_is_pending()
    {
        var gate = new FeatureGate();

        Assert.False(gate.IsResolved);
        Assert.All(FeatureIds.All, id => Assert.True(gate.IsEnabled(id), id));
        Assert.False(gate.ReadOnlyRole);
        Assert.Empty(gate.PendingRestart);
    }

    [Fact]
    public void Without_a_features_section_the_first_resolution_is_the_sql_developer_role_and_raises_one_event()
    {
        // D-013: 설정 없음 = 기본 Role(SQL 개발자). 「설정 읽기 전 = 모두 켬」에서 R1 로 줄어드니 사건이 한 번 난다.
        var gate = new FeatureGate();
        var raised = 0;
        gate.Changed += (_, _) => raised++;

        Assert.True(gate.Resolve(null));
        Assert.False(gate.Resolve(new FeaturesSettings())); // null 과 빈 섹션은 같은 결과
        Assert.False(gate.Resolve(Roles(RoleIds.SqlDev))); // 직접 고른 R1 과도 같은 결과

        Assert.True(gate.IsResolved);
        Assert.Equal(1, raised);
        Assert.Equal(FeatureCatalog.Resolve(Roles(RoleIds.SqlDev)).ToHashSet(), gate.EnabledFeatures);
        Assert.False(gate.IsEnabled(FeatureIds.SessionMonitor));
        Assert.False(gate.ReadOnlyRole);
        Assert.Empty(gate.PendingRestart);
    }

    [Fact]
    public void Use_all_features_resolves_like_before_settings_are_read_and_raises_no_event()
    {
        var gate = new FeatureGate();
        var raised = 0;
        gate.Changed += (_, _) => raised++;

        Assert.False(gate.Resolve(All));
        Assert.False(gate.Resolve(new FeaturesSettings { Disabled = [] , AllFeatures = true, Roles = [RoleIds.Viewer] })); // allFeatures 가 이긴다

        Assert.Equal(0, raised);
        Assert.Equal(FeatureIds.All.ToHashSet(), gate.EnabledFeatures);
        Assert.False(gate.ReadOnlyRole);
    }

    // ── 해석 규칙 표(첫 해석 = 시작 때 값이라 StartupBound 도 그대로 따른다) ──

    public static TheoryData<string, FeaturesSettings, string[], string[], bool> Table() => new()
    {
        { "R1 만", Roles(RoleIds.SqlDev), [FeatureIds.GridEdit, FeatureIds.BlockerAlert, FeatureIds.PlanAdvanced], [FeatureIds.MyBatis, FeatureIds.LockMonitor, FeatureIds.SqlMonitor, FeatureIds.PlsqlEditor], false },
        { "R6 만 = 조회 전용", Roles(RoleIds.Viewer), [FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.TableSpec], [FeatureIds.GridEdit, FeatureIds.BlockerAlert, FeatureIds.SessionMonitor, FeatureIds.DataImport], true },
        { "R6 만 + 쓰기 전용 직접 켬 = 여전히 끔", new() { Roles = [RoleIds.Viewer], Enabled = [FeatureIds.GridEdit, FeatureIds.PlsqlRun, FeatureIds.Trace] }, [FeatureIds.Trace], [FeatureIds.GridEdit, FeatureIds.PlsqlRun], true },
        { "R6 중복 = 조회 전용", Roles(RoleIds.Viewer, RoleIds.Viewer), [FeatureIds.TableSpec], [FeatureIds.GridEdit], true },
        { "R6 + R1 = 차단 없음", Roles(RoleIds.Viewer, RoleIds.SqlDev), [FeatureIds.GridEdit, FeatureIds.DataImport, FeatureIds.TableSpec], [FeatureIds.SessionMonitor], false },
        { "R4 + R5 합집합", Roles(RoleIds.Tuner, RoleIds.Dba), [FeatureIds.TuningAssistant, FeatureIds.ColumnReorder, FeatureIds.Flashback, FeatureIds.TuningReport], [FeatureIds.MyBatis, FeatureIds.MyBatisLog], false },
        { "직접 켬·끔", new() { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.Flashback] }, [FeatureIds.Trace, FeatureIds.GridEdit], [FeatureIds.Flashback], false },
        { "겹침 = 끔이 이김", new() { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.Trace] }, [], [FeatureIds.Trace], false },
        { "Requires 못 채움", new() { Roles = [RoleIds.PlsqlDev], Disabled = [FeatureIds.PlsqlEditor] }, [FeatureIds.PlsqlRun], [FeatureIds.PlsqlEditor, FeatureIds.PlsqlWorkspace], false },
        { "Requires 를 직접 켬만으로", new() { Roles = [], Enabled = [FeatureIds.PlsqlWorkspace] }, [], [FeatureIds.PlsqlWorkspace, FeatureIds.PlsqlEditor], false },
        { "빈 Role = 공통만", Roles(), [], [.. FeatureIds.All], false },
        { "모르는 Role·id 는 무시(조회 전용 판정은 계약대로 Role 목록 그대로)", new() { Roles = ["futureRole", RoleIds.Viewer], Enabled = ["futureFeature"] }, [FeatureIds.TableSpec], [FeatureIds.GridEdit], false },
        { "Role 없음 + 직접 끔", new() { Disabled = [FeatureIds.MyBatis] }, [FeatureIds.GridEdit, FeatureIds.LockMonitor], [FeatureIds.MyBatis], false },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Resolution_follows_the_rule_table(string name, FeaturesSettings settings, string[] on, string[] off, bool readOnly)
    {
        var gate = Resolved(settings);

        Assert.All(on, id => Assert.True(gate.IsEnabled(id), name + ": " + id + " 켬이어야"));
        Assert.All(off, id => Assert.False(gate.IsEnabled(id), name + ": " + id + " 끔이어야"));
        Assert.Equal(readOnly, gate.ReadOnlyRole);
        Assert.Empty(gate.PendingRestart); // 첫 해석 = 시작 때 값
        Assert.Equal(FeatureCatalog.Resolve(settings), gate.EnabledFeatures); // 첫 해석은 카탈로그 1~4단계와 같다
    }

    [Fact]
    public void Unknown_feature_ids_are_treated_as_common()
    {
        var gate = Resolved(Roles());

        Assert.True(gate.IsEnabled("someFutureFeature"));
        Assert.DoesNotContain("someFutureFeature", gate.EnabledFeatures);
        Assert.Throws<ArgumentNullException>(() => gate.IsEnabled(null!));
    }

    // ── StartupBound(5단계) ──────────────────────────────────────────

    [Fact]
    public void Turning_a_startup_bound_feature_on_at_runtime_waits_for_a_restart()
    {
        var gate = Resolved(Roles(RoleIds.SqlDev)); // myBatis 는 시작 때 꺼짐
        var raised = 0;
        gate.Changed += (_, _) => raised++;

        Assert.True(gate.Resolve(Roles(RoleIds.SqlDev, RoleIds.MyBatisDev)));

        Assert.False(gate.IsEnabled(FeatureIds.MyBatis)); // 배선 없는 MyBatis 명령이 나타나지 않는다
        Assert.True(gate.IsEnabled(FeatureIds.MyBatisLog)); // 시작 때 도는 기능이 아니면 바로 켜진다
        Assert.Equal([FeatureIds.MyBatis], gate.PendingRestart);
        Assert.False(gate.IsCommandEnabled("oranda.mybatis.any", typeof(Oranda.App.Features.MyBatisWorkspace.Hooks.MyBatisWorkspaceWiring)));
        Assert.Equal(1, raised);

        // 되돌리면 재시작 대기도 사라진다.
        Assert.True(gate.Resolve(Roles(RoleIds.SqlDev)));
        Assert.Empty(gate.PendingRestart);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Turning_a_startup_bound_feature_off_hides_it_now_and_lists_it_as_pending()
    {
        var gate = Resolved(All);

        gate.Resolve(Roles(RoleIds.Viewer));

        Assert.False(gate.IsEnabled(FeatureIds.BlockerAlert)); // 메뉴는 바로 사라진다
        Assert.False(gate.IsEnabled(FeatureIds.ChangeJournal));
        Assert.Equal(
            new[] { FeatureIds.ChangeJournal, FeatureIds.PlsqlWorkspace, FeatureIds.MyBatis, FeatureIds.BlockerAlert }.ToHashSet(),
            gate.PendingRestart.ToHashSet()); // 감시·타이머는 다음 실행에 멈춘다
        Assert.True(gate.ReadOnlyRole);
    }

    [Fact]
    public void Same_settings_again_raise_no_event()
    {
        var gate = Resolved(Roles(RoleIds.Tuner));
        var raised = 0;
        gate.Changed += (_, _) => raised++;

        Assert.False(gate.Resolve(Roles(RoleIds.Tuner)));
        Assert.False(gate.Resolve(new FeaturesSettings { Roles = [RoleIds.Tuner], Enabled = [FeatureIds.SqlMonitor] })); // 이미 켬 = 결과 같음

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Only_the_read_only_flag_changing_still_raises_an_event()
    {
        // R6 + R6 기본에 없는 Role 인데 기본값이 R6 의 부분집합인 경우는 없으므로 Enabled 로 같은 집합을 만든다.
        var gate = Resolved(new FeaturesSettings { Roles = [], Enabled = [FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.TableSpec] });
        var raised = 0;
        gate.Changed += (_, _) => raised++;

        Assert.True(gate.Resolve(Roles(RoleIds.Viewer)));

        Assert.True(gate.ReadOnlyRole);
        Assert.Equal(1, raised);
    }

    // ── 꼬리표 ───────────────────────────────────────────────────────

    [Fact]
    public void Registered_items_follow_their_tags_and_the_lookup_is_cached()
    {
        var gate = Resolved(new FeaturesSettings { Roles = [RoleIds.SqlDev], Disabled = [FeatureIds.PlsqlEditor] });
        var openSource = ShellTestHelpers.AppContributions.Commands.Single(c => c.Definition.Id == "oranda.plsql.openSource");
        var import = ShellTestHelpers.AppContributions.Commands.Single(c => c.Definition.Id == "oranda.tools.import");

        Assert.True(gate.IsCommandEnabled(openSource.Definition.Id, openSource.HandlerType)); // 덮어쓰기 = 공통(D-007)
        Assert.Null(gate.FeatureOf(openSource.Definition.Id, openSource.HandlerType));
        Assert.Equal(FeatureIds.DataImport, gate.FeatureOf(import.Definition.Id, import.HandlerType));
        Assert.Equal(FeatureIds.ChangeJournal, gate.FeatureOf(null, typeof(Oranda.Platform.PlatformAssembly).Assembly.GetType("Oranda.Platform.Journal.ChangeJournalRecorder", throwOnError: true)!));
        Assert.False(gate.IsPanelEnabled("oranda.panel.compileErrors", typeof(Oranda.App.Features.PlsqlEditor.CompileErrorsViewModel)));
        Assert.True(gate.IsTypeEnabled(typeof(FeatureGateTests))); // 기능 폴더 밖 = 공통
    }

    /// <summary>시작 작업 구현 형(App·Platform·Language 어셈블리).</summary>
    private static IEnumerable<Type> StartupTaskTypes() =>
        new[] { typeof(AppComposition).Assembly, typeof(Oranda.Platform.PlatformAssembly).Assembly, Assembly.Load("Oranda.Language"), Assembly.Load("Oranda.Oracle") }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IStartupTask).IsAssignableFrom(t));

    [Fact]
    public void Every_registered_item_maps_to_one_feature_or_common_and_every_feature_has_an_item()
    {
        var gate = new FeatureGate();
        var c = ShellTestHelpers.AppContributions;
        var mapped = new List<(string Item, string? Feature)>();
        mapped.AddRange(c.Commands.Select(x => ("cmd " + x.Definition.Id, gate.FeatureOf(x.Definition.Id, x.HandlerType))));
        mapped.AddRange(c.Panels.Select(x => ("panel " + x.Id, gate.FeatureOf(x.Id, x.ViewModelType))));
        mapped.AddRange(c.StatusBarItems.Select(x => ("status " + x.Id, gate.FeatureOf(null, x.ItemType))));
        mapped.AddRange(StartupTaskTypes().Select(t => ("startup " + t.FullName, gate.FeatureOf(null, t))));

        // 꼬리표는 카탈로그가 아는 기능이거나 공통(null)이다.
        Assert.All(mapped, m => Assert.True(m.Feature is null || FeatureCatalog.IsKnownFeature(m.Feature), m.Item + " → " + m.Feature));

        // 모든 기능 id 에 등록 항목이 하나 이상 있다(예외 없음).
        var covered = mapped.Select(m => m.Feature).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.Empty(FeatureIds.All.Where(id => !covered.Contains(id)));

        // 실제 스캔 결과의 대표 항목(시작 작업 14개 중 기능에 드는 것).
        Assert.Contains(("startup Oranda.Platform.Journal.ChangeJournalRecorder", FeatureIds.ChangeJournal), mapped);
        Assert.Contains(("startup " + typeof(Oranda.App.Features.MyBatisWorkspace.Save.MapperWatchStartup).FullName, FeatureIds.MyBatis), mapped);
        Assert.Contains(("startup " + typeof(Oranda.App.Features.PlsqlWorkspace.PlsqlWorkspacePersistence).FullName, FeatureIds.PlsqlEditor), mapped);
    }

    // ── 설정 바뀜·문맥 키 ─────────────────────────────────────────────

    [Fact]
    public async Task Saving_the_features_section_re_resolves_and_updates_context_keys()
    {
        using var services = Compose();
        var gate = (FeatureGate)services.GetRequiredService<IFeatureGate>();
        var settings = services.GetRequiredService<SettingsService>();
        var keys = services.GetRequiredService<ContextKeyService>();
        await settings.LoadAsync();
        gate.Resolve(settings.Current.Features); // 설정 없음 = 기본 Role(SQL 개발자, D-013)
        var raised = 0;
        gate.Changed += (_, _) => Interlocked.Increment(ref raised);
        Assert.False(keys.Get(FeatureIds.ContextKey(FeatureIds.SessionMonitor)));
        Assert.True(keys.Evaluate("feature.gridEdit && !feature.sessionMonitor")); // 점이 든 이름도 식에서 읽는다

        await settings.UpdateAsync(s => s with { Features = All });

        Assert.Equal(1, raised);
        Assert.True(gate.IsEnabled(FeatureIds.SessionMonitor));
        Assert.True(keys.Get("feature.sessionMonitor"));
        Assert.True(keys.Get("feature.gridEdit"));

        // 다른 섹션 저장은 게이트를 건드리지 않는다.
        await settings.UpdateAsync(s => s with { Ui = s.Ui with { Theme = Theme.Dark } });
        Assert.Equal(1, raised);

        // 설정 없음으로 되돌리면 다시 기본 Role 이다.
        await settings.UpdateAsync(s => s with { Features = new FeaturesSettings() });
        Assert.Equal(2, raised);
        Assert.False(keys.Get("feature.sessionMonitor"));
        Assert.True(keys.Get("feature.gridEdit"));
    }

    [Fact]
    public async Task Pending_restart_is_already_updated_when_the_settings_save_returns()
    {
        // 설정 창(TSK-07-04)은 저장이 끝난 직후 PendingRestart 를 보고 재시작 띠를 띄운다: 같은 흐름 안에서 다시 해석돼 있어야 한다.
        using var services = Compose();
        var gate = (FeatureGate)services.GetRequiredService<IFeatureGate>();
        var settings = services.GetRequiredService<SettingsService>();
        await settings.LoadAsync();
        gate.Resolve(settings.Current.Features); // 시작 때 = 설정 없음 = R1(MyBatis·PL/SQL 작업 공간 꺼짐)

        await settings.UpdateAsync(s => s with { Features = All });
        Assert.Equal(new[] { FeatureIds.PlsqlWorkspace, FeatureIds.MyBatis }.ToHashSet(), gate.PendingRestart.ToHashSet());

        await settings.UpdateAsync(s => s with { Features = Roles(RoleIds.SqlDev) }); // 직접 고른 R1 = 시작 때와 같음
        Assert.Empty(gate.PendingRestart);
    }

    [Fact]
    public void Context_keys_start_true_and_change_once_per_resolution()
    {
        var keys = new ContextKeyService();
        var changes = 0;
        keys.Changed += (_, _) => changes++;
        var gate = new FeatureGate(null, null, keys);

        Assert.All(FeatureIds.All, id => Assert.True(keys.Get(FeatureIds.ContextKey(id)), id));
        changes = 0;

        gate.Resolve(Roles(RoleIds.Viewer));

        Assert.Equal(1, changes); // 키 25개를 한 번에
        Assert.True(keys.Get("feature.tableSpec"));
        Assert.False(keys.Get("feature.gridEdit"));
    }

    [Fact]
    public async Task Readers_on_execution_threads_see_whole_snapshots_while_settings_change()
    {
        // 정책 관문(TSK-07-05)은 실행 스레드에서 ReadOnlyRole·IsEnabled 를 읽는다. 해석은 불변 스냅숏을 통째로 갈아 끼우므로
        // 읽는 쪽은 잠그지 않고도 깨진 값(반쯤 바뀐 집합·열거 중 바뀜 예외)을 보지 않는다.
        var gate = Resolved(All); // 시작 때 모두 켬 → 켜진 집합 = 카탈로그 해석 그대로(StartupBound AND 가 아무것도 빼지 않는다)
        var viewer = Roles(RoleIds.Viewer);
        var dev = Roles(RoleIds.SqlDev);
        gate.Resolve(viewer);
        var bad = 0;

        // 돌기만 하는 스레드로 다른 시험을 굶기지 않게 횟수를 정해 읽는다.
        var readers = Enumerable.Range(0, 2).Select(n => Task.Run(() =>
        {
            for (var k = 0; k < 2000; k++)
            {
                var enabled = gate.EnabledFeatures; // 한 스냅숏의 사본
                var isViewerSet = enabled.SetEquals(FeatureCatalog.Resolve(viewer));
                var isDevSet = enabled.SetEquals(FeatureCatalog.Resolve(dev));
                if (!isViewerSet && !isDevSet)
                {
                    Interlocked.Increment(ref bad);
                }

                _ = gate.ReadOnlyRole;
                _ = gate.IsEnabled(FeatureIds.GridEdit);
                _ = gate.PendingRestart.Count; // 열거 중 바뀜 예외가 없어야 한다
                _ = gate.IsCommandEnabled("oranda.tools.import", typeof(object));
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                gate.Resolve(i % 2 == 0 ? dev : viewer);
            }
        });

        await Task.WhenAll([.. readers, writer]);

        Assert.Equal(0, bad);
        Assert.True(gate.ReadOnlyRole); // 마지막 = viewer
        Assert.False(gate.IsEnabled(FeatureIds.GridEdit));
    }

    [Fact]
    public async Task Concurrent_resolutions_settle_on_a_consistent_state()
    {
        var gate = Resolved(All);
        var a = Roles(RoleIds.Viewer);
        var b = Roles(RoleIds.Tuner);

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            gate.Resolve(i % 2 == 0 ? a : b);
            _ = gate.IsCommandEnabled("oranda.tools.import", typeof(object));
        })));

        gate.Resolve(b);
        Assert.False(gate.ReadOnlyRole);
        Assert.True(gate.IsEnabled(FeatureIds.TuningAssistant));
    }
}
