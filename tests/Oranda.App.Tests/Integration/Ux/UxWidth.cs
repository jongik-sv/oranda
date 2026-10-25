using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Integration.Roles;
using Oranda.Core.Composition;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Integration.Ux;

/// <summary>
/// TRD 11.11 의 「메뉴 너비」 한 장(TSK-09-10). 실제 FeatureGate 로 조립한 앱(<see cref="RoleApp"/>)에서 주 창이 쓰는 것과 같은 입력
/// (레지스트리 명령·키맵 글·도구 창 목록·게이트)으로 메뉴 모델을 다시 지어 센다.
/// </summary>
/// <param name="TotalItems">(1) 메뉴 항목 총수 — 조사 1.1 주1: 명령 자리(하위 메뉴 안·옮겨 실은 오른쪽 클릭 포함) + 보기 &gt; 도구 창 토글. 구분선·열 때 채우는 줄 제외, 한 명령이 두 자리면 2.</param>
/// <param name="TopMenus">(2) 최상위 메뉴 수(그린 것만).</param>
/// <param name="WideMenus">(3) 한 단계 줄 수가 15를 넘는 메뉴(최상위·하위 메뉴 모두, When 숨김 미적용). 「경로=줄 수」.</param>
/// <param name="TopLines">(4) 최상위 메뉴 id → 한 단계 줄 수(명령·하위 메뉴 머리·토글, 구분선 제외).</param>
/// <param name="TopItems">최상위 메뉴 id → 항목 총수(조사 1.1 둘째 표와 같은 기준).</param>
/// <param name="GridLines">(5) 그리드 오른쪽 클릭 한 단계 줄 수.</param>
/// <param name="GridItems">그리드 오른쪽 클릭 명령 총수(하위 메뉴 안 포함).</param>
/// <param name="ToolbarPlaced">툴바에 놓인 단추 수.</param>
/// <param name="ToolbarShown">(6) 미커밋 없음일 때 보이는 툴바 단추 수.</param>
/// <param name="ToolbarShownPending">(6) 미커밋 있음일 때 보이는 툴바 단추 수.</param>
/// <param name="ToolsRegistered">등록 도구 창 수(Tool 종류, 개발 빌드 전용 제외).</param>
/// <param name="ToolsVisibleWithSqlTab">(7) 기본 배치에서 SQL 탭과 함께 보이는 도구 창 수.</param>
/// <param name="SqlTabVisibleItems">보조 열: SQL 탭이 활성일 때 When 규칙으로 보이는 명령 항목 수(도구 창 토글 제외, 조사 1.1 주2).</param>
/// <param name="Submenus">하위 메뉴 수(도구 창·열 때 채우는 하위 메뉴 포함).</param>
/// <param name="PaletteCommands">명령 팔레트 줄 수(빈 검색).</param>
internal sealed record UxWidth(
    int TotalItems,
    int TopMenus,
    IReadOnlyList<string> WideMenus,
    IReadOnlyDictionary<string, int> TopLines,
    IReadOnlyDictionary<string, int> TopItems,
    int GridLines,
    int GridItems,
    int ToolbarPlaced,
    int ToolbarShown,
    int ToolbarShownPending,
    int ToolsRegistered,
    int ToolsVisibleWithSqlTab,
    int SqlTabVisibleItems,
    int Submenus,
    int PaletteCommands)
{
    public const int WideLimit = 15;

    /// <summary>한 줄 요약(결과서·시험 출력용).</summary>
    public string Row(string name) =>
        $"{name} | 항목 {TotalItems} | 최상위 {TopMenus} | 15넘음 {WideMenus.Count} [{string.Join(", ", WideMenus)}] | 그리드 {GridLines}({GridItems}) | 툴바 {ToolbarShown}/{ToolbarShownPending}(놓임 {ToolbarPlaced}) | 도구 창 {ToolsVisibleWithSqlTab}/{ToolsRegistered} | SQL 탭 보임 {SqlTabVisibleItems} | 하위 메뉴 {Submenus} | 팔레트 {PaletteCommands}"
        + Environment.NewLine + "    한 단계: " + string.Join(" ", MenuModelBuilder.TopMenus.Select(t => $"{t}={(TopLines.TryGetValue(t, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}"))
        + Environment.NewLine + "    항목: " + string.Join(" ", MenuModelBuilder.TopMenus.Select(t => $"{t}={(TopItems.TryGetValue(t, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}"));

    /// <summary>
    /// 주 창과 같은 입력으로 메뉴 막대 모델을 짓는다(MainWindow.RebuildMenus). <paramref name="devBuild"/> 가 거짓이면 개발 빌드 전용 도구 창
    /// (<see cref="ToolPanelDefinition.DevBuildOnly"/>, 확장 시험 창)을 빼고 짓는다 — 게시본과 같은 모양이고 조사 1.1 주5 와 같은 기준이다.
    /// </summary>
    public static IReadOnlyList<MenuNode> MenuBar(RoleApp app, bool devBuild = false) =>
        MenuModelBuilder.BuildMenuBar(
            app.Get<ICommandRegistry>().Commands,
            app.Get<ShellController>().Text,
            app.Get<KeymapService>().ShortcutText,
            app.Get<IToolPanelRegistry>().Panels.Where(p => devBuild || !p.DevBuildOnly),
            app.Gate.IsEnabled);

    /// <summary>한 단계 줄 수가 15를 넘는 메뉴(최상위·하위 메뉴 모두). 「경로=줄 수」.</summary>
    public static List<string> Wide(IReadOnlyList<MenuNode> bar, IReadOnlyList<string> ids)
    {
        var wide = new List<string>();
        for (var i = 0; i < bar.Count; i++)
        {
            foreach (var (path, node) in Walk(bar[i], ids[i]))
            {
                var n = Lines(node.Children);
                if (n > WideLimit)
                {
                    wide.Add($"{path}={n}");
                }
            }
        }

        return wide;
    }

    /// <summary>그리드 오른쪽 클릭 모델(MainWindow 의 ShellContextMenus.Factory 와 같은 입력).</summary>
    public static IReadOnlyList<MenuNode> GridContext(RoleApp app) =>
        MenuModelBuilder.BuildContext(app.Get<ICommandRegistry>().Commands, app.Get<ShellController>().Text, app.Get<KeymapService>().ShortcutText, "grid");

    public static int Lines(IReadOnlyList<MenuNode> level) => level.Count(n => n.Kind != MenuNodeKind.Separator);

    public static int Items(IReadOnlyList<MenuNode> level) =>
        level.Sum(n => n.Kind switch
        {
            MenuNodeKind.Command or MenuNodeKind.PanelToggle => 1,
            MenuNodeKind.Submenu => Items(n.Children),
            _ => 0,
        });

    private static IEnumerable<(string Path, MenuNode Node)> Walk(MenuNode node, string path)
    {
        yield return (path, node);
        foreach (var child in node.Children.Where(c => c.Kind == MenuNodeKind.Submenu))
        {
            foreach (var x in Walk(child, path + "/" + (child.Key?.Split('.')[^1] ?? child.Title)))
            {
                yield return x;
            }
        }
    }

    /// <summary>When 을 적용해 보이는 명령 ID(하위 메뉴 안까지, 토글 제외).</summary>
    public static List<string> VisibleCommandIds(MenuNode top, IContextKeys keys)
    {
        var state = MenuVisibility.Evaluate(top, keys);
        var result = new List<string>();
        void Walk(IReadOnlyList<MenuNode> nodes, IReadOnlyList<MenuState> states)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (!states[i].Visible)
                {
                    continue;
                }

                if (nodes[i].Kind == MenuNodeKind.Command)
                {
                    result.Add(nodes[i].CommandId!);
                }

                Walk(nodes[i].Children, states[i].Children);
            }
        }

        Walk(top.Children, state.Children);
        return result;
    }

    /// <summary>When 을 적용해 보이는 명령 수(하위 메뉴 안까지, 토글 제외).</summary>
    public static int VisibleCommands(MenuNode top, IContextKeys keys) => VisibleCommandIds(top, keys).Count;

    /// <summary>최상위 메뉴 하나(그린 것). 없으면 null.</summary>
    public static MenuNode? Top(RoleApp app, string id) =>
        MenuBar(app).SingleOrDefault(b => b.Title == app.Get<ShellController>().Text.Lookup($"Menu.{id}"));

    /// <summary>주 메뉴가 쓰는 문맥 키(PL/SQL 문서 활성은 없음으로 본다).</summary>
    public static IContextKeys MenuKeys(RoleApp app) => new MenuScopeKeys(app.Get<ContextKeyService>(), () => false);

    /// <summary>보이는 툴바 단추(When 으로 숨은 단추 제외, TSK-09-04).</summary>
    public static List<string> ShownToolbar(RoleApp app) =>
    [
        .. app.Window.Toolbar.Children.OfType<Button>()
            .Where(b => !b.Classes.Contains(MenuControls.ToolbarOutOfScopeClass))
            .Select(b => ((RegistryCommand)b.Command!).CommandId),
    ];

    /// <summary>SQL 편집기 도킹 배선(시작 작업)을 돌리고 새 SQL 탭을 연다(DefaultLayoutAutoOpenTests 와 같은 길).</summary>
    public static async Task<SqlEditorViewModel> OpenSqlTabAsync(RoleApp app, string text = "select 1 from dual")
    {
        app.Services.ScanDeferred();
        var wiring = app.Services.GetService<IEnumerable<IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        var tab = SqlEditorHost.Current.NewDocument(text);
        Dispatcher.UIThread.RunJobs();
        return tab;
    }

    /// <summary>
    /// 지금 앱에서 센다. 툴바 미커밋 있음 칸은 <c>txnPending</c> 을 잠깐 켜서 보고 되돌린다. SQL 탭은 이 안에서 하나 연다.
    /// </summary>
    public static async Task<UxWidth> MeasureAsync(RoleApp app)
    {
        await RoleApp.SettleAsync();
        var bar = MenuBar(app);
        var ids = MenuModelBuilder.TopMenus.Where(id => bar.Any(b => b.Title == app.Get<ShellController>().Text.Lookup($"Menu.{id}"))).ToList();
        Assert.Equal(ids.Count, bar.Count);

        // 모델이 창에 그린 것과 같은지(최상위 이름·순서).
        var drawn = app.Window.Menus.ItemsSource!.Cast<MenuItem>().Select(m => Avalonia.Automation.AutomationProperties.GetName(m)).ToList();
        Assert.Equal(bar.Select(b => b.Title), drawn);

        var topLines = new Dictionary<string, int>(StringComparer.Ordinal);
        var topItems = new Dictionary<string, int>(StringComparer.Ordinal);
        var wide = Wide(bar, ids);
        var submenus = 0;
        for (var i = 0; i < bar.Count; i++)
        {
            topLines[ids[i]] = Lines(bar[i].Children);
            topItems[ids[i]] = Items(bar[i].Children);
            submenus += Walk(bar[i], ids[i]).Count() - 1;
        }

        var grid = GridContext(app);
        var keys = app.Get<ContextKeyService>();
        var placed = app.Window.Toolbar.Children.OfType<Button>().Count();
        var shown = ShownToolbar(app).Count;
        var hadPending = keys.Get("txnPending");
        keys.Set("txnPending", true);
        await RoleApp.SettleAsync();
        var shownPending = ShownToolbar(app).Count;
        keys.Set("txnPending", hadPending);
        await RoleApp.SettleAsync();

        await OpenSqlTabAsync(app);

        await RoleApp.SettleAsync();
        var panels = app.Get<IToolPanelRegistry>().Panels.Where(p => p.Kind == ToolPanelKind.Tool && !p.DevBuildOnly).ToList();
        var docking = app.Get<DockWorkspace>();
        var visibleTools = panels.Count(p => docking.IsToolVisible(p.Id));
        var scope = MenuKeys(app);
        var sqlTabVisible = MenuBar(app).Sum(top => VisibleCommands(top, scope));
        var palette = app.Capture().PaletteCommands.Count;

        return new UxWidth(
            bar.Sum(b => Items(b.Children)),
            bar.Count,
            wide,
            topLines,
            topItems,
            Lines(grid),
            Items(grid),
            placed,
            shown,
            shownPending,
            panels.Count,
            visibleTools,
            sqlTabVisible,
            submenus,
            palette);
    }
}
