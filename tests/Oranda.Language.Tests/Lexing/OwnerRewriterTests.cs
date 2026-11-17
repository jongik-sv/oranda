using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Lexing;

// 소유: te-m1b. 설계 9.3(소유자 붙이기)·9.4/TE-22(소유자 바꾸기)·TE-31.
public class OwnerRewriterTests
{
    static readonly Dictionary<string, string> Map = new() { ["SAMPLE"] = "TEST1" };

    // ---- Qualify ----

    [Theory]
    [InlineData("ALTER TABLE ORDERS ADD (MEMO VARCHAR2(20))", "ALTER TABLE SAMPLE.ORDERS ADD (MEMO VARCHAR2(20))")]
    [InlineData("alter table if exists orders add c number", "alter table if exists SAMPLE.orders add c number")]
    [InlineData("CREATE TABLE ORDERS (ID NUMBER)", "CREATE TABLE SAMPLE.ORDERS (ID NUMBER)")]
    [InlineData("CREATE OR REPLACE VIEW V_ORDERS AS SELECT * FROM DUAL", "CREATE OR REPLACE VIEW SAMPLE.V_ORDERS AS SELECT * FROM DUAL")]
    [InlineData("CREATE UNIQUE INDEX UX_O ON ORDERS (ID)", "CREATE UNIQUE INDEX SAMPLE.UX_O ON SAMPLE.ORDERS (ID)")]
    [InlineData("CREATE INDEX IX_O ON \"Mixed\" (ID)", "CREATE INDEX SAMPLE.IX_O ON SAMPLE.\"Mixed\" (ID)")]
    [InlineData("DROP TABLE ORDERS PURGE", "DROP TABLE SAMPLE.ORDERS PURGE")]
    [InlineData("DROP INDEX IX_O", "DROP INDEX SAMPLE.IX_O")]
    [InlineData("DROP TABLE IF EXISTS ORDERS", "DROP TABLE IF EXISTS SAMPLE.ORDERS")]
    [InlineData("TRUNCATE TABLE ORDERS", "TRUNCATE TABLE SAMPLE.ORDERS")]
    [InlineData("COMMENT ON TABLE ORDERS IS 'x'", "COMMENT ON TABLE SAMPLE.ORDERS IS 'x'")]
    [InlineData("COMMENT ON COLUMN ORDERS.C IS 'x'", "COMMENT ON COLUMN SAMPLE.ORDERS.C IS 'x'")]
    [InlineData("GRANT SELECT, UPDATE ON ORDERS TO APP_ROLE", "GRANT SELECT, UPDATE ON SAMPLE.ORDERS TO APP_ROLE")]
    [InlineData("REVOKE SELECT ON ORDERS FROM APP_ROLE", "REVOKE SELECT ON SAMPLE.ORDERS FROM APP_ROLE")]
    [InlineData("CREATE OR REPLACE TRIGGER TRG BEFORE INSERT ON ORDERS BEGIN NULL; END;", "CREATE OR REPLACE TRIGGER SAMPLE.TRG BEFORE INSERT ON SAMPLE.ORDERS BEGIN NULL; END;")]
    [InlineData("CREATE SEQUENCE SEQ_O", "CREATE SEQUENCE SAMPLE.SEQ_O")]
    [InlineData("CREATE OR REPLACE PACKAGE BODY PK AS END;", "CREATE OR REPLACE PACKAGE BODY SAMPLE.PK AS END;")]
    public void Qualify_adds_schema_to_unowned_targets(string sql, string expected)
    {
        var r = OwnerRewriter.Qualify(sql, "SAMPLE");

        Assert.Equal(expected, r.Sql);
        Assert.True(r.Changed);
        Assert.False(r.Uncertain);
    }

    [Fact]
    public void Qualify_adds_schema_to_references_target()
    {
        var r = OwnerRewriter.Qualify(
            "ALTER TABLE ORDER_ITEMS ADD CONSTRAINT FK_OI_01 FOREIGN KEY (CUST_ID) REFERENCES CUSTOMERS (ID)", "SAMPLE");

        Assert.Equal(
            "ALTER TABLE SAMPLE.ORDER_ITEMS ADD CONSTRAINT FK_OI_01 FOREIGN KEY (CUST_ID) REFERENCES SAMPLE.CUSTOMERS (ID)", r.Sql);
    }

    [Fact]
    public void Qualify_leaves_already_owned_names()
    {
        var r = OwnerRewriter.Qualify(
            "ALTER TABLE OTHER.ORDERS ADD CONSTRAINT FK_O FOREIGN KEY (C) REFERENCES OTHER.CUSTOMERS (ID)", "SAMPLE");

        Assert.False(r.Changed);
        Assert.False(r.Uncertain);
        Assert.Equal("ALTER TABLE OTHER.ORDERS ADD CONSTRAINT FK_O FOREIGN KEY (C) REFERENCES OTHER.CUSTOMERS (ID)", r.Sql);
    }

    [Fact]
    public void Qualify_mixes_owned_and_unowned_in_one_statement()
    {
        var r = OwnerRewriter.Qualify("ALTER TABLE ORDERS ADD CONSTRAINT FK_O FOREIGN KEY (C) REFERENCES HR.CUSTOMERS (ID)", "SAMPLE");

        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD CONSTRAINT FK_O FOREIGN KEY (C) REFERENCES HR.CUSTOMERS (ID)", r.Sql);
    }

    [Fact]
    public void Qualify_comment_column_with_owner_is_unchanged()
    {
        var r = OwnerRewriter.Qualify("COMMENT ON COLUMN SAMPLE.ORDERS.C IS 'x'", "SAMPLE");

        Assert.False(r.Changed);
        Assert.False(r.Uncertain);
    }

    [Fact]
    public void Qualify_does_not_touch_literals_or_comments()
    {
        var r = OwnerRewriter.Qualify("COMMENT ON TABLE ORDERS IS 'ALTER TABLE X'  -- DROP TABLE Y", "SAMPLE");

        Assert.Equal("COMMENT ON TABLE SAMPLE.ORDERS IS 'ALTER TABLE X'  -- DROP TABLE Y", r.Sql);
    }

    [Fact]
    public void Qualify_skips_leading_comment()
    {
        var r = OwnerRewriter.Qualify("/* why */ DROP TABLE ORDERS", "SAMPLE");

        Assert.Equal("/* why */ DROP TABLE SAMPLE.ORDERS", r.Sql);
    }

    [Fact]
    public void Qualify_quotes_schema_that_is_not_upper_plain()
    {
        var r = OwnerRewriter.Qualify("DROP TABLE ORDERS", "My Schema");

        Assert.Equal("DROP TABLE \"My Schema\".ORDERS", r.Sql);
    }

    [Theory]
    [InlineData("SELECT * FROM ORDERS")]
    [InlineData("INSERT INTO ORDERS VALUES (1)")]
    [InlineData("COMMIT")]
    [InlineData("")]
    [InlineData("-- only a comment")]
    public void Qualify_statement_without_target_is_unchanged_and_certain(string sql)
    {
        var r = OwnerRewriter.Qualify(sql, "SAMPLE");

        Assert.Equal(sql, r.Sql);
        Assert.False(r.Changed);
        Assert.False(r.Uncertain);
    }

    [Theory]
    [InlineData("CREATE USER APP IDENTIFIED BY x")]
    [InlineData("DROP PUBLIC SYNONYM SYN_O")]
    [InlineData("CREATE PUBLIC SYNONYM SYN_O FOR SAMPLE.ORDERS")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = SAMPLE")]
    [InlineData("GRANT CREATE SESSION TO APP")]
    [InlineData("GRANT READ ON DIRECTORY DATA_DIR TO APP")]
    [InlineData("DROP DATABASE LINK LNK")]
    public void Qualify_targets_without_owner_concept_are_left_alone(string sql)
    {
        var r = OwnerRewriter.Qualify(sql, "SAMPLE");

        Assert.Equal(sql, r.Sql);
        Assert.False(r.Changed);
        Assert.False(r.Uncertain);
    }

    [Theory]
    [InlineData("RENAME ORDERS TO ORDERS_OLD")]
    [InlineData("FLASHBACK TABLE ORDERS TO BEFORE DROP")]
    [InlineData("CREATE FROBNICATOR X")]
    [InlineData("DROP")]
    [InlineData("TRUNCATE")]
    [InlineData("CREATE INDEX IX_O")]
    public void Qualify_unknown_shape_returns_original_as_uncertain(string sql)
    {
        var r = OwnerRewriter.Qualify(sql, "SAMPLE");

        Assert.Equal(sql, r.Sql);
        Assert.False(r.Changed);
        Assert.True(r.Uncertain);
    }

    [Fact]
    public void Qualify_uncertain_part_discards_other_edits()
    {
        // 인덱스 이름은 고칠 수 있어도 ON 이 없으면 통째로 원문.
        var r = OwnerRewriter.Qualify("CREATE INDEX IX_O (ID)", "SAMPLE");

        Assert.False(r.Changed);
        Assert.True(r.Uncertain);
        Assert.Equal("CREATE INDEX IX_O (ID)", r.Sql);
    }

    [Fact]
    public void Qualify_blank_schema_throws()
    {
        Assert.Throws<ArgumentException>(() => OwnerRewriter.Qualify("DROP TABLE T", " "));
    }

    // ---- Rewrite ----

    [Fact]
    public void Rewrite_replaces_qualified_owner()
    {
        var r = OwnerRewriter.Rewrite("ALTER TABLE SAMPLE.ORDERS ADD (MEMO VARCHAR2(20))", Map);

        Assert.Equal("ALTER TABLE TEST1.ORDERS ADD (MEMO VARCHAR2(20))", r.Sql);
        Assert.True(r.Changed);
        Assert.False(r.Uncertain);
    }

    [Fact]
    public void Rewrite_keeps_literals_comments_and_hints()
    {
        var sql = "COMMENT ON TABLE SAMPLE.ORDERS IS 'SAMPLE.ORDERS' /* SAMPLE.X */ -- SAMPLE.Y\n/*+ INDEX(SAMPLE.ORDERS) */";
        var r = OwnerRewriter.Rewrite(sql, Map);

        Assert.Equal(
            "COMMENT ON TABLE TEST1.ORDERS IS 'SAMPLE.ORDERS' /* SAMPLE.X */ -- SAMPLE.Y\n/*+ INDEX(SAMPLE.ORDERS) */", r.Sql);
    }

    [Fact]
    public void Rewrite_keeps_q_quote_and_escaped_quote_literals()
    {
        var r = OwnerRewriter.Rewrite("COMMENT ON TABLE SAMPLE.ORDERS IS q'[it's SAMPLE.ORDERS]' || 'a''SAMPLE.B'", Map);

        Assert.Equal("COMMENT ON TABLE TEST1.ORDERS IS q'[it's SAMPLE.ORDERS]' || 'a''SAMPLE.B'", r.Sql);
    }

    [Fact]
    public void Rewrite_handles_quoted_names_and_keeps_quotes()
    {
        var r = OwnerRewriter.Rewrite("SELECT * FROM \"SAMPLE\".\"ORDERS\"", Map);

        Assert.Equal("SELECT * FROM \"TEST1\".\"ORDERS\"", r.Sql);
    }

    [Fact]
    public void Rewrite_is_case_insensitive_for_unquoted_owner()
    {
        var r = OwnerRewriter.Rewrite("select * from sample.orders", Map);

        Assert.Equal("select * from TEST1.orders", r.Sql);
    }

    [Fact]
    public void Rewrite_quoted_owner_with_other_case_still_matches_ignoring_case()
    {
        var r = OwnerRewriter.Rewrite("DROP TABLE \"sample\".T", Map);

        Assert.Equal("DROP TABLE \"TEST1\".T", r.Sql);
    }

    [Fact]
    public void Rewrite_comment_on_column()
    {
        var r = OwnerRewriter.Rewrite("COMMENT ON COLUMN SAMPLE.ORDERS.C IS 'x'", Map);

        Assert.Equal("COMMENT ON COLUMN TEST1.ORDERS.C IS 'x'", r.Sql);
    }

    [Fact]
    public void Rewrite_references_and_index_target()
    {
        var r = OwnerRewriter.Rewrite(
            "ALTER TABLE SAMPLE.ORDER_ITEMS ADD CONSTRAINT FK_OI_01 FOREIGN KEY (C) REFERENCES SAMPLE.CUSTOMERS (ID)", Map);

        Assert.Equal(
            "ALTER TABLE TEST1.ORDER_ITEMS ADD CONSTRAINT FK_OI_01 FOREIGN KEY (C) REFERENCES TEST1.CUSTOMERS (ID)", r.Sql);

        var ix = OwnerRewriter.Rewrite("CREATE INDEX SAMPLE.IX_O ON SAMPLE.ORDERS (ID)", Map);
        Assert.Equal("CREATE INDEX TEST1.IX_O ON TEST1.ORDERS (ID)", ix.Sql);
    }

    [Fact]
    public void Rewrite_requires_a_name_after_the_dot()
    {
        // 소유자 이름이 단독으로 쓰였거나 점 뒤가 이름이 아니면 건드리지 않는다.
        var r = OwnerRewriter.Rewrite("GRANT SELECT ON T TO SAMPLE; SELECT SAMPLE FROM DUAL; SELECT SAMPLE.* FROM DUAL", Map);

        Assert.False(r.Changed);
    }

    [Fact]
    public void Rewrite_skips_middle_part_of_longer_path_and_db_link()
    {
        var r = OwnerRewriter.Rewrite("SELECT * FROM HR.SAMPLE.ORDERS; SELECT * FROM T@SAMPLE.WORLD", Map);

        Assert.False(r.Changed);
    }

    [Fact]
    public void Rewrite_does_not_touch_longer_names_with_same_prefix()
    {
        var r = OwnerRewriter.Rewrite("DROP TABLE SAMPLE2.ORDERS; DROP TABLE MY_SAMPLE.ORDERS", Map);

        Assert.False(r.Changed);
    }

    [Fact]
    public void Rewrite_replaces_every_occurrence()
    {
        var r = OwnerRewriter.Rewrite("CREATE VIEW SAMPLE.V AS SELECT * FROM SAMPLE.A JOIN SAMPLE.B ON 1=1", Map);

        Assert.Equal("CREATE VIEW TEST1.V AS SELECT * FROM TEST1.A JOIN TEST1.B ON 1=1", r.Sql);
    }

    [Fact]
    public void Rewrite_supports_several_owners_in_map()
    {
        var map = new Dictionary<string, string> { ["SAMPLE"] = "TEST1", ["HR"] = "HR_T" };
        var r = OwnerRewriter.Rewrite("ALTER TABLE SAMPLE.A ADD CONSTRAINT F FOREIGN KEY (C) REFERENCES HR.B (ID)", map);

        Assert.Equal("ALTER TABLE TEST1.A ADD CONSTRAINT F FOREIGN KEY (C) REFERENCES HR_T.B (ID)", r.Sql);
    }

    [Fact]
    public void Rewrite_target_needing_quotes_is_quoted()
    {
        var r = OwnerRewriter.Rewrite("DROP TABLE SAMPLE.T", new Dictionary<string, string> { ["SAMPLE"] = "Test Owner" });

        Assert.Equal("DROP TABLE \"Test Owner\".T", r.Sql);
    }

    [Fact]
    public void Rewrite_same_name_mapping_changes_nothing()
    {
        var r = OwnerRewriter.Rewrite("DROP TABLE SAMPLE.T", new Dictionary<string, string> { ["SAMPLE"] = "SAMPLE" });

        Assert.False(r.Changed);
        Assert.Equal("DROP TABLE SAMPLE.T", r.Sql);
    }

    [Fact]
    public void Rewrite_empty_map_and_no_owner_statements_are_unchanged()
    {
        Assert.False(OwnerRewriter.Rewrite("DROP TABLE SAMPLE.T", new Dictionary<string, string>()).Changed);
        Assert.False(OwnerRewriter.Rewrite("DROP TABLE T", Map).Changed);
        Assert.False(OwnerRewriter.Rewrite("COMMIT", Map).Changed);
        Assert.False(OwnerRewriter.Rewrite("", Map).Changed);
    }

    [Fact]
    public void Rewrite_multiline_statement_keeps_layout()
    {
        var r = OwnerRewriter.Rewrite("CREATE TABLE SAMPLE.T (\r\n  ID NUMBER REFERENCES SAMPLE.P (ID)\r\n)", Map);

        Assert.Equal("CREATE TABLE TEST1.T (\r\n  ID NUMBER REFERENCES TEST1.P (ID)\r\n)", r.Sql);
    }

    [Fact]
    public void Rewrite_never_reports_uncertain()
    {
        Assert.False(OwnerRewriter.Rewrite("garbage ''' SAMPLE.X", Map).Uncertain);
    }
}
