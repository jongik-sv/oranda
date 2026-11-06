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
    public void Map_target_keeps_job_mapping_for_default_owner_and_owner_name_otherwise()
    {
        var job = Job();
        Assert.Equal("DST", CopyOwners.MapTarget(job, "SRC"));
        Assert.Equal("APP", CopyOwners.MapTarget(job, "APP"));
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
