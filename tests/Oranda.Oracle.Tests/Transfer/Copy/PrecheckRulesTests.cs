using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: 데이터 복사 Task 8. 사전 점검의 순수 판정(DB 없음).

public sealed class PrecheckRulesTests
{
    private static readonly TableRule Rule = new("ORDERS", RuleKind.All, TargetAction.MergeByPk);

    private static ColumnInfo Col(string name, string type, int position = 1) => new(name, position, type);

    private static TableInfo Table(params ColumnInfo[] columns) => new(new DbObjectRef("APP", "ORDERS", "TABLE"), columns);

    private static TableInfo WithPk(TableInfo table, params string[] columns)
        => table with { Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, columns)] };

    [Fact]
    public void Source_column_missing_in_target_is_confirm_and_skipped()
    {
        var source = Table(Col("ID", "NUMBER"), Col("MEMO", "VARCHAR2"));
        var target = Table(Col("ID", "NUMBER"));

        var items = PrecheckRules.CompareColumns(source, target, Rule);

        var item = Assert.Single(items);
        Assert.Equal("ColumnNotInTarget", item.Code);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("MEMO", item.Column);
        Assert.Equal(["ID"], PrecheckRules.SelectColumns(source, target, Rule));
    }

    [Fact]
    public void Target_not_null_without_default_missing_in_source_is_error()
    {
        var source = Table(Col("ID", "NUMBER"));
        var target = Table(
            Col("ID", "NUMBER"),
            Col("OWNER_ID", "NUMBER", 2) with { IsNullable = false },
            Col("STATUS", "VARCHAR2", 3) with { IsNullable = false, DefaultValue = "'N'" },
            Col("NOTE", "VARCHAR2", 4));

        var items = PrecheckRules.CompareColumns(source, target, Rule);

        var item = Assert.Single(items);
        Assert.Equal("TargetRequiredColumnMissing", item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Equal("OWNER_ID", item.Column);
    }

    [Fact]
    public void Narrower_target_length_and_precision_is_confirm()
    {
        var source = Table(
            Col("NAME", "VARCHAR2") with { Length = 100 },
            Col("AMOUNT", "NUMBER", 2) with { Precision = 12, Scale = 2 },
            Col("SAME", "VARCHAR2", 3) with { Length = 10 });
        var target = Table(
            Col("NAME", "VARCHAR2") with { Length = 20 },
            Col("AMOUNT", "NUMBER", 2) with { Precision = 8, Scale = 2 },
            Col("SAME", "VARCHAR2", 3) with { Length = 10 });

        var items = PrecheckRules.CompareColumns(source, target, Rule);

        Assert.Equal(2, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal("NarrowerType", i.Code);
            Assert.Equal(CheckLevel.Confirm, i.Level);
        });
        Assert.Equal(["NAME", "AMOUNT"], items.Select(i => i.Column));
    }

    [Fact]
    public void Unsupported_type_is_error_with_skip_choice()
    {
        var source = Table(Col("ID", "NUMBER"), Col("DOC", "XMLTYPE", 2));
        var target = Table(Col("ID", "NUMBER"), Col("DOC", "XMLTYPE", 2));

        var item = Assert.Single(PrecheckRules.CompareColumns(source, target, Rule));

        Assert.Equal("UnsupportedType", item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Equal("DOC", item.Column);
        Assert.Contains(CheckChoice.SkipColumn, item.Choices);
        Assert.Equal(["ID"], PrecheckRules.SelectColumns(source, target, Rule));

        // 건너뛰기로 정하면 오류가 사라진다.
        Assert.Empty(PrecheckRules.CompareColumns(source, target, Rule with { SkipColumns = ["DOC"] }));
    }

    [Fact]
    public void Identity_always_is_error_and_virtual_is_skipped_silently()
    {
        var source = Table(Col("ID", "NUMBER"), Col("TOTAL", "NUMBER", 2) with { IsVirtual = true });
        var target = Table(
            Col("ID", "NUMBER") with { IsIdentity = true, IsNullable = false },
            Col("TOTAL", "NUMBER", 2) with { IsVirtual = true });

        var items = PrecheckRules.CompareColumns(source, target, Rule);

        var item = Assert.Single(items);
        Assert.Equal("IdentityAlways", item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Equal("ID", item.Column);
        Assert.DoesNotContain(items, i => i.Column == "TOTAL");
        Assert.DoesNotContain("TOTAL", PrecheckRules.SelectColumns(source, target, Rule));
    }

    [Fact]
    public void Mask_on_primary_key_or_parent_link_is_error()
    {
        var source = WithPk(Table(Col("ID", "NUMBER"), Col("PARENT_ID", "NUMBER", 2), Col("EMAIL", "VARCHAR2", 3), Col("PHONE", "VARCHAR2", 4)), "ID");
        var rule = new TableRule("ORDERS", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            Parent = new ParentLink("CUSTOMERS", ["PARENT_ID"], ["ID"]),
            Masks =
            [
                new MaskRule("ID", MaskKind.Hash),
                new MaskRule("PARENT_ID", MaskKind.Null),
                new MaskRule("EMAIL", MaskKind.Hash),
                new MaskRule("PHONE", MaskKind.KeepLast, Count: 4),
            ],
        };

        var items = PrecheckRules.CheckMasks(rule, source, ["ID"]);

        Assert.Equal(["ID", "PARENT_ID"], items.Where(i => i.Code == "MaskOnKey").Select(i => i.Column));
        Assert.All(items.Where(i => i.Code == "MaskOnKey"), i => Assert.Equal(CheckLevel.Error, i.Level));

        // 가리는 열 목록은 통과 항목으로 보여 준다(키 열은 빠진다).
        var listed = Assert.Single(items, i => i.Code == "MaskedColumns");
        Assert.Equal(CheckLevel.Pass, listed.Level);
        Assert.Contains("EMAIL", listed.Message, StringComparison.Ordinal);
        Assert.Contains("PHONE", listed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PARENT_ID", listed.Message, StringComparison.Ordinal);
    }
}
