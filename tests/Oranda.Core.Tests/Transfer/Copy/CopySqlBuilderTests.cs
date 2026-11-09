using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class CopySqlBuilderTests
{
    private static readonly ResolvedPeriod Period = new(new DateTime(2026, 10, 7), new DateTime(2026, 11, 7));

    private static TableInfo T(string owner, string name, params (string Col, string Type)[] cols)
        => new(new DbObjectRef(owner, name, "TABLE"), cols.Select((c, i) => new ColumnInfo(c.Col, i + 1, c.Type)).ToList());

    private static CopySqlBuilder B(IReadOnlyList<TableRule> rules, ResolvedPeriod? period = null, params (string Name, (string, string)[] Cols)[] tables)
    {
        var job = new CopyJob
        {
            Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "TGT"),
            SourceOwner = "SRC", TargetOwner = "TGT", MaskSalt = "AA==", Tables = rules,
        };
        var src = tables.ToDictionary(t => t.Name, t => T("SRC", t.Name, t.Cols));
        var tgt = tables.ToDictionary(t => t.Name, t => T("TGT", t.Name, t.Cols));
        return new CopySqlBuilder(job, period ?? Period, src, tgt);
    }

    private static (string, string)[] Cols(params (string, string)[] c) => c;

    [Fact]
    public void All_rule_selects_without_where()
    {
        var b = B([new TableRule("CODE", RuleKind.All, TargetAction.MergeByPk)], null, ("CODE", Cols(("ID", "NUMBER"))));
        var s = b.SourceSelect("CODE", ["ID", "NAME"]);
        Assert.Equal("SELECT \"ID\", \"NAME\" FROM \"SRC\".\"CODE\"", s.Sql);
        Assert.Empty(s.Binds);
        Assert.Equal("SELECT COUNT(*) FROM \"SRC\".\"CODE\"", b.SourceCount("CODE").Sql);
    }

    [Fact]
    public void Period_date_column_uses_typed_binds()
    {
        var b = B([new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_DT")] }],
                  null, ("ORDERS", Cols(("ORDER_DT", "DATE"))));
        var s = b.SourceSelect("ORDERS", ["ORDER_DT"]);
        Assert.Equal("SELECT \"ORDER_DT\" FROM \"SRC\".\"ORDERS\" WHERE \"ORDER_DT\" >= :B1_FROM AND \"ORDER_DT\" < :B1_TO", s.Sql);
        Assert.Equal(new BindValue(BindDataType.Date, Period.From), s.Binds["B1_FROM"]);
        Assert.Equal(new BindValue(BindDataType.Date, Period.To), s.Binds["B1_TO"]);
        Assert.Equal("SELECT COUNT(*) FROM \"TGT\".\"ORDERS\" WHERE \"ORDER_DT\" >= :B1_FROM AND \"ORDER_DT\" < :B1_TO", b.TargetCount("ORDERS").Sql);
    }

    [Fact]
    public void Day_start_hour_shifts_date_binds_but_not_string_dates()
    {
        var day = new ResolvedPeriod(new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)) { DayStartHour = 7 };
        var b = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert)
                   { PeriodColumns = [new PeriodColumn("ORDER_DT"), new PeriodColumn("YMD", "YYYYMMDD")], ExtraWhere = "X < :TO_DT" }],
                  day, ("O", Cols(("ORDER_DT", "DATE"), ("YMD", "VARCHAR2"))));
        var s = b.SourceSelect("O", ["ORDER_DT"]);
        Assert.Equal(new BindValue(BindDataType.Date, new DateTime(2026, 11, 1, 7, 0, 0)), s.Binds["B1_FROM"]);
        Assert.Equal(new BindValue(BindDataType.Date, new DateTime(2026, 11, 2, 7, 0, 0)), s.Binds["B1_TO"]);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "20261101"), s.Binds["B2_FROM"]);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "20261102"), s.Binds["B2_TO"]);
        Assert.Equal(new BindValue(BindDataType.Date, new DateTime(2026, 11, 2, 7, 0, 0)), s.Binds["TO_DT"]);
    }

    [Fact]
    public void Period_timestamp_tz_and_ltz_columns_bind_matching_types()
    {
        var b = B([new TableRule("E", RuleKind.Period, TargetAction.DeleteRangeInsert)
                   { PeriodColumns = [new PeriodColumn("A"), new PeriodColumn("B"), new PeriodColumn("C")] }],
                  null, ("E", Cols(("A", "TIMESTAMP(6)"), ("B", "TIMESTAMP(6) WITH TIME ZONE"), ("C", "TIMESTAMP(6) WITH LOCAL TIME ZONE"))));
        var s = b.SourceSelect("E", ["A"]);
        Assert.Equal(BindDataType.Timestamp, s.Binds["B1_FROM"].Type);
        Assert.Equal(BindDataType.TimestampTz, s.Binds["B2_FROM"].Type);
        Assert.Equal(BindDataType.TimestampLtz, s.Binds["B3_TO"].Type);
    }

    [Fact]
    public void Period_string_column_binds_formatted_strings()
    {
        var b = B([new TableRule("S", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("YMD", "YYYYMMDD")] }],
                  null, ("S", Cols(("YMD", "VARCHAR2"))));
        var s = b.SourceSelect("S", ["YMD"]);
        Assert.DoesNotContain("TO_DATE", s.Sql);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "20261007"), s.Binds["B1_FROM"]);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "20261107"), s.Binds["B1_TO"]);
    }

    [Fact]
    public void Two_period_columns_are_ored()
    {
        var b = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("A"), new PeriodColumn("B")] }],
                  null, ("O", Cols(("A", "DATE"), ("B", "DATE"))));
        Assert.Equal(
            "SELECT \"A\" FROM \"SRC\".\"O\" WHERE (\"A\" >= :B1_FROM AND \"A\" < :B1_TO) OR (\"B\" >= :B2_FROM AND \"B\" < :B2_TO)",
            b.SourceSelect("O", ["A"]).Sql);
    }

    [Fact]
    public void Custom_where_binds_from_and_to_only_when_used()
    {
        var withBinds = B([new TableRule("C", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "D >= :FROM_DT AND D < :TO_DT" }],
                          null, ("C", Cols(("D", "DATE"))));
        var s = withBinds.SourceSelect("C", ["D"]);
        Assert.Equal("SELECT \"D\" FROM \"SRC\".\"C\" WHERE (D >= :FROM_DT AND D < :TO_DT)", s.Sql);
        Assert.Equal(new BindValue(BindDataType.Date, Period.From), s.Binds["FROM_DT"]);
        Assert.Equal(new BindValue(BindDataType.Date, Period.To), s.Binds["TO_DT"]);

        var none = B([new TableRule("C", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "STATUS = 'A'" }],
                     null, ("C", Cols(("D", "DATE"))));
        Assert.Empty(none.SourceSelect("C", ["D"]).Binds);
    }

    // 기간 규칙 + 추가 조건(AND): 원본 조회·원본 건수·대상 지우기·대상 건수가 모두 같은 식을 쓴다. 추가 조건 안의 OR 는 괄호 안에 머문다.
    [Fact]
    public void Period_extra_where_is_anded_into_every_statement_of_the_rule()
    {
        var b = B([new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert)
                   { PeriodColumns = [new PeriodColumn("ORDER_DT")], ExtraWhere = "STATUS = 'DONE' OR STATUS = 'SHIP'" }],
                  null, ("ORDERS", Cols(("ORDER_DT", "DATE"), ("STATUS", "VARCHAR2"))));
        const string where = " WHERE (\"ORDER_DT\" >= :B1_FROM AND \"ORDER_DT\" < :B1_TO) AND (STATUS = 'DONE' OR STATUS = 'SHIP')";
        Assert.Equal("SELECT \"ORDER_DT\" FROM \"SRC\".\"ORDERS\"" + where, b.SourceSelect("ORDERS", ["ORDER_DT"]).Sql);
        Assert.Equal("SELECT COUNT(*) FROM \"SRC\".\"ORDERS\"" + where, b.SourceCount("ORDERS").Sql);
        Assert.Equal("DELETE FROM \"TGT\".\"ORDERS\"" + where, b.TargetDelete("ORDERS")!.Sql); // 조건 밖 행은 대상에 남는다
        Assert.Equal("SELECT COUNT(*) FROM \"TGT\".\"ORDERS\"" + where, b.TargetCount("ORDERS").Sql);
        Assert.Equal(["B1_FROM", "B1_TO"], b.TargetDelete("ORDERS")!.Binds.Keys.Order());
    }

    [Fact]
    public void Two_period_columns_with_extra_where_keep_the_or_group_together()
    {
        var b = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert)
                   { PeriodColumns = [new PeriodColumn("A"), new PeriodColumn("B")], ExtraWhere = "X = 1" }],
                  null, ("O", Cols(("A", "DATE"), ("B", "DATE"))));
        Assert.Equal(
            "SELECT \"A\" FROM \"SRC\".\"O\" WHERE ((\"A\" >= :B1_FROM AND \"A\" < :B1_TO) OR (\"B\" >= :B2_FROM AND \"B\" < :B2_TO)) AND (X = 1)",
            b.SourceSelect("O", ["A"]).Sql);
    }

    [Fact]
    public void Extra_where_binds_from_and_to_dt_like_a_custom_condition()
    {
        var b = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert)
                   { PeriodColumns = [new PeriodColumn("A")], ExtraWhere = "SHIP_DT < :TO_DT" }],
                  null, ("O", Cols(("A", "DATE"))));
        var s = b.SourceSelect("O", ["A"]);
        Assert.EndsWith(" AND (SHIP_DT < :TO_DT)", s.Sql, StringComparison.Ordinal);
        Assert.Equal(new BindValue(BindDataType.Date, Period.To), s.Binds["TO_DT"]);
        Assert.False(s.Binds.ContainsKey("FROM_DT"));
    }

    [Fact]
    public void Open_period_with_extra_where_uses_only_the_extra_condition_and_blank_extra_changes_nothing()
    {
        var open = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert)
                      { PeriodColumns = [new PeriodColumn("A")], ExtraWhere = "X = 1" }],
                     new ResolvedPeriod(null, null), ("O", Cols(("A", "DATE"))));
        Assert.Equal("DELETE FROM \"TGT\".\"O\" WHERE (X = 1)", open.TargetDelete("O")!.Sql);

        var blank = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert)
                       { PeriodColumns = [new PeriodColumn("A")], ExtraWhere = "  " }],
                      null, ("O", Cols(("A", "DATE"))));
        Assert.Equal("SELECT \"A\" FROM \"SRC\".\"O\" WHERE \"A\" >= :B1_FROM AND \"A\" < :B1_TO", blank.SourceSelect("O", ["A"]).Sql);
    }

    [Fact]
    public void Extra_where_is_ignored_for_rules_that_are_not_period_rules()
    {
        var b = B([new TableRule("CODE", RuleKind.All, TargetAction.DeleteRangeInsert) { ExtraWhere = "X = 1" },
                   new TableRule("C", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "Y = 2", ExtraWhere = "X = 1" }],
                  null, ("CODE", Cols(("X", "NUMBER"))), ("C", Cols(("Y", "NUMBER"))));
        Assert.Equal("SELECT \"X\" FROM \"SRC\".\"CODE\"", b.SourceSelect("CODE", ["X"]).Sql);
        Assert.Equal("SELECT \"Y\" FROM \"SRC\".\"C\" WHERE (Y = 2)", b.SourceSelect("C", ["Y"]).Sql);
    }

    [Fact]
    public void Parent_key_subquery_carries_the_parent_extra_where()
    {
        var rules = new[]
        {
            new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_DT")], ExtraWhere = "STATUS = 'DONE'" },
            new TableRule("ITEM", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("ORDERS", ["ORDER_ID"], ["ID"]) },
        };
        var b = B(rules, null, ("ORDERS", Cols(("ID", "NUMBER"), ("ORDER_DT", "DATE"))), ("ITEM", Cols(("ORDER_ID", "NUMBER"))));
        const string sub = " WHERE \"ORDER_ID\" IN (SELECT \"ID\" FROM \"{0}\".\"ORDERS\" WHERE (\"ORDER_DT\" >= :B1_FROM AND \"ORDER_DT\" < :B1_TO) AND (STATUS = 'DONE'))";
        Assert.Equal("SELECT \"ORDER_ID\" FROM \"SRC\".\"ITEM\"" + string.Format(System.Globalization.CultureInfo.InvariantCulture, sub, "SRC"), b.SourceSelect("ITEM", ["ORDER_ID"]).Sql);
        Assert.Equal("DELETE FROM \"TGT\".\"ITEM\"" + string.Format(System.Globalization.CultureInfo.InvariantCulture, sub, "TGT"), b.TargetDelete("ITEM")!.Sql);
    }

    // 괄호 밖으로 빠져나가는 조건은 AND 를 벗어나 대상 지우기 범위를 표 전체로 넓힌다: 검사가 거부하고, 빌더도 최후 방어로 문장을 만들지 않는다.
    [Theory]
    [InlineData("X=1) OR (1=1")]
    [InlineData("X=1 -- 뒤는 주석")]
    [InlineData("X=1 /* 주석 */")]
    [InlineData("X=1; DELETE FROM T")]
    [InlineData("(X=1")]
    [InlineData("NAME = 'A")]
    [InlineData("X = q'[a'b]') OR (1=1 OR 'x'='x")]
    [InlineData("N = nQ'{x}'")]
    public void Escaping_user_conditions_are_rejected_for_extra_and_custom_conditions(string where)
    {
        Assert.NotNull(CopySqlBuilder.CheckUserCondition(where));
        var b = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("A")], ExtraWhere = where },
                   new TableRule("C", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = where }],
                  null, ("O", Cols(("A", "DATE"))), ("C", Cols(("A", "DATE"))));
        Assert.Throws<InvalidOperationException>(() => b.TargetDelete("O"));
        Assert.Throws<InvalidOperationException>(() => b.TargetDelete("C"));
        Assert.Throws<InvalidOperationException>(() => b.SourceSelect("O", ["A"]));
    }

    // 대체 따옴표 안의 ' 는 문자열을 닫지 않아 괄호 검사가 문자열 끝을 잘못 본다: 머리(q'·nq', 대소문자 무관)부터 막는다.
    [Theory]
    [InlineData("X = q'[a'b]') OR (1=1 OR 'x'='x")]
    [InlineData("N = nQ'{x}'")]
    [InlineData("Q'<a>' = X")]
    [InlineData("X IN (Nq'!a!')")]
    public void Alternative_quoting_is_rejected(string where) =>
        Assert.Equal("대체 따옴표(q'…')는 쓸 수 없습니다.", CopySqlBuilder.CheckUserCondition(where));

    [Theory]
    [InlineData("NOTE = 'a) OR (1=1'")]
    [InlineData("NOTE LIKE '--%' AND MEMO <> '/* x */;'")]
    [InlineData("NAME = 'O''BRIEN' AND (A = 1 OR (B = 2))")]
    [InlineData("\"Odd)Name\" = 1")]
    [InlineData("NAME = seq'a'")]
    public void Parentheses_and_comment_marks_inside_quotes_are_allowed(string where)
    {
        Assert.Null(CopySqlBuilder.CheckUserCondition(where));
        var b = B([new TableRule("O", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("A")], ExtraWhere = where }],
                  null, ("O", Cols(("A", "DATE"))));
        Assert.EndsWith($" AND ({where})", b.TargetDelete("O")!.Sql, StringComparison.Ordinal);
    }

    private static IReadOnlyList<TableRule> Chain() =>
    [
        new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_DT")] },
        new TableRule("ITEM", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("ORDERS", ["ORDER_ID"], ["ID"]) },
        new TableRule("OPT", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("ITEM", ["ITEM_ID"], ["ID"]) },
    ];

    [Fact]
    public void Parent_key_nests_two_levels()
    {
        var b = B(Chain(), null, ("ORDERS", Cols(("ID", "NUMBER"), ("ORDER_DT", "DATE"))), ("ITEM", Cols(("ID", "NUMBER"), ("ORDER_ID", "NUMBER"))),
                  ("OPT", Cols(("ITEM_ID", "NUMBER"))));
        var s = b.SourceSelect("OPT", ["ITEM_ID"]);
        Assert.Equal(
            "SELECT \"ITEM_ID\" FROM \"SRC\".\"OPT\" WHERE \"ITEM_ID\" IN (SELECT \"ID\" FROM \"SRC\".\"ITEM\" WHERE \"ORDER_ID\" IN " +
            "(SELECT \"ID\" FROM \"SRC\".\"ORDERS\" WHERE \"ORDER_DT\" >= :B1_FROM AND \"ORDER_DT\" < :B1_TO))",
            s.Sql);
        Assert.Equal(["B1_FROM", "B1_TO"], s.Binds.Keys.Order());
    }

    [Fact]
    public void Parent_key_with_composite_columns_uses_tuple_in()
    {
        var b = B([new TableRule("P", RuleKind.All, TargetAction.MergeByPk),
                   new TableRule("C", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("P", ["A1", "A2"], ["K1", "K2"]) }],
                  null, ("P", Cols(("K1", "NUMBER"), ("K2", "NUMBER"))), ("C", Cols(("A1", "NUMBER"), ("A2", "NUMBER"))));
        Assert.Equal("SELECT \"A1\" FROM \"SRC\".\"C\" WHERE (\"A1\", \"A2\") IN (SELECT \"K1\", \"K2\" FROM \"SRC\".\"P\")", b.SourceSelect("C", ["A1"]).Sql);
    }

    [Fact]
    public void Target_delete_uses_target_owner_and_target_parent()
    {
        var b = B(Chain(), null, ("ORDERS", Cols(("ID", "NUMBER"), ("ORDER_DT", "DATE"))), ("ITEM", Cols(("ID", "NUMBER"), ("ORDER_ID", "NUMBER"))),
                  ("OPT", Cols(("ITEM_ID", "NUMBER"))));
        var d = b.TargetDelete("ITEM")!;
        Assert.Equal(
            "DELETE FROM \"TGT\".\"ITEM\" WHERE \"ORDER_ID\" IN (SELECT \"ID\" FROM \"TGT\".\"ORDERS\" WHERE \"ORDER_DT\" >= :B1_FROM AND \"ORDER_DT\" < :B1_TO)",
            d.Sql);
        Assert.Equal(2, d.Binds.Count);
    }

    [Fact]
    public void Delete_all_has_no_where_and_append_has_no_delete()
    {
        var b = B([new TableRule("A", RuleKind.All, TargetAction.DeleteAllInsert), new TableRule("B", RuleKind.All, TargetAction.AppendOnly),
                   new TableRule("M", RuleKind.All, TargetAction.MergeByPk)],
                  null, ("A", Cols(("X", "NUMBER"))), ("B", Cols(("X", "NUMBER"))), ("M", Cols(("X", "NUMBER"))));
        Assert.Equal("DELETE FROM \"TGT\".\"A\"", b.TargetDelete("A")!.Sql);
        Assert.Null(b.TargetDelete("B"));
        Assert.Null(b.TargetDelete("M"));
        Assert.Equal("INSERT INTO \"TGT\".\"A\" (\"X\", \"Y\") VALUES (:C1, :C2)", b.Insert("A", ["X", "Y"]));
        Assert.Equal("SELECT \"X\", \"Y\" FROM \"SRC\".\"A\" WHERE \"X\" = :K1 AND \"Y\" = :K2", b.SourceSelectByKeys("A", ["X", "Y"], ["X", "Y"]));
    }

    [Fact]
    public void Merge_updates_non_key_columns_and_omits_matched_when_all_keys()
    {
        var b = B([new TableRule("M", RuleKind.All, TargetAction.MergeByPk)], null, ("M", Cols(("K", "NUMBER"))));
        Assert.Equal(
            "MERGE INTO \"TGT\".\"M\" T USING (SELECT :C1 AS \"K\", :C2 AS \"V\" FROM DUAL) S ON (T.\"K\" = S.\"K\") " +
            "WHEN MATCHED THEN UPDATE SET T.\"V\" = S.\"V\" WHEN NOT MATCHED THEN INSERT (\"K\", \"V\") VALUES (S.\"K\", S.\"V\")",
            b.Merge("M", ["K", "V"], ["K"]));
        Assert.Equal(
            "MERGE INTO \"TGT\".\"M\" T USING (SELECT :C1 AS \"K\" FROM DUAL) S ON (T.\"K\" = S.\"K\") " +
            "WHEN NOT MATCHED THEN INSERT (\"K\") VALUES (S.\"K\")",
            b.Merge("M", ["K"], ["K"]));
    }

    [Fact]
    public void Value_placeholders_default_to_bind_names_and_follow_value_at()
    {
        var b = B([new TableRule("M", RuleKind.All, TargetAction.MergeByPk)], null, ("M", Cols(("K", "NUMBER"))));

        // 기본 = :Cn — 값 자리 함수가 :Cn 을 주면 글이 같다(Oracle 방언).
        static string Plain(int i) => ":C" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(b.Insert("M", ["K", "V"]), b.Insert("M", ["K", "V"], Plain));
        Assert.Equal(b.Merge("M", ["K", "V"], ["K"]), b.Merge("M", ["K", "V"], ["K"], Plain));

        // 감싸는 방언(Tibero): VALUES 와 USING (SELECT …) 의 값 자리에만 들어가고, MERGE 의 INSERT VALUES 는 S.열 그대로다.
        string Wrapped(int i) => i == 1 ? "TO_DATE(:C2, 'YYYY-MM-DD HH24:MI:SS')" : Plain(i);
        Assert.Equal("INSERT INTO \"TGT\".\"M\" (\"K\", \"V\") VALUES (:C1, TO_DATE(:C2, 'YYYY-MM-DD HH24:MI:SS'))", b.Insert("M", ["K", "V"], Wrapped));
        Assert.Equal(
            "MERGE INTO \"TGT\".\"M\" T USING (SELECT :C1 AS \"K\", TO_DATE(:C2, 'YYYY-MM-DD HH24:MI:SS') AS \"V\" FROM DUAL) S ON (T.\"K\" = S.\"K\") " +
            "WHEN MATCHED THEN UPDATE SET T.\"V\" = S.\"V\" WHEN NOT MATCHED THEN INSERT (\"K\", \"V\") VALUES (S.\"K\", S.\"V\")",
            b.Merge("M", ["K", "V"], ["K"], Wrapped));
    }

    [Fact]
    public void Identifiers_are_always_quoted()
    {
        var b = B([new TableRule("MixedCase", RuleKind.All, TargetAction.DeleteAllInsert)], null, ("MixedCase", Cols(("a", "NUMBER"))));
        Assert.Equal("SELECT \"a\" FROM \"SRC\".\"MixedCase\"", b.SourceSelect("MixedCase", ["a"]).Sql);
        Assert.Equal("DELETE FROM \"TGT\".\"MixedCase\"", b.TargetDelete("MixedCase")!.Sql);
    }

    [Fact]
    public void Target_period_bind_types_follow_target_metadata()
    {
        var rule = new TableRule("E", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("A")] };
        var job = new CopyJob
        {
            Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "TGT"),
            SourceOwner = "SRC", TargetOwner = "TGT", MaskSalt = "AA==", Tables = [rule],
        };
        var b = new CopySqlBuilder(job, Period, new Dictionary<string, TableInfo> { ["E"] = T("SRC", "E", ("A", "DATE")) },
                                   new Dictionary<string, TableInfo> { ["E"] = T("TGT", "E", ("A", "TIMESTAMP(6)")) });
        Assert.Equal(BindDataType.Date, b.SourceCount("E").Binds["B1_FROM"].Type);
        Assert.Equal(BindDataType.Timestamp, b.TargetCount("E").Binds["B1_FROM"].Type);
        Assert.Equal(BindDataType.Timestamp, b.TargetDelete("E")!.Binds["B1_TO"].Type);
    }

    [Fact]
    public void Open_period_binds_null_for_named_custom_params_and_skips_period_columns()
    {
        // :FROM_DT2 는 :FROM_DT 가 아니다(이름 경계). 변수로 두지 않은 이름은 막으므로 작업 변수로 둔다.
        var rule = new TableRule("C", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "D >= :FROM_DT AND X = :FROM_DT2" };
        var job = new CopyJob
        {
            Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "TGT"),
            SourceOwner = "SRC", TargetOwner = "TGT", MaskSalt = "AA==", Tables = [rule],
            Variables = [new CopyVariable("FROM_DT2", CopyVariableType.Text, ["A"])],
        };
        var b = new CopySqlBuilder(job, new ResolvedPeriod(null, null),
            new Dictionary<string, TableInfo> { ["C"] = T("SRC", "C", ("D", "DATE")) }, new Dictionary<string, TableInfo> { ["C"] = T("TGT", "C", ("D", "DATE")) });
        var s = b.SourceSelect("C", ["D"]);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "A"), s.Binds["FROM_DT2"]);
        Assert.Equal(new BindValue(BindDataType.Date, null), s.Binds["FROM_DT"]);
        Assert.False(s.Binds.ContainsKey("TO_DT"));

        var p = B([new TableRule("P", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("D")] }],
                  new ResolvedPeriod(new DateTime(2026, 1, 1), null), ("P", Cols(("D", "DATE"))));
        var ps = p.SourceSelect("P", ["D"]);
        Assert.Equal("SELECT \"D\" FROM \"SRC\".\"P\" WHERE \"D\" >= :B1_FROM", ps.Sql);
        Assert.Single(ps.Binds);
    }

    [Fact]
    public void Rejects_cyclic_parent_links_and_invalid_inputs()
    {
        var b = B([new TableRule("E", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("E", ["M"], ["ID"]) },
                   new TableRule("Z", RuleKind.Period, TargetAction.DeleteRangeInsert)],
                  null, ("E", Cols(("ID", "NUMBER"))), ("Z", Cols(("D", "DATE"))));
        Assert.Throws<InvalidOperationException>(() => b.SourceSelect("E", ["ID"]));
        Assert.Throws<InvalidOperationException>(() => b.SourceSelect("Z", ["D"]));
        Assert.Throws<ArgumentException>(() => b.Merge("E", ["ID"], []));
        Assert.Throws<ArgumentException>(() => b.Merge("E", ["ID"], ["OTHER"]));
    }

    // ---- 규칙마다의 계정(묶음 B) ----

    private static CopySqlBuilder Owners(params TableRule[] rules)
    {
        var job = new CopyJob
        {
            Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "TGT"),
            SourceOwner = "SRC", TargetOwner = "TGT", MaskSalt = "AA==", Tables = rules,
        };
        var empty = rules.ToDictionary(r => CopyOwners.Key(job, r), _ => T("X", "X"));
        return new CopySqlBuilder(job, Period, empty, empty);
    }

    [Fact]
    public void Each_statement_uses_its_rule_source_and_target_owner()
    {
        var b = Owners(
            new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = "APP2", TargetOwner = "APP2X" });

        // 같은 이름 표 둘: 기본 계정 표는 이름만, 다른 계정 표는 계정.표 키로 가린다.
        Assert.Equal("SELECT \"ID\" FROM \"SRC\".\"ORDERS\"", b.SourceSelect("ORDERS", ["ID"]).Sql);
        Assert.Equal("SELECT \"ID\" FROM \"APP2\".\"ORDERS\"", b.SourceSelect("APP2.ORDERS", ["ID"]).Sql);
        Assert.Equal("DELETE FROM \"TGT\".\"ORDERS\"", b.TargetDelete("ORDERS")!.Sql);
        Assert.Equal("DELETE FROM \"APP2X\".\"ORDERS\"", b.TargetDelete("APP2.ORDERS")!.Sql);
        Assert.Equal("INSERT INTO \"APP2X\".\"ORDERS\" (\"ID\") VALUES (:C1)", b.Insert("APP2.ORDERS", ["ID"]));
        Assert.StartsWith("MERGE INTO \"APP2X\".\"ORDERS\" T", b.Merge("APP2.ORDERS", ["ID", "N"], ["ID"]), StringComparison.Ordinal);
        Assert.Equal("SELECT COUNT(*) FROM \"APP2\".\"ORDERS\"", b.SourceCount("APP2.ORDERS").Sql);
        Assert.Equal("SELECT COUNT(*) FROM \"APP2X\".\"ORDERS\"", b.TargetCount("APP2.ORDERS").Sql);
        Assert.Equal("SELECT \"ID\" FROM \"APP2\".\"ORDERS\" WHERE \"ID\" = :K1", b.SourceSelectByKeys("APP2.ORDERS", ["ID"], ["ID"]));
        Assert.Throws<InvalidOperationException>(() => b.SourceSelect("SRC.ORDERS", ["ID"])); // 기본 계정 표의 키는 이름만이다
    }

    [Fact]
    public void Parent_key_reads_parent_owner_on_source_and_parent_rule_target_on_target()
    {
        var b = Owners(
            new TableRule("CUSTOMERS", RuleKind.Custom, TargetAction.MergeByPk) { SourceOwner = "APP", TargetOwner = "APPT", CustomWhere = "ACTIVE = 1" },
            new TableRule("ORDERS", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
            {
                SourceOwner = "APP2", TargetOwner = "APP2T",
                Parent = new ParentLink("CUSTOMERS", ["CUST_ID"], ["ID"]) { ParentOwner = "APP" },
            });

        Assert.Equal("SELECT \"ID\" FROM \"APP2\".\"ORDERS\" WHERE \"CUST_ID\" IN (SELECT \"ID\" FROM \"APP\".\"CUSTOMERS\" WHERE (ACTIVE = 1))",
            b.SourceSelect("APP2.ORDERS", ["ID"]).Sql);
        Assert.Equal("DELETE FROM \"APP2T\".\"ORDERS\" WHERE \"CUST_ID\" IN (SELECT \"ID\" FROM \"APPT\".\"CUSTOMERS\" WHERE (ACTIVE = 1))",
            b.TargetDelete("APP2.ORDERS")!.Sql);
    }

    [Fact]
    public void Parent_outside_job_uses_child_source_owner_and_mapped_target()
    {
        var b = Owners(
            new TableRule("LINES", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
            {
                SourceOwner = "APP2", TargetOwner = "APP2",
                Parent = new ParentLink("HEADERS", ["H_ID"], ["ID"]),
            },
            new TableRule("ITEM", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("HEAD", ["H_ID"], ["ID"]) });

        // 부모가 작업 밖: 원본은 자식의 원본 계정, 대상은 MapTarget(기본 계정이 아니면 이름 그대로, 기본 계정이면 기본 대상 계정).
        Assert.Equal("DELETE FROM \"APP2\".\"LINES\" WHERE \"H_ID\" IN (SELECT \"ID\" FROM \"APP2\".\"HEADERS\")", b.TargetDelete("APP2.LINES")!.Sql);
        Assert.Equal("DELETE FROM \"TGT\".\"ITEM\" WHERE \"H_ID\" IN (SELECT \"ID\" FROM \"TGT\".\"HEAD\")", b.TargetDelete("ITEM")!.Sql);
        Assert.Equal("SELECT \"ID\" FROM \"SRC\".\"ITEM\" WHERE \"H_ID\" IN (SELECT \"ID\" FROM \"SRC\".\"HEAD\")", b.SourceSelect("ITEM", ["ID"]).Sql);
    }
}
