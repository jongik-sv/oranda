using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Integration.Roles;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration.Ux;

/// <summary>
/// TSK-09-10 통합 1: 실제 FeatureGate 로 앱을 조립해 구성(모든 기능·고르지 않음(D-013 = R1)·R1~R6·R1+R4·Role 0개)마다 TRD 11.11 의 7개 숫자를 다시 센다.
/// 숫자는 결과서 docs/oranda1/itest/TSK-09-10.md 의 「지금」 열과 같고, 바뀌면 이 시험이 먼저 알린다(메뉴를 늘리거나 묶음을 풀면 결과서도 고친다).
/// 「조사」 숫자(413a33d)는 같은 도우미(<see cref="UxWidth"/>)를 413a33d 스냅숏에 넣어 다시 센 값이고 조사 1.1 표와 같다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RoleItestUi")]
public sealed class MenuWidthTests(ITestOutputHelper output)
{
    /// <param name="Json">settings.json 의 features 글. null = 파일 없음.</param>
    /// <param name="Items">(1) 메뉴 항목 총수.</param>
    /// <param name="Top">(2) 최상위 메뉴 수.</param>
    /// <param name="Wide">(3) 한 단계 15줄을 넘는 메뉴.</param>
    /// <param name="Grid">(5) 그리드 오른쪽 클릭 한 단계 줄 수.</param>
    /// <param name="Toolbar">(6) 툴바 단추 수 — 미커밋 없음.</param>
    /// <param name="ToolbarPending">(6) 툴바 단추 수 — 미커밋 있음.</param>
    /// <param name="Tools">(7) 기본 배치에서 SQL 탭과 함께 보이는 도구 창 수.</param>
    /// <param name="Registered">등록 도구 창 수(개발 빌드 전용 제외).</param>
    /// <param name="SqlTab">보조: SQL 탭 활성 상태에서 보이는 메뉴 명령 수.</param>
    /// <param name="Palette">명령 팔레트 줄 수.</param>
    /// <param name="Survey">조사(413a33d) 메뉴 항목 총수 — 차이 설명 시험용.</param>
    /// <param name="SurveyTop">조사(413a33d) 최상위 메뉴 수.</param>
    internal sealed record Config(
        string? Json, int Items, int Top, string[] Wide, int Grid, int Toolbar, int ToolbarPending, int Tools, int Registered, int SqlTab, int Palette, int Survey, int SurveyTop);

    internal static readonly Dictionary<string, Config> Configs = new(StringComparer.Ordinal)
    {
        ["all-features"] = new(PrdMatrix.FeaturesJson(null, allFeatures: true), 272, 11, ["view/도구 창=16", "sql=27", "database=22", "monitor=21", "tuning=22"], 15, 18, 18, 5, 16, 223, 282, 274, 11),
        ["none(default-R1)"] = new(null, 203, 10, ["sql=23"], 15, 14, 14, 5, 13, 190, 205, 209, 10),
        ["R1"] = new(PrdMatrix.FeaturesJson(["sqlDev"]), 203, 10, ["sql=23"], 15, 14, 14, 5, 13, 190, 205, 209, 10),
        ["R2"] = new(PrdMatrix.FeaturesJson(["plsqlDev"]), 216, 10, ["sql=24", "database=19"], 15, 16, 16, 5, 14, 198, 218, 219, 10),
        ["R3"] = new(PrdMatrix.FeaturesJson(["myBatisDev"]), 210, 10, ["sql=26"], 14, 14, 14, 5, 15, 188, 218, 216, 10),
        ["R4"] = new(PrdMatrix.FeaturesJson(["tuner"]), 236, 11, ["sql=23", "database=17", "monitor=21", "tuning=22"], 13, 18, 18, 5, 13, 198, 238, 239, 11),
        ["R5"] = new(PrdMatrix.FeaturesJson(["dba"]), 254, 11, ["sql=24", "database=21", "monitor=21", "tuning=19"], 15, 18, 18, 5, 14, 214, 256, 256, 11),
        ["R6"] = new(PrdMatrix.FeaturesJson(["viewer"]), 178, 9, ["sql=22"], 13, 12, 14, 5, 12, 164, 182, 184, 9),
        ["R1+R4"] = new(PrdMatrix.FeaturesJson(["sqlDev", "tuner"]), 254, 11, ["sql=24", "database=19", "monitor=21", "tuning=22"], 15, 18, 18, 5, 14, 215, 258, 257, 11),
        // TSK-14-17: SQL 모니터 기능이 있는 구성(all-features·R4·R5·R1+R4)은 튜닝 메뉴 「튜닝 추천」 한 줄이 늘었다(항목·tuning·SQL 탭·팔레트 +1).
        ["roles-empty"] = new(PrdMatrix.FeaturesJson([]), 171, 9, ["sql=22"], 9, 14, 14, 5, 11, 160, 175, 176, 9),
    };

    /// <summary>조사(413a33d) 뒤 모든 구성에 더해진 메뉴 줄: 보기 &gt; 도구 창 ▸ 「세션」 토글(TSK-14-04) 1, 도움말 「새 기능(릴리스 노트)」 1.</summary>
    private const int AddedSinceSurvey = 2;

    /// <summary>보기 메뉴에서 자리를 뺀 명령(「도구 창 ▸」 토글과 같은 창, UX-05).</summary>
    private static readonly string[] ViewDuplicates =
        ["oranda.view.outline", "oranda.view.output", "oranda.view.messages", "oranda.view.plan", "oranda.view.rowView", "oranda.view.compileErrors", "oranda.view.templates"];

    /// <summary>Role 과 관계없는 공통 메뉴(PRD 13.3 묶음 대상).</summary>
    private static readonly string[] CommonMenus = ["edit", "file", "result"];

    public static IEnumerable<object[]> ConfigNames() => Configs.Keys.Select(k => new object[] { k });

    private static async Task<T> WithApp<T>(string name, Func<RoleApp, Task<T>> body)
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Configs[name].Json);
            await RoleApp.SettleAsync();
            return await body(app);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    }

    private static MenuNode TopOf(RoleApp app, IReadOnlyList<MenuNode> bar, string id) =>
        bar.Single(b => b.Title == app.Get<ShellController>().Text.Lookup($"Menu.{id}"));

    [Theory]
    [MemberData(nameof(ConfigNames))]
    public Task Every_configuration_has_the_recounted_numbers(string name) => HeadlessHost.Run(async () =>
    {
        var want = Configs[name];
        var got = await WithApp(name, UxWidth.MeasureAsync);
        output.WriteLine(got.Row(name));

        Assert.Equal(want.Items, got.TotalItems);
        Assert.Equal(want.Top, got.TopMenus);
        Assert.Equal(want.Wide, got.WideMenus);
        Assert.Equal(want.Grid, got.GridLines);
        Assert.Equal(want.Toolbar, got.ToolbarShown);
        Assert.Equal(want.ToolbarPending, got.ToolbarShownPending);
        Assert.Equal(want.Tools, got.ToolsVisibleWithSqlTab);
        Assert.Equal(want.Registered, got.ToolsRegistered);
        Assert.Equal(want.SqlTab, got.SqlTabVisibleItems);
        Assert.Equal(want.Palette, got.PaletteCommands);
    });

    /// <summary>
    /// 지금 모든 기능의 15줄 넘는 메뉴는 5 — 기존 4(sql·database·monitor·tuning)에 보기 &gt; 도구 창 ▸ 16줄(세션 창 TSK-14-04 가 토글 한 줄을 더함)이 더해졌다.
    /// PRD 13.5 목표도 이 5 를 받아들여 「5 이하」 로 고쳤다(2026-10-28 사용자 결정, TSK-14-19).
    /// </summary>
    private const int AllFeaturesWideLimit = 5;

    /// <summary>PRD 13.5·D-023 목표: 15줄 넘는 메뉴 모든 기능 5 이하·R1 2 이하·R6 1 이하, 편집·파일·결과·그리드 오른쪽 클릭 각 15 이하, 보기 7 이하, 최상위 메뉴 수는 조사 이하.</summary>
    [Theory]
    [MemberData(nameof(ConfigNames))]
    public Task Prd_13_5_width_targets_hold(string name) => HeadlessHost.Run(async () =>
    {
        var got = await WithApp(name, UxWidth.MeasureAsync);
        var limit = name switch
        {
            "all-features" => AllFeaturesWideLimit,
            "none(default-R1)" or "R1" => 2,
            "R6" => 1,
            _ => int.MaxValue,
        };
        Assert.True(got.WideMenus.Count <= limit, $"{name}: {string.Join(", ", got.WideMenus)}");
        foreach (var id in CommonMenus)
        {
            Assert.True(got.TopLines[id] <= UxWidth.WideLimit, $"{name} {id}={got.TopLines[id]}");
        }

        Assert.True(got.GridLines <= UxWidth.WideLimit, $"{name} grid={got.GridLines}");
        Assert.True(got.TopLines["view"] <= 7, $"{name} view={got.TopLines["view"]}");
        Assert.True(got.TopMenus <= Configs[name].SurveyTop);
        Assert.DoesNotContain(got.WideMenus, w => w.StartsWith("edit", StringComparison.Ordinal) || w.StartsWith("file", StringComparison.Ordinal) || w.StartsWith("result", StringComparison.Ordinal));
    });

    /// <summary>
    /// 메뉴 항목 총수 차이 = 뺀 중복(보기 7쌍 중 그 구성에 있는 것·도구 메뉴 템플릿 1·보기 레이아웃 초기화 1) − 더한 PL/SQL ▸(그 구성에 있는 것)(PRD 13.5),
    /// 거기에 조사 뒤에 더한 2줄(<see cref="AddedSinceSurvey"/>)을 더한다.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigNames))]
    public Task Total_item_difference_is_only_the_removed_duplicates_and_the_added_plsql_submenu(string name) => HeadlessHost.Run(async () =>
    {
        var (items, dups, plsql) = await WithApp(name, async app =>
        {
            var bar = UxWidth.MenuBar(app);
            var registry = app.Get<ICommandRegistry>();
            var dupCount = ViewDuplicates.Count(id => registry.Find(id) is not null);
            var templates = registry.Find("oranda.view.templates") is not null ? 1 : 0;
            var reset = registry.Find("oranda.view.resetLayout") is not null ? 1 : 0;
            var recommend = registry.Find("oranda.tuning.recommend") is not null ? 1 : 0; // TSK-14-17 튜닝 메뉴 「튜닝 추천」(SQL 모니터 기능이 있는 구성만)
            var sql = TopOf(app, bar, "sql");
            var sub = sql.Children.SingleOrDefault(c => c.Kind == MenuNodeKind.Submenu && c.Key == "Menu.sql.plsql");
            await Task.CompletedTask;
            return (bar.Sum(b => UxWidth.Items(b.Children)) - recommend, dupCount + templates + reset, sub is null ? 0 : UxWidth.Items(sub.Children));
        });

        output.WriteLine($"{name}: 조사 {Configs[name].Survey} − 중복 {dups} + PL/SQL {plsql} + 추가 {AddedSinceSurvey} = {Configs[name].Survey - dups + plsql + AddedSinceSurvey}, 지금 {items}");
        Assert.Equal(Configs[name].Survey - dups + plsql + AddedSinceSurvey, items);
        if (name is "all-features" or "R2" or "R5")
        {
            Assert.Equal(9, dups);
            Assert.Equal(4, plsql);
        }
    });

    /// <summary>
    /// 개발 빌드(Debug)는 확장 시험 창(DevBuildOnly)이 보기 &gt; 도구 창 ▸ 에 한 줄 더 있어 그 하위 메뉴가 17줄이다(게시본 16). 게시본에는 없다(결과서 「차이 이유」).
    /// </summary>
    [Fact]
    public Task Dev_build_adds_only_the_extension_test_toggle() => HeadlessHost.Run(async () =>
    {
        var (release, dev, devPanels) = await WithApp("all-features", async app =>
        {
            await Task.CompletedTask;
            var ids = MenuModelBuilder.TopMenus.ToList();
            var r = UxWidth.MenuBar(app);
            var d = UxWidth.MenuBar(app, devBuild: true);
            return (UxWidth.Wide(r, ids), UxWidth.Wide(d, ids), app.Get<IToolPanelRegistry>().Panels.Where(p => p.DevBuildOnly).Select(p => p.Id).ToList());
        });

        Assert.Equal(["oranda.panel.extensionTest"], devPanels);
        Assert.Equal(release.Count, dev.Count);
        Assert.Contains(release, w => w == "view/도구 창=16");
        Assert.Contains(dev, w => w == "view/도구 창=17");
    });

    /// <summary>설정 「기능」 탭 첫 화면 입력 칸(라디오·확인란·단추·고급 머리 토글): 조사 33 → 10 이하(PRD 13.5).</summary>
    [Theory]
    [InlineData("all-features", 8)]
    [InlineData("none(default-R1)", 8)]
    [InlineData("R1", 8)]
    [InlineData("R4", 9)]
    [InlineData("R6", 8)]
    [InlineData("R1+R4", 10)] // 맞지 않는 조합 → 「지금 조합」 라디오 + 팩 링크
    [InlineData("roles-empty", 9)]
    public Task Features_tab_opens_with_ten_or_fewer_inputs(string name, int expected) => HeadlessHost.Run(async () =>
    {
        var count = await WithApp(name, async app =>
        {
            Assert.True(await app.Get<ICommandRegistry>().ExecuteAsync(FeatureCommandIds.Roles, app.Get<ContextKeyService>()));
            var window = Assert.IsType<SettingsWindow>(app.Get<ShellController>().LastWindow);
            try
            {
                await RoleApp.SettleAsync();
                window.UpdateLayout();
                await RoleApp.SettleAsync();
                var view = window.GetVisualDescendants().OfType<FeatureSettingsView>().Single();
                return view.GetVisualDescendants().OfType<Control>()
                    .Where(c => c is RadioButton or CheckBox or Button or ToggleButton && c.IsEffectivelyVisible && !c.GetVisualAncestors().OfType<ScrollBar>().Any())
                    .Distinct()
                    .Count();
            }
            finally
            {
                window.Close();
            }
        });

        output.WriteLine($"{name}: 기능 탭 첫 화면 입력 칸 {count}");
        Assert.True(count <= 10, $"{name}: {count}");
        Assert.Equal(expected, count);
    });
}
