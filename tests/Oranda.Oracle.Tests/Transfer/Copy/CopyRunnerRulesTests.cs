using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

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
}
