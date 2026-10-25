using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Layout;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Testing;
using KeyBinding = Oranda.Core.Shell.KeyBinding;

namespace Oranda.App.Tests.Features.Roles;

/// <summary>
/// TSK-07-02: 명령·도구 창 레지스트리 거르기(Commands·Panels·Find·CanExecute·ExecuteAsync·실행 중 Register), features 없음 = 거르기 전과 같은 집합,
/// 게이트 바뀜 → CommandsChanged/PanelsChanged 한 번·메뉴 다시 짓기(Headless), 빈 최상위 메뉴 숨김, 꺼진 명령 단축키 무반응, 시작 순서(D-005),
/// 열린 도구 창은 닫지 않음·다시 켜면 기본 위치(D-010).
/// </summary>
public sealed class RegistryFilterTests
{
    private const string LockMonitor = "oranda.monitor.locks";
    private const string SessionMonitor = "oranda.monitor.sessions";
    private const string RowView = "oranda.panel.rowView";
    private const string ChangeJournalPanel = "oranda.panel.changeJournal";

    // D-010 시험용 도구 창: plsqlEditor 는 시작 때 도는 기능이 아니라 실행 중 다시 켜면 바로 돌아온다.
    private const string CompileErrors = "oranda.panel.compileErrors";

    private static readonly string[] ContextSurfaces = ["monitor", "grid", "schema", "editor"];

    private static ShellContributions Contributions => ShellTestHelpers.AppContributions;

    private static FeaturesSettings Roles(params string[] roles) => new() { Roles = roles };

    private static (CommandRegistry Commands, ToolPanelRegistry Panels) Registries(IFeatureGate gate) =>
        (new CommandRegistry(Contributions.Commands, _ => new NoopHandler(), gate), new ToolPanelRegistry(Contributions.Panels, includeDevBuildOnly: true, gate));

    private static CommandDefinition Def(string id, params string[] aliases) =>
        new() { Id = id, Title = id, Category = CommandCategory.Tools, Aliases = aliases, Placements = ["menu:tools/x@10"] };

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "oranda-registry-" + Guid.NewGuid().ToString("N"));

    private static string WriteSettings(string? featuresJson)
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        if (featuresJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"schemaVersion\":1,\"features\":" + featuresJson + "}");
        }

        return dir;
    }

    // ── features 없음 = 거르기 전과 같음 ─────────────────────────────

    [Fact]
    public void Without_a_features_section_the_filtered_sets_equal_the_unfiltered_sets()
    {
        var gate = new FeatureGate();
        var (commands, panels) = Registries(gate);
        var plainCommands = new CommandRegistry(Contributions.Commands);
        var plainPanels = new ToolPanelRegistry(Contributions.Panels, includeDevBuildOnly: true);
        var raised = 0;
        commands.CommandsChanged += (_, _) => raised++;
        panels.PanelsChanged += (_, _) => raised++;

        gate.Resolve(null);

        Assert.Equal(Contributions.Commands.Count, commands.Commands.Count);
        Assert.Equal(plainCommands.Commands, commands.Commands);
        Assert.Equal(plainPanels.Panels, panels.Panels);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Dev_build_only_panels_are_still_filtered_by_the_build_flag()
    {
        var release = new ToolPanelRegistry(Contributions.Panels, includeDevBuildOnly: false, new FeatureGate());

        Assert.Null(release.Find("oranda.panel.extensionTest"));
        Assert.Equal(Contributions.Panels.Count(p => !p.DevBuildOnly), release.Panels.Count);
    }

    // ── 거르기 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_commands_vanish_from_every_lookup()
    {
        var gate = FakeFeatureGate.ForRoles(RoleIds.Viewer);
        var (commands, panels) = Registries(gate);

        Assert.DoesNotContain(commands.Commands, c => c.Id is LockMonitor or SessionMonitor or "oranda.tools.import");
        Assert.Null(commands.Find(LockMonitor));
        Assert.False(commands.CanExecute(LockMonitor, FakeContextKeys.AllTrue));
        Assert.False(await commands.ExecuteAsync(LockMonitor, FakeContextKeys.AllTrue));
        Assert.Null(panels.Find(ChangeJournalPanel));
        Assert.DoesNotContain(panels.Panels, p => p.Id == ChangeJournalPanel);

        // 공통 명령·공통으로 덮어쓴 명령은 남는다.
        Assert.NotNull(commands.Find("oranda.plsql.openSource"));
        Assert.NotNull(commands.Find("oranda.view.commandPalette"));
        Assert.NotNull(panels.Find(RowView)); // lobRowView 는 R6 기본 켬
    }

    [Fact]
    public void Disabled_commands_are_hidden_from_aliases_too_but_keep_their_names()
    {
        var gate = new FakeFeatureGate().Disable(FeatureIds.DataImport);
        var registry = new CommandRegistry([], features: gate);
        using var reg = registry.Register(Def("oranda.tools.import", "imp.alias"), new NoopHandler());

        Assert.Null(registry.Find("imp.alias"));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Def("other", "imp.alias"), new NoopHandler())); // 꺼져도 별칭은 차지한다
        Assert.Throws<InvalidOperationException>(() => registry.Register(Def("oranda.tools.import"), new NoopHandler()));
    }

    [Fact]
    public void Commands_registered_at_runtime_follow_the_same_rule_and_reappear_when_enabled()
    {
        var gate = new FakeFeatureGate().Disable(FeatureIds.DataImport);
        var registry = new CommandRegistry([], features: gate);
        var raised = 0;
        registry.CommandsChanged += (_, _) => raised++;

        var reg = registry.Register(Def("oranda.tools.import"), new NoopHandler());
        Assert.Equal(1, raised); // Register 는 늘 알린다(기존 동작)
        Assert.Empty(registry.Commands);

        gate.Enable(FeatureIds.DataImport).RaiseChanged();
        Assert.Equal(2, raised);
        Assert.NotNull(registry.Find("oranda.tools.import"));

        gate.RaiseChanged(); // 결과가 같으면 알리지 않는다
        Assert.Equal(2, raised);

        reg.Dispose();
        Assert.Empty(registry.Commands);
    }

    [Fact]
    public void Gate_change_raises_commands_and_panels_changed_once_each()
    {
        var gate = new FeatureGate();
        gate.Resolve(null);
        var (commands, panels) = Registries(gate);
        int c = 0, p = 0;
        commands.CommandsChanged += (_, _) => c++;
        panels.PanelsChanged += (_, _) => p++;

        gate.Resolve(Roles(RoleIds.SqlDev));

        Assert.Equal(1, c);
        Assert.Equal(1, p); // MyBatis·PL/SQL 패널이 빠진다
        Assert.Null(panels.Find("oranda.panel.mybatisStatements"));

        gate.Resolve(new FeaturesSettings { Roles = [RoleIds.SqlDev], Disabled = [FeatureIds.DataImport] }); // 명령 하나(가져오기)만 걸린 기능
        Assert.Equal(2, c); // 명령은 바뀜
        Assert.Equal(1, p); // 패널 집합은 그대로 = 알리지 않음
    }

    [Fact]
    public void Runtime_mybatis_enable_does_not_show_mybatis_commands_until_restart()
    {
        var gate = new FeatureGate();
        gate.Resolve(Roles(RoleIds.SqlDev));
        var (commands, panels) = Registries(gate);
        var mybatis = Contributions.Commands
            .Where(x => x.HandlerType.Namespace!.StartsWith("Oranda.App.Features.MyBatisWorkspace", StringComparison.Ordinal))
            .Select(x => x.Definition.Id)
            .ToList();
        Assert.NotEmpty(mybatis);

        gate.Resolve(Roles(RoleIds.SqlDev, RoleIds.MyBatisDev));

        Assert.All(mybatis, id => Assert.Null(commands.Find(id)));
        Assert.Null(panels.Find("oranda.panel.mybatisStatements"));
        Assert.Contains(FeatureIds.MyBatis, gate.PendingRestart);
        Assert.Contains(commands.Commands, d => gate.FeatureOf(d.Id, Contributions.Commands.Single(x => x.Definition.Id == d.Id).HandlerType) == FeatureIds.MyBatisLog);
    }

    // ── 메뉴 ────────────────────────────────────────────────────────

    private static List<string> TopMenuIds(CommandRegistry commands, ToolPanelRegistry panels, IFeatureGate gate)
    {
        var text = new ShellText(language: "ko");
        var bar = MenuModelBuilder.BuildMenuBar(commands.Commands, text, _ => null, panels.Panels, gate.IsEnabled);
        var titleToId = MenuModelBuilder.TopMenus.ToDictionary(id => text.Lookup($"Menu.{id}"), id => id);
        return [.. bar.Select(n => titleToId[n.Title])];
    }

    [Fact]
    public void Viewer_only_hides_the_monitor_and_tuning_top_menus_and_their_context_and_toolbar_commands()
    {
        var gate = FakeFeatureGate.ForRoles(RoleIds.Viewer);
        var (commands, panels) = Registries(gate);
        var text = new ShellText(language: "ko");

        var tops = TopMenuIds(commands, panels, gate);

        Assert.DoesNotContain("monitor", tops);
        Assert.DoesNotContain("tuning", tops);
        Assert.Contains("file", tops);
        Assert.Contains("help", tops);
        var monitorIds = Contributions.Commands
            .Where(x => x.Definition.Placements.Any(p => p.StartsWith("menu:monitor", StringComparison.Ordinal) || p.StartsWith("menu:tuning", StringComparison.Ordinal)))
            .Where(x => FeatureCatalog.FeatureFor(x.Definition.Id, x.HandlerType) is not null) // 공통 계획 명령(예상·실제 계획)은 남는다
            .Select(x => x.Definition.Id)
            .ToHashSet();
        var context = ContextSurfaces
            .SelectMany(s => MenuModelBuilder.BuildContext(commands.Commands, text, _ => null, s))
            .Select(n => n.CommandId)
            .OfType<string>();
        var toolbar = MenuModelBuilder.BuildToolbar(commands.Commands, text, _ => null).Select(n => n.CommandId).OfType<string>();
        Assert.NotEmpty(monitorIds);
        Assert.Empty(context.Where(monitorIds.Contains));
        Assert.Empty(toolbar.Where(monitorIds.Contains));
    }

    [Fact]
    public void With_every_feature_on_all_eleven_top_menus_are_drawn()
    {
        var gate = new FeatureGate();
        var (commands, panels) = Registries(gate);

        Assert.Equal(MenuModelBuilder.TopMenus, TopMenuIds(commands, panels, gate));
    }

    [Fact]
    public void Tuning_menu_stays_while_any_tuning_feature_is_on_even_if_only_common_plan_commands_would_remain()
    {
        // R1 = planAdvanced 만 튜닝 묶음 → 튜닝 메뉴가 있다. planAdvanced 를 끄면 공통 계획 명령만 남아도 그리지 않는다.
        var on = FakeFeatureGate.ForRoles(RoleIds.SqlDev);
        var off = FakeFeatureGate.For(new FeaturesSettings { Roles = [RoleIds.SqlDev], Disabled = [FeatureIds.PlanAdvanced] });
        var (c1, p1) = Registries(on);
        var (c2, p2) = Registries(off);

        Assert.Contains("tuning", TopMenuIds(c1, p1, on));
        Assert.DoesNotContain("tuning", TopMenuIds(c2, p2, off));
        Assert.NotNull(c2.Find("oranda.plan.explain")); // 공통 명령은 SQL 메뉴·팔레트에 남는다
        Assert.DoesNotContain("monitor", TopMenuIds(c1, p1, on));
    }

    // ── 단축키 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Shortcut_of_a_disabled_command_is_not_swallowed_and_does_not_run()
    {
        var gate = new FakeFeatureGate().Disable(FeatureIds.DataImport);
        var registry = new CommandRegistry([], features: gate);
        var import = new CountingHandler();
        var fallback = new CountingHandler();
        using var r1 = registry.Register(Def("oranda.tools.import"), import);
        using var r2 = registry.Register(Def("test.fallback"), fallback);
        var keys = new ContextKeyService();
        var onlyImport = KeyBindingTable.Build(
            new Keymap { Id = "t", Name = "t", Bindings = [new KeyBinding { Command = "oranda.tools.import", Keys = ["Primary+I"] }] }, macOS: false);
        var shared = KeyBindingTable.Build(
            new Keymap
            {
                Id = "t",
                Name = "t",
                Bindings =
                [
                    new KeyBinding { Command = "oranda.tools.import", Keys = ["Primary+J"] },
                    new KeyBinding { Command = "test.fallback", Keys = ["Primary+J"] },
                ],
            },
            macOS: false);

        Assert.False(new KeyDispatcher(() => onlyImport, registry, keys).Handle(Key.I, KeyModifiers.Control)); // 키를 흘려보낸다
        var dispatcher = new KeyDispatcher(() => shared, registry, keys);
        Assert.True(dispatcher.Handle(Key.J, KeyModifiers.Control)); // 같은 키의 켜진 바인딩이 이긴다
        await dispatcher.LastRun!;
        Assert.Equal(0, import.Count);
        Assert.Equal(1, fallback.Count);

        // 다시 켜면 그 바인딩이 그대로 돌아온다(키맵은 지우지 않았다).
        gate.Enable(FeatureIds.DataImport).RaiseChanged();
        var again = new KeyDispatcher(() => onlyImport, registry, keys);
        Assert.True(again.Handle(Key.I, KeyModifiers.Control));
        await again.LastRun!;
        Assert.Equal(1, import.Count);
    }

    // ── 시작 순서(D-005) ────────────────────────────────────────────

    [Fact]
    public async Task Load_resolves_the_gate_before_initialize_and_attach()
    {
        var dir = WriteSettings("{\"roles\":[\"viewer\"]}");
        using var services = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir, DevBuild = true });
        var shell = services.GetRequiredService<ShellController>(); // 레지스트리가 설정보다 먼저 생긴다
        var commands = services.GetRequiredService<ICommandRegistry>();
        var gate = (FeatureGate)services.GetRequiredService<IFeatureGate>();
        Assert.NotNull(commands.Find(LockMonitor)); // 읽기 전 = 모두 켬
        var raised = 0;
        commands.CommandsChanged += (_, _) => raised++;

        await Task.Run(() => shell.LoadAsync()); // 앱과 같이 스레드 풀에서

        Assert.True(gate.IsResolved);
        Assert.True(gate.ReadOnlyRole);
        Assert.Null(commands.Find(LockMonitor));
        Assert.Null(services.GetRequiredService<IToolPanelRegistry>().Find(ChangeJournalPanel));
        Assert.Equal(1, raised);
        Assert.Empty(gate.PendingRestart); // 이 값이 시작 때 값
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static MenuItem[] TopItems(ShellHarness h) => [.. h.Window.Menus.ItemsSource!.Cast<MenuItem>()];

    private static bool HasTop(ShellHarness h, string top) =>
        TopItems(h).Any(m => Avalonia.Automation.AutomationProperties.GetName(m) == h.Get<ShellController>().Text.Lookup($"Menu.{top}"));

    [Fact]
    public Task Shell_started_with_viewer_only_builds_menus_and_docking_from_the_resolved_gate() => HeadlessHost.Run(() =>
    {
        var dir = WriteSettings("{\"roles\":[\"viewer\"]}");
        using var h = new ShellHarness(dir);

        Assert.False(HasTop(h, "monitor"));
        Assert.False(HasTop(h, "tuning"));
        Assert.True(HasTop(h, "file"));
        Assert.False(h.Get<Oranda.App.Shell.Docking.DockWorkspace>().IsToolVisible(ChangeJournalPanel));
        Assert.True(h.Get<ContextKeyService>().Get("feature.tableSpec"));
        Assert.False(h.Get<ContextKeyService>().Get("feature.gridEdit"));
        Assert.Empty(ShellContextMenus.Factory!("monitor").OfType<MenuItem>().Where(i => i.Command is RegistryCommand { CommandId: LockMonitor or SessionMonitor }));
    });

    [Fact]
    public Task Changing_roles_at_runtime_rebuilds_menus_without_restart() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var settings = h.Get<SettingsService>();
        var commands = h.Get<ICommandRegistry>();
        var raised = 0;
        commands.CommandsChanged += (_, _) => Interlocked.Increment(ref raised);
        Assert.True(HasTop(h, "monitor"));

        await settings.UpdateAsync(s => s with { Features = Roles(RoleIds.Viewer) });
        await HeadlessHost.WaitUntil(() => !HasTop(h, "monitor"));

        Assert.Equal(1, raised);
        Assert.False(HasTop(h, "tuning"));
        Assert.Null(commands.Find(LockMonitor));

        // 튜너를 더하면 튜닝·모니터 메뉴가 바로 나타난다.
        await settings.UpdateAsync(s => s with { Features = Roles(RoleIds.Viewer, RoleIds.Tuner) });
        await HeadlessHost.WaitUntil(() => HasTop(h, "tuning") && HasTop(h, "monitor"));
        Assert.Equal(2, raised);

        // 「모든 기능 쓰기」 로 되돌리면 원래 집합.
        await settings.UpdateAsync(s => s with { Features = new FeaturesSettings() });
        await HeadlessHost.WaitUntil(() => commands.Commands.Count == Contributions.Commands.Count);
        Assert.Equal(MenuModelBuilder.TopMenus.Count, TopItems(h).Length);
    });

    [Fact]
    public Task Open_tool_window_of_a_disabled_feature_stays_open_and_returns_to_its_default_place_when_enabled_again() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<Oranda.App.Shell.Docking.DockWorkspace>();
        var settings = h.Get<SettingsService>();
        var panels = h.Get<IToolPanelRegistry>();
        Assert.True(docking.ShowTool(CompileErrors));

        await settings.UpdateAsync(s => s with { Features = new FeaturesSettings { Disabled = [FeatureIds.PlsqlEditor] } });
        await HeadlessHost.WaitUntil(() => panels.Find(CompileErrors) is null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(docking.IsToolVisible(CompileErrors)); // 닫지 않는다(D-010)
        Assert.True(docking.ShowTool("oranda.panel.history")); // 배치를 다시 지어도
        Assert.True(docking.IsToolVisible(CompileErrors));

        // 사용자가 닫으면 빠지고 꺼진 동안 다시 열 수 없다.
        Assert.True(docking.HideTool(CompileErrors));
        Assert.False(docking.IsToolVisible(CompileErrors));
        Assert.False(docking.ShowTool(CompileErrors));
        Assert.DoesNotContain(CompileErrors, docking.Capture().HiddenTools ?? []);

        // 다시 켜면 기본 위치(아래)로 돌아온다.
        await settings.UpdateAsync(s => s with { Features = new FeaturesSettings() });
        await HeadlessHost.WaitUntil(() => panels.Find(CompileErrors) is not null);
        Assert.True(docking.ShowTool(CompileErrors));
        var root = docking.Capture().Root!;
        var group = FindGroup(root, CompileErrors);
        Assert.NotNull(group);
        Assert.Equal(DockLocation.Bottom, LayoutPlanner.LocationOf(root, group!));
    });

    // ── 07-06 이 남긴 배선(조정자 허가) ─────────────────────────────

    [Fact]
    public Task Main_window_start_page_gets_services_so_the_choose_role_row_follows_the_settings() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await HeadlessHost.WaitUntil(() => h.Window.GetVisualDescendants().OfType<StartPage>().Any());
        var page = h.Window.GetVisualDescendants().OfType<StartPage>().First();

        Assert.True(page.ShowsRoleLink); // Role 을 고른 적 없음 → 「Role 고르기…」 줄(services 를 받아야 보인다)

        await h.Get<SettingsService>().UpdateAsync(s => s with { Features = Roles(RoleIds.SqlDev) });
        await HeadlessHost.WaitUntil(() => !page.ShowsRoleLink);
    });

    [Fact]
    public Task Sql_monitor_tuning_report_button_hides_when_the_tuning_report_feature_is_off() => HeadlessHost.Run(() =>
    {
        var gate = new FakeFeatureGate();
        var registry = new CommandRegistry(Contributions.Commands, _ => new NoopHandler(), gate);
        var services = new ServiceContainer().AddInstance<ICommandRegistry>(registry).AddInstance<IFeatureGate>(gate);
        var vm = new Oranda.App.Features.SqlMonitor.SqlMonitorViewModel(services) { PostToUi = a => a() };
        var view = new Oranda.App.Features.SqlMonitor.SqlMonitorView { DataContext = vm };
        var window = new Window { Content = view, Width = 1300, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var button = view.FindControl<Button>("TuningReportButton")!;
            Assert.True(button.IsVisible);

            gate.Disable(FeatureIds.TuningReport).RaiseChanged(); // 레지스트리가 거르고 CommandsChanged → 단추 숨김
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsVisible);

            gate.Enable(FeatureIds.TuningReport).RaiseChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    private static DockNode? FindGroup(DockNode node, string toolId) =>
        node.Kind == DockNodeKind.ToolGroup && (node.Tools ?? []).Contains(toolId)
            ? node
            : (node.Children ?? []).Select(c => FindGroup(c, toolId)).FirstOrDefault(g => g is not null);

    private sealed class NoopHandler : ICommandHandler
    {
        public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingHandler : ICommandHandler
    {
        public int Count { get; private set; }

        public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
