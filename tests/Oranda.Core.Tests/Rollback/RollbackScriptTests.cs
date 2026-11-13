using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Script;

namespace Oranda.Core.Tests.Rollback;

/// <summary>원복 스크립트 글(되돌리기 지점 설계 6.7, 시안 화면 2 #318)과 반대 문장(RD-8).</summary>
public class RollbackScriptTests
{
    private static readonly DbObjectRef Products = new("SAMPLE", "PRODUCTS", "TABLE");

    private static DateTimeOffset At(int h, int m, int s) => new(2026, 11, 12, h, m, s, TimeSpan.Zero);

    private const string Delete2001 =
        "DELETE FROM SAMPLE.PRODUCTS WHERE PRODUCT_ID = 2001 AND PRODUCT_NAME = '유자차 스틱' AND CATEGORY_ID = 3 AND UNIT_PRICE = 6200 AND DISCONTINUED = 'N' AND MEMO IS NULL";

    private const string Insert1040 =
        "INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, CATEGORY_ID, UNIT_PRICE, DISCONTINUED, MEMO) VALUES (1040, '보리차 티백', 3, 4730, 'Y', NULL)";

    private const string Update1040 = "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730";

    private static (RollbackGroup Group, RollbackScriptDocument Document) Sample318(RollbackScriptState state = RollbackScriptState.Confirmed)
    {
        RollbackStatementInfo Info(int seq, DateTimeOffset at, DmlVerb verb, string summary, long rows) =>
            new(seq, at, verb, Products, summary, rows, null, RollbackScriptState.Open, null, 1, false);

        var group = new RollbackGroup
        {
            Id = 318, ProfileId = Guid.Empty, ProfileName = "prod-example", DbKey = "K", State = state,
            EndedAt = At(14, 5, 10), EndKind = TransactionEndKind.Commit, CommitPoint = new ChangePoint(48214022, "SCN", At(14, 5, 10), "q"),
            Statements =
            [
                Info(1, At(14, 3, 21), DmlVerb.Update, "UPDATE … SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = 3", 12),
                Info(2, At(14, 4, 2), DmlVerb.Delete, "DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y'", 3),
                Info(3, At(14, 4, 40), DmlVerb.InsertValues, "INSERT INTO SAMPLE.PRODUCTS … VALUES (2001, …)", 1),
            ],
        };
        RollbackScriptStatement Doc(int seq, DateTimeOffset at, string step, long rows) =>
            new(seq, at, "원문", [], [new RollbackStep(step)], [], false, null, rows);
        var document = new RollbackScriptDocument(1, 318,
        [
            Doc(1, At(14, 3, 21), Update1040, 12), Doc(2, At(14, 4, 2), Insert1040, 3), Doc(3, At(14, 4, 40), Delete2001, 1),
        ]);
        return (group, document);
    }

    [Fact]
    public void Writes_proposal_screen_318_in_reverse_order()
    {
        var (group, document) = Sample318();
        var expected = """
            -- Oranda 원복 스크립트 #318 · 접속 prod-example · 상태: 확정
            -- 커밋 2026-11-12 14:05:10 (SCN 48214022) · 실행한 문장 3개 · 원복 문장 3개
            -- 실행한 순서의 역순입니다. 그 뒤 다른 사람이 바꾼 행은 0행으로 끝납니다.
            -- [3/3] 14:04:40 INSERT INTO SAMPLE.PRODUCTS … VALUES (2001, …) · 1행
            DELETE FROM SAMPLE.PRODUCTS
             WHERE PRODUCT_ID = 2001 AND PRODUCT_NAME = '유자차 스틱' AND CATEGORY_ID = 3
               AND UNIT_PRICE = 6200 AND DISCONTINUED = 'N' AND MEMO IS NULL;
            -- [2/3] 14:04:02 DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y' · 3행
            INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, CATEGORY_ID, UNIT_PRICE, DISCONTINUED, MEMO)
             VALUES (1040, '보리차 티백', 3, 4730, 'Y', NULL);
            -- [1/3] 14:03:21 UPDATE … SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = 3 · 12행
            UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300
             WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730;

            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, RollbackScriptWriter.Write(group, document, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Marks_invalidated_incomplete_and_unverified()
    {
        var (group, document) = Sample318(RollbackScriptState.Unverified);
        group = group with
        {
            Statements = [group.Statements[0], group.Statements[1] with { Invalidated = true, Reason = "ROLLBACK TO SP1 로 되돌림" }, group.Statements[2]],
        };
        document = document with
        {
            Statements = [document.Statements[0] with { IncompleteReason = "행 수 다름(잠금 12, 실행 13)" }, document.Statements[1] with { Invalidated = true }, document.Statements[2]],
        };

        var text = RollbackScriptWriter.Write(group, document, TimeZoneInfo.Utc);

        Assert.Contains("상태: 확인 필요", text, StringComparison.Ordinal);
        Assert.Contains("-- 경고: " + RollbackScriptWriter.UnverifiedWarning, text, StringComparison.Ordinal);
        Assert.Contains("-- 무효: ROLLBACK TO SP1 로 되돌림\n-- [1/3]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VALUES (1040", text, StringComparison.Ordinal);
        Assert.Contains("-- 불완전: 행 수 다름(잠금 12, 실행 13)\nUPDATE", text, StringComparison.Ordinal);
        Assert.Contains("원복 문장 2개", text, StringComparison.Ordinal);
        var injected = document with { Statements = [document.Statements[0] with { IncompleteReason = "x\nDROP TABLE T" }, .. document.Statements.Skip(1)] };
        Assert.DoesNotContain("\nDROP TABLE", RollbackScriptWriter.Write(group, injected, TimeZoneInfo.Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void Statement_without_script_is_labelled_not_created()
    {
        var (group, document) = Sample318();
        group = group with
        {
            Statements = [group.Statements[0] with { State = RollbackScriptState.NotCreated, Reason = "MERGE", StepCount = 0 }, .. group.Statements.Skip(1)],
        };
        document = document with { Statements = [document.Statements[0] with { Steps = [], IncompleteReason = "MERGE" }, .. document.Statements.Skip(1)] };

        var text = RollbackScriptWriter.Write(group, document, TimeZoneInfo.Utc);

        Assert.Contains("-- 원복 문장 없음: MERGE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-- 불완전: MERGE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reverse_swaps_before_and_after_values()
    {
        Assert.Equal("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4730 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4300",
                     RollbackReverse.Reverse(new RollbackStep(Update1040))!.Sql);
        Assert.Equal("UPDATE T SET A = NULL, B = 'x, y' WHERE ROWID = CHARTOROWID('R1') AND A = TO_DATE(' 2026-01-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS') AND B IS NULL",
                     RollbackReverse.Reverse(new RollbackStep(
                         "UPDATE T SET A = TO_DATE(' 2026-01-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS'), B = NULL WHERE ROWID = CHARTOROWID('R1') AND A IS NULL AND B = 'x, y'"))!.Sql);
    }

    [Fact]
    public void Reverse_turns_insert_into_delete_and_delete_into_insert()
    {
        Assert.Equal(
            "DELETE FROM SAMPLE.PRODUCTS WHERE PRODUCT_ID = 1040 AND PRODUCT_NAME = '보리차 티백' AND CATEGORY_ID = 3 AND UNIT_PRICE = 4730 AND DISCONTINUED = 'Y' AND MEMO IS NULL AND ROWNUM = 1",
            RollbackReverse.Reverse(new RollbackStep(Insert1040))!.Sql);
        var back = RollbackReverse.Reverse(new RollbackStep(Delete2001))!;
        Assert.Equal("INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, CATEGORY_ID, UNIT_PRICE, DISCONTINUED, MEMO) VALUES (2001, '유자차 스틱', 3, 6200, 'N', NULL)",
                     back.Sql);
        Assert.Null(RollbackReverse.Reverse(new RollbackStep("DELETE FROM T WHERE ROWID = CHARTOROWID('R')")));
        Assert.Null(RollbackReverse.Reverse(new RollbackStep("MERGE INTO T USING …")));
        Assert.Null(RollbackReverse.Reverse(new RollbackStep("UPDATE T SET A = 1 WHERE A = 2")));
        Assert.Null(RollbackReverse.Reverse(new RollbackStep("DELETE FROM T WHERE ID = 1") { Note = RollbackReverse.NoReverseNote }));

        var all = RollbackReverse.ReverseAll([new RollbackStep(Update1040), new RollbackStep("SELECT 1 FROM DUAL")], out var skipped);
        Assert.Equal(1, skipped);
        Assert.StartsWith("UPDATE", Assert.Single(all).Sql, StringComparison.Ordinal);
    }
}
