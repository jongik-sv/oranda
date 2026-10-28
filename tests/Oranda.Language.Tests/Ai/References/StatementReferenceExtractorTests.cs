using Oranda.Core.Metadata;
using Oranda.Language.Ai.References;

namespace Oranda.Language.Tests.Ai.References;

public class StatementReferenceExtractorTests
{
    private static IEnumerable<string> Names(StatementReferences r) => r.Objects.Select(o => o.Display);

    private static ColumnUse Col(StatementReferences r, string table, string column) =>
        Assert.Single(r.Columns, c => c.Column == column && (c.Table?.Display == table || c.Candidates.Any(x => x.Display == table)));

    [Fact]
    public void Join_with_aliases_attributes_columns_by_clause()
    {
        var r = StatementReferenceExtractor.Extract("""
            SELECT o.order_id, d.dept_nm
              FROM app.tb_order o
              JOIN tb_dept d ON d.dept_id = o.dept_id
             WHERE o.stat_cd = :1
             GROUP BY d.dept_nm
             ORDER BY o.order_dt DESC
            """);

        Assert.Equal(["APP.TB_ORDER", "TB_DEPT"], Names(r));
        Assert.Equal(ColumnClause.Select, Col(r, "APP.TB_ORDER", "ORDER_ID").Clauses);
        Assert.Equal(ColumnClause.Join, Col(r, "TB_DEPT", "DEPT_ID").Clauses);
        Assert.Equal(ColumnClause.Join, Col(r, "APP.TB_ORDER", "DEPT_ID").Clauses);
        Assert.Equal(ColumnClause.Where, Col(r, "APP.TB_ORDER", "STAT_CD").Clauses);
        Assert.Equal(ColumnClause.Select | ColumnClause.Group, Col(r, "TB_DEPT", "DEPT_NM").Clauses);
        Assert.Equal(ColumnClause.Order, Col(r, "APP.TB_ORDER", "ORDER_DT").Clauses);
    }

    [Fact]
    public void Unqualified_column_with_one_table_is_attributed_and_with_two_is_candidate()
    {
        var one = StatementReferenceExtractor.Extract("SELECT stat_cd FROM tb_order WHERE order_dt > SYSDATE - 1");
        Assert.Equal("TB_ORDER", Col(one, "TB_ORDER", "STAT_CD").Table?.Display);
        Assert.DoesNotContain(one.Columns, c => c.Column == "SYSDATE");

        var two = StatementReferenceExtractor.Extract("SELECT stat_cd FROM tb_order a, tb_dept b WHERE a.dept_id = b.dept_id");
        var stat = Assert.Single(two.Columns, c => c.Column == "STAT_CD");
        Assert.Null(stat.Table);
        Assert.Equal(2, stat.Candidates.Count);
        Assert.Contains("STAT_CD", two.ColumnsOf(two.Objects[0]));
    }

    [Fact]
    public void With_clause_names_are_not_objects_but_their_bodies_are_scanned()
    {
        var r = StatementReferenceExtractor.Extract("""
            WITH recent AS (SELECT cust_id FROM tb_order WHERE order_dt > SYSDATE - 7)
            SELECT c.cust_nm FROM tb_cust c JOIN recent r ON r.cust_id = c.cust_id
            """);

        Assert.Equal(["TB_ORDER", "TB_CUST"], Names(r));
        Assert.Equal(ColumnClause.Select, Col(r, "TB_ORDER", "CUST_ID").Clauses);
        Assert.Equal(ColumnClause.Join, Col(r, "TB_CUST", "CUST_ID").Clauses);
    }

    [Fact]
    public void Inline_view_and_scalar_subquery_and_union_are_scanned()
    {
        var r = StatementReferenceExtractor.Extract("""
            SELECT v.n, (SELECT MAX(h.amt) FROM tb_hist h WHERE h.cust_id = v.cust_id) mx
              FROM (SELECT cust_id, COUNT(*) n FROM tb_order GROUP BY cust_id) v
            UNION ALL
            SELECT 1, 2 FROM tb_other x WHERE x.k = 1
            """);

        Assert.Equal(["TB_HIST", "TB_ORDER", "TB_OTHER"], Names(r).Order().ToList());
        Assert.Equal(ColumnClause.Where, Col(r, "TB_HIST", "CUST_ID").Clauses);
        Assert.Equal(ColumnClause.Select | ColumnClause.Group, Col(r, "TB_ORDER", "CUST_ID").Clauses);
        Assert.Equal(ColumnClause.Where, Col(r, "TB_OTHER", "K").Clauses);
        Assert.DoesNotContain(r.Columns, c => c.Column is "N" or "MX"); // 별칭은 열이 아니다
    }

    [Fact]
    public void Function_calls_extract_and_cast_do_not_produce_columns_and_outer_join_marker_is_skipped()
    {
        var r = StatementReferenceExtractor.Extract("""
            SELECT NVL(a.x, 0), EXTRACT(YEAR FROM a.dt), CAST(a.y AS NUMBER), pkg.fn(a.z)
              FROM t1 a, t2 b WHERE a.id = b.id(+) AND seq.NEXTVAL > 0
            """);

        Assert.Equal(["T1", "T2"], Names(r));
        Assert.Equal(["DT", "ID", "X", "Y", "Z"], r.Columns.Select(c => c.Column).Distinct().Order().ToList());
        Assert.Equal(ColumnClause.Where, Col(r, "T2", "ID").Clauses);
    }

    [Fact]
    public void Dblink_object_is_listed_as_remote_and_dml_targets_are_found()
    {
        var r = StatementReferenceExtractor.Extract("SELECT * FROM tb_a@lnk a WHERE a.x = 1");
        Assert.True(Assert.Single(r.Objects).IsRemote);

        var upd = StatementReferenceExtractor.Extract("UPDATE tb_order SET stat_cd = 'X' WHERE order_id = 1");
        Assert.Equal(["TB_ORDER"], Names(upd));
        Assert.Equal(ColumnClause.Other, Col(upd, "TB_ORDER", "STAT_CD").Clauses);

        var merge = StatementReferenceExtractor.Extract("MERGE INTO tb_t t USING tb_s s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = s.v");
        Assert.Equal(["TB_S", "TB_T"], Names(merge).Order().ToList());
    }

    [Theory]
    [InlineData("SELECT ROWNUM rn, a.x FROM tb_a a", "RN")]
    [InlineData("SELECT SYSDATE dt FROM dual", "DT")]
    [InlineData("SELECT LISTAGG(a.nm, ',') WITHIN GROUP (ORDER BY a.nm) nms, a.dept FROM tb_a a GROUP BY a.dept", "NMS")]
    [InlineData("SELECT a.cd code FROM tb_a a ORDER BY code", "CODE")]
    [InlineData("SELECT a.x FROM tb_a a ORDER BY a.x OFFSET 10 ROWS FETCH NEXT 10 ROWS ONLY", "NEXT")]
    [InlineData("SELECT a.x FROM tb_a a FOR UPDATE SKIP LOCKED", "LOCKED")]
    [InlineData("SELECT a.x INTO v_x FROM tb_a a WHERE a.id = 1", "V_X")]
    [InlineData("SELECT a.x FROM tb_a SAMPLE (10) a", "A")]
    [InlineData("SELECT a.x FROM tb_a a, TABLE(pkg.fn(1)) t WHERE t.column_value = a.z", "COLUMN_VALUE")]
    public void Aliases_and_clause_words_are_not_columns(string sql, string notAColumn)
    {
        var r = StatementReferenceExtractor.Extract(sql);

        Assert.DoesNotContain(r.Columns, c => c.Column == notAColumn);
        Assert.NotEmpty(r.Objects);
    }

    [Fact]
    public void Within_group_does_not_switch_clause_and_for_update_comma_is_not_a_table()
    {
        var r = StatementReferenceExtractor.Extract(
            "SELECT LISTAGG(a.nm, ',') WITHIN GROUP (ORDER BY a.nm) nms, a.dept FROM tb_a a GROUP BY a.dept");
        Assert.Equal(ColumnClause.Select, Col(r, "TB_A", "NM").Clauses);
        Assert.Equal(ColumnClause.Select | ColumnClause.Group, Col(r, "TB_A", "DEPT").Clauses);

        var u = StatementReferenceExtractor.Extract("SELECT * FROM tb_a a, tb_b b FOR UPDATE OF a.c1, b.c2");
        Assert.Equal(["TB_A", "TB_B"], Names(u));
    }

    [Fact]
    public void Dblink_alias_is_not_swallowed_and_insert_select_splits_target_from_source()
    {
        var link = StatementReferenceExtractor.Extract("SELECT a.x FROM tb_a@lnk a WHERE a.x = 1");
        Assert.Equal("LNK", Assert.Single(link.Objects).DbLink);
        Assert.Contains(link.Columns, c => c.Column == "X");

        var ins = StatementReferenceExtractor.Extract("INSERT INTO tb_x (a, b) SELECT c, d FROM tb_y");
        Assert.Equal(["TB_X", "TB_Y"], Names(ins).Order().ToList());
        Assert.Equal("TB_X", ins.Columns.Single(c => c.Column == "A").Table?.Display);
        Assert.Equal("TB_Y", ins.Columns.Single(c => c.Column == "C").Table?.Display);

        Assert.Equal(["TB_X"], Names(StatementReferenceExtractor.Extract("DELETE tb_x WHERE id = 1")));
    }

    [Fact]
    public void Unqualified_column_next_to_with_name_stays_a_candidate()
    {
        var r = StatementReferenceExtractor.Extract("""
            WITH r AS (SELECT cust_id, amt FROM tb_order)
            SELECT c.cust_nm, amt FROM tb_cust c JOIN r ON r.cust_id = c.cust_id
            """);

        Assert.DoesNotContain(r.Columns, c => c.Column == "AMT" && c.Table?.Display == "TB_CUST");
        Assert.Contains(r.Columns, c => c.Column == "AMT" && c.Table is null && c.Candidates.Any(x => x.Display == "TB_CUST"));
    }

    [Fact]
    public async Task Resolve_follows_public_synonym_and_reports_db_link()
    {
        var r = StatementReferenceExtractor.Extract("SELECT 1 FROM syn_order o, tb_a@lnk a WHERE o.x = 1");
        Task<NameStep?> Step(string owner, string name) => Task.FromResult<NameStep?>((owner, name) switch
        {
            ("PUBLIC", "SYN_ORDER") => NameStep.Synonym("APP", "TB_ORDER"),
            ("APP", "TB_ORDER") => NameStep.TableOrView,
            _ => null,
        });

        var resolved = await StatementReferenceExtractor.ResolveAsync(r, "SCOTT", Step);

        Assert.Equal(new DbObjectRef("APP", "TB_ORDER", "TABLE"), resolved[0].Target);
        Assert.Null(resolved[1].Target);
        Assert.Contains("DB 링크", resolved[1].Problem, StringComparison.Ordinal);
    }
}
