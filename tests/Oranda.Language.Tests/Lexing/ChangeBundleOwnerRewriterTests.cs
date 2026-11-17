using Oranda.Core.ChangeBundle;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Lexing;

// 소유: te-bundle(T5). 반영 묶음 쪽 IOwnerRewriter: 글 고치기는 OwnerRewriter(te-m1b) 에 맡기고, 대상 소유자·이름과 CURRENT_SCHEMA 전환을 읽는다.
public sealed class ChangeBundleOwnerRewriterTests
{
    private readonly ChangeBundleOwnerRewriter _rewriter = new();

    [Theory]
    [InlineData("CREATE TABLE ORDERS (ID NUMBER)", "SAMPLE", "ORDERS")]
    [InlineData("create global temporary table tmp_x (id number) on commit preserve rows", "SAMPLE", "TMP_X")]
    [InlineData("ALTER TABLE orders ADD (note VARCHAR2(100))", "SAMPLE", "ORDERS")]
    [InlineData("DROP INDEX ix_orders_01", "SAMPLE", "IX_ORDERS_01")]
    [InlineData("TRUNCATE TABLE orders", "SAMPLE", "ORDERS")]
    [InlineData("CREATE OR REPLACE FORCE VIEW v_orders AS SELECT * FROM orders", "SAMPLE", "V_ORDERS")]
    [InlineData("CREATE UNIQUE INDEX ux_orders_01 ON orders (code DESC)", "SAMPLE", "UX_ORDERS_01")]
    [InlineData("ALTER INDEX ix_a RENAME TO ix_b", "SAMPLE", "IX_A")]
    [InlineData("COMMENT ON TABLE orders IS 'x'", "SAMPLE", "ORDERS")]
    [InlineData("COMMENT ON COLUMN orders.note IS 'x'", "SAMPLE", "ORDERS")]
    [InlineData("GRANT SELECT, INSERT ON orders TO app_user", "SAMPLE", "ORDERS")]
    [InlineData("ALTER TABLE \"Other\".\"Orders\" ADD (x NUMBER)", "Other", "Orders")]
    [InlineData("COMMENT ON COLUMN other.orders.note IS 'x'", "OTHER", "ORDERS")]
    [InlineData("/* orders */ ALTER TABLE -- other.x\n orders ADD (note VARCHAR2(10) DEFAULT 'OTHER.X')", "SAMPLE", "ORDERS")]
    public void Target_owner_and_name_are_read_after_qualifying(string text, string owner, string name)
    {
        var r = _rewriter.Qualify(text, "SAMPLE");
        Assert.True(r.Understood);
        Assert.Equal(owner, r.TargetOwner);
        Assert.Equal(name, r.TargetName);
        Assert.Equal(OwnerRewriter.Qualify(text, "SAMPLE").Sql, r.Text); // 글은 OwnerRewriter 결과 그대로
        Assert.Equal(OwnerRewriter.Qualify(text, "SAMPLE").Changed, r.Changed);
    }

    [Fact]
    public void Qualified_text_carries_the_schema()
    {
        var r = _rewriter.Qualify("ALTER TABLE orders ADD (note VARCHAR2(100))", "SAMPLE");
        Assert.Equal("ALTER TABLE SAMPLE.orders ADD (note VARCHAR2(100))", r.Text);
        Assert.True(r.Changed);
    }

    [Fact]
    public void Public_synonym_belongs_to_public()
    {
        var r = _rewriter.Qualify("CREATE PUBLIC SYNONYM orders FOR SAMPLE.orders", "SAMPLE");
        Assert.True(r.Understood);
        Assert.Equal("PUBLIC", r.TargetOwner);
        Assert.Equal("ORDERS", r.TargetName);
    }

    [Fact]
    public void Uncertain_shape_returns_the_original_and_no_target()
    {
        const string text = "RENAME orders TO orders_old";
        Assert.True(OwnerRewriter.Qualify(text, "SAMPLE").Uncertain);
        Assert.Equal(new OwnerQualifyResult(text, false, false, null, null), _rewriter.Qualify(text, "SAMPLE"));
    }

    [Theory]
    [InlineData("CREATE USER app_user IDENTIFIED BY x")] // 소유자 개념이 없는 대상
    [InlineData("GRANT CREATE TABLE TO app_user")]
    [InlineData("ALTER SYSTEM FLUSH SHARED_POOL")]
    [InlineData("COMMENT ON TABLE orders IS 'abc")] // 닫히지 않은 리터럴
    public void Statements_without_an_owned_target_have_no_target(string text)
    {
        var r = _rewriter.Qualify(text, "SAMPLE");
        Assert.Null(r.TargetOwner);
        Assert.Null(r.TargetName);
    }

    [Fact]
    public void Remote_object_through_a_link_has_no_local_target()
        => Assert.Null(ChangeBundleOwnerRewriter.ReadTarget("DROP TABLE SAMPLE.ORDERS@remote_link"));

    [Fact]
    public void Rewrite_delegates_to_owner_rewriter()
    {
        var map = new Dictionary<string, string> { ["SAMPLE"] = "SAMPLE_PRD" };
        const string text = "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(30) DEFAULT 'SAMPLE.ORDERS') -- SAMPLE.X";
        var r = _rewriter.Rewrite(text, map);
        var expected = OwnerRewriter.Rewrite(text, map);
        Assert.Equal(new OwnerRenameResult(expected.Sql, expected.Changed, expected.Uncertain), r);
        Assert.Equal("ALTER TABLE SAMPLE_PRD.ORDERS ADD (NOTE VARCHAR2(30) DEFAULT 'SAMPLE.ORDERS') -- SAMPLE.X", r.Text);
    }

    [Theory]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = sample", "SAMPLE")]
    [InlineData("alter session set current_schema=\"Mixed\"", "Mixed")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = sample extra", null)]
    [InlineData("ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY'", null)]
    [InlineData("ALTER TABLE x ADD (y NUMBER)", null)]
    [InlineData("", null)]
    public void Current_schema_switch_is_read(string text, string? expected)
        => Assert.Equal(expected, _rewriter.CurrentSchemaSwitch(text));
}
