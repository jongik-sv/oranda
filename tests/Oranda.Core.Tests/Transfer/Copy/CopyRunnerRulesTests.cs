using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

// 소유: 데이터 복사 Task 9. 실행기의 순수 판정: 오류 문구에서 행 값 지우기, 사전 점검 뒤 규칙 변경 감지.
public class CopyRunnerRulesTests
{
    [Fact]
    public void Safe_error_drops_row_values_but_keeps_names()
    {
        Assert.Equal(
            "ORA-01722: unable to convert string value containing '…' to a number: \"CP_DST\".\"T\".\"AMT\"",
            CopyRunner.SafeError("ORA-01722: unable to convert string value containing 'secret-123' to a number: \"CP_DST\".\"T\".\"AMT\"\nHelp: https://docs.oracle.com/error-help/db/ora-01722/"));
        Assert.Equal(
            "ORA-00001: unique constraint (\"CP_DST\".\"PK_T\") violated on table \"CP_DST\".\"T\" columns (\"ID\") row with column values (…)",
            CopyRunner.SafeError("ORA-00001: unique constraint (\"CP_DST\".\"PK_T\") violated on table \"CP_DST\".\"T\" columns (\"ID\") row with column values (ID:42, NAME:'kim')"));
        Assert.Equal("ORA-02290: check constraint (CP_DST.CK_T) violated", CopyRunner.SafeError("ORA-02290: check constraint (CP_DST.CK_T) violated\r\nmore"));
    }

    private static ServerInfo Server(string? host, string? instance, bool rac = false) =>
        new("19.0.0.0.0", instance, host, "PDB1", "ORCL", "PDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), DbUniqueName: "ORCL", IsRac: rac);

    [Theory]
    [InlineData("db1.example.com", "ORCL", "db1.example.com", "ORCL", "APP", "APP", false, true)]
    [InlineData("db1.example.com", "ORCL", "db1.example.com", "ORCL", "APP", "app", false, true)]
    [InlineData("db1.example.com", "ORCL", "db1.example.com", "ORCL", "APP", "APP_DEV", false, false)]
    [InlineData("db1.example.com", "ORCL", "db2.example.com", "ORCL", "APP", "APP", false, false)]
    [InlineData("db1.example.com", "ORCL1", "db2.example.com", "ORCL2", "APP", "APP", true, true)]
    [InlineData(null, null, "db2.example.com", "ORCL", "APP", "APP", false, true)]
    public void Same_database_and_owner_uses_names_host_and_instance(string? sourceHost, string? sourceInstance, string? targetHost,
                                                                     string? targetInstance, string sourceOwner, string targetOwner,
                                                                     bool rac, bool expected)
    {
        // 이름이 같은 복제 DB·기본 이름 컨테이너(호스트가 다름)는 다른 DB 로 본다. RAC 는 노드마다 호스트가 달라 이름만 본다.
        var same = CopyRunner.IsSameDatabaseAndOwner("ORCL/PDB1/APP_R", Server(sourceHost, sourceInstance, rac),
                                                     "orcl/pdb1/APP_W", Server(targetHost, targetInstance, rac), sourceOwner, targetOwner);
        Assert.Equal(expected, same);
        Assert.False(CopyRunner.IsSameDatabaseAndOwner("ORCL/PDB1/APP_R", Server("h", "i"), "ORCL/PDB2/APP_W", Server("h", "i"), "APP", "APP"));
    }

    [Fact]
    public void Same_database_ignores_owner_names()
    {
        Assert.True(CopyRunner.IsSameDatabase("ORCL/PDB1/APP_R", Server("h", "i"), "orcl/pdb1/OTHER", Server("h", "i")));
        Assert.False(CopyRunner.IsSameDatabase("ORCL/PDB1/APP_R", Server("h", "i"), "ORCL/PDB2/APP_R", Server("h", "i")));
        Assert.False(CopyRunner.IsSameDatabase("ORCL/PDB1/APP_R", Server("h1", "i"), "ORCL/PDB1/APP_R", Server("h2", "i")));
    }

    // ---- 점검 보고서와 작업이 같은가(EnsureReportMatchesJob) ----

    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    private static CopyJob ReportJob(params TableRule[] rules) => new()
    {
        Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
        SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = rules,
    };

    private static TableInfo Info(string owner, string table)
        => new(new DbObjectRef(owner, table, "TABLE"), [new ColumnInfo("ID", 1, "NUMBER"), new ColumnInfo("P", 2, "NUMBER")]);

    /// <summary>사전 점검이 낼 보고서처럼: 제외 아닌 규칙마다 계획 하나, 대상 DELETE 미리 보기.</summary>
    private static PrecheckReport ReportFor(CopyJob job)
    {
        var order = job.Tables.Where(r => r.Kind != RuleKind.Exclude)
            .Select(r => new CopyTablePlan(r, Info(CopyOwners.Source(job, r), r.Table), Info(CopyOwners.Target(job, r), r.Table), ["ID", "P"], ["ID"]))
            .ToList();
        var builder = new CopySqlBuilder(job, NoPeriod,
            order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Source, StringComparer.Ordinal),
            order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Target, StringComparer.Ordinal));
        var preview = order.Select(p => builder.TargetDelete(CopyOwners.Key(job, p.Rule))).OfType<SqlWithBinds>()
            .Select(d => new PreviewStatement(d.Sql, PreviewSide.Target, PolicyDecisionKind.Allow, null, null)).ToList();
        return new PrecheckReport([], order, preview, new Dictionary<string, long>());
    }

    [Fact]
    public void Report_of_the_same_job_passes()
    {
        var job = ReportJob(new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("CUSTOMERS", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = "APP", TargetOwner = "APP2" });
        CopyRunner.EnsureReportMatchesJob(job, ReportFor(job), NoPeriod);
    }

    [Fact]
    public void Default_owner_change_after_precheck_is_refused_by_the_owner_check()
    {
        // 규칙은 그대로(계정 비움)인데 작업 기본 대상 계정이 바뀌었다: 점검한 대상 표 계정과 다르다.
        var job = ReportJob(new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk));
        var report = ReportFor(job);

        var ex = Assert.Throws<InvalidOperationException>(() => CopyRunner.EnsureReportMatchesJob(job with { TargetOwner = "DST2" }, report, NoPeriod));
        Assert.Contains("소유자가 바뀌었습니다", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_with_one_rule_planned_twice_is_refused()
    {
        // 계획 수가 규칙 키 수와 다르다(같은 키 두 번): 규칙 수만 맞춰서는 지나가지 않는다.
        var job = ReportJob(new TableRule("ORDERS", RuleKind.All, TargetAction.MergeByPk));
        var report = ReportFor(job);
        var doubled = report with { Order = [report.Order[0], report.Order[0]] };

        var ex = Assert.Throws<InvalidOperationException>(() => CopyRunner.EnsureReportMatchesJob(job, doubled, NoPeriod));
        Assert.Contains("규칙이 바뀌었습니다", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Excluded_parent_target_owner_change_after_precheck_is_refused_by_the_delete_text()
    {
        // 제외 규칙은 비교 대상(SameRule)이 아니지만, 그 대상 계정이 자식의 지우기 범위(부모 표)를 정한다.
        var child = new TableRule("ITEMS", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("CODES", ["P"], ["ID"]) };
        var parent = new TableRule("CODES", RuleKind.Exclude, TargetAction.MergeByPk) { TargetOwner = "DST" };
        var job = ReportJob(child, parent);
        var report = ReportFor(job);
        var changed = job with { Tables = [child, parent with { TargetOwner = "DST2" }] };

        var ex = Assert.Throws<InvalidOperationException>(() => CopyRunner.EnsureReportMatchesJob(changed, report, NoPeriod));
        Assert.Contains("지우기 문장이 바뀌었습니다", ex.Message, StringComparison.Ordinal);

        // 「실패 행만 다시 넣기」(기간 없음)는 지우지 않으므로 DELETE 글을 비교하지 않는다.
        CopyRunner.EnsureReportMatchesJob(changed, report, period: null);
    }

    [Fact]
    public void Outside_parent_target_change_after_precheck_is_refused_by_the_delete_text()
    {
        // 규칙 계정을 모두 적어 둔 작업(버전 1 변환·Rebase 뒤)에서 기본 계정만 바뀌었다: 작업 밖 부모 APP.HEAD 의 대상 기본값(MapTarget)이
        // APP → APPX 로 바뀌어 자식 DELETE 의 부모 표가 달라진다. 규칙·계획 계정은 그대로라 앞 검사들은 지나간다.
        var child = new TableRule("LINES", RuleKind.ParentKey, TargetAction.DeleteRangeInsert)
        {
            SourceOwner = "SRC", TargetOwner = "DST", Parent = new ParentLink("HEAD", ["P"], ["ID"]) { ParentOwner = "APP" },
        };
        var job = ReportJob(child);
        var report = ReportFor(job);
        Assert.Contains("\"APP\".\"HEAD\"", report.Preview[0].Sql, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CopyRunner.EnsureReportMatchesJob(job with { SourceOwner = "APP", TargetOwner = "APPX" }, report, NoPeriod));
        Assert.Contains("지우기 문장이 바뀌었습니다", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_rule_compares_lists_by_content()
    {
        var a = new TableRule("T", RuleKind.Period, TargetAction.DeleteRangeInsert)
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT")],
            Masks = [new MaskRule("NAME", MaskKind.Hash)],
            SkipColumns = ["X"],
            Parent = new ParentLink("P", ["P_ID"], ["ID"]),
        };
        var copy = a with
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT")],
            Masks = [new MaskRule("NAME", MaskKind.Hash)],
            SkipColumns = ["X"],
            Parent = new ParentLink("P", ["P_ID"], ["ID"]),
        };

        Assert.True(CopyRunner.SameRule(a, copy));
        Assert.False(CopyRunner.SameRule(a, a with { Kind = RuleKind.Exclude }));
        Assert.False(CopyRunner.SameRule(a, a with { Masks = [] }));
        Assert.False(CopyRunner.SameRule(a, a with { Parent = new ParentLink("P", ["P_ID2"], ["ID"]) }));

        // 표마다의 계정(묶음 B): 원본·대상·부모 계정이 바뀌면 다른 규칙이다(지우는 표가 달라진다).
        Assert.False(CopyRunner.SameRule(a, a with { SourceOwner = "APP2" }));
        Assert.False(CopyRunner.SameRule(a, a with { TargetOwner = "APP2" }));
        Assert.False(CopyRunner.SameRule(a, a with { Parent = new ParentLink("P", ["P_ID"], ["ID"]) { ParentOwner = "APP" } }));
        Assert.True(CopyRunner.SameRule(a, a with { WrittenAs = "SYN" })); // 표시용 이름은 실행과 상관없다
    }

    [Fact]
    public void Same_rule_notices_a_create_if_missing_difference()
    {
        var a = new TableRule("T", RuleKind.All, TargetAction.DeleteAllInsert);
        Assert.True(CopyRunner.SameRule(a, a with { }));
        Assert.False(CopyRunner.SameRule(a, a with { CreateIfMissing = true }));
        Assert.False(CopyRunner.SameRule(a with { CreateIfMissing = true }, a));
        Assert.True(CopyRunner.SameRule(a with { CreateIfMissing = true }, a with { CreateIfMissing = true }));
    }

    /// <summary>첫 계획을 「대상에 없어 만들 표」로 바꾼 보고서: 새 표라 DELETE 미리 보기가 없다.</summary>
    private static PrecheckReport CreatingReport(CopyJob job)
    {
        var report = ReportFor(job);
        return report with
        {
            Order = [report.Order[0] with { CreateMissing = true }, .. report.Order.Skip(1)],
            Preview = report.Preview.Where(x => !x.Sql.Contains("\"ORDERS\"", StringComparison.Ordinal)).ToList(),
        };
    }

    [Fact]
    public void Create_missing_plan_passes_without_a_delete_preview_but_a_normal_plan_still_needs_it()
    {
        var job = ReportJob(new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true },
            new TableRule("CUSTOMERS", RuleKind.All, TargetAction.DeleteAllInsert));
        var creating = CreatingReport(job);
        Assert.DoesNotContain(creating.Preview, x => x.Sql.Contains("\"ORDERS\"", StringComparison.Ordinal));
        Assert.Contains(creating.Preview, x => x.Sql.Contains("\"CUSTOMERS\"", StringComparison.Ordinal)); // 있는 표의 DELETE 는 남는다
        CopyRunner.EnsureReportMatchesJob(job, creating, NoPeriod);

        // 만들지 않는 계획(일반)에서 같은 DELETE 미리 보기가 빠졌으면 여전히 던진다.
        var normal = creating with { Order = creating.Order.Select(p => p with { CreateMissing = false }).ToList() };
        var ex = Assert.Throws<InvalidOperationException>(() => CopyRunner.EnsureReportMatchesJob(job, normal, NoPeriod));
        Assert.Contains("지우기 문장이 바뀌었습니다", ex.Message, StringComparison.Ordinal);

        // 만드는 표가 아닌 쪽의 DELETE 미리 보기가 빠져도 던진다.
        var missingOther = creating with { Preview = [] };
        Assert.Throws<InvalidOperationException>(() => CopyRunner.EnsureReportMatchesJob(job, missingOther, NoPeriod));
    }

    [Theory]
    [InlineData(false, TargetAction.DeleteAllInsert)] // 점검 뒤 「없으면 만들기」가 꺼졌다
    [InlineData(true, TargetAction.Recreate)]         // 점검 뒤 다시 만들기로 바뀌었다
    public void Create_missing_plan_is_refused_when_the_rule_no_longer_allows_it(bool createIfMissing, TargetAction action)
    {
        var planned = ReportJob(new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true });
        var report = CreatingReport(planned);
        var now = ReportJob(new TableRule("ORDERS", RuleKind.All, action) { CreateIfMissing = createIfMissing });

        // 계획이 든 규칙(점검할 때의 것)과 지금 작업이 같은 채로 어긋난 경우: 보고서 안의 규칙이 곧 지금 규칙이다.
        var stale = report with { Order = [report.Order[0] with { Rule = now.Tables[0] }] };
        var ex = Assert.Throws<InvalidOperationException>(() => CopyRunner.EnsureReportMatchesJob(now, stale, NoPeriod));
        Assert.Contains("없으면 만들기", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_if_missing_turned_off_after_precheck_is_refused_as_a_rule_change()
    {
        // 점검 때 켜져 있던 규칙이 지금 작업에서 꺼졌다(SameRule 이 CreateIfMissing 도 비교한다).
        var planned = ReportJob(new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true });
        var report = CreatingReport(planned);
        var now = ReportJob(new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert));

        var ex = Assert.Throws<InvalidOperationException>(() => CopyRunner.EnsureReportMatchesJob(now, report, NoPeriod));
        Assert.Contains("규칙이 바뀌었습니다", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disable_targets_turn_off_only_the_chosen_schemas_foreign_key_and_report_rule_keys()
    {
        // 같은 이름 FK(FK_ORD)가 두 계정에 있다: APP 쪽만 고르면 APP2 쪽 FK 는 그대로 켜져 있어야 한다.
        var job = ReportJob(
            new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = "APP2", TargetOwner = "APP2" });
        var app = new DisabledObject(DisabledKind.ForeignKey, "DST", "ITEMS", "FK_ORD");
        var app2 = new DisabledObject(DisabledKind.ForeignKey, "APP2", "ITEMS", "FK_ORD");
        var report = ReportFor(job);
        report = report with
        {
            Order =
            [
                report.Order[0] with { DisablableForeignKeys = [app], CanDisableTriggers = true, Triggers = ["ORD_TRG"] },
                report.Order[1] with { DisablableForeignKeys = [app2], CanDisableTriggers = true, Triggers = ["ORD_TRG"] },
            ],
        };
        var decisions = new CopyDecisions(new HashSet<string> { "APP2.ORDERS" }, new HashSet<ForeignKeyRef> { new("DST", "FK_ORD") },
            new HashSet<string>(), ProceedSequenceGap: false);

        var targets = CopyRunner.DisableTargets(report, decisions, p => CopyOwners.Key(job, p.Rule));

        Assert.Equal(2, targets.Count);
        var trigger = Assert.Single(targets, t => t.Object.Kind == DisabledKind.Trigger);
        Assert.Equal(("APP2", "ORD_TRG", "APP2.ORDERS"), (trigger.Object.Owner, trigger.Object.Name, trigger.Key)); // 진행 줄은 규칙 키
        var fk = Assert.Single(targets, t => t.Object.Kind == DisabledKind.ForeignKey);
        Assert.Same(app, fk.Object);
        Assert.Equal("ORDERS", fk.Key);

        // 결정이 FK 이름만 같고 계정이 다르면(대소문자도 글자 그대로) 끄지 않는다.
        var other = decisions with { DisableTriggerTables = new HashSet<string>(), DisableForeignKeys = new HashSet<ForeignKeyRef> { new("dst", "FK_ORD") } };
        Assert.Empty(CopyRunner.DisableTargets(report, other, p => CopyOwners.Key(job, p.Rule)));
    }
}
