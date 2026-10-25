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
using Oranda.App.Tests.Shell;
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
/// TSK-07-08 통합 2: 흐름. 설정 파일 없음(= 기본 Role SQL 개발자, D-013)으로 시작 → 설정 「기능」 탭(<c>oranda.tools.roles</c> 로 연 실제 설정 창)에서
/// 「모든 기능 쓰기」 → 즉시 반영(메뉴) / 재시작 안내(시작 작업) → 저장 → 재시작 → 조회 전용 → Role 더하기 → 「모든 기능 쓰기」 로 되돌리기.
/// 모두 실제 FeatureGate·설정 저장소·레지스트리·메인 창.
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

    /// <summary>설정 「기능」 탭에서 주 업무 하나와 덧붙임을 고른다(TSK-09-08, D-017).</summary>
    private static void Choose(SettingsViewModel vm, PrimaryWorkKind primary, bool plsql = false, bool myBatis = false)
    {
        vm.Features.Primary = primary;
        vm.Features.PlsqlAddon = plsql;
        vm.Features.MyBatisAddon = myBatis;
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

    private static bool AllFeaturesSaved(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")));
        return doc.RootElement.TryGetProperty("features", out var f) && f.TryGetProperty("allFeatures", out var a) && a.ValueKind == JsonValueKind.True;
    }

    [Fact]
    public Task Full_flow_default_R1_to_all_features_restart_R6_R6R1_add_R4_and_back_to_all_features() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            RoleSurface allAtStart;
            var mybatisCommands = ShellTestHelpers.AppContributions.Commands.Select(c => c.Definition.Id)
                .Where(id => id.StartsWith("oranda.mybatis.", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(mybatisCommands);

            // ── 1. settings.json 없음 = 기본 Role SQL 개발자(D-013): MyBatis 시작 작업·상태 칸 없음·블로커 칸 있음 ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var first = app.Capture();
                Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
                Assert.True(PrdMatrix.Enabled(["sqlDev"]).SetEquals(first.GateEnabled));
                Assert.False(HasTop(first, app, "monitor"));
                Assert.True(HasTop(first, app, "tuning")); // planAdvanced 가 튜닝 묶음에 있어 R1 에도 튜닝 메뉴가 남는다
                Assert.Equal(10, first.TopMenus.Count);
                Assert.Empty(first.RegistryCommands.Intersect(mybatisCommands));
                Assert.Empty(first.PendingRestart);
                Assert.Equal(PrdMatrix.ExpectedTasks(PrdMatrix.Enabled(["sqlDev"])).Order(), first.StartupTasks.Order());
                Assert.Equal(9, first.StartupTasks.Count);
                Assert.DoesNotContain(first.StartupTasks, t => PrdMatrix.MyBatisTasks.Contains(t));
                Assert.DoesNotContain("MapperSaveStatusViewModel", first.StatusTypes);
                Assert.Contains("BlockerStatusViewModel", first.StatusTypes);
                Assert.NotNull(app.BlockerChip());
                var startPage = app.Window.GetVisualDescendants().OfType<StartPage>().First();
                Assert.True(startPage.ShowsRoleLink); // 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」 카드(고른 적 없음)

                // 앱이 실제로 시작 작업을 돌리는 길(첫 프레임 뒤 + 유휴 뒤): 건너뛴 작업 수가 Trace 에 남는다(TSK-07-07 이 센다).
                var run = new Oranda.App.App.StartupRun();
                using (var trace = new TraceCapture())
                {
                    await Oranda.App.App.RunStartupTasksAsync(app.Services, run, Oranda.Core.Composition.StartupPhase.AfterFirstFrame);
                    await Oranda.App.App.RunStartupTasksAsync(app.Services, run, null);
                    Assert.Equal(9, run.Ran.Count);
                    Assert.Equal(5, run.Skipped.Count);
                    Assert.Contains(trace.Lines, l => l.Contains("시작 작업: 돈 9개, 건너뜀 5개", StringComparison.Ordinal));
                }

                Assert.Same(MyBatisHooks.Empty, Oranda.App.Features.SqlEditor.SqlEditorHost.Current.MyBatisHooks); // MyBatis 배선이 돌지 않았다

                // ── 2. 설정 「기능」 탭: 주 업무 「SQL 개발」 이 골라진 채 열리고, 「모든 기능」 을 고른다 ──
                var (window, vm) = await OpenFeaturesTabAsync(app);
                Assert.Equal(PrimaryWorkKind.SqlDev, vm.Features.Primary);
                Assert.False(vm.Features.PlsqlAddon || vm.Features.MyBatisAddon);
                Assert.True(vm.Features.ShowDefaultRoleNotice);
                Choose(vm, PrimaryWorkKind.All);
                var closed = false;
                vm.CloseRequested += (_, _) => closed = true;
                await vm.SaveAsync();

                // 재시작 안내 띠: 창을 닫지 않고 「기능」 탭에 남아 MyBatis·PL/SQL 작업 공간을 알린다.
                Assert.False(closed);
                Assert.True(vm.Features.HasRestartBanner);
                Assert.StartsWith("재시작하면 적용됩니다: ", vm.Features.RestartBannerText, StringComparison.Ordinal);
                Assert.Contains("MyBatis", vm.Features.RestartBannerText, StringComparison.Ordinal);
                Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);

                // 재시작 없이 메뉴가 바뀐다(시작 때 도는 MyBatis 명령은 재시작 전까지 나타나지 않는다).
                await RoleApp.SettleAsync(() => HasTop(app.Capture(), app, "monitor"));
                var afterAll = app.Capture();
                Assert.Equal(11, afterAll.TopMenus.Count);
                Assert.Empty(afterAll.RegistryCommands.Intersect(mybatisCommands));
                Assert.Equal(["myBatis", "plsqlWorkspace"], afterAll.PendingRestart.Order().ToArray());
                Assert.Equal(first.StatusTypes.Order(), afterAll.StatusTypes.Order()); // 상태 칸은 재시작 뒤에 반영
                Assert.Null(SavedRoles(dir));
                Assert.True(AllFeaturesSaved(dir)); // 「모든 기능 쓰기」 는 따로 적힌다
                await RoleApp.SettleAsync(() => !startPage.ShowsRoleLink); // 직접 골랐으니 카드가 사라진다
                window.Close();
            }

            // ── 3. 재시작: 모든 기능 = a507d81 과 같은 수 ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var s = allAtStart = app.Capture();
                Assert.Empty(s.PendingRestart);
                Assert.Equal(PrdMatrix.AllFeatures.Order(), s.GateEnabled.Order());
                Assert.Equal(11, s.TopMenus.Count);
                Assert.Equal(14, s.StartupTasks.Count);
                Assert.Equal(10, s.StatusTypes.Count);
                Assert.Subset(s.RegistryCommands, mybatisCommands.ToHashSet(StringComparer.Ordinal));
                Assert.Contains("MapperSaveStatusViewModel", s.StatusTypes);
                Assert.False(app.Window.GetVisualDescendants().OfType<StartPage>().First().ShowsRoleLink);

                // 설정 창은 저장한 「모든 기능 쓰기」 를 그대로 보여 준다(저장 → 다시 읽기 왕복).
                var (window, vm) = await OpenFeaturesTabAsync(app);
                Assert.Equal(PrimaryWorkKind.All, vm.Features.Primary);
                Assert.False(vm.Features.ShowDefaultRoleNotice);
                Assert.False(vm.Features.HasRestartBanner);

                // ── 4. R6 만 고르기 ──
                Choose(vm, PrimaryWorkKind.Viewer);
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

                // ── 5. 조회만 → SQL 개발: 거부 없음(R6 + R1 조합은 화면에서 만들 수 없다 — 그 설정 파일은 RoleEdgeCaseTests·정책 시험) ──
                Choose(vm, PrimaryWorkKind.SqlDev);
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

                // ── 9. 튜닝(R4)으로 바꾸기: 튜닝·모니터 메뉴가 닫는 즉시 나타난다 ──
                Choose(vm, PrimaryWorkKind.Tuner);
                await vm.SaveAsync();
                await RoleApp.SettleAsync(() => HasTop(app.Capture(), app, "monitor"));
                var tuner = app.Capture();
                Assert.True(HasTop(tuner, app, "tuning"));
                Assert.Contains("oranda.tuning.assistant", tuner.MenuCommands);
                Assert.Contains("oranda.monitor.locks", tuner.MenuCommands);
                Assert.Contains("oranda.tuning.report", tuner.MenuCommands);

                // ── 10. 「모든 기능 쓰기」 로 되돌리기: 시작 때가 모든 기능이었으니 재시작 대기 없이 원래 집합 ──
                Choose(vm, PrimaryWorkKind.All);
                await vm.SaveAsync();
                await RoleApp.SettleAsync(() => HasTop(app.Capture(), app, "monitor") && app.Capture().RegistryCommands.Contains("oranda.tools.import"));
                var all = app.Capture();
                Assert.Equal(11, all.TopMenus.Count);
                Assert.Empty(all.PendingRestart);
                Assert.True(allAtStart.RegistryCommands.SetEquals(all.RegistryCommands));
                Assert.False(vm.Features.HasRestartBanner);
                if (window.IsVisible)
                {
                    window.Close(); // 재시작 대기가 없으면 저장이 창을 닫는다
                }
            }

            // ── 원래 집합: 한 번 더 재시작하면 3 의 「모든 기능」 과 같다 ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var back = app.Capture();
                var first = allAtStart;
                Assert.Null(SavedRoles(dir)); // 「모든 기능 쓰기」 = roles 없음 + allFeatures
                Assert.True(AllFeaturesSaved(dir));
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
