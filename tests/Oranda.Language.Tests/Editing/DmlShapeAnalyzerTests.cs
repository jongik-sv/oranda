using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Language.Editing;

namespace Oranda.Language.Tests.Editing;

// 소유: RB-30. 쓰기 문장 모양(설계 13.2·16.1).
public class DmlShapeAnalyzerTests
{
    private static readonly DmlShapeAnalyzer A = new();

    [Fact]
    public void Update_with_schema_alias_and_where()
    {
        var s = A.Analyze("update scott.emp e set e.sal = :s, comm = 0 where e.deptno = :d")!;
        Assert.Equal(DmlVerb.Update, s.Verb);
        Assert.Equal(("SCOTT", "EMP", "E"), (s.Owner, s.Name, s.Alias));
        Assert.Equal("scott.emp e", s.TableText);
        Assert.Equal(["SAL", "COMM"], s.SetColumns);
        Assert.Equal("e.deptno = :d", s.WhereText);
        Assert.Null(s.NotTarget);
    }

    [Fact]
    public void Update_quoted_name_partition_and_tuple_set()
    {
        var s = A.Analyze("UPDATE \"Sales\".\"My Tab\" PARTITION (p1) t SET (a, \"b\") = (SELECT 1, 2 FROM dual WHERE 1 = 1), c = 3")!;
        Assert.Equal(("Sales", "My Tab", "T"), (s.Owner, s.Name, s.Alias));
        Assert.Equal("\"Sales\".\"My Tab\" PARTITION (p1) t", s.TableText);
        Assert.Equal(["A", "b", "C"], s.SetColumns);
        Assert.Null(s.WhereText); // 서브쿼리 안의 WHERE 는 최상위가 아니다
    }

    [Fact]
    public void Where_in_comment_string_and_subquery_is_ignored()
    {
        var s = A.Analyze("DELETE emp /* where x */ WHERE ename = 'a where b' AND deptno IN (SELECT deptno FROM dept WHERE loc = 'X') -- where")!;
        Assert.Equal(DmlVerb.Delete, s.Verb);
        Assert.Equal("EMP", s.Name);
        Assert.Null(s.Alias);
        Assert.Equal("ename = 'a where b' AND deptno IN (SELECT deptno FROM dept WHERE loc = 'X')", s.WhereText);
    }

    [Fact]
    public void Delete_from_without_where()
    {
        var s = A.Analyze("DELETE FROM emp;")!;
        Assert.Equal(("EMP", (string?)null), (s.Name, s.WhereText));
    }

    [Theory]
    [InlineData("INSERT INTO emp (empno, ename) VALUES (1, 'A')", new[] { "EMPNO", "ENAME" })]
    [InlineData("INSERT INTO emp VALUES (1, 'A')", new string[0])]
    public void Insert_values_single_row(string sql, string[] cols)
    {
        var s = A.Analyze(sql)!;
        Assert.Equal(DmlVerb.InsertValues, s.Verb);
        Assert.Equal(cols, s.InsertColumns);
        Assert.Null(s.NotTarget);
    }

    [Fact]
    public void Insert_select_is_target_shape_with_select_verb()
    {
        var s = A.Analyze("INSERT INTO emp (empno) SELECT empno FROM emp2 WHERE 1 = 1")!;
        Assert.Equal(DmlVerb.InsertSelect, s.Verb);
        Assert.Equal(["EMPNO"], s.InsertColumns);
        Assert.Null(s.WhereText);
        Assert.Null(s.NotTarget); // 불완전 판단은 엔진 몫(RD-1)
    }

    [Theory]
    [InlineData("INSERT ALL INTO a VALUES (1) INTO b VALUES (2) SELECT * FROM dual", DmlVerb.InsertSelect, DmlShapeAnalyzer.NotTargetMultiTable)]
    [InlineData("INSERT INTO a (x) VALUES (1), (2)", DmlVerb.InsertValues, DmlShapeAnalyzer.NotTargetMultiRow)]
    [InlineData("MERGE INTO emp e USING src s ON (e.id = s.id) WHEN MATCHED THEN UPDATE SET e.x = s.x", DmlVerb.Merge, DmlShapeAnalyzer.NotTargetMerge)]
    [InlineData("UPDATE emp@remote.world SET x = 1", DmlVerb.Update, DmlShapeAnalyzer.NotTargetDbLink)]
    [InlineData("UPDATE (SELECT * FROM emp) v SET v.x = 1", DmlVerb.Update, DmlShapeAnalyzer.NotTargetInlineView)]
    [InlineData("UPDATE emp SET x = 1 WHERE id = 2 RETURNING x INTO :o", DmlVerb.Update, DmlShapeAnalyzer.NotTargetReturning)]
    [InlineData("DELETE emp WHERE id = 2 LOG ERRORS INTO err$ REJECT LIMIT 10", DmlVerb.Delete, DmlShapeAnalyzer.NotTargetLogErrors)]
    [InlineData("BEGIN UPDATE emp SET x = 1; END;", DmlVerb.PlSql, DmlShapeAnalyzer.NotTargetPlSql)]
    public void Not_target_shapes_carry_reason(string sql, DmlVerb verb, string reason)
    {
        var s = A.Analyze(sql)!;
        Assert.Equal(verb, s.Verb);
        Assert.Equal(reason, s.NotTarget);
    }

    [Fact]
    public void Db_link_and_merge_keep_table_name()
    {
        Assert.Equal(("EMP", "REMOTE.WORLD"), (A.Analyze("UPDATE emp@remote.world SET x = 1")!.Name, A.Analyze("UPDATE emp@remote.world SET x = 1")!.DbLink));
        Assert.Equal("EMP", A.Analyze("MERGE INTO scott.emp e USING s ON (1 = 1) WHEN MATCHED THEN UPDATE SET e.x = 1")!.Name);
    }

    [Fact]
    public void Where_stops_before_existing_returning()
    {
        var s = A.Analyze("DELETE emp WHERE id = :i RETURNING ename INTO :n")!;
        Assert.Equal("id = :i", s.WhereText);
    }

    [Theory]
    [InlineData("SELECT * FROM emp WHERE 1 = 1")]
    [InlineData("CREATE TABLE t (x NUMBER)")]
    [InlineData("COMMIT")]
    [InlineData("LOCK TABLE emp IN EXCLUSIVE MODE")]
    [InlineData("")]
    [InlineData("UPDATE")]
    public void Non_write_or_broken_text_does_not_throw(string sql)
    {
        var s = A.Analyze(sql);
        Assert.True(s is null || s.NotTarget is not null);
    }

    [Fact]
    public void Hint_as_alias_partition_and_outer_where_only()
    {
        var s = A.Analyze("DELETE /*+ index(t) */ FROM emp PARTITION (p1) AS t WHERE t.id IN (SELECT id FROM x WHERE y = 1) AND t.z = 2;\n/")!;
        Assert.Equal(("EMP", "T"), (s.Name, s.Alias));
        Assert.Equal("t.id IN (SELECT id FROM x WHERE y = 1) AND t.z = 2", s.WhereText);

        var u = A.Analyze("UPDATE emp SET a = (SELECT max(b) FROM x WHERE x.k = emp.k) WHERE id = 1")!;
        Assert.Equal(["A"], u.SetColumns);
        Assert.Equal("id = 1", u.WhereText);
    }

    [Theory]
    [InlineData("INSERT INTO emp (SELECT * FROM emp2)")]
    [InlineData("INSERT INTO emp WITH w AS (SELECT 1 x FROM dual) SELECT x FROM w")]
    public void Insert_subquery_forms_are_insert_select(string sql)
    {
        var s = A.Analyze(sql)!;
        Assert.Equal((DmlVerb.InsertSelect, "EMP"), (s.Verb, s.Name));
        Assert.Empty(s.InsertColumns);
    }

    [Theory]
    [InlineData("<<lbl>> BEGIN NULL; END;", DmlVerb.PlSql, DmlShapeAnalyzer.NotTargetPlSql)]
    [InlineData("EXEC proc(1)", DmlVerb.PlSql, DmlShapeAnalyzer.NotTargetPlSql)]
    [InlineData("DELETE FROM TABLE(SELECT c FROM t WHERE id = 1) WHERE x = 1", DmlVerb.Delete, DmlShapeAnalyzer.NotTargetInlineView)]
    [InlineData("UPDATE t SET obj.attr = 1", DmlVerb.Update, DmlShapeAnalyzer.NotTargetUnknown)]
    public void More_not_target_shapes(string sql, DmlVerb verb, string reason)
    {
        var s = A.Analyze(sql)!;
        Assert.Equal((verb, reason), (s.Verb, s.NotTarget));
    }

    [Theory]
    [InlineData("UPDATE t SET (")]
    [InlineData("UPDATE t SET a = (1 WHERE (")]
    [InlineData("DELETE t WHERE a = 'x")]
    [InlineData("UPDATE \"EMP SET x = 1")]
    [InlineData(")")]
    public void Malformed_input_returns_without_hanging(string sql)
    {
        var s = A.Analyze(sql);
        Assert.True(s is null || s.Verb != DmlVerb.InsertValues);
    }

    [Fact]
    public void RowDmlBuilder_fills_statement_shape()
    {
        var emp = new DbObjectRef("SCOTT", "EMP", "TABLE");
        var update = RowDmlBuilder.Build(new RowChange(RowChangeKind.Update, 0, emp)
        {
            Key = new Dictionary<string, object?> { ["EMPNO"] = 1m },
            NewValues = new Dictionary<string, object?> { ["SAL"] = 10m },
        })!;
        var shape = update.Statement.Dml!;
        Assert.Equal((DmlVerb.Update, "SCOTT", "EMP"), (shape.Verb, shape.Owner, shape.Name));
        Assert.Equal(["SAL"], shape.SetColumns);
        Assert.Equal("EMPNO = :k1", shape.WhereText);

        var insert = RowDmlBuilder.Build(new RowChange(RowChangeKind.Insert, 0, emp)
        {
            NewValues = new Dictionary<string, object?> { ["EMPNO"] = 2m },
        })!;
        Assert.Equal(DmlVerb.InsertValues, insert.Statement.Dml!.Verb);
        Assert.Equal(["EMPNO"], insert.Statement.Dml.InsertColumns);
    }
}
