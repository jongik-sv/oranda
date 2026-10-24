using Oranda.Core.Tuning;

namespace Oranda.Core.Tests.Tuning;

// 소유: TSK-06-02(TN-16). 스크립트 문안 — 글만 만든다.
public class TuningScriptsTests
{
    [Fact]
    public void Dbms_stats_for_whole_table_cascades_and_warns_it_writes()
    {
        var script = TuningScripts.DbmsStats("SCOTT", "EMP");

        Assert.Equal(TuningScriptKind.DbmsStats, script.Kind);
        Assert.StartsWith("-- ", script.Body, StringComparison.Ordinal);
        Assert.Contains("DB 에 씁니다", script.Body, StringComparison.Ordinal);
        Assert.Contains("DBMS_STATS.GATHER_TABLE_STATS(", script.Body, StringComparison.Ordinal);
        Assert.Contains("ownname          => 'SCOTT'", script.Body, StringComparison.Ordinal);
        Assert.Contains("tabname          => 'EMP'", script.Body, StringComparison.Ordinal);
        Assert.Contains("'FOR ALL COLUMNS SIZE AUTO'", script.Body, StringComparison.Ordinal);
        Assert.Contains("cascade          => TRUE", script.Body, StringComparison.Ordinal);
        Assert.EndsWith("END;\n/", script.Body.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_case_names_are_double_quoted_and_quotes_in_literals_are_doubled()
    {
        var script = TuningScripts.DbmsStats("App", "O'Brien Tab", "Col1");

        Assert.Contains("ownname          => '\"App\"'", script.Body, StringComparison.Ordinal);
        Assert.Contains("tabname          => '\"O''Brien Tab\"'", script.Body, StringComparison.Ordinal);
        Assert.Contains("'FOR COLUMNS SIZE AUTO \"Col1\"'", script.Body, StringComparison.Ordinal);
        Assert.Contains("cascade          => FALSE", script.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A\"B")]
    [InlineData("A\nB")]
    [InlineData("A\0B")]
    public void Names_that_cannot_be_identifiers_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => TuningScripts.DbmsStats(name, "T"));
        Assert.Throws<ArgumentException>(() => TuningScripts.DbmsStats("S", name));
    }

    [Fact]
    public void Too_long_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => TuningScripts.DbmsStats(new string('A', 129), "T"));
        Assert.Equal("'" + new string('A', 128) + "'", TuningScripts.Literal(TuningScripts.DictionaryName(new string('A', 128))));
    }

    [Fact]
    public void Tuning_advisor_creates_executes_and_reports_with_pack_warning()
    {
        var script = TuningScripts.TuningAdvisor("4c8yp4y5m3s1u");

        Assert.Equal(TuningScriptKind.TuningAdvisor, script.Kind);
        Assert.Contains("DB 에 씁니다", script.Body, StringComparison.Ordinal);
        Assert.Contains("Tuning Pack 필요", script.Body, StringComparison.Ordinal);
        Assert.Contains("DBMS_SQLTUNE.CREATE_TUNING_TASK(", script.Body, StringComparison.Ordinal);
        Assert.Contains("sql_id      => '4c8yp4y5m3s1u'", script.Body, StringComparison.Ordinal);
        Assert.Contains("task_name   => 'ORANDA_4C8YP4Y5M3S1U'", script.Body, StringComparison.Ordinal);
        Assert.Contains("time_limit  => 60", script.Body, StringComparison.Ordinal);
        Assert.Contains("DBMS_SQLTUNE.EXECUTE_TUNING_TASK(task_name => l_task)", script.Body, StringComparison.Ordinal);
        Assert.Contains("DBMS_SQLTUNE.REPORT_TUNING_TASK('ORANDA_4C8YP4Y5M3S1U')", script.Body, StringComparison.Ordinal);
        Assert.Contains("-- EXEC DBMS_SQLTUNE.DROP_TUNING_TASK", script.Body, StringComparison.Ordinal); // 지우기는 주석으로만.
    }

    [Fact]
    public void Task_name_quote_is_escaped_and_bad_inputs_are_rejected()
    {
        var script = TuningScripts.TuningAdvisor("4c8yp4y5m3s1u", "my'task", 5);
        Assert.Contains("task_name   => 'my''task'", script.Body, StringComparison.Ordinal);
        Assert.Contains("time_limit  => 5", script.Body, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => TuningScripts.TuningAdvisor("x'; DROP TABLE t; --"));
        Assert.Throws<ArgumentException>(() => TuningScripts.TuningAdvisor("4C8YP4Y5M3S1U")); // sql_id 는 소문자
        Assert.Throws<ArgumentException>(() => TuningScripts.TuningAdvisor("4c8yp4y5m3s1u", "  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => TuningScripts.TuningAdvisor("4c8yp4y5m3s1u", null, 0));
        Assert.Throws<ArgumentNullException>(() => TuningScripts.TuningAdvisor(null!));
    }

    [Fact]
    public void Baseline_loads_from_cursor_cache_with_optional_plan_hash()
    {
        var all = TuningScripts.Baseline("4c8yp4y5m3s1u", null, fixedPlan: false);
        Assert.Equal(TuningScriptKind.Baseline, all.Kind);
        Assert.Contains("DB 에 씁니다", all.Body, StringComparison.Ordinal);
        Assert.Contains("Tuning Pack", all.Body, StringComparison.Ordinal);
        Assert.Contains("DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE(", all.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("plan_hash_value", all.Body, StringComparison.Ordinal);
        Assert.Contains("fixed           => 'NO'", all.Body, StringComparison.Ordinal);

        var one = TuningScripts.Baseline(" 4c8yp4y5m3s1u ", 3956160932);
        Assert.Contains("sql_id          => '4c8yp4y5m3s1u'", one.Body, StringComparison.Ordinal);
        Assert.Contains("plan_hash_value => 3956160932", one.Body, StringComparison.Ordinal);
        Assert.Contains("fixed           => 'YES'", one.Body, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => TuningScripts.Baseline("4c8yp4y5m3s1u", -1));
        Assert.Throws<ArgumentException>(() => TuningScripts.Baseline("short"));
    }

    [Fact]
    public void Literal_doubles_single_quotes()
    {
        Assert.Equal("'a''b'''", TuningScripts.Literal("a'b'"));
        Assert.Equal("''", TuningScripts.Literal(string.Empty));
        Assert.Equal("ABC_1$#", TuningScripts.DictionaryName("ABC_1$#"));
        Assert.Equal("\"1ABC\"", TuningScripts.DictionaryName("1ABC"));
        Assert.Equal("\"한글\"", TuningScripts.DictionaryName("한글"));
    }
}
