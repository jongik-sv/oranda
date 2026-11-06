using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>규칙마다의 계정(묶음 B): 빈 계정 = 작업 기본 계정, 규칙 키, 대상 계정 기본값(B-1 a), 규칙 사이 모순.</summary>
public class CopyOwnersTests
{
    private static CopyJob Job(params TableRule[] rules) => new()
    {
        Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
        SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = rules,
    };

    [Fact]
    public void Empty_owners_fall_back_to_job_defaults_and_key_is_plain_name()
    {
        var rule = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk);
        var job = Job(rule);

        Assert.Equal("SRC", CopyOwners.Source(job, rule));
        Assert.Equal("DST", CopyOwners.Target(job, rule));
        Assert.Equal("ORDERS", CopyOwners.Key(job, rule));
        Assert.Equal("SRC.ORDERS", CopyOwners.SourceName(job, rule));
        Assert.Equal("DST.ORDERS", CopyOwners.TargetName(job, rule));
        Assert.Same(rule, CopyOwners.Find(job, "ORDERS"));
    }

    [Fact]
    public void Other_source_owner_gets_qualified_key_and_same_named_tables_stay_apart()
    {
        var mine = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "SRC" };
        var other = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP2", TargetOwner = "APP2" };
        var job = Job(mine, other);

        Assert.Equal("ORDERS", CopyOwners.Key(job, mine));
        Assert.Equal("APP2.ORDERS", CopyOwners.Key(job, other));
        Assert.Same(other, CopyOwners.Find(job, "APP2.ORDERS"));
        Assert.Same(other, CopyOwners.FindByTarget(job, "APP2", "ORDERS"));
        Assert.Same(mine, CopyOwners.FindBySource(job, "SRC", "ORDERS"));
        Assert.Empty(CopyOwners.Problems(job));
    }

    [Fact]
    public void Dotted_table_of_default_owner_and_table_of_owner_named_like_its_prefix_get_different_keys()
    {
        // 기본 계정(SRC)의 표 "X.Y" 와 계정 X 의 표 Y: 예전에는 둘 다 X.Y 였다.
        var dotted = new TableRule("X.Y", RuleKind.All, TargetAction.MergeByPk);
        var owned = new TableRule("Y", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "X", TargetOwner = "XT" };
        var both = new TableRule("A.B", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "X", TargetOwner = "XT" };
        var job = Job(dotted, owned, both);

        Assert.Equal("\"X.Y\"", CopyOwners.Key(job, dotted));
        Assert.Equal("X.Y", CopyOwners.Key(job, owned));
        Assert.Equal("X.\"A.B\"", CopyOwners.Key(job, both));
        Assert.Same(dotted, CopyOwners.Find(job, "\"X.Y\""));
        Assert.Same(owned, CopyOwners.Find(job, "X.Y"));
        Assert.Same(both, CopyOwners.Find(job, "X.\"A.B\""));
        Assert.Same(dotted, CopyOwners.FindBySource(job, "SRC", "X.Y"));
        Assert.Empty(CopyOwners.Problems(job));

        Assert.Equal((null, "X.Y"), CopyOwners.SplitKey("\"X.Y\""));
        Assert.Equal(("X", "Y"), CopyOwners.SplitKey("X.Y"));
        Assert.Equal(("X", "A.B"), CopyOwners.SplitKey("X.\"A.B\""));
        Assert.Equal((null, "ORDERS"), CopyOwners.SplitKey("ORDERS"));
        Assert.Equal((null, "Q\"T"), CopyOwners.SplitKey(CopyOwners.Key(job, "SRC", "Q\"T")));
        Assert.Null(CopyOwners.SplitKey("\"X.Y"));
        Assert.Null(CopyOwners.SplitKey("A.B.C"));
        Assert.Null(CopyOwners.SplitKey(""));
    }

    [Fact]
    public void Parent_with_dotted_name_finds_the_right_rule()
    {
        var dottedParent = new TableRule("X.Y", RuleKind.All, TargetAction.MergeByPk) { TargetOwner = "DST" };
        var ownerParent = new TableRule("Y", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "X", TargetOwner = "XT" };
        var childOfDotted = new TableRule("C1", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            Parent = new ParentLink("X.Y", ["P"], ["ID"]),
        };
        var childOfOwner = new TableRule("C2", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            Parent = new ParentLink("Y", ["P"], ["ID"]) { ParentOwner = "X" },
        };
        var job = Job(dottedParent, ownerParent, childOfDotted, childOfOwner);

        Assert.Same(dottedParent, CopyOwners.Parent(job, childOfDotted));
        Assert.Same(ownerParent, CopyOwners.Parent(job, childOfOwner));
        Assert.Equal("DST", CopyOwners.ParentTarget(job, childOfDotted));
        Assert.Equal("XT", CopyOwners.ParentTarget(job, childOfOwner));

        // 문장도 맞는 부모 규칙의 계정으로 만든다.
        var builder = new CopySqlBuilder(job, new ResolvedPeriod(null, null), new Dictionary<string, Oranda.Core.Metadata.TableInfo>(), new Dictionary<string, Oranda.Core.Metadata.TableInfo>());
        Assert.Contains("FROM \"DST\".\"X.Y\"", builder.TargetDelete(CopyOwners.Key(job, childOfDotted))!.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"XT\".\"Y\"", builder.TargetDelete(CopyOwners.Key(job, childOfOwner))!.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_target_check_does_not_mix_owner_dots_with_table_dots()
    {
        // 대상 "X.Y"."Z" 와 대상 "X"."Y.Z" 는 다른 표다.
        var a = new TableRule("Z", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "S1", TargetOwner = "X.Y" };
        var b = new TableRule("Y.Z", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "S2", TargetOwner = "X" };
        Assert.Empty(CopyOwners.Problems(Job(a, b)));
        Assert.NotEqual(CopyOwners.QualifiedKey("X.Y", "Z"), CopyOwners.QualifiedKey("X", "Y.Z"));
    }

    [Fact]
    public void Map_target_keeps_job_mapping_for_default_owner_and_owner_name_otherwise()
    {
        var job = Job();
        Assert.Equal("DST", CopyOwners.MapTarget(job, "SRC"));
        Assert.Equal("APP", CopyOwners.MapTarget(job, "APP"));
    }

    [Fact]
    public void Missing_target_owner_of_another_owner_rule_maps_to_its_source_owner()
    {
        // 대상 계정을 적지 않은 다른 계정 규칙: 작업 기본 대상 계정(DST)이 아니라 원본 계정 그대로(MapTarget, B-1 a).
        var other = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP2" };
        var mine = new TableRule("ITEMS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "SRC" };
        var job = Job(other, mine);

        Assert.Equal("APP2", CopyOwners.Target(job, other));
        Assert.Equal("APP2.ORDERS", CopyOwners.TargetName(job, other));
        Assert.Equal("DST", CopyOwners.Target(job, mine));
    }

    [Fact]
    public void Parent_target_is_the_same_whether_an_excluded_parent_rule_without_target_owner_stays_or_goes()
    {
        var child = new TableRule("LINES", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            SourceOwner = "SRC",
            Parent = new ParentLink("HEADERS", ["H_ID"], ["ID"]) { ParentOwner = "APP" },
        };
        var excludedParent = new TableRule("HEADERS", RuleKind.Exclude, TargetAction.MergeByPk) { SourceOwner = "APP" };

        Assert.Equal("APP", CopyOwners.ParentTarget(Job(child, excludedParent), child));
        Assert.Equal("APP", CopyOwners.ParentTarget(Job(child), child));
    }

    [Fact]
    public void Parent_owner_defaults_to_child_source_and_target_follows_parent_rule()
    {
        var parent = new TableRule("CUSTOMERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP", TargetOwner = "APPX" };
        var child = new TableRule("ORDERS", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            SourceOwner = "APP2",
            Parent = new ParentLink("CUSTOMERS", ["CUST_ID"], ["ID"]) { ParentOwner = "APP" },
        };
        var outside = new TableRule("LINES", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            SourceOwner = "APP2",
            Parent = new ParentLink("HEADERS", ["H_ID"], ["ID"]),
        };
        var job = Job(parent, child, outside);

        Assert.Equal("APP.CUSTOMERS", CopyOwners.ParentKey(job, child));
        Assert.Same(parent, CopyOwners.Parent(job, child));
        Assert.Equal("APPX", CopyOwners.ParentTarget(job, child));

        // 부모가 작업 밖: 부모 원본 계정 = 자식 원본 계정, 대상은 MapTarget(다른 계정이면 이름 그대로).
        Assert.Equal("APP2", CopyOwners.ParentSource(job, outside));
        Assert.Null(CopyOwners.Parent(job, outside));
        Assert.Equal("APP2", CopyOwners.ParentTarget(job, outside));
    }

    [Fact]
    public void Sequence_owner_defaults_to_its_table_rule_target()
    {
        var rule = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP2", TargetOwner = "APP2" };
        var job = Job(rule);
        Assert.Equal("APP2", CopyOwners.SequenceOwner(job, new SequenceRule("S", "APP2.ORDERS", "ID")));
        Assert.Equal("X", CopyOwners.SequenceOwner(job, new SequenceRule("S", "APP2.ORDERS", "ID") { Owner = "X" }));
        Assert.Equal("DST", CopyOwners.SequenceOwner(job, new SequenceRule("S", "NONE", "ID")));
    }

    [Fact]
    public void Two_rules_for_one_source_or_one_target_table_are_problems()
    {
        var a = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk);
        var b = new TableRule("ORDERS", RuleKind.Exclude, TargetAction.MergeByPk) { SourceOwner = "SRC" };
        Assert.Single(CopyOwners.Problems(Job(a, b)), p => p.Contains("같은 원본 표", StringComparison.Ordinal));

        // 서로 다른 원본 표 둘이 같은 대상 표에 쓴다: 한쪽 DELETE 가 다른 쪽 행을 지운다.
        var c = new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP2", TargetOwner = "DST" };
        var problem = Assert.Single(CopyOwners.Problems(Job(a, c)));
        Assert.Contains("DST.ORDERS", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Target_that_another_rule_reads_is_reported_for_same_database()
    {
        // APP.CUSTOMERS → APP.CUSTOMERS(기본 매핑 그대로) 와, 다른 규칙이 읽는 SRC.CODES 에 쓰는 규칙.
        var self = new TableRule("CUSTOMERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP", TargetOwner = "APP" };
        var codes = new TableRule("CODES", RuleKind.All, TargetAction.MergeByPk);
        var intoSource = new TableRule("CODES", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = "OTHER", TargetOwner = "SRC" };
        var parentRead = new TableRule("ITEMS", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            Parent = new ParentLink("HEAD", ["H"], ["ID"]) { ParentOwner = "APP9" },
        };
        var intoParent = new TableRule("HEAD", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = "Q", TargetOwner = "APP9" };
        var excluded = new TableRule("X", RuleKind.Exclude, TargetAction.DeleteAllInsert) { SourceOwner = "Z", TargetOwner = "Z" };
        var job = Job(self, codes, intoSource, parentRead, intoParent, excluded);

        var hits = CopyOwners.TargetsReadAsSource(job);
        Assert.Equal(["APP.CUSTOMERS", "OTHER.CODES", "Q.HEAD"], hits.Select(h => h.Key));
        Assert.Equal(["APP.CUSTOMERS", "SRC.CODES", "APP9.HEAD"], hits.Select(h => h.Table));
    }

    [Fact]
    public void Other_target_owners_skip_session_user_and_excluded_rules()
    {
        var job = Job(
            new TableRule("A", RuleKind.All, TargetAction.MergeByPk),
            new TableRule("B", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP", TargetOwner = "APP" },
            new TableRule("C", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP", TargetOwner = "app" },
            new TableRule("D", RuleKind.Exclude, TargetAction.MergeByPk) { SourceOwner = "Z", TargetOwner = "Z" });
        Assert.Equal(["APP"], CopyOwners.OtherTargetOwners(job, "dst"));
        Assert.Equal(["DST"], CopyOwners.OtherTargetOwners(job, "APP"));
    }
}
