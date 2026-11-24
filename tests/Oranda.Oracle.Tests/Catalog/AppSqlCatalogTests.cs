using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: Tibero T1 O6. App 안에 있던 인라인 Oracle SQL 을 카탈로그로 옮겼다. 옮기기 전 글과 한 글자도 다르지 않아야 한다.
[Trait("Category", "Catalog")]
public class AppSqlCatalogTests
{
    [Fact]
    public void Trace_statements_keep_their_text()
    {
        Assert.Equal("ALTER SESSION SET EVENTS '10053 trace name context forever, level 1'", EmbeddedCatalog.Text("app.trace_event_on"));
        Assert.Equal("ALTER SESSION SET EVENTS '10053 trace name context off'", EmbeddedCatalog.Text("app.trace_event_off"));
        Assert.Equal(
            "DECLARE\n  c INTEGER := DBMS_SQL.OPEN_CURSOR;\nBEGIN\n  DBMS_SQL.PARSE(c, :oranda_sql, DBMS_SQL.NATIVE);\n  DBMS_SQL.CLOSE_CURSOR(c);\n"
            + "EXCEPTION\n  WHEN OTHERS THEN\n    IF DBMS_SQL.IS_OPEN(c) THEN\n      DBMS_SQL.CLOSE_CURSOR(c);\n    END IF;\n    RAISE;\nEND;",
            EmbeddedCatalog.Text("app.trace_parse").ReplaceLineEndings("\n"));
        Assert.Equal("ALTER SESSION SET STATISTICS_LEVEL = ALL", EmbeddedCatalog.Text("app.statistics_level"));
    }

    [Fact]
    public void Session_statements_fill_their_placeholders()
    {
        Assert.Equal("ALTER SYSTEM KILL SESSION '7,8,@1' IMMEDIATE", EmbeddedCatalog.Text("app.kill_session", ("id", "7,8,@1")));
        Assert.Equal("ALTER SYSTEM DISCONNECT SESSION '12,345' POST_TRANSACTION",
            EmbeddedCatalog.Text("app.disconnect_session", ("id", "12,345"), ("mode", "POST_TRANSACTION")));
        Assert.Equal("GRANT SELECT ON sys.dba_ddl_locks TO SCOTT;", EmbeddedCatalog.Text("app.ddl_locks_grant", ("user", "SCOTT")));
        Assert.Equal("SELECT * FROM \"HR\".\"T\" WHERE ROWID = 'AAAB'",
            EmbeddedCatalog.Text("app.locked_row_select", ("table", "\"HR\".\"T\""), ("rowid", "AAAB")));
    }

    [Fact]
    public void Placeholder_values_are_not_expanded_again()
    {
        var sql = EmbeddedCatalog.Text("meta.object_users", ("owner", "@NAME@"), ("name", "X"));
        Assert.Contains("owner = '@NAME@'", sql, StringComparison.Ordinal);
        Assert.Contains("object = 'X'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Object_users_counts_sessions_of_every_rac_instance_but_mine()
    {
        var query = EmbeddedCatalog.Load().Get("meta.object_users", 19);

        Assert.Equal(Oranda.Core.Catalog.CatalogRac.Gv, query.Header.Rac);
        Assert.Contains("gv$access", query.Header.Requires);
        Assert.Contains("FROM gv$access", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" v$access", query.Sql, StringComparison.Ordinal);
        // 같은 SID 라도 다른 인스턴스의 세션은 남이다: 내 세션은 인스턴스와 SID 가 둘 다 같을 때만 뺀다.
        Assert.Contains("NOT (inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AND sid = TO_NUMBER(SYS_CONTEXT('USERENV', 'SID')))", query.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT inst_id, sid", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Object_users_local_fallback_keeps_the_v_access_statement()
    {
        // GV_$ACCESS 없이 V_$ACCESS 만 받은 계정이 쓰는 대체 문장: 예전 글 그대로.
        Assert.Equal("SELECT COUNT(DISTINCT sid) FROM v$access WHERE owner = 'SCOTT' AND object = 'PK' AND sid <> TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))",
            EmbeddedCatalog.Text("meta.object_users_local", ("owner", "SCOTT"), ("name", "PK")));
        Assert.Equal(Oranda.Core.Catalog.CatalogRac.V, EmbeddedCatalog.Load().Get("meta.object_users_local", 19).Header.Rac);
    }

    [Fact]
    public void Foreign_key_index_check_has_optional_owner_line()
    {
        var withOwner = EmbeddedCatalog.Text("app.fk_index_check", ("owner_filter", "   AND c.owner = 'HR'\n"), ("table", "'T'"));
        var without = EmbeddedCatalog.Text("app.fk_index_check", ("owner_filter", string.Empty), ("table", "'T'"));
        Assert.Contains(" WHERE c.constraint_type = 'R'\n   AND c.owner = 'HR'\n   AND c.table_name = 'T'\n", withOwner.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains(" WHERE c.constraint_type = 'R'\n   AND c.table_name = 'T'\n", without.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Flashback_versions_template()
    {
        var sql = EmbeddedCatalog.Text("dbinfo.flashback_versions",
            ("columns", "\"A\""), ("table", "\"HR\".\"T\""), ("range", "SCN MINVALUE"), ("conditions", "\"A\" = 1")).ReplaceLineEndings("\n");
        Assert.Equal(
            "SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, \"A\"\n"
            + "FROM \"HR\".\"T\" VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE\nWHERE \"A\" = 1\nORDER BY VERSIONS_STARTSCN DESC NULLS LAST",
            sql);
    }
}
