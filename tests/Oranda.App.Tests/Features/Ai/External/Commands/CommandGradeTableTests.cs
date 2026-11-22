using Oranda.App.Features.Ai.External.Commands;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Tests.Features.Ai.External.Commands;

/// <summary>
/// <c>run_command</c> 안전 등급표(2단계 계획 §3.3·E-30). 빠짐 시험: 앱이 특성으로 등록하는 명령 전부가 표에 있고 예외·규칙 하나에 걸린다.
/// 새 명령이 생기면 첫 시험이 그 id 를 꼽는다 — <see cref="CommandGradeTable"/> 의 <c>KnownIds</c> 에 넣고 등급을 정한다.
/// </summary>
public class CommandGradeTableTests
{
    private static readonly IReadOnlyList<string> Registered =
        [.. ShellContributions.Scan(typeof(AppComposition).Assembly).Commands.Select(c => c.Definition.Id).Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_registered_command_is_in_the_table_and_the_table_has_no_stale_ids()
    {
        var added = Registered.Where(id => !CommandGradeTable.KnownIds.Contains(id)).ToList();
        var removed = CommandGradeTable.KnownIds.Where(id => !Registered.Contains(id)).Order(StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0, "새 명령이 등급표에 없다(지금은 거부로 떨어진다). CommandGradeTable.KnownIds 에 넣고 예외·규칙으로 등급을 정하세요: " + string.Join(", ", added));
        Assert.True(removed.Count == 0, "등급표에만 있고 앱이 등록하지 않는 id(이름이 바뀌었나): " + string.Join(", ", removed));
    }

    [Fact]
    public void Every_known_id_is_graded_by_an_exception_or_a_rule_not_by_the_default()
    {
        var unclassified = CommandGradeTable.KnownIds.Where(id => CommandGradeTable.Classify(id) is null).Order(StringComparer.Ordinal).ToList();

        Assert.True(unclassified.Count == 0, "예외·접두어 규칙 어디에도 안 걸리는 id: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void Exceptions_and_rules_all_point_at_known_commands()
    {
        Assert.All(CommandGradeTable.Exceptions.Keys, id => Assert.Contains(id, CommandGradeTable.KnownIds));
        Assert.All(CommandGradeTable.PrefixRules, rule => Assert.Contains(CommandGradeTable.KnownIds, id => id.StartsWith(rule.Prefix, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("oranda.view.dynamicExtension")] // 바로 접두어 아래라도 표가 모르면 거부(동적 등록)
    [InlineData("oranda.window.fromPlugin")]
    [InlineData("oranda.help.newPage")]
    [InlineData("oranda.grid.sortByPlugin")]
    [InlineData("oranda.file.saveSomewhere")]
    [InlineData("oranda.unknown")]
    [InlineData("")]
    public void Unknown_ids_are_denied_even_under_a_now_or_confirm_prefix(string id)
    {
        Assert.Equal(CommandGrade.Deny, CommandGradeTable.Grade(id));
    }

    [Theory]
    // §3.3 바로
    [InlineData("oranda.view.history", CommandGrade.Now)]
    [InlineData("oranda.window.nextDocument", CommandGrade.Now)]
    [InlineData("oranda.nav.back", CommandGrade.Now)]
    [InlineData("oranda.nav.forward", CommandGrade.Now)]
    [InlineData("oranda.nav.describeObject", CommandGrade.Now)]
    [InlineData("oranda.nav.quickOpenObject", CommandGrade.Now)]
    [InlineData("oranda.nav.goToDefinition", CommandGrade.Now)]
    [InlineData("oranda.file.newSqlEditor", CommandGrade.Now)]
    [InlineData("oranda.file.newPlsqlEditor", CommandGrade.Now)]
    [InlineData("oranda.file.open", CommandGrade.Now)]
    [InlineData("oranda.grid.sortAscending", CommandGrade.Now)]
    [InlineData("oranda.grid.filterByValue", CommandGrade.Now)]
    [InlineData("oranda.grid.toggleTextMode", CommandGrade.Now)]
    [InlineData("oranda.grid.hideColumn", CommandGrade.Now)]
    [InlineData("oranda.grid.showAllColumns", CommandGrade.Now)]
    [InlineData("oranda.grid.freezeColumns", CommandGrade.Now)]
    [InlineData("oranda.grid.rowView", CommandGrade.Now)]
    [InlineData("oranda.grid.viewValue", CommandGrade.Now)]
    [InlineData("oranda.grid.find", CommandGrade.Now)]
    [InlineData("oranda.grid.fetchAll", CommandGrade.Now)]
    [InlineData("oranda.grid.countRows", CommandGrade.Now)]
    [InlineData("oranda.grid.export", CommandGrade.Now)]
    [InlineData("oranda.grid.exportExcel", CommandGrade.Now)]
    [InlineData("oranda.monitor.sessions", CommandGrade.Now)]
    [InlineData("oranda.monitor.locks", CommandGrade.Now)]
    [InlineData("oranda.monitor.sql", CommandGrade.Now)]
    [InlineData("oranda.monitor.refresh", CommandGrade.Now)]
    [InlineData("oranda.monitor.sessionDetail", CommandGrade.Now)]
    [InlineData("oranda.exec.cancel", CommandGrade.Now)]
    [InlineData("oranda.exec.goToError", CommandGrade.Now)]
    [InlineData("oranda.exec.toggleDbmsOutput", CommandGrade.Now)]
    [InlineData("oranda.exec.toggleStatistics", CommandGrade.Now)]
    [InlineData("oranda.help.about", CommandGrade.Now)]
    [InlineData("oranda.edit.find", CommandGrade.Now)]
    [InlineData("oranda.edit.goToLine", CommandGrade.Now)]
    [InlineData("oranda.edit.fold", CommandGrade.Now)]
    [InlineData("oranda.edit.foldAll", CommandGrade.Now)]
    [InlineData("oranda.edit.unfoldAll", CommandGrade.Now)]
    // §3.3 띠 한 번
    [InlineData("oranda.file.save", CommandGrade.Confirm)]
    [InlineData("oranda.file.saveAs", CommandGrade.Confirm)]
    [InlineData("oranda.file.saveAll", CommandGrade.Confirm)]
    [InlineData("oranda.file.saveWorkspace", CommandGrade.Confirm)]
    [InlineData("oranda.file.saveWorkspaceFileAs", CommandGrade.Confirm)]
    [InlineData("oranda.file.close", CommandGrade.Confirm)]
    [InlineData("oranda.file.closeAll", CommandGrade.Confirm)]
    [InlineData("oranda.file.closeWorkspace", CommandGrade.Confirm)]
    [InlineData("oranda.conn.disconnect", CommandGrade.Confirm)]
    [InlineData("oranda.conn.reconnect", CommandGrade.Confirm)]
    [InlineData("oranda.monitor.locks.exportCsv", CommandGrade.Confirm)]
    // §3.3 거부
    [InlineData("oranda.edit.undo", CommandGrade.Deny)]
    [InlineData("oranda.edit.paste", CommandGrade.Deny)]
    [InlineData("oranda.edit.copy", CommandGrade.Deny)]
    [InlineData("oranda.edit.findNext", CommandGrade.Deny)] // §3.3 이 꼽지 않은 edit.* 는 거부
    [InlineData("oranda.format.document", CommandGrade.Deny)]
    [InlineData("oranda.format.statement", CommandGrade.Deny)]
    [InlineData("oranda.exec.runStatement", CommandGrade.Deny)]
    [InlineData("oranda.exec.runScript", CommandGrade.Deny)]
    [InlineData("oranda.exec.runForEdit", CommandGrade.Deny)]
    [InlineData("oranda.txn.commit", CommandGrade.Deny)]
    [InlineData("oranda.txn.rollback", CommandGrade.Deny)]
    [InlineData("oranda.txn.toggleAutoCommit", CommandGrade.Deny)]
    [InlineData("oranda.grid.edit.apply", CommandGrade.Deny)]
    [InlineData("oranda.grid.edit.applyAndCommit", CommandGrade.Deny)]
    [InlineData("oranda.grid.flashback.restore", CommandGrade.Deny)]
    [InlineData("oranda.grid.flashback.restoreRow", CommandGrade.Deny)]
    [InlineData("oranda.grid.copy", CommandGrade.Deny)]
    [InlineData("oranda.grid.copyAsInsert", CommandGrade.Deny)]
    [InlineData("oranda.monitor.killSession", CommandGrade.Deny)]
    [InlineData("oranda.monitor.disconnectSession", CommandGrade.Deny)]
    [InlineData("oranda.monitor.traceOn", CommandGrade.Deny)]
    [InlineData("oranda.conn.changePassword", CommandGrade.Deny)]
    [InlineData("oranda.conn.import", CommandGrade.Deny)]
    [InlineData("oranda.conn.export", CommandGrade.Deny)]
    [InlineData("oranda.mybatis.deleteStatement", CommandGrade.Deny)]
    [InlineData("oranda.mybatis.renameStatement", CommandGrade.Deny)]
    [InlineData("oranda.mybatis.applyAutoFix", CommandGrade.Deny)]
    [InlineData("oranda.mybatis.formatAll", CommandGrade.Deny)]
    [InlineData("oranda.ai.tune", CommandGrade.Deny)]
    [InlineData("oranda.ai.explainSql", CommandGrade.Deny)]
    [InlineData("oranda.file.exit", CommandGrade.Deny)]
    public void Section_3_3_examples_get_their_grade(string id, CommandGrade grade)
    {
        Assert.Equal(grade, CommandGradeTable.Grade(id));
    }

    [Theory]
    // 접두어 예시가 덮지 않는 자리: 정확한 예외로 막는다
    [InlineData("oranda.monitor.locks.killHolder")]
    [InlineData("oranda.monitor.locks.disconnectHolder")]
    [InlineData("oranda.agent.external.connect")]
    [InlineData("oranda.exec.pasteBindValues")]
    [InlineData("oranda.grid.edit.toggleOptimisticLock")] // grid.toggle* 보다 grid.edit.* 가 길다
    [InlineData("oranda.plan.explain")]
    [InlineData("oranda.plan.runActual")]
    [InlineData("oranda.tuning.benchmark")]
    [InlineData("oranda.tuning.rerunWithHint")]
    [InlineData("oranda.tuning.optimizerTrace")]
    [InlineData("oranda.sqlplus.runFile")]
    [InlineData("oranda.rollback.runScript")]
    [InlineData("oranda.plsql.run")]
    [InlineData("oranda.plsql.runFromBrowser")]
    [InlineData("oranda.plsql.compileSpecAndBody")]
    [InlineData("oranda.schema.gatherStats")]
    [InlineData("oranda.schema.compileInvalid")]
    [InlineData("oranda.schema.recyclebinPurge")]
    [InlineData("oranda.schema.insertSelect")]
    [InlineData("oranda.tuning.gatherStats")]
    [InlineData("oranda.tools.import")]
    [InlineData("oranda.tools.keymapPreset.default")]
    [InlineData("oranda.tableEdit.toggle")]
    [InlineData("oranda.mybatis.newStatement")]
    [InlineData("oranda.mybatis.copyExpanded")]
    public void Commands_that_touch_the_database_text_clipboard_or_grants_are_denied(string id)
    {
        Assert.Contains(id, CommandGradeTable.KnownIds);
        Assert.Equal(CommandGrade.Deny, CommandGradeTable.Grade(id));
    }

    [Fact]
    public void Grade_counts_are_pinned_so_a_rule_edit_cannot_move_commands_silently()
    {
        var counts = CommandGradeTable.KnownIds.GroupBy(CommandGradeTable.Grade).ToDictionary(g => g.Key, g => g.Count());

        // 바뀌면: 어떤 명령의 등급이 옮겨 갔는지 확인하고 이 수를 고친다.
        Assert.Equal(
            "now=165 confirm=16 deny=132",
            $"now={counts.GetValueOrDefault(CommandGrade.Now)} confirm={counts.GetValueOrDefault(CommandGrade.Confirm)} deny={counts.GetValueOrDefault(CommandGrade.Deny)}");
    }

    [Theory]
    [InlineData(CommandGrade.Now, "now")]
    [InlineData(CommandGrade.Confirm, "confirm")]
    [InlineData(CommandGrade.Deny, "deny")]
    public void Grades_round_trip_through_their_contract_text(CommandGrade grade, string text)
    {
        Assert.Equal(text, grade.ToWire());
        Assert.Contains(text, AppToolValues.CommandGrades.All);
        Assert.Equal(grade, CommandGradeNames.FromWire(text));
    }

    [Fact]
    public void Unknown_grade_text_is_not_a_grade()
    {
        Assert.Null(CommandGradeNames.FromWire("maybe"));
        Assert.Null(CommandGradeNames.FromWire(null));
    }
}
