using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 새 표에 딸려 함께 만드는 객체의 문장 만들기(SQ2, 조정 결정 78, <see cref="CopyCompanions"/>·<see cref="CompanionDdl"/>). 순수 함수, 서버 없음.
/// </summary>
public sealed class CopyCompanionsTests
{
    // ---- 함수 기반 인덱스 ----

    [Fact]
    public void Function_index_reowns_the_source_owner_prefix_inside_the_expression()
    {
        var index = new IndexInfo("IX_ORD_FN", false, ["SYS_NC00003$"]) { IndexType = "FUNCTION-BASED NORMAL" };
        IndexColumnDetail[] columns = [new(1, "SYS_NC00003$", "\"SRC\".\"FN_UP\"(\"NAME\")", false)];

        var ddl = CopyCompanions.FunctionIndex("DST", "ORDERS", index, columns, "SRC");

        Assert.Equal("CREATE INDEX \"DST\".\"IX_ORD_FN\" ON \"DST\".\"ORDERS\" (\"DST\".\"FN_UP\"(\"NAME\"))", ddl);
    }

    [Fact]
    public void Function_index_keeps_position_order_desc_unique_and_quotes_a_plain_column()
    {
        // 열은 위치 순으로 놓는다(받은 순서가 아니다). 식은 앞뒤 공백을 떼고, 일반 열은 따옴표 이름으로 쓴다.
        var index = new IndexInfo("UX_ORD", true, ["SYS_NC00004$", "ID"]) { IndexType = "FUNCTION-BASED NORMAL" };
        IndexColumnDetail[] columns = [new(2, "ID", null, false), new(1, "SYS_NC00004$", " UPPER(\"NAME\") ", true)];

        var ddl = CopyCompanions.FunctionIndex("DST", "ORDERS", index, columns, "SRC");

        Assert.Equal("CREATE UNIQUE INDEX \"DST\".\"UX_ORD\" ON \"DST\".\"ORDERS\" (UPPER(\"NAME\") DESC, \"ID\")", ddl);
    }

    [Fact]
    public void Function_index_on_the_same_owner_is_left_as_it_is_and_a_desc_plain_column_gets_desc()
    {
        var index = new IndexInfo("IX_ORD", false, ["ID", "SYS_NC00005$"]) { IndexType = "FUNCTION-BASED NORMAL" };
        IndexColumnDetail[] columns = [new(1, "ID", null, true), new(2, "SYS_NC00005$", "\"SRC\".\"FN\"(\"ID\")", false)];

        var ddl = CopyCompanions.FunctionIndex("SRC", "ORDERS", index, columns, "SRC");

        Assert.Equal("CREATE INDEX \"SRC\".\"IX_ORD\" ON \"SRC\".\"ORDERS\" (\"ID\" DESC, \"SRC\".\"FN\"(\"ID\"))", ddl);
    }

    [Fact]
    public void Function_index_without_column_details_is_null()
    {
        var index = new IndexInfo("IX_ORD_FN", false, ["SYS_NC00003$"]) { IndexType = "FUNCTION-BASED NORMAL" };

        Assert.Null(CopyCompanions.FunctionIndex("DST", "ORDERS", index, [], "SRC"));
    }

    [Theory]
    [InlineData("FUNCTION-BASED NORMAL", true)]
    [InlineData("FUNCTION-BASED BITMAP", true)]
    [InlineData("function-based normal", true)]
    [InlineData("NORMAL", false)]
    [InlineData("BITMAP", false)]
    [InlineData(null, false)]
    public void Function_index_is_told_by_the_index_type(string? indexType, bool expected)
    {
        var index = new IndexInfo("IX", false, ["A"]) { IndexType = indexType };

        Assert.Equal(expected, CopyCompanions.IsFunctionIndex(index));
    }

    // ---- 교차 복사에서 만들지 않는 열을 쓰는가 ----

    [Fact]
    public void Uses_column_finds_a_removed_column_quoted_in_an_expression_or_as_a_plain_column()
    {
        var removed = new HashSet<string>(StringComparer.Ordinal) { "OLD_COL", "A\"B" };

        Assert.Equal("OLD_COL", CopyCompanions.UsesColumn([new(1, "SYS_NC1$", "SUBSTR(\"OLD_COL\",1,3)", false)], removed));
        Assert.Equal("OLD_COL", CopyCompanions.UsesColumn([new(1, "ID", null, false), new(2, "OLD_COL", null, false)], removed));
        Assert.Equal("A\"B", CopyCompanions.UsesColumn([new(1, "SYS_NC1$", "UPPER(\"A\"\"B\")", false)], removed)); // 따옴표 안 "" 는 " 하나
    }

    [Fact]
    public void Uses_column_is_null_when_no_removed_column_is_used()
    {
        var removed = new HashSet<string>(StringComparer.Ordinal) { "OLD_COL" };

        Assert.Null(CopyCompanions.UsesColumn([new(1, "SYS_NC1$", "UPPER(\"NAME\")", false), new(2, "ID", null, false)], removed));
        Assert.Null(CopyCompanions.UsesColumn([new(1, "SYS_NC1$", "UPPER(\"OLD_COL_2\")", false)], removed)); // 이름 일부가 아니라 따옴표 이름 전체를 본다
        Assert.Null(CopyCompanions.UsesColumn([new(1, "SYS_NC1$", "UPPER(\"OLD_COL\")", false)], new HashSet<string>(StringComparer.Ordinal)));
    }

    // ---- 권한 받는 쪽 ----

    [Theory]
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"APP_USER\"", "APP_USER")]
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO PUBLIC", "PUBLIC")]
    [InlineData("GRANT UPDATE ON \"SRC\".\"ORDERS\" TO \"APP_USER\" WITH GRANT OPTION", "APP_USER")]
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO PUBLIC WITH GRANT OPTION", "PUBLIC")]
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"My \"\"Odd\"\" Role\"", "My \"Odd\" Role")]
    public void Grantee_is_the_unquoted_receiver_name(string grant, string expected)
    {
        Assert.Equal(expected, CopyCompanions.Grantee(grant));
    }

    [Theory]
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO APP_USER")] // 따옴표 없는 이름은 실행기 받아 두기가 만들지 않는 모양
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"APP_USER\" WITH HIERARCHY OPTION")]
    [InlineData("SELECT 1 FROM DUAL")]
    [InlineData("")]
    public void Grantee_of_an_unreadable_grant_is_null(string grant)
    {
        Assert.Null(CopyCompanions.Grantee(grant));
    }

    [Fact]
    public void Grantee_of_null_is_null()
    {
        Assert.Null(CopyCompanions.Grantee(null!));
    }

    // ---- 소유자 바꾸기(트리거·권한 문장) ----

    [Theory]
    [InlineData("\"SRC\".\"T\"", "\"DST\".\"T\"")]                                          // 따옴표 소유자
    [InlineData("SELECT src.seq.NEXTVAL INTO :NEW.ID FROM DUAL", "SELECT \"DST\".seq.NEXTVAL INTO :NEW.ID FROM DUAL")] // 트리거 본문의 따옴표 없는 소유자(대소문자 무시)
    [InlineData("SRC.\"T\"", "\"DST\".\"T\"")]                                              // 따옴표 없는 소유자 뒤 따옴표 이름
    [InlineData("SRC._T", "\"DST\"._T")]
    [InlineData("v := 'SRC.x' || SRC.T.C;", "v := 'SRC.x' || \"DST\".T.C;")]               // 작은따옴표 문자열 리터럴 안은 그대로
    [InlineData("v := 'it''s SRC.x'; w := SRC.T;", "v := 'it''s SRC.x'; w := \"DST\".T;")] // 리터럴 안 '' 도 리터럴이다
    [InlineData("XSRC.T", "XSRC.T")]                                                        // 낱말 경계: 다른 이름의 일부는 그대로
    [InlineData("SRC_X.T", "SRC_X.T")]
    [InlineData("A$SRC.T", "A$SRC.T")]
    [InlineData("SRC.1", "SRC.1")]                                                          // 다음 글자가 영문·_·" 가 아니면 그대로
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"SRC\"", "GRANT SELECT ON \"DST\".\"ORDERS\" TO \"SRC\"")] // 점이 따르지 않는 받는 쪽 이름은 그대로
    public void Reowned_replaces_the_quoted_and_bare_source_owner_prefix_outside_string_literals(string text, string expected)
    {
        Assert.Equal(expected, CopyCompanions.Reowned(text, "SRC", "DST"));
    }

    [Theory]
    [InlineData("GRANT SELECT ON \"SRC\".\"ORDERS\" TO \"APP\"")]
    [InlineData("SELECT src.seq.NEXTVAL FROM DUAL")]
    public void Reowned_leaves_the_text_as_it_is_when_the_source_and_target_owner_are_the_same(string text)
    {
        Assert.Equal(text, CopyCompanions.Reowned(text, "SRC", "SRC"));
    }

    [Fact]
    public void Reowned_of_an_empty_text_is_empty()
    {
        Assert.Equal(string.Empty, CopyCompanions.Reowned(string.Empty, "SRC", "DST"));
    }

    // ---- 딸린 객체 묶음 ----

    [Fact]
    public void Statements_are_function_indexes_then_triggers_then_grants_then_outer_foreign_keys()
    {
        var companions = new CompanionDdl(
            Triggers: ["TRG1", "TRG1 DISABLE"],
            TriggerObjects: [new DisabledObject(DisabledKind.Trigger, "DST", "ORDERS", "TRG1"), new DisabledObject(DisabledKind.Trigger, "DST", "ORDERS", "TRG1")],
            Grants: ["GRANT1"],
            FunctionIndexes: ["FBI1", "FBI2"],
            OuterForeignKeys: [("CUSTOMERS", "FK1", "FK_ORD_CUST"), ("ITEMS", "FK2", null)]);

        Assert.Equal(["FBI1", "FBI2", "TRG1", "TRG1 DISABLE", "GRANT1", "FK1", "FK2"], companions.Statements);
        Assert.False(companions.IsEmpty);
    }

    [Fact]
    public void Empty_is_empty_and_any_single_kind_makes_it_not_empty()
    {
        Assert.True(CompanionDdl.Empty.IsEmpty);
        Assert.Empty(CompanionDdl.Empty.Statements);
        Assert.True(new CompanionDdl([], [], [], [], []).IsEmpty);

        Assert.False((CompanionDdl.Empty with { Triggers = ["T"] }).IsEmpty);
        Assert.False((CompanionDdl.Empty with { Grants = ["G"] }).IsEmpty);
        Assert.False((CompanionDdl.Empty with { FunctionIndexes = ["I"] }).IsEmpty);
        Assert.False((CompanionDdl.Empty with { OuterForeignKeys = [("P", "F", null)] }).IsEmpty);
        Assert.True((CompanionDdl.Empty with { TriggerObjects = [new DisabledObject(DisabledKind.Trigger, "DST", "ORDERS", "T")] }).IsEmpty); // 문장이 없으면 빈 것
    }
}
