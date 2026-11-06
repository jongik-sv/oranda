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
        var b = B([new TableRule("C", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "D >= :FROM_DT AND X = :FROM_DT2" }],
                  new ResolvedPeriod(null, null), ("C", Cols(("D", "DATE"))));
        var s = b.SourceSelect("C", ["D"]);
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
}
