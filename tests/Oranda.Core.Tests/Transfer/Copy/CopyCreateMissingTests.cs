using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 「대상에 표가 없으면 원본 구조로 만든 뒤 넣기」(<see cref="TableRule.CreateIfMissing"/>)의 실행기 흐름(서버 없음, 가짜 협력자).
/// 보낸 문장의 순서와 적재 실패 때의 DROP … PURGE 정리를 본다. 실제 서버로 도는 확인은 Oracle 통합 시험이 맡는다.
/// </summary>
public sealed class CopyCreateMissingTests
{
    private const string CreateText = "CREATE TABLE \"DST\".\"ORDERS\" (\"ID\" NUMBER(10) NOT NULL, \"NAME\" VARCHAR2(20 CHAR))";
    private const string CommentText = "COMMENT ON TABLE \"DST\".\"ORDERS\" IS '주문'";
    private const string DropText = "DROP TABLE \"DST\".\"ORDERS\" PURGE";

    private static readonly DbError NameInUse = new("ORA-00955", "name is already used by an existing object");

    // 표보다 먼저 만드는 시퀀스(SQ1): 원본 ID 열 기본값이 원본 계정의 시퀀스를 가리킨다 → 대상 계정으로 바꿔 만든다.
    private const string SourceSequenceDefault = "\"SRC\".\"ORDERS_SEQ\".NEXTVAL";
    private const string CreateWithDefaultText = "CREATE TABLE \"DST\".\"ORDERS\" (\"ID\" NUMBER(10) DEFAULT \"DST\".\"ORDERS_SEQ\".NEXTVAL NOT NULL, \"NAME\" VARCHAR2(20 CHAR))";
    private const string DropSequenceText = "DROP SEQUENCE \"DST\".\"ORDERS_SEQ\"";
    private const string MaxKeyText = "SELECT MAX(\"ID\") FROM \"DST\".\"ORDERS\"";
    private const string NextValueText = "SELECT \"DST\".\"ORDERS_SEQ\".NEXTVAL FROM DUAL CONNECT BY LEVEL <= :N";
    private static readonly DbError NoPrivilege = new("ORA-01031", "insufficient privileges");

    // ---- 실행기: 만든 뒤 적재 ----

    [Theory]
    [InlineData(TargetAction.DeleteAllInsert)]
    [InlineData(TargetAction.MergeByPk)]
    [InlineData(TargetAction.AppendOnly)]
    public async Task Missing_table_is_created_then_loaded_by_insert_without_any_delete(TargetAction action)
    {
        var f = new Fixture(action, createMissing: true);

        var result = await f.RunAsync();

        // 보낸 순서: CREATE → 나머지 DDL(표 주석) → INSERT 배열 적재. DELETE·MERGE·DROP 은 없다.
        Assert.Equal(2, f.Order.Count(x => x.StartsWith("EXEC ", StringComparison.Ordinal)));
        Assert.Equal("EXEC " + CreateText, f.Order[0]);
        Assert.Equal("EXEC " + CommentText, f.Order[1]);
        Assert.StartsWith("BULK INSERT INTO \"DST\".\"ORDERS\" (\"ID\", \"NAME\") VALUES (:C1, :C2)", f.Order[2], StringComparison.Ordinal);
        Assert.Equal(3, f.Order.Count);
        Assert.DoesNotContain(f.All, x => x.Contains("DELETE", StringComparison.Ordinal) || x.Contains("MERGE", StringComparison.Ordinal)
                                                                                         || x.Contains("DROP", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Execution.Calls, c => c.Statement.Verb == "DELETE");

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(2, table.SourceRows);
        Assert.Equal(2, table.TargetRows);
        Assert.Contains("원본 구조로 만들었습니다", table.Message, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Notices);

        // 만든 CREATE 는 실행 기록에도 남는다(정리 문장과 같은 경로).
        Assert.Contains(CreateText, f.RunRecord.Statements);
        Assert.Equal(CopyOutcome.Succeeded, f.RunRecord.Completed);
    }

    [Fact]
    public async Task Existing_table_is_not_created_and_the_normal_delete_is_still_sent()
    {
        // 같은 규칙인데 대상 표가 있어 계획이 CreateMissing=false 이면 지금까지처럼 DELETE 한 뒤 넣는다(CREATE 없음).
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: false);

        var result = await f.RunAsync();

        Assert.DoesNotContain(f.Order, x => x.Contains("CREATE TABLE", StringComparison.Ordinal));
        Assert.Equal("EXEC DELETE FROM \"DST\".\"ORDERS\"", f.Order[0]);
        Assert.StartsWith("BULK INSERT INTO", f.Order[1], StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(f.All, x => x.Contains("PURGE", StringComparison.Ordinal));
    }

    // ---- 실행기: 적재 실패 → 만든 표 지우기 ----

    [Fact]
    public async Task Load_failure_drops_the_table_it_created_with_purge_after_ending_the_transaction()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true);
        f.Bulk.OnArray = c => c.ColumnCount > 0 ? new BulkResult(ExecutionStatus.Denied, 0) { Policy = new PolicyDecision(PolicyDecisionKind.Deny, Message: "insert denied") } : null;

        var result = await f.RunAsync();

        // 만들고 → 넣기 실패 → 대상 트랜잭션을 끝낸 뒤 DROP … PURGE.
        var texts = f.Execution.Calls.Select(c => c.Statement.Text).ToList();
        Assert.Equal([CreateText, CommentText, DropText], texts);
        var drop = f.Execution.Calls[^1].Statement;
        Assert.Equal("DROP", drop.Verb);
        Assert.Equal("ORDERS", drop.DangerousTarget);
        Assert.Same(f.Target, f.Execution.Calls[^1].Session);
        var dropAt = f.All.IndexOf("EXEC " + DropText);
        Assert.True(dropAt > 0);
        Assert.Contains("CTL COMMIT", f.All.Take(dropAt)); // DROP 은 DDL(묵시 커밋)이라 대상 트랜잭션을 끝낸 뒤에 보낸다

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.Contains("배치를 넣지 못했습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains("DROP … PURGE", table.Message, StringComparison.Ordinal);
        Assert.Equal(-1, table.TargetRows); // 지운 표는 건수를 세지 않는다
        var notice = Assert.Single(result.Notices);
        Assert.Contains("지웠습니다", notice, StringComparison.Ordinal);
        Assert.Contains("DST.ORDERS", notice, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Source_read_failure_also_drops_the_created_table()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true);
        f.Bulk.SourceReadFails = true;

        var result = await f.RunAsync();

        Assert.Equal([CreateText, CommentText, DropText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        Assert.DoesNotContain(f.Order, x => x.StartsWith("BULK ", StringComparison.Ordinal)); // 한 행도 넣지 않았다
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.Contains("정책이 원본 읽기를 거부했습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains("지웠습니다", Assert.Single(result.Notices), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drop_that_fails_tells_the_user_to_drop_by_hand_and_keeps_the_original_failure()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true);
        f.Bulk.OnArray = c => c.ColumnCount > 0 ? new BulkResult(ExecutionStatus.Denied, 0) { Policy = new PolicyDecision(PolicyDecisionKind.Deny, Message: "insert denied") } : null;
        f.FailExecution = text => text == DropText ? new DbError("ORA-00054", "resource busy") : null;

        var result = await f.RunAsync();

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome); // 원래 실패 그대로
        Assert.Contains("배치를 넣지 못했습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains("지우지 못했습니다. 직접 지우세요: " + DropText, table.Message, StringComparison.Ordinal);
        var notice = Assert.Single(result.Notices);
        Assert.Contains("직접 지우세요: " + DropText, notice, StringComparison.Ordinal);
        Assert.DoesNotContain("지웠습니다", notice, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal(3, f.Execution.Calls.Count); // DROP 을 한 번만 시도하고 되풀이하지 않는다
    }

    [Fact]
    public async Task Create_that_fails_because_the_name_is_taken_sends_no_drop_and_loads_nothing()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true);
        f.FailExecution = text => text.StartsWith("CREATE TABLE", StringComparison.Ordinal) ? NameInUse : null;

        var result = await f.RunAsync();

        // 남이 만든 같은 이름 표일 수 있으니 지우지 않는다(만든 표가 아니다).
        Assert.Equal([CreateText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        Assert.DoesNotContain(f.All, x => x.Contains("DROP", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Order, x => x.StartsWith("BULK ", StringComparison.Ordinal));
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.Contains("대상에 테이블을 만들지 못했습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains("ORA-00955", table.Message, StringComparison.Ordinal);
        Assert.Equal(-1, table.TargetRows); // 만들지 못한 표는 건수를 세지 않는다
        Assert.Empty(result.Notices);
        Assert.Equal(CopyOutcome.Failed, result.Outcome);
    }

    // ---- 실행기: 표보다 먼저 만드는 시퀀스(SQ1) ----

    [Fact]
    public async Task Planned_sequence_is_created_right_before_the_create_table_and_noted_on_the_table()
    {
        // START WITH 100 > 키 최댓값(기본 2): 적재 뒤 맞출 것이 없다.
        var sequence = Sequence(start: "100");
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sequences: [sequence], idDefault: SourceSequenceDefault);

        var result = await f.RunAsync();

        // 보낸 순서: CREATE SEQUENCE → CREATE TABLE(DEFAULT 는 대상 계정 시퀀스) → 표 주석 → INSERT 배열 적재.
        const string SequenceText = "CREATE SEQUENCE \"DST\".\"ORDERS_SEQ\" MINVALUE 1 NOMAXVALUE INCREMENT BY 1 START WITH 100 CACHE 20 NOORDER NOCYCLE";
        Assert.Equal(SequenceText, sequence.CreateDdl);
        Assert.Equal("EXEC " + SequenceText, f.Order[0]);
        Assert.Equal("EXEC " + CreateWithDefaultText, f.Order[1]);
        Assert.Equal("EXEC " + CommentText, f.Order[2]);
        Assert.StartsWith("BULK INSERT INTO \"DST\".\"ORDERS\"", f.Order[3], StringComparison.Ordinal);
        Assert.Single(f.Execution.Calls, c => c.Statement.Text.StartsWith("CREATE SEQUENCE", StringComparison.Ordinal)); // 한 번만 보낸다
        Assert.DoesNotContain(f.All, x => x.Contains("ALTER SEQUENCE", StringComparison.Ordinal) || x.Contains(".NEXTVAL FROM", StringComparison.Ordinal)
                                                                                               || x.Contains("DROP", StringComparison.Ordinal));

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Contains("시퀀스 DST.ORDERS_SEQ 을(를) 원본 속성으로 만들었습니다(START WITH 100)", table.Message, StringComparison.Ordinal);
        Assert.Contains("원본 구조로 만들었습니다", table.Message, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Notices);
        Assert.Contains(SequenceText, f.RunRecord.Statements); // 만든 시퀀스도 실행 기록에 남는다
    }

    [Fact]
    public async Task Created_sequence_is_moved_past_the_loaded_max_key_after_the_commit()
    {
        // START WITH 10, INCREMENT BY 5, 적재한 키 최댓값 50: NEXTVAL(10) → INCREMENT BY 40 → NEXTVAL(50) → INCREMENT BY 5. 다음 값 55.
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sequences: [Sequence(start: "10", increment: "5")], idDefault: SourceSequenceDefault);
        f.Bulk.MaxKey = 50L;

        var result = await f.RunAsync();

        var steps = f.All.Where(x => x.Contains("ORDERS_SEQ", StringComparison.Ordinal) || x.Contains("SELECT MAX(", StringComparison.Ordinal))
            .Where(x => !x.Contains("CREATE ", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            "QUERY " + MaxKeyText,
            "QUERY " + NextValueText,
            "EXEC ALTER SEQUENCE \"DST\".\"ORDERS_SEQ\" INCREMENT BY 40",
            "QUERY " + NextValueText,
            "EXEC ALTER SEQUENCE \"DST\".\"ORDERS_SEQ\" INCREMENT BY 5",
        ], steps);

        // 맞추기는 대상 트랜잭션을 끝낸 뒤(ALTER 는 DDL, 묵시 커밋)다.
        var commitAt = f.All.LastIndexOf("CTL COMMIT");
        Assert.True(commitAt >= 0);
        Assert.True(f.All.IndexOf("QUERY " + MaxKeyText) > commitAt);

        // NEXTVAL 은 방언의 여러 값 조회를 개수 1 로 부른다.
        var nexts = f.Bulk.Queries.Where(q => q.Text == NextValueText).ToList();
        Assert.Equal(2, nexts.Count);
        Assert.All(nexts, q => Assert.Equal(1m, q.Binds[CopyCommonStatements.NextValuesBind].Value));

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        var notice = Assert.Single(result.Notices);
        Assert.Equal("시퀀스 DST.ORDERS_SEQ: 적재한 키 최댓값(ORDERS.ID = 50) 뒤로 맞췄습니다(다음 값 55).", notice);
        Assert.DoesNotContain(f.All, x => x.Contains("DROP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Created_sequence_starting_at_the_max_key_consumes_one_value_without_altering_the_increment()
    {
        // START WITH = 키 최댓값: NEXTVAL 한 번(= 50)이면 다음 값이 51 이다. INCREMENT BY 를 바꾸지 않는다.
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sequences: [Sequence(start: "50")], idDefault: SourceSequenceDefault);
        f.Bulk.MaxKey = 50L;

        var result = await f.RunAsync();

        Assert.Single(f.All, x => x == "QUERY " + NextValueText);
        Assert.DoesNotContain(f.All, x => x.Contains("ALTER SEQUENCE", StringComparison.Ordinal));
        Assert.Contains("뒤로 맞췄습니다(다음 값 51)", Assert.Single(result.Notices), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Created_sequence_already_past_the_max_key_is_left_alone()
    {
        // START WITH 100 > 키 최댓값 50: 최댓값만 읽고 NEXTVAL·ALTER 는 보내지 않는다.
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sequences: [Sequence(start: "100")], idDefault: SourceSequenceDefault);
        f.Bulk.MaxKey = 50L;

        var result = await f.RunAsync();

        Assert.Single(f.All, x => x == "QUERY " + MaxKeyText);
        Assert.DoesNotContain(f.All, x => x.Contains(".NEXTVAL FROM", StringComparison.Ordinal) || x.Contains("ALTER SEQUENCE", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notices, x => x.Contains("맞췄습니다", StringComparison.Ordinal));
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
    }

    [Fact]
    public async Task Load_failure_drops_the_created_sequence_after_purging_the_table()
    {
        var sequence = Sequence(start: "1");
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sequences: [sequence], idDefault: SourceSequenceDefault);
        f.Bulk.OnArray = c => c.ColumnCount > 0 ? new BulkResult(ExecutionStatus.Denied, 0) { Policy = new PolicyDecision(PolicyDecisionKind.Deny, Message: "insert denied") } : null;

        var result = await f.RunAsync();

        // 만들고 → 넣기 실패 → 대상 트랜잭션을 끝낸 뒤 표 DROP … PURGE → 쓰는 표가 없어진 시퀀스 DROP.
        Assert.Equal([sequence.CreateDdl, CreateWithDefaultText, CommentText, DropText, DropSequenceText],
            f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        var drop = f.Execution.Calls[^1].Statement;
        Assert.Equal("DROP", drop.Verb);
        Assert.Equal("ORDERS_SEQ", drop.DangerousTarget);
        Assert.Same(f.Target, f.Execution.Calls[^1].Session);
        Assert.Contains("CTL COMMIT", f.All.Take(f.All.IndexOf("EXEC " + DropSequenceText)));
        // 지운 시퀀스는 맞추지 않는다(키 최댓값도 읽지 않는다).
        Assert.DoesNotContain(f.All, x => x.StartsWith("QUERY ", StringComparison.Ordinal) || x.Contains("ALTER SEQUENCE", StringComparison.Ordinal));

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.Equal(2, result.Notices.Count);
        Assert.Contains(result.Notices, x => x.Contains("DROP … PURGE", StringComparison.Ordinal) && x.Contains("DST.ORDERS", StringComparison.Ordinal));
        var notice = Assert.Single(result.Notices, x => x.StartsWith("시퀀스 DST.ORDERS_SEQ:", StringComparison.Ordinal));
        Assert.Contains("이번에 만든 시퀀스를 지웠습니다", notice, StringComparison.Ordinal);
        Assert.Equal(CopyOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Sequence_that_cannot_be_created_drops_the_column_default_and_degrades_the_table()
    {
        var sequence = Sequence(start: "1");
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sequences: [sequence], idDefault: SourceSequenceDefault);
        f.FailExecution = text => text.StartsWith("CREATE SEQUENCE", StringComparison.Ordinal) ? NoPrivilege : null;

        var result = await f.RunAsync();

        // CREATE SEQUENCE 실패 → 그 시퀀스를 쓰는 열(ID)의 DEFAULT 를 빼고 만든다 → 적재는 그대로.
        Assert.Equal([sequence.CreateDdl, CreateText, CommentText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        Assert.DoesNotContain(f.All, x => x.Contains("DEFAULT", StringComparison.Ordinal));
        Assert.Contains(f.Order, x => x.StartsWith("BULK INSERT INTO", StringComparison.Ordinal));
        // 만들지 못한 시퀀스는 맞추지도 지우지도 않는다.
        Assert.DoesNotContain(f.All, x => x.Contains("DROP", StringComparison.Ordinal) || x.Contains("ALTER SEQUENCE", StringComparison.Ordinal)
                                                                                      || x.StartsWith("QUERY ", StringComparison.Ordinal));

        var table = Assert.Single(result.Tables);
        Assert.NotEqual(CopyOutcome.Failed, table.Outcome);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(2, table.TargetRows);
        Assert.Contains("시퀀스 DST.ORDERS_SEQ 을(를) 만들지 못해(ORA-01031", table.Message, StringComparison.Ordinal);
        Assert.Contains("열 ID 의 기본값을 빼고 테이블을 만들었습니다", table.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("원본 속성으로 만들었습니다", table.Message, StringComparison.Ordinal);
        var notice = Assert.Single(result.Notices);
        Assert.Contains("기본값을 빼고", notice, StringComparison.Ordinal);
        Assert.Contains("다시 거세요: " + sequence.CreateDdl, notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_only_sequence_that_cannot_be_created_keeps_the_create_table_and_tells_to_create_it_by_hand()
    {
        // 열 기본값이 쓰지 않는(트리거만 쓰는) 시퀀스: CREATE TABLE 은 그대로, 직접 만들 문장을 알린다.
        var sequence = Sequence(start: "1", defaultColumn: false);
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sequences: [sequence]);
        f.FailExecution = text => text.StartsWith("CREATE SEQUENCE", StringComparison.Ordinal) ? NoPrivilege : null;

        var result = await f.RunAsync();

        Assert.Equal([sequence.CreateDdl, CreateText, CommentText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Contains("트리거가 쓰는 시퀀스 DST.ORDERS_SEQ 을(를) 만들지 못했습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains("직접 만드세요: " + sequence.CreateDdl, Assert.Single(result.Notices), StringComparison.Ordinal);
        Assert.DoesNotContain(f.All, x => x.Contains("DROP", StringComparison.Ordinal));
    }

    /// <summary>대상 계정 DST 에 만들 ORDERS_SEQ. 기본은 ID 열 기본값이 쓰는 시퀀스(<paramref name="defaultColumn"/>), 맞추기 기준 열은 ID.</summary>
    private static CopySequencePlan Sequence(string start, string increment = "1", bool defaultColumn = true) =>
        new("DST", "ORDERS_SEQ", new SequenceSpec("1", "9999999999999999999999999999", increment, "20", Cycle: false, Order: false, start))
        {
            KeyColumns = ["ID"],
            DefaultColumns = defaultColumn ? ["ID"] : [],
        };

    // ---- 실행기: 새 표의 PK·UK·인덱스는 적재 뒤(PF4a) ----

    private const string KeyText = "ALTER TABLE \"DST\".\"ORDERS\" ADD CONSTRAINT \"PK_ORDERS\" PRIMARY KEY (\"ID\")";

    [Fact]
    public async Task Primary_key_of_a_new_table_is_added_after_the_load_and_commit_before_the_companions()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sourcePrimaryKey: true, companions: Companions());

        var result = await f.RunAsync();

        // CREATE TABLE 에는 PK 가 없다(인덱스 유지 없이 넣는다). 적재·COMMIT 뒤 PK → 딸린 객체(함수 기반 인덱스 → 트리거 → 권한 → 작업 밖 FK).
        Assert.Equal([CreateText, CommentText, KeyText, FunctionIndexText, TriggerText, GrantText, OuterForeignKeyText],
            f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        AssertCommittedBefore(f, "EXEC " + KeyText);
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task Primary_key_that_cannot_be_added_after_the_load_fails_the_run_and_leaves_the_statement_in_a_notice()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sourcePrimaryKey: true);
        f.FailExecution = text => text == KeyText ? new DbError("ORA-02437", "cannot validate - primary key violated") : null;

        var result = await f.RunAsync();

        // 키 없는 표가 남았다: 표는 적재를 끝내 부분 실패로 보이고, 실행 결과는 되살리기 실패와 같이 실패다. 만든 표는 지우지 않는다.
        const string Message = "적재 뒤 PK·UK 를 만들지 못했습니다(ORA-02437: cannot validate - primary key violated). 키가 겹치는 행을 고친 뒤 직접 실행하세요: " + KeyText;
        Assert.Equal([CreateText, CommentText, KeyText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(2, table.TargetRows);
        Assert.Contains(Message, table.Message, StringComparison.Ordinal);
        Assert.Equal("ORDERS: " + Message, Assert.Single(result.Notices));
        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal(CopyOutcome.Failed, f.RunRecord.Completed);
        Assert.DoesNotContain(f.All, x => x.Contains("DROP", StringComparison.Ordinal));
    }

    // ---- 실행기: 새 표에 딸린 객체(SQ2, 조정 결정 78) ----

    private const string FunctionIndexText = "CREATE INDEX \"DST\".\"IX_ORD_UPNAME\" ON \"DST\".\"ORDERS\" (UPPER(\"NAME\"))";
    private const string TriggerText = "CREATE OR REPLACE TRIGGER \"DST\".\"TRG_ORD\" BEFORE INSERT ON \"DST\".\"ORDERS\" FOR EACH ROW BEGIN :NEW.NAME := UPPER(:NEW.NAME); END;";
    private const string DisableTriggerText = "ALTER TRIGGER \"DST\".\"TRG_ORD\" DISABLE";
    private const string GrantText = "GRANT SELECT ON \"DST\".\"ORDERS\" TO \"APP_USER\"";
    private const string OuterForeignKeyText = "ALTER TABLE \"DST\".\"ORDERS\" ADD CONSTRAINT \"FK_ORD_CUST\" FOREIGN KEY (\"NAME\") REFERENCES \"DST\".\"CUSTOMERS\" (\"NAME\")";

    /// <summary>사전 점검이 정한 모양의 딸린 객체 한 벌(대상 소유자로 바꾼 글).</summary>
    private static CompanionDdl Companions() => new(
        Triggers: [TriggerText],
        TriggerObjects: [new DisabledObject(DisabledKind.Trigger, "DST", "ORDERS", "TRG_ORD")],
        Grants: [GrantText],
        FunctionIndexes: [FunctionIndexText],
        OuterForeignKeys: [("CUSTOMERS", OuterForeignKeyText, "FK_ORD_CUST")]);

    /// <summary><paramref name="entry"/> 앞, 마지막 배열 적재 뒤에 대상 COMMIT 이 있다(딸린 객체·적재 뒤 키는 대상 트랜잭션을 끝낸 뒤에 보낸다).</summary>
    private static void AssertCommittedBefore(Fixture f, string entry)
    {
        var at = f.All.IndexOf(entry);
        Assert.True(at > 0, entry);
        var lastLoad = f.All.FindLastIndex(at, x => x.StartsWith("BULK ", StringComparison.Ordinal));
        Assert.True(lastLoad >= 0, "적재가 먼저다");
        Assert.Contains("CTL COMMIT", f.All.Skip(lastLoad).Take(at - lastLoad));
    }

    [Fact]
    public async Task Companions_are_sent_after_the_final_commit_in_index_trigger_grant_foreign_key_order()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, companions: Companions());

        var result = await f.RunAsync();

        Assert.Equal([CreateText, CommentText, FunctionIndexText, TriggerText, GrantText, OuterForeignKeyText],
            f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        AssertCommittedBefore(f, "EXEC " + FunctionIndexText);
        Assert.All(f.Execution.Calls.Skip(2), c => Assert.Same(f.Target, c.Session));
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Notices);
        Assert.Contains(GrantText, f.RunRecord.Statements); // 보낸 딸린 객체도 실행 기록에 남는다
    }

    [Theory]
    [InlineData(FunctionIndexText, "함수 기반 인덱스를 만들지 못해 뺐습니다")]
    [InlineData(TriggerText, "원본 트리거를 만들지 못했습니다")]
    [InlineData(GrantText, "원본 권한을 붙이지 못했습니다(받는 사용자·롤이 대상에 없을 수 있습니다)")]
    [InlineData(OuterForeignKeyText, "작업 밖 부모 테이블을 참조하는 원본 FK 를 붙이지 못해 뺐습니다")]
    public async Task Companion_that_fails_degrades_the_table_with_a_notice_and_the_rest_are_still_sent(string failing, string what)
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, companions: Companions());
        f.FailExecution = text => text == failing ? NoPrivilege : null;

        var result = await f.RunAsync();

        // 하나를 만들지 못해도 나머지는 보낸다. 표는 실패가 아니라 부분 실패(데이터는 들어 있다), 알림에 직접 실행할 문장이 남는다.
        Assert.Equal([CreateText, CommentText, FunctionIndexText, TriggerText, GrantText, OuterForeignKeyText],
            f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        var message = $"{what}(ORA-01031: insufficient privileges). 직접 실행하세요: {failing}";
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(2, table.TargetRows);
        Assert.Contains(message, table.Message, StringComparison.Ordinal);
        Assert.Equal("ORDERS: " + message, Assert.Single(result.Notices));
        Assert.Equal(CopyOutcome.PartiallyFailed, result.Outcome);
        Assert.DoesNotContain(f.All, x => x.Contains("DROP", StringComparison.Ordinal)); // 적재한 표는 지우지 않는다
    }

    [Fact]
    public async Task Source_trigger_that_compiles_with_errors_is_kept_but_disabled_and_only_degrades_the_table()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, companions: Companions());
        f.FailExecution = text => text == TriggerText
            ? new DbError("ORA-24344", "success with compilation error") { Kind = DbErrorKind.CompiledWithErrors }
            : null;

        var result = await f.RunAsync();

        // 만들어졌지만 INVALID: 켜진 채 두면 이 표의 DML 이 막히므로 바로 끈다(지우지 않는다). 그 뒤 권한·FK 는 그대로 보낸다.
        Assert.Equal([CreateText, CommentText, FunctionIndexText, TriggerText, DisableTriggerText, GrantText, OuterForeignKeyText],
            f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        const string Message = "트리거 DST.TRG_ORD 은 원본에서 옮겨 만들었지만 대상에서 컴파일되지 않아 컴파일 오류(ORA-24344)로 INVALID 입니다. 꺼 두었으니 고친 뒤 켜세요.";
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Contains(Message, table.Message, StringComparison.Ordinal);
        Assert.Equal("ORDERS: " + Message, Assert.Single(result.Notices));
        Assert.Equal(CopyOutcome.PartiallyFailed, result.Outcome); // 대상 쪽 되살리기와 달리 실행 실패가 아니다
    }

    [Fact]
    public async Task Load_failure_purges_the_new_table_and_sends_no_companion()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sourcePrimaryKey: true, companions: Companions());
        f.Bulk.OnArray = c => c.ColumnCount > 0 ? new BulkResult(ExecutionStatus.Denied, 0) { Policy = new PolicyDecision(PolicyDecisionKind.Deny, Message: "insert denied") } : null;

        var result = await f.RunAsync();

        // 지운 표에는 적재 뒤 키도 딸린 객체도 보내지 않는다.
        Assert.Equal([CreateText, CommentText, DropText], f.Execution.Calls.Select(c => c.Statement.Text).ToList());
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.DoesNotContain("직접 실행하세요", table.Message, StringComparison.Ordinal);
        Assert.Contains("지웠습니다", Assert.Single(result.Notices), StringComparison.Ordinal);
    }

    // ---- 실행기: 만들 수 없는 대상 ----

    [Fact]
    public async Task Non_development_target_is_refused_before_anything_is_sent()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, environment: ConnectionEnvironment.Test);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.RunAsync());

        Assert.Contains("개발 접속이 아니라", ex.Message, StringComparison.Ordinal);
        Assert.Contains("없으면 만들기", ex.Message, StringComparison.Ordinal);
        Assert.Empty(f.All);
    }

    [Fact]
    public async Task Create_by_another_account_than_the_target_owner_is_refused_before_anything_is_sent()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, targetUser: "OTHER");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.RunAsync());

        Assert.Contains("대상 소유자(DST) 계정으로 접속해야 합니다(지금 OTHER)", ex.Message, StringComparison.Ordinal);
        Assert.Empty(f.All);
    }

    // ---- 사전 점검: 대상에 표가 없을 때 ----

    [Fact]
    public async Task Precheck_plans_the_create_on_a_development_target_instead_of_reporting_a_missing_target()
    {
        var p = new PrecheckFixture(createIfMissing: true, targetHasTable: false);

        var report = await p.RunAsync();

        Assert.False(report.HasErrors);
        Assert.DoesNotContain(report.Items, i => i.Code == "TargetMissing");
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CreateTable);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Equal("ORDERS", item.Table);
        Assert.Contains("원본 구조로 만든 뒤 넣습니다", item.Message, StringComparison.Ordinal);
        Assert.Contains("새 표라 대상을 지우지 않습니다", item.Message, StringComparison.Ordinal);
        Assert.Contains("DROP … PURGE", item.Message, StringComparison.Ordinal);

        var plan = Assert.Single(report.Order);
        Assert.True(plan.CreateMissing);
        Assert.Equal("DST", plan.Target.Table.Owner); // 만들 구조는 대상 계정 이름이다
        Assert.Equal(2, report.EstimatedRows["ORDERS"]);

        // 미리 보기: 만드는 문장 → 적재 실패 때 지우는 DROP … PURGE. 새 표라 대상 DELETE 는 없다.
        var target = report.Preview.Where(x => x.Side == PreviewSide.Target).Select(x => x.Sql).ToList();
        var createAt = target.FindIndex(x => x.StartsWith("CREATE TABLE \"DST\".\"ORDERS\"", StringComparison.Ordinal));
        var dropAt = target.IndexOf(DropText);
        Assert.True(createAt >= 0);
        Assert.True(dropAt > createAt);
        Assert.DoesNotContain(target, x => x.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Contains(report.Preview, x => x.Side == PreviewSide.Source && x.Sql.StartsWith("SELECT", StringComparison.Ordinal));

        // 지우는 문장은 정책이 위험 명령으로 볼 수 있게(Verb·대상) 평가로 넘어갔다 → 화면이 미리 받은 확인으로 넘긴다.
        Assert.Contains(p.Policy.Evaluated, s => s.Verb == "DROP" && s.DangerousTarget == "ORDERS" && s.Text == DropText);

        // 실행기가 같은 보고서를 받아들인다(DELETE 미리 보기가 없어도 만드는 표는 통과).
        CopyRunner.EnsureReportMatchesJob(p.Job, report, new ResolvedPeriod(null, null));
    }

    [Fact]
    public async Task Precheck_refuses_to_create_on_a_non_development_target_with_the_development_only_wording()
    {
        var p = new PrecheckFixture(createIfMissing: true, targetHasTable: false, environment: ConnectionEnvironment.Test);

        var report = await p.RunAsync();

        Assert.True(report.HasErrors);
        var item = Assert.Single(report.Items, i => i.Code == "TargetMissing");
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Contains("「없으면 만들기」를 쓸 수 없습니다", item.Message, StringComparison.Ordinal);
        Assert.Contains("개발 접속에서만 만들 수 있습니다", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.CreateTable);
        Assert.Empty(report.Order);
        Assert.DoesNotContain(report.Preview, x => x.Sql.Contains("CREATE TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Precheck_keeps_the_missing_target_error_when_the_option_is_off()
    {
        var p = new PrecheckFixture(createIfMissing: false, targetHasTable: false);

        var report = await p.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == "TargetMissing");
        Assert.DoesNotContain("없으면 만들기", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.CreateTable);
        Assert.Empty(report.Order);
    }

    [Fact]
    public async Task Precheck_ignores_the_option_when_the_target_table_exists()
    {
        var p = new PrecheckFixture(createIfMissing: true, targetHasTable: true);

        var report = await p.RunAsync();

        Assert.DoesNotContain(report.Items, i => i.Code is "TargetMissing" or CopyCheckCodes.CreateTable);
        var plan = Assert.Single(report.Order);
        Assert.False(plan.CreateMissing);
        var target = report.Preview.Where(x => x.Side == PreviewSide.Target).Select(x => x.Sql).ToList();
        Assert.Contains("DELETE FROM \"DST\".\"ORDERS\"", target); // 지금 동작 그대로: 지우고 넣는다
        Assert.DoesNotContain(target, x => x.Contains("CREATE TABLE", StringComparison.Ordinal) || x.Contains("PURGE", StringComparison.Ordinal));
        CopyRunner.EnsureReportMatchesJob(p.Job, report, new ResolvedPeriod(null, null));
    }

    [Fact]
    public async Task Precheck_refuses_when_a_non_table_object_has_the_same_name()
    {
        var p = new PrecheckFixture(createIfMissing: true, targetHasTable: false, otherObjectExists: true);

        var report = await p.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == "TargetMissing");
        Assert.Contains("같은 이름의 테이블이 아닌 객체가 있습니다", item.Message, StringComparison.Ordinal);
        Assert.Empty(report.Order);
    }

    [Fact]
    public async Task Precheck_reports_missing_create_table_privilege_as_an_error()
    {
        var p = new PrecheckFixture(createIfMissing: true, targetHasTable: false, canCreateTable: false);

        var report = await p.RunAsync();

        Assert.True(report.HasErrors);
        var item = Assert.Single(report.Items, i => i.Code == CopyCheckCodes.CreateNoPrivilege);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Contains("CREATE TABLE", item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == CopyCheckCodes.CreateTable);
    }

    // ---- 원본·대상 부품 나누기(xdb-copy XC2) ----

    [Fact]
    public async Task Runner_sends_read_only_with_the_source_dialect_to_the_source_and_savepoints_with_the_target_dialect_to_the_target()
    {
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: false,
            sourceParts: Parts(new NoCatalog(), new FakePrecheckDialect(), new FakeDialect("SRC")),
            targetParts: Parts(new NoCatalog(), new FakePrecheckDialect(), new FakeDialect("TGT")));

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        var readOnly = Assert.Single(f.Bulk.Calls, c => c.Text.Contains("SET TRANSACTION READ ONLY", StringComparison.Ordinal));
        Assert.Equal("SRC SET TRANSACTION READ ONLY", readOnly.Text);
        Assert.Same(f.Source, readOnly.Session);
        var savepoints = f.Bulk.Calls.Where(c => c.Text.Contains("SAVEPOINT", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(savepoints);
        Assert.All(savepoints, c =>
        {
            Assert.StartsWith("TGT ", c.Text, StringComparison.Ordinal);
            Assert.Same(f.Target, c.Session);
        });
    }

    [Fact]
    public async Task Precheck_takes_the_read_consistency_parameter_and_hint_from_the_source_dialect()
    {
        // 대상 벌의 방언은 파라미터가 없다(null). 원본 벌이 묻고 원본 세션에서 읽은 값(900)으로 안내한다.
        var p = new PrecheckFixture(createIfMissing: false, targetHasTable: true,
            sourceParts: Parts(new FakeCatalog(), new FakePrecheckDialect("undo_retention", "SRC "), new FakeDialect()));

        var report = await p.RunAsync();

        var item = Assert.Single(report.Items, i => i.Code == "ReadConsistency");
        Assert.Equal("SRC 읽기 일관성 900", item.Message);
    }

    [Fact]
    public async Task Runner_asks_the_source_dialect_for_server_names_and_the_source_recreate_step_for_foreign_keys_on_the_source_session()
    {
        // 서버가 지은 제약 이름인지는 원본 이름을 보는 판정이라 원본 방언이, 작업 안 FK 는 원본 세션에서 원본 벌의 받아 두기가 읽는다.
        var sourceRecreate = new FakeRecreateStep();
        var targetRecreate = new FakeRecreateStep();
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: true, sourcePrimaryKey: true,
            sourceParts: Parts(new NoCatalog(), new FakePrecheckDialect(), new FakeDialect(serverName: "PK_ORDERS"), recreate: sourceRecreate),
            targetParts: Parts(new NoCatalog(), new FakePrecheckDialect(), new FakeDialect(), recreate: targetRecreate));

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        // 새 표의 PK 는 적재 뒤에 붙인다(PF4a): CREATE TABLE 에는 없고, 마지막 COMMIT 뒤 ALTER TABLE … ADD PRIMARY KEY 로 간다.
        Assert.DoesNotContain("PRIMARY KEY", f.Execution.Calls[0].Statement.Text, StringComparison.Ordinal);
        const string ServerNamedKeyText = "ALTER TABLE \"DST\".\"ORDERS\" ADD PRIMARY KEY (\"ID\")"; // 원본 방언이 PK_ORDERS 를 서버 이름으로 봤다(CONSTRAINT 없음, 대상 방언은 아니라고 본다)
        Assert.Equal(ServerNamedKeyText, Assert.Single(f.Execution.Calls, c => c.Statement.Text.Contains("PRIMARY KEY", StringComparison.Ordinal)).Statement.Text);
        AssertCommittedBefore(f, "EXEC " + ServerNamedKeyText);
        Assert.Same(f.Source, sourceRecreate.ForeignKeySession);
        Assert.Null(targetRecreate.ForeignKeySession);
    }

    [Fact]
    public async Task Runner_shapes_rows_with_the_source_codec_not_the_target_codec()
    {
        // 마스킹을 건 열의 형식 해석(RowShape.Create → KindOf)은 원본 값을 보므로 원본 벌의 코덱이 한다.
        var sourceCodec = new FakeCodec();
        var targetCodec = new FakeCodec();
        var f = new Fixture(TargetAction.DeleteAllInsert, createMissing: false, masks: [new MaskRule("NAME", MaskKind.Null)],
            sourceParts: Parts(new NoCatalog(), new FakePrecheckDialect(), new FakeDialect(), codec: sourceCodec),
            targetParts: Parts(new NoCatalog(), new FakePrecheckDialect(), new FakeDialect(), codec: targetCodec));

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Contains("VARCHAR2", sourceCodec.KindOfCalls);
        Assert.Empty(targetCodec.KindOfCalls);
    }

    [Fact]
    public async Task Precheck_reads_source_access_and_the_read_consistency_parameter_through_the_source_catalog()
    {
        var sourceLog = new List<string>();
        var targetLog = new List<string>();
        var p = new PrecheckFixture(createIfMissing: false, targetHasTable: true,
            sourceParts: Parts(new FakeCatalog(sourceLog), new FakePrecheckDialect("undo_retention"), new FakeDialect()),
            targetParts: Parts(new FakeCatalog(targetLog), new FakePrecheckDialect(), new FakeDialect()));

        await p.RunAsync();

        // 원본 세션이 어떤 계정·권한인지는 원본 카탈로그로, 같은 시점 읽기 파라미터도 원본 카탈로그로 묻는다. 대상 카탈로그는 둘 다 묻지 않는다.
        Assert.Single(sourceLog, x => x == "copy.session_user");
        Assert.Single(sourceLog, x => x == "copy.parameter_value");
        Assert.Single(targetLog, x => x == "copy.session_user");
        Assert.DoesNotContain("copy.parameter_value", targetLog);
    }

    /// <summary>시험 부품 한 벌. 분류기는 늘 같은 가짜이고, 코덱·다시 만들기 받아 두기는 주지 않으면 새 가짜다.</summary>
    private static CopyDbmsParts Parts(Oranda.Core.Catalog.ISqlCatalog catalog, ICopyPrecheckDialect precheckDialect, ICopyDialect dialect,
                                       IBulkValueCodec? codec = null, ICopyRecreateStep? recreate = null) =>
        new(Dbms.Oracle, catalog, new FakeClassifier(), dialect, precheckDialect, codec ?? new FakeCodec(), recreate ?? new FakeRecreateStep());

    // ---- 가짜 협력자 ----

    private sealed class PrecheckFixture
    {
        public PrecheckFixture(bool createIfMissing, bool targetHasTable, ConnectionEnvironment environment = ConnectionEnvironment.Development,
                               bool otherObjectExists = false, bool canCreateTable = true, CopyDbmsParts? sourceParts = null, CopyDbmsParts? targetParts = null)
        {
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==",
                Tables = [new TableRule("ORDERS", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = createIfMissing }],
            };
            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile(environment) with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            var metadata = new FakeMetadataProvider();
            metadata.Tables[new DbObjectRef("SRC", "ORDERS", "TABLE")] = OrdersWithPrimaryKey("SRC");
            if (targetHasTable)
            {
                metadata.Tables[new DbObjectRef("DST", "ORDERS", "TABLE")] = OrdersWithPrimaryKey("DST");
            }

            Policy = new RecordingPolicy();
            var parts = Parts(new FakeCatalog(), new FakePrecheckDialect(), new FakeDialect());
            Service = new CopyPrecheckService(new FakePrecheckBulk(Source, Target, targetHasTable, otherObjectExists, canCreateTable), metadata, Policy,
                sourceParts ?? parts, targetParts ?? parts);
        }

        public CopyJob Job { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public RecordingPolicy Policy { get; }

        private CopyPrecheckService Service { get; }

        public Task<PrecheckReport> RunAsync() => Service.RunAsync(new CopySessions(Source, Target), Job, new ResolvedPeriod(null, null), null, CancellationToken.None);

        private static TableInfo OrdersWithPrimaryKey(string owner) =>
            new(new DbObjectRef(owner, "ORDERS", "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    /// <summary>카탈로그 글 대신 id 를 그대로 글로 돌려준다(가짜 대량 읽기가 id 로 답한다).</summary>
    private sealed class FakeCatalog(List<string>? log = null) : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server)
        {
            log?.Add(id);
            return new(new Oranda.Core.Catalog.CatalogHeader(id, 11, [], Oranda.Core.Catalog.CatalogLicense.None, Oranda.Core.Catalog.CatalogRac.V, id + ".sql"), id);
        }
    }

    /// <summary>점검 조회(id)에 답한다. 모르는 id 는 행 없음, <c>SELECT COUNT(*)</c> 는 2.</summary>
    private sealed class FakePrecheckBulk(ISession source, ISession target, bool targetHasTable, bool otherObjectExists, bool canCreateTable) : IBulkExecutionService
    {
        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var isTarget = ReferenceEquals(s, target);
            object?[][] rows = query.Text switch
            {
                "copy.session_user" => [[isTarget ? "DST" : "SRC"]],
                "copy.parameter_value" when !isTarget => [[900L]],
                "copy.session_privs" when isTarget && canCreateTable => [["CREATE TABLE"]],
                "copy.target_is_table" => [[targetHasTable ? 1L : 0L]],
                "copy.object_exists" => [[otherObjectExists ? 1L : 0L]],
                _ when query.Text.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal) && ReferenceEquals(s, source) => [[2L]],
                _ => [],
            };
            foreach (var row in rows)
            {
                yield return row;
            }
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPolicy : IExecutionPolicy
    {
        public List<SqlStatement> Evaluated { get; } = [];

        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct)
        {
            Evaluated.Add(stmt);
            return ValueTask.FromResult(PolicyDecision.Allowed);
        }
    }

    /// <summary><paramref name="readConsistencyParameter"/> 를 주면 그 이름으로 원본 파라미터를 묻고, 안내 글 앞에 <paramref name="hintTag"/> 와 뒤에 읽은 값을 붙인다.</summary>
    private sealed class FakePrecheckDialect(string? readConsistencyParameter = null, string hintTag = "") : ICopyPrecheckDialect
    {
        public bool SupportsReadPrivilege => false;

        public string? RecreateBlockedReason(ISession target) => null;

        public Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                       CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => [];

        public string RecycleBinNote(string? tablespace) => string.Empty;

        public string? ReadConsistencyParameter => readConsistencyParameter;

        public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention) =>
            (CheckLevel.Pass, hintTag + "읽기 일관성" + (retention is { } r ? " " + r.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty));

        public string ChildRecordFoundError => "ORA-02292";
    }

    private sealed class Fixture
    {
        public Fixture(TargetAction action, bool createMissing, ConnectionEnvironment environment = ConnectionEnvironment.Development,
                       string targetUser = "DST", CopyDbmsParts? sourceParts = null, CopyDbmsParts? targetParts = null,
                       IReadOnlyList<MaskRule>? masks = null, bool sourcePrimaryKey = false,
                       IReadOnlyList<CopySequencePlan>? sequences = null, string? idDefault = null, CompanionDdl? companions = null)
        {
            Rule = new TableRule("ORDERS", RuleKind.All, action) { CreateIfMissing = createMissing, Masks = masks ?? [] };
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = [Rule],
            };
            var source = Orders("SRC", sourcePrimaryKey, idDefault);
            Plan = new CopyTablePlan(Rule, source, Orders("DST"), ["ID", "NAME"], ["ID"])
            {
                CreateMissing = createMissing,
                CreateSequences = sequences ?? [],
                Companions = companions ?? CompanionDdl.Empty, // 사전 점검이 정한 딸린 객체(SQ2)
            };
            var preview = new List<PreviewStatement>();
            if (!createMissing)
            {
                // 사전 점검이 낼 미리 보기처럼: 있는 표의 대상 DELETE.
                var builder = new CopySqlBuilder(Job, new ResolvedPeriod(null, null),
                    new Dictionary<string, TableInfo> { ["ORDERS"] = Plan.Source }, new Dictionary<string, TableInfo> { ["ORDERS"] = Plan.Target });
                preview.AddRange(new[] { builder.TargetDelete("ORDERS") }.OfType<SqlWithBinds>()
                    .Select(d => new PreviewStatement(d.Sql, PreviewSide.Target, PolicyDecisionKind.Allow, null, null)));
            }

            Report = new PrecheckReport([], [Plan], preview, new Dictionary<string, long>());

            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile(environment) with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/" + targetUser };

            Execution = new FakeExecutionService();
            Execution.Responder = call =>
            {
                All.Add("EXEC " + call.Statement.Text);
                return FailExecution(call.Statement.Text) is { } error
                    ? new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = error }
                    : new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);
            };
            Bulk = new FakeBulk(this);
            RunRecord = new FakeRunRecord();
            var parts = Parts(new NoCatalog(), new FakePrecheckDialect(), new FakeDialect());
            Runner = new CopyRunner(Bulk, Execution, new FakeRunLog(RunRecord), sourceParts ?? parts, targetParts ?? parts);
        }

        public TableRule Rule { get; }

        public CopyJob Job { get; }

        public CopyTablePlan Plan { get; }

        public PrecheckReport Report { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public FakeExecutionService Execution { get; }

        public FakeBulk Bulk { get; }

        public FakeRunRecord RunRecord { get; }

        private CopyRunner Runner { get; }

        /// <summary>실행 엔진에 보낸 문장을 실패시키려면 오류를 돌려준다(없으면 성공).</summary>
        public Func<string, DbError?> FailExecution { get; set; } = _ => null;

        /// <summary>
        /// 보낸 모든 문장(순서대로): <c>EXEC</c>=실행 엔진, <c>BULK</c>=배열 DML, <c>CTL</c>=트랜잭션 제어,
        /// <c>QUERY</c>=대상 시퀀스 맞추기 조회(<c>SELECT MAX(</c>·NEXTVAL, 건수 COUNT(*) 는 넣지 않는다).
        /// </summary>
        public List<string> All { get; } = [];

        /// <summary>트랜잭션 제어를 뺀 문장.</summary>
        public List<string> Order => [.. All.Where(x => !x.StartsWith("CTL ", StringComparison.Ordinal))];

        public Task<CopyRunResult> RunAsync() => Runner.RunAsync(new CopySessions(Source, Target), Job, Report,
            new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false),
            new ResolvedPeriod(null, null), null, new CopyRunControl(), CancellationToken.None);

        private static TableInfo Orders(string owner, bool primaryKey = false, string? idDefault = null) =>
            new(new DbObjectRef(owner, "ORDERS", "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false, DefaultValue = idDefault },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Comment = "주문",
                Constraints = primaryKey ? [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])] : [],
            };
    }

    private sealed record BulkCall(ISession Session, string Text, int ColumnCount, int RowCount);

    private sealed class FakeBulk(Fixture fixture) : IBulkExecutionService
    {
        private long _inserted;

        /// <summary>배열 DML·트랜잭션 제어에 대한 응답. null 을 돌려주면 성공(행 수만큼 영향).</summary>
        public Func<BulkCall, BulkResult?>? OnArray { get; set; }

        public bool SourceReadFails { get; set; }

        /// <summary>대상 <c>SELECT MAX(</c> 조회의 답(적재한 키 최댓값). 기본은 넣은 ID 의 최댓값 2.</summary>
        public object? MaxKey { get; set; } = 2L;

        /// <summary>대상 NEXTVAL 조회의 답.</summary>
        public object? NextValue { get; set; } = 1L;

        /// <summary>대상 시퀀스 맞추기 조회(<c>SELECT MAX(</c>·NEXTVAL)와 바인드(보낸 순서).</summary>
        public List<(string Text, IReadOnlyDictionary<string, BindValue> Binds)> Queries { get; } = [];

        /// <summary>배열 DML·트랜잭션 제어 호출(세션 포함, 보낸 순서).</summary>
        public List<BulkCall> Calls { get; } = [];

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (ReferenceEquals(s, fixture.Source))
            {
                if (SourceReadFails)
                {
                    throw new BulkDeniedException(new PolicyDecision(PolicyDecisionKind.Deny, Message: "read denied"));
                }

                yield return [1L, "kim"];
                yield return [2L, "lee"];
                yield break;
            }

            var isMax = query.Text.StartsWith("SELECT MAX(", StringComparison.Ordinal);
            if (isMax || query.Text.Contains(".NEXTVAL", StringComparison.Ordinal))
            {
                fixture.All.Add("QUERY " + query.Text);
                Queries.Add((query.Text, binds));
                yield return [isMax ? MaxKey : NextValue];
                yield break;
            }

            yield return [_inserted]; // 대상 COUNT(*)
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct)
        {
            var isDml = columns.Count > 0;
            fixture.All.Add((isDml ? "BULK " : "CTL ") + stmt.Text);
            var call = new BulkCall(s, stmt.Text, columns.Count, rows.Count);
            Calls.Add(call);
            var result = OnArray?.Invoke(call) ?? new BulkResult(ExecutionStatus.Succeeded, isDml ? rows.Count : 0);
            if (isDml && result.Status == ExecutionStatus.Succeeded && ReferenceEquals(s, fixture.Target))
            {
                _inserted += rows.Count;
            }

            return Task.FromResult(result);
        }
    }

    private sealed class FakeRunRecord : ICopyRunRecord
    {
        public List<string> Statements { get; } = [];

        public CopyOutcome? Completed { get; private set; }

        public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

        public Task RecordStatementAsync(string sql, CancellationToken ct)
        {
            Statements.Add(sql);
            return Task.CompletedTask;
        }

        public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

        public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct) => Task.CompletedTask;

        public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct)
        {
            Completed = outcome;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRunLog(FakeRunRecord record) : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(record);

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) => throw new NotSupportedException("이 시험은 카탈로그 조회를 쓰지 않는다: " + id);
    }

    private sealed class FakeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => null;
    }

    private sealed class FakeCodec : IBulkValueCodec
    {
        /// <summary>형식 해석(<see cref="KindOf"/>)을 부른 서버 형식(어느 벌의 코덱이 불렸는지 가린다).</summary>
        public List<string> KindOfCalls { get; } = [];

        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType)
        {
            KindOfCalls.Add(serverType);
            return serverType.StartsWith("VARCHAR", StringComparison.Ordinal) ? BulkKind.Text : BulkKind.Other;
        }

        public BindDataType BindTypeOf(string serverType) => BindDataType.Varchar2;

        public string? ToKeyText(object? value, string serverType) => value?.ToString();

        public object? FromKeyText(string? text, string serverType) => text;

        public long? ToInt64(object? value) => value is long l ? l : null;

        public string? ToText(object? value) => value?.ToString();

        public string? NumberText(object? value) => value is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        public bool IsZero(object? value) => value is 0L;

        public long ToInt64Exact(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeRecreateStep : ICopyRecreateStep
    {
        public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) => Task.FromResult(PreservedDdl.Empty); // 원본 트리거·권한(SQ2)도 이것으로 읽는다: 없음

        /// <summary>작업 안 FK 를 읽으러 온 원본 세션(불리지 않았으면 null).</summary>
        public ISession? ForeignKeySession { get; private set; }

        public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
            IReadOnlySet<string> parents, string targetOwner, CancellationToken ct)
        {
            ForeignKeySession = source;
            return Task.FromResult<IReadOnlyList<(string Parent, string Ddl, string? Name)>>([]);
        }

        public Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// 문장 글만 만드는 방언. <c>DropCreatedTable</c> 은 인터페이스 기본 구현(공통 글)을 그대로 쓴다.
    /// <paramref name="tag"/> 를 주면 트랜잭션 제어 글(READ ONLY·세이브포인트) 앞에 붙인다(어느 벌의 방언이 보냈는지 가린다).
    /// </summary>
    private sealed class FakeDialect(string? tag = null, string? serverName = null) : ICopyDialect
    {
        private readonly string _prefix = tag is null ? string.Empty : tag + " ";

        /// <summary><paramref name="serverName"/> 을 주면 그 이름만 서버가 지은 이름으로 본다(주지 않으면 공통 기본 판정).</summary>
        public bool IsServerName(string name, string owner) => serverName is null ? CreateTableDdl.IsServerName(name) : name == serverName;

        private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl);

        public string BindPlaceholder(string bindName, string serverType) => ":" + bindName;

        public SqlStatement DisableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" DISABLE");

        public SqlStatement DisableConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DISABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement DropTable(string owner, string table) => Ddl($"DROP TABLE \"{owner}\".\"{table}\"");

        public SqlStatement NextValues(string owner, string sequence) =>
            CopyCommonStatements.Query($"SELECT \"{owner}\".\"{sequence}\".NEXTVAL FROM DUAL CONNECT BY LEVEL <= :{CopyCommonStatements.NextValuesBind}");

        public SqlStatement SetTransactionReadOnly() => new(_prefix + "SET TRANSACTION READ ONLY", default, StatementKind.TransactionControl);

        public SqlStatement Savepoint(string name) => new(_prefix + "SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement RollbackToSavepoint(string name) => new(_prefix + "ROLLBACK TO SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement EnableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" ENABLE");

        public SqlStatement DropConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DROP CONSTRAINT \"{fk.Name}\"");

        public SqlStatement EnableConstraint(DisabledObject fk, bool validate) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" ENABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement RecycleBinOn() => throw new NotSupportedException();

        public string RestartIdentity(string owner, string table, string column) => throw new NotSupportedException();

        public string AddForeignKeyWithoutValidation(string addForeignKeyDdl) => addForeignKeyDdl;

        public bool IsParentKeysNotFound(DbError? dbError) => false;

        public bool IsReferencedByForeignKeys(DbError? dbError) => false;
    }
}
