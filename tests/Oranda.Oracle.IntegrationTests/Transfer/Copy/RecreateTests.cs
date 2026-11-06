using System.Globalization;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 Task 10. 「테이블 다시 만들기」(개발 접속만, CopyDbScope 사용).
// 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 테이블 다시 만들기: 개발 라벨에서만 선택, 원본 구조로 다시 만들고 트리거·권한·참조 FK 되살리기, 휴지통, 테스트 라벨 DROP 확인,
/// DROP 뒤 CREATE 실패(표가 없는 상태의 안내와 남은 DDL).
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class RecreateTests
{
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    private readonly OracleFreeFixture _db;

    public RecreateTests(OracleFreeFixture db) => _db = db;

    // ---- 시험 ----

    [Fact]
    public async Task Recreate_choice_only_for_development_target()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("RC_T", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.CreateTargetTableAsync("RC_T", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        var job = scope.Job(new TableRule("RC_T", RuleKind.All, TargetAction.Recreate));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Test);

        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);

        Assert.Contains(report.Items, i => i.Code == "RecreateNotAllowed" && i.Level == CheckLevel.Error && i.Table == "RC_T");
        // DROP·CREATE 는 개발 라벨 대상일 때만 만든다(미리 보기에도 없다).
        Assert.DoesNotContain(report.Preview, p => p.Sql.StartsWith("DROP", StringComparison.Ordinal));
        // 점검을 건너뛰고 실행기를 불러도 개발 라벨이 아니면 지우지 않는다.
        var forced = report with { Items = [] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(scope, sessions, job, forced, new RecordingLog()));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM DBA_TABLES WHERE OWNER = '{scope.DstUser}' AND TABLE_NAME = 'RC_T'"));
    }

    [Fact]
    public async Task Recreates_with_source_structure_and_restores_triggers_grants_and_fks()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());

        // 원본: 새 열·다른 길이·이름 있는 PK·CHECK·인덱스·주석, 자식 표의 FK.
        await scope.ExecSourceAsync(
            "CREATE TABLE RC_PARENT (ID NUMBER CONSTRAINT RC_PARENT_PK PRIMARY KEY, NAME VARCHAR2(30) NOT NULL,"
            + " NEW_COL NUMBER(5) DEFAULT 7, MGR_ID NUMBER CONSTRAINT RC_PARENT_MGR_FK REFERENCES RC_PARENT (ID), CONSTRAINT RC_PARENT_CK CHECK (ID > 0))");
        await scope.ExecSourceAsync("CREATE INDEX RC_PARENT_NAME_IX ON RC_PARENT (NAME)");
        await scope.ExecSourceAsync("COMMENT ON TABLE RC_PARENT IS 'parent from source'");
        await scope.ExecSourceAsync(
            "CREATE TABLE RC_CHILD (ID NUMBER PRIMARY KEY, PARENT_ID NUMBER CONSTRAINT RC_CHILD_FK REFERENCES RC_PARENT (ID) ON DELETE CASCADE, NOTE VARCHAR2(10))");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO RC_PARENT (ID, NAME) VALUES (1, 'source-name-long-1')",
            "INSERT INTO RC_PARENT (ID, NAME) VALUES (2, 'source-name-long-2')",
            "INSERT INTO RC_PARENT (ID, NAME) VALUES (3, 'source-name-long-3')",
            // 자기 참조: 앞에 저장된 행 1 이 뒤의 행 3 을 가리킨다(원본 읽기 순서에서 자식 행이 부모 행보다 먼저 온다).
            "UPDATE RC_PARENT SET MGR_ID = 3 WHERE ID = 1",
            "INSERT INTO RC_CHILD VALUES (10, 1, 'a')",
            "INSERT INTO RC_CHILD VALUES (11, 2, 'b')"));

        // 옛 대상: 짧은 NAME, 원본에 없는 NOT NULL 열(사전 점검이 막으면 안 된다), 켜진·꺼진 트리거, 쓰기 계정 권한, 작업 밖 자식 표의 FK.
        await scope.CreateTargetTableAsync("RC_PARENT", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(5), OLD_REQ VARCHAR2(10) NOT NULL");
        await scope.CreateTargetTableAsync("RC_CHILD", "ID NUMBER PRIMARY KEY, PARENT_ID NUMBER REFERENCES RC_PARENT (ID)");
        await scope.CreateTargetTableAsync("RC_OUT", "ID NUMBER PRIMARY KEY, P_ID NUMBER CONSTRAINT RC_OUT_FK REFERENCES RC_PARENT (ID)");
        await scope.ExecTargetAsync(Block(
            "INSERT INTO RC_PARENT VALUES (1, 'a', 'x')",
            "INSERT INTO RC_PARENT VALUES (99, 'z', 'y')",
            "INSERT INTO RC_CHILD VALUES (50, 99)",
            "INSERT INTO RC_OUT VALUES (1, 1)",
            "INSERT INTO RC_OUT VALUES (2, 99)"));
        await scope.ExecTargetAsync("CREATE TRIGGER RC_PARENT_TRG BEFORE INSERT ON RC_PARENT FOR EACH ROW BEGIN :NEW.NAME := 'T:' || :NEW.NAME; END;");
        await scope.ExecTargetAsync("CREATE TRIGGER RC_PARENT_OFF BEFORE UPDATE ON RC_PARENT FOR EACH ROW BEGIN NULL; END;");
        await scope.ExecTargetAsync("ALTER TRIGGER RC_PARENT_OFF DISABLE");
        await scope.GrantWriterAsync("RC_PARENT", "SELECT");

        var job = scope.Job(
            new TableRule("RC_PARENT", RuleKind.All, TargetAction.Recreate),
            new TableRule("RC_CHILD", RuleKind.All, TargetAction.Recreate));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job);
        Assert.Equal(2, report.Items.Count(i => i.Code == "RecreateTable" && i.Level == CheckLevel.Confirm));
        // 자식 → 부모 순서의 DROP 이 미리 보기에 있고 이름 재입력이 필요하다.
        var drops = report.Preview.Where(p => p.Sql.StartsWith("DROP TABLE", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [$"DROP TABLE \"{scope.DstUser}\".\"RC_CHILD\"", $"DROP TABLE \"{scope.DstUser}\".\"RC_PARENT\""],
            drops.Select(d => d.Sql));
        Assert.All(drops, d => Assert.Equal(PolicyDecisionKind.Confirm, d.Policy));
        Assert.All(drops, d => Assert.NotNull(d.RetypeText));

        var log = new RecordingLog();
        var result = await RunAsync(scope, sessions, job, report, log);

        Assert.True(result.Outcome == CopyOutcome.Succeeded && result.FailedRows.Count == 0, Describe(result));
        Assert.Empty(scope.Ui.Confirms); // 미리 보기의 DROP 글과 실행기의 DROP 글이 같다(미리 받은 확인으로 통과).

        // 원본 구조: 옛 대상에만 있던 열은 없고, 길이·기본값·제약·인덱스·주석은 원본대로다.
        var dst = scope.DstUser;
        // 길이는 글자 수로 본다(CreateTableDdl 은 VARCHAR2(n CHAR) 로 만든다: DATA_LENGTH 는 문자 집합에 따라 바이트 수다).
        Assert.Equal("ID:NUMBER,NAME:VARCHAR2(30),NEW_COL:NUMBER(5),MGR_ID:NUMBER",
            await ListAsync($"SELECT COLUMN_NAME || ':' || DATA_TYPE || CASE WHEN DATA_TYPE = 'VARCHAR2' THEN '(' || CHAR_LENGTH || ')'"
                            + " WHEN DATA_PRECISION IS NOT NULL THEN '(' || DATA_PRECISION || ')' END FROM DBA_TAB_COLUMNS"
                            + $" WHERE OWNER = '{dst}' AND TABLE_NAME = 'RC_PARENT' ORDER BY COLUMN_ID"));
        Assert.Equal("RC_PARENT_CK,RC_PARENT_PK",
            await ListAsync($"SELECT CONSTRAINT_NAME FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}' AND TABLE_NAME = 'RC_PARENT'"
                            + " AND CONSTRAINT_TYPE IN ('P', 'C') AND CONSTRAINT_NAME NOT LIKE 'SYS%' ORDER BY CONSTRAINT_NAME"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM DBA_INDEXES WHERE OWNER = '{dst}' AND INDEX_NAME = 'RC_PARENT_NAME_IX'"));
        Assert.Equal("parent from source", await ScalarAsync($"SELECT COMMENTS FROM DBA_TAB_COMMENTS WHERE OWNER = '{dst}' AND TABLE_NAME = 'RC_PARENT'"));
        // 함께 다시 만든 표끼리의 FK 는 원본 카탈로그대로(ON DELETE CASCADE 유지), 자기 참조 FK 도 만든다.
        Assert.Equal("CASCADE", await ScalarAsync($"SELECT DELETE_RULE FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}' AND TABLE_NAME = 'RC_CHILD'"
                                                  + " AND CONSTRAINT_NAME = 'RC_CHILD_FK' AND CONSTRAINT_TYPE = 'R' AND STATUS = 'ENABLED'"));
        // 작업 안 FK 는 적재 뒤에 붙이므로 자식 행이 먼저 와도 실패 행이 없고, 붙인 FK 는 VALIDATE 로 켜진다.
        Assert.Equal("RC_PARENT:ENABLED:VALIDATED", await ScalarAsync($"SELECT p.TABLE_NAME || ':' || c.STATUS || ':' || c.VALIDATED FROM DBA_CONSTRAINTS c"
                                                    + " JOIN DBA_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME"
                                                    + $" WHERE c.OWNER = '{dst}' AND c.CONSTRAINT_NAME = 'RC_PARENT_MGR_FK'"));
        Assert.Equal("3", await ScalarAsync($"SELECT MGR_ID FROM {dst}.RC_PARENT WHERE ID = 1"));

        // 행: 원본 그대로(트리거는 적재 뒤에 되살리므로 접두어가 붙지 않는다). 옛 대상 행은 없다.
        Assert.Equal("1:source-name-long-1,2:source-name-long-2,3:source-name-long-3",
            await ListAsync($"SELECT ID || ':' || NAME FROM {dst}.RC_PARENT ORDER BY ID"));
        Assert.Equal("10,11", await ListAsync($"SELECT ID FROM {dst}.RC_CHILD ORDER BY ID"));

        // 트리거(켜진 것·꺼진 것)·권한·작업 밖 자식 표의 FK 가 되살아났다. RC_OUT 의 P_ID=99 는 부모가 없어 NOVALIDATE 다.
        Assert.Equal("RC_PARENT_OFF:DISABLED,RC_PARENT_TRG:ENABLED",
            await ListAsync($"SELECT TRIGGER_NAME || ':' || STATUS FROM DBA_TRIGGERS WHERE OWNER = '{dst}' AND TABLE_NAME = 'RC_PARENT' ORDER BY TRIGGER_NAME"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM DBA_TAB_PRIVS WHERE OWNER = '{dst}' AND TABLE_NAME = 'RC_PARENT'"
                                         + $" AND GRANTEE = '{scope.WriterUser}' AND PRIVILEGE = 'SELECT'"));
        Assert.Equal("ENABLED:NOT VALIDATED",
            await ScalarAsync($"SELECT STATUS || ':' || VALIDATED FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}' AND CONSTRAINT_NAME = 'RC_OUT_FK'"));
        Assert.Contains(result.Notices, n => n.Contains("RC_OUT_FK", StringComparison.Ordinal) && n.Contains("NOVALIDATE", StringComparison.Ordinal));
        Assert.Contains(result.Notices, n => n.Contains("FLASHBACK TABLE", StringComparison.Ordinal) && n.Contains("RC_PARENT", StringComparison.Ordinal));

        // 되살린 트리거가 실제로 돈다.
        await scope.ExecTargetAsync(Block("INSERT INTO RC_PARENT (ID, NAME) VALUES (4, 'n')"));
        Assert.Equal("T:n", await ScalarAsync($"SELECT NAME FROM {dst}.RC_PARENT WHERE ID = 4"));

        // 실행 기록: 보존 DDL(작업 밖 자식 FK 원문 포함)이 첫 파괴적 문장보다 앞, 참조 FK 떼어 내기 → 자식 → 부모 DROP, 그 뒤 CREATE.
        var statements = log.Statements;
        var firstDestructive = statements.FindIndex(s => s.StartsWith("DROP TABLE", StringComparison.Ordinal) || s.Contains(" DROP CONSTRAINT ", StringComparison.Ordinal));
        var preservedFk = statements.FindIndex(s => s.StartsWith("-- 보존 DDL", StringComparison.Ordinal) && s.Contains("RC_OUT_FK", StringComparison.Ordinal));
        Assert.True(preservedFk >= 0 && preservedFk < firstDestructive, string.Join(" / ", statements));
        Assert.Contains($"ALTER TABLE \"{dst}\".\"RC_OUT\" DROP CONSTRAINT \"RC_OUT_FK\"", statements);
        var childDrop = statements.FindIndex(s => s.StartsWith("DROP TABLE", StringComparison.Ordinal) && s.Contains("RC_CHILD", StringComparison.Ordinal));
        var parentDrop = statements.FindIndex(s => s.StartsWith("DROP TABLE", StringComparison.Ordinal) && s.Contains("RC_PARENT", StringComparison.Ordinal));
        var parentCreate = statements.FindIndex(s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal) && s.Contains("RC_PARENT", StringComparison.Ordinal));
        Assert.True(childDrop >= 0 && childDrop < parentDrop && parentDrop < parentCreate, string.Join(" / ", statements));
        Assert.DoesNotContain(statements, s => s.Contains("PURGE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Complete:Succeeded", log.Events[^1]);
    }

    [Fact]
    public async Task Dropped_table_is_in_recycle_bin()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        // 원본 ID 는 IDENTITY: 다시 만든 표는 BY DEFAULT 로 만들고, 적재 뒤 다음 값을 복사한 최대값 뒤로 옮긴다.
        await scope.CreateSourceTableAsync("RB_T", "ID NUMBER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.CreateTargetTableAsync("RB_T", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.ExecSourceAsync(Block("INSERT INTO RB_T (ID, NAME) VALUES (1, 'new')", "INSERT INTO RB_T (ID, NAME) VALUES (5, 'new')"));
        await scope.ExecTargetAsync(Block("INSERT INTO RB_T VALUES (7, 'old')"));
        var job = scope.Job(new TableRule("RB_T", RuleKind.All, TargetAction.Recreate));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job);

        var result = await RunAsync(scope, sessions, job, report, new RecordingLog());

        Assert.True(result.Outcome == CopyOutcome.Succeeded, Describe(result));
        Assert.True(await DstCountAsync(scope, "SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME = 'RB_T' AND OPERATION = 'DROP' AND TYPE = 'TABLE'") >= 1);
        // 안내는 이번 DROP 이 만든 휴지통 이름(BIN$…)으로 한다. 안내 글 그대로 실행하면 옛 행이 다른 이름으로 돌아온다.
        var notice = Assert.Single(result.Notices, n => n.Contains("FLASHBACK TABLE", StringComparison.Ordinal));
        var flashback = Regex.Match(notice, "FLASHBACK TABLE \"[^\"]+\"\\.\"(BIN\\$[^\"]+)\" TO BEFORE DROP RENAME TO \"RB_T_OLD\"");
        Assert.True(flashback.Success, notice);
        Assert.Equal(1, await DstCountAsync(scope, $"SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE OBJECT_NAME = '{flashback.Groups[1].Value}' AND ORIGINAL_NAME = 'RB_T'"));
        await scope.ExecTargetAsync(flashback.Value);
        Assert.Equal("7", await ListAsync($"SELECT ID FROM {scope.DstUser}.RB_T_OLD"));
        Assert.Equal("1,5", await ListAsync($"SELECT ID FROM {scope.DstUser}.RB_T ORDER BY ID"));

        // ID 없이 넣어도 복사한 값과 겹치지 않는다(IDENTITY 다음 값 = 최대값 뒤).
        await scope.ExecTargetAsync(Block("INSERT INTO RB_T (NAME) VALUES ('app')"));
        Assert.True(long.Parse(await ScalarAsync($"SELECT ID FROM {scope.DstUser}.RB_T WHERE NAME = 'app'") ?? "0", CultureInfo.InvariantCulture) > 5);
    }

    [Fact]
    public async Task Test_target_drop_is_denied_by_policy()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateTargetTableAsync("RD_T", "ID NUMBER PRIMARY KEY");
        var target = await scope.OpenTargetAsync(asWriter: false, ConnectionEnvironment.Test); // 세션은 scope 가 닫는다.
        scope.Ui.ConfirmAnswer = false;

        var r = await NewExecution(scope).ExecuteAsync(target, CopyStatements.DropTable(scope.DstUser, "RD_T"), new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.DataCopy }, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        var confirm = Assert.Single(scope.Ui.Confirms);
        Assert.Equal("RD_T", confirm.RetypeText);
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM DBA_TABLES WHERE OWNER = '{scope.DstUser}' AND TABLE_NAME = 'RD_T'"));
    }

    [Fact]
    public async Task Failed_create_after_drop_reports_flashback_and_pending_ddl()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        // 원본 PK 이름이 대상의 다른 표 제약 이름과 겹친다: DROP 은 되고 CREATE TABLE 이 ORA-02264 로 실패한다.
        await scope.CreateSourceTableAsync("RF_T", "ID NUMBER CONSTRAINT RF_DUP_PK PRIMARY KEY, V NUMBER");
        await scope.ExecSourceAsync(Block("INSERT INTO RF_T VALUES (1, 10)"));
        await scope.CreateTargetTableAsync("RF_T", "ID NUMBER PRIMARY KEY, V NUMBER");
        await scope.ExecTargetAsync(Block("INSERT INTO RF_T VALUES (5, 50)", "INSERT INTO RF_T VALUES (6, 60)"));
        await scope.ExecTargetAsync("CREATE TRIGGER RF_T_TRG BEFORE INSERT ON RF_T FOR EACH ROW BEGIN NULL; END;");
        await scope.GrantWriterAsync("RF_T", "SELECT");
        var job = scope.Job(new TableRule("RF_T", RuleKind.All, TargetAction.Recreate));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job);

        // 점검 뒤 실행 전에 다른 표가 같은 제약 이름을 가져간다(경합). 지금 다시 점검하면 RecreateBlocked 로 막힌다.
        await scope.CreateTargetTableAsync("RF_OTHER", "X NUMBER CONSTRAINT RF_DUP_PK PRIMARY KEY");
        var again = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);
        Assert.Contains(again.Items, i => i.Code == "RecreateBlocked" && i.Level == CheckLevel.Error && i.Message.Contains("RF_DUP_PK", StringComparison.Ordinal));

        // 앞의(오래된) 점검 결과로 실행하면 DROP 뒤 CREATE 가 ORA-02264 로 실패한다.
        var log = new RecordingLog();
        var result = await RunAsync(scope, sessions, job, report, log);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.NotNull(table.Message);
        Assert.Contains("다시 만들지 못했습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains($"FLASHBACK TABLE \"{scope.DstUser}\".\"BIN$", table.Message, StringComparison.Ordinal);
        Assert.Contains("되살리지 않은 DDL", table.Message, StringComparison.Ordinal);
        Assert.Contains("RF_T_TRG", table.Message, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT", table.Message, StringComparison.Ordinal);
        Assert.Contains(result.Notices, n => n.Contains("표가 없습니다", StringComparison.Ordinal));

        // 실행 기록의 표 결과에도 같은 안내와 남은 DDL 이 남는다(다음에 기록을 열어도 알 수 있다).
        var recorded = Assert.Single(log.Tables);
        Assert.Equal(table.Message, recorded.Message);
        Assert.Equal("Complete:Failed", log.Events[^1]);

        // 표는 없고 휴지통에 있다. 안내대로 되돌리면 옛 행이 그대로다.
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM DBA_TABLES WHERE OWNER = '{scope.DstUser}' AND TABLE_NAME = 'RF_T'"));
        await scope.ExecTargetAsync("FLASHBACK TABLE RF_T TO BEFORE DROP");
        Assert.Equal("5,6", await ListAsync($"SELECT ID FROM {scope.DstUser}.RF_T ORDER BY ID"));
    }

    [Fact]
    public async Task Hidden_foreign_key_stops_before_drop_and_reattaches_detached_fks()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("RH_T", "ID NUMBER PRIMARY KEY");
        await scope.ExecSourceAsync(Block("INSERT INTO RH_T VALUES (1)"));
        await scope.CreateTargetTableAsync("RH_T", "ID NUMBER PRIMARY KEY");
        await scope.CreateTargetTableAsync("RH_VIS", "ID NUMBER PRIMARY KEY, P_ID NUMBER CONSTRAINT RH_VIS_FK REFERENCES RH_T (ID)");
        await scope.ExecTargetAsync(Block("INSERT INTO RH_T VALUES (7)", "INSERT INTO RH_VIS VALUES (1, 7)"));
        // 대상 소유자에게 보이지 않는 FK: 원본 계정 스키마의 표가 대상 표를 참조한다(대상 계정은 그 표에 권한이 없다).
        await scope.ExecTargetAsync($"GRANT REFERENCES ON RH_T TO {scope.SrcUser}");
        await scope.ExecSourceAsync($"CREATE TABLE RH_HID (ID NUMBER PRIMARY KEY, P_ID NUMBER CONSTRAINT RH_HID_FK REFERENCES {scope.DstUser}.RH_T (ID))");
        var job = scope.Job(new TableRule("RH_T", RuleKind.All, TargetAction.Recreate));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job);

        var result = await RunAsync(scope, sessions, job, report, new RecordingLog());

        // CASCADE 없이 지우므로 서버가 ORA-02449 로 거부한다: 표·행은 그대로, 떼어 낸 보이는 FK 는 다시 붙는다.
        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        var table = Assert.Single(result.Tables);
        Assert.Contains("ORA-02449", table.Message ?? string.Empty, StringComparison.Ordinal);
        var dst = scope.DstUser;
        Assert.Equal("7", await ListAsync($"SELECT ID FROM {dst}.RH_T"));
        Assert.Equal("ENABLED:VALIDATED", await ScalarAsync($"SELECT STATUS || ':' || VALIDATED FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}' AND CONSTRAINT_NAME = 'RH_VIS_FK'"));
        Assert.Equal("ENABLED", await ScalarAsync($"SELECT STATUS FROM DBA_CONSTRAINTS WHERE OWNER = '{scope.SrcUser}' AND CONSTRAINT_NAME = 'RH_HID_FK'"));
        Assert.Equal(0, await DstCountAsync(scope, "SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME = 'RH_T'"));
    }

    [Fact]
    public async Task Failed_job_foreign_key_does_not_stop_load_or_restore()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("RG_P", "ID NUMBER PRIMARY KEY");
        await scope.CreateSourceTableAsync("RG_C", "ID NUMBER PRIMARY KEY, P_ID NUMBER CONSTRAINT RG_C_FK REFERENCES RG_P (ID)");
        await scope.ExecSourceAsync(Block("INSERT INTO RG_P VALUES (1)", "INSERT INTO RG_C VALUES (10, 1)", "INSERT INTO RG_C VALUES (11, 1)"));
        await scope.CreateTargetTableAsync("RG_P", "ID NUMBER PRIMARY KEY");
        await scope.CreateTargetTableAsync("RG_C", "ID NUMBER PRIMARY KEY, P_ID NUMBER");
        await scope.ExecTargetAsync("CREATE TRIGGER RG_C_TRG BEFORE INSERT ON RG_C FOR EACH ROW BEGIN NULL; END;");
        var job = scope.Job(
            new TableRule("RG_P", RuleKind.All, TargetAction.Recreate),
            new TableRule("RG_C", RuleKind.All, TargetAction.Recreate));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job);
        // 점검 뒤 다른 표가 FK 이름을 가져간다: 모든 CREATE 뒤에 붙이는 원본 FK 만 ORA-02264 로 실패한다.
        await scope.CreateTargetTableAsync("RG_X", "A NUMBER CONSTRAINT RG_C_FK CHECK (A > 0)");

        var log = new RecordingLog();
        var result = await RunAsync(scope, sessions, job, report, log);

        // FK 하나가 실패해도 적재·되살리기는 계속한다. 결과는 부분 실패, FK 원문은 표 메시지·보존 DDL 기록에 남는다.
        Assert.True(result.Outcome == CopyOutcome.PartiallyFailed, Describe(result));
        var child = Assert.Single(result.Tables, t => t.Table == "RG_C");
        Assert.Equal(CopyOutcome.PartiallyFailed, child.Outcome);
        Assert.Contains("ADD CONSTRAINT \"RG_C_FK\"", child.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(log.Statements, s => s.StartsWith("-- 보존 DDL RG_C:", StringComparison.Ordinal) && s.Contains("\"RG_C_FK\"", StringComparison.Ordinal));
        var dst = scope.DstUser;
        Assert.Equal("10,11", await ListAsync($"SELECT ID FROM {dst}.RG_C ORDER BY ID"));
        Assert.Equal("ENABLED", await ScalarAsync($"SELECT STATUS FROM DBA_TRIGGERS WHERE OWNER = '{dst}' AND TRIGGER_NAME = 'RG_C_TRG'"));
    }

    // ---- 실행 도우미 ----

    private static OracleExecutionService NewExecution(CopyDbScope scope)
        => new(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500);

    private static async Task<PrecheckReport> PrecheckAsync(CopyDbScope scope, CopySessions sessions, CopyJob job)
    {
        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        return report;
    }

    /// <summary>화면처럼 확인이 필요한 미리 보기 문장(DROP)을 미리 받은 확인으로 넘긴다. 확인 창이 뜨면 거절한다(글이 어긋나면 표가 실패한다).</summary>
    private static Task<CopyRunResult> RunAsync(CopyDbScope scope, CopySessions sessions, CopyJob job, PrecheckReport report, RecordingLog log)
    {
        scope.Ui.ConfirmAnswer = false;
        var decisions = new CopyDecisions(
            new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);
        var runner = new CopyRunner(scope.Bulk(), NewExecution(scope), log);
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

    // ---- 가짜 ----

    /// <summary>실행 기록 가짜. 실제 CopyRunLog 처럼 취소된 토큰이면 쓰지 않고 예외를 낸다.</summary>
    private sealed class RecordingLog : ICopyRunLog
    {
        public List<string> Events { get; } = [];

        public List<string> Statements { get; } = [];

        public List<TableRunResult> Tables { get; } = [];

        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(new Record(this));

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

        private sealed class Record(RecordingLog log) : ICopyRunRecord
        {
            public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                log.Events.Add("Disabled");
                return Task.CompletedTask;
            }

            public Task RecordStatementAsync(string sql, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                log.Events.Add("Statement");
                log.Statements.Add(sql);
                return Task.CompletedTask;
            }

            public Task RecordTableAsync(TableRunResult result, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                log.Events.Add("Table");
                log.Tables.Add(result);
                return Task.CompletedTask;
            }

            public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                log.Events.Add("FailedRow");
                return Task.CompletedTask;
            }

            public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                log.Events.Add("Complete:" + outcome);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
