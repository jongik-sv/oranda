using Oranda.Language.Editing;

namespace Oranda.Language.Tests.Editing;

// 소유: fb-w1 FB-01. 「이 행 상태로 되돌리기」의 과거 값 결과 판정(단일 표 + VERSIONS BETWEEN·AS OF 절)과 절 뒤 별칭 읽기.
public class FlashbackResultAnalyzerTests
{
    [Theory]
    [InlineData("SELECT * FROM emp VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE", FlashbackClauseKind.Versions, null, "VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE")]
    [InlineData("select versions_operation, e.* from scott.emp versions between timestamp systimestamp - interval '1' hour and systimestamp e where e.empno = 7369",
        FlashbackClauseKind.Versions, "E", "versions between timestamp systimestamp - interval '1' hour and systimestamp")]
    [InlineData("SELECT * FROM emp AS OF TIMESTAMP (SYSTIMESTAMP - INTERVAL '10' MINUTE)", FlashbackClauseKind.AsOf, null, "AS OF TIMESTAMP (SYSTIMESTAMP - INTERVAL '10' MINUTE)")]
    [InlineData("SELECT * FROM emp AS OF TIMESTAMP SYSTIMESTAMP - INTERVAL '10' MINUTE", FlashbackClauseKind.AsOf, null, "AS OF TIMESTAMP SYSTIMESTAMP - INTERVAL '10' MINUTE")]
    [InlineData("SELECT e.* FROM emp AS OF SCN 123456 e", FlashbackClauseKind.AsOf, "E", "AS OF SCN 123456")]
    [InlineData("SELECT x.* FROM emp AS OF SCN :scn \"x\" ORDER BY 1", FlashbackClauseKind.AsOf, "x", "AS OF SCN :scn")]
    [InlineData("SELECT * FROM emp AS OF TIMESTAMP TO_TIMESTAMP('2026-11-13 09:00', 'YYYY-MM-DD HH24:MI') e", FlashbackClauseKind.AsOf, "E",
        "AS OF TIMESTAMP TO_TIMESTAMP('2026-11-13 09:00', 'YYYY-MM-DD HH24:MI')")]
    public void Single_table_flashback_query_is_a_past_values_result(string sql, FlashbackClauseKind kind, string? alias, string clause)
    {
        var shape = FlashbackResultAnalyzer.Analyze(sql);

        Assert.True(shape.IsPastValues, shape.Reason);
        Assert.Equal(kind, shape.Kind);
        Assert.Equal("EMP", shape.Table!.Name);
        Assert.Equal(alias, shape.Table.Alias);
        Assert.Equal(clause, shape.Table.FlashbackText);
    }

    [Theory]
    [InlineData("SELECT * FROM emp", FlashbackResultAnalyzer.NotFlashback)]
    [InlineData("SELECT * FROM (SELECT * FROM emp AS OF SCN 1)", FlashbackResultAnalyzer.NotFlashback)] // 인라인 뷰 안의 절은 바깥 표가 아니다
    [InlineData("WITH v AS (SELECT * FROM emp AS OF SCN 1) SELECT * FROM v", FlashbackResultAnalyzer.NotFlashback)]
    [InlineData("SELECT deptno, COUNT(*) FROM emp AS OF SCN 1 GROUP BY deptno", FlashbackResultAnalyzer.NotFlashback)]
    [InlineData("SELECT * FROM emp AS OF SCN 1 e JOIN dept d ON d.deptno = e.deptno", FlashbackResultAnalyzer.MultipleTables)]
    [InlineData("SELECT * FROM emp AS OF SCN 1 e, dept d", FlashbackResultAnalyzer.MultipleTables)]
    [InlineData("SELECT * FROM emp@remote AS OF SCN 1", FlashbackResultAnalyzer.Remote)]
    [InlineData("SELECT * FROM emp VERSIONS PERIOD FOR valid_time BETWEEN SYSDATE - 1 AND SYSDATE", FlashbackResultAnalyzer.Period)]
    [InlineData("SELECT * FROM emp AS OF PERIOD FOR valid_time SYSDATE", FlashbackResultAnalyzer.Period)]
    public void Other_results_explain_why(string sql, string reason)
    {
        var shape = FlashbackResultAnalyzer.Analyze(sql);

        Assert.False(shape.IsPastValues);
        Assert.Equal(reason, shape.Reason);
    }

    [Fact]
    public void Join_condition_ends_the_clause()
    {
        var shape = SelectAnalyzer.Analyze("SELECT * FROM emp e JOIN dept AS OF SCN 1 d ON d.deptno = e.deptno AND d.loc IS NULL");

        var dept = shape.Tables[1];
        Assert.Equal("D", dept.Alias);
        Assert.Equal("AS OF SCN 1", dept.FlashbackText);
    }

    [Fact]
    public void Alias_after_the_clause_qualifies_the_row_id_and_the_star()
    {
        var sql = "SELECT * FROM emp AS OF SCN 1 e WHERE e.empno = 1";
        var shape = SelectAnalyzer.Analyze(sql);

        Assert.Equal("e", Assert.Single(shape.Tables).Qualifier);
        Assert.Equal("SELECT e.*, e.ROWID FROM emp AS OF SCN 1 e WHERE e.empno = 1", SelectAnalyzer.AppendRowId(sql, shape));
    }
}
