using Avalonia.Input;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Features;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>
/// TSK-07-08 통합 1: <b>실제 FeatureGate</b> 로 앱 전체를 조립하고(설정 파일 → 설정 읽기 → 게이트 해석 → 레지스트리·메뉴·도킹·상태 칸·시작 작업),
/// Role 6개 각각과 조합에서 메뉴·툴바·우클릭·팔레트·단축키·도구 창·상태 칸·시작 작업이 PRD 12.2 표(독립 정답표 <see cref="PrdMatrix"/>)와 같은지 본다.
/// 정답표는 제품 카탈로그를 읽지 않고 표를 다시 적은 것이라 카탈로그·꼬리표·거르기 어느 한 쪽이 틀려도 걸린다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RoleItestUi")]
public sealed class RoleSurfaceMatrixTests
{
    private sealed record Case(string[]? Roles, string[]? Enabled = null, string[]? Disabled = null, bool AllFeatures = false)
    {
        /// <summary>settings.json 에 쓸 features 글. 아무것도 없으면 null(파일을 쓰지 않음 = 설정 없음).</summary>
        public string? Json => Roles is null && Enabled is null && Disabled is null && !AllFeatures ? null : PrdMatrix.FeaturesJson(Roles, Enabled, Disabled, AllFeatures);

        public HashSet<string> Expected => PrdMatrix.Enabled(Roles, Enabled, Disabled, AllFeatures);
    }

    /// <summary>「모든 기능 쓰기」(D-013 전의 「설정 없음」 = a507d81 기준선).</summary>
    private static readonly Case AllCase = new(null, AllFeatures: true);

    private static readonly Dictionary<string, Case> Cases = new(StringComparer.Ordinal)
    {
        ["none(default-R1)"] = new(null),
        ["all-features"] = AllCase,
        ["all-features+disabled-lockMonitor"] = new(null, null, ["lockMonitor"], AllFeatures: true),
        ["all-features+roles-ignored"] = new(["viewer"], AllFeatures: true),
        ["R1-sqlDev"] = new(["sqlDev"]),
        ["R2-plsqlDev"] = new(["plsqlDev"]),
        ["R3-myBatisDev"] = new(["myBatisDev"]),
        ["R4-tuner"] = new(["tuner"]),
        ["R5-dba"] = new(["dba"]),
        ["R6-viewer"] = new(["viewer"]),
        ["R1+R4"] = new(["sqlDev", "tuner"]),
        ["R2+R5"] = new(["plsqlDev", "dba"]),
        ["R6+R1"] = new(["viewer", "sqlDev"]),
        ["all-six-roles"] = new(["sqlDev", "plsqlDev", "myBatisDev", "tuner", "dba", "viewer"]),
        ["R1+enabled-trace+disabled-flashback"] = new(["sqlDev"], ["trace"], ["flashback"]),
        ["R6+enabled-gridEdit(blocked)"] = new(["viewer"], ["gridEdit", "plsqlEditor"]),
        ["R6+disabled-tableSpec"] = new(["viewer"], null, ["tableSpec"]),
        ["roles-empty(common-only)"] = new([]),
        ["none+disabled-lockMonitor+myBatis"] = new(null, null, ["lockMonitor", "myBatis"]),
    };

    public static IEnumerable<object[]> CaseNames() => Cases.Keys.Select(k => new object[] { k });

    private static string[] TopMenusFor(HashSet<string> on)
    {
        var tops = new List<string> { "file", "edit", "view", "sql", "result", "database" };
        if (on.Contains("sessionMonitor") || on.Contains("lockMonitor") || on.Contains("sqlMonitor"))
        {
            tops.Add("monitor");
        }

        string[] tuning = ["planAdvanced", "sqlMonitor", "tuningAssistant", "childCursors", "tableStats", "trace", "tuningReport"];
        if (tuning.Any(on.Contains))
        {
            tops.Add("tuning");
        }

        tops.AddRange(["tools", "window", "help"]);
        return [.. tops];
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Every_surface_follows_the_prd_matrix(string name) => HeadlessHost.Run(async () =>
    {
        var c = Cases[name];
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, c.Json);
            await RoleApp.SettleAsync();
            var expected = c.Expected;
            var s = app.Capture();
            var gate = app.Gate;
            var text = app.Get<ShellController>().Text;
            var commandsAll = ShellTestHelpers.AppContributions.Commands;

            // ---- 게이트: 켜진 기능 집합·조회 전용·재시작 대기 ----
            Assert.True(expected.SetEquals(s.GateEnabled), $"{name}: 게이트 켠 기능 차이 = {string.Join(",", expected.Except(s.GateEnabled))} / {string.Join(",", s.GateEnabled.Except(expected))}");
            Assert.Equal(!c.AllFeatures && c.Roles is { Length: 1 } && c.Roles[0] == "viewer", s.GateReadOnlyRole);
            Assert.Empty(s.PendingRestart); // 시작 때 값이 곧 지금 값

            // ---- 명령 레지스트리: 모든 명령이 (공통 또는 켜진 기능)일 때만 있다 ----
            foreach (var x in commandsAll)
            {
                var feature = gate.FeatureOf(x.Definition.Id, x.HandlerType);
                var shouldBe = feature is null || expected.Contains(feature);
                Assert.True(shouldBe == s.RegistryCommands.Contains(x.Definition.Id), $"{name}: {x.Definition.Id}(기능 {feature ?? "공통"}) 레지스트리 {(shouldBe ? "있어야" : "없어야")} 한다");
            }

            // 사람이 정한 명령-기능 표(조사 1절)로 꼬리표 자체도 대조한다.
            foreach (var (id, feature) in PrdMatrix.CommandFeatures)
            {
                var def = commandsAll.Single(x => x.Definition.Id == id);
                Assert.Equal(feature, gate.FeatureOf(id, def.HandlerType));
                Assert.Equal(feature is null || expected.Contains(feature), s.RegistryCommands.Contains(id));
                Assert.Equal(feature is null || expected.Contains(feature), s.PaletteCommands.Contains(id) || !def.Definition.ShowInPalette);
            }

            // ---- 최상위 메뉴 ----
            var expectedTops = TopMenusFor(expected).Select(t => text.Lookup($"Menu.{t}")).ToArray();
            Assert.Equal(expectedTops, s.TopMenus);

            // ---- 메뉴·툴바·우클릭·팔레트에는 꺼진 명령이 없다 ----
            var disabled = commandsAll.Select(x => x.Definition.Id).Where(id => !s.RegistryCommands.Contains(id)).ToHashSet(StringComparer.Ordinal);
            Assert.Empty(s.MenuCommands.Intersect(disabled));
            Assert.Empty(s.ToolbarCommands.Intersect(disabled));
            Assert.Empty(s.ContextCommands.Intersect(disabled));
            Assert.Empty(s.PaletteCommands.Intersect(disabled));
            Assert.Subset(s.RegistryCommands, s.PaletteCommands);
            Assert.True(s.RegistryCommands.Where(id => commandsAll.Single(x => x.Definition.Id == id).Definition.ShowInPalette).ToHashSet().SetEquals(s.PaletteCommands), $"{name}: 팔레트 = 레지스트리의 팔레트 명령");

            // 켜진 명령 중 보이는 최상위 메뉴에 놓인 것은 실제 메뉴에 있다.
            var visibleTops = TopMenusFor(expected).ToHashSet(StringComparer.Ordinal);
            foreach (var id in s.RegistryCommands)
            {
                var def = commandsAll.Single(x => x.Definition.Id == id).Definition;
                var placedInVisibleTop = def.Placements.Select(p => Placement.TryParse(p, out var pl) ? pl : null)
                    .Any(pl => pl is { Surface: "menu" } && visibleTops.Contains(pl.Path[0]));
                if (placedInVisibleTop)
                {
                    Assert.True(s.MenuCommands.Contains(id), $"{name}: {id} 는 보이는 메뉴에 놓였는데 메뉴에 없다");
                }
            }

            // ---- 툴바: 켜진 기능의 툴바 명령은 남는다 ----
            foreach (var id in s.RegistryCommands)
            {
                var def = commandsAll.Single(x => x.Definition.Id == id).Definition;
                if (def.Placements.Any(p => Placement.TryParse(p, out var pl) && pl is { Surface: "toolbar" } && pl.Path[0] == "main"))
                {
                    Assert.True(s.ToolbarCommands.Contains(id), $"{name}: {id} 툴바에 있어야 한다");
                }
            }

            // ---- 단축키: 꺼진 명령의 키는 그 명령으로 가지 않는다 ----
            var keymap = app.Get<KeymapService>();
            var registry = app.Get<ICommandRegistry>();
            var contextKeys = app.Get<ContextKeyService>();
            var checkedKeys = 0;
            foreach (var id in disabled)
            {
                Assert.False(await registry.ExecuteAsync(id, contextKeys), $"{name}: 꺼진 명령 {id} 가 실행됐다");
                if (keymap.Table.SingleGesture(id) is { } gesture)
                {
                    checkedKeys++;
                    var match = keymap.Table.Find([(gesture.Key, gesture.KeyModifiers)], contextKeys, cid => !((CommandRegistry)registry).IsFeatureDisabled(cid));
                    Assert.NotEqual(id, match.CommandId);
                }
            }

            // 켜진 명령은 단축키가 그대로다(꺼진 쪽에서 키맵을 지우지 않았다).
            Assert.Equal(keymap.Table.ShortcutText("oranda.view.commandPalette"), keymap.ShortcutText("oranda.view.commandPalette"));
            if (!c.AllFeatures && c.Roles is { Length: 1 } && c.Roles[0] == "viewer")
            {
                Assert.True(checkedKeys > 0, "조회 전용에서는 단축키가 있는 꺼진 명령이 있어야 이 확인이 의미가 있다");
            }

            // ---- 도구 창 ----
            var panelsAll = ShellTestHelpers.AppContributions.Panels;
            foreach (var p in panelsAll)
            {
                var feature = gate.FeatureOf(p.Id, p.ViewModelType);
                var shouldBe = feature is null || expected.Contains(feature);
                Assert.True(shouldBe == s.RegistryPanels.Contains(p.Id), $"{name}: 도구 창 {p.Id}(기능 {feature ?? "공통"}) 레지스트리 {(shouldBe ? "있어야" : "없어야")} 한다");
            }

            foreach (var (id, feature) in new (string, string?)[]
            {
                ("oranda.panel.changeJournal", "changeJournal"), ("oranda.panel.compileErrors", "plsqlEditor"),
                ("oranda.panel.mybatisStatements", "myBatis"), ("oranda.panel.mybatisCombos", "myBatis"),
                ("oranda.panel.sessionMonitor", "sessionMonitor"), ("oranda.panel.sqlMonitor", "sqlMonitor"),
                ("oranda.panel.tuningAssistant", "tuningAssistant"), ("oranda.panel.tableSpec", "tableSpec"),
                ("oranda.panel.rowView", "lobRowView"), ("oranda.panel.lobEditor", "lobRowView"),
                ("oranda.panel.dbInfo", "dbTools"), ("oranda.panel.ddlExtract", "dbTools"),
                ("oranda.panel.plan", null), ("oranda.panel.results", null), ("oranda.panel.schemaBrowser", null),
                ("oranda.panel.history", null), ("oranda.panel.binds", null), ("oranda.panel.messages", null),
            })
            {
                Assert.Equal(feature is null || expected.Contains(feature), s.RegistryPanels.Contains(id));
            }

            // 열려 있는 도구 창은 모두 켜진 것이고, 기본 배치에 있던 켜진 도구 창은 그대로 열려 있다.
            Assert.Subset(s.RegistryPanels, s.VisibleToolWindows);
            var baseline = await BaselineAsync();
            Assert.Equal(baseline.VisibleToolWindows.Where(s.RegistryPanels.Contains).Order(), s.VisibleToolWindows.Order());

            // 보기 메뉴의 도구 창 목록은 레지스트리의 도구 창 수와 같다.
            Assert.Equal(app.Get<IToolPanelRegistry>().Panels.Count(p => p.Kind == ToolPanelKind.Tool), s.ViewMenuPanelToggles);

            // ---- 상태 칸·시작 작업 ----
            Assert.Equal(PrdMatrix.ExpectedStatusTypes(expected, baseline.StatusTypes).Order(), s.StatusTypes.Order());
            Assert.Equal(PrdMatrix.ExpectedTasks(expected).Order(), s.StartupTasks.Order());
            Assert.Equal(PrdMatrix.AllTasks.Except(PrdMatrix.ExpectedTasks(expected)).Order(), s.SkippedTasks.Order());
            Assert.Contains("PlSqlWarmUpTask", s.StartupTasks); // Language 의 공통 작업은 늘 돈다
            Assert.Equal(expected.Contains("blockerAlert"), app.BlockerChip() is not null);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    /// <summary>
    /// TSK-09-04 툴바 정답표(D-022): 조회 전용(R6)만 고르고 미커밋이 없으면 커밋·롤백 2개가 숨어 조사 14개에서 12개, 미커밋이 생기면 14개.
    /// 조회 전용이 아닌 구성은 툴바가 그대로(놓인 단추 = 보이는 단추).
    /// </summary>
    [Theory]
    [InlineData("R6-viewer", 14, 12)]
    [InlineData("R1-sqlDev", 14, 14)]
    [InlineData("none(default-R1)", 14, 14)]
    [InlineData("R6+R1", 14, 14)]
    [InlineData("all-features", 18, 18)]
    public Task Toolbar_hides_commit_and_rollback_only_for_read_only_without_a_pending_transaction(string name, int placed, int shownWithoutTxn) => HeadlessHost.Run(async () =>
    {
        var c = Cases[name];
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, c.Json);
            await RoleApp.SettleAsync();
            var keys = app.Get<ContextKeyService>();
            List<string> Shown() =>
            [
                .. app.Window.Toolbar.Children.OfType<Avalonia.Controls.Button>()
                    .Where(b => !b.Classes.Contains(MenuControls.ToolbarOutOfScopeClass))
                    .Select(b => ((RegistryCommand)b.Command!).CommandId),
            ];

            var buttons = app.Window.Toolbar.Children.OfType<Avalonia.Controls.Button>().Count();
            Assert.Equal(placed, buttons);
            Assert.Equal(shownWithoutTxn, Shown().Count);
            Assert.Equal(shownWithoutTxn != placed, keys.Evaluate("readOnlySession"));
            Assert.Equal(shownWithoutTxn == placed, Shown().Contains("oranda.txn.commit") && Shown().Contains("oranda.txn.rollback"));

            // 미커밋이 생기면(Role 을 바꾸기 전 DML 등) 끝낼 길이 보인다.
            keys.Set("txnPending", true);
            await RoleApp.SettleAsync();
            Assert.Equal(placed, Shown().Count);
            keys.Set("txnPending", false);
            await RoleApp.SettleAsync();
            Assert.Equal(shownWithoutTxn, Shown().Count);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Without_a_features_section_the_surfaces_equal_an_explicit_sql_developer_choice() => HeadlessHost.Run(async () =>
    {
        // D-013: 설정 없음 = 기본 Role(SQL 개발자). 직접 R1 을 고른 것과 보이는 면이 모두 같다(시작 화면 카드만 다르다).
        var none = RoleApp.NewDir();
        var r1 = RoleApp.NewDir();
        try
        {
            RoleSurface a, b;
            using (var app = new RoleApp(none, null))
            {
                await RoleApp.SettleAsync();
                a = app.Capture();
                Assert.True(app.Window.GetVisualDescendants().OfType<StartPage>().First().ShowsRoleLink);
            }

            using (var app = new RoleApp(r1, PrdMatrix.FeaturesJson(["sqlDev"])))
            {
                await RoleApp.SettleAsync();
                b = app.Capture();
                Assert.False(app.Window.GetVisualDescendants().OfType<StartPage>().First().ShowsRoleLink);
            }

            Assert.True(a.GateEnabled.SetEquals(b.GateEnabled));
            Assert.True(a.RegistryCommands.SetEquals(b.RegistryCommands));
            Assert.True(a.RegistryPanels.SetEquals(b.RegistryPanels));
            Assert.True(a.MenuCommands.SetEquals(b.MenuCommands));
            Assert.True(a.ToolbarCommands.SetEquals(b.ToolbarCommands));
            Assert.True(a.PaletteCommands.SetEquals(b.PaletteCommands));
            Assert.Equal(a.TopMenus, b.TopMenus);
            Assert.Equal(a.StatusTypes.Order(), b.StatusTypes.Order());
            Assert.Equal(a.StartupTasks.Order(), b.StartupTasks.Order());
            Assert.Equal(9, a.StartupTasks.Count);
            Assert.Equal(10, a.TopMenus.Count); // 「모니터」 가 없다
            Assert.True(a.RegistryCommands.Count < ShellTestHelpers.AppContributions.Commands.Count);
        }
        finally
        {
            RoleApp.DeleteDir(none);
            RoleApp.DeleteDir(r1);
        }
    });

    [Fact]
    public Task Use_all_features_every_count_equals_the_a507d81_baseline() => HeadlessHost.Run(async () =>
    {
        var s = await BaselineAsync();
        var all = ShellTestHelpers.AppContributions;

        Assert.Equal(all.Commands.Count, s.RegistryCommands.Count);
        Assert.Equal(all.Panels.Count, s.RegistryPanels.Count);
        Assert.Equal(11, s.TopMenus.Count);
        Assert.Equal(10, s.StatusTypes.Count);
        Assert.Equal(14, s.StartupTasks.Count);
        Assert.Empty(s.SkippedTasks);
        Assert.Equal(PrdMatrix.AllFeatures.Order(), s.GateEnabled.Order());
        Assert.False(s.GateReadOnlyRole);
        Assert.Equal(PrdMatrix.AllTasks.Order(), s.StartupTasks.Order());

        // 조사 0절의 숫자(명령 281·도구 창 33·상태 칸 10·시작 작업 14) + WP-07 이 더한 것(기능 Role 고르기 명령 1개). 도구 창 33 = 개발 빌드 전용 포함.
        Assert.Equal(282, all.Commands.Count);
    });

    private static RoleSurface? _baseline;

    /// <summary>「모든 기능 쓰기」 한 벌(다른 구성과 견줄 기준 = D-013 전의 features 없음). 한 번만 짓는다.</summary>
    private static async Task<RoleSurface> BaselineAsync()
    {
        if (_baseline is not null)
        {
            return _baseline;
        }

        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, AllCase.Json);
            await RoleApp.SettleAsync();
            return _baseline = app.Capture();
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    }
}
