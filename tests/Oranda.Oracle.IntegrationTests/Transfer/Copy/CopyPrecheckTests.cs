using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.IntegrationTests.Integration;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 Task 8. 사전 점검 서비스(26ai 컨테이너 하나 안의 시험 사용자 셋).

/// <summary>사전 점검: 권한·표 없음·운영 대상·자식 행 경고·문자열 날짜 길이·DELETE 미리 보기의 정책.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyPrecheckTests
{
    private const string Columns = "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)";

    private readonly OracleFreeFixture _db;

    public CopyPrecheckTests(OracleFreeFixture db) => _db = db;

    [Fact]
    public async Task Writer_without_owner_rights_passes_basic_checks_and_hides_disable_choices()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("ORD", Columns);
        await scope.CreateTargetTableAsync("ORD", Columns);
        await scope.ExecSourceAsync("INSERT INTO ORD VALUES (1, 'a')");
        await scope.ExecSourceAsync("COMMIT");
        await scope.ExecTargetAsync("CREATE TRIGGER ORD_TRG BEFORE INSERT ON ORD FOR EACH ROW BEGIN NULL; END;");
        await scope.GrantWriterAsync("ORD");
        var job = scope.Job(new TableRule("ORD", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync(asWriter: true, env: ConnectionEnvironment.Test);

        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(null, null), null, default);

        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        var plan = Assert.Single(report.Order);
        Assert.False(plan.CanDisableTriggers);
        Assert.Empty(plan.DisablableForeignKeys);
        Assert.Equal(["ID", "NAME"], plan.Columns);
        Assert.Equal(["ID"], plan.KeyColumns);
        Assert.Equal(1, report.EstimatedRows["ORD"]);

        // 끌 권한이 없으면 트리거·FK 를 끄는 선택지를 내지 않는다(설계 12장).
        Assert.DoesNotContain(report.Items, i => i.Choices.Contains(CheckChoice.DisableTriggers) || i.Choices.Contains(CheckChoice.DisableForeignKeys));
    }

    [Fact]
    public async Task Missing_target_table_and_missing_privilege_are_errors()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("GONE", Columns);
        await scope.CreateSourceTableAsync("LIMITED", Columns);
        await scope.CreateTargetTableAsync("LIMITED", Columns);

        // 쓰기 계정에 SELECT·INSERT 만 준다: 표는 보이지만 지우는 DELETE 와 PK 표의 MERGE 에 쓰는 UPDATE 는 없다.
        await scope.GrantWriterAsync("LIMITED", "SELECT", "INSERT");
        var job = scope.Job(
            new TableRule("GONE", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("LIMITED", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync(asWriter: true);

        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(null, null), null, default);

        Assert.True(report.HasErrors);
        var missing = Assert.Single(report.Items, i => i.Code == "TargetMissing");
        Assert.Equal("GONE", missing.Table);
        Assert.Equal(CheckLevel.Error, missing.Level);
        var noWrite = Assert.Single(report.Items, i => i.Code == "TargetNoWrite");
        Assert.Equal("LIMITED", noWrite.Table);
        Assert.Contains("DELETE", noWrite.Message, StringComparison.Ordinal);
        Assert.Equal(["LIMITED"], report.Order.Select(p => p.Rule.Table));
    }

    [Fact]
    public async Task Production_target_is_error()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("ORD", Columns);
        await scope.CreateTargetTableAsync("ORD", Columns);
        var job = scope.Job(new TableRule("ORD", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Production);

        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(null, null), null, default);

        // 운영 대상이면 이 오류 하나만 낸다(다른 조회도 하지 않는다).
        var item = Assert.Single(report.Items);
        Assert.Equal("TargetProduction", item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.True(report.HasErrors);
        Assert.Empty(report.Order);
        Assert.Empty(report.Preview);
        Assert.Empty(report.EstimatedRows);
    }

    [Fact]
    public async Task Child_rows_outside_job_warn_ora_02292()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("PARENT", Columns);
        await scope.CreateTargetTableAsync("PARENT", Columns);

        // 작업 밖 자식 표가 대상의 PARENT 를 참조한다.
        await scope.CreateTargetTableAsync("CHILD", "ID NUMBER PRIMARY KEY, PARENT_ID NUMBER REFERENCES PARENT (ID)");
        var job = scope.Job(new TableRule("PARENT", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);

        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(null, null), null, default);

        var item = Assert.Single(report.Items, i => i.Code == "ChildRowsMayBlock");
        Assert.Equal("PARENT", item.Table);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Contains(CheckChoice.SwitchToMerge, item.Choices);
        Assert.Contains("CHILD", item.Message, StringComparison.Ordinal);

        // 대상 소유자는 FK 를 끌 수 있으므로 그 선택지도 나온다.
        Assert.Contains(CheckChoice.DisableForeignKeys, item.Choices);
        var fk = Assert.Single(Assert.Single(report.Order).DisablableForeignKeys);
        Assert.Equal(DisabledKind.ForeignKey, fk.Kind);
        Assert.Equal(scope.DstUser, fk.Owner, ignoreCase: true);
        Assert.Equal("CHILD", fk.Table);
    }

    [Fact]
    public async Task Precheck_counts_wrong_length_string_dates()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        const string columns = "ID NUMBER PRIMARY KEY, LOG_DT VARCHAR2(8)";
        await scope.CreateSourceTableAsync("LOGS", columns);
        await scope.CreateTargetTableAsync("LOGS", columns);

        // "2026101"(7자)은 문자열 비교로 2026-10 범위 안에 든다.
        await scope.ExecSourceAsync("INSERT INTO LOGS VALUES (1, '20261001')");
        await scope.ExecSourceAsync("INSERT INTO LOGS VALUES (2, '20261015')");
        await scope.ExecSourceAsync("INSERT INTO LOGS VALUES (3, '2026101')");
        await scope.ExecSourceAsync("COMMIT");
        var rule = new TableRule("LOGS", RuleKind.Period, TargetAction.DeleteRangeInsert)
        {
            PeriodColumns = [new PeriodColumn("LOG_DT", "YYYYMMDD")],
        };
        var job = scope.Job(rule);
        var sessions = await scope.OpenSessionsAsync();

        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), null, default);

        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        var item = Assert.Single(report.Items, i => i.Code == "WrongLengthDates");
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("LOG_DT", item.Column);
        Assert.Contains("1행", item.Message, StringComparison.Ordinal);
        Assert.Equal(3, report.EstimatedRows["LOGS"]);
    }

    [Fact]
    public async Task Preview_lists_target_deletes_with_policy_decision()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        foreach (var table in new[] { "ALL_ROWS", "SOME_ROWS" })
        {
            await scope.CreateSourceTableAsync(table, Columns);
            await scope.CreateTargetTableAsync(table, Columns);
        }

        var job = scope.Job(
            new TableRule("ALL_ROWS", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("SOME_ROWS", RuleKind.Custom, TargetAction.DeleteRangeInsert) { CustomWhere = "ID > 0" });
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);

        var report = await scope.Precheck().RunAsync(sessions, job, new ResolvedPeriod(null, null), null, default);

        Assert.False(report.HasErrors);
        var targetDeletes = report.Preview.Where(p => p.Side == PreviewSide.Target && p.Sql.StartsWith("DELETE FROM", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, targetDeletes.Count);

        // WHERE 없는 DELETE(비우고 넣기)는 정책이 확인을 낸다. 조건이 있는 범위 DELETE 는 개발 접속에서 그냥 통과한다.
        var all = Assert.Single(targetDeletes, p => p.Sql.Contains("\"ALL_ROWS\"", StringComparison.Ordinal));
        Assert.Equal(PolicyDecisionKind.Confirm, all.Policy);
        Assert.NotNull(all.PolicyMessage);
        var some = Assert.Single(targetDeletes, p => p.Sql.Contains("\"SOME_ROWS\"", StringComparison.Ordinal));
        Assert.Equal(PolicyDecisionKind.Allow, some.Policy);

        // 원본 조회도 미리 보기에 있다.
        Assert.Equal(2, report.Preview.Count(p => p.Side == PreviewSide.Source));
    }
}
