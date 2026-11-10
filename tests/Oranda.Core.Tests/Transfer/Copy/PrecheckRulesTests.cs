using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

// 소유: 데이터 복사 Task 8. 사전 점검의 순수 판정(DB 없음).

public sealed class PrecheckRulesTests
{
    private static readonly TableRule Rule = new("ORDERS", RuleKind.All, TargetAction.MergeByPk);

    private static readonly IBulkValueCodec Codec = new ScalarOnlyCodec();

    private static ColumnInfo Col(string name, string type, int position = 1) => new(name, position, type);

    private static TableInfo Table(params ColumnInfo[] columns) => new(new DbObjectRef("APP", "ORDERS", "TABLE"), columns);

    private static TableInfo WithPk(TableInfo table, params string[] columns)
        => table with { Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, columns)] };

    [Fact]
    public void Source_column_missing_in_target_is_confirm_and_skipped()
    {
        var source = Table(Col("ID", "NUMBER"), Col("MEMO", "VARCHAR2"));
        var target = Table(Col("ID", "NUMBER"));

        var items = PrecheckRules.CompareColumns(Codec, source, target, Rule);

        var item = Assert.Single(items);
        Assert.Equal("ColumnNotInTarget", item.Code);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("MEMO", item.Column);
        Assert.Equal(["ID"], PrecheckRules.SelectColumns(Codec, source, target, Rule));
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

        var items = PrecheckRules.CompareColumns(Codec, source, target, Rule);

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

        var items = PrecheckRules.CompareColumns(Codec, source, target, Rule);

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

        var item = Assert.Single(PrecheckRules.CompareColumns(Codec, source, target, Rule));

        Assert.Equal("UnsupportedType", item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Equal("DOC", item.Column);
        Assert.Contains(CheckChoice.SkipColumn, item.Choices);
        Assert.Equal(["ID"], PrecheckRules.SelectColumns(Codec, source, target, Rule));

        // 건너뛰기로 정하면 오류가 사라진다.
        Assert.Empty(PrecheckRules.CompareColumns(Codec, source, target, Rule with { SkipColumns = ["DOC"] }));
    }

    [Fact]
    public void Identity_always_is_error_and_virtual_is_skipped_silently()
    {
        var source = Table(Col("ID", "NUMBER"), Col("TOTAL", "NUMBER", 2) with { IsVirtual = true });
        var target = Table(
            Col("ID", "NUMBER") with { IsIdentity = true, IsNullable = false },
            Col("TOTAL", "NUMBER", 2) with { IsVirtual = true });

        var items = PrecheckRules.CompareColumns(Codec, source, target, Rule);

        var item = Assert.Single(items);
        Assert.Equal("IdentityAlways", item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Equal("ID", item.Column);
        Assert.DoesNotContain(items, i => i.Column == "TOTAL");
        Assert.DoesNotContain("TOTAL", PrecheckRules.SelectColumns(Codec, source, target, Rule));
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

    // ---- 규칙마다의 계정(묶음 B) ----

    private static CopyJob Job(params TableRule[] rules) => new()
    {
        Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
        SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = rules,
    };

    [Fact]
    public void Other_owner_targets_lists_accounts_tables_and_deletes()
    {
        var job = Job(
            new TableRule("A", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("B", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP", TargetOwner = "APP" },
            new TableRule("C", RuleKind.Period, TargetAction.DeleteRangeInsert) { SourceOwner = "APP2", TargetOwner = "APP2" },
            new TableRule("D", RuleKind.Exclude, TargetAction.DeleteAllInsert) { SourceOwner = "Z", TargetOwner = "Z" });

        Assert.Null(PrecheckRules.OtherOwnerTargets(Job(new TableRule("A", RuleKind.All, TargetAction.DeleteAllInsert)), "dst"));

        var item = PrecheckRules.OtherOwnerTargets(job, "DST");
        Assert.NotNull(item);
        Assert.Equal(CopyCheckCodes.OtherOwnerTargets, item.Code);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Null(item.Table);
        Assert.Contains("대상 접속 계정(DST)이 아닌 계정 APP, APP2 의 테이블 2개에 씁니다(그중 1개는 지우고 넣습니다)", item.Message, StringComparison.Ordinal);
        Assert.Contains("APP2.C", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Z.D", item.Message, StringComparison.Ordinal);

        // 쓰기 계정(W)으로 접속하면 기본 대상 계정 표도 남의 계정이다.
        Assert.Contains("DST, APP, APP2", PrecheckRules.OtherOwnerTargets(job, "W")!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_period_blocks_only_jobs_with_period_rules()
    {
        var periodRule = new TableRule("A", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("D")] };
        var day = new DateTime(2026, 11, 1);
        var job = Job(periodRule) with { Period = new CopyPeriod(PeriodKind.Custom, day, day) };

        var item = PrecheckRules.EmptyPeriod(job, PeriodCalculator.Resolve(job.Period, day));
        Assert.NotNull(item);
        Assert.Equal(CopyCheckCodes.EmptyPeriod, item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Contains("끝 날짜 포함", item.Message, StringComparison.Ordinal);

        var inclusive = job with { Period = job.Period with { EndInclusive = true } };
        Assert.Null(PrecheckRules.EmptyPeriod(inclusive, PeriodCalculator.Resolve(inclusive.Period, day)));

        var noPeriodRule = Job(new TableRule("A", RuleKind.All, TargetAction.DeleteAllInsert)) with { Period = job.Period };
        Assert.Null(PrecheckRules.EmptyPeriod(noPeriodRule, new ResolvedPeriod(day, day)));
        Assert.NotNull(PrecheckRules.EmptyPeriod(job, new ResolvedPeriod(day.AddDays(1), day)));
        Assert.Null(PrecheckRules.EmptyPeriod(job, new ResolvedPeriod(null, null)));
    }

    [Fact]
    public void Parent_link_masks_find_the_parent_by_owner()
    {
        var parent = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP2", Masks = [new MaskRule("ID", MaskKind.Hash)] };
        var sameName = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { Masks = [new MaskRule("ID", MaskKind.Hash)] };
        var child = new TableRule("ITEM", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            Parent = new ParentLink("ORDERS", ["ORDER_ID"], ["ID"]) { ParentOwner = "APP2" },
        };

        var item = Assert.Single(PrecheckRules.CheckParentLinkMasks(Job(parent, sameName, child)));
        Assert.Equal("APP2.ORDERS", item.Table);
        Assert.Equal("MaskOnKey", item.Code);
    }
}

/// <summary>
/// 판정 시험용 코덱: 이 시험이 쓰는 형식 가운데 NUMBER·VARCHAR2 만 지원(XMLTYPE 등은 미지원)으로 본다. 값 해석은 쓰지 않는다.
/// Oracle 코덱(<c>OracleBulkValueCodec</c>)의 지원 목록은 Oracle.Tests 가 따로 본다.
/// </summary>
internal sealed class ScalarOnlyCodec : IBulkValueCodec
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { "NUMBER", "VARCHAR2" };

    public bool IsSupported(string serverType) => Supported.Contains(serverType);

    public BulkKind KindOf(string serverType) => string.Equals(serverType, "VARCHAR2", StringComparison.OrdinalIgnoreCase) ? BulkKind.Text : BulkKind.Other;

    public BindDataType BindTypeOf(string serverType) => throw new NotSupportedException();

    public string? ToKeyText(object? value, string serverType) => throw new NotSupportedException();

    public object? FromKeyText(string? text, string serverType) => throw new NotSupportedException();

    public long? ToInt64(object? value) => throw new NotSupportedException();

    public string? ToText(object? value) => throw new NotSupportedException();

    public string? NumberText(object? value) => throw new NotSupportedException();

    public bool IsZero(object? value) => throw new NotSupportedException();

    public long ToInt64Exact(object? value) => throw new NotSupportedException();
}
