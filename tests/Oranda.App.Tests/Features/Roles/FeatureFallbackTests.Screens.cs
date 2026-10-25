using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.ExecutionPlan;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Roles;

// 화면 안 요소: 계획 창 고급 보기(planAdvanced)·튜닝 단추·SQL 모니터 바로 가기(레지스트리 거르기 + CommandsChanged)·시작 화면 기본 Role 카드(D-013).
// ORANDA_RENDER_DIR 가 있으면 라이트·다크 PNG 를 남긴다.
public sealed partial class FeatureFallbackTests
{
    private static readonly string[] AllTuning = [PlanViewModel.StatsCommandId, PlanViewModel.ChildCursorsCommandId, PlanViewModel.AssistantCommandId, "oranda.tuning.report"];

    private static readonly string[] AdvancedButtons = ["PinButton", "CompareButton", "UnpinButton", "FormatButton"];

    private static readonly string[] TuningButtons = ["StatsButton", "ChildCursorsButton", "TuningReportButton"];

    // ---- 계획 창 ---------------------------------------------------------------------------

    [Fact]
    public Task Plan_advanced_off_hides_compare_graph_report_views_pin_and_format_buttons_and_on_brings_them_back() => HeadlessHost.Run(() =>
    {
        var gate = new FakeFeatureGate();
        var vm = new PlanViewModel(new PlanBoostTests.FakePlans()) { PostToUi = a => a(), HasCommand = new HashSet<string>(AllTuning).Contains, Features = gate };
        vm.Apply(PlanBoostTests.WithActuals("7h35uxf5uhmm1"), "실제(ALLSTATS LAST)");
        Assert.True(vm.PinCurrent()); // 켜져 있을 때 고정해 둔 계획
        vm.Apply(PlanBoostTests.WithActuals("7h35uxf5uhmm1"), "실제(ALLSTATS LAST)");
        gate.Disable(FeatureIds.PlanAdvanced).RaiseChanged();
        Assert.False(vm.PinCurrent()); // 꺼지면 직접 불러도 고정·비교하지 않는다
        Assert.False(vm.CompareWithPinned());
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1300, Height = 420 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var items = view.FindControl<ListBox>("ViewSwitch")!.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(5, items.Count);
            Assert.Equal([true, true, false, false, false], items.Select(i => i.IsVisible));
            foreach (var name in AdvancedButtons)
            {
                Assert.False(view.FindControl<Button>(name)!.IsEffectivelyVisible, name);
            }

            Assert.False(vm.HasPinned); // 끄면 고정을 푼다(상태 줄이 비교를 이어 말하지 않게)
            Assert.False(vm.ShowPinnedChip);
            Assert.DoesNotContain("고정 계획", vm.StatusText, StringComparison.Ordinal);
            Assert.True(view.FindControl<Button>("StatsButton")!.IsEffectivelyVisible); // 튜닝 단추는 planAdvanced 와 상관없다
            Assert.True(view.FindControl<Button>("TuningReportButton")!.IsEffectivelyVisible);
            MyBatisFlowRig.Render(window, "rl-fallback-plan-advanced-off", 1300, 420);

            // 되돌림: 켜면 바로 나타난다.
            gate.Enable(FeatureIds.PlanAdvanced).RaiseChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.All(items, i => Assert.True(i.IsVisible));
            Assert.True(view.FindControl<Button>("PinButton")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("FormatButton")!.IsEffectivelyVisible);
            Assert.True(vm.PinCurrent());
            Assert.True(vm.ShowPinnedChip);

            // 고급 보기를 보는 중에 끄면 트리로 돌아간다.
            vm.SelectedTabIndex = PlanViewModel.GraphTabIndex;
            gate.SetEnabled(FeatureIds.All.Where(id => id != FeatureIds.PlanAdvanced));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, vm.SelectedTabIndex);
            Assert.False(vm.IsAdvancedEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Plan_without_a_gate_keeps_every_view_and_detaching_the_gate_stops_listening()
    {
        var vm = new PlanViewModel(new PlanBoostTests.FakePlans()) { PostToUi = a => a() };
        Assert.True(vm.IsAdvancedEnabled);

        var gate = new FakeFeatureGate().Disable(FeatureIds.PlanAdvanced);
        vm.Features = gate;
        Assert.False(vm.IsAdvancedEnabled);
        vm.Features = null;
        Assert.True(vm.IsAdvancedEnabled);
        vm.SelectedTabIndex = PlanViewModel.ReportTabIndex;
        gate.RaiseChanged(); // 떼어 낸 게이트의 알림은 받지 않는다
        Assert.Equal(PlanViewModel.ReportTabIndex, vm.SelectedTabIndex);
    }

    [Fact]
    public Task Plan_tuning_buttons_follow_the_registry_filter_as_soon_as_commands_change() => HeadlessHost.Run(() =>
    {
        var gate = new FakeFeatureGate();
        var services = new GatedServices(gate);
        var vm = new PlanViewModel(services) { PostToUi = a => a() };
        vm.Apply(PlanBoostTests.WithActuals("x"), "실제");
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1300, Height = 420 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(vm.HasStatsCommand && vm.HasChildCursorsCommand && vm.HasTuningReportCommand && vm.HasAssistantCommand);
            Assert.True(view.FindControl<Button>("ChildCursorsButton")!.IsEffectivelyVisible);

            // 튜닝 Role 을 빼면(레지스트리가 명령을 거르고 CommandsChanged) 통계·자식 커서·보고서·도우미 단추가 바로 숨는다.
            gate.Disable(FeatureIds.TableStats, FeatureIds.ChildCursors, FeatureIds.TuningReport, FeatureIds.TuningAssistant).RaiseChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.ShowTuningButtons);
            foreach (var name in TuningButtons)
            {
                Assert.False(view.FindControl<Button>(name)!.IsEffectivelyVisible, name);
            }

            Assert.False(vm.HasAssistantCommand);
            MyBatisFlowRig.Render(window, "rl-fallback-plan-tuning-off", 1300, 420);

            // 보고서만 남기면 그 단추만 보인다(줄은 보인다).
            gate.Enable(FeatureIds.TuningReport).RaiseChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ShowTuningButtons);
            Assert.True(view.FindControl<Button>("TuningReportButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("StatsButton")!.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Sql_monitor_shortcuts_follow_the_registry_filter_and_commands_changed()
    {
        var gate = new FakeFeatureGate();
        var services = new GatedServices(gate);
        var vm = new SqlMonitorViewModel(services) { PostToUi = a => a() };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(vm.CanOpenChildCursors);
        Assert.True(vm.CanOpenTuningReport);
        Assert.True(vm.CanOpenLockMonitor);

        gate.Disable(FeatureIds.ChildCursors, FeatureIds.TuningReport, FeatureIds.LockMonitor).RaiseChanged();

        Assert.Contains(nameof(SqlMonitorViewModel.CanOpenChildCursors), changed);
        Assert.Contains(nameof(SqlMonitorViewModel.CanOpenTuningReport), changed);
        Assert.False(vm.CanOpenChildCursors);
        Assert.False(vm.CanOpenTuningReport);
        Assert.False(vm.CanOpenLockMonitor);

        gate.Enable(FeatureIds.ChildCursors).RaiseChanged();
        Assert.True(vm.CanOpenChildCursors);
        Assert.Equal(1, services.Registry.Subscribers); // 처음 물을 때 한 번만 붙는다
    }

    // ---- 시작 화면 (D-011) -------------------------------------------------------------------

    [Fact]
    public Task Start_page_shows_choose_role_until_roles_are_picked_and_drops_mappers_when_mybatis_is_off() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness(allFeatures: false); // 설정 없음 = 기본 Role(D-013)
        var gate = new FakeFeatureGate();
        h.Services.AddInstance<IFeatureGate>(gate);
        var previous = ShellCommandRunner.Runner;
        var ran = new List<string>();
        ShellCommandRunner.Runner = id =>
        {
            ran.Add(id);
            return Task.CompletedTask;
        };
        var page = new StartPage(h.Get<ICommandRegistry>(), h.Get<IContextKeys>(), h.Get<IShellText>(), _ => null, null, h.Services);
        var window = new Window { Content = page, Width = 900, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(page.ShowsRoleLink);
            Assert.Equal("MyBatis 매퍼(.xml)나 작업 공간(.md)을 열면 문장마다 탭이 생깁니다.", page.OpenHintText);
            // D-013 카드: 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」 + 빠진 메뉴를 어디서 켜는지 한 줄. 모달이 아니다(창 안 카드).
            var link = page.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "지금 SQL 개발자 메뉴입니다 — 바꾸기…");
            Assert.True(link.IsEffectivelyVisible);
            Assert.Equal("바꾸기…", link.Content);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "지금 SQL 개발자 메뉴입니다" && t.IsEffectivelyVisible);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "튜닝·모니터·MyBatis·PL/SQL 메뉴는 주 업무를 바꾸거나 덧붙여 켭니다." && t.IsEffectivelyVisible);
            Assert.Contains(page.GetVisualDescendants().OfType<Border>(), b => AutomationProperties.GetName(b) == "지금 SQL 개발자 메뉴입니다" && b.IsEffectivelyVisible);
            Assert.Empty(window.OwnedWindows); // 첫 실행에 창을 띄우지 않는다
            MyBatisFlowRig.Render(window, "rl-fallback-start-choose-role", 900, 640);

            link.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([FeatureCommandIds.Roles], ran);

            // MyBatis 끔: 안내에서 매퍼를 뺀다(게이트 Changed 로 바로).
            gate.Disable(FeatureIds.MyBatis).RaiseChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("작업 공간(.md)이나 SQL 파일을 열면 문장마다 탭이 생깁니다.", page.OpenHintText);

            // Role 을 고르면 줄이 사라지고, 「모든 기능 쓰기」로 되돌리면 다시 나타난다.
            var settings = h.Get<SettingsService>();
            await settings.UpdateAsync(s => s with { Features = new FeaturesSettings { Roles = [RoleIds.SqlDev] } });
            await HeadlessHost.WaitUntil(() => !page.ShowsRoleLink);
            MyBatisFlowRig.Render(window, "rl-fallback-start-role-chosen-mybatis-off", 900, 640);
            await settings.UpdateAsync(s => s with { Features = new FeaturesSettings() });
            await HeadlessHost.WaitUntil(() => page.ShowsRoleLink);

            // 「모든 기능 쓰기」 도 직접 고른 것이라 카드가 사라진다.
            await settings.UpdateAsync(s => s with { Features = new FeaturesSettings { AllFeatures = true } });
            await HeadlessHost.WaitUntil(() => !page.ShowsRoleLink);
            await settings.UpdateAsync(s => s with { Features = new FeaturesSettings() });
            await HeadlessHost.WaitUntil(() => page.ShowsRoleLink);

            // 붙어 있지 않을 때는 듣지 않는다.
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            gate.Enable(FeatureIds.MyBatis).RaiseChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("작업 공간(.md)이나 SQL 파일을 열면 문장마다 탭이 생깁니다.", page.OpenHintText);
        }
        finally
        {
            ShellCommandRunner.Runner = previous;
            window.Close();
        }
    });

    [Fact]
    public Task Start_page_without_services_looks_as_before() => HeadlessHost.Run(() =>
    {
        var page = new StartPage(ShellTestHelpers.CreateCommandRegistry(), new FakeContextKeys(), new ShellText(language: "ko"), _ => null);
        Assert.False(page.ShowsRoleLink);
        Assert.Equal("MyBatis 매퍼(.xml)나 작업 공간(.md)을 열면 문장마다 탭이 생깁니다.", page.OpenHintText);
    });

    /// <summary>
    /// 레지스트리 거르기 흉내(TSK-07-02 의 실제 레지스트리 대신): 표본 명령 가운데 게이트가 끈 기능의 명령은 <see cref="Find"/> 에서 빠지고,
    /// 게이트가 Changed 를 내면 <see cref="CommandsChanged"/> 를 낸다.
    /// </summary>
    private sealed class GatedRegistry : ICommandRegistry
    {
        private static readonly Dictionary<string, string?> Tags = new(StringComparer.Ordinal)
        {
            [PlanViewModel.StatsCommandId] = FeatureIds.TableStats,
            [PlanViewModel.ChildCursorsCommandId] = FeatureIds.ChildCursors,
            [PlanViewModel.AssistantCommandId] = FeatureIds.TuningAssistant,
            ["oranda.tuning.report"] = FeatureIds.TuningReport,
            ["oranda.monitor.locks"] = FeatureIds.LockMonitor,
            ["oranda.conn.manage"] = null,
        };

        private readonly FakeFeatureGate _gate;
        private EventHandler? _changed;

        public GatedRegistry(FakeFeatureGate gate)
        {
            _gate = gate;
            gate.Changed += (_, _) => _changed?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? CommandsChanged
        {
            add
            {
                _changed += value;
                Subscribers++;
            }

            remove
            {
                _changed -= value;
                Subscribers--;
            }
        }

        public int Subscribers { get; private set; }

        public IReadOnlyList<CommandDefinition> Commands => [.. Tags.Keys.Select(Find).OfType<CommandDefinition>()];

        public CommandDefinition? Find(string idOrAlias) =>
            Tags.TryGetValue(idOrAlias, out var feature) && (feature is null || _gate.IsEnabled(feature))
                ? new CommandDefinition { Id = idOrAlias, Title = "Cmd." + idOrAlias, Category = CommandCategory.Tools }
                : null;

        public bool CanExecute(string idOrAlias, IContextKeys keys) => Find(idOrAlias) is not null;

        public Task<bool> ExecuteAsync(string idOrAlias, IContextKeys keys, object? parameter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Find(idOrAlias) is not null);

        public IDisposable Register(CommandDefinition definition, ICommandHandler handler) => throw new NotSupportedException();
    }

    private sealed class GatedServices(FakeFeatureGate gate) : IServiceProvider
    {
        public GatedRegistry Registry { get; } = new(gate);

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandRegistry) ? Registry
            : serviceType == typeof(IFeatureGate) ? gate
            : null;
    }
}
