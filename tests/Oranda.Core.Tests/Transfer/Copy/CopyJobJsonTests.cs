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
    public void End_inclusive_and_day_start_round_trip_and_defaults_are_not_written()
    {
        var job = Sample() with { Period = new CopyPeriod(PeriodKind.Custom, new DateTime(2026, 11, 1), new DateTime(2026, 11, 1), true, 7) };
        var json = JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob);
        var back = JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob)!.Period;
        Assert.True(back.EndInclusive);
        Assert.Equal(7, back.DayStartHour);

        // 기본값(미만·자정)은 쓰지 않아 예전 파일과 같고, 예전 파일은 기본값으로 읽는다.
        var old = JsonSerializer.Serialize(Sample(), CopyJobJsonContext.Default.CopyJob);
        Assert.DoesNotContain("endInclusive", old, StringComparison.Ordinal);
        Assert.DoesNotContain("dayStartHour", old, StringComparison.Ordinal);
        var oldPeriod = JsonSerializer.Deserialize(old, CopyJobJsonContext.Default.CopyJob)!.Period;
        Assert.False(oldPeriod.EndInclusive);
        Assert.Equal(0, oldPeriod.DayStartHour);
    }

    [Fact]
    public void Extra_where_round_trips_and_older_files_without_it_read_as_null()
    {
        var job = Sample() with
        {
            Tables = [new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert)
                      { PeriodColumns = [new PeriodColumn("ORDER_DT")], ExtraWhere = "STATUS = 'DONE'" }],
        };
        var json = JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob);
        Assert.Contains("\"extraWhere\": \"STATUS = 'DONE'\"", json.Replace("\\u0027", "'", StringComparison.Ordinal));
        Assert.Equal("STATUS = 'DONE'", JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob)!.Tables[0].ExtraWhere);

        // 예전 파일(속성 없음): 추가 조건 없이 읽고, 직접 조건(customWhere)이 남아 있어도 기간 규칙에 붙이지 않는다.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var table = node["tables"]![0]!.AsObject();
        table.Remove("extraWhere");
        table["customWhere"] = "LEFTOVER = 1";
        var old = JsonSerializer.Deserialize(node.ToJsonString(), CopyJobJsonContext.Default.CopyJob)!;
        Assert.Null(old.Tables[0].ExtraWhere);

        var plain = Sample() with { Tables = [new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_DT")] }] };
        Assert.DoesNotContain("extraWhere", JsonSerializer.Serialize(plain, CopyJobJsonContext.Default.CopyJob), StringComparison.Ordinal); // 없으면 쓰지 않는다
    }

    [Fact]
    public void Per_table_owners_round_trip_and_are_left_out_when_empty()
    {
        var job = Sample() with
        {
            Tables =
            [
                new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk) { SourceOwner = "APP2", TargetOwner = "APP2", WrittenAs = "ORDERS" },
                new TableRule("ITEM", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
                {
                    Parent = new ParentLink("ORDERS", ["ORDER_ID"], ["ID"]) { ParentOwner = "APP2" },
                },
            ],
            Sequences = [new SequenceRule("SEQ_ITEM", "ITEM", "ID") { Owner = "TGT_USER" }],
        };
        var json = JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob);
        Assert.Contains("\"sourceOwner\": \"APP2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"writtenAs\": \"ORDERS\"", json, StringComparison.Ordinal);
        Assert.Contains("\"parentOwner\": \"APP2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"owner\": \"TGT_USER\"", json, StringComparison.Ordinal);
        Assert.Equivalent(job, JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob));

        // 계정을 비운 규칙은 필드를 쓰지 않는다(작업 기본 계정).
        var plain = JsonSerializer.Serialize(Sample(), CopyJobJsonContext.Default.CopyJob);
        Assert.DoesNotContain("writtenAs", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("parentOwner", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_if_missing_is_written_only_when_on_and_older_files_read_as_off()
    {
        // 꺼짐(기본값)은 쓰지 않아 예전 파일과 같다.
        var off = JsonSerializer.Serialize(Sample(), CopyJobJsonContext.Default.CopyJob);
        Assert.DoesNotContain("createIfMissing", off, StringComparison.Ordinal);
        Assert.All(JsonSerializer.Deserialize(off, CopyJobJsonContext.Default.CopyJob)!.Tables, r => Assert.False(r.CreateIfMissing));

        // 켜짐은 쓰고 읽으면 그대로다.
        var on = Sample() with
        {
            Tables = [new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true },
                      new TableRule("CODE", RuleKind.All, TargetAction.MergeByPk)],
        };
        var json = JsonSerializer.Serialize(on, CopyJobJsonContext.Default.CopyJob);
        Assert.Contains("\"createIfMissing\": true", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob)!;
        Assert.True(back.Tables[0].CreateIfMissing);
        Assert.False(back.Tables[1].CreateIfMissing);
        Assert.Equivalent(on, back);
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

    /// <summary>
    /// 스키마 버전을 올리지 않은 새 옵션(PF1: waitLimitMinutes·serialReadWrite, 결정 77)의 조건: 그 칸을 모르는 읽기기(스키마 5 판 앱)가
    /// 새 키가 섞인 파일을 오류 없이 열고 무시한다. 같은 직렬화 문맥이 모르는 키를 건너뛰는지로 본다 — 옛 판에는 이 두 속성이 없으므로
    /// 지금 읽기기에 처음 보는 키를 같은 자리(options·최상위)에 섞어 같은 처지를 만든다.
    /// </summary>
    [Fact]
    public void A_file_with_option_keys_the_reader_does_not_know_opens_and_ignores_them()
    {
        var job = Sample() with { Options = Sample().Options with { WaitLimitMinutes = 3, SerialReadWrite = true } };
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob))!;
        Assert.Equal(3, (int)node["options"]!["waitLimitMinutes"]!);
        Assert.True((bool)node["options"]!["serialReadWrite"]!);

        // 옛 판 처지: 읽기기가 모르는 키(앞으로 생길 옵션)가 options 와 최상위에 있다.
        node["options"]!["optionFromANewerApp"] = 7;
        node["fieldFromANewerApp"] = "x";
        var back = JsonSerializer.Deserialize(node.ToJsonString(), CopyJobJsonContext.Default.CopyJob);

        Assert.NotNull(back);
        Assert.Equal(job.Options, back!.Options);
        Assert.Equal(job.Tables.Count, back.Tables.Count);
    }
}
