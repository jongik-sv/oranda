using Oranda.App.Features.Tuning;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Shell;

/// <summary>TSK-06-01: 「튜닝」 최상위 메뉴 순서와 옮긴 명령 배치, ITuningService 기본 구현.</summary>
public class TuningMenuTests
{
    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    private static MenuNode TuningMenu() =>
        MenuModelBuilder.BuildMenuBar(AppCommands, new ShellText(language: "ko"), _ => null, [])
            [MenuModelBuilder.TopMenus.ToList().IndexOf("tuning")];

    private static List<string> CommandIds(MenuNode node)
    {
        var ids = new List<string>();
        foreach (var child in node.Children)
        {
            if (child.Kind == MenuNodeKind.Command)
            {
                ids.Add(child.CommandId!);
            }

            ids.AddRange(CommandIds(child));
        }

        return ids;
    }

    [Fact]
    public void Tuning_comes_right_after_monitor_and_has_both_language_titles()
    {
        var top = MenuModelBuilder.TopMenus.ToList();
        Assert.Equal(top.IndexOf("monitor") + 1, top.IndexOf("tuning"));
        Assert.Equal("튜닝", new ShellText(language: "ko").Lookup("Menu.tuning"));
        Assert.Equal("Tuning", new ShellText(language: "en").Lookup("Menu.tuning"));
    }

    [Fact]
    public void Moved_commands_sit_under_tuning_in_group_order()
    {
        // 도우미(400~) 등 각 Task 가 더한 oranda.tuning.* 는 자기 시험이 본다.
        var ids = CommandIds(TuningMenu()).Where(id => !id.StartsWith("oranda.tuning.", StringComparison.Ordinal)).ToList();
        string[] expected =
        [
            "oranda.plan.explain", "oranda.plan.actual", "oranda.plan.displayCursorOptions", "oranda.plan.pinForCompare", "oranda.plan.compare",
            "oranda.monitor.sql", "oranda.monitor.sqlSearch", "oranda.monitor.sqlShowPlan", "oranda.monitor.sqlShowBinds",
            "oranda.ai.tune",
        ];
        Assert.Equal(expected, ids.Where(id => !id.StartsWith("oranda.tuning.", StringComparison.Ordinal))); // oranda.tuning.* 은 각 Task 가 더한다
    }

    [Fact]
    public void Plan_boost_commands_sit_in_the_300_group_with_the_rerun_submenu()
    {
        // TSK-06-04: 계획 보강 300~ — 「실제 행 수 받으며 다시 실행 ▶」(힌트·STATISTICS_LEVEL) · 10053 · 트레이스 보기.
        var ids = CommandIds(TuningMenu());
        string[] boost = ["oranda.tuning.rerunWithHint", "oranda.tuning.rerunWithStatisticsLevel", "oranda.tuning.optimizerTrace", "oranda.tuning.traceViewer"];
        var at = ids.IndexOf(boost[0]);
        Assert.True(at > ids.IndexOf("oranda.monitor.sqlShowBinds"));
        Assert.Equal(boost, ids.Skip(at).Take(boost.Length));
        var submenu = TuningMenu().Children.Single(c => c.Children.Any(x => x.CommandId == "oranda.tuning.rerunWithHint"));
        Assert.Equal("실제 행 수 받으며 다시 실행", submenu.Title);
        Assert.Equal(2, submenu.Children.Count(c => c.Kind == MenuNodeKind.Command));
    }

    [Fact]
    public void Explain_and_actual_stay_in_the_sql_menu_but_the_rest_left_it()
    {
        var sql = MenuModelBuilder.BuildMenuBar(AppCommands, new ShellText(language: "ko"), _ => null, [])
            [MenuModelBuilder.TopMenus.ToList().IndexOf("sql")];
        var ids = CommandIds(sql);
        Assert.Contains("oranda.plan.explain", ids);
        Assert.Contains("oranda.plan.actual", ids);
        Assert.DoesNotContain("oranda.plan.displayCursorOptions", ids);
        Assert.DoesNotContain("oranda.plan.pinForCompare", ids);
        Assert.DoesNotContain("oranda.plan.compare", ids);
        var monitor = CommandIds(MenuModelBuilder.BuildMenuBar(AppCommands, new ShellText(language: "ko"), _ => null, [])
            [MenuModelBuilder.TopMenus.ToList().IndexOf("monitor")]);
        Assert.DoesNotContain("oranda.monitor.sql", monitor);
    }

    [Fact]
    public void Shortcuts_and_toolbar_placements_are_kept()
    {
        var explain = AppCommands.Single(c => c.Id == "oranda.plan.explain");
        Assert.Contains("toolbar:main/explain@230", explain.Placements);
        Assert.Contains("menu:sql/explain@50", explain.Placements);
        var sql = AppCommands.Single(c => c.Id == "oranda.monitor.sql");
        Assert.Contains("toolbar:main/sqlMonitor@520", sql.Placements);
    }

    [Fact]
    public void Tuning_command_constants_are_unique_and_registered_ones_are_listed()
    {
        Assert.Equal(TuningCommands.Ids.Length, TuningCommands.Ids.Distinct().Count());
        Assert.All(TuningCommands.Ids, id => Assert.StartsWith("oranda.tuning.", id, StringComparison.Ordinal));
        Assert.All(
            [TuningCommands.StatsPanelId, TuningCommands.ChildCursorsPanelId, TuningCommands.TraceViewerPanelId, TuningCommands.AssistantPanelId],
            id => Assert.StartsWith("oranda.panel.", id, StringComparison.Ordinal));
        // 처리기를 단 oranda.tuning.* 명령은 계약 상수(TuningCommands.Ids)에 있거나 TSK-06-04 의 계획 보강 명령이다.
        string[] planBoost = ["oranda.tuning.traceViewer", "oranda.tuning.optimizerTrace", "oranda.tuning.rerunWithHint", "oranda.tuning.rerunWithStatisticsLevel"];
        Assert.All(AppCommands.Where(c => c.Id.StartsWith("oranda.tuning.", StringComparison.Ordinal)).Select(c => c.Id),
            id => Assert.True(planBoost.Contains(id) || TuningCommands.Ids.Contains(id), id));
        Assert.All(planBoost, id => Assert.Contains(AppCommands, c => c.Id == id));
    }

    private sealed class EmptyTuningService : ITuningService;

    [Fact]
    public async Task Unimplemented_tuning_service_methods_throw_not_supported()
    {
        ITuningService service = new EmptyTuningService();
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetChildCursorsAsync(null!, "abc", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetTableStatsAsync(null!, "S", "T", CancellationToken.None));
    }

    [Fact]
    public void Child_cursor_per_exec_values_are_null_without_executions()
    {
        var row = new ChildCursorRow(0, 1, false, false, true, 0, 100, 5000);
        Assert.Null(row.GetsPerExec);
        Assert.Equal(50d, row with { Executions = 2 } is { } r ? r.GetsPerExec : null);
    }
}
