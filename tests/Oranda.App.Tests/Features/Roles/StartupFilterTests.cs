using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.BlockerAlert;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.MyBatisWorkspace.RoleProbes;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Features;
using Oranda.App.Tests.Features.BlockerAlert;
using Oranda.App.Tests.Features.PlsqlWorkspace;
using Oranda.App.Tests.Features.Roles.Probes;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.App.Views;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Roles
{
    /// <summary>
    /// TSK-07-03: 시작 작업 형 단계 거르기(꺼진 기능의 작업은 생성자 호출 0회)·지연 어셈블리 훑기 뒤에도 거름·Trace 기록,
    /// 실제 앱 조립의 시작 작업 14개와 Role 별 거름, 상태 칸 거르기, 블로커 칸 클릭 대체(D-009).
    /// PL/SQL 작업 공간 나누기는 정적 활성 탭 때문에 <see cref="PlsqlWorkspaceSplitTests"/> 로 따로 둔다.
    /// </summary>
    public sealed class StartupFilterTests
    {
        /// <summary>features 없음 = 지금과 같은 시작 작업 16개(조사 1.8, TSK-14-04 가 SessionsAutoShow, 설정 이력·캐시 수정이 MetadataCachePruner 를 더함).</summary>
        private static readonly string[] AllStartupTasks =
        [
            "EditorDiagnosticsStartup", "MyBatisConvertReady", "MyBatisFolderReadyTask", "MyBatisWorkspaceWiring", "MapperWatchStartup",
            "NavigationWiring", "PlsqlWorkspacePersistence", "RecentFilesStartup", "ResultGridSettingsStartup", "SqlEditorDockWiring",
            "UpdateStartup", "HistoryRecorder", "MetadataCachePruner", "ChangeJournalRecorder", "PlSqlWarmUpTask", "SessionsAutoShow",
        ];

        private static readonly string[] MyBatisStartupTasks = ["MyBatisConvertReady", "MyBatisFolderReadyTask", "MyBatisWorkspaceWiring", "MapperWatchStartup"];

        private static void ResetProbes()
        {
            MyBatisProbeTask.Reset();
            DeferredMyBatisProbeTask.Reset();
            CommonProbeTask.Reset();
            CommonIdleProbeTask.Reset();
            DeferredCommonProbeTask.Reset();
        }

        // ---- 컨테이너: 형 단계 거르기 ----

        [Fact]
        public void A_disabled_feature_task_is_never_constructed_and_is_reported_as_skipped()
        {
            ResetProbes();
            using var c = new ServiceContainer();
            var gate = new FakeFeatureGate().Disable(FeatureIds.MyBatis);
            c.AddInstance<IFeatureGate>(gate);
            c.AddType(typeof(IStartupTask), typeof(CommonProbeTask));
            c.AddType(typeof(IStartupTask), typeof(MyBatisProbeTask));
            c.AddType(typeof(IStartupTask), typeof(CommonIdleProbeTask));
            var skipped = new List<Type>();

            var tasks = c.GetStartupTasks(skipped.Add);

            Assert.Equal([typeof(CommonProbeTask), typeof(CommonIdleProbeTask)], tasks.Select(t => t.GetType()));
            Assert.Equal([typeof(MyBatisProbeTask)], skipped);
            Assert.Equal(0, MyBatisProbeTask.Created);
            Assert.Equal(1, CommonProbeTask.Created);
            Assert.Contains(typeof(MyBatisProbeTask), gate.TypeQueries);

            // 같은 컨테이너에서 다시 물어도 싱글턴은 다시 만들지 않고, 꺼진 작업은 여전히 만들지 않는다.
            _ = c.GetStartupTasks();
            Assert.Equal(1, CommonProbeTask.Created);
            Assert.Equal(0, MyBatisProbeTask.Created);
        }

        [Fact]
        public void Asking_the_container_for_the_task_list_is_filtered_too()
        {
            ResetProbes();
            using var c = new ServiceContainer();
            c.AddInstance<IFeatureGate>(new FakeFeatureGate().Disable(FeatureIds.MyBatis));
            c.AddType(typeof(IStartupTask), typeof(MyBatisProbeTask));
            c.AddType(typeof(IStartupTask), typeof(CommonProbeTask));

            var tasks = c.GetService<IEnumerable<IStartupTask>>()!;

            Assert.IsType<CommonProbeTask>(Assert.Single(tasks));
            Assert.Equal(0, MyBatisProbeTask.Created);
        }

        [Fact]
        public void Without_a_gate_every_task_is_returned_and_deferred_assemblies_stay_asleep()
        {
            ResetProbes();
            using var c = new ServiceContainer();
            c.AddType(typeof(IStartupTask), typeof(MyBatisProbeTask));
            c.AddDeferredAssembly(typeof(StartupFilterTests).Assembly.GetName().Name!);

            var tasks = c.GetStartupTasks();

            Assert.IsType<MyBatisProbeTask>(Assert.Single(tasks));
            Assert.False(c.DeferredScanned); // 게이트를 찾는다고 지연 어셈블리를 깨우지 않는다
            Assert.Null(c.GetRegistered<IFeatureGate>());
            Assert.False(c.DeferredScanned);
        }

        [Fact]
        public void Factory_registrations_are_asked_by_their_built_type()
        {
            ResetProbes();
            using var c = new ServiceContainer();
            c.AddInstance<IFeatureGate>(new FakeFeatureGate().Disable(FeatureIds.MyBatis));
            c.AddFactory(typeof(IStartupTask), _ => new MyBatisProbeTask());
            c.AddFactory(typeof(IStartupTask), _ => new CommonProbeTask());
            var skipped = new List<Type>();

            var tasks = c.GetServices<IStartupTask>(c.GetRegistered<IFeatureGate>()!.IsTypeEnabled, skipped.Add);

            Assert.IsType<CommonProbeTask>(Assert.Single(tasks));
            Assert.Equal([typeof(MyBatisProbeTask)], skipped);
        }

        [Fact]
        public void Tasks_from_deferred_assemblies_follow_the_same_rule_after_the_scan()
        {
            ResetProbes();
            using var c = new ServiceContainer();
            c.AddInstance<IFeatureGate>(new FakeFeatureGate().Disable(FeatureIds.MyBatis));
            c.AddDeferredAssembly(typeof(StartupFilterTests).Assembly.GetName().Name!);

            Assert.Empty(c.GetStartupTasks());
            Assert.False(c.DeferredScanned);

            c.ScanDeferred();
            var skipped = new List<Type>();
            var tasks = c.GetStartupTasks(skipped.Add);

            Assert.Contains(tasks, t => t is DeferredCommonProbeTask);
            Assert.DoesNotContain(tasks, t => t is DeferredMyBatisProbeTask);
            Assert.Contains(typeof(DeferredMyBatisProbeTask), skipped);
            Assert.Equal(0, DeferredMyBatisProbeTask.Created);
        }

        // ---- 앱의 시작 작업 실행기 ----

        [Fact]
        public async Task Runner_runs_each_enabled_task_once_across_both_passes_and_traces_skips_once_with_a_summary()
        {
            ResetProbes();
            using var c = new ServiceContainer();
            c.AddInstance<IFeatureGate>(new FakeFeatureGate().Disable(FeatureIds.MyBatis));
            c.AddType(typeof(IStartupTask), typeof(CommonProbeTask));
            c.AddType(typeof(IStartupTask), typeof(CommonIdleProbeTask));
            c.AddType(typeof(IStartupTask), typeof(MyBatisProbeTask));
            c.AddDeferredAssembly(typeof(StartupFilterTests).Assembly.GetName().Name!);
            var run = new Oranda.App.App.StartupRun();
            using var trace = new TraceCapture();

            await Oranda.App.App.RunStartupTasksAsync(c, run, StartupPhase.AfterFirstFrame);

            Assert.Equal(1, CommonProbeTask.Ran);
            Assert.Equal(0, CommonIdleProbeTask.Ran); // 유휴 작업은 둘째 차례
            Assert.DoesNotContain(trace.Lines, l => l.Contains("시작 작업: 돈", StringComparison.Ordinal));

            c.ScanDeferred();
            await Oranda.App.App.RunStartupTasksAsync(c, run, null);

            Assert.Equal(1, CommonProbeTask.Ran);
            Assert.Equal(1, CommonIdleProbeTask.Ran);
            Assert.Equal(1, DeferredCommonProbeTask.Ran);
            Assert.Equal(0, MyBatisProbeTask.Created + MyBatisProbeTask.Ran);
            Assert.Equal(0, DeferredMyBatisProbeTask.Created + DeferredMyBatisProbeTask.Ran);
            Assert.Equal(3, run.Ran.Count);
            Assert.Equal(2, run.Skipped.Count);
            Assert.Contains(typeof(MyBatisProbeTask), run.Skipped);
            Assert.Contains(typeof(DeferredMyBatisProbeTask), run.Skipped);
            Assert.Single(trace.Lines, l => l.Contains("시작 작업 건너뜀: " + typeof(MyBatisProbeTask).FullName, StringComparison.Ordinal));
            Assert.Single(trace.Lines, l => l.Contains("시작 작업 건너뜀: " + typeof(DeferredMyBatisProbeTask).FullName, StringComparison.Ordinal));
            Assert.Contains(trace.Lines, l => l.Contains("시작 작업: 돈 3개, 건너뜀 2개", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_failing_task_is_logged_and_the_rest_still_run()
        {
            ResetProbes();
            using var c = new ServiceContainer();
            c.AddInstance<IFeatureGate>(new FakeFeatureGate());
            c.AddType(typeof(IStartupTask), typeof(ThrowingProbeTask));
            c.AddType(typeof(IStartupTask), typeof(CommonProbeTask));
            var run = new Oranda.App.App.StartupRun();

            await Oranda.App.App.RunStartupTasksAsync(c, run, null);

            Assert.Equal(1, CommonProbeTask.Ran);
            Assert.Equal(2, run.Ran.Count);
            Assert.Empty(run.Skipped);
        }

        // ---- 실제 앱 조립 ----

        [Theory]
        [InlineData(null)]
        [InlineData(RoleIds.SqlDev)]
        [InlineData(RoleIds.Viewer)]
        [InlineData(RoleIds.MyBatisDev)]
        public Task App_startup_tasks_follow_the_roles(string? role) => HeadlessHost.Run(() =>
        {
            var dir = TempDir();
            try
            {
                using var services = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir, DevBuild = true });
                var gate = role is null ? new FakeFeatureGate() : FakeFeatureGate.ForRoles(role);
                services.AddInstance<IFeatureGate>(gate);
                services.ScanDeferred();
                var skipped = new List<Type>();

                var names = services.GetStartupTasks(skipped.Add).Select(t => t.GetType().Name).ToList();

                string[] expectedOff = role switch
                {
                    null => [],
                    RoleIds.SqlDev => [.. MyBatisStartupTasks, "PlsqlWorkspacePersistence"],
                    RoleIds.Viewer => [.. MyBatisStartupTasks, "PlsqlWorkspacePersistence", "ChangeJournalRecorder"],
                    _ => ["PlsqlWorkspacePersistence"], // MyBatis 개발: PL/SQL 없음, 변경 일지는 있음
                };
                // 직접 켜는 기능(aiTuning)의 작업은 모든 기능을 켠 가짜 게이트(role null)에서만 돌고, Role 로 고른 게이트에서는 꺼져 있다.
                if (role is not null)
                {
                    expectedOff = [.. expectedOff, .. OptInItems.StartupTaskNames];
                }

                Assert.Equal(AllStartupTasks.Concat(OptInItems.StartupTaskNames).Except(expectedOff).Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
                Assert.Equal(expectedOff.Order(StringComparer.Ordinal), skipped.Select(t => t.Name).Order(StringComparer.Ordinal));
                Assert.Contains("PlSqlWarmUpTask", names); // Language 의 공통 작업은 늘 돈다
            }
            finally
            {
                DeleteDir(dir);
            }
        });

        // ---- 상태 칸 ----

        [Fact]
        public Task No_features_section_keeps_all_ten_status_items() => HeadlessHost.Run(() =>
        {
            using var shell = new GatedShell(new FakeFeatureGate());

            Assert.Equal(10, shell.Window.ViewModel!.StatusItems.Count);
        });

        [Fact]
        public Task Sql_developer_has_no_mybatis_status_item_but_keeps_the_blocker() => HeadlessHost.Run(() =>
        {
            var gate = FakeFeatureGate.ForRoles(RoleIds.SqlDev);
            using var shell = new GatedShell(gate);

            var types = StatusTypes(shell);
            Assert.DoesNotContain("MapperSaveStatusViewModel", types);
            Assert.Contains("BlockerStatusViewModel", types);
            Assert.Equal(9, types.Count);
            Assert.Contains(gate.TypeQueries, t => t.Name == "MapperSaveStatusViewModel");
        });

        [Fact]
        public Task Viewer_has_no_blocker_item_so_no_watcher_or_monitor_session() => HeadlessHost.Run(() =>
        {
            using var shell = new GatedShell(FakeFeatureGate.ForRoles(RoleIds.Viewer));

            var types = StatusTypes(shell);
            Assert.DoesNotContain("BlockerStatusViewModel", types);
            Assert.DoesNotContain("MapperSaveStatusViewModel", types);
            Assert.Contains("SaveStatusViewModel", types); // 저장 알림은 공통: 조회 전용도 내보내기 완료 알림이 보인다
            Assert.Contains("ConnectionStatusViewModel", types);
            Assert.DoesNotContain(shell.Window.GetVisualDescendants(), v => v is BlockerStatusView);
        });

        [Fact]
        public Task Turning_off_only_the_blocker_alert_drops_only_that_item() => HeadlessHost.Run(() =>
        {
            using var shell = new GatedShell(new FakeFeatureGate().Disable(FeatureIds.BlockerAlert));

            var types = StatusTypes(shell);
            Assert.Equal(9, types.Count);
            Assert.DoesNotContain("BlockerStatusViewModel", types);
        });

        // ---- 블로커 칸 클릭 대체 (D-009) ----

        [Fact]
        public async Task With_the_lock_monitor_off_a_click_asks_for_the_notice_instead_of_opening_the_monitor()
        {
            var (vm, watcher) = await BlockingAsync(new FakeFeatureGate().Disable(FeatureIds.LockMonitor));
            using var _ = watcher;
            var runs = new List<string>();
            var old = ShellCommandRunner.Runner;
            ShellCommandRunner.Runner = id =>
            {
                runs.Add(id);
                return Task.CompletedTask;
            };
            try
            {
                var asked = 0;
                vm.LockMonitorOffRequested += (_, _) => asked++;

                await vm.OnClickAsync();

                Assert.Equal(1, asked);
                Assert.DoesNotContain(LockMonitorCommands.Open, runs);
                Assert.False(vm.LockMonitorEnabled);
                Assert.DoesNotContain("눌러서 Lock 모니터 열기", vm.ToolTip, StringComparison.Ordinal);
                Assert.Contains("Lock 모니터 기능이 꺼져 있습니다", vm.ToolTip, StringComparison.Ordinal);
                Assert.Contains("MAX_IDLE_BLOCKER_TIME", vm.ToolTip, StringComparison.Ordinal);
                Assert.Contains("3 세션 막음", vm.ToolTip, StringComparison.Ordinal);

                await BlockerStatusViewModel.OpenFeatureSettingsAsync();
                Assert.Equal([FeatureCommandIds.Roles], runs);
            }
            finally
            {
                ShellCommandRunner.Runner = old;
            }
        }

        [Fact]
        public Task With_the_lock_monitor_on_a_click_opens_it_and_turning_it_off_later_switches_at_once() => HeadlessHost.Run(async () =>
        {
            var gate = new FakeFeatureGate();
            var (vm, watcher) = await BlockingAsync(gate);
            using var _ = watcher;
            var runs = new List<string>();
            var old = ShellCommandRunner.Runner;
            ShellCommandRunner.Runner = id =>
            {
                runs.Add(id);
                return Task.CompletedTask;
            };
            try
            {
                var asked = 0;
                vm.LockMonitorOffRequested += (_, _) => asked++;
                await vm.OnClickAsync();
                Assert.Equal([LockMonitorCommands.Open], runs);
                Assert.Contains("Lock 모니터 열기", vm.ToolTip, StringComparison.Ordinal);

                // 실행 중 끔: 게이트 Changed 로 툴팁을 다시 알리고 클릭은 안내로 바뀐다(메뉴와 같이 즉시)
                var changed = new List<string?>();
                vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                gate.Disable(FeatureIds.LockMonitor).RaiseChanged();
                await HeadlessHost.WaitUntil(() => changed.Contains(nameof(BlockerStatusViewModel.ToolTip)));
                await vm.OnClickAsync();

                Assert.Equal(1, asked);
                Assert.Single(runs);
                Assert.Contains("Lock 모니터 기능이 꺼져 있습니다", vm.ToolTip, StringComparison.Ordinal);

                // 다시 켜면 되돌아온다
                gate.Enable(FeatureIds.LockMonitor).RaiseChanged();
                await vm.OnClickAsync();
                Assert.Equal([LockMonitorCommands.Open, LockMonitorCommands.Open], runs);
            }
            finally
            {
                ShellCommandRunner.Runner = old;
            }
        });

        // UI 스레드에서 돈다: 밖에서 돌면 칸은 감시 알림을 UI 스레드로 Post 해, 앞서 쌓인 알림이 구독 뒤에 와서 셈에 섞인다.
        // Dispose 가 감시를 끄며 내는 마지막 알림도 셈에서 빼려고, Dispose 뒤에 구독하고 게이트 사건만 본다.
        [Fact]
        public Task Without_a_gate_the_click_still_opens_the_lock_monitor_and_dispose_unsubscribes() => HeadlessHost.Run(async () =>
        {
            var gate = new FakeFeatureGate();
            var (vm, watcher) = await BlockingAsync(null);
            using var _ = watcher;
            Assert.True(vm.LockMonitorEnabled);

            // 게이트를 쥔 칸은 Dispose 뒤 사건을 받지 않는다
            var (gated, watcher2) = await BlockingAsync(gate);
            gated.Dispose();
            Pump();
            var raised = 0;
            gated.PropertyChanged += (_, _) => raised++;
            gate.RaiseChanged();
            Pump();
            Assert.Equal(0, raised);
            watcher2.Dispose();
        });

        [Fact]
        public Task Notice_flyout_shows_the_reserved_text_and_open_settings_runs_the_roles_command() => HeadlessHost.Run(async () =>
        {
            var (vm, watcher) = await BlockingAsync(new FakeFeatureGate().Disable(FeatureIds.LockMonitor));
            using var _ = watcher;
            var runs = new List<string>();
            var old = ShellCommandRunner.Runner;
            ShellCommandRunner.Runner = id =>
            {
                runs.Add(id);
                return Task.CompletedTask;
            };
            var view = new BlockerStatusView { DataContext = vm, Height = 22, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var window = new Window { Width = 420, Height = 160, Content = new Border { Padding = new Thickness(8), Child = view, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom } };
            try
            {
                window.Show();
                Pump();
                await vm.OnClickAsync();
                Pump();

                var flyout = Assert.IsType<Flyout>(view.LockMonitorOffFlyout);
                var content = Assert.IsAssignableFrom<Panel>(flyout.Content);
                var message = Assert.IsType<TextBlock>(content.Children[0]);
                var open = Assert.IsType<Button>(content.Children[1]);
                Assert.Equal("Lock 모니터 기능이 꺼져 있습니다. 설정 > 기능에서 켤 수 있습니다.", message.Text);
                Assert.Equal("설정 열기", open.Content);
                Assert.Equal("설정 열기", Avalonia.Automation.AutomationProperties.GetName(open));

                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await HeadlessHost.WaitUntil(() => runs.Count > 0);
                Assert.Equal([FeatureCommandIds.Roles], runs);

                RenderNotice(view, content, vm);
            }
            finally
            {
                ShellCommandRunner.Runner = old;
                window.Close();
            }
        });

        // ---- 도우미 ----

        private static List<string> StatusTypes(GatedShell shell) =>
            [.. shell.Window.ViewModel!.StatusItems.Select(e => e.Item.GetType().Name)];

        private static async Task<(BlockerStatusViewModel Vm, BlockerWatcher Watcher)> BlockingAsync(IFeatureGate? gate)
        {
            var bus = new EventBus();
            var time = new FakeTimeProvider();
            var watcher = new BlockerWatcher(bus,
                (p, _) => Task.FromResult<ISession?>(new FakeMonitorSession(p, BlockerTestData.Server(BlockerTestData.Dba))),
                (_, _, _) => Task.FromResult(BlockerTestData.Snapshot(3, maxIdle: 10, lastCall: 300)),
                time, _ => { });
            var vm = new BlockerStatusViewModel(watcher, gate);
            var id = Guid.NewGuid();
            bus.Publish(new SessionOpened(id, BlockerTestData.Profile(), BlockerTestData.Server(BlockerTestData.Dba), SessionPurpose.Work));
            bus.Publish(new TransactionStateChanged(id, true));
            time.Advance(TimeSpan.Zero);
            await watcher.WhenIdleAsync();
            Assert.Equal(BlockerAlertKind.Blocking, vm.Kind);
            return (vm, watcher);
        }

        private static void Pump()
        {
            for (var i = 0; i < 6; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>
        /// <c>ORANDA_RENDER_DIR</c> 가 있으면 칩 + 안내 플라이아웃 본문을 라이트·다크 PNG 로 쓴다(헤드리스는 팝업을 창 그림에 넣지 않으므로 본문을 따로 올린다).
        /// </summary>
        private static void RenderNotice(BlockerStatusView view, Panel content, BlockerStatusViewModel vm)
        {
            var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var flyout = view.LockMonitorOffFlyout!;
            flyout.Hide();
            flyout.Content = null;
            const int width = 360, height = 160;
            var host = new Window
            {
                Width = width,
                Height = height,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(12),
                    Children =
                    {
                        new FlyoutPresenter { Content = content, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right },
                        new BlockerStatusView { DataContext = vm, Height = 22, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right },
                    },
                },
            };
            host.Show();
            Directory.CreateDirectory(dir);
            try
            {
                foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    Pump();
                    host.UpdateLayout();
                    using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                    bmp.Render(host);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                    bmp.Save(Path.Combine(dir, $"blocker-lockmonitor-off-{tone}.png"));
#pragma warning restore CS0618
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                Pump();
                host.Close();
            }
        }

        internal static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "oranda-roles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static void DeleteDir(string dir)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>Trace 로 남긴 줄을 모은다(전역 수신기라 다른 시험 줄도 섞이므로 내용으로 찾는다).</summary>
        private sealed class TraceCapture : TraceListener
        {
            private readonly ConcurrentQueue<string> _lines = new();

            public TraceCapture() => Trace.Listeners.Add(this);

            public IReadOnlyList<string> Lines => [.. _lines];

            public override void Write(string? message)
            {
                if (message is not null)
                {
                    _lines.Enqueue(message);
                }
            }

            public override void WriteLine(string? message) => Write(message);

            protected override void Dispose(bool disposing)
            {
                Trace.Listeners.Remove(this);
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// TSK-07-03 PL/SQL 작업 공간 나누기(TRD 10.7.3): plsqlWorkspace 가 꺼지면 라우터만 있고 복원·자동 저장 타이머·종료 저장이 없다.
    /// 정적 활성 탭을 건드리므로 <see cref="PlsqlStatics"/> 묶음에서 돈다.
    /// </summary>
    [Collection(PlsqlStatics.Name)]
    public sealed class PlsqlWorkspaceSplitTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task Workspace_restore_and_autosave_follow_the_plsql_workspace_feature(bool workspaceOn) => HeadlessHost.Run(async () =>
        {
            var dir = StartupFilterTests.TempDir();
            try
            {
                // 앞 실행이 남긴 작업 공간 하나
                var store = new PlsqlWorkspaceStore(Path.Combine(dir, "plsql-workspaces"), Path.Combine(dir, "backup", "plsql-workspaces"));
                await store.SaveAsync(new PlsqlWorkspaceFile { Id = "a1", Name = "PL/SQL 1", Tabs = [new PlsqlTabFile { Title = "새 PL/SQL", Text = "-- mine" }] });
                await store.SaveIndexAsync(new PlsqlWorkspaceIndex { OpenIds = ["a1"], LastId = "a1" });
                var indexBefore = await File.ReadAllTextAsync(Path.Combine(dir, "plsql-workspaces", "open.json"));

                var gate = new FakeFeatureGate();
                if (!workspaceOn)
                {
                    gate.Disable(FeatureIds.PlsqlWorkspace);
                }

                using (var shell = new GatedShell(gate, dir))
                {
                    var persistence = Assert.Single(shell.Services.GetStartupTasks().OfType<PlsqlWorkspacePersistence>());
                    await persistence.RunAsync(CancellationToken.None);
                    await HeadlessHost.WaitUntil(() => persistence.Persisting is not null);

                    var router = PlsqlWorkspaceRouter.Of(shell.Services);
                    Assert.NotNull(router); // 라우터는 늘 있다(PL/SQL 편집 탭이 열리는 자리)
                    Assert.Equal(workspaceOn, persistence.Persisting);
                    Assert.Equal(workspaceOn, persistence.AutosaveRunning);
                    if (workspaceOn)
                    {
                        await HeadlessHost.WaitUntil(() => router!.Workspaces.Count == 1);
                        Assert.Equal("a1", router!.Workspaces[0].Id);
                    }
                    else
                    {
                        Pump();
                        Assert.Empty(router!.Workspaces);
                    }
                }

                // 끔이면 종료 저장도 없어 앞 실행의 목록이 그대로 남는다(다시 켜면 되살아난다)
                Assert.Equal(indexBefore, await File.ReadAllTextAsync(Path.Combine(dir, "plsql-workspaces", "open.json")));
                Assert.True(File.Exists(Path.Combine(dir, "plsql-workspaces", "a1.json")));
            }
            finally
            {
                Oranda.App.Features.PlsqlEditor.PlsqlEditorViewModel.Active = null;
                StartupFilterTests.DeleteDir(dir);
            }
        });

        [Fact]
        public Task Plsql_editor_off_skips_the_whole_persistence_task() => HeadlessHost.Run(() =>
        {
            var dir = StartupFilterTests.TempDir();
            try
            {
                using var shell = new GatedShell(new FakeFeatureGate().Disable(FeatureIds.PlsqlEditor), dir);

                Assert.Empty(shell.Services.GetStartupTasks().OfType<PlsqlWorkspacePersistence>());
            }
            finally
            {
                StartupFilterTests.DeleteDir(dir);
            }
        });

        private static void Pump()
        {
            for (var i = 0; i < 6; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    /// <summary>
    /// 기능 게이트를 갈아 끼운 앱 전체(조립·메인 창). <see cref="ShellHarness"/> 와 같되 셸을 짓기 전에 게이트를 등록한다
    /// (마지막 등록이 이긴다). 시작 작업은 돌리지 않는다 — 시험이 필요한 것만 부른다.
    /// </summary>
    internal sealed class GatedShell : IDisposable
    {
        private readonly bool _ownsDirectory;
        private readonly Oranda.App.Features.SqlEditor.SqlEditorHost _previousHost;

        public GatedShell(IFeatureGate gate, string? dataDirectory = null)
        {
            _ownsDirectory = dataDirectory is null;
            DataDirectory = dataDirectory ?? StartupFilterTests.TempDir();
            Services = AppComposition.Create(new AppCompositionOptions { DataDirectory = DataDirectory, DevBuild = true });
            Services.AddInstance(gate);
            _previousHost = Oranda.App.Features.SqlEditor.SqlEditorHost.Current;
            Oranda.App.Features.SqlEditor.SqlEditorHost.Current = new Oranda.App.Features.SqlEditor.SqlEditorHost();
            Window = new MainWindow();
            ((Oranda.App.App)Application.Current!).Start(Services, Window);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public string DataDirectory { get; }

        public ServiceContainer Services { get; }

        public MainWindow Window { get; }

        public void Dispose()
        {
            Window.CloseWithoutSaving();
            Dispatcher.UIThread.RunJobs();
            (Application.Current as Oranda.App.App)?.ReleaseServices(Services);
            Services.Dispose();
            Oranda.App.Features.SqlEditor.SqlEditorHost.Current = _previousHost;
            if (_ownsDirectory)
            {
                StartupFilterTests.DeleteDir(DataDirectory);
            }
        }
    }
}

// 시험 탐침: 네임스페이스로 기능 꼬리표를 받는다(TRD 10.2). MyBatisWorkspace 아래 = myBatis.
namespace Oranda.App.Features.MyBatisWorkspace.RoleProbes
{
    public sealed class MyBatisProbeTask : IStartupTask
    {
        private static int _created;
        private static int _ran;

        public MyBatisProbeTask() => Interlocked.Increment(ref _created);

        public static int Created => Volatile.Read(ref _created);

        public static int Ran => Volatile.Read(ref _ran);

        public StartupPhase Phase => StartupPhase.AfterFirstFrame;

        public static void Reset() => (_created, _ran) = (0, 0);

        public Task RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _ran);
            return Task.CompletedTask;
        }
    }

    /// <summary>지연 어셈블리(이 시험 어셈블리를 지연으로 등록) 안의 myBatis 작업.</summary>
    [OrandaService(typeof(IStartupTask))]
    public sealed class DeferredMyBatisProbeTask : IStartupTask
    {
        private static int _created;
        private static int _ran;

        public DeferredMyBatisProbeTask() => Interlocked.Increment(ref _created);

        public static int Created => Volatile.Read(ref _created);

        public static int Ran => Volatile.Read(ref _ran);

        public StartupPhase Phase => StartupPhase.Idle;

        public static void Reset() => (_created, _ran) = (0, 0);

        public Task RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _ran);
            return Task.CompletedTask;
        }
    }
}

// 공통(꼬리표 없음) 탐침.
namespace Oranda.App.Tests.Features.Roles.Probes
{
    public sealed class CommonProbeTask : IStartupTask
    {
        private static int _created;
        private static int _ran;

        public CommonProbeTask() => Interlocked.Increment(ref _created);

        public static int Created => Volatile.Read(ref _created);

        public static int Ran => Volatile.Read(ref _ran);

        public StartupPhase Phase => StartupPhase.AfterFirstFrame;

        public static void Reset() => (_created, _ran) = (0, 0);

        public Task RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _ran);
            return Task.CompletedTask;
        }
    }

    public sealed class CommonIdleProbeTask : IStartupTask
    {
        private static int _ran;

        public static int Ran => Volatile.Read(ref _ran);

        public StartupPhase Phase => StartupPhase.Idle;

        public static void Reset() => _ran = 0;

        public Task RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _ran);
            return Task.CompletedTask;
        }
    }

    [OrandaService(typeof(IStartupTask))]
    public sealed class DeferredCommonProbeTask : IStartupTask
    {
        private static int _ran;

        public static int Ran => Volatile.Read(ref _ran);

        public StartupPhase Phase => StartupPhase.Idle;

        public static void Reset() => _ran = 0;

        public Task RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _ran);
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowingProbeTask : IStartupTask
    {
        public StartupPhase Phase => StartupPhase.AfterFirstFrame;

        public Task RunAsync(CancellationToken ct) => throw new InvalidOperationException("탐침 실패");
    }
}
