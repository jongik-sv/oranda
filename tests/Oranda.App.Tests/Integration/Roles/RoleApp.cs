using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Oranda.App.Features.BlockerAlert;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Palette;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>
/// PRD 12.2 표를 그대로 옮긴 독립 정답표(TSK-07-08). 제품의 <see cref="FeatureCatalog"/> 를 읽지 않고, 표(○ = O, · = .)와 D-002·D-006 규칙만으로
/// 켜진 기능 집합을 다시 계산한다. 조립한 앱이 이 집합과 같게 움직이는지 단정하는 기준이다.
/// </summary>
internal static class PrdMatrix
{
    /// <summary>열 순서 = R1~R6.</summary>
    public static readonly string[] RoleOrder = ["sqlDev", "plsqlDev", "myBatisDev", "tuner", "dba", "viewer"];

    private static readonly (string Feature, string Cols)[] Rows =
    [
        ("gridEdit", "OOO.O."),
        ("lobRowView", "OOOOOO"),
        ("dataExport", "OOOOOO"),
        ("dataImport", "OOOOO."),
        ("dedicatedSession", "OO.OO."),
        ("flashback", "OO..O."),
        ("changeJournal", "OOO.O."),
        ("tableSpec", "O...OO"),
        ("myBatisLog", "..O..."),
        ("plsqlEditor", ".O.OO."),
        ("plsqlWorkspace", ".O...."),
        ("plsqlRun", ".O..O."),
        ("myBatis", "..O..."),
        ("planAdvanced", "OOOOO."),
        ("sqlMonitor", "...OO."),
        ("tuningAssistant", "...O.."),
        ("childCursors", "...OO."),
        ("tableStats", "...OO."),
        ("trace", "...OO."),
        ("tuningReport", "...O.."),
        ("sessionMonitor", "...OO."),
        ("lockMonitor", "...OO."),
        ("blockerAlert", "OOOOO."),
        ("dbTools", ".O.OO."),
        ("columnReorder", "....O."),
    ];

    /// <summary>조회 전용만 골랐을 때 켤 수 없는 쓰기 전용 기능(PRD 12.3 RL-08).</summary>
    public static readonly string[] WritesOnly = ["gridEdit", "dataImport", "plsqlEditor", "plsqlWorkspace", "plsqlRun", "columnReorder"];

    /// <summary>시작 때 도는 기능(RL-06): 바꾸면 재시작 뒤 반영.</summary>
    public static readonly string[] StartupBound = ["myBatis", "plsqlWorkspace", "blockerAlert", "changeJournal"];

    public static IReadOnlyList<string> AllFeatures => [.. Rows.Select(r => r.Feature)];

    /// <summary>켜진 기능 집합. roles == null 이면 모든 기능(RL-01·RL-02).</summary>
    public static HashSet<string> Enabled(string[]? roles, string[]? enabled = null, string[]? disabled = null)
    {
        var on = new HashSet<string>(StringComparer.Ordinal);
        if (roles is null)
        {
            on.UnionWith(AllFeatures);
        }
        else
        {
            foreach (var role in roles.Distinct())
            {
                var col = Array.IndexOf(RoleOrder, role);
                if (col < 0)
                {
                    continue;
                }

                on.UnionWith(Rows.Where(r => r.Cols[col] == 'O').Select(r => r.Feature));
            }
        }

        on.UnionWith(enabled ?? []);
        on.ExceptWith(disabled ?? []);
        if (roles is { Length: 1 } && roles[0] == "viewer")
        {
            on.ExceptWith(WritesOnly);
        }

        if (!on.Contains("plsqlEditor"))
        {
            on.Remove("plsqlWorkspace"); // plsqlWorkspace 는 plsqlEditor 가 있어야 한다(TRD 10.4 Requires)
        }

        on.IntersectWith(AllFeatures);
        return on;
    }

    /// <summary>열린 도구 창·메뉴 명령 중 이 명령이 어느 기능에 드는지 사람이 정한 표(조사 1절·TRD 10.2). null = 공통.</summary>
    public static readonly (string CommandId, string? Feature)[] CommandFeatures =
    [
        ("oranda.grid.edit.addRow", "gridEdit"),
        ("oranda.grid.rowView", "lobRowView"),
        ("oranda.view.rowView", "lobRowView"),
        ("oranda.grid.exportExcel", "dataExport"),
        ("oranda.tools.import", "dataImport"),
        ("oranda.file.newDedicatedSqlEditor", "dedicatedSession"),
        ("oranda.grid.flashback.asOf", "flashback"),
        ("oranda.view.changeJournal", "changeJournal"),
        ("oranda.tools.tableSpec", "tableSpec"),
        ("oranda.edit.pasteMyBatisLog", "myBatisLog"),
        ("oranda.file.newPlsqlEditor", "plsqlEditor"),
        ("oranda.plsql.compile", "plsqlEditor"),
        ("oranda.plsql.run", "plsqlRun"),
        ("oranda.plsql.saveWorkspaceFile", "plsqlWorkspace"),
        ("oranda.plsql.openSource", null),
        ("oranda.mybatis.checkCombinations", "myBatis"),
        ("oranda.plan.explain", null),
        ("oranda.plan.actual", null),
        ("oranda.plan.compare", "planAdvanced"),
        ("oranda.plan.pinForCompare", "planAdvanced"),
        ("oranda.monitor.sql", "sqlMonitor"),
        ("oranda.tuning.assistant", "tuningAssistant"),
        ("oranda.tuning.childCursors", "childCursors"),
        ("oranda.tuning.stats", "tableStats"),
        ("oranda.tuning.traceViewer", "trace"),
        ("oranda.tuning.optimizerTrace", "trace"),
        ("oranda.tuning.report", "tuningReport"),
        ("oranda.monitor.sessions", "sessionMonitor"),
        ("oranda.monitor.locks", "lockMonitor"),
        ("oranda.db.info", "dbTools"),
        ("oranda.schema.objectSearch", "dbTools"),
        ("oranda.schema.compileInvalid", "dbTools"),
        ("oranda.schema.reorderColumns", "columnReorder"),
        ("oranda.exec.runStatement", null),
        ("oranda.view.commandPalette", null),
        ("oranda.tools.settings", null),
        ("oranda.tools.roles", null),
        ("oranda.conn.manage", null),
    ];

    /// <summary>시작 작업 14개 중 MyBatis 4개(RL-04, 조사 1.8).</summary>
    public static readonly string[] MyBatisTasks = ["MyBatisConvertReady", "MyBatisFolderReadyTask", "MyBatisWorkspaceWiring", "MapperWatchStartup"];

    /// <summary>a507d81 의 시작 작업 14개.</summary>
    public static readonly string[] AllTasks =
    [
        "EditorDiagnosticsStartup", "MyBatisConvertReady", "MyBatisFolderReadyTask", "MyBatisWorkspaceWiring", "MapperWatchStartup",
        "NavigationWiring", "PlsqlWorkspacePersistence", "RecentFilesStartup", "ResultGridSettingsStartup", "SqlEditorDockWiring",
        "UpdateStartup", "HistoryRecorder", "ChangeJournalRecorder", "PlSqlWarmUpTask",
    ];

    /// <summary>켜진 기능으로 정답 시작 작업 이름 집합(TRD 10.2·10.7.2: PL/SQL 작업 공간 형은 plsqlEditor 에 매달린다).</summary>
    public static string[] ExpectedTasks(HashSet<string> on) =>
    [
        .. AllTasks.Where(t => t switch
        {
            "MyBatisConvertReady" or "MyBatisFolderReadyTask" or "MyBatisWorkspaceWiring" or "MapperWatchStartup" => on.Contains("myBatis"),
            "PlsqlWorkspacePersistence" => on.Contains("plsqlEditor"),
            "ChangeJournalRecorder" => on.Contains("changeJournal"),
            _ => true,
        }),
    ];

    /// <summary>a507d81 의 상태 칸 10개 중 기능에 매인 것: 블로커(blockerAlert)·MyBatis 저장(myBatis). 저장 알림은 공통이다(TSK-07-09).</summary>
    public static string[] ExpectedStatusTypes(HashSet<string> on, IEnumerable<string> allTypes) =>
    [
        .. allTypes.Where(t => t switch
        {
            "BlockerStatusViewModel" => on.Contains("blockerAlert"),
            "MapperSaveStatusViewModel" => on.Contains("myBatis"),
            _ => true,
        }),
    ];

    /// <summary>settings.json 의 features 섹션 글(JSON).</summary>
    public static string FeaturesJson(string[]? roles, string[]? enabled = null, string[]? disabled = null)
    {
        var map = new Dictionary<string, object?>();
        if (roles is not null)
        {
            map["roles"] = roles;
        }

        if (enabled is { Length: > 0 })
        {
            map["enabled"] = enabled;
        }

        if (disabled is { Length: > 0 })
        {
            map["disabled"] = disabled;
        }

        return JsonSerializer.Serialize(map);
    }
}

/// <summary>조립한 앱에서 읽은 「사용자에게 보이는 면」 한 장(메뉴·툴바·우클릭·팔레트·단축키·도구 창·상태 칸·시작 작업).</summary>
internal sealed record RoleSurface(
    HashSet<string> RegistryCommands,
    HashSet<string> MenuCommands,
    IReadOnlyList<string> TopMenus,
    HashSet<string> ToolbarCommands,
    HashSet<string> ContextCommands,
    HashSet<string> PaletteCommands,
    HashSet<string> RegistryPanels,
    HashSet<string> VisibleToolWindows,
    int ViewMenuPanelToggles,
    IReadOnlyList<string> StatusTypes,
    IReadOnlyList<string> StartupTasks,
    IReadOnlyList<string> SkippedTasks,
    HashSet<string> GateEnabled,
    bool GateReadOnlyRole,
    IReadOnlyList<string> PendingRestart);

/// <summary>
/// 통합 시험 한 건의 앱 한 벌: 임시 데이터 폴더(settings.json 을 미리 쓸 수 있음)로 <b>실제 FeatureGate</b> 를 쓰는 전체 앱 조립 + 메인 창.
/// 같은 폴더로 다시 만들면 「재시작」이다(<see cref="ShellHarness"/> 와 같다). 헤드리스 UI 스레드에서만 쓴다.
/// </summary>
internal sealed class RoleApp : IDisposable
{
    private readonly ShellHarness _harness;

    public RoleApp(string dataDirectory, string? featuresJson)
    {
        Directory.CreateDirectory(dataDirectory);
        if (featuresJson is not null)
        {
            File.WriteAllText(Path.Combine(dataDirectory, "settings.json"), "{\"schemaVersion\":1,\"features\":" + featuresJson + "}");
        }

        _harness = new ShellHarness(dataDirectory);
    }

    public ShellHarness Harness => _harness;

    public ServiceContainer Services => _harness.Services;

    public Oranda.App.Views.MainWindow Window => _harness.Window;

    public FeatureGate Gate => (FeatureGate)_harness.Get<IFeatureGate>();

    public T Get<T>()
        where T : class => _harness.Get<T>();

    public static string NewDir() => Path.Combine(Path.GetTempPath(), "oranda-roleit-" + Guid.NewGuid().ToString("N"));

    public static void DeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>지금 화면에 있는 면을 읽는다. 메뉴는 창이 다시 짓는 중일 수 있어 호출 전에 <see cref="Settle"/> 를 부른다.</summary>
    public RoleSurface Capture()
    {
        var registry = Get<ICommandRegistry>();
        var panels = Get<IToolPanelRegistry>();
        var shell = Get<ShellController>();
        var keymap = Get<KeymapService>();
        var keys = Get<ContextKeyService>();
        var docking = Get<DockWorkspace>();

        var tops = Window.Menus.ItemsSource!.Cast<MenuItem>().ToList();
        var menu = new HashSet<string>(StringComparer.Ordinal);
        var toggles = 0;
        foreach (var top in tops)
        {
            Walk(top, menu, ref toggles);
        }

        var toolbar = Window.Toolbar.Children.OfType<Button>().Select(b => (b.Command as RegistryCommand)?.CommandId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var context = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in new[] { "monitor", "grid", "schema", "editor" })
        {
            var ignored = 0;
            foreach (var item in ShellContextMenus.Factory!(surface).OfType<MenuItem>())
            {
                Walk(item, context, ref ignored);
            }
        }

        var palette = new CommandPaletteViewModel(registry, keys, shell.Text, keymap.ShortcutText).Search(string.Empty).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var statusTypes = Window.ViewModel!.StatusItems.Select(e => e.Item.GetType().Name).ToList();

        Services.ScanDeferred();
        var skipped = new List<Type>();
        var tasks = Services.GetStartupTasks(skipped.Add).Select(t => t.GetType().Name).ToList();

        return new RoleSurface(
            registry.Commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal),
            menu,
            [.. tops.Select(t => AutomationProperties.GetName(t) ?? string.Empty)],
            toolbar,
            context,
            palette,
            panels.Panels.Select(p => p.Id).ToHashSet(StringComparer.Ordinal),
            panels.Panels.Where(p => docking.IsToolVisible(p.Id)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal),
            toggles,
            statusTypes,
            tasks,
            [.. skipped.Select(t => t.Name)],
            Gate.EnabledFeatures.ToHashSet(StringComparer.Ordinal),
            Gate.ReadOnlyRole,
            [.. Gate.PendingRestart]);
    }

    private static void Walk(MenuItem item, HashSet<string> commands, ref int toggles)
    {
        if (item.Command is RegistryCommand rc)
        {
            commands.Add(rc.CommandId);
        }

        if (item.ToggleType == MenuItemToggleType.CheckBox && item.Command is null)
        {
            toggles++;
        }

        if (item.ItemsSource is not null)
        {
            foreach (var child in item.ItemsSource.OfType<MenuItem>())
            {
                Walk(child, commands, ref toggles);
            }
        }
    }

    /// <summary>
    /// 메뉴 다시 짓기(<c>CommandsChanged</c> → UI 스레드 게시)가 끝나길 기다린다. 조건이 없으면 UI 작업 큐만 비운다.
    /// </summary>
    public static async Task SettleAsync(Func<bool>? until = null)
    {
        if (until is not null)
        {
            await HeadlessHost.WaitUntil(until);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(30);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>블로커 칸(상태 칸)을 찾는다. 기능이 꺼져 만들지 않았으면 null.</summary>
    public BlockerStatusViewModel? BlockerChip() =>
        Window.ViewModel!.StatusItems.Select(e => e.Item).OfType<BlockerStatusViewModel>().SingleOrDefault();

    public void Dispose() => _harness.Dispose();
}
