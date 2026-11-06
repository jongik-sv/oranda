using System.Text.Json;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class CopyJobJsonTests
{
    private static CopyJob Sample() => new()
    {
        Id = "0123456789abcdef0123456789abcdef",
        Name = "월간 복사",
        Source = new ConnectionRef("src.example", "SRC_USER", Guid.Parse("11111111-1111-1111-1111-111111111111")),
        Target = new ConnectionRef("dev.example", "TGT_USER"),
        SourceOwner = "SRC_USER",
        TargetOwner = "TGT_USER",
        Period = new CopyPeriod(PeriodKind.Custom, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)),
        MaskSalt = Convert.ToBase64String(new byte[32]),
        Options = new CopyOptions { BatchSize = 1000, Commit = CommitMode.PerBatch, DisableTriggersByDefault = true },
        Sequences = [new SequenceRule("SEQ_ORDER_ID", "ORDERS", "ORDER_ID")],
        LastRun = new LastRunInfo(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), CopyOutcome.Succeeded, 12),
        Tables =
        [
            new TableRule("CODE", RuleKind.All, TargetAction.MergeByPk),
            new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert)
            {
                PeriodColumns = [new PeriodColumn("ORDER_DT"), new PeriodColumn("SALE_YMD", "YYYYMMDD")],
                Masks = [new MaskRule("PHONE", MaskKind.KeepFirst, null, 3), new MaskRule("EMAIL", MaskKind.Hash)],
                SkipColumns = ["MEMO"],
            },
            new TableRule("ORDER_ITEM", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
            {
                Parent = new ParentLink("ORDERS", ["ORDER_ID"], ["ID"], "FK_ITEM_ORDER"),
            },
            new TableRule("AUDIT", RuleKind.Custom, TargetAction.AppendOnly) { CustomWhere = "CREATED_AT >= :FROM_DT" },
            new TableRule("TEMP_X", RuleKind.Exclude, TargetAction.DeleteRangeInsert),
        ],
    };

    [Fact]
    public void Round_trips_job_with_all_rule_kinds()
    {
        var job = Sample();
        var json = JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob);
        var back = JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob);
        Assert.Equivalent(job, back);
    }

    [Fact]
    public void Writes_camel_case_enum_strings_and_no_password_field()
    {
        var json = JsonSerializer.Serialize(Sample(), CopyJobJsonContext.Default.CopyJob);
        Assert.Contains("\"kind\": \"Period\"", json);
        Assert.Contains("\"action\": \"DeleteRangeInsert\"", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_action_follows_rule_kind()
    {
        Assert.Equal(TargetAction.MergeByPk, TargetActions.DefaultFor(RuleKind.All, true));
        Assert.Equal(TargetAction.DeleteAllInsert, TargetActions.DefaultFor(RuleKind.All, false));
        foreach (var pk in new[] { true, false })
        {
            Assert.Equal(TargetAction.DeleteRangeInsert, TargetActions.DefaultFor(RuleKind.Period, pk));
            Assert.Equal(TargetAction.DeleteRangeInsert, TargetActions.DefaultFor(RuleKind.ParentKey, pk));
            Assert.Equal(TargetAction.DeleteRangeInsert, TargetActions.DefaultFor(RuleKind.Custom, pk));
        }
    }
}
