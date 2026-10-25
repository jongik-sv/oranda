using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.Execution;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.App.Views;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.Language;
using Oranda.Core.History;
using Oranda.Core.Shell;
using Oranda.Testing;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>
/// TSK-07-08 통합 2: 흐름. 설정 파일 없음으로 시작 → 설정 「기능」 탭(<c>oranda.tools.roles</c> 로 연 실제 설정 창)에서 Role 고르기 → 즉시 반영(메뉴) /
/// 재시작 안내(시작 작업) → 저장 → 재시작 → 조회 전용 → Role 더하기 → 「모든 기능 쓰기」 로 되돌리기. 모두 실제 FeatureGate·설정 저장소·레지스트리·메인 창.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RoleItestUi")]
public sealed class RoleMenuFlowTests
{
    private static readonly string[] Writes =
    [
        "update emp set sal = 1 where id = 1", "create table t (x number)", "begin null; end;",
        "delete from emp where id = 1", "insert into emp values (1)", "drop table emp",
    ];

    private static bool HasTop(RoleSurface s, RoleApp app, string id) => s.TopMenus.Contains(app.Get<ShellController>().Text.Lookup($"Menu.{id}"));

    /// <summary>실제 명령으로 설정 창을 「기능」 탭으로 연다(블로커 칸 「설정 열기」·시작 화면 링크가 부르는 길과 같다).</summary>
    private static async Task<(SettingsWindow Window, SettingsViewModel Vm)> OpenFeaturesTabAsync(RoleApp app)
    {
        var ran = await app.Get<ICommandRegistry>().ExecuteAsync(FeatureCommandIds.Roles, app.Get<ContextKeyService>());
        Assert.True(ran);
        var window = Assert.IsType<SettingsWindow>(app.Get<ShellController>().LastWindow);
        var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
        Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);
        return (window, vm);
    }

    private static void Choose(SettingsViewModel vm, params string[] roles)
    {
        vm.Features.UseRoles = true;
        foreach (var r in vm.Features.Roles)
        {
            r.IsChecked = roles.Contains(r.Id);
        }
    }

    private static string[]? SavedRoles(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")));
        if (!doc.RootElement.TryGetProperty("features", out var f) || !f.TryGetProperty("roles", out var r) || r.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return [.. r.EnumerateArray().Select(e => e.GetString()!)];
    }

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

    private static IReadOnlyList<SqlStatement> Split(RoleApp app, string sql)
    {
        app.Services.ScanDeferred();
        return app.Get<ILanguageService>().Split(sql, SplitOptions.Default);
    }

    private static PolicyDecision Judge(RoleApp app, string sql) =>
        app.Get<IExecutionPolicy>().EvaluateAsync(new PolicyContext(TestData.Profile(), TestData.Server(), HistorySource.Editor), Assert.Single(Split(app, sql)), default).AsTask().GetAwaiter().GetResult();

    [Fact]
    public Task Full_flow_default_to_R1_restart_R6_R6R1_add_R4_and_back_to_all_features() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            RoleSurface first;
            string[] mybatisCommands;

            // ── 1. settings.json 없음 ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                first = app.Capture();
                Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
                Assert.Equal(11, first.TopMenus.Count);
                Assert.Equal(14, first.StartupTasks.Count);
                Assert.Equal(10, first.StatusTypes.Count);
                Assert.Equal(PrdMatrix.AllFeatures.Order(), first.GateEnabled.Order());
                mybatisCommands = [.. first.RegistryCommands.Where(id => id.StartsWith("oranda.mybatis.", StringComparison.Ordinal))];
                Assert.NotEmpty(mybatisCommands);
                var startPage = app.Window.GetVisualDescendants().OfType<StartPage>().First();
                Assert.True(startPage.ShowsRoleLink); // 「Role 고르기…」 줄(Role 을 고른 적 없음)

                // ── 2. 설정 「기능」 탭에서 R1 고르기 ──
                var (window, vm) = await OpenFeaturesTabAsync(app);
                Assert.True(vm.Features.UseAllFeatures);
                Choose(vm, RoleIds.SqlDev);
                var closed = false;
                vm.CloseRequested += (_, _) => closed = true;
                await vm.SaveAsync();

                // 재시작 안내 띠: 창을 닫지 않고 「기능」 탭에 남아 MyBatis·PL/SQL 작업 공간을 알린다.
                Assert.False(closed);
                Assert.True(vm.Features.HasRestartBanner);
                Assert.StartsWith("재시작하면 적용됩니다: ", vm.Features.RestartBannerText, StringComparison.Ordinal);
                Assert.Contains("MyBatis", vm.Features.RestartBannerText, StringComparison.Ordinal);
                Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);
                Assert.Equal(["changeJournal"], app.Gate.EnabledFeatures.Intersect(["changeJournal", "myBatis"]).ToArray()); // myBatis 는 곧바로 꺼진 것으로 본다

                // 재시작 없이 메뉴가 바뀐다.
                await RoleApp.SettleAsync(() => !HasTop(app.Capture(), app, "monitor"));
                var afterR1 = app.Capture();
                Assert.False(HasTop(afterR1, app, "monitor"));
                Assert.True(HasTop(afterR1, app, "tuning")); // planAdvanced 가 튜닝 묶음에 있어 R1 에도 튜닝 메뉴가 남는다
                Assert.Empty(afterR1.RegistryCommands.Intersect(mybatisCommands));
                Assert.DoesNotContain(afterR1.MenuCommands, id => id.StartsWith("oranda.mybatis.", StringComparison.Ordinal));
                Assert.Equal(["myBatis", "plsqlWorkspace"], afterR1.PendingRestart.Order().ToArray());
                // 시작 때 만든 상태 칸은 이 실행 동안 그대로다(재시작 뒤에 반영).
                Assert.Equal(first.StatusTypes.Order(), afterR1.StatusTypes.Order());
                Assert.Equal(["sqlDev"], SavedRoles(dir)); // 저장 파일에 Role 이 적혔다
                window.Close();
            }

            // ── 3. 재시작: MyBatis 시작 작업·상태 칸 없음·블로커 칸 있음 ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var s = app.Capture();
                Assert.Empty(s.PendingRestart);
                Assert.Equal(PrdMatrix.ExpectedTasks(PrdMatrix.Enabled(["sqlDev"])).Order(), s.StartupTasks.Order());
                Assert.Equal(9, s.StartupTasks.Count);
                Assert.DoesNotContain(s.StartupTasks, t => PrdMatrix.MyBatisTasks.Contains(t));
                Assert.DoesNotContain("MapperSaveStatusViewModel", s.StatusTypes);
                Assert.Contains("BlockerStatusViewModel", s.StatusTypes);
                Assert.NotNull(app.BlockerChip());
                Assert.False(app.Window.GetVisualDescendants().OfType<StartPage>().First().ShowsRoleLink);

                // 앱이 실제로 시작 작업을 돌리는 길(첫 프레임 뒤 + 유휴 뒤): 건너뛴 작업 수가 Trace 에 남는다(TSK-07-07 이 센다).
                var run = new Oranda.App.App.StartupRun();
                using var trace = new TraceCapture();
                await Oranda.App.App.RunStartupTasksAsync(app.Services, run, Oranda.Core.Composition.StartupPhase.AfterFirstFrame);
                await Oranda.App.App.RunStartupTasksAsync(app.Services, run, null);
                Assert.Equal(9, run.Ran.Count);
                Assert.Equal(5, run.Skipped.Count);
                Assert.Contains(trace.Lines, l => l.Contains("시작 작업: 돈 9개, 건너뜀 5개", StringComparison.Ordinal));
                Assert.Same(MyBatisHooks.Empty, Oranda.App.Features.SqlEditor.SqlEditorHost.Current.MyBatisHooks); // MyBatis 배선이 돌지 않았다

                // 설정 창은 저장한 Role 을 그대로 보여 준다(저장 → 다시 읽기 왕복).
                var (window, vm) = await OpenFeaturesTabAsync(app);
                Assert.True(vm.Features.UseRoles);
                Assert.Equal([RoleIds.SqlDev], vm.Features.Roles.Where(r => r.IsChecked).Select(r => r.Id).ToArray());
                Assert.False(vm.Features.HasRestartBanner);

                // ── 4. R6 만 고르기 ──
                Choose(vm, RoleIds.Viewer);
                Assert.True(vm.Features.IsViewerOnly);
                Assert.All(vm.Features.Items.Where(i => PrdMatrix.WritesOnly.Contains(i.Id)), i => Assert.True(i.IsBlocked && !i.IsChecked));
                await vm.SaveAsync();
                await RoleApp.SettleAsync(() => !HasTop(app.Capture(), app, "tuning"));
                var viewer = app.Capture();
                Assert.False(HasTop(viewer, app, "monitor"));
                Assert.False(HasTop(viewer, app, "tuning"));
                Assert.True(viewer.GateReadOnlyRole);
                Assert.Empty(viewer.RegistryCommands.Where(id => id.StartsWith("oranda.grid.edit.", StringComparison.Ordinal)));
                Assert.DoesNotContain("oranda.tools.import", viewer.RegistryCommands);
                Assert.Contains("oranda.grid.exportExcel", viewer.RegistryCommands); // 내보내기는 조회 전용에도 남는다
                Assert.Contains("oranda.plan.explain", viewer.PaletteCommands); // 공통 계획 명령은 팔레트에 남는다
                Assert.DoesNotContain("oranda.monitor.locks", viewer.PaletteCommands);

                // 실행 정책: 조회는 통과, 쓰기·DDL·익명 블록은 거부(드라이버 호출 전). 상태 키도 참.
                ExecutionCommands.Resolve(app.Services);
                Assert.True(app.Get<ContextKeyService>().Get("readOnlySession"));
                Assert.Equal(PolicyDecisionKind.Allow, Judge(app, "select * from dual").Kind);
                foreach (var sql in Writes)
                {
                    var d = Judge(app, sql);
                    Assert.True(d.Kind == PolicyDecisionKind.Deny && d.Rule == PolicyRule.ReadOnlyRole, $"{sql} => {d.Kind}/{d.Rule}");
                    Assert.Contains("조회 전용 Role 에서는", d.Message, StringComparison.Ordinal);
                }

                // ── 5. R6 + R1: 거부 없음 ──
                Choose(vm, RoleIds.Viewer, RoleIds.SqlDev);
                Assert.False(vm.Features.IsViewerOnly);
                await vm.SaveAsync();
                await RoleApp.SettleAsync(() => !app.Gate.ReadOnlyRole);
                Assert.False(app.Get<ContextKeyService>().Get("readOnlySession"));
                foreach (var sql in Writes.Take(3))
                {
                    Assert.NotEqual(PolicyRule.ReadOnlyRole, Judge(app, sql).Rule);
                    Assert.NotEqual(PolicyDecisionKind.Deny, Judge(app, sql).Kind);
                }

                await RoleApp.SettleAsync(() => app.Capture().RegistryCommands.Contains("oranda.grid.edit.addRow"));

                // ── 9. R4 더하기: 튜닝·모니터 메뉴가 닫는 즉시 나타난다 ──
                Choose(vm, RoleIds.Viewer, RoleIds.SqlDev, RoleIds.Tuner);
                await vm.SaveAsync();
                await RoleApp.SettleAsync(() => HasTop(app.Capture(), app, "monitor"));
                var tuner = app.Capture();
                Assert.True(HasTop(tuner, app, "tuning"));
                Assert.Contains("oranda.tuning.assistant", tuner.MenuCommands);
                Assert.Contains("oranda.monitor.locks", tuner.MenuCommands);
                Assert.Contains("oranda.tuning.report", tuner.MenuCommands);

                // ── 10. 「모든 기능 쓰기」 로 되돌리기 ──
                vm.Features.UseAllFeatures = true;
                await vm.SaveAsync();
                await RoleApp.SettleAsync(() => HasTop(app.Capture(), app, "monitor") && app.Capture().RegistryCommands.Contains("oranda.tools.import"));
                var all = app.Capture();
                Assert.Equal(11, all.TopMenus.Count);
                // 시작 때 꺼져 있던 기능(MyBatis·PL/SQL 작업 공간)은 재시작 전까지 돌아오지 않는다 — 안내가 이를 알린다.
                Assert.Contains("myBatis", all.PendingRestart);
                Assert.Empty(all.RegistryCommands.Intersect(mybatisCommands));
                Assert.True(vm.Features.HasRestartBanner);
                window.Close();
            }

            // ── 원래 집합: 한 번 더 재시작하면 처음(설정 없음)과 같다 ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var back = app.Capture();
                Assert.Null(SavedRoles(dir)); // 「모든 기능 쓰기」 = roles 없음
                Assert.True(first.RegistryCommands.SetEquals(back.RegistryCommands));
                Assert.True(first.RegistryPanels.SetEquals(back.RegistryPanels));
                Assert.True(first.MenuCommands.SetEquals(back.MenuCommands));
                Assert.True(first.ToolbarCommands.SetEquals(back.ToolbarCommands));
                Assert.True(first.PaletteCommands.SetEquals(back.PaletteCommands));
                Assert.Equal(first.TopMenus, back.TopMenus);
                Assert.Equal(first.StatusTypes.Order(), back.StatusTypes.Order());
                Assert.Equal(first.StartupTasks.Order(), back.StartupTasks.Order());
                Assert.True(first.VisibleToolWindows.SetEquals(back.VisibleToolWindows));
                Assert.Empty(back.PendingRestart);
            }
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });
}
