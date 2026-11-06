using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 묶음 B. 표마다 계정·시노님 풀기(26ai 컨테이너 하나 안의 시험 사용자 SRC·APP·DST·W).

/// <summary>
/// 표마다의 계정(묶음 B): 시노님을 실제 계정·표로 풀기, 같은 DB 기본 매핑(APP→APP)은 SameSourceTarget, 쓰기 계정이 받은 권한만으로 다른 계정 표 쓰기,
/// 계정 넘는 FK 순서·다시 실행 같은 결과, 계정이 섞인 다시 만들기의 RecreateNoPrivilege.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyCrossOwnerTests
{
    private readonly OracleFreeFixture _db;

    public CopyCrossOwnerTests(OracleFreeFixture db) => _db = db;

    [Fact]
    public async Task Synonyms_resolve_to_the_real_owner_and_a_moved_synonym_is_reported()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await CrossOwnerCases.SynonymsResolveAsync(scope);

        // 저장한 시노님을 다른 표로 바꾸면 사전 점검이 확인 항목으로 알리고, 작업은 저장된 표를 그대로 쓴다.
        await scope.CreateTargetTableAsync("CUSTOMERS", CrossOwnerCases.CustomerColumns);
        await scope.CreateSourceTableAsync("OTHER_CUST", CrossOwnerCases.CustomerColumns);
        var job = scope.Job(new TableRule("CUSTOMERS", RuleKind.All, TargetAction.MergeByPk)
        {
            SourceOwner = scope.AppUser, TargetOwner = scope.DstUser, WrittenAs = "CUST",
        });
        var sessions = await scope.OpenSessionsAsync();

        var before = await scope.Precheck().RunAsync(sessions, job, CrossOwnerCases.NoPeriod, null, default);
        Assert.False(before.HasErrors, CrossOwnerCases.Errors(before));
        Assert.DoesNotContain(before.Items, i => i.Code == CopyCheckCodes.SynonymChanged);

        await scope.ExecSourceAsync("CREATE OR REPLACE SYNONYM CUST FOR OTHER_CUST");
        var after = await scope.Precheck().RunAsync(sessions, job, CrossOwnerCases.NoPeriod, null, default);
        var moved = Assert.Single(after.Items, i => i.Code == CopyCheckCodes.SynonymChanged);
        Assert.Equal(CheckLevel.Confirm, moved.Level);
        Assert.Contains($"{scope.SrcUser}.OTHER_CUST", moved.Message, StringComparison.Ordinal);
        Assert.Equal(scope.AppUser, Assert.Single(after.Order).Source.Table.Owner);
    }

    [Fact]
    public async Task Same_database_default_mapping_of_another_owner_is_same_source_target()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.ExecAppAsync($"CREATE TABLE CUSTOMERS ({CrossOwnerCases.CustomerColumns})");
        await scope.ExecAppAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON CUSTOMERS TO {scope.SrcUser}");
        await scope.ExecAppAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON CUSTOMERS TO {scope.DstUser}");
        await scope.ExecAppAsync("BEGIN INSERT INTO CUSTOMERS VALUES (1, 'keep'); COMMIT; END;");

        // 시노님으로 더한 다른 계정 표의 대상 기본값은 원본 계정 그대로(B-1 a): 같은 DB 면 원본 표를 지우게 된다.
        var probe = scope.Job();
        var rule = new TableRule("CUSTOMERS", RuleKind.All, TargetAction.DeleteAllInsert)
        {
            SourceOwner = scope.AppUser, TargetOwner = CopyOwners.MapTarget(probe, scope.AppUser),
        };
        Assert.Equal(scope.AppUser, rule.TargetOwner);
        var job = scope.Job(rule);
        var sessions = await scope.OpenSessionsAsync();

        var report = await scope.Precheck().RunAsync(sessions, job, CrossOwnerCases.NoPeriod, null, default);

        var item = Assert.Single(report.Items);
        Assert.Equal("SameSourceTarget", item.Code);
        Assert.Equal($"{scope.AppUser}.CUSTOMERS", item.Table);
        Assert.Empty(report.Order);

        // 실행기도 따로 막는다(점검 오류를 지운 보고서로도).
        await Assert.ThrowsAsync<InvalidOperationException>(() => CrossOwnerCases.NewRunner(scope).RunAsync(
            sessions, job, report with { Items = [] }, CrossOwnerCases.Decisions(report), CrossOwnerCases.NoPeriod, null, new CopyRunControl(), default));
        Assert.Equal(1, await scope.CountAsync(scope.AppUser, "CUSTOMERS")); // 원본 행은 그대로
    }

    [Fact]
    public async Task Writer_copies_into_another_owner_with_granted_rights_and_missing_delete_is_an_error()
        => await CrossOwnerCases.WriterOnOtherOwnerAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));

    [Fact]
    public async Task Two_owner_job_orders_by_cross_owner_fk_and_rerun_gives_the_same_result()
        => await CrossOwnerCases.TwoOwnerCopyAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));

    [Fact]
    public async Task Target_view_synonym_or_materialized_view_is_target_missing()
        => await CrossOwnerCases.TargetThatIsNotATableAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));

    [Fact]
    public async Task Rule_with_another_target_owner_copies_there_and_leaves_the_default_target_table_alone()
        => await CrossOwnerCases.OtherTargetOwnerLeavesDefaultTargetAloneAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));

    [Fact]
    public async Task Mixed_owner_recreate_needs_each_target_owner()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("ORD", CrossOwnerCases.CustomerColumns);
        await scope.CreateTargetTableAsync("ORD", CrossOwnerCases.CustomerColumns);
        await scope.CreateSourceTableAsync("CODES", CrossOwnerCases.CustomerColumns);
        await scope.ExecAppAsync($"CREATE TABLE CODES ({CrossOwnerCases.CustomerColumns})");
        await scope.ExecAppAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON CODES TO {scope.DstUser}");
        var job = scope.Job(
            new TableRule("ORD", RuleKind.All, TargetAction.Recreate),
            new TableRule("CODES", RuleKind.All, TargetAction.Recreate) { SourceOwner = scope.SrcUser, TargetOwner = scope.AppUser });
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);

        var report = await scope.Precheck().RunAsync(sessions, job, CrossOwnerCases.NoPeriod, null, default);

        // 표마다 본다: 접속 계정(DST)의 표는 다시 만들 수 있고, APP 의 표는 그 계정으로 접속해야 한다.
        var denied = Assert.Single(report.Items, i => i.Code == "RecreateNoPrivilege");
        Assert.Equal("CODES", denied.Table);
        Assert.Contains(scope.AppUser, denied.Message, StringComparison.Ordinal);
        Assert.Contains(report.Items, i => i.Code == "RecreateTable" && i.Table == "ORD");
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.OtherOwnerTargets && i.Message.Contains(scope.AppUser, StringComparison.Ordinal));

        // 실행기도 표마다 막고, 아무 표도 지우지 않는다.
        var forced = report with { Items = [] };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CrossOwnerCases.NewRunner(scope).RunAsync(
            sessions, job, forced, CrossOwnerCases.Decisions(forced), CrossOwnerCases.NoPeriod, null, new CopyRunControl(), default));
        Assert.Contains(scope.AppUser, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await scope.CountAsync(scope.DstUser, "ORD"));
        Assert.Equal(0, await scope.CountAsync(scope.AppUser, "CODES"));
    }
}

/// <summary>26ai·11.2 가 함께 쓰는 시험 몸통(<see cref="CopyCrossOwnerTests"/>, <see cref="CopyCrossOwner11gTests"/>).</summary>
internal static class CrossOwnerCases
{
    public const string CustomerColumns = "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)";

    public static readonly ResolvedPeriod NoPeriod = new(null, null);

    public static CopyRunner NewRunner(CopyDbScope scope)
        => new(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NullRunLog());

    /// <summary>화면이 「복사 실행」에서 하는 것처럼 미리 보기에서 확인이 필요한 문장을 미리 받은 확인으로 넘긴다.</summary>
    public static CopyDecisions Decisions(PrecheckReport report)
        => new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);

    public static string Errors(PrecheckReport report)
        => string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message));

    /// <summary>개인 시노님·PUBLIC 시노님·계정을 적은 이름이 모두 APP.CUSTOMERS 로 풀린다. 없는 이름은 더하지 않는다.</summary>
    public static async Task SynonymsResolveAsync(CopyDbScope scope)
    {
        await scope.ExecAppAsync($"CREATE TABLE CUSTOMERS ({CustomerColumns})");
        await scope.ExecAppAsync($"GRANT SELECT ON CUSTOMERS TO {scope.SrcUser}");
        await scope.ExecSourceAsync($"CREATE SYNONYM CUST FOR {scope.AppUser}.CUSTOMERS");
        var publicName = $"CP_{scope.Tag}_PUB";
        await scope.CreatePublicSynonymAsync(publicName, $"{scope.AppUser}.CUSTOMERS");
        var source = await scope.OpenSourceAsync();
        Task<CopyTableResolution> Resolve(string input) =>
            CopyTableInput.ResolveAsync((o, n) => scope.Metadata.GetNameStepAsync(source, o, n, default), input, scope.SrcUser, scope.SrcUser);

        var priv = await Resolve("cust");
        Assert.True(priv.Found, priv.Problem);
        Assert.Equal(new CopyTableName(scope.AppUser, "CUSTOMERS", "CUST"), priv.Table);

        var pub = await Resolve(publicName);
        Assert.True(pub.Found, pub.Problem);
        Assert.Equal(new CopyTableName(scope.AppUser, "CUSTOMERS", publicName), pub.Table);

        var written = await Resolve($"{scope.AppUser}.CUSTOMERS");
        Assert.Equal(new CopyTableName(scope.AppUser, "CUSTOMERS"), written.Table);

        // 계정을 적으면 PUBLIC 시노님을 보지 않는다.
        Assert.False((await Resolve($"{scope.DstUser}.{publicName}")).Found);
        Assert.False((await Resolve("NO_SUCH_TABLE_X")).Found);
    }

    /// <summary>
    /// 쓰기 계정 W 가 DST 표에 SELECT·INSERT·DELETE 만 받았다: 비우고 넣기는 통과(다른 계정 표 확인 항목)하고 복사된다.
    /// DELETE 를 빼면 TargetNoWrite 다.
    /// </summary>
    public static async Task WriterOnOtherOwnerAsync(CopyDbScope owned)
    {
        await using var scope = owned;
        await scope.CreateSourceTableAsync("ORD", CustomerColumns);
        await scope.CreateTargetTableAsync("ORD", CustomerColumns);
        await scope.ExecSourceAsync("BEGIN INSERT INTO ORD VALUES (1, 'a'); INSERT INTO ORD VALUES (2, 'b'); COMMIT; END;");
        await scope.ExecTargetAsync("BEGIN INSERT INTO ORD VALUES (9, 'old'); COMMIT; END;");
        await scope.GrantWriterAsync("ORD", "SELECT", "INSERT", "DELETE");
        var job = scope.Job(new TableRule("ORD", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = scope.SrcUser, TargetOwner = scope.DstUser });
        var sessions = await scope.OpenSessionsAsync(asWriter: true);

        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, default);

        Assert.False(report.HasErrors, Errors(report));
        var other = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.OtherOwnerTargets);
        Assert.Equal(CheckLevel.Confirm, other.Level);
        Assert.Contains($"{scope.DstUser}.ORD", other.Message, StringComparison.Ordinal);
        Assert.Contains(report.Preview, p => p.Sql == $"DELETE FROM \"{scope.DstUser}\".\"ORD\"");

        scope.Ui.ConfirmAnswer = false;
        var result = await NewRunner(scope).RunAsync(sessions, job, report, Decisions(report), NoPeriod, null, new CopyRunControl(), default);
        Assert.True(result.Outcome == CopyOutcome.Succeeded, Describe(result));
        Assert.Equal(2, await scope.CountAsync(scope.DstUser, "ORD"));

        await scope.ExecTargetAsync($"REVOKE DELETE ON ORD FROM {scope.WriterUser}");
        var denied = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, default);
        var noWrite = Assert.Single(denied.Items, i => i.Code == "TargetNoWrite");
        Assert.Equal("ORD", noWrite.Table);
        Assert.Contains("DELETE", noWrite.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 기본 계정(SRC→DST) 표 ORDERS 와 다른 계정 표 APP.CUSTOMERS(→ DST) 를 한 작업으로 복사한다. ORDERS 가 원본에서 APP.CUSTOMERS 를,
    /// 대상에서 DST.CUSTOMERS 를 참조하므로 규칙 순서와 달리 CUSTOMERS 가 먼저 적재되고 ORDERS 가 먼저 지워져야 한다. 다시 실행해도 같다.
    /// </summary>
    public static async Task TwoOwnerCopyAsync(CopyDbScope owned)
    {
        await using var scope = owned;
        await scope.ExecAppAsync($"CREATE TABLE CUSTOMERS ({CustomerColumns})");
        await scope.ExecAppAsync($"GRANT SELECT, REFERENCES ON CUSTOMERS TO {scope.SrcUser}");
        await scope.ExecAppAsync("BEGIN INSERT INTO CUSTOMERS VALUES (1, 'kim'); INSERT INTO CUSTOMERS VALUES (2, 'lee'); INSERT INTO CUSTOMERS VALUES (3, 'park'); COMMIT; END;");
        await scope.CreateSourceTableAsync("ORDERS", $"ID NUMBER PRIMARY KEY, CUST_ID NUMBER REFERENCES {scope.AppUser}.CUSTOMERS (ID), AMT NUMBER");
        await scope.ExecSourceAsync("BEGIN INSERT INTO ORDERS VALUES (10, 1, 100); INSERT INTO ORDERS VALUES (11, 2, 200); INSERT INTO ORDERS VALUES (12, 3, 300); COMMIT; END;");
        await scope.CreateTargetTableAsync("CUSTOMERS", CustomerColumns);
        await scope.CreateTargetTableAsync("ORDERS", "ID NUMBER PRIMARY KEY, CUST_ID NUMBER REFERENCES CUSTOMERS (ID), AMT NUMBER");
        await scope.ExecTargetAsync("BEGIN INSERT INTO CUSTOMERS VALUES (9, 'old'); INSERT INTO ORDERS VALUES (90, 9, 1); COMMIT; END;");

        var customers = new TableRule("CUSTOMERS", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = scope.AppUser, TargetOwner = scope.DstUser };
        var job = scope.Job(
            new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { SourceOwner = scope.SrcUser, TargetOwner = scope.DstUser },
            customers);
        var appKey = $"{scope.AppUser}.CUSTOMERS";
        Assert.Equal(appKey, CopyOwners.Key(job, customers));
        var sessions = await scope.OpenSessionsAsync();

        for (var round = 1; round <= 2; round++)
        {
            var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, default);
            Assert.False(report.HasErrors, Errors(report));
            Assert.Equal([appKey, "ORDERS"], report.Order.Select(p => CopyOwners.Key(job, p.Rule)));
            Assert.Equal(3, report.EstimatedRows[appKey]);
            Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.OtherOwnerTargets); // 모두 접속 계정(DST)의 표

            scope.Ui.ConfirmAnswer = false;
            var result = await NewRunner(scope).RunAsync(sessions, job, report, Decisions(report), NoPeriod, null, new CopyRunControl(), default);

            Assert.True(result.Outcome == CopyOutcome.Succeeded, $"{round}회: " + Describe(result));
            Assert.Equal([appKey, "ORDERS"], result.Tables.Select(t => t.Table).Order(StringComparer.Ordinal));
            Assert.All(result.Tables, t => Assert.Equal(3, t.TargetRows));
            Assert.Equal(3, await scope.CountAsync(scope.DstUser, "CUSTOMERS"));
            Assert.Equal(3, await scope.CountAsync(scope.DstUser, "ORDERS"));
            Assert.Equal(3, await scope.CountAsync(scope.AppUser, "CUSTOMERS")); // 원본은 그대로
        }
    }

    /// <summary>
    /// 대상 계정이 작업 기본 대상 계정(DST)과 다른 규칙 SRC.ORDERS → APP.ORDERS 를 실제로 복사한다. APP.ORDERS 만 비우고 채우며,
    /// 기본 대상 계정의 같은 이름 표 DST.ORDERS 는 행이 그대로다(대상 계정이 DST 로 새지 않는다).
    /// 원본과 대상이 같은 DB 라 원본 계정과 같은 대상 계정(APP→APP)은 SameSourceTarget 이 되므로 원본은 기본 계정 표로 둔다.
    /// </summary>
    public static async Task OtherTargetOwnerLeavesDefaultTargetAloneAsync(CopyDbScope owned)
    {
        await using var scope = owned;
        await scope.CreateSourceTableAsync("ORDERS", CustomerColumns);
        await scope.ExecSourceAsync("BEGIN INSERT INTO ORDERS VALUES (1, 'a'); INSERT INTO ORDERS VALUES (2, 'b'); COMMIT; END;");
        await scope.ExecAppAsync($"CREATE TABLE ORDERS ({CustomerColumns})");
        await scope.ExecAppAsync("BEGIN INSERT INTO ORDERS VALUES (8, 'app-old'); COMMIT; END;");
        await scope.ExecAppAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON ORDERS TO {scope.DstUser}");
        await scope.CreateTargetTableAsync("ORDERS", CustomerColumns);
        await scope.ExecTargetAsync("BEGIN INSERT INTO ORDERS VALUES (90, 'keep1'); INSERT INTO ORDERS VALUES (91, 'keep2'); INSERT INTO ORDERS VALUES (92, 'keep3'); COMMIT; END;");

        var rule = new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { TargetOwner = scope.AppUser };
        var job = scope.Job(rule);
        Assert.Equal("ORDERS", CopyOwners.Key(job, rule));
        Assert.Equal($"{scope.AppUser}.ORDERS", CopyOwners.TargetName(job, rule));
        var sessions = await scope.OpenSessionsAsync();

        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, default);

        Assert.False(report.HasErrors, Errors(report));
        Assert.Equal(scope.AppUser, Assert.Single(report.Order).Target.Table.Owner);
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.OtherOwnerTargets && i.Message.Contains($"{scope.AppUser}.ORDERS", StringComparison.Ordinal));
        Assert.Contains(report.Preview, p => p.Sql == $"DELETE FROM \"{scope.AppUser}\".\"ORDERS\"");
        Assert.DoesNotContain(report.Preview, p => p.Sql.Contains($"\"{scope.DstUser}\".\"ORDERS\"", StringComparison.Ordinal));

        scope.Ui.ConfirmAnswer = false;
        var result = await NewRunner(scope).RunAsync(sessions, job, report, Decisions(report), NoPeriod, null, new CopyRunControl(), default);

        Assert.True(result.Outcome == CopyOutcome.Succeeded, Describe(result));
        Assert.Equal(2, await scope.CountAsync(scope.AppUser, "ORDERS"));
        Assert.Equal(3, await scope.CountAsync(scope.DstUser, "ORDERS")); // 기본 대상 계정의 같은 이름 표는 그대로
        Assert.Equal(2, await scope.CountAsync(scope.SrcUser, "ORDERS"));
    }

    /// <summary>
    /// 대상 이름이 표가 아니면(뷰·시노님·MV) TargetMissing 이다. MV 의 저장 표는 ALL_TABLES 에도 나오므로 ALL_MVIEWS 로 뺀다.
    /// 대상 표는 지우지 않고 계획에도 넣지 않는다.
    /// </summary>
    public static async Task TargetThatIsNotATableAsync(CopyDbScope owned)
    {
        await using var scope = owned;
        await scope.ExecAdminAsync($"GRANT CREATE VIEW, CREATE MATERIALIZED VIEW TO {scope.DstUser}");
        foreach (var name in new[] { "T_VIEW", "T_SYN", "T_MV" })
        {
            await scope.CreateSourceTableAsync(name, CustomerColumns);
            await scope.ExecSourceAsync($"BEGIN INSERT INTO {name} VALUES (1, 'a'); COMMIT; END;");
        }

        await scope.CreateTargetTableAsync("BASE_T", CustomerColumns);
        await scope.ExecTargetAsync("BEGIN INSERT INTO BASE_T VALUES (9, 'keep'); COMMIT; END;");
        await scope.ExecTargetAsync("CREATE VIEW T_VIEW AS SELECT * FROM BASE_T");
        await scope.ExecTargetAsync("CREATE SYNONYM T_SYN FOR BASE_T");
        await scope.ExecTargetAsync("CREATE MATERIALIZED VIEW T_MV AS SELECT * FROM BASE_T");
        var job = scope.Job(
            new TableRule("T_VIEW", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("T_SYN", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("T_MV", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync();

        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, default);

        var missing = report.Items.Where(i => i.Code == "TargetMissing").Select(i => i.Table).Order(StringComparer.Ordinal);
        Assert.Equal(["T_MV", "T_SYN", "T_VIEW"], missing);
        Assert.Empty(report.Order);
        Assert.Equal(1, await scope.CountAsync(scope.DstUser, "BASE_T"));
    }

    private static string Describe(CopyRunResult result)
        => result.Outcome + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
           + " | notices: " + string.Join(" / ", result.Notices);

    /// <summary>실행 기록을 남기지 않는 가짜(이 시험은 기록 내용을 보지 않는다).</summary>
    private sealed class NullRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(new Record());

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

        private sealed class Record : ICopyRunRecord
        {
            public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

            public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

            public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

            public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct) => Task.CompletedTask;

            public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
