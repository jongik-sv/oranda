using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 「대상에 표가 없으면 만들기」(TableRule.CreateIfMissing, 개발 접속만, CopyDbScope 사용).
// 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 대상 계정에 표가 없으면 원본 구조로 만든 뒤 넣는다: 만들기 → 적재 → (두 번째 실행은 표가 있으니 만들지 않음), 트리거·권한은 안 만듦,
/// 개발 접속이 아니면 막힘, 적재 실패 시 만든 표를 PURGE 로 지움(휴지통에 안 남음).
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CreateMissingTests
{
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    private readonly OracleFreeFixture _db;

    public CreateMissingTests(OracleFreeFixture db) => _db = db;

    [Fact]
    public async Task Creates_the_missing_target_from_the_source_loads_it_and_leaves_it_alone_on_the_second_run()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.ExecSourceAsync(
            "CREATE TABLE CM_PARENT (ID NUMBER CONSTRAINT CM_PARENT_PK PRIMARY KEY, NAME VARCHAR2(30) NOT NULL, AMT NUMBER(8) DEFAULT 7,"
            + " CONSTRAINT CM_PARENT_CK CHECK (AMT > 0))");
        await scope.ExecSourceAsync("CREATE INDEX CM_PARENT_NAME_IX ON CM_PARENT (NAME)");
        await scope.ExecSourceAsync("COMMENT ON TABLE CM_PARENT IS 'parent from source'");
        await scope.ExecSourceAsync("CREATE TABLE CM_CHILD (ID NUMBER PRIMARY KEY, PARENT_ID NUMBER CONSTRAINT CM_CHILD_FK REFERENCES CM_PARENT (ID), NOTE VARCHAR2(10))");
        await scope.ExecSourceAsync("CREATE TRIGGER CM_PARENT_TRG BEFORE INSERT ON CM_PARENT FOR EACH ROW BEGIN :NEW.NAME := :NEW.NAME; END;");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO CM_PARENT (ID, NAME, AMT) VALUES (1, 'one', 5)",
            "INSERT INTO CM_PARENT (ID, NAME, AMT) VALUES (2, 'two', 6)",
            "INSERT INTO CM_CHILD VALUES (10, 1, 'a')",
            "INSERT INTO CM_CHILD VALUES (11, 2, 'b')"));

        // 대상에는 두 표 모두 없다. 부모는 MERGE, 자식은 「비우고 넣기」: 새 표라 둘 다 INSERT 만 한다(대상 DELETE 가 없다).
        var job = scope.Job(
            new TableRule("CM_PARENT", RuleKind.All, TargetAction.MergeByPk) { CreateIfMissing = true },
            new TableRule("CM_CHILD", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true });
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job);

        Assert.Equal(2, report.Items.Count(i => i.Code == CopyCheckCodes.CreateTable && i.Level == CheckLevel.Confirm));
        Assert.DoesNotContain(report.Items, i => i.Code == "TargetMissing");
        Assert.All(report.Order, p => Assert.True(p.CreateMissing));
        Assert.Equal(["CM_PARENT", "CM_CHILD"], report.Order.Select(p => p.Rule.Table)); // 부모 → 자식
        var creates = report.Preview.Where(p => p.Sql.StartsWith("CREATE TABLE", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, creates.Count);
        Assert.DoesNotContain(report.Preview, p => p.Sql.StartsWith("DELETE", StringComparison.Ordinal));
        var purges = report.Preview.Where(p => p.Sql.StartsWith("DROP TABLE", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [$"DROP TABLE \"{scope.DstUser}\".\"CM_CHILD\" PURGE", $"DROP TABLE \"{scope.DstUser}\".\"CM_PARENT\" PURGE"],
            purges.Select(p => p.Sql)); // 자식 → 부모
        Assert.All(purges, p => Assert.Equal(PolicyDecisionKind.Confirm, p.Policy));
        Assert.All(purges, p => Assert.NotNull(p.RetypeText));

        var result = await RunAsync(scope, sessions, job, report);

        Assert.True(result.Outcome == CopyOutcome.Succeeded && result.FailedRows.Count == 0, Describe(result));
        Assert.Empty(scope.Ui.Confirms); // 미리 보기의 글과 실행기가 보낸 글이 같다(미리 받은 확인으로 통과).

        // 원본 구조(열·기본값·이름 있는 PK·CHECK·인덱스·주석·작업 안 FK)와 행.
        var dst = scope.DstUser;
        Assert.Equal("ID:NUMBER,NAME:VARCHAR2(30),AMT:NUMBER(8)",
            await ListAsync($"SELECT COLUMN_NAME || ':' || DATA_TYPE || CASE WHEN DATA_TYPE = 'VARCHAR2' THEN '(' || CHAR_LENGTH || ')'"
                            + " WHEN DATA_PRECISION IS NOT NULL THEN '(' || DATA_PRECISION || ')' END FROM DBA_TAB_COLUMNS"
                            + $" WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_PARENT' ORDER BY COLUMN_ID"));
        Assert.Equal("CM_PARENT_CK,CM_PARENT_PK",
            await ListAsync($"SELECT CONSTRAINT_NAME FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_PARENT'"
                            + " AND CONSTRAINT_TYPE IN ('P', 'C') AND CONSTRAINT_NAME NOT LIKE 'SYS%' ORDER BY CONSTRAINT_NAME"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM DBA_INDEXES WHERE OWNER = '{dst}' AND INDEX_NAME = 'CM_PARENT_NAME_IX'"));
        Assert.Equal("parent from source", await ScalarAsync($"SELECT COMMENTS FROM DBA_TAB_COMMENTS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_PARENT'"));
        Assert.Equal("ENABLED:VALIDATED", await ScalarAsync($"SELECT STATUS || ':' || VALIDATED FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}'"
                                                            + " AND CONSTRAINT_NAME = 'CM_CHILD_FK' AND CONSTRAINT_TYPE = 'R'"));
        Assert.Equal("1:one:5,2:two:6", await ListAsync($"SELECT ID || ':' || NAME || ':' || AMT FROM {dst}.CM_PARENT ORDER BY ID"));
        Assert.Equal("10,11", await ListAsync($"SELECT ID FROM {dst}.CM_CHILD ORDER BY ID"));

        // 트리거·권한은 만들지 않는다.
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM DBA_TRIGGERS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_PARENT'"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM DBA_TAB_PRIVS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_PARENT'"));

        // 두 번째 실행: 표가 이미 있으니 선택을 무시한다(만들지 않고 규칙대로 넣는다).
        var again = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var secondReport = await PrecheckAsync(scope, again, job);
        Assert.DoesNotContain(secondReport.Items, i => i.Code == CopyCheckCodes.CreateTable);
        Assert.All(secondReport.Order, p => Assert.False(p.CreateMissing));
        Assert.DoesNotContain(secondReport.Preview, p => p.Sql.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.Contains(secondReport.Preview, p => p.Sql.StartsWith("DELETE FROM", StringComparison.Ordinal)); // 「비우고 넣기」 자식 표
        var second = await RunAsync(scope, again, job, secondReport);
        Assert.True(second.Outcome == CopyOutcome.Succeeded, Describe(second));
        Assert.Equal("10,11", await ListAsync($"SELECT ID FROM {dst}.CM_CHILD ORDER BY ID"));
    }

    [Fact]
    public async Task Is_refused_unless_the_target_connection_is_a_development_connection()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("CM_NODEV", "ID NUMBER PRIMARY KEY");
        var job = scope.Job(new TableRule("CM_NODEV", RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true });
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Test);

        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);

        var item = Assert.Single(report.Items, i => i.Code == "TargetMissing");
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Contains("개발 접속에서만 만들 수 있습니다", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Preview, p => p.Sql.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM DBA_TABLES WHERE OWNER = '{scope.DstUser}' AND TABLE_NAME = 'CM_NODEV'"));
    }

    [Fact]
    public async Task Without_the_option_a_missing_target_still_stops_the_precheck()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("CM_OFF", "ID NUMBER PRIMARY KEY");
        var job = scope.Job(new TableRule("CM_OFF", RuleKind.All, TargetAction.AppendOnly));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);

        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);

        Assert.Contains(report.Items, i => i.Code == "TargetMissing" && i.Level == CheckLevel.Error);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.CreateTable);
    }

    [Fact]
    public async Task Load_failure_drops_the_table_it_created_without_the_recycle_bin_and_keeps_the_others()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("CM_OK", "ID NUMBER PRIMARY KEY");
        await scope.CreateSourceTableAsync("CM_BAD", "ID NUMBER PRIMARY KEY, AMT NUMBER CONSTRAINT CM_BAD_CK CHECK (AMT > 0)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO CM_OK VALUES (1)",
            "INSERT INTO CM_OK VALUES (2)",
            "INSERT INTO CM_BAD VALUES (1, 5)",
            "INSERT INTO CM_BAD VALUES (2, 6)",
            "INSERT INTO CM_BAD VALUES (3, 7)"));

        // 마스킹이 AMT 를 -1 로 바꿔 만든 표의 CHECK 에 걸린다: 실패 행이 상한(1)을 넘어 CM_BAD 의 적재가 끝나지 않는다.
        var job = scope.Job(
            new TableRule("CM_OK", RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true },
            new TableRule("CM_BAD", RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true, Masks = [new MaskRule("AMT", MaskKind.Fixed, "-1")] })
            with { Options = new CopyOptions { MaxFailedRows = 1 } };
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job);

        var result = await RunAsync(scope, sessions, job, report);

        Assert.NotEqual(CopyOutcome.Succeeded, result.Outcome);
        Assert.Equal(CopyOutcome.Succeeded, result.Tables.Single(t => t.Table == "CM_OK").Outcome);
        Assert.Equal(CopyOutcome.Failed, result.Tables.Single(t => t.Table == "CM_BAD").Outcome);
        Assert.Contains(result.Notices, n => n.Contains("CM_BAD", StringComparison.Ordinal) && n.Contains("지웠습니다", StringComparison.Ordinal));
        var dst = scope.DstUser;
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM DBA_TABLES WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_BAD'"));
        Assert.Equal(0, await DstCountAsync(scope, "SELECT COUNT(*) FROM USER_RECYCLEBIN")); // PURGE: 휴지통에 남지 않는다
        Assert.Equal("1,2", await ListAsync($"SELECT ID FROM {dst}.CM_OK ORDER BY ID")); // 앞 표는 그대로

        // 다시 실행하면 처음처럼 만든다(지운 표는 없으니).
        var again = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var secondReport = await PrecheckAsync(scope, again, job);
        Assert.Contains(secondReport.Order, p => p.Rule.Table == "CM_BAD" && p.CreateMissing);
        Assert.Contains(secondReport.Order, p => p.Rule.Table == "CM_OK" && !p.CreateMissing);
    }

    // ---- 도우미 ----

    private static OracleExecutionService NewExecution(CopyDbScope scope)
        => new(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500);

    private static async Task<PrecheckReport> PrecheckAsync(CopyDbScope scope, CopySessions sessions, CopyJob job)
    {
        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        return report;
    }

    /// <summary>화면처럼 확인이 필요한 미리 보기 문장(DROP … PURGE)을 미리 받은 확인으로 넘긴다. 확인 창이 뜨면 거절한다(글이 어긋나면 표가 실패한다).</summary>
    private static Task<CopyRunResult> RunAsync(CopyDbScope scope, CopySessions sessions, CopyJob job, PrecheckReport report)
    {
        scope.Ui.ConfirmAnswer = false;
        var decisions = new CopyDecisions(
            new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);
        var runner = new OracleCopyRunner(scope.Bulk(), NewExecution(scope), new NoRunLog());
        return runner.RunAsync(sessions, job, report, decisions, NoPeriod, null, new CopyRunControl(), CancellationToken.None);
    }

    private static string Describe(CopyRunResult result)
        => result.Outcome + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
           + " | notices: " + string.Join(" / ", result.Notices);

    /// <summary>문장 여러 개를 PL/SQL 블록 하나로 묶어 한 번에 보내고 커밋한다.</summary>
    private static string Block(params string[] statements)
        => "BEGIN " + string.Join(' ', statements.Select(s => s.TrimEnd(';') + ";")) + " COMMIT; END;";

    // ---- 정답 확인(앱 경로를 거치지 않는 직접 조회) ----

    private string AdminConnectionString => new ConnectionProfile("admin", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword);

    private async Task<List<string?>> AdminColumnAsync(string sql)
    {
        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        return await ColumnAsync(conn, sql);
    }

    private static async Task<List<string?>> ColumnAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var values = new List<string?>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return values;
    }

    private async Task<string?> ScalarAsync(string sql) => (await AdminColumnAsync(sql)).FirstOrDefault();

    private async Task<long> CountAsync(string sql) => long.Parse(await ScalarAsync(sql) ?? "0", CultureInfo.InvariantCulture);

    /// <summary>첫 열 값을 쉼표로 이은 글자.</summary>
    private async Task<string> ListAsync(string sql) => string.Join(',', (await AdminColumnAsync(sql)).Select(v => v ?? "<null>"));

    /// <summary>대상 소유자 계정으로 센다(USER_RECYCLEBIN 처럼 자기 스키마 뷰).</summary>
    private async Task<long> DstCountAsync(CopyDbScope scope, string sql)
    {
        await using var conn = new OracleConnection(new ConnectionProfile("direct", scope.DstUser, _db.DataSource).BuildConnectionString(scope.Password));
        await conn.OpenAsync();
        return long.Parse((await ColumnAsync(conn, sql)).FirstOrDefault() ?? "0", CultureInfo.InvariantCulture);
    }

    /// <summary>실행 기록 가짜(아무것도 쓰지 않는다).</summary>
    private sealed class NoRunLog : ICopyRunLog
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
