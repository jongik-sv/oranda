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
/// 대상 계정에 표가 없으면 원본 구조로 만든 뒤 넣는다: 만들기 → 적재 → (두 번째 실행은 표가 있으니 만들지 않음),
/// 연결된 시퀀스를 표보다 먼저 만들고 적재 뒤 키 최댓값 위로 맞춤(SQ1), PK 는 적재 뒤(PF4a), 함수 기반 인덱스·트리거·권한·작업 밖 부모 FK 는 적재 뒤 함께 만듦(SQ2),
/// 개발 접속이 아니면 막힘, 적재 실패 시 만든 표를 PURGE 로 지움(휴지통에 안 남음).
/// 이 클래스는 26ai(<see cref="OracleFreeFixture"/>)만 쓴다: 시퀀스 열 기본값(12c 부터)을 그대로 둔다. 11g 대상은 DEFAULT 를 빼는 쪽이라 이 시험의 대상이 아니다.
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

        // 원본 트리거는 적재 뒤 함께 만든다(SQ2). 원본에서 준 권한이 없으니 권한도 없다.
        Assert.Equal("CM_PARENT_TRG:ENABLED",
            await ListAsync($"SELECT TRIGGER_NAME || ':' || STATUS FROM DBA_TRIGGERS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_PARENT'"));
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
    public async Task Creates_the_linked_sequence_first_then_adds_the_key_and_the_companion_objects_after_the_load()
    {
        // CopyDbScope 의 원본·대상 소유자는 CREATE TABLE·TRIGGER·SEQUENCE 권한이 있다(대상 세션 = 대상 소유자). 권한 받을 계정 = 쓰기 계정(같은 DB 라 대상에도 있다).
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        var dst = scope.DstUser;
        var writer = scope.WriterUser;

        // 원본: 시퀀스(NOCACHE: 다음 값 = LAST_NUMBER 가 정확하다), 작업 밖 부모, 시퀀스 기본값 열·이름 있는 PK·작업 밖 FK 를 가진 표,
        // 함수 기반 인덱스, 시퀀스를 쓰는 BEFORE INSERT 트리거, 쓰기 계정에 준 SELECT.
        await scope.ExecSourceAsync("CREATE SEQUENCE CM_SQ_SEQ START WITH 1 INCREMENT BY 1 NOCACHE");
        await scope.ExecSourceAsync("CREATE TABLE CM_SQ_OUTER (ID NUMBER CONSTRAINT CM_SQ_OUTER_PK PRIMARY KEY)");
        await scope.ExecSourceAsync(
            "CREATE TABLE CM_SQ (ID NUMBER DEFAULT CM_SQ_SEQ.NEXTVAL CONSTRAINT CM_SQ_PK PRIMARY KEY, NAME VARCHAR2(30), NOTE VARCHAR2(30),"
            + " OUTER_ID NUMBER CONSTRAINT CM_SQ_OUTER_FK REFERENCES CM_SQ_OUTER (ID))");
        await scope.ExecSourceAsync("CREATE INDEX CM_SQ_UP_IX ON CM_SQ (UPPER(NAME))");
        await scope.ExecSourceAsync("CREATE TRIGGER CM_SQ_TRG BEFORE INSERT ON CM_SQ FOR EACH ROW BEGIN"
                                    + " IF :NEW.ID IS NULL THEN :NEW.ID := CM_SQ_SEQ.NEXTVAL; END IF; :NEW.NOTE := NVL(:NEW.NOTE, 'trg'); END;");
        await scope.ExecSourceAsync($"GRANT SELECT ON CM_SQ TO {writer}");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO CM_SQ_OUTER VALUES (1)",
            "INSERT INTO CM_SQ (NAME, NOTE, OUTER_ID) VALUES ('alpha', 'a', 1)", // 기본값 → ID 1 (원본 시퀀스 다음 값은 최댓값 200 보다 작다: 적재 뒤 맞춘다)
            "INSERT INTO CM_SQ (ID, NAME, NOTE, OUTER_ID) VALUES (100, 'beta', 'b', 1)",
            "INSERT INTO CM_SQ (ID, NAME, NOTE, OUTER_ID) VALUES (200, 'gamma', 'c', 1)"));

        // 대상: 작업 밖 부모만 있다(같은 행). 시퀀스·CM_SQ 는 없다.
        await scope.CreateTargetTableAsync("CM_SQ_OUTER", "ID NUMBER CONSTRAINT CM_SQ_OUTER_PK PRIMARY KEY");
        await scope.ExecTargetAsync(Block("INSERT INTO CM_SQ_OUTER VALUES (1)"));

        var sourceNext = await ScalarAsync($"SELECT TO_CHAR(LAST_NUMBER) FROM DBA_SEQUENCES WHERE SEQUENCE_OWNER = '{scope.SrcUser}' AND SEQUENCE_NAME = 'CM_SQ_SEQ'");
        Assert.NotNull(sourceNext);

        var job = scope.Job(new TableRule("CM_SQ", RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true });
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var report = await PrecheckAsync(scope, sessions, job); // 오류 없음

        // 사전 점검: 시퀀스를 만든다(SQ1) — 원본 다음 값으로 시작, 맞추기 기준 = 기본값 열 ID.
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.CreateTable && i.Level == CheckLevel.Confirm);
        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.SequenceCreated && i.Level == CheckLevel.Confirm);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.SequenceDefaultDropped);
        var plan = Assert.Single(report.Order);
        Assert.True(plan.CreateMissing);
        var sequence = Assert.Single(plan.CreateSequences);
        Assert.Equal((dst, "CM_SQ_SEQ", sourceNext), (sequence.Owner, sequence.Name, sequence.Spec.StartWith)); // START WITH = 원본 LAST_NUMBER
        Assert.Equal(["ID"], sequence.KeyColumns);

        // 사전 점검: 함께 만드는 객체(SQ2) — 빼는 것이 없어 통과 항목이다.
        var companions = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CompanionObjects);
        Assert.Equal(CheckLevel.Pass, companions.Level);
        foreach (var part in new[] { "함수 기반 인덱스 CM_SQ_UP_IX", "트리거 CM_SQ_TRG", $"권한 → {writer}", "FK CM_SQ_OUTER_FK" })
        {
            Assert.Contains(part, companions.Message, StringComparison.Ordinal);
        }

        // 미리 보기: CREATE SEQUENCE → CREATE TABLE(PK 없이, DEFAULT 는 대상 시퀀스) → 적재 뒤 PK(PF4a) → 딸린 객체. 만든 시퀀스를 지우는 DROP 도 미리 받는다.
        var sql = report.Preview.Select(p => p.Sql).ToList();
        var createSequence = sql.IndexOf(sequence.CreateDdl);
        Assert.Equal($"CREATE SEQUENCE \"{dst}\".\"CM_SQ_SEQ\" MINVALUE 1 NOMAXVALUE INCREMENT BY 1 START WITH {sourceNext} NOCACHE NOORDER NOCYCLE", sequence.CreateDdl);
        var createTable = sql.FindIndex(s => s.StartsWith($"CREATE TABLE \"{dst}\".\"CM_SQ\"", StringComparison.Ordinal));
        var addKey = sql.FindIndex(s => s.StartsWith($"ALTER TABLE \"{dst}\".\"CM_SQ\" ADD CONSTRAINT \"CM_SQ_PK\" PRIMARY KEY", StringComparison.Ordinal));
        Assert.True(createSequence >= 0 && createSequence < createTable && createTable < addKey, string.Join(Environment.NewLine, sql));
        Assert.DoesNotContain("PRIMARY KEY", sql[createTable], StringComparison.Ordinal);
        Assert.Contains($"\"{dst}\".\"CM_SQ_SEQ\"", sql[createTable], StringComparison.Ordinal);
        Assert.DoesNotContain(scope.SrcUser, sql[createTable], StringComparison.Ordinal);
        Assert.Contains(report.Preview, p => p.Sql == $"DROP SEQUENCE \"{dst}\".\"CM_SQ_SEQ\"" && p.Policy == PolicyDecisionKind.Confirm);
        Assert.Contains(sql, s => s.StartsWith($"CREATE INDEX \"{dst}\".\"CM_SQ_UP_IX\" ON \"{dst}\".\"CM_SQ\" (", StringComparison.Ordinal)
                                  && s.Contains("UPPER", StringComparison.Ordinal));
        Assert.Contains(sql, s => s.Contains("TRIGGER", StringComparison.Ordinal) && s.Contains($"\"{dst}\".\"CM_SQ_TRG\"", StringComparison.Ordinal));
        Assert.Contains($"GRANT SELECT ON \"{dst}\".\"CM_SQ\" TO \"{writer}\"", sql);
        Assert.Contains(sql, s => s.StartsWith($"ALTER TABLE \"{dst}\".\"CM_SQ\" ADD CONSTRAINT \"CM_SQ_OUTER_FK\" FOREIGN KEY", StringComparison.Ordinal)
                                  && s.Contains($"REFERENCES \"{dst}\".\"CM_SQ_OUTER\"", StringComparison.Ordinal));

        var result = await RunAsync(scope, sessions, job, report);

        // 딸린 객체 하나라도 못 만들면 부분 실패가 되므로, 성공 = 모두 만들었다.
        Assert.True(result.Outcome == CopyOutcome.Succeeded && result.FailedRows.Count == 0, Describe(result));
        Assert.Empty(scope.Ui.Confirms);
        Assert.Contains(result.Notices, n => n.Contains("CM_SQ_SEQ", StringComparison.Ordinal) && n.Contains("뒤로 맞췄습니다", StringComparison.Ordinal));
        Assert.Equal("1:a,100:b,200:c", await ListAsync($"SELECT ID || ':' || NOTE FROM {dst}.CM_SQ ORDER BY ID"));

        // SQ1: 시퀀스(증가값은 맞춘 뒤 원래값으로 돌아옴)와 대상 시퀀스를 가리키는 기본값.
        Assert.Equal("1:0", await ScalarAsync($"SELECT INCREMENT_BY || ':' || CACHE_SIZE FROM DBA_SEQUENCES WHERE SEQUENCE_OWNER = '{dst}' AND SEQUENCE_NAME = 'CM_SQ_SEQ'"));
        var idDefault = await ScalarAsync($"SELECT DATA_DEFAULT_VC FROM DBA_TAB_COLUMNS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_SQ' AND COLUMN_NAME = 'ID'");
        Assert.NotNull(idDefault);
        Assert.Contains("CM_SQ_SEQ", idDefault, StringComparison.Ordinal);
        Assert.Contains("NEXTVAL", idDefault, StringComparison.Ordinal);
        Assert.DoesNotContain(scope.SrcUser, idDefault, StringComparison.Ordinal);

        // PF4a: 적재 뒤 붙인 이름 있는 PK.
        Assert.Equal("ENABLED:VALIDATED", await ScalarAsync($"SELECT STATUS || ':' || VALIDATED FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}'"
                                                            + " AND TABLE_NAME = 'CM_SQ' AND CONSTRAINT_NAME = 'CM_SQ_PK' AND CONSTRAINT_TYPE = 'P'"));

        // SQ2: 함수 기반 인덱스, 켜진 VALID 트리거, 쓰기 계정 권한, 대상에 있던 작업 밖 부모를 참조하는 FK.
        Assert.Equal("FUNCTION-BASED NORMAL", await ScalarAsync($"SELECT INDEX_TYPE FROM DBA_INDEXES WHERE OWNER = '{dst}' AND INDEX_NAME = 'CM_SQ_UP_IX'"
                                                                + $" AND TABLE_OWNER = '{dst}' AND TABLE_NAME = 'CM_SQ'"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM DBA_IND_EXPRESSIONS WHERE INDEX_OWNER = '{dst}' AND INDEX_NAME = 'CM_SQ_UP_IX'"));
        Assert.Equal("ENABLED:VALID", await ScalarAsync("SELECT t.STATUS || ':' || o.STATUS FROM DBA_TRIGGERS t"
                                                        + " JOIN DBA_OBJECTS o ON o.OWNER = t.OWNER AND o.OBJECT_NAME = t.TRIGGER_NAME AND o.OBJECT_TYPE = 'TRIGGER'"
                                                        + $" WHERE t.OWNER = '{dst}' AND t.TRIGGER_NAME = 'CM_SQ_TRG' AND t.TABLE_OWNER = '{dst}' AND t.TABLE_NAME = 'CM_SQ'"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM DBA_TAB_PRIVS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_SQ'"
                                         + $" AND GRANTEE = '{writer}' AND PRIVILEGE = 'SELECT'"));
        Assert.Equal($"ENABLED:VALIDATED:{dst}:CM_SQ_OUTER_PK", await ScalarAsync("SELECT STATUS || ':' || VALIDATED || ':' || R_OWNER || ':' || R_CONSTRAINT_NAME"
                                                                                  + $" FROM DBA_CONSTRAINTS WHERE OWNER = '{dst}' AND TABLE_NAME = 'CM_SQ'"
                                                                                  + " AND CONSTRAINT_NAME = 'CM_SQ_OUTER_FK' AND CONSTRAINT_TYPE = 'R'"));

        // 새 행(ID 없이): 기본값의 시퀀스가 복사한 최댓값(200) 뒤에서 나오고, 트리거가 NOTE 를 채운다.
        await scope.ExecTargetAsync("INSERT INTO CM_SQ (NAME, OUTER_ID) VALUES ('new', 1)");
        var added = await ScalarAsync($"SELECT ID || ':' || NOTE FROM {dst}.CM_SQ WHERE NAME = 'new'");
        Assert.NotNull(added);
        var parts = added.Split(':');
        Assert.True(long.Parse(parts[0], CultureInfo.InvariantCulture) > 200, added);
        Assert.Equal("trg", parts[1]);
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

    /// <summary>
    /// 「대상에 열 추가」(PC1, 결정 D-066): 대상에 없는 원본 열에 처리 두 개(빼고 복사·열 추가)가 붙고, 열 추가를 고르면 점검은 문장만 미리 보기에 두고(대상에 DDL 을 보내지 않음)
    /// 실행이 대상 정리 전에 <c>ALTER TABLE … ADD</c> 를 보낸 뒤 그 열까지 넣는다. 더한 열은 NULL 허용·기본값 없음이다.
    /// </summary>
    [Fact]
    public async Task Adds_the_missing_target_column_at_run_time_when_the_add_column_fix_is_applied()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        var dst = scope.DstUser;
        await scope.ExecSourceAsync("CREATE TABLE PC1_AC (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30), NOTE VARCHAR2(40) DEFAULT 'n' NOT NULL)");
        await scope.ExecSourceAsync(Block("INSERT INTO PC1_AC VALUES (1, 'one', 'first')", "INSERT INTO PC1_AC VALUES (2, 'two', 'second')"));
        await scope.ExecTargetAsync("CREATE TABLE PC1_AC (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30))");
        await scope.ExecTargetAsync(Block("INSERT INTO PC1_AC VALUES (9, 'old')"));

        var job = scope.Job(new TableRule("PC1_AC", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development);
        var first = await PrecheckAsync(scope, sessions, job);

        var missing = Assert.Single(first.Items, i => i.Code == "ColumnNotInTarget");
        Assert.Equal([CopyFixKind.SkipColumn, CopyFixKind.AddColumn], missing.Fixes.Select(f => f.Kind));
        var alter = $"ALTER TABLE \"{dst}\".\"PC1_AC\" ADD (\"NOTE\" VARCHAR2(40 CHAR))";
        Assert.Equal(alter, missing.Fixes[1].Preview);
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM DBA_TAB_COLUMNS WHERE OWNER = '{dst}' AND TABLE_NAME = 'PC1_AC' AND COLUMN_NAME = 'NOTE'")); // 점검은 DDL 을 보내지 않는다

        var fixedJob = CopyPrecheckFixes.Apply(job, [missing.Fixes[1]]);
        Assert.Equal(["NOTE"], fixedJob.Tables[0].AddColumns);
        var report = await PrecheckAsync(scope, sessions, fixedJob);
        Assert.Single(report.Items, i => i.Code == CopyCheckCodes.AddColumn);
        Assert.Contains(report.Preview, p => p.Sql == alter);
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM DBA_TAB_COLUMNS WHERE OWNER = '{dst}' AND TABLE_NAME = 'PC1_AC' AND COLUMN_NAME = 'NOTE'"));

        var result = await RunAsync(scope, sessions, fixedJob, report);

        Assert.True(result.Outcome == CopyOutcome.Succeeded && result.FailedRows.Count == 0, Describe(result));
        Assert.Empty(scope.Ui.Confirms); // 미리 보기의 글과 실행기가 보낸 글이 같다
        Assert.Equal("VARCHAR2:40:Y", await ScalarAsync(
            $"SELECT DATA_TYPE || ':' || CHAR_LENGTH || ':' || NULLABLE FROM DBA_TAB_COLUMNS"
            + $" WHERE OWNER = '{dst}' AND TABLE_NAME = 'PC1_AC' AND COLUMN_NAME = 'NOTE'"));
        Assert.Equal("1:first,2:second", await ListAsync($"SELECT ID || ':' || NOTE FROM {dst}.PC1_AC ORDER BY ID"));
    }

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
