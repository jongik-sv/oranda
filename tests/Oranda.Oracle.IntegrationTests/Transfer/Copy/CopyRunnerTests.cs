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

// 소유: 데이터 복사 Task 9. 복사 실행기(26ai 컨테이너 하나 안의 시험 사용자 셋, CopyDbScope 사용).
// 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI·다른 PC 의 컨테이너에서 한다.

/// <summary>
/// 복사 실행기: 기간·부모 키·제외 규칙, 다시 실행, LOB, 트리거 되살리기(실패·중지), 실행 기록 순서, 실패 행 키, 시퀀스 보정,
/// 읽기 전용 대상 거부, 쓰기 전용 계정, 실패 행 다시 넣기.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyRunnerTests
{
    private static readonly ResolvedPeriod March = new(new DateTime(2026, 3, 1), new DateTime(2026, 4, 1));
    private static readonly ResolvedPeriod April = new(new DateTime(2026, 4, 1), new DateTime(2026, 5, 1));
    private static readonly ResolvedPeriod NoPeriod = new(null, null);
    private static readonly string[] RerunTables = ["RR_PERIOD", "RR_ALL", "RR_MERGE"];

    private readonly OracleFreeFixture _db;

    public CopyRunnerTests(OracleFreeFixture db) => _db = db;

    // ---- 시험 ----

    [Fact]
    public async Task Copies_all_period_parent_and_excluded_rules()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "PO_HEAD", "ID NUMBER PRIMARY KEY, HEAD_DT DATE");
        await BothAsync(scope, "PO_LINE", "ID NUMBER PRIMARY KEY, HEAD_ID NUMBER REFERENCES PO_HEAD (ID)");
        await BothAsync(scope, "PO_ITEM", "ID NUMBER PRIMARY KEY, LINE_ID NUMBER REFERENCES PO_LINE (ID)");
        await BothAsync(scope, "ORD_DT", "ID NUMBER PRIMARY KEY, ORDER_DT DATE");
        await BothAsync(scope, "ORD_TS", "ID NUMBER PRIMARY KEY, CREATED_AT TIMESTAMP(6)");
        await BothAsync(scope, "ORD_STR", "ID NUMBER PRIMARY KEY, ORDER_YMD VARCHAR2(8)");
        await BothAsync(scope, "ORD_OR", "ID NUMBER PRIMARY KEY, A_DT DATE, B_DT DATE");
        await BothAsync(scope, "KEEP_AS_IS", "ID NUMBER PRIMARY KEY, NOTE VARCHAR2(20)");

        // 경계: 시작(3-01 00:00:00)은 들어가고 끝(4-01 00:00:00)은 빠진다. 3-31 23:59:59 는 들어간다.
        await scope.ExecSourceAsync(Block(
            $"INSERT INTO ORD_DT VALUES (1, {Dt("2026-02-28 23:59:59")})",
            $"INSERT INTO ORD_DT VALUES (2, {Dt("2026-03-01 00:00:00")})",
            $"INSERT INTO ORD_DT VALUES (3, {Dt("2026-03-15 12:00:00")})",
            $"INSERT INTO ORD_DT VALUES (4, {Dt("2026-03-31 23:59:59")})",
            $"INSERT INTO ORD_DT VALUES (5, {Dt("2026-04-01 00:00:00")})",
            $"INSERT INTO ORD_TS VALUES (1, {Ts("2026-02-28 23:59:59.999999")})",
            $"INSERT INTO ORD_TS VALUES (2, {Ts("2026-03-01 00:00:00")})",
            $"INSERT INTO ORD_TS VALUES (3, {Ts("2026-03-15 12:00:00.5")})",
            $"INSERT INTO ORD_TS VALUES (4, {Ts("2026-03-31 23:59:59.999999")})",
            $"INSERT INTO ORD_TS VALUES (5, {Ts("2026-04-01 00:00:00")})",
            "INSERT INTO ORD_STR VALUES (1, '20260228')",
            "INSERT INTO ORD_STR VALUES (2, '20260301')",
            "INSERT INTO ORD_STR VALUES (3, '20260315')",
            "INSERT INTO ORD_STR VALUES (4, '20260331')",
            "INSERT INTO ORD_STR VALUES (5, '20260401')",

            // 두 열 OR: A 만 범위 안(1), B 만 범위 안(2), 둘 다 밖(3), 둘 다 안(4), 둘 다 NULL(5).
            $"INSERT INTO ORD_OR VALUES (1, {Dt("2026-03-10 00:00:00")}, {Dt("2026-01-01 00:00:00")})",
            $"INSERT INTO ORD_OR VALUES (2, {Dt("2026-01-01 00:00:00")}, {Dt("2026-03-20 00:00:00")})",
            $"INSERT INTO ORD_OR VALUES (3, {Dt("2026-01-01 00:00:00")}, {Dt("2026-01-02 00:00:00")})",
            $"INSERT INTO ORD_OR VALUES (4, {Dt("2026-03-05 00:00:00")}, {Dt("2026-03-06 00:00:00")})",
            "INSERT INTO ORD_OR VALUES (5, NULL, NULL)",

            // 부모 키 2단계: HEAD(기간) → LINE → ITEM. 머리 3 은 2월이라 그 자식(14, 104)은 빠진다.
            $"INSERT INTO PO_HEAD VALUES (1, {Dt("2026-03-05 00:00:00")})",
            $"INSERT INTO PO_HEAD VALUES (2, {Dt("2026-03-20 00:00:00")})",
            $"INSERT INTO PO_HEAD VALUES (3, {Dt("2026-02-10 00:00:00")})",
            "INSERT INTO PO_LINE VALUES (11, 1)",
            "INSERT INTO PO_LINE VALUES (12, 1)",
            "INSERT INTO PO_LINE VALUES (13, 2)",
            "INSERT INTO PO_LINE VALUES (14, 3)",
            "INSERT INTO PO_ITEM VALUES (101, 11)",
            "INSERT INTO PO_ITEM VALUES (102, 12)",
            "INSERT INTO PO_ITEM VALUES (103, 13)",
            "INSERT INTO PO_ITEM VALUES (104, 14)",
            "INSERT INTO KEEP_AS_IS VALUES (1, 'source')",
            "INSERT INTO KEEP_AS_IS VALUES (2, 'source')"));

        // 대상에 이미 있는 행: 범위 안(옛 값)은 지워지고, 범위 밖은 그대로다. 자식 → 부모 순서로 지워야 FK 가 막지 않는다.
        await scope.ExecTargetAsync(Block(
            $"INSERT INTO ORD_DT VALUES (98, {Dt("2026-01-01 00:00:00")})",
            $"INSERT INTO ORD_DT VALUES (99, {Dt("2026-03-10 00:00:00")})",
            $"INSERT INTO PO_HEAD VALUES (1, {Dt("2026-03-07 00:00:00")})",
            $"INSERT INTO PO_HEAD VALUES (3, {Dt("2026-02-10 00:00:00")})",
            "INSERT INTO PO_LINE VALUES (11, 1)",
            "INSERT INTO PO_LINE VALUES (14, 3)",
            "INSERT INTO PO_ITEM VALUES (101, 11)",
            "INSERT INTO KEEP_AS_IS VALUES (1, 'target-only')"));

        var job = scope.Job(
            new TableRule("ORD_DT", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_DT")] },
            new TableRule("ORD_TS", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("CREATED_AT")] },
            new TableRule("ORD_STR", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_YMD", "YYYYMMDD")] },
            new TableRule("ORD_OR", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("A_DT"), new PeriodColumn("B_DT")] },
            new TableRule("PO_HEAD", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("HEAD_DT")] },
            new TableRule("PO_LINE", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("PO_HEAD", ["HEAD_ID"], ["ID"]) },
            new TableRule("PO_ITEM", RuleKind.ParentKey, TargetAction.DeleteRangeInsert) { Parent = new ParentLink("PO_LINE", ["LINE_ID"], ["ID"]) },
            new TableRule("KEEP_AS_IS", RuleKind.Exclude, TargetAction.AppendOnly));
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, March);
        var log = new RecordingRunLog();

        var result = await RunAsync(scope, sessions, job, report, March, log);

        AssertSucceeded(result);

        // 사람이 센 기대 건수. 건수 비교(SourceRows == TargetRows)만으로는 같은 조건 문장이 틀려도 통과하므로 직접 확인한다.
        var expected = new Dictionary<string, long>
        {
            ["ORD_DT"] = 3, ["ORD_TS"] = 3, ["ORD_STR"] = 3, ["ORD_OR"] = 3, ["PO_HEAD"] = 2, ["PO_LINE"] = 3, ["PO_ITEM"] = 3,
        };
        Assert.Equal(expected.Count, result.Tables.Count);
        foreach (var (table, rows) in expected)
        {
            var t = Assert.Single(result.Tables, x => x.Table == table);
            Assert.Equal(CopyOutcome.Succeeded, t.Outcome);
            Assert.Equal(rows, t.SourceRows);
            Assert.Equal(rows, t.TargetRows);
            Assert.Equal(rows, report.EstimatedRows[table]);
        }

        Assert.Equal("2,3,4,98", await IdsAsync(scope.DstUser, "ORD_DT"));
        Assert.Equal("2,3,4", await IdsAsync(scope.DstUser, "ORD_TS"));
        Assert.Equal("2,3,4", await IdsAsync(scope.DstUser, "ORD_STR"));
        Assert.Equal("1,2,4", await IdsAsync(scope.DstUser, "ORD_OR"));
        Assert.Equal("1,2,3", await IdsAsync(scope.DstUser, "PO_HEAD"));
        Assert.Equal("11,12,13,14", await IdsAsync(scope.DstUser, "PO_LINE"));
        Assert.Equal("101,102,103", await IdsAsync(scope.DstUser, "PO_ITEM"));

        // 옛 값(3-07)이 새 값(3-05)으로 바뀌었다.
        Assert.Equal("2026-03-05", await AdminScalarAsync($"SELECT TO_CHAR(HEAD_DT, 'YYYY-MM-DD') FROM {scope.DstUser}.PO_HEAD WHERE ID = 1"));

        // 제외 규칙 표는 건드리지 않는다.
        Assert.Equal("1:target-only", await ListAsync($"SELECT ID || ':' || NOTE FROM {scope.DstUser}.KEEP_AS_IS ORDER BY ID"));

        // 부모 → 자식 순서.
        var order = report.Order.Select(p => p.Rule.Table).ToList();
        Assert.True(order.IndexOf("PO_HEAD") < order.IndexOf("PO_LINE") && order.IndexOf("PO_LINE") < order.IndexOf("PO_ITEM"));

        // 실행기가 보낸 문장 가운데 미리 받은 확인에 없는 것이 있었다면 확인 창이 떴을 것이다(시험은 거절로 답한다).
        Assert.Empty(scope.Ui.Confirms);
        Assert.Equal("Complete:Succeeded", log.Events[^1]);
    }

    [Fact]
    public async Task Rerun_produces_same_result()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "RR_PERIOD", "ID NUMBER PRIMARY KEY, EVT_DT DATE, VAL VARCHAR2(20)");
        await BothAsync(scope, "RR_ALL", "ID NUMBER, VAL VARCHAR2(20)");
        await BothAsync(scope, "RR_MERGE", "ID NUMBER PRIMARY KEY, VAL VARCHAR2(20)");

        // 2-21 ~ 3-22 의 30일. 3월은 3-01 ~ 3-22 의 22행.
        await scope.ExecSourceAsync(Block(
            "INSERT INTO RR_PERIOD SELECT LEVEL, DATE '2026-02-20' + LEVEL, 'v' || LEVEL FROM DUAL CONNECT BY LEVEL <= 30",
            "INSERT INTO RR_ALL SELECT LEVEL, 'v' || LEVEL FROM DUAL CONNECT BY LEVEL <= 15",
            "INSERT INTO RR_MERGE SELECT LEVEL, 'v' || LEVEL FROM DUAL CONNECT BY LEVEL <= 15"));
        await scope.ExecTargetAsync(Block(
            $"INSERT INTO RR_PERIOD VALUES (500, {Dt("2026-03-10 00:00:00")}, 'stale')",
            $"INSERT INTO RR_PERIOD VALUES (501, {Dt("2026-01-05 00:00:00")}, 'outside')",
            "INSERT INTO RR_ALL VALUES (900, 'stale')",
            "INSERT INTO RR_MERGE VALUES (1, 'old')",
            "INSERT INTO RR_MERGE VALUES (901, 'keep')"));
        var job = scope.Job(
            new TableRule("RR_PERIOD", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("EVT_DT")] },
            new TableRule("RR_ALL", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("RR_MERGE", RuleKind.All, TargetAction.MergeByPk));
        var sessions = await scope.OpenSessionsAsync();

        var first = await RunOnceAsync(scope, sessions, job, March);
        var snapshot1 = await SnapshotAsync(scope);
        var second = await RunOnceAsync(scope, sessions, job, March);
        var snapshot2 = await SnapshotAsync(scope);

        Assert.Equal(snapshot1, snapshot2);
        Assert.Equal(
            first.Tables.Select(t => (t.Table, t.SourceRows, t.TargetRows, t.Outcome)),
            second.Tables.Select(t => (t.Table, t.SourceRows, t.TargetRows, t.Outcome)));
        Assert.Equal(22, Assert.Single(second.Tables, t => t.Table == "RR_PERIOD").SourceRows);

        // 겹친 행이 없다: PK 없는 표를 두 번 비우고 넣어도 15행이고, 범위 밖 옛 행(501)과 MERGE 로 남는 행(901)은 그대로다.
        Assert.Equal(15, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RR_ALL"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RR_PERIOD WHERE ID = 500"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RR_PERIOD WHERE ID = 501"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RR_MERGE WHERE ID = 901"));
        Assert.Equal("v1", await AdminScalarAsync($"SELECT VAL FROM {scope.DstUser}.RR_MERGE WHERE ID = 1"));
    }

    [Fact]
    public async Task Rerun_after_period_value_change_merges_without_dup()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "ORD", "ID NUMBER PRIMARY KEY, ORDER_DT DATE, NOTE VARCHAR2(20)");
        await scope.ExecSourceAsync(Block(
            $"INSERT INTO ORD VALUES (1, {Dt("2026-03-05 00:00:00")}, 'a')",
            $"INSERT INTO ORD VALUES (2, {Dt("2026-03-15 00:00:00")}, 'b')",
            $"INSERT INTO ORD VALUES (3, {Dt("2026-03-25 00:00:00")}, 'c')"));
        var job = scope.Job(new TableRule("ORD", RuleKind.Period, TargetAction.DeleteRangeInsert) { PeriodColumns = [new PeriodColumn("ORDER_DT")] });
        var sessions = await scope.OpenSessionsAsync();

        var first = await RunOnceAsync(scope, sessions, job, March);
        Assert.Empty(first.FailedRows);
        Assert.Equal("1,2,3", await IdsAsync(scope.DstUser, "ORD"));

        // 같은 3월 범위 안에서 날짜만 바뀐 경우: 새 값으로 갱신된다.
        await scope.ExecSourceAsync(Block($"UPDATE ORD SET ORDER_DT = {Dt("2026-03-20 00:00:00")}, NOTE = 'b2' WHERE ID = 2"));
        var second = await RunOnceAsync(scope, sessions, job, March);
        Assert.Empty(second.FailedRows);
        Assert.Equal("1,2,3", await IdsAsync(scope.DstUser, "ORD"));
        Assert.Equal("2026-03-20:b2", await AdminScalarAsync($"SELECT TO_CHAR(ORDER_DT, 'YYYY-MM-DD') || ':' || NOTE FROM {scope.DstUser}.ORD WHERE ID = 2"));

        // 새 범위(4월)로 옮겨 가면 대상의 옛 행(3-20)은 범위 DELETE 에 걸리지 않는다. INSERT 였다면 ORA-00001 이 났을 자리다.
        await scope.ExecSourceAsync(Block($"UPDATE ORD SET ORDER_DT = {Dt("2026-04-10 00:00:00")}, NOTE = 'b3' WHERE ID = 2"));
        var third = await RunOnceAsync(scope, sessions, job, April);

        Assert.Empty(third.FailedRows);
        var table = Assert.Single(third.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(1, table.SourceRows);
        Assert.Equal(1, table.TargetRows);
        Assert.Equal("1,2,3", await IdsAsync(scope.DstUser, "ORD"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.ORD WHERE ID = 2"));
        Assert.Equal("2026-04-10:b3", await AdminScalarAsync($"SELECT TO_CHAR(ORDER_DT, 'YYYY-MM-DD') || ':' || NOTE FROM {scope.DstUser}.ORD WHERE ID = 2"));
    }

    // 기간 규칙 + 추가 조건(AND): 사전 점검을 통과하고, 원본·대상 건수가 추가 조건만큼 줄며, 대상 지우기는 조건 밖 행(기간 안이라도)을 남긴다.
    [Fact]
    public async Task Period_rule_with_extra_where_copies_and_deletes_only_matching_rows()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "EX_ORD", "ID NUMBER PRIMARY KEY, ORDER_DT DATE, STATUS VARCHAR2(10)");
        await scope.ExecSourceAsync(Block(
            $"INSERT INTO EX_ORD VALUES (1, {Dt("2026-03-05 00:00:00")}, 'DONE')",
            $"INSERT INTO EX_ORD VALUES (2, {Dt("2026-03-10 00:00:00")}, 'OPEN')",
            $"INSERT INTO EX_ORD VALUES (3, {Dt("2026-03-20 00:00:00")}, 'DONE')",
            $"INSERT INTO EX_ORD VALUES (4, {Dt("2026-02-10 00:00:00")}, 'DONE')"));
        await scope.ExecTargetAsync(Block(
            $"INSERT INTO EX_ORD VALUES (10, {Dt("2026-03-12 00:00:00")}, 'OPEN')",  // 기간 안·조건 밖: 남는다
            $"INSERT INTO EX_ORD VALUES (11, {Dt("2026-03-15 00:00:00")}, 'DONE')",  // 기간 안·조건 안: 지워진다
            $"INSERT INTO EX_ORD VALUES (12, {Dt("2026-02-01 00:00:00")}, 'DONE')")); // 기간 밖: 남는다
        var job = scope.Job(new TableRule("EX_ORD", RuleKind.Period, TargetAction.DeleteRangeInsert)
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT")],
            ExtraWhere = "STATUS = 'DONE'",
        });
        var sessions = await scope.OpenSessionsAsync();

        var report = await PrecheckAsync(scope, sessions, job, March);
        Assert.Contains(report.Preview, p => p.Side == PreviewSide.Target && p.Sql.StartsWith("DELETE", StringComparison.Ordinal)
                                             && p.Sql.EndsWith("AND (STATUS = 'DONE')", StringComparison.Ordinal));
        var result = await RunAsync(scope, sessions, job, report, March, new RecordingRunLog());
        AssertSucceeded(result);

        var table = Assert.Single(result.Tables);
        Assert.Equal(2, table.SourceRows); // 3월 3행(1·2·3) 가운데 DONE 2행(1, 3). OPEN(2)·2월(4)은 읽지 않는다
        Assert.Equal(2, table.TargetRows); // 대상 건수도 같은 조건: 남은 OPEN(10)·2월(12)은 세지 않는다
        Assert.Equal("1,3,10,12", await IdsAsync(scope.DstUser, "EX_ORD"));
    }

    [Fact]
    public async Task Work_log_shows_the_statements_sent_without_bind_or_masked_values()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "LOG_DOC", "ID NUMBER PRIMARY KEY, DOC_NO VARCHAR2(20), CUST_NAME VARCHAR2(40)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO LOG_DOC VALUES (1, 'DOCVAL-1', 'SECRET-NAME-1')",
            "INSERT INTO LOG_DOC VALUES (2, 'DOCVAL-2', 'SECRET-NAME-2')",
            "INSERT INTO LOG_DOC VALUES (3, 'DOCVAL-3', 'SECRET-NAME-3')"));
        var job = scope.Job(new TableRule("LOG_DOC", RuleKind.Custom, TargetAction.DeleteRangeInsert)
        {
            CustomWhere = "DOC_NO IN (:DOCS)",
            Masks = [new MaskRule("CUST_NAME", MaskKind.Fixed, "MASK-FIXED")],
        }) with
        {
            Variables = [new CopyVariable("DOCS", CopyVariableType.Text, ["DOCVAL-1", "DOCVAL-2"])],
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, March);
        var lines = new LogRecorder();

        var result = await RunAsync(scope, sessions, job, report, March, new RecordingRunLog(), progress: lines);

        AssertSucceeded(result);
        var text = string.Join('\n', lines.Entries.Select(CopyLogText.Format));
        var delete = Assert.Single(report.Preview, p => p.Side == PreviewSide.Target && p.Sql.StartsWith("DELETE", StringComparison.Ordinal)).Sql;
        Assert.Contains("대상: " + delete + "  [바인드 :DOCS = 2개]", text, StringComparison.Ordinal); // 미리 보기와 같은 실제 글
        Assert.Contains("원본: SET TRANSACTION READ ONLY", text, StringComparison.Ordinal);
        // PK 가 있어 MERGE 로 넣는다. 적재 문장은 배치마다 되풀이하지 않고 한 번만 남는다.
        Assert.Single(lines.Entries, e => e.Text.StartsWith("대상: MERGE INTO", StringComparison.Ordinal) || e.Text.StartsWith("대상: INSERT INTO", StringComparison.Ordinal));
        Assert.Contains("마스킹(앱에서 바꿔 보냄, 값은 남기지 않음): CUST_NAME(고정값)", text, StringComparison.Ordinal);
        Assert.Contains("배치 1 · 2행 보냄 · 누적 2행", text, StringComparison.Ordinal);
        Assert.Contains("대상: COMMIT", text, StringComparison.Ordinal);
        Assert.Contains("→ 원본 2건 · 대상 2건 · 같음", text, StringComparison.Ordinal);
        Assert.Contains("끝: 성공", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DOCVAL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-NAME", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MASK-FIXED", text, StringComparison.Ordinal);
        Assert.All(lines.Entries, e => Assert.False(string.IsNullOrEmpty(e.Phase)));
    }

    [Fact]
    public async Task Copies_lob_columns()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "LOBS", "ID NUMBER PRIMARY KEY, CL CLOB, NCL NCLOB, BL BLOB");

        // 4,000자(바이트)를 훨씬 넘는 값은 미리보기로 잘리지 않고 끝까지 같아야 한다(Review Focus 1).
        const int clobChars = 100_000;
        const int nclobChars = 50_000;
        const int blobBytes = 200_001;
        var blob = new byte[blobBytes];
        for (var i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)((i * 31) + 7);
        }

        await using (var conn = await OpenUserAsync(scope, scope.SrcUser))
        {
            await using var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = "INSERT INTO LOBS (ID, CL, NCL, BL) VALUES (:id, :cl, :ncl, :bl)";
            var id = new OracleParameter("id", OracleDbType.Decimal);
            var cl = new OracleParameter("cl", OracleDbType.Clob);
            var ncl = new OracleParameter("ncl", OracleDbType.NClob);
            var bl = new OracleParameter("bl", OracleDbType.Blob);
            cmd.Parameters.Add(id);
            cmd.Parameters.Add(cl);
            cmd.Parameters.Add(ncl);
            cmd.Parameters.Add(bl);

            id.Value = 1;
            cl.Value = new string('x', clobChars - 1) + "Z";
            ncl.Value = string.Concat(Enumerable.Repeat("한글복사", nclobChars / 4));
            bl.Value = blob;
            await cmd.ExecuteNonQueryAsync();

            id.Value = 2;
            cl.Value = DBNull.Value;
            ncl.Value = DBNull.Value;
            bl.Value = DBNull.Value;
            await cmd.ExecuteNonQueryAsync();

            id.Value = 3;
            cl.Value = "abc";
            ncl.Value = "한글";
            bl.Value = new byte[] { 1, 2, 3 };
            await cmd.ExecuteNonQueryAsync();

            await using var commit = conn.CreateCommand();
            commit.CommandText = "COMMIT";
            await commit.ExecuteNonQueryAsync();
        }

        // PK 기준 MERGE: LOB 열을 MERGE … USING (SELECT :C1 … FROM DUAL) 바인드 배열로 넣는 실행기 경로를 확인한다.
        var job = scope.Job(new TableRule("LOBS", RuleKind.All, TargetAction.MergeByPk));
        var sessions = await scope.OpenSessionsAsync();

        var result = await RunOnceAsync(scope, sessions, job, NoPeriod);

        var table = Assert.Single(result.Tables);
        Assert.Equal(3, table.SourceRows);
        Assert.Equal(3, table.TargetRows);

        // 바이트·글자 단위로 같다(NULL 은 NULL 끼리).
        Assert.Equal(3, await CountAsync($"""
            SELECT COUNT(*) FROM {scope.SrcUser}.LOBS s JOIN {scope.DstUser}.LOBS d ON d.ID = s.ID
             WHERE (s.CL IS NULL AND d.CL IS NULL OR DBMS_LOB.COMPARE(s.CL, d.CL) = 0)
               AND (s.NCL IS NULL AND d.NCL IS NULL OR DBMS_LOB.COMPARE(s.NCL, d.NCL) = 0)
               AND (s.BL IS NULL AND d.BL IS NULL OR DBMS_LOB.COMPARE(s.BL, d.BL) = 0)
            """));
        var lengths = await AdminScalarAsync($"SELECT DBMS_LOB.GETLENGTH(CL) || ',' || DBMS_LOB.GETLENGTH(NCL) || ',' || DBMS_LOB.GETLENGTH(BL) FROM {scope.DstUser}.LOBS WHERE ID = 1");
        Assert.Equal("100000,50000,200001", lengths);
    }

    [Fact]
    public async Task Triggers_are_reenabled_even_when_copy_fails()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("ORD_F", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20), QTY NUMBER");

        // 대상에만 있는 CHECK: 원본의 QTY = -1 행(3)이 실패 행이 된다.
        await scope.CreateTargetTableAsync("ORD_F", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20), QTY NUMBER, CONSTRAINT CK_ORD_F_QTY CHECK (QTY >= 0)");
        await CreatePrefixTriggerAsync(scope, "ORD_F_TRG", "ORD_F");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO ORD_F VALUES (1, 'a', 10)",
            "INSERT INTO ORD_F VALUES (2, 'b', 10)",
            "INSERT INTO ORD_F VALUES (3, 'c', -1)",
            "INSERT INTO ORD_F VALUES (4, 'd', 10)",
            "INSERT INTO ORD_F VALUES (5, 'e', 10)"));
        var job = scope.Job(new TableRule("ORD_F", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            Options = new CopyOptions { MaxFailedRows = 0 },
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        var log = new RecordingRunLog();

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log, Decisions(report, disableTriggerTables: ["ORD_F"]));

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);

        // 상한을 넘어 표 적재를 되돌렸으므로 실패 행은 결과·기록에 남기지 않는다(「실패 행만 다시 넣기」로 빈 표가 성공이 되지 않게).
        Assert.Empty(result.FailedRows);
        Assert.DoesNotContain("FailedRow", log.Events);
        Assert.Contains("다시 실행", table.Message, StringComparison.Ordinal);

        // 트리거는 실제로 꺼졌다가 켜졌다(기록에 DISABLE 과 ENABLE 문장이 모두 있다).
        Assert.Contains(log.Events, e => IsTriggerStatement(e, "DISABLE"));
        Assert.Contains(log.Events, e => IsTriggerStatement(e, "ENABLE"));
        Assert.Equal("ENABLED", await TriggerStatusAsync(scope, "ORD_F_TRG"));
        Assert.Equal("Complete:Failed", log.Events[^1]);
    }

    [Fact]
    public async Task Per_batch_table_stopped_midway_hides_committed_failed_rows()
    {
        // 「배치마다 커밋」: 첫 배치의 실패 행(ID 2)은 커밋과 함께 확정되지만, 둘째 배치에서 상한을 넘어 표를 멈춘다.
        // 나머지 배치를 못 넣은 표의 실패 행으로 「실패 행만 다시 넣기」를 하면 표가 성공으로 보이므로 결과에서 빠져야 한다.
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await scope.CreateSourceTableAsync("PB_F", "ID NUMBER PRIMARY KEY, QTY NUMBER");
        await scope.CreateTargetTableAsync("PB_F", "ID NUMBER PRIMARY KEY, QTY NUMBER, CONSTRAINT CK_PB_F_QTY CHECK (QTY >= 0)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO PB_F SELECT LEVEL, CASE WHEN LEVEL IN (2, 6, 7) THEN -1 ELSE 1 END FROM DUAL CONNECT BY LEVEL <= 15"));
        var job = scope.Job(new TableRule("PB_F", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            Options = new CopyOptions { BatchSize = 5, Commit = CommitMode.PerBatch, MaxFailedRows = 1 },
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, new RecordingRunLog(), Decisions(report));

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.Empty(result.FailedRows);
        Assert.Contains("다시 실행", table.Message, StringComparison.Ordinal);

        // 첫 배치(1~5, 실패 2)는 커밋됐고, 둘째 배치는 되돌렸다.
        Assert.Equal("1,3,4,5", await IdsAsync(scope.DstUser, "PB_F"));
    }

    [Fact]
    public async Task Triggers_are_reenabled_after_stop()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "STOP_T", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await CreatePrefixTriggerAsync(scope, "STOP_T_TRG", "STOP_T");
        await scope.ExecSourceAsync(Block("INSERT INTO STOP_T SELECT LEVEL, 'n' || LEVEL FROM DUAL CONNECT BY LEVEL <= 100"));

        // 대상에 옛 행 3개: 중지하면 비우기(DELETE)와 적재가 모두 되돌려져 그대로 남는다.
        await scope.ExecTargetAsync(Block(
            "INSERT INTO STOP_T VALUES (1001, 'old')",
            "INSERT INTO STOP_T VALUES (1002, 'old')",
            "INSERT INTO STOP_T VALUES (1003, 'old')"));
        var job = scope.Job(new TableRule("STOP_T", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            Options = new CopyOptions { BatchSize = 10 },
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        var log = new RecordingRunLog();
        using var cts = new CancellationTokenSource();

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log, Decisions(report, disableTriggerTables: ["STOP_T"]),
            new CancelAfterFirstBatch(cts), cts.Token);

        Assert.Equal(CopyOutcome.Stopped, result.Outcome);
        Assert.Contains(log.Events, e => IsTriggerStatement(e, "DISABLE"));
        Assert.Contains(log.Events, e => IsTriggerStatement(e, "ENABLE"));
        Assert.Equal("ENABLED", await TriggerStatusAsync(scope, "STOP_T_TRG"));
        Assert.Equal("Complete:Stopped", log.Events[^1]);
        Assert.Equal("1001,1002,1003", await IdsAsync(scope.DstUser, "STOP_T"));
    }

    [Fact]
    public async Task Stop_in_second_table_reports_committed_delete_without_load()
    {
        // 정리(DELETE)는 모든 표에 먼저 하므로(설계 8.2) 첫 표의 커밋이 둘째 표의 DELETE 도 확정한다.
        // 둘째 표 적재 중 중지하면 둘째 표의 적재만 되돌려지고, 그 표가 비었다는 사실이 결과·알림에 남아야 한다.
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "ST_A", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await BothAsync(scope, "ST_B", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO ST_A SELECT LEVEL, 'a' || LEVEL FROM DUAL CONNECT BY LEVEL <= 5",
            "INSERT INTO ST_B SELECT LEVEL, 'b' || LEVEL FROM DUAL CONNECT BY LEVEL <= 100"));
        await scope.ExecTargetAsync(Block("INSERT INTO ST_A VALUES (900, 'old')", "INSERT INTO ST_B VALUES (900, 'old')"));
        var job = scope.Job(
            new TableRule("ST_A", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("ST_B", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            Options = new CopyOptions { BatchSize = 10, Commit = CommitMode.PerTable },
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        var log = new RecordingRunLog();
        using var cts = new CancellationTokenSource();

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log, Decisions(report),
            new CancelAfterFirstBatch(cts, "ST_B"), cts.Token);

        Assert.Equal(CopyOutcome.Stopped, result.Outcome);
        Assert.Equal("1,2,3,4,5", await IdsAsync(scope.DstUser, "ST_A"));
        Assert.Equal(string.Empty, await IdsAsync(scope.DstUser, "ST_B"));
        Assert.Contains(result.Notices, n => n.Contains("ST_B", StringComparison.Ordinal) && n.Contains("다시 실행", StringComparison.Ordinal));
        var b = Assert.Single(result.Tables, t => t.Table == "ST_B");
        Assert.Contains("DELETE", b.Message, StringComparison.Ordinal);
        Assert.Equal("Complete:Stopped", log.Events[^1]);
    }

    [Fact]
    public async Task Disabled_objects_are_logged_before_disable()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "DO_PARENT", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.ExecSourceAsync(Block("INSERT INTO DO_PARENT VALUES (1, 'a')", "INSERT INTO DO_PARENT VALUES (2, 'b')"));

        // 대상에만 있는 자식 표(작업 밖)가 부모 1 을 참조한다: FK 를 끄지 않으면 부모를 비울 때 ORA-02292 가 난다.
        await scope.CreateTargetTableAsync("DO_CHILD", "ID NUMBER PRIMARY KEY, PARENT_ID NUMBER, CONSTRAINT FK_DO_CHILD FOREIGN KEY (PARENT_ID) REFERENCES DO_PARENT (ID)");
        await scope.ExecTargetAsync(Block("INSERT INTO DO_PARENT VALUES (1, 'old')", "INSERT INTO DO_CHILD VALUES (10, 1)"));
        await CreatePrefixTriggerAsync(scope, "DO_PARENT_TRG", "DO_PARENT");
        var job = scope.Job(new TableRule("DO_PARENT", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        var plan = Assert.Single(report.Order);
        Assert.True(plan.CanDisableTriggers);
        var fk = Assert.Single(plan.DisablableForeignKeys);
        Assert.Equal("DO_CHILD", fk.Table);
        string? statusAtRecord = null;
        var log = new RecordingRunLog { OnRecordDisabled = async () => statusAtRecord = await TriggerStatusAsync(scope, "DO_PARENT_TRG") };

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log,
            Decisions(report, disableTriggerTables: ["DO_PARENT"], disableForeignKeys: [ForeignKeyRef.Of(fk)]));

        // 기록하는 순간 트리거는 아직 켜져 있다: 기록이 ALTER ... DISABLE 보다 먼저다.
        Assert.Equal("ENABLED", statusAtRecord);

        AssertSucceeded(result);

        // 끈 목록 기록이 첫 사건이고, 모든 ALTER ... DISABLE 문장 기록보다 앞선다(Review Focus 3).
        Assert.Equal("Disabled", log.Events[0]);
        Assert.Contains(log.DisabledObjects, d => d.Kind == DisabledKind.Trigger && d.Name == "DO_PARENT_TRG");
        Assert.Contains(log.DisabledObjects, d => d.Kind == DisabledKind.ForeignKey && d.Name == "FK_DO_CHILD" && d.Table == "DO_CHILD");
        var triggerOff = log.Events.FindIndex(e => IsTriggerStatement(e, "DISABLE"));
        var fkOff = log.Events.FindIndex(e => e.StartsWith("Statement:ALTER TABLE", StringComparison.Ordinal) && e.Contains("DISABLE CONSTRAINT", StringComparison.Ordinal));
        Assert.True(triggerOff > 0, "트리거 끄기 문장이 기록에 없다.");
        Assert.True(fkOff > 0, "FK 끄기 문장이 기록에 없다.");
        var deleteAt = log.Events.FindIndex(e => e.StartsWith("Statement:DELETE FROM", StringComparison.Ordinal));
        Assert.True(deleteAt > triggerOff && deleteAt > fkOff, "DELETE 는 끄기 뒤에 와야 한다.");
        Assert.True(log.Events.FindIndex(e => IsTriggerStatement(e, "ENABLE")) > deleteAt, "다시 켜기는 DELETE 뒤에 와야 한다.");
        Assert.Equal("Complete:Succeeded", log.Events[^1]);

        // 복사하는 동안 트리거는 꺼져 있었다(값에 접두어가 붙지 않았다). 끝나면 트리거와 FK 모두 켜져 있다.
        Assert.Equal("1:a,2:b", await ListAsync($"SELECT ID || ':' || NAME FROM {scope.DstUser}.DO_PARENT ORDER BY ID"));
        Assert.Equal("ENABLED", await TriggerStatusAsync(scope, "DO_PARENT_TRG"));
        Assert.Equal("ENABLED:VALIDATED", await AdminScalarAsync(
            $"SELECT STATUS || ':' || VALIDATED FROM DBA_CONSTRAINTS WHERE OWNER = '{scope.DstUser}' AND CONSTRAINT_NAME = 'FK_DO_CHILD'"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.DO_CHILD"));
    }

    [Fact]
    public async Task Target_only_check_constraint_creates_failed_rows_with_key_only()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        const string columns = "ID NUMBER PRIMARY KEY, NAME VARCHAR2(30), SECRET VARCHAR2(30), QTY NUMBER";
        await scope.CreateSourceTableAsync("CK_T", columns);
        await scope.CreateTargetTableAsync("CK_T", columns + ", CONSTRAINT CK_CK_T_QTY CHECK (QTY >= 0)");

        // 키가 아닌 열에 눈에 띄는 표지 값을 둔다: 실행 기록 어디에도 나오면 안 된다.
        await scope.ExecSourceAsync(Block(
            "INSERT INTO CK_T VALUES (1, 'name-marker-1', 'secret-marker-1', 5)",
            "INSERT INTO CK_T VALUES (2, 'name-marker-2', 'secret-marker-2', -1)",
            "INSERT INTO CK_T VALUES (3, 'name-marker-3', 'secret-marker-3', 5)",
            "INSERT INTO CK_T VALUES (4, 'name-marker-4', 'secret-marker-4', 5)",
            "INSERT INTO CK_T VALUES (5, 'name-marker-5', 'secret-marker-5', -1)",
            "INSERT INTO CK_T VALUES (6, 'name-marker-6', 'secret-marker-6', 5)"));
        var job = scope.Job(new TableRule("CK_T", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        Assert.Contains(report.Items, i => i.Code == "TargetOnlyConstraint");
        var log = new RecordingRunLog();

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log);

        Assert.Equal(CopyOutcome.PartiallyFailed, result.Outcome);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(6, table.SourceRows);
        Assert.Equal(4, table.TargetRows);
        Assert.Equal("1,3,4,6", await IdsAsync(scope.DstUser, "CK_T"));

        Assert.Equal(["2", "5"], result.FailedRows.Select(r => r.Key["ID"]).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(result.FailedRows.Count, log.FailedRows.Count);
        foreach (var row in log.FailedRows)
        {
            // 키 사전에는 키 열(ID)만 있고, 다른 열의 이름·값은 어디에도 없다.
            Assert.Equal("CK_T", row.Table);
            Assert.Equal(["ID"], row.Key.Keys.ToArray());
            Assert.Contains("ORA-02290", row.Error, StringComparison.Ordinal);
        }

        var recorded = string.Join('\n', log.Events)
                       + string.Join('\n', log.FailedRows.Select(r => r.Table + "|" + string.Join(",", r.Key.Select(k => k.Key + "=" + k.Value)) + "|" + r.Error))
                       + string.Join('\n', log.Tables.Select(t => t.Table + "|" + t.Message));
        Assert.DoesNotContain("marker", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("NAME", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", recorded, StringComparison.Ordinal);
        Assert.Equal("Complete:PartiallyFailed", log.Events[^1]);
    }

    [Fact]
    public async Task Sequence_is_advanced_by_nextval()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "SQ_A", "ID NUMBER PRIMARY KEY, V VARCHAR2(10)");
        await BothAsync(scope, "SQ_B", "ID NUMBER PRIMARY KEY, V VARCHAR2(10)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO SQ_A SELECT LEVEL, 'a' FROM DUAL CONNECT BY LEVEL <= 500",
            "INSERT INTO SQ_B SELECT LEVEL, 'b' FROM DUAL CONNECT BY LEVEL <= 100"));

        // 대상 시퀀스는 1 에서 시작해 원본 최대값(500, 100)보다 한참 모자라다. 증가값 5 인 시퀀스도 둔다.
        await scope.ExecTargetAsync("CREATE SEQUENCE SQ_A_SEQ START WITH 1 INCREMENT BY 1 CACHE 20");
        await scope.ExecTargetAsync("CREATE SEQUENCE SQ_B_SEQ START WITH 1 INCREMENT BY 5 CACHE 20");
        var job = scope.Job(
            new TableRule("SQ_A", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("SQ_B", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            Sequences = [new SequenceRule("SQ_A_SEQ", "SQ_A", "ID"), new SequenceRule("SQ_B_SEQ", "SQ_B", "ID")],
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        var log = new RecordingRunLog();

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log);

        AssertSucceeded(result);
        Assert.Contains(result.Notices, n => n.Contains("SQ_A_SEQ", StringComparison.Ordinal) && n.Contains("NEXTVAL", StringComparison.Ordinal));
        Assert.Contains(result.Notices, n => n.Contains("SQ_B_SEQ", StringComparison.Ordinal) && n.Contains("NEXTVAL", StringComparison.Ordinal));

        // 다음 값이 최대값 + 1 이상이다(시퀀스는 내려가지 않으므로 소비한 뒤의 값을 읽는다).
        var nextA = decimal.Parse(await ScalarAsAsync(scope, scope.DstUser, "SELECT SQ_A_SEQ.NEXTVAL FROM DUAL") ?? "0", CultureInfo.InvariantCulture);
        var nextB = decimal.Parse(await ScalarAsAsync(scope, scope.DstUser, "SELECT SQ_B_SEQ.NEXTVAL FROM DUAL") ?? "0", CultureInfo.InvariantCulture);
        Assert.True(nextA >= 501m, "SQ_A_SEQ 다음 값 " + nextA.ToString(CultureInfo.InvariantCulture));
        Assert.True(nextB >= 101m, "SQ_B_SEQ 다음 값 " + nextB.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// SQ3(결정 84): 시퀀스 보정 연결의 시퀀스가 대상 계정에 없으면 적재 전에 만들고 보정한다. SQ_C_SEQ 는 원본 계정에 있어 그 속성(INCREMENT BY 2, CACHE 10)으로,
    /// SQ_D_SEQ 는 원본에도 없어 기본 속성(INCREMENT BY 1)으로 만든다. 두 시퀀스 모두 다음 값이 적재한 키 최댓값 뒤다. 표는 이미 있으니 적재 뒤에도 시퀀스는 남는다.
    /// </summary>
    [Fact]
    public async Task Missing_rule_sequence_is_created_before_the_load_and_ends_after_the_loaded_keys()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "SQ_C", "ID NUMBER PRIMARY KEY, V VARCHAR2(10)");
        await BothAsync(scope, "SQ_D", "ID NUMBER PRIMARY KEY, V VARCHAR2(10)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO SQ_C SELECT LEVEL, 'c' FROM DUAL CONNECT BY LEVEL <= 300",
            "INSERT INTO SQ_D SELECT LEVEL, 'd' FROM DUAL CONNECT BY LEVEL <= 40"));
        await scope.ExecSourceAsync("CREATE SEQUENCE SQ_C_SEQ START WITH 7 INCREMENT BY 2 CACHE 10");
        var job = scope.Job(
            new TableRule("SQ_C", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("SQ_D", RuleKind.All, TargetAction.DeleteAllInsert)) with
        {
            Sequences = [new SequenceRule("SQ_C_SEQ", "SQ_C", "ID"), new SequenceRule("SQ_D_SEQ", "SQ_D", "ID")],
        };
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);

        var created = report.Items.Where(i => i.Code == CopyCheckCodes.SequenceCreated).ToList();
        Assert.Equal(2, created.Count);
        Assert.Contains(created, i => i.Sequence == "SQ_C_SEQ" && i.Message.Contains("원본 시퀀스", StringComparison.Ordinal));
        Assert.Contains(created, i => i.Sequence == "SQ_D_SEQ" && i.Message.Contains("기본 속성", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Items, i => i.Code == "SequenceGap" && i.Message.Contains("볼 수 없어", StringComparison.Ordinal));
        Assert.Contains(report.Preview, p => p.Sql.StartsWith($"CREATE SEQUENCE \"{scope.DstUser}\".\"SQ_C_SEQ\"", StringComparison.Ordinal) && p.Sql.Contains("INCREMENT BY 2", StringComparison.Ordinal));

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, new RecordingRunLog());

        AssertSucceeded(result);
        Assert.Equal(1m, decimal.Parse(await ScalarAsAsync(scope, scope.DstUser, "SELECT COUNT(*) FROM USER_SEQUENCES WHERE SEQUENCE_NAME = 'SQ_C_SEQ' AND INCREMENT_BY = 2 AND CACHE_SIZE = 10") ?? "0", CultureInfo.InvariantCulture));
        Assert.Equal(1m, decimal.Parse(await ScalarAsAsync(scope, scope.DstUser, "SELECT COUNT(*) FROM USER_SEQUENCES WHERE SEQUENCE_NAME = 'SQ_D_SEQ' AND INCREMENT_BY = 1") ?? "0", CultureInfo.InvariantCulture));
        var nextC = decimal.Parse(await ScalarAsAsync(scope, scope.DstUser, "SELECT SQ_C_SEQ.NEXTVAL FROM DUAL") ?? "0", CultureInfo.InvariantCulture);
        var nextD = decimal.Parse(await ScalarAsAsync(scope, scope.DstUser, "SELECT SQ_D_SEQ.NEXTVAL FROM DUAL") ?? "0", CultureInfo.InvariantCulture);
        Assert.True(nextC >= 301m, "SQ_C_SEQ 다음 값 " + nextC.ToString(CultureInfo.InvariantCulture));
        Assert.True(nextD >= 41m, "SQ_D_SEQ 다음 값 " + nextD.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Readonly_target_profile_is_denied()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "RO_DEL", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await BothAsync(scope, "RO_APP", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO RO_DEL VALUES (1, 'a')",
            "INSERT INTO RO_DEL VALUES (2, 'b')",
            "INSERT INTO RO_APP VALUES (1, 'a')",
            "INSERT INTO RO_APP VALUES (2, 'b')"));
        await scope.ExecTargetAsync(Block("INSERT INTO RO_DEL VALUES (900, 'keep')"));

        // 하나는 비우고 넣기(DELETE 가 정책에 막힌다), 하나는 추가만(적재 시작의 SAVEPOINT 가 대량 경로에서 막힌다).
        var job = scope.Job(
            new TableRule("RO_DEL", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("RO_APP", RuleKind.All, TargetAction.AppendOnly));
        var sessions = new CopySessions(
            await scope.OpenSourceAsync(),
            await scope.OpenTargetAsync(asWriter: false, ConnectionEnvironment.Test, readOnly: true));

        // 사전 점검은 읽기 전용 대상에서도 오류 없이 지난다(미리 보기의 Deny 는 점검 항목이 아니다).
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        Assert.Contains(report.Preview, p => p.Side == PreviewSide.Target && p.Policy == PolicyDecisionKind.Deny);
        var log = new RecordingRunLog();

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal(2, result.Tables.Count);
        foreach (var table in result.Tables)
        {
            Assert.Equal(CopyOutcome.Failed, table.Outcome);
            Assert.NotNull(table.Message);
            Assert.Contains("정책 거부", table.Message, StringComparison.Ordinal);
        }

        // 아무것도 쓰지 않았다.
        Assert.Equal("900", await IdsAsync(scope.DstUser, "RO_DEL"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RO_APP"));
        Assert.DoesNotContain(log.Events, e => e.StartsWith("Statement:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writer_account_copies_without_disable_choices()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await BothAsync(scope, "WR_PK", "ID NUMBER PRIMARY KEY, NAME VARCHAR2(20)");
        await BothAsync(scope, "WR_NOPK", "ID NUMBER, NAME VARCHAR2(20)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO WR_PK SELECT LEVEL, 'n' || LEVEL FROM DUAL CONNECT BY LEVEL <= 3",
            "INSERT INTO WR_NOPK SELECT LEVEL, 'n' || LEVEL FROM DUAL CONNECT BY LEVEL <= 3"));
        await scope.ExecTargetAsync(Block("INSERT INTO WR_PK VALUES (900, 'stale')", "INSERT INTO WR_NOPK VALUES (900, 'stale')"));

        // 트리거가 있어도 쓰기 계정은 끌 수 없다: 선택지가 없고, 트리거는 복사한 행에도 실행된다.
        await CreatePrefixTriggerAsync(scope, "WR_PK_TRG", "WR_PK");
        await scope.GrantWriterAsync("WR_PK");
        await scope.GrantWriterAsync("WR_NOPK");
        var job = scope.Job(
            new TableRule("WR_PK", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("WR_NOPK", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync(asWriter: true);
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        Assert.All(report.Order, p =>
        {
            Assert.False(p.CanDisableTriggers);
            Assert.Empty(p.DisablableForeignKeys);
        });
        Assert.DoesNotContain(report.Items, i => i.Choices.Contains(CheckChoice.DisableTriggers) || i.Choices.Contains(CheckChoice.DisableForeignKeys));
        var log = new RecordingRunLog();

        var result = await RunAsync(scope, sessions, job, report, NoPeriod, log);

        AssertSucceeded(result);
        foreach (var table in result.Tables)
        {
            Assert.Equal(3, table.SourceRows);
            Assert.Equal(3, table.TargetRows);
        }

        Assert.Equal("1,2,3", await IdsAsync(scope.DstUser, "WR_PK"));
        Assert.Equal("1,2,3", await IdsAsync(scope.DstUser, "WR_NOPK"));
        Assert.Equal("T:n1,T:n2,T:n3", await ListAsync($"SELECT NAME FROM {scope.DstUser}.WR_PK ORDER BY ID"));
        Assert.DoesNotContain(log.Events, e => e.StartsWith("Statement:ALTER", StringComparison.Ordinal));
        Assert.Equal("ENABLED", await TriggerStatusAsync(scope, "WR_PK_TRG"));
    }

    [Fact]
    public async Task Retry_failed_rows_rereads_by_key()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());

        // NUMBER 키 표(소수 자리가 있는 큰 값 포함).
        const string numColumns = "ID NUMBER PRIMARY KEY, QTY NUMBER";
        await scope.CreateSourceTableAsync("RT_NUM", numColumns);
        await scope.CreateTargetTableAsync("RT_NUM", numColumns + ", CONSTRAINT CK_RT_NUM CHECK (QTY >= 0)");

        // NUMBER + DATE(시각 있음) + VARCHAR2 가 섞인 복합 키 표.
        const string mixColumns = "ID NUMBER NOT NULL, BIZ_DT DATE NOT NULL, CODE VARCHAR2(5) NOT NULL, QTY NUMBER, CONSTRAINT PK_RT_MIX PRIMARY KEY (ID, BIZ_DT, CODE)";
        await scope.CreateSourceTableAsync("RT_MIX", mixColumns);
        await scope.CreateTargetTableAsync("RT_MIX", mixColumns + ", CONSTRAINT CK_RT_MIX CHECK (QTY >= 0)");
        await scope.ExecSourceAsync(Block(
            "INSERT INTO RT_NUM VALUES (1, 10)",
            "INSERT INTO RT_NUM VALUES (2, -1)",
            "INSERT INTO RT_NUM VALUES (3, 10)",
            "INSERT INTO RT_NUM VALUES (4, 10)",
            "INSERT INTO RT_NUM VALUES (5, -1)",
            "INSERT INTO RT_NUM VALUES (123456789012345.25, -1)",
            $"INSERT INTO RT_MIX VALUES (1, {Dt("2026-03-05 14:30:15")}, 'A', 10)",
            $"INSERT INTO RT_MIX VALUES (1, {Dt("2026-03-05 14:30:16")}, 'A', -1)",
            $"INSERT INTO RT_MIX VALUES (2, {Dt("2026-03-05 14:30:15")}, 'B', -1)",
            $"INSERT INTO RT_MIX VALUES (2, {Dt("2026-03-06 00:00:00")}, 'B', 10)",
            $"INSERT INTO RT_MIX VALUES (3, {Dt("2026-03-07 08:00:00")}, 'C', 10)"));
        var job = scope.Job(
            new TableRule("RT_NUM", RuleKind.All, TargetAction.DeleteAllInsert),
            new TableRule("RT_MIX", RuleKind.All, TargetAction.DeleteAllInsert));
        var sessions = await scope.OpenSessionsAsync();
        var report = await PrecheckAsync(scope, sessions, job, NoPeriod);
        var log = new RecordingRunLog();

        var first = await RunAsync(scope, sessions, job, report, NoPeriod, log);

        // 실패 행이 있어야 다시 넣기 시험이 뜻을 가진다.
        Assert.Equal(CopyOutcome.PartiallyFailed, first.Outcome);
        Assert.Equal(5, first.FailedRows.Count);
        Assert.Equal(3, first.FailedRows.Count(r => r.Table == "RT_NUM"));
        Assert.Equal(2, first.FailedRows.Count(r => r.Table == "RT_MIX"));
        Assert.Contains(first.FailedRows, r => r.Table == "RT_NUM" && r.Key["ID"] == "123456789012345.25");
        var mix = Assert.Single(first.FailedRows, r => r.Table == "RT_MIX" && r.Key["ID"] == "1");
        Assert.Equal("2026-03-05T14:30:16", mix.Key["BIZ_DT"]);
        Assert.Equal("A", mix.Key["CODE"]);
        Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RT_NUM"));
        Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RT_MIX"));

        // 실패 원인을 지우고 키로 원본을 다시 읽어 넣는다.
        await scope.ExecTargetAsync("ALTER TABLE RT_NUM DROP CONSTRAINT CK_RT_NUM");
        await scope.ExecTargetAsync("ALTER TABLE RT_MIX DROP CONSTRAINT CK_RT_MIX");
        var retryLog = new RecordingRunLog();

        var retry = await NewRunner(scope, retryLog).RetryFailedRowsAsync(sessions, job, report, first.FailedRows, null, CancellationToken.None);

        AssertSucceeded(retry);
        Assert.Empty(retry.FailedRows);
        Assert.Empty(retryLog.FailedRows);
        Assert.Equal(6, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RT_NUM"));
        Assert.Equal(5, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.RT_MIX"));
        Assert.Equal("-1", await AdminScalarAsync($"SELECT QTY FROM {scope.DstUser}.RT_NUM WHERE ID = 123456789012345.25"));
        Assert.Equal("-1", await AdminScalarAsync($"SELECT QTY FROM {scope.DstUser}.RT_MIX WHERE ID = 1 AND BIZ_DT = {Dt("2026-03-05 14:30:16")} AND CODE = 'A'"));
        Assert.Equal("Complete:Succeeded", retryLog.Events[^1]);
    }

    // ---- 실행 도우미 ----

    private static OracleCopyRunner NewRunner(CopyDbScope scope, ICopyRunLog log)
        => new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), log);

    /// <summary>사전 점검을 돌리고 오류가 없음을 확인한다(실행기는 오류가 있으면 예외를 낸다).</summary>
    private static async Task<PrecheckReport> PrecheckAsync(CopyDbScope scope, CopySessions sessions, CopyJob job, ResolvedPeriod period)
    {
        var report = await scope.Precheck().RunAsync(sessions, job, period, null, CancellationToken.None);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        return report;
    }

    /// <summary>
    /// 화면이 「복사 실행」에서 하는 것과 같다: 미리 보기에서 확인이 필요한 문장(WHERE 없는 DELETE 등)을 미리 받은 확인으로 넘긴다.
    /// </summary>
    private static CopyDecisions Decisions(PrecheckReport report, IEnumerable<string>? disableTriggerTables = null, IEnumerable<ForeignKeyRef>? disableForeignKeys = null)
        => new(
            new HashSet<string>(disableTriggerTables ?? [], StringComparer.Ordinal),
            new HashSet<ForeignKeyRef>(disableForeignKeys ?? []),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);

    private static async Task<CopyRunResult> RunAsync(CopyDbScope scope, CopySessions sessions, CopyJob job, PrecheckReport report, ResolvedPeriod period,
                                                      RecordingRunLog log, CopyDecisions? decisions = null, IProgress<CopyProgress>? progress = null,
                                                      CancellationToken ct = default)
    {
        // 확인 창이 떴다면 거절한다: 미리 받은 확인과 실행기 문장이 어긋나면 조용히 통과하지 않고 표가 실패한다.
        scope.Ui.ConfirmAnswer = false;
        return await NewRunner(scope, log).RunAsync(sessions, job, report, decisions ?? Decisions(report), period, progress, new CopyRunControl(), ct);
    }

    /// <summary>사전 점검 → 실행. 성공이 아니면 시험을 실패시킨다.</summary>
    private static async Task<CopyRunResult> RunOnceAsync(CopyDbScope scope, CopySessions sessions, CopyJob job, ResolvedPeriod period)
    {
        var report = await PrecheckAsync(scope, sessions, job, period);
        var result = await RunAsync(scope, sessions, job, report, period, new RecordingRunLog());
        AssertSucceeded(result);
        return result;
    }

    private static void AssertSucceeded(CopyRunResult result)
        => Assert.True(
            result.Outcome == CopyOutcome.Succeeded && result.FailedRows.Count == 0,
            result.Outcome + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
            + " | notices: " + string.Join(" / ", result.Notices));

    private static Task BothAsync(CopyDbScope scope, string table, string columns)
        => Task.WhenAll(scope.CreateSourceTableAsync(table, columns), scope.CreateTargetTableAsync(table, columns));

    /// <summary>대상에 <c>NAME</c> 에 접두어를 붙이는 트리거를 만든다(꺼져 있었는지 값으로 알 수 있다).</summary>
    private static Task CreatePrefixTriggerAsync(CopyDbScope scope, string trigger, string table)
        => scope.ExecTargetAsync($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW BEGIN :NEW.NAME := 'T:' || :NEW.NAME; END;");

    /// <summary>실행 기록의 <c>Statement:ALTER TRIGGER … DISABLE|ENABLE</c> 한 줄인가.</summary>
    private static bool IsTriggerStatement(string evt, string action)
        => evt.StartsWith("Statement:ALTER TRIGGER", StringComparison.Ordinal) && evt.EndsWith(" " + action, StringComparison.Ordinal);

    private static string Dt(string text) => $"TO_DATE('{text}', 'YYYY-MM-DD HH24:MI:SS')";

    private static string Ts(string text) => $"TIMESTAMP '{text}'";

    /// <summary>문장 여러 개를 PL/SQL 블록 하나로 묶어 한 번에 보내고 커밋한다.</summary>
    private static string Block(params string[] statements)
        => "BEGIN " + string.Join(' ', statements.Select(s => s.TrimEnd(';') + ";")) + " COMMIT; END;";

    // ---- 정답 확인(앱 경로를 거치지 않는 직접 조회) ----

    private string AdminConnectionString => new ConnectionProfile("admin", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword);

    private async Task<OracleConnection> OpenUserAsync(CopyDbScope scope, string user)
    {
        var conn = new OracleConnection(new ConnectionProfile("direct", user, _db.DataSource).BuildConnectionString(scope.Password));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<List<string?[]>> RowsAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await reader.ReadAsync())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task<List<string?[]>> AdminRowsAsync(string sql)
    {
        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        return await RowsAsync(conn, sql);
    }

    private async Task<string?> AdminScalarAsync(string sql) => (await AdminRowsAsync(sql)).FirstOrDefault()?[0];

    private async Task<string?> ScalarAsAsync(CopyDbScope scope, string user, string sql)
    {
        await using var conn = await OpenUserAsync(scope, user);
        return (await RowsAsync(conn, sql)).FirstOrDefault()?[0];
    }

    private async Task<long> CountAsync(string sql)
        => long.Parse(await AdminScalarAsync(sql) ?? "0", CultureInfo.InvariantCulture);

    /// <summary>첫 열 값을 쉼표로 이은 글자(없으면 빈 글자).</summary>
    private async Task<string> ListAsync(string sql)
        => string.Join(',', (await AdminRowsAsync(sql)).Select(r => r[0] ?? "<null>"));

    private Task<string> IdsAsync(string owner, string table) => ListAsync($"SELECT ID FROM {owner}.{table} ORDER BY ID");

    private async Task<string?> TriggerStatusAsync(CopyDbScope scope, string trigger)
        => await AdminScalarAsync($"SELECT STATUS FROM DBA_TRIGGERS WHERE OWNER = '{scope.DstUser}' AND TRIGGER_NAME = '{trigger}'");

    private async Task<string> SnapshotAsync(CopyDbScope scope)
    {
        var parts = new List<string>();
        foreach (var table in RerunTables)
        {
            parts.Add(table + "=" + await ListAsync($"SELECT ID || ':' || VAL FROM {scope.DstUser}.{table} ORDER BY ID"));
        }

        return string.Join('\n', parts);
    }

    // ---- 가짜·도우미 형식 ----

    /// <summary>적재 단계에서 첫 배치를 보낸 뒤 취소한다(동기 보고: <c>Progress&lt;T&gt;</c> 는 비동기로 올려 시점이 흔들린다).</summary>
    private sealed class CancelAfterFirstBatch(CancellationTokenSource cts, string? table = null) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value)
        {
            if (value.Phase == CopyRunner.PhaseLoad && value.Rows > 0 && (table is null || value.Table == table))
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>작업 로그 줄만 동기로 모은다(진행 보고는 버린다).</summary>
    private sealed class LogRecorder : IProgress<CopyProgress>
    {
        private readonly object _gate = new();
        private readonly List<CopyLogEntry> _entries = [];

        public IReadOnlyList<CopyLogEntry> Entries
        {
            get { lock (_gate) { return [.. _entries]; } }
        }

        public void Report(CopyProgress value)
        {
            if (value.Log is { } entry)
            {
                lock (_gate) { _entries.Add(entry); }
            }
        }
    }

    /// <summary>
    /// 실행 기록 가짜. 사건을 순서대로 쌓는다: <c>Disabled</c>, <c>Statement:&lt;sql&gt;</c>, <c>Table</c>, <c>FailedRow</c>, <c>Complete:&lt;outcome&gt;</c>.
    /// Platform 의 실제 구현(<c>CopyRunLog</c>)은 이 시험 프로젝트가 참조하지 않는다.
    /// </summary>
    private sealed class RecordingRunLog : ICopyRunLog
    {
        public List<string> Events { get; } = [];

        public List<DisabledObject> DisabledObjects { get; } = [];

        public List<FailedRow> FailedRows { get; } = [];

        public List<TableRunResult> Tables { get; } = [];

        /// <summary>끈 목록을 기록하는 순간에 부른다(그때 대상 상태를 읽어 「DISABLE 전에 기록」을 증명한다).</summary>
        public Func<Task>? OnRecordDisabled { get; set; }

        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(new Record(this));

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

        private sealed class Record(RecordingRunLog log) : ICopyRunRecord
        {
            // 실제 CopyRunLog 처럼 취소된 토큰이면 쓰지 않고 예외를 낸다(정리 단계에 ct 를 넘기는 퇴행을 잡는다).
            public async Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (log.OnRecordDisabled is { } hook)
                {
                    await hook();
                }

                log.Events.Add("Disabled");
                log.DisabledObjects.AddRange(objects);
            }

            public Task RecordStatementAsync(string sql, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                log.Events.Add("Statement:" + sql);
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
                log.FailedRows.Add(row);
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
