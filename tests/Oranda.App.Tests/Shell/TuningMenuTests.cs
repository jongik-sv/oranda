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
        var ids = CommandIds(TuningMenu());
        string[] expected =
        [
            "oranda.plan.explain", "oranda.plan.actual", "oranda.plan.displayCursorOptions", "oranda.plan.pinForCompare", "oranda.plan.compare",
            "oranda.monitor.sql", "oranda.monitor.sqlSearch", "oranda.monitor.sqlShowPlan", "oranda.monitor.sqlShowBinds",
        ];
        Assert.Equal(expected, ids.Where(id => !id.StartsWith("oranda.tuning.", StringComparison.Ordinal))); // oranda.tuning.* 은 각 Task 가 더한다
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
        // 처리기를 단 oranda.tuning.* 명령은 모두 계약 상수에 있다(TSK-06-03 부터 차례로 등록).
        Assert.All(AppCommands.Where(c => c.Id.StartsWith("oranda.tuning.", StringComparison.Ordinal)), c => Assert.Contains(c.Id, TuningCommands.Ids));
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
