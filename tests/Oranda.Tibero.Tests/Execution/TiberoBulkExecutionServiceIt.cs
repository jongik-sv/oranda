using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Integration;
using Xunit.Abstractions;

// 측정이 섞인 쓰기 시험: 서버가 거부하는 것도 사실이므로 측정 단계는 던지지 않고 번호·메시지를 찍는다(단정은 설계가 약속한 동작에만 건다).

namespace Oranda.Tibero.Tests.Execution;

/// <summary>
/// Tibero 대량 실행 서비스(<see cref="TiberoBulkExecutionService"/>, TB1 설계 4.4)를 실제 서버로 확인하는 쓰기 IT.
/// <list type="bullet">
/// <item><c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돈다(<see cref="TiberoItSessions.SkipWrite"/>). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 만드는 표는 모두 <c>ORT_TB1_*</c> 이고 끝에 DROP … PURGE 로 지운다(실패는 무시). 표당 수백 행, 배치 50. 쓰기 IT 는 한 번에 한 레인만 돌린다.</item>
/// <item>Tibero 복사 방언(TB3b)이 아직 없으므로 INSERT·MERGE 글은 시험이 <see cref="TiberoBulkValueCodec.BindExpression"/> 으로 직접 만든다.</item>
/// <item>결과는 시험 로그의 <c>TB1 …</c> 줄로 읽는다(설계 문서에 옮길 사실). 접속 정보(주소·계정·비밀번호)는 어디에도 찍지 않는다.</item>
/// <item>실행: <c>ORANDA_IT_TIBERO_WRITE=1 dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~TiberoBulkExecutionServiceIt" --logger "console;verbosity=detailed"</c></item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoBulkExecutionServiceIt(ITestOutputHelper output)
{
    private const int RowCount = 300;
    private const int Batch = 50;
    private const string Savepoint = "ORANDA_COPY_BATCH";
    private const string Digits38 = "12345678901234567890123456789012345678";

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private static readonly Col[] RoundTripCols =
    [
        new("ID", "NUMBER(10)"),
        new("N10", "NUMBER(38,10)"),
        new("NUM", "NUMBER"),
        new("VC", "VARCHAR2(100)"),
        new("CH5", "CHAR(5)"),
        new("NVC", "NVARCHAR2(50)"),
        new("CL", "CLOB"),
        new("DT", "DATE"),
        new("TS", "TIMESTAMP(9)"),
        new("RW", "RAW(2000)"),
    ];

    private static readonly Col[] IdText = [new("ID", "NUMBER(10)"), new("VC", "VARCHAR2(30)")];

    // =================================================================================================================
    // 1. 왕복: 지원 형식 전부 · 읽기(StreamRowsAsync) → 코덱 감싸기 INSERT(배치 50) → 두 표 비교
    // =================================================================================================================

    /// <summary>
    /// 원본 표(SQL 로 채움 + 바인드로 NVARCHAR2·100k자 CLOB) 300행을 StreamRowsAsync 로 읽어 배치 50 으로 대상 표에 넣고, SQL(MINUS·DBMS_LOB.COMPARE)과
    /// 값·키 글자 비교로 같음을 확인한다. NULL·DATE 끝값·TIMESTAMP(9) 나노초·CHAR 끝 공백·CP949 밖 글자·RAW 2000B 포함.
    /// </summary>
    [Fact]
    public async Task Step1_round_trip_of_all_supported_types_through_stream_and_array_insert()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var src = await it.OpenAsync();
        var dst = await it.OpenAsync();
        var tSrc = TiberoItSessions.ObjectName("TB1_SRC");
        var tDst = TiberoItSessions.ObjectName("TB1_DST");
        await CleanupAsync([src, dst], tSrc, tDst);
        try
        {
            await ExecAsync(src, RoundTripDdl(tSrc));
            await ExecAsync(dst, RoundTripDdl(tDst));
            await ExecAsync(src, RoundTripSeed(tSrc));
            await UpdateNationalAndBigClobAsync(service, src, tSrc);
            await ExecAsync(src, "COMMIT");
            Log("step1 server types " + await QueryTextAsync(src,
                $"SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{tSrc}' ORDER BY COLUMN_ID", 20));

            // 읽기 → 쓰기(배치 50). 원본 세션은 열거가 끝날 때까지 잠겨 있고 대상은 다른 세션이다.
            var names = string.Join(", ", RoundTripCols.Select(c => c.Name));
            var insert = InsertSql(tDst, RoundTripCols);
            var bulk = BulkCols(RoundTripCols);
            var batch = new List<object?[]>(Batch);
            var batches = 0;
            var copied = 0;
            var sw = Stopwatch.StartNew();
            await foreach (var row in service.StreamRowsAsync(src, Query($"SELECT {names} FROM {tSrc} ORDER BY ID"), NoBinds, ExecutionOptions.Default,
                               CancellationToken.None))
            {
                batch.Add((object?[])row.Clone());
                if (batch.Count == Batch)
                {
                    AssertOk(await RunAsync(service, dst, insert, bulk, batch), "round trip batch " + N(batches + 1), batch.Count);
                    copied += batch.Count;
                    batches++;
                    batch = new List<object?[]>(Batch);
                }
            }

            if (batch.Count > 0)
            {
                AssertOk(await RunAsync(service, dst, insert, bulk, batch), "round trip last batch", batch.Count);
                copied += batch.Count;
                batches++;
            }

            await ExecAsync(dst, "COMMIT");
            Log($"step1 round trip rows={N(copied)} batches={N(batches)} ms={N(sw.ElapsedMilliseconds)}");
            Assert.Equal(RowCount, copied);

            // (1) SQL 로 비교: CLOB 을 뺀 열은 MINUS 양쪽, CLOB 은 DBMS_LOB.COMPARE.
            var plain = "ID, N10, NUM, VC, CH5, NVC, DT, TS, RW";
            var onlySrc = await ScalarAsync(dst, $"SELECT TO_CHAR(COUNT(*)) FROM (SELECT {plain} FROM {tSrc} MINUS SELECT {plain} FROM {tDst})");
            var onlyDst = await ScalarAsync(dst, $"SELECT TO_CHAR(COUNT(*)) FROM (SELECT {plain} FROM {tDst} MINUS SELECT {plain} FROM {tSrc})");
            var clobDiff = await ScalarAsync(dst,
                $"SELECT TO_CHAR(COUNT(*)) FROM {tSrc} s JOIN {tDst} d ON d.ID = s.ID WHERE DBMS_LOB.COMPARE(s.CL, d.CL) <> 0 "
                + "OR (s.CL IS NULL AND d.CL IS NOT NULL) OR (s.CL IS NOT NULL AND d.CL IS NULL)");
            var dstCount = await ScalarAsync(dst, $"SELECT TO_CHAR(COUNT(*)) FROM {tDst}");
            Log($"step1 sql compare onlySrc={onlySrc} onlyDst={onlyDst} clobDiff={clobDiff} dstCount={dstCount}");
            Assert.Equal("0", onlySrc);
            Assert.Equal("0", onlyDst);
            Assert.Equal("0", clobDiff);
            Assert.Equal(N(RowCount), dstCount);

            // (2) 다시 읽어 값·키 글자로 비교(두 표를 같은 읽기 경로로).
            var select = $"SELECT {names} FROM {{0}} ORDER BY ID";
            var srcRows = await StreamAllAsync(service, src, Query(string.Format(CultureInfo.InvariantCulture, select, tSrc)));
            var dstRows = await StreamAllAsync(service, dst, Query(string.Format(CultureInfo.InvariantCulture, select, tDst)));
            Assert.Equal(RowCount, srcRows.Count);
            Assert.Equal(RowCount, dstRows.Count);
            var mismatches = new List<string>();
            for (var r = 0; r < srcRows.Count && mismatches.Count < 20; r++)
            {
                for (var c = 0; c < RoundTripCols.Length; c++)
                {
                    var col = RoundTripCols[c];
                    var different = !SameCell(srcRows[r][c], dstRows[r][c]);
                    if (!different && TiberoBulkTypes.ShapeOf(col.ServerType).KeyBind is not null)
                    {
                        different = TiberoBulkValueCodec.Instance.ToKeyText(srcRows[r][c], col.ServerType)
                                    != TiberoBulkValueCodec.Instance.ToKeyText(dstRows[r][c], col.ServerType);
                    }

                    if (different)
                    {
                        mismatches.Add($"row {N(r + 1)} column {col.Name}");
                    }
                }
            }

            Assert.Empty(mismatches);

            // (3) 읽은 값의 모양: NUMBER = TiberoBulkNumber, TIMESTAMP = TiberoBulkTimestamp(나노초 보존), CHAR 끝 공백, CP949 밖 글자, 100k자 CLOB.
            var first = srcRows[0];
            Assert.IsType<TiberoBulkNumber>(first[0]);
            Assert.IsType<TiberoBulkNumber>(first[1]);
            Assert.IsType<TiberoBulkNumber>(first[2]);
            Assert.Equal("ab   ", first[4]);
            Assert.Equal(NationalText(1), first[5]);
            Assert.True(string.Equals(BigClob, (string)first[6]!, StringComparison.Ordinal), "CLOB 100000 chars survived the read");
            Assert.Equal(new DateTime(1, 1, 1, 0, 0, 0), first[7]);
            Assert.Equal(new TiberoBulkTimestamp(new DateTime(2026, 1, 2, 3, 4, 1), 3_333_331), first[8]);
            Assert.Equal(new byte[] { 0x0A, 0xFF }, first[9]);
            Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59), srcRows[1][7]);
            Assert.Equal(2000, ((byte[])srcRows[24][9]!).Length);
            Assert.Null(srcRows[7][6]); // 8번째 행의 CLOB = NULL
            Log("step1 sample row1 " + string.Join(" | ", first.Select(Fmt)));
        }
        finally
        {
            await CleanupAsync([src, dst], tSrc, tDst);
        }
    }

    // =================================================================================================================
    // 1b. (b) INT·SMALLINT·FLOAT·DOUBLE·REAL 열도 NUMBER 형식으로 읽힌다
    // =================================================================================================================

    /// <summary>
    /// 메모 확인 (b): 정수·실수 선언 열이 StreamRowsAsync 에서 모두 <see cref="TiberoBulkNumber"/> 인가(정밀도 바뀜 없이). 38자리 INTEGER 가 그대로 나오는지,
    /// BINARY_FLOAT/DOUBLE 은 float/double 인지(만들어지면) 본다.
    /// </summary>
    [Fact]
    public async Task Step1b_integer_float_and_double_columns_are_read_as_number_values()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TB1_NUM");
        await CleanupAsync([s], t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER(5) PRIMARY KEY, I INTEGER, SI SMALLINT, DEC2 NUMERIC(10,2), FL FLOAT, DP DOUBLE PRECISION, RL REAL, NN NUMBER)");
            await ExecAsync(s, $"INSERT INTO {t} VALUES (1, 42, -7, 12345.67, 0.1, 0.1, 0.1, 1.5)");
            await ExecAsync(s, $"INSERT INTO {t} VALUES (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL)");
            await ExecAsync(s, $"INSERT INTO {t} VALUES (3, 99999999999999999999999999999999999999, 32767, -99999999.99, 1E100, 1/3, 1/3, -0.000001)");
            Log("step1b server types " + await QueryTextAsync(s,
                $"SELECT COLUMN_NAME, DATA_TYPE, DATA_PRECISION, DATA_SCALE FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' ORDER BY COLUMN_ID", 20));

            var binary = await TryTextAsync(s, $"ALTER TABLE {t} ADD (BF BINARY_FLOAT, BD BINARY_DOUBLE)");
            Log("step1b add BINARY_FLOAT/BINARY_DOUBLE " + binary);
            var hasBinary = binary.StartsWith("OK", StringComparison.Ordinal);
            if (hasBinary)
            {
                await ExecAsync(s, $"UPDATE {t} SET BF = 1.5, BD = 2.25 WHERE ID = 1");
            }

            var rows = await StreamAllAsync(service, s, Query($"SELECT ID, I, SI, DEC2, FL, DP, RL, NN{(hasBinary ? ", BF, BD" : string.Empty)} FROM {t} ORDER BY ID"));
            Assert.Equal(3, rows.Count);
            var labels = new[] { "ID", "I", "SI", "DEC2", "FL", "DP", "RL", "NN" };
            foreach (var row in rows)
            {
                Log("step1b row " + string.Join(" | ", row.Select((v, i) => (i < labels.Length ? labels[i] : i == 8 ? "BF" : "BD") + "=" + Fmt(v))));
            }

            for (var c = 0; c < labels.Length; c++)
            {
                Assert.True(rows[0][c] is TiberoBulkNumber, "row 1 column " + labels[c] + " is TiberoBulkNumber");
                Assert.True(rows[2][c] is TiberoBulkNumber, "row 3 column " + labels[c] + " is TiberoBulkNumber");
                if (c > 0)
                {
                    Assert.Null(rows[1][c]);
                }
            }

            Assert.Equal("42", ((TiberoBulkNumber)rows[0][1]!).Text);
            Assert.Equal("-7", ((TiberoBulkNumber)rows[0][2]!).Text);
            Assert.Equal("12345.67", ((TiberoBulkNumber)rows[0][3]!).Text);
            Assert.Equal("1.5", ((TiberoBulkNumber)rows[0][7]!).Text);
            Assert.Equal(new string('9', 38), ((TiberoBulkNumber)rows[2][1]!).Text);
            Assert.Equal("32767", ((TiberoBulkNumber)rows[2][2]!).Text);
            Assert.Equal("-99999999.99", ((TiberoBulkNumber)rows[2][3]!).Text);
            Assert.Equal("-0.000001", ((TiberoBulkNumber)rows[2][7]!).Text);
            if (hasBinary)
            {
                Assert.Equal(1.5f, rows[0][8]);
                Assert.Equal(2.25d, rows[0][9]);
            }
        }
        finally
        {
            await CleanupAsync([s], t);
        }
    }

    // =================================================================================================================
    // 2. 묶음 실패: ORANDA-ARRAY-PARTIAL → ROLLBACK TO SAVEPOINT → 한 행씩
    // =================================================================================================================

    /// <summary>
    /// 50행 배열의 가운데(25번째)가 PK 중복이면 오류 없이 앞 24행만 들어가고 ORANDA-ARRAY-PARTIAL 로 실패한다. 세이브포인트로 되돌린 뒤 한 행씩 넣으면
    /// 중복 행만 서버 오류(10007, ConstraintViolation)로 실패한다. 그다음 같은 서비스로 300행을 배치 50 으로 넣는다.
    /// </summary>
    [Fact]
    public async Task Step2_array_batch_with_a_duplicate_key_fails_as_partial_then_rows_one_by_one_isolate_the_bad_row()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TB1_DST");
        await CleanupAsync([s], t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER(10) PRIMARY KEY, VC VARCHAR2(30))");
            await ExecAsync(s, $"INSERT INTO {t} VALUES (125, 'seed')");
            await ExecAsync(s, "COMMIT");
            var insert = InsertSql(t, IdText);
            var cols = BulkCols(IdText);
            var rows = Enumerable.Range(101, Batch).Select(id => new object?[] { Num(id), "row" + N(id) }).ToList();

            AssertOk(await ControlAsync(service, s, "SAVEPOINT " + Savepoint), "savepoint");
            var partial = await RunAsync(service, s, insert, cols, rows);
            Log("step2 array result " + Show(partial));
            Assert.Equal(ExecutionStatus.Failed, partial.Status);
            Assert.Equal(0, partial.RowsAffected);
            Assert.Equal(TiberoBulkExecutionService.PartialCode, partial.Error?.Code);
            Assert.Contains("앞 24행", partial.Error!.Message, StringComparison.Ordinal);
            Assert.Contains("25번째 행에서", partial.Error.Message, StringComparison.Ordinal);
            Assert.True(s.HasPendingTransaction, "the first 24 rows stay in the open transaction");
            Log("step2 rows in transaction after partial " + await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));

            AssertOk(await ControlAsync(service, s, "ROLLBACK TO SAVEPOINT " + Savepoint), "rollback to savepoint");
            Assert.Equal("1", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));

            var failed = new List<long>();
            foreach (var row in rows)
            {
                var one = await RunAsync(service, s, insert, cols, [row]);
                if (one.Status == ExecutionStatus.Succeeded)
                {
                    Assert.Equal(1, one.RowsAffected);
                    continue;
                }

                var id = long.Parse(((TiberoBulkNumber)row[0]!).Text, CultureInfo.InvariantCulture);
                failed.Add(id);
                Log("step2 single row failure " + Show(one));
                Assert.Equal(ExecutionStatus.Failed, one.Status);
                Assert.Equal(DbErrorKind.ConstraintViolation, one.Error?.Kind);
                Assert.Equal(10007, Math.Abs(one.Error!.Number));
            }

            Assert.Equal([125L], failed);
            AssertOk(await ControlAsync(service, s, "COMMIT"), "commit");
            Assert.Equal("50", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));

            // 수백 행을 배치 50 으로: 모두 한 번에 들어간다.
            var sw = Stopwatch.StartNew();
            for (var start = 1000; start < 1000 + RowCount; start += Batch)
            {
                var chunk = Enumerable.Range(start, Batch).Select(id => new object?[] { Num(id), "bulk" + N(id) }).ToList();
                AssertOk(await RunAsync(service, s, insert, cols, chunk), "bulk batch from " + N(start), Batch);
            }

            AssertOk(await ControlAsync(service, s, "COMMIT"), "commit bulk");
            Log($"step2 bulk rows={N(RowCount)} ms={N(sw.ElapsedMilliseconds)}");
            Assert.Equal(N(50 + RowCount), await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));
        }
        finally
        {
            await CleanupAsync([s], t);
        }
    }

    // =================================================================================================================
    // 3. BLOB 표: 한 행씩 경로
    // =================================================================================================================

    /// <summary>
    /// BLOB 열이 있는 3행 묶음(1 MB·가운데 PK 중복·NULL)은 첫 실패 행에서 멈추고(Failed, 세 번째 행은 안 감) 앞 행은 트랜잭션에 남는다. 되돌리고 한 행씩 넣으면
    /// 중복만 실패. 빈 byte[] 는 NULL 로 저장되는지 측정하고, 3000B BLOB 200행을 배치 50 으로 넣어 읽어 본다.
    /// </summary>
    [Fact]
    public async Task Step3_blob_table_runs_row_by_row_and_stops_at_the_first_failing_row()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TB1_BLOB");
        await CleanupAsync([s], t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER(10) PRIMARY KEY, B BLOB)");
            await ExecAsync(s, $"INSERT INTO {t} VALUES (2, NULL)");
            await ExecAsync(s, "COMMIT");
            Col[] colSpec = [new("ID", "NUMBER(10)"), new("B", "BLOB")];
            var insert = InsertSql(t, colSpec);
            var cols = BulkCols(colSpec);
            var mega = Pattern(1024 * 1024);
            var rows = new List<object?[]>
            {
                new object?[] { Num(1), mega },
                new object?[] { Num(2), Pattern(3000) }, // PK 중복
                new object?[] { Num(3), null },
            };

            AssertOk(await ControlAsync(service, s, "SAVEPOINT " + Savepoint), "savepoint");
            var failed = await RunAsync(service, s, insert, cols, rows);
            Log("step3 batch result " + Show(failed));
            Assert.Equal(ExecutionStatus.Failed, failed.Status);
            Assert.Equal(0, failed.RowsAffected);
            Assert.Equal("2", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}")); // 시드 + 1번 행, 3번 행은 안 감
            AssertOk(await ControlAsync(service, s, "ROLLBACK TO SAVEPOINT " + Savepoint), "rollback to savepoint");
            Assert.Equal("1", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));

            var results = new List<BulkResult>();
            foreach (var row in rows)
            {
                results.Add(await RunAsync(service, s, insert, cols, [row]));
            }

            Log("step3 single rows " + string.Join(" ; ", results.Select(Show)));
            Assert.Equal(ExecutionStatus.Succeeded, results[0].Status);
            Assert.Equal(ExecutionStatus.Failed, results[1].Status);
            Assert.Equal(DbErrorKind.ConstraintViolation, results[1].Error?.Kind);
            Assert.Equal(ExecutionStatus.Succeeded, results[2].Status);
            AssertOk(await ControlAsync(service, s, "COMMIT"), "commit");
            Assert.Equal("1048576", await ScalarAsync(s, $"SELECT TO_CHAR(DBMS_LOB.GETLENGTH(B)) FROM {t} WHERE ID = 1"));
            Assert.Equal("1", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t} WHERE ID = 3 AND B IS NULL"));

            // 읽기: BLOB 은 끝까지 읽은 byte[].
            var read = await StreamAllAsync(service, s, Query($"SELECT ID, B FROM {t} WHERE ID IN (1, 3) ORDER BY ID"));
            Assert.Equal(2, read.Count);
            Assert.Equal(mega, read[0][1]);
            Assert.Null(read[1][1]);

            // 빈 byte[] → NULL 인가(측정).
            var empty = await RunAsync(service, s, insert, cols, [new object?[] { Num(4), Array.Empty<byte>() }]);
            Log("step3 measure empty byte[] result " + Show(empty) + " storedAsNull="
                + await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t} WHERE ID = 4 AND B IS NULL"));
            AssertOk(await ControlAsync(service, s, "COMMIT"), "commit");

            // 수백 행: 3000B BLOB 200행, 배치 50 → 한 행씩 경로로 모두 성공.
            var sw = Stopwatch.StartNew();
            for (var start = 1000; start < 1200; start += Batch)
            {
                var chunk = Enumerable.Range(start, Batch).Select(id => new object?[] { Num(id), Pattern(3000) }).ToList();
                AssertOk(await RunAsync(service, s, insert, cols, chunk), "blob batch from " + N(start), Batch);
            }

            AssertOk(await ControlAsync(service, s, "COMMIT"), "commit bulk");
            Log($"step3 bulk blob rows=200 ms={N(sw.ElapsedMilliseconds)}");
            Assert.Equal("200;600000", await ScalarAsync(s,
                $"SELECT TO_CHAR(COUNT(*)) || ';' || TO_CHAR(SUM(DBMS_LOB.GETLENGTH(B))) FROM {t} WHERE ID >= 1000"));
        }
        finally
        {
            await CleanupAsync([s], t);
        }
    }

    // =================================================================================================================
    // 4. MERGE
    // =================================================================================================================

    /// <summary>
    /// UPDATE 가 있는 MERGE 는 일치·새 행이 섞여도 k == n. 키 열만 있는 MERGE 는 일치 행이 있으면 반환 행 수가 n 보다 작아 ORANDA-ARRAY-PARTIAL 이 되고(설계 3.2),
    /// 되돌린 뒤 한 행씩 보내면 일치 행(반환 0)도 성공한다. 모두 새 행이면 배열 한 번에 성공.
    /// </summary>
    [Fact]
    public async Task Step4_merge_with_update_returns_n_and_key_only_merge_with_matches_falls_back_to_rows_one_by_one()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TB1_DST");
        await CleanupAsync([s], t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER(10) PRIMARY KEY, VC VARCHAR2(30), NUM NUMBER)");
            await ExecAsync(s, $"INSERT INTO {t} SELECT LEVEL, 'old', 0 FROM DUAL CONNECT BY LEVEL <= 100");
            await ExecAsync(s, "COMMIT");
            Col[] full = [new("ID", "NUMBER(10)"), new("VC", "VARCHAR2(30)"), new("NUM", "NUMBER")];
            Col[] keyOnly = [new("ID", "NUMBER(10)")];
            var withUpdate = MergeSql(t, full, 1);
            var keyOnlySql = MergeSql(t, keyOnly, 1);
            Log("step4 merge text " + withUpdate);
            Log("step4 key-only merge text " + keyOnlySql);

            object?[] FullRow(int id) => [Num(id), "new" + N(id), Num(id * 10)];
            object?[] KeyRow(int id) => [Num(id)];

            // (a) 50행 모두 일치 → UPDATE.
            var allMatched = await RunAsync(service, s, withUpdate, BulkCols(full), Enumerable.Range(1, Batch).Select(FullRow).ToList());
            Log("step4 update-all-matched " + Show(allMatched));
            AssertOk(allMatched, "merge all matched", Batch);
            Assert.Equal("50", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t} WHERE VC LIKE 'new%' AND NUM = ID * 10"));

            // (b) 일치 10 + 새 행 40.
            var mixed = await RunAsync(service, s, withUpdate, BulkCols(full), Enumerable.Range(91, Batch).Select(FullRow).ToList());
            Log("step4 update-mixed " + Show(mixed));
            AssertOk(mixed, "merge matched and new", Batch);
            Assert.Equal("140", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));

            // (c) 키만, 50행 모두 일치 → k < n → PARTIAL → 되돌림 → 한 행씩 모두 성공.
            AssertOk(await ControlAsync(service, s, "SAVEPOINT " + Savepoint), "savepoint");
            var keysMatched = await RunAsync(service, s, keyOnlySql, BulkCols(keyOnly), Enumerable.Range(1, Batch).Select(KeyRow).ToList());
            Log("step4 key-only-all-matched " + Show(keysMatched));
            Assert.Equal(ExecutionStatus.Failed, keysMatched.Status);
            Assert.Equal(TiberoBulkExecutionService.PartialCode, keysMatched.Error?.Code);
            AssertOk(await ControlAsync(service, s, "ROLLBACK TO SAVEPOINT " + Savepoint), "rollback to savepoint");
            var oneByOne = 0;
            foreach (var id in Enumerable.Range(1, Batch))
            {
                var one = await RunAsync(service, s, keyOnlySql, BulkCols(keyOnly), [KeyRow(id)]);
                AssertOk(one, "key-only single row " + N(id));
                oneByOne++;
            }

            Log($"step4 key-only one by one succeeded={N(oneByOne)}");
            Assert.Equal("140", await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));

            // (d) 키만, 모두 새 행 → 배열 한 번.
            var keysNew = await RunAsync(service, s, keyOnlySql, BulkCols(keyOnly), Enumerable.Range(200, Batch).Select(KeyRow).ToList());
            Log("step4 key-only-all-new " + Show(keysNew));
            AssertOk(keysNew, "key-only all new", Batch);

            // (e) 키만, 일치 11 + 새 39: PARTIAL 인 경우 k 를 로그에 남기고 같은 방식으로 한 행씩.
            AssertOk(await ControlAsync(service, s, "SAVEPOINT " + Savepoint), "savepoint");
            var keysMixed = await RunAsync(service, s, keyOnlySql, BulkCols(keyOnly), Enumerable.Range(130, Batch).Select(KeyRow).ToList());
            Log("step4 key-only-mixed " + Show(keysMixed));
            if (keysMixed.Status != ExecutionStatus.Succeeded)
            {
                Assert.Equal(TiberoBulkExecutionService.PartialCode, keysMixed.Error?.Code);
                AssertOk(await ControlAsync(service, s, "ROLLBACK TO SAVEPOINT " + Savepoint), "rollback to savepoint");
                foreach (var id in Enumerable.Range(130, Batch))
                {
                    AssertOk(await RunAsync(service, s, keyOnlySql, BulkCols(keyOnly), [KeyRow(id)]), "key-only single row " + N(id));
                }
            }

            AssertOk(await ControlAsync(service, s, "COMMIT"), "commit");
            Assert.Equal(N(140 + Batch + 39), await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t}"));
        }
        finally
        {
            await CleanupAsync([s], t);
        }
    }

    // =================================================================================================================
    // 5. NUMBER 끝값 · 배열 vs 스칼라 Decimal+글자
    // =================================================================================================================

    /// <summary>
    /// 38자리·1E125·1E-125·-0.000001·0 을 스칼라(행 1)·배열(7행)·두 행 배열(값마다)로 넣어 읽은 <see cref="TiberoBulkNumber.Text"/> 가 보낸 값과 같은지 본다.
    /// 어느 경로가 어떤 값에서 실패하는지는 로그에 남고, 실패는 모아서 끝에 단정한다. 39자리(38자리로 반올림 저장)는 측정만.
    /// </summary>
    [Fact]
    public async Task Step5_number_extreme_values_survive_array_and_scalar_binds()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TB1_DST");
        await CleanupAsync([s], t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER(10) PRIMARY KEY, N NUMBER)");
            Col[] spec = [new("ID", "NUMBER(10)"), new("N", "NUMBER")];
            var insert = InsertSql(t, spec);
            var cols = BulkCols(spec);
            (string Label, string Text)[] values =
            [
                ("digits38", Digits38),
                ("neg_digits38", "-" + Digits38),
                ("fraction38", "0." + Digits38),
                ("e125", "1" + new string('0', 125)),
                ("e_minus125", "0." + new string('0', 124) + "1"),
                ("neg_micro", "-0.000001"),
                ("zero", "0"),
            ];
            var expected = new Dictionary<long, (string Label, string Text)>();
            var problems = new List<string>();

            async Task InsertAsync(string path, List<object?[]> rows)
            {
                // 실패한 호출만 되돌리도록 호출마다 세이브포인트를 새로 잡는다(앞서 성공한 행은 남긴다).
                AssertOk(await ControlAsync(service, s, "SAVEPOINT " + Savepoint), "savepoint");
                var result = await RunAsync(service, s, insert, cols, rows);
                Log($"step5 {path} rows={N(rows.Count)} {Show(result)}");
                if (result.Status != ExecutionStatus.Succeeded)
                {
                    problems.Add($"{path} insert failed ({Show(result)})");
                    foreach (var row in rows)
                    {
                        expected.Remove(long.Parse(((TiberoBulkNumber)row[0]!).Text, CultureInfo.InvariantCulture));
                    }

                    await ControlAsync(service, s, "ROLLBACK TO SAVEPOINT " + Savepoint);
                }
            }

            object?[] RowOf(long id, string label, string text)
            {
                var number = TiberoBulkNumber.Parse(text);
                expected[id] = (label, number.Text);
                return [Num(id), number];
            }

            // 스칼라(행 1): Decimal 종류 + 글자.
            for (var i = 0; i < values.Length; i++)
            {
                await InsertAsync("scalar " + values[i].Label, [RowOf(100 + i, values[i].Label, values[i].Text)]);
            }

            // 배열 7행 한꺼번에.
            await InsertAsync("array7", values.Select((v, i) => RowOf(200 + i, v.Label, v.Text)).ToList());

            // 두 행 배열(값마다) — 7행이 실패해도 어느 값이 문제인지 가린다.
            for (var i = 0; i < values.Length; i++)
            {
                await InsertAsync("array2 " + values[i].Label, [RowOf(300 + (i * 2), values[i].Label, values[i].Text), RowOf(301 + (i * 2), "one", "1")]);
            }

            // 39자리: 38자리로 반올림해 저장되는지(측정).
            var digits39 = "123456789012345678901234567890123456789";
            var stored39 = await RunAsync(service, s, insert, cols, [new object?[] { Num(900), TiberoBulkNumber.Parse(digits39) }]);
            Log("step5 measure digits39 scalar " + Show(stored39));

            var read = await StreamAllAsync(service, s, Query($"SELECT ID, N FROM {t} ORDER BY ID"));
            var actual = read.ToDictionary(r => long.Parse(((TiberoBulkNumber)r[0]!).Text, CultureInfo.InvariantCulture), r => r[1]);
            foreach (var (id, want) in expected)
            {
                if (!actual.TryGetValue(id, out var got) || got is not TiberoBulkNumber n || n.Text != want.Text)
                {
                    problems.Add($"id {N(id)} {want.Label} read back {(actual.TryGetValue(id, out var g) ? Fmt(g) : "(missing)")}");
                }
            }

            if (actual.TryGetValue(900, out var read39))
            {
                Log("step5 measure digits39 stored as " + Fmt(read39));
            }

            Assert.Empty(problems);
        }
        finally
        {
            await CleanupAsync([s], t);
        }
    }

    // =================================================================================================================
    // 6. 키 되읽기: 38자리 NUMBER · TIMESTAMP(9) · DATE 키 바인드로 한 행 찾기
    // =================================================================================================================

    /// <summary>
    /// 읽은 키 값을 실패 행 키 글자(<c>ToKeyText</c>/<c>FromKeyText</c>)로 왕복시켜 <see cref="TiberoBulkNumber"/>(38자리)·<see cref="TiberoBulkTimestamp"/>(나노초 9자리)·
    /// DateTime 으로 바인드하면 마지막 자리만 다른 이웃 행과 섞이지 않고 정확히 한 행을 찾는지 본다.
    /// </summary>
    [Fact]
    public async Task Step6_keys_of_38_digits_and_9_digit_timestamps_find_exactly_one_row()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TB1_KEY");
        await CleanupAsync([s], t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (NK NUMBER(38) PRIMARY KEY, TK TIMESTAMP(9), DK DATE, VC VARCHAR2(20))");
            await ExecAsync(s, $$"""
                INSERT INTO {{t}}
                SELECT TO_NUMBER('1234567890123456789012345678901234567' || TO_CHAR(LEVEL - 1)),
                       TO_TIMESTAMP('2026-05-06 07:08:09.12345678' || TO_CHAR(LEVEL - 1), 'YYYY-MM-DD HH24:MI:SS.FF9'),
                       TO_DATE('2026-05-06 07:08:09', 'YYYY-MM-DD HH24:MI:SS') + (LEVEL - 1) / 86400,
                       'key' || TO_CHAR(LEVEL - 1)
                FROM DUAL CONNECT BY LEVEL <= 10
                """);
            await ExecAsync(s, "COMMIT");

            var codec = TiberoBulkValueCodec.Instance;
            const string numberType = "NUMBER(38)";
            const string stampType = "TIMESTAMP(9)";
            const string dateType = "DATE";
            Assert.Equal(BindDataType.Number, codec.BindTypeOf(numberType));
            Assert.Equal(BindDataType.Timestamp, codec.BindTypeOf(stampType));
            Assert.Equal(BindDataType.Date, codec.BindTypeOf(dateType));

            var keys = await StreamAllAsync(service, s, Query($"SELECT NK, TK, DK, VC FROM {t} ORDER BY NK"));
            Assert.Equal(10, keys.Count);
            for (var i = 0; i < keys.Count; i++)
            {
                Assert.Equal("1234567890123456789012345678901234567" + N(i), ((TiberoBulkNumber)keys[i][0]!).Text);
                Assert.Equal(new TiberoBulkTimestamp(new DateTime(2026, 5, 6, 7, 8, 9), 123_456_780 + i), keys[i][1]);
            }

            var first = keys[0];
            Log($"step6 key texts number={codec.ToKeyText(first[0], numberType)} timestamp={codec.ToKeyText(first[1], stampType)} date={codec.ToKeyText(first[2], dateType)}");
            var stamp9 = codec.ToKeyText(keys[3][1], stampType);
            Log("step6 timestamp key with 9th digit " + stamp9);

            foreach (var row in keys)
            {
                var texts = new[]
                {
                    codec.ToKeyText(row[0], numberType),
                    codec.ToKeyText(row[1], stampType),
                    codec.ToKeyText(row[2], dateType),
                };
                var binds = new Dictionary<string, BindValue>
                {
                    ["K1"] = new(codec.BindTypeOf(numberType), codec.FromKeyText(texts[0], numberType)),
                    ["K2"] = new(codec.BindTypeOf(stampType), codec.FromKeyText(texts[1], stampType)),
                    ["K3"] = new(codec.BindTypeOf(dateType), codec.FromKeyText(texts[2], dateType)),
                };
                var wanted = (string)row[3]!;

                var all = await StreamAllAsync(service,
                    s, Query($"SELECT VC FROM {t} WHERE NK = :K1 AND TK = :K2 AND DK = :K3", "K1", "K2", "K3"), binds);
                var byNumber = await StreamAllAsync(service, s, Query($"SELECT VC FROM {t} WHERE NK = :K1", "K1"), binds);
                var byStamp = await StreamAllAsync(service, s, Query($"SELECT VC FROM {t} WHERE TK = :K2", "K2"), binds);
                Assert.Equal([wanted], all.Select(r => (string?)r[0]));
                Assert.Equal([wanted], byNumber.Select(r => (string?)r[0]));
                Assert.Equal([wanted], byStamp.Select(r => (string?)r[0]));
            }

            Log("step6 all 10 rows found by 38-digit number, 9-digit timestamp and date keys");
        }
        finally
        {
            await CleanupAsync([s], t);
        }
    }

    // =================================================================================================================
    // 7. 취소
    // =================================================================================================================

    /// <summary>
    /// 서버가 오래 계산하는 읽기(3중 카티전 SUM)와 행이 계속 나오는 읽기(CONNECT BY 5천만 행)를 읽는 도중 취소하면
    /// <see cref="OperationCanceledException"/> 이다. 취소 뒤 명령 잠금이 풀리고 세션이 Idle 이며 다음 SELECT 1 이 된다. 서버 CPU 를 쓰므로 한 번에 하나만 돌린다.
    /// </summary>
    [Fact]
    public async Task Step7_cancelling_a_long_read_throws_cancellation_releases_the_lock_and_the_session_keeps_working()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();

        // (a) 서버가 계산 중일 때 취소.
        const string heavy = "SELECT SUM(a.l + b.l + c.l) FROM (SELECT LEVEL l FROM DUAL CONNECT BY LEVEL <= 3000) a, "
                             + "(SELECT LEVEL l FROM DUAL CONNECT BY LEVEL <= 3000) b, (SELECT LEVEL l FROM DUAL CONNECT BY LEVEL <= 3000) c";
        using (var cts = new CancellationTokenSource())
        {
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var unused in service.StreamRowsAsync(s, Query(heavy), NoBinds, ExecutionOptions.Default, cts.Token))
                {
                }
            }).WaitAsync(TimeSpan.FromSeconds(90));
            Log($"step7 cancel while the server computes: cancelled after ms={N(sw.ElapsedMilliseconds)}");
        }

        await AssertSessionUsableAsync(service, s);

        // (b) 행을 읽는 도중 취소.
        using (var cts = new CancellationTokenSource())
        {
            var count = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var unused in service.StreamRowsAsync(s, Query("SELECT LEVEL FROM DUAL CONNECT BY LEVEL <= 50000000"), NoBinds,
                                   ExecutionOptions.Default, cts.Token))
                {
                    if (++count == 2000)
                    {
                        await cts.CancelAsync();
                    }
                }
            }).WaitAsync(TimeSpan.FromSeconds(90));
            Log($"step7 cancel while rows stream: rows read before the exception={N(count)}");
            Assert.True(count < 50_000_000, "the read stopped early");
        }

        await AssertSessionUsableAsync(service, s);
    }

    // =================================================================================================================
    // 8. 읽기 전용 프로필
    // =================================================================================================================

    /// <summary>읽기 전용 프로필: <see cref="TiberoExecutionService.ReadOnlyBlock"/> 은 Succeeded, INSERT 는 서버로 보내지 않고 Denied, 읽기 전용 트랜잭션 안의 SELECT 는 된다.</summary>
    [Fact]
    public async Task Step8_read_only_profile_accepts_the_read_only_block_and_denies_an_insert()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var ro = await it.OpenAsync(profile: it.Profile() with { ReadOnly = true });
        var table = TiberoItSessions.ObjectName("TB1_DST");

        var block = await ControlAsync(service, ro, TiberoExecutionService.ReadOnlyBlock);
        Log("step8 read-only block " + Show(block));
        AssertOk(block, "read-only transaction block");

        var denied = await RunAsync(service, ro, InsertSql(table, IdText), BulkCols(IdText), [new object?[] { Num(1), "x" }]);
        Log("step8 insert " + Show(denied));
        Assert.Equal(ExecutionStatus.Denied, denied.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, denied.Policy?.Rule);

        var rows = await StreamAllAsync(service, ro, Query("SELECT 1 FROM DUAL"));
        Assert.Equal("1", Assert.IsType<TiberoBulkNumber>(Assert.Single(rows)[0]).Text);

        AssertOk(await ControlAsync(service, ro, "ROLLBACK"), "rollback");
    }

    // =================================================================================================================
    // (a) CHAR(3) → CHAR(5) MERGE 의 끝 공백 비교 (측정)
    // =================================================================================================================

    /// <summary>
    /// 메모 확인 (a), 측정만: CHAR(3) 키(끝 공백 채움)를 읽어 CHAR(5) 키 표에 MERGE 한다. 키는 Varchar2 배열로 바인드되므로 <c>T.K = S.K</c> 가 끝 공백 채움 비교인지
    /// (일치해 UPDATE 되는지, 안 맞아 새 행 INSERT 가 PK 중복이 되는지) 본다. 끝 공백을 자른 키와 직접 바인드 비교도 같이 찍는다.
    /// </summary>
    [Fact]
    public async Task Step_a_measure_char3_keys_merged_into_char5_target()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var t3 = TiberoItSessions.ObjectName("TB1_K3");
        var t5 = TiberoItSessions.ObjectName("TB1_K5");
        await CleanupAsync([s], t3, t5);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t3} (K CHAR(3) PRIMARY KEY, V NUMBER)");
            await ExecAsync(s, $"CREATE TABLE {t5} (K CHAR(5) PRIMARY KEY, V NUMBER)");
            await ExecAsync(s, $"INSERT INTO {t3} VALUES ('a', 1)");
            await ExecAsync(s, $"INSERT INTO {t3} VALUES ('ab', 2)");
            await ExecAsync(s, $"INSERT INTO {t3} VALUES ('abc', 3)");
            await ExecAsync(s, $"INSERT INTO {t5} VALUES ('a', 0)");
            await ExecAsync(s, $"INSERT INTO {t5} VALUES ('ab', 0)");
            await ExecAsync(s, $"INSERT INTO {t5} VALUES ('abc', 0)");
            await ExecAsync(s, "COMMIT");

            var source = await StreamAllAsync(service, s, Query($"SELECT K, V FROM {t3} ORDER BY V"));
            Assert.Equal(["a  ", "ab ", "abc"], source.Select(r => (string?)r[0]));
            Log("step_a target keys " + await QueryTextAsync(s, $"SELECT K, V FROM {t5} ORDER BY V, K", 10));

            // 직접 바인드 비교: Varchar2 / Char 매개변수로 CHAR(5) 열을 찾는다.
            foreach (var probe in new[] { "ab", "ab ", "ab   " })
            {
                foreach (var kind in new[] { TiberoParamKind.Varchar2, TiberoParamKind.Char })
                {
                    var hit = await TryScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {t5} WHERE K = :P", cmd => AddParam(s, cmd, "P", kind, probe));
                    Log($"step_a measure where K = :P kind={kind} length={N(probe.Length)} count={hit}");
                }
            }

            Col[] spec = [new("K", "CHAR(3)"), new("V", "NUMBER")];
            var merge = MergeSql(t5, spec, 1);
            foreach (var (label, trim) in new[] { ("padded to 3", false), ("trimmed", true) })
            {
                var rows = source.Select(r => new object?[] { trim ? ((string)r[0]!).TrimEnd() : r[0], r[1] }).ToList();
                await SilentAsync(s, "ROLLBACK");
                var result = await RunAsync(service, s, merge, BulkCols(spec), rows);
                Log($"step_a measure merge keys {label}: {Show(result)}");
                Log($"step_a measure merge keys {label}: target now " + await QueryTextAsync(s, $"SELECT K, V FROM {t5} ORDER BY V, K", 10));
                await SilentAsync(s, "ROLLBACK");
            }
        }
        finally
        {
            await CleanupAsync([s], t3, t5);
        }
    }

    // =================================================================================================================
    // 9. 측정만: 꺼진 형식의 후보 바인드 글
    // =================================================================================================================

    /// <summary>
    /// 단정 없음. 꺼진 형식(BINARY_FLOAT/DOUBLE·TZ·LTZ·INTERVAL·LONG·LONG RAW)의 후보 감싸기 글과 TIMESTAMP FF9/FF7 을 배열·스칼라 바인드로 보내 결과와 되읽은 값을
    /// <c>TB1 measure &lt;형식&gt; …</c> 줄로 남긴다. 어떤 실패도 시험을 실패시키지 않는다.
    /// </summary>
    [Fact]
    public async Task Step9_measure_candidate_bind_texts_for_disabled_types()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var s = await it.OpenAsync();
        var table = TiberoItSessions.ObjectName("TB1_MEAS");
        await CleanupAsync([s], table);
        try
        {
            const string stampRead = "TO_CHAR(X, 'YYYY-MM-DD HH24:MI:SS.FF9')";
            string[] nine = ["2026-01-02 03:04:05.123456789", "1999-12-31 23:59:59.999999999", "2000-02-29 00:00:00.000000001"];
            string[] seven = ["2026-01-02 03:04:05.1234567", "1999-12-31 23:59:59.9999999", "2000-02-29 00:00:00.0000001"];
            var cases = new List<MeasureCase>
            {
                new("BINARY_FLOAT TO_BINARY_DOUBLE (shape candidate)", "BINARY_FLOAT", Candidate("BINARY_FLOAT"), TiberoParamKind.Varchar2,
                    ["1.5", "-2.25", "3.4028234E38"], ["1.5", "NaN", "Infinity", "-Infinity"], "TO_CHAR(X)"),
                new("BINARY_FLOAT TO_BINARY_FLOAT", "BINARY_FLOAT", "TO_BINARY_FLOAT(:C1)", TiberoParamKind.Varchar2,
                    ["1.5", "-2.25", "3.4028234E38"], ["1.5", "NaN"], "TO_CHAR(X)"),
                new("BINARY_DOUBLE TO_BINARY_DOUBLE (shape candidate)", "BINARY_DOUBLE", Candidate("BINARY_DOUBLE"), TiberoParamKind.Varchar2,
                    ["1.5", "-2.25", "1.7976931348623157E308"], ["1.5", "NaN", "Infinity", "-Infinity", "1.7976931348623157E308"], "TO_CHAR(X)"),
                new("TIMESTAMP WITH TIME ZONE TO_TIMESTAMP_TZ (shape candidate)", "TIMESTAMP(9) WITH TIME ZONE", Candidate("TIMESTAMP WITH TIME ZONE"),
                    TiberoParamKind.Varchar2,
                    ["2026-01-02 03:04:05.123456789 +09:00", "2026-06-07 08:09:10.000000001 -05:30", "1999-12-31 23:59:59.999999999 +00:00"],
                    ["2026-01-02 03:04:05.123456789 +09:00"], "TO_CHAR(X, 'YYYY-MM-DD HH24:MI:SS.FF9 TZH:TZM')"),
                new("TIMESTAMP WITH LOCAL TIME ZONE FROM_TZ (shape candidate)", "TIMESTAMP(9) WITH LOCAL TIME ZONE", Candidate("TIMESTAMP WITH LOCAL TIME ZONE"),
                    TiberoParamKind.Varchar2, nine, [nine[0]], stampRead),
                new("INTERVAL YEAR TO MONTH TO_YMINTERVAL (shape candidate)", "INTERVAL YEAR(9) TO MONTH", Candidate("INTERVAL YEAR TO MONTH"),
                    TiberoParamKind.Varchar2, ["+01-02", "-10-11", "+00-00"], ["+01-02", "1-2"], "TO_CHAR(X)"),
                new("INTERVAL DAY TO SECOND TO_DSINTERVAL (shape candidate)", "INTERVAL DAY(9) TO SECOND(9)", Candidate("INTERVAL DAY TO SECOND"),
                    TiberoParamKind.Varchar2, ["+5 04:03:02.123456789", "-1 00:00:01.000000001", "+0 00:00:00.000000000"],
                    ["+5 04:03:02.123456789", "5 04:03:02"], "TO_CHAR(X)"),
                new("LONG :C1 kind=Long", "LONG", ":C1", TiberoParamKind.Long, ["long one", "long two", "long three"], ["long one"], null),
                new("LONG RAW :C1 kind=LongRaw scalar", "LONG RAW", ":C1", TiberoParamKind.LongRaw, null, [new byte[] { 1, 2, 3, 0xFF }], null),
                new("TIMESTAMP(9) FF9 array", "TIMESTAMP(9)", "TO_TIMESTAMP(:C1, 'YYYY-MM-DD HH24:MI:SS.FF9')", TiberoParamKind.Varchar2, nine, [nine[0]], stampRead),
                new("TIMESTAMP(9) FF7 format with 9-digit text array", "TIMESTAMP(9)", "TO_TIMESTAMP(:C1, 'YYYY-MM-DD HH24:MI:SS.FF7')", TiberoParamKind.Varchar2, nine,
                    [nine[0]], stampRead),
                new("TIMESTAMP(9) FF7 array", "TIMESTAMP(9)", "TO_TIMESTAMP(:C1, 'YYYY-MM-DD HH24:MI:SS.FF7')", TiberoParamKind.Varchar2, seven, [seven[0]], stampRead),
            };
            foreach (var c in cases)
            {
                await MeasureCaseAsync(service, s, table, c);
            }
        }
        finally
        {
            await CleanupAsync([s], table);
        }
    }

    // =================================================================================================================
    // 10. 이분(bisect) 측정: 배열 INSERT 가 연결을 끊는 원인 찾기 · BLOB 명령 재사용 (측정만 한다 — 단정 없음)
    // =================================================================================================================

    private static readonly Col IdCol = new("ID", "NUMBER(10)");

    private sealed record BisectCase(string Name, string ColumnType, Func<int, object?> Value);

    private static string ClobText(int length) => BigClob[..length];

    private static TiberoBulkNumber? GenN10(int i) => i % 11 == 0 ? null : TiberoBulkNumber.FromDecimal(decimal.Round(i * 1234567.0123456789m / 7m, 10));

    private static TiberoBulkNumber? GenNum(int i) => i % 13 == 0 ? null : TiberoBulkNumber.FromDecimal(i / 3m * (i % 2 == 0 ? -1 : 1));

    private static object? GenVc(int i) => i % 9 == 0 ? null : "v" + N(i) + " abc";

    private static object? GenCh5(int i) => i % 10 == 0 ? null : "abcde"[..(i % 5 + 1)];

    private static string? GenNvc(int i, bool national) => i % 4 == 0 ? null : national ? NationalText(i) : "ascii-" + N(i);

    /// <summary>8의 배수 행은 NULL, 1·25번 행만 <paramref name="bigLength"/> 자, 나머지는 짧은 글(Step1 의 모양).</summary>
    private static string? GenClob(int i, int bigLength) => i % 8 == 0 ? null : i is 1 or 25 ? ClobText(bigLength) : "clob " + N(i);

    private static DateTime? GenDt(int i)
    {
        if (i == 1)
        {
            return new DateTime(1, 1, 1, 0, 0, 0);
        }

        if (i == 2)
        {
            return new DateTime(9999, 12, 31, 23, 59, 59);
        }

        return i % 12 == 0 ? null : new DateTime(2000, 1, 1).AddSeconds(i * 3661L);
    }

    private static TiberoBulkTimestamp? GenTs(int i) =>
        i % 14 == 0 ? null : new TiberoBulkTimestamp(new DateTime(2026, 1, 2, 3, 4, i % 60), (int)(i * 3_333_331L % 1_000_000_000L));

    private static byte[]? GenRw(int i) => i % 6 == 0 ? null : Pattern(i % 25 == 0 ? 2000 : 2 * (i % 40 + 1));

    private static object?[] MixedRow(int i, bool withClob, int bigLength)
    {
        var row = new List<object?> { Num(i), GenN10(i), GenNum(i), GenVc(i), GenCh5(i), GenNvc(i, true) };
        if (withClob)
        {
            row.Add(GenClob(i, bigLength));
        }

        row.Add(GenDt(i));
        row.Add(GenTs(i));
        row.Add(GenRw(i));
        return [.. row];
    }

    /// <summary>
    /// 표 하나(첫 열 = PK)를 새 세션으로 만들고 <paramref name="count"/> 행을 ExecuteArrayAsync 한 번으로 넣어 결과 한 줄만 찍는다(던지지 않는다).
    /// 연결이 끊겨도 다음 사례에 번지지 않게 사례마다 새 세션을 열고, 표는 또 다른 새 세션으로 지운다.
    /// </summary>
    private async Task BisectAsync(TiberoItSessions it, string name, IReadOnlyList<Col> cols, Func<int, object?[]> rowOf, int count = Batch)
    {
        var table = NextBisectTable();
        string outcome;
        TiberoSession? s = null;
        try
        {
            var service = NewService(it);
            s = await it.OpenAsync();
            await DropBisectLeftoversAsync(s);
            await DropAsync(s, table);
            var ddl = $"CREATE TABLE {table} ({string.Join(", ", cols.Select((c, i) => c.Name + " " + c.ServerType + (i == 0 ? " PRIMARY KEY" : string.Empty)))})";
            await ExecAsync(s, ddl);
            var rows = new List<object?[]>(count);
            for (var i = 1; i <= count; i++)
            {
                rows.Add(rowOf(i));
            }

            var sw = Stopwatch.StartNew();
            var result = await RunAsync(service, s, InsertSql(table, cols), BulkCols(cols), rows);
            outcome = Show(result) + " ms=" + N(sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            outcome = "EXCEPTION " + Describe(ex);
        }
        finally
        {
            await ReleaseAsync(s);
            await DropFreshAsync(it, table);
        }

        Log($"bisect {name} {outcome}");
    }

    /// <summary>사례마다 다른 표 이름(ORT_TB1_BIS1, 2, …) — 앞 사례의 정리가 실패해도 다음 사례가 같은 이름에 막히지 않는다.</summary>
    private string NextBisectTable() => TiberoItSessions.ObjectName(BisectPrefix + N(Interlocked.Increment(ref _bisectSeq)));

    private const string BisectPrefix = "TB1_BIS";

    private int _bisectSeq;

    /// <summary>
    /// 사례 세션의 트랜잭션을 되돌리고 닫는다. 넣은 행이 미커밋으로 남으면 그 표 잠금 때문에 다른 세션의 DROP 이 조용히 실패한다(win144 #78:
    /// 다음 사례 CREATE 가 TBR-7102). 끊긴 세션이면 ROLLBACK 은 실패하고 서버가 세션 끝에 되돌린다.
    /// </summary>
    private static async Task ReleaseAsync(TiberoSession? s)
    {
        if (s is null)
        {
            return;
        }

        await SilentAsync(s, "ROLLBACK");
        await s.DisposeAsync();
    }

    /// <summary>앞 실행이 남긴 ORT_TB1_BIS* 표를 지운다(실패는 무시).</summary>
    private static async Task DropBisectLeftoversAsync(TiberoSession s)
    {
        var prefix = TiberoItSessions.ObjectName(BisectPrefix);
        var names = new List<string>();
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand($"SELECT TABLE_NAME FROM USER_TABLES WHERE TABLE_NAME LIKE '{prefix}%'");
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }
        catch (DbException)
        {
            return;
        }
        finally
        {
            s.CommandGate.Release();
        }

        foreach (var name in names)
        {
            await DropAsync(s, name);
        }
    }

    /// <summary>
    /// 끊긴 세션이 DROP 을 막지 않게 새 세션으로 표를 지운다(서버가 끊긴 세션의 트랜잭션을 되돌린다). 지운 뒤 남았는지 확인해 남았으면 찍는다.
    /// </summary>
    private async Task DropFreshAsync(TiberoItSessions it, string table)
    {
        TiberoSession? cleanup = null;
        try
        {
            cleanup = await it.OpenAsync();
            await CleanupAsync([cleanup], table);
            var left = await ScalarAsync(cleanup, $"SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = '{table}'");
            if (left is not "0")
            {
                Log($"bisect cleanup {table} still exists (count={left ?? "null"})");
            }
        }
        catch (Exception ex)
        {
            Log($"bisect cleanup {table} failed {Describe(ex)}");
        }
        finally
        {
            await ReleaseAsync(cleanup);
        }
    }

    /// <summary>(a) 지원 형식 하나씩 단독 표(ID + 그 열)에 50행을 배열 한 번으로 — 어느 형식이 연결을 끊는지.</summary>
    [Fact]
    public async Task Step1c_bisect_array_insert_by_type()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        BisectCase[] cases =
        [
            new("number38_10", "NUMBER(38,10)", GenN10),
            new("number", "NUMBER", GenNum),
            new("varchar2_100", "VARCHAR2(100)", GenVc),
            new("char5", "CHAR(5)", GenCh5),
            new("nvarchar2_50_national", "NVARCHAR2(50)", i => GenNvc(i, true)),
            new("clob_two_of_50_are_100000", "CLOB", i => GenClob(i, 100_000)),
            new("date", "DATE", i => GenDt(i)),
            new("timestamp9", "TIMESTAMP(9)", GenTs),
            new("raw2000", "RAW(2000)", GenRw),
        ];
        foreach (var c in cases)
        {
            await BisectAsync(it, "type-" + c.Name, [IdCol, new Col("X", c.ColumnType)], i => [Num(i), c.Value(i)]);
        }
    }

    /// <summary>(b) CLOB 한 열 50행: 모든 행이 같은 길이 L 일 때 + Step1 처럼 2행만 100000자이고 나머지는 짧을 때.</summary>
    [Fact]
    public async Task Step1d_bisect_clob_array_sizes()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        Col[] cols = [IdCol, new("X", "CLOB")];
        foreach (var length in new[] { 1000, 4000, 4001, 8000, 32767, 32768, 65536, 100_000 })
        {
            var text = ClobText(length);
            await BisectAsync(it, "clob-all-rows-" + N(length) + "-chars", cols, i => [Num(i), text]);
        }

        await BisectAsync(it, "clob-only-rows-1-and-25-are-100000-rest-short", cols, i => [Num(i), i is 1 or 25 ? ClobText(100_000) : "clob " + N(i)]);
        await BisectAsync(it, "clob-step1-shape-with-nulls", cols, i => [Num(i), GenClob(i, 100_000)]);
    }

    /// <summary>(c) NVARCHAR2 한 열: CP949 밖 글자 vs ASCII 만.</summary>
    [Fact]
    public async Task Step1e_bisect_nvarchar2_national_vs_ascii()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        Col[] cols = [IdCol, new("X", "NVARCHAR2(50)")];
        await BisectAsync(it, "nvarchar2-national-outside-cp949", cols, i => [Num(i), GenNvc(i, true)]);
        await BisectAsync(it, "nvarchar2-ascii-only", cols, i => [Num(i), GenNvc(i, false)]);
    }

    /// <summary>(d) Step1 의 전체 열 묶음에서 CLOB 을 뺀 것 / CLOB 을 4000자로 줄인 것.</summary>
    [Fact]
    public async Task Step1f_bisect_mixed_column_set_without_clob_and_with_capped_clob()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var noClob = RoundTripCols.Where(c => c.Name != "CL").ToList();
        await BisectAsync(it, "mixed-without-clob", noClob, i => MixedRow(i, false, 0));
        await BisectAsync(it, "mixed-clob-capped-4000", RoundTripCols, i => MixedRow(i, true, 4000));
    }

    /// <summary>
    /// (g) Step1 의 실제 값으로 이분(win144 #81: 형식 하나·CLOB 크기 하나로는 끊김이 재현 안 됨). 원본 표를 Step1 과 같게 채워 열거로 다 읽은 뒤(열거 닫힘)
    /// 첫 묶음 50행을 — 모든 열 / 열 하나씩 뺌 / ID + 열 하나 / 앞 25행·뒤 25행 — 로 사례마다 새 표·새 세션에 넣고, 마지막으로 원본 열거가 열린 채 넣는다(Step1 모양).
    /// </summary>
    [Fact]
    public async Task Step1g_bisect_step1_rows_by_column_and_with_the_source_stream_open()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var service = NewService(it);
        var src = await it.OpenAsync();
        var tSrc = TiberoItSessions.ObjectName("TB1_BSRC");
        await CleanupAsync([src], tSrc);
        try
        {
            await ExecAsync(src, RoundTripDdl(tSrc));
            await ExecAsync(src, RoundTripSeed(tSrc));
            await UpdateNationalAndBigClobAsync(service, src, tSrc);
            await ExecAsync(src, "COMMIT");
            var select = Query($"SELECT {string.Join(", ", RoundTripCols.Select(c => c.Name))} FROM {tSrc} ORDER BY ID");
            var first = (await StreamAllAsync(service, src, select)).Take(Batch).ToList();
            Log("bisect step1g row1 " + string.Join(" | ", first[0].Select(Fmt)));

            await BisectAsync(it, "step1g-all-columns", RoundTripCols, i => first[i - 1]);
            await BisectAsync(it, "step1g-rows-1-25", RoundTripCols, i => first[i - 1], 25);
            await BisectAsync(it, "step1g-rows-26-50", RoundTripCols, i => first[i + 24], 25);
            for (var c = 1; c < RoundTripCols.Length; c++)
            {
                var without = Enumerable.Range(0, RoundTripCols.Length).Where(k => k != c).ToArray();
                await BisectAsync(it, "step1g-without-" + RoundTripCols[c].Name, [.. without.Select(k => RoundTripCols[k])],
                    i => [.. without.Select(k => first[i - 1][k])]);
                await BisectAsync(it, "step1g-only-" + RoundTripCols[c].Name, [RoundTripCols[0], RoundTripCols[c]], i => [first[i - 1][0], first[i - 1][c]]);
            }

            await StreamOpenCaseAsync(it, service, src, select);
        }
        finally
        {
            await CleanupAsync([src], tSrc);
        }
    }

    /// <summary>원본 열거가 열린 채(원본 세션 잠금·리더 열림) 다른 새 세션에 첫 묶음을 넣는다 — Step1 과 같은 순서.</summary>
    private async Task StreamOpenCaseAsync(TiberoItSessions it, TiberoBulkExecutionService service, TiberoSession src, SqlStatement select)
    {
        var table = NextBisectTable();
        string outcome = "no rows";
        TiberoSession? dst = null;
        try
        {
            dst = await it.OpenAsync();
            await DropBisectLeftoversAsync(dst);
            await ExecAsync(dst, RoundTripDdl(table));
            var batch = new List<object?[]>(Batch);
            await foreach (var row in service.StreamRowsAsync(src, select, NoBinds, ExecutionOptions.Default, CancellationToken.None))
            {
                batch.Add((object?[])row.Clone());
                if (batch.Count == Batch)
                {
                    var sw = Stopwatch.StartNew();
                    var result = await RunAsync(service, dst, InsertSql(table, RoundTripCols), BulkCols(RoundTripCols), batch);
                    outcome = Show(result) + " ms=" + N(sw.ElapsedMilliseconds);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            outcome = "EXCEPTION " + Describe(ex);
        }
        finally
        {
            await ReleaseAsync(dst);
            await DropFreshAsync(it, table);
        }

        Log($"bisect step1g-stream-open {outcome}");
    }

    private static DbCommand NewBlobCommand(TiberoSession s, string sql, int id, byte[] data)
    {
        var cmd = s.CreateCommand(sql);
        AddParam(s, cmd, "C1", TiberoParamKind.Decimal, N(id));
        AddParam(s, cmd, "C2", TiberoParamKind.Blob, data);
        return cmd;
    }

    /// <summary>
    /// (e) 드라이버 직접: BLOB 3행(가운데 PK 중복)을 명령 하나 재사용 vs 행마다 새 명령으로 실행해 행마다 오류 번호를 찍는다(1 MB 뒤 TBR-590727 이 재사용 때문인지).
    /// 사례마다 새 세션.
    /// </summary>
    [Fact]
    public async Task Step3b_blob_reuse_measure()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        foreach (var size in new[] { 3000, 65536, 1024 * 1024 })
        {
            foreach (var reuse in new[] { true, false })
            {
                var table = NextBisectTable();
                await BlobReuseCaseAsync(it, table, InsertSql(table, [IdCol, new Col("B", "BLOB")]), size, reuse);
            }
        }
    }

    private async Task BlobReuseCaseAsync(TiberoItSessions it, string table, string insert, int size, bool reuse)
    {
        var label = $"blob-{N(size)}B-{(reuse ? "reused-command" : "new-command-per-row")}";
        var lines = new List<string>();
        TiberoSession? s = null;
        try
        {
            s = await it.OpenAsync();
            await DropBisectLeftoversAsync(s);
            await DropAsync(s, table);
            await ExecAsync(s, $"CREATE TABLE {table} (ID NUMBER(10) PRIMARY KEY, B BLOB)");
            await ExecAsync(s, $"INSERT INTO {table} VALUES (2, NULL)");
            await ExecAsync(s, "COMMIT");
            var data = Pattern(size);
            await s.CommandGate.EnterAsync(CancellationToken.None);
            try
            {
                DbCommand? shared = null;
                try
                {
                    foreach (var id in new[] { 1, 2, 3 })
                    {
                        DbCommand cmd;
                        if (reuse && shared is not null)
                        {
                            cmd = shared;
                            cmd.Parameters[0].Value = N(id);
                            cmd.Parameters[1].Value = data;
                        }
                        else
                        {
                            cmd = NewBlobCommand(s, insert, id, data);
                            if (reuse)
                            {
                                shared = cmd;
                            }
                        }

                        try
                        {
                            var n = await cmd.ExecuteNonQueryAsync();
                            lines.Add($"row{N(id)}=OK(rows={N(n)})");
                        }
                        catch (Exception ex)
                        {
                            lines.Add($"row{N(id)}=ERR({Describe(ex)})");
                        }
                        finally
                        {
                            if (!reuse)
                            {
                                await TiberoExecutionService.SafeDisposeAsync(cmd);
                            }
                        }
                    }
                }
                finally
                {
                    await TiberoExecutionService.SafeDisposeAsync(shared);
                }
            }
            finally
            {
                s.CommandGate.Release();
            }
        }
        catch (Exception ex)
        {
            lines.Add("EXCEPTION " + Describe(ex));
        }
        finally
        {
            await ReleaseAsync(s);
            await DropFreshAsync(it, table);
        }

        Log($"bisect step3b-{label} {string.Join(" ; ", lines)}");
    }

    // =================================================================================================================
    // 도우미
    // =================================================================================================================

    private sealed record Col(string Name, string ServerType);

    /// <summary>측정 후보 한 줄. <paramref name="Values"/> = 배열 바인드 값(없으면 배열 시험 안 함), <paramref name="Scalars"/> = 스칼라 바인드 값들(문자열 또는 byte[]).</summary>
    private sealed record MeasureCase(string Label, string ColumnType, string Expr, TiberoParamKind Kind, string[]? Values, object[] Scalars, string? ReadbackExpr);

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }

    private static readonly string BigClob = string.Concat(Enumerable.Repeat("abc가나다", 16_667))[..100_000];

    private void Log(string text) => output.WriteLine("TB1 " + text);

    private static TiberoBulkExecutionService NewService(TiberoItSessions it) => new(new AllowAll(), new NoQuestions(), it.Events);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static TiberoBulkNumber Num(long value) => TiberoBulkNumber.FromInt64(value);

    private static SqlStatement Query(string sql, params string[] bindNames) => new(sql, default, StatementKind.Query) { BindNames = bindNames };

    private static SqlStatement Dml(string sql) => new(sql, default, StatementKind.Dml);

    private static string NationalText(int id) => "สวัสดี-" + N(id) + "-ẞ";

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static BulkColumn[] BulkCols(IReadOnlyList<Col> cols) =>
        [.. cols.Select((c, i) => new BulkColumn("C" + N(i + 1), c.ServerType))];

    private static string BindOf(Col col, int index) => TiberoBulkValueCodec.BindExpression("C" + N(index + 1), col.ServerType);

    /// <summary>복사 방언(TB3b) 대신 시험이 만드는 INSERT: 값 자리는 코덱의 감싸기 글.</summary>
    private static string InsertSql(string table, IReadOnlyList<Col> cols) =>
        $"INSERT INTO {table} ({string.Join(", ", cols.Select(c => "\"" + c.Name + "\""))}) VALUES ({string.Join(", ", cols.Select(BindOf))})";

    /// <summary>CopySqlBuilder.Merge 와 같은 모양: 앞 <paramref name="keyCount"/> 개가 키, 나머지가 있으면 WHEN MATCHED THEN UPDATE, 없으면 키만(INSERT 만).</summary>
    private static string MergeSql(string table, IReadOnlyList<Col> cols, int keyCount)
    {
        var usingList = string.Join(", ", cols.Select((c, i) => BindOf(c, i) + " AS \"" + c.Name + "\""));
        var on = string.Join(" AND ", cols.Take(keyCount).Select(c => "T.\"" + c.Name + "\" = S.\"" + c.Name + "\""));
        var sb = new StringBuilder();
        sb.Append("MERGE INTO ").Append(table).Append(" T USING (SELECT ").Append(usingList).Append(" FROM DUAL) S ON (").Append(on).Append(')');
        var updates = cols.Skip(keyCount).ToList();
        if (updates.Count > 0)
        {
            sb.Append(" WHEN MATCHED THEN UPDATE SET ")
                .Append(string.Join(", ", updates.Select(c => "T.\"" + c.Name + "\" = S.\"" + c.Name + "\"")));
        }

        sb.Append(" WHEN NOT MATCHED THEN INSERT (").Append(string.Join(", ", cols.Select(c => "\"" + c.Name + "\""))).Append(") VALUES (")
            .Append(string.Join(", ", cols.Select(c => "S.\"" + c.Name + "\""))).Append(')');
        return sb.ToString();
    }

    private static string Candidate(string serverType) => TiberoBulkTypes.ShapeOf(serverType).Wrap("C1");

    private static string RoundTripDdl(string table) =>
        $"CREATE TABLE {table} (ID NUMBER(10) PRIMARY KEY, N10 NUMBER(38,10), NUM NUMBER, VC VARCHAR2(100), CH5 CHAR(5), NVC NVARCHAR2(50), CL CLOB, DT DATE, TS TIMESTAMP(9), RW RAW(2000))";

    /// <summary>
    /// 원본 300행을 SQL 로만 채운다(서비스를 거치지 않아 왕복 비교가 독립적이다). NULL 은 열마다 다른 주기, DATE 는 0001-01-01·9999-12-31 23:59:59 끝값,
    /// TIMESTAMP(9) 는 행마다 다른 나노초, RAW 는 2~80B 이고 25의 배수 행은 2000B.
    /// </summary>
    private static string RoundTripSeed(string table) => $"""
        INSERT INTO {table} (ID, N10, NUM, VC, CH5, CL, DT, TS, RW)
        SELECT LEVEL,
               CASE WHEN MOD(LEVEL, 11) = 0 THEN NULL ELSE LEVEL * 1234567.0123456789 / 7 END,
               CASE WHEN MOD(LEVEL, 13) = 0 THEN NULL ELSE POWER(10, MOD(LEVEL, 40) - 20) * LEVEL / 3 * CASE WHEN MOD(LEVEL, 2) = 0 THEN -1 ELSE 1 END END,
               CASE WHEN MOD(LEVEL, 9) = 0 THEN NULL ELSE 'v' || TO_CHAR(LEVEL) || ' abc' END,
               CASE WHEN MOD(LEVEL, 10) = 0 THEN NULL ELSE SUBSTR('abcde', 1, MOD(LEVEL, 5) + 1) END,
               CASE WHEN MOD(LEVEL, 8) = 0 THEN NULL ELSE TO_CLOB('clob ' || TO_CHAR(LEVEL)) END,
               CASE WHEN LEVEL = 1 THEN TO_DATE('0001-01-01 00:00:00', 'YYYY-MM-DD HH24:MI:SS')
                    WHEN LEVEL = 2 THEN TO_DATE('9999-12-31 23:59:59', 'YYYY-MM-DD HH24:MI:SS')
                    WHEN MOD(LEVEL, 12) = 0 THEN NULL
                    ELSE TO_DATE('2000-01-01 00:00:00', 'YYYY-MM-DD HH24:MI:SS') + LEVEL * 3661 / 86400 END,
               CASE WHEN MOD(LEVEL, 14) = 0 THEN NULL
                    ELSE TO_TIMESTAMP('2026-01-02 03:04:' || LPAD(TO_CHAR(MOD(LEVEL, 60)), 2, '0') || '.' || LPAD(TO_CHAR(LEVEL * 3333331), 9, '0'),
                                      'YYYY-MM-DD HH24:MI:SS.FF9') END,
               CASE WHEN MOD(LEVEL, 6) = 0 THEN NULL
                    ELSE HEXTORAW(RPAD('0A', CASE WHEN MOD(LEVEL, 25) = 0 THEN 4000 ELSE 2 * (MOD(LEVEL, 40) + 1) END, 'FF')) END
        FROM DUAL CONNECT BY LEVEL <= {RowCount}
        """;

    /// <summary>CP949 밖 글자가 든 NVARCHAR2(4의 배수 행은 NULL)와 100k자 CLOB(1·150번 행)을 바인드로 넣는다(SQL 글자는 드라이버 문자셋을 타므로).</summary>
    private async Task UpdateNationalAndBigClobAsync(TiberoBulkExecutionService service, TiberoSession s, string table)
    {
        var national = Enumerable.Range(1, RowCount).Where(id => id % 4 != 0).Select(id => new object?[] { NationalText(id), Num(id) }).ToList();
        foreach (var chunk in national.Chunk(Batch))
        {
            var result = await RunAsync(service, s, $"UPDATE {table} SET \"NVC\" = :C1 WHERE \"ID\" = :C2", Cols("NVARCHAR2(50)", "NUMBER"), chunk);
            AssertOk(result, "update NVARCHAR2 batch");
        }

        foreach (var id in new[] { 1, 150 })
        {
            var result = await RunAsync(service, s, $"UPDATE {table} SET \"CL\" = :C1 WHERE \"ID\" = :C2", Cols("CLOB", "NUMBER"), [new object?[] { BigClob, Num(id) }]);
            AssertOk(result, "update CLOB 100000 chars row " + N(id));
        }

        Assert.Equal(N(national.Count), await ScalarAsync(s, $"SELECT TO_CHAR(COUNT(*)) FROM {table} WHERE NVC IS NOT NULL"));
        Log("step1 seeded NVARCHAR2 rows=" + N(national.Count) + " big CLOB rows=2");
    }

    private static BulkColumn[] Cols(params string[] serverTypes) =>
        [.. serverTypes.Select((type, i) => new BulkColumn("C" + N(i + 1), type))];

    private static Task<BulkResult> RunAsync(TiberoBulkExecutionService service, TiberoSession s, string sql, BulkColumn[] cols, IReadOnlyList<object?[]> rows) =>
        service.ExecuteArrayAsync(s, Dml(sql), cols, rows, ExecutionOptions.Default, CancellationToken.None);

    /// <summary>바인드 없는 문장 한 번(SAVEPOINT·ROLLBACK TO SAVEPOINT·COMMIT·ROLLBACK·읽기 전용 블록).</summary>
    private static Task<BulkResult> ControlAsync(TiberoBulkExecutionService service, TiberoSession s, string sql) =>
        service.ExecuteArrayAsync(s, new SqlStatement(sql, default, StatementKind.TransactionControl), [], [], ExecutionOptions.Default, CancellationToken.None);

    private static async Task<List<object?[]>> StreamAllAsync(TiberoBulkExecutionService service, TiberoSession s, SqlStatement query,
                                                              IReadOnlyDictionary<string, BindValue>? binds = null)
    {
        var rows = new List<object?[]>();
        await foreach (var row in service.StreamRowsAsync(s, query, binds ?? NoBinds, ExecutionOptions.Default, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task AssertSessionUsableAsync(TiberoBulkExecutionService service, TiberoSession s)
    {
        Assert.False(s.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, s.State);
        var rows = await StreamAllAsync(service, s, Query("SELECT 1 FROM DUAL"));
        Assert.Equal("1", Assert.IsType<TiberoBulkNumber>(Assert.Single(rows)[0]).Text);
    }

    private static void AssertOk(BulkResult result, string label, long expectedRows = -1)
    {
        Assert.True(result.Status == ExecutionStatus.Succeeded, label + ": " + Show(result));
        if (expectedRows >= 0)
        {
            Assert.True(result.RowsAffected == expectedRows, label + ": rows affected " + N(result.RowsAffected) + " but " + N(expectedRows) + " expected");
        }
    }

    private static string Show(BulkResult result) =>
        $"{result.Status} rows={N(result.RowsAffected)} code={result.Error?.Code ?? "-"} number={N(result.Error?.Number ?? 0)} kind={result.Error?.Kind} "
        + $"message={Cut(Flat(result.Error?.Message ?? string.Empty), 300)}";

    private static bool SameCell(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
        _ => Equals(a, b),
    };

    private static string Fmt(object? value) => value switch
    {
        null => "NULL",
        string text => "string(" + Cut(Flat(text), 40) + ")",
        byte[] bytes => "byte[" + N(bytes.Length) + "]",
        DateTime time => "DateTime(" + time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ")",
        TiberoBulkNumber number => "Number(" + Cut(number.Text, 60) + ")",
        _ => value!.GetType().Name + "(" + value + ")",
    };

    private static string Flat(string text) => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static string Describe(Exception ex)
    {
        var error = TiberoErrorClassifier.ToDbError(ex, null);
        var head = error is null
            ? ex.GetType().Name + " " + Flat(ex.Message)
            : "number=" + N(error.Number) + " " + Flat(error.Message);
        return Cut(head, 500);
    }

    // ---- 드라이버 직접(측정용) ---------------------------------------------------------------------------------------

    private static TiberoDriver Drv(TiberoSession s) =>
        s.Driver ?? throw new InvalidOperationException("이 시험은 실제 드라이버 세션이 필요하다.");

    private static void AddParam(TiberoSession s, DbCommand cmd, string name, TiberoParamKind kind, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        Drv(s).SetParameterKind(p, kind);
        p.Direction = ParameterDirection.Input;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static async Task<int> ExecAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            prepare?.Invoke(cmd);
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private static async Task<bool> SilentAsync(TiberoSession s, string sql)
    {
        try
        {
            await ExecAsync(s, sql);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>문장을 보내고 "OK rows=n ms=t" 또는 "ERR …" 를 돌려준다(던지지 않는다).</summary>
    private static async Task<string> TryTextAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var n = await ExecAsync(s, sql, prepare);
            return $"OK rows={N(n)} ms={N(sw.ElapsedMilliseconds)}";
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    /// <summary>한 값(첫 행 첫 열)을 글자로. NULL·행 없음 = null.</summary>
    private static async Task<string?> ScalarAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            prepare?.Invoke(cmd);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.IsDBNull(0))
            {
                return null;
            }

            return Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private static async Task<string> TryScalarAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        try
        {
            return await ScalarAsync(s, sql, prepare) ?? "(null)";
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    /// <summary>조회 결과를 "열, 열 | 열, 열" 글자로(최대 <paramref name="maxRows"/> 행). 열 이름은 앞에 한 번. 던지지 않는다.</summary>
    private static async Task<string> QueryTextAsync(TiberoSession s, string sql, int maxRows)
    {
        try
        {
            await s.CommandGate.EnterAsync(CancellationToken.None);
            try
            {
                await using var cmd = s.CreateCommand(sql);
                await using var reader = await cmd.ExecuteReaderAsync();
                var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName);
                var sb = new StringBuilder("cols=[").AppendJoin(',', names).Append(']');
                var count = 0;
                while (count < maxRows && await reader.ReadAsync())
                {
                    var values = new List<string>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        values.Add(reader.IsDBNull(i) ? "NULL" : Flat(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty));
                    }

                    sb.Append(" | ").AppendJoin(", ", values);
                    count++;
                }

                return Cut(sb.ToString(), 1500);
            }
            finally
            {
                s.CommandGate.Release();
            }
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static async Task DropAsync(TiberoSession s, string table)
    {
        await SilentAsync(s, "ROLLBACK");
        if (!await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS PURGE"))
        {
            await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS");
        }
    }

    /// <summary>모든 세션의 열린 트랜잭션을 되돌린 뒤(잠금이 DROP 을 막지 않게) 표를 지운다. 실패는 무시한다.</summary>
    private static async Task CleanupAsync(IReadOnlyList<TiberoSession> sessions, params string[] tables)
    {
        foreach (var session in sessions)
        {
            await SilentAsync(session, "ROLLBACK");
        }

        foreach (var table in tables)
        {
            await DropAsync(sessions[0], table);
        }
    }

    // ---- 9. 측정 실행 -------------------------------------------------------------------------------------------------

    private async Task MeasureCaseAsync(TiberoBulkExecutionService service, TiberoSession s, string table, MeasureCase c)
    {
        var label = "measure " + c.Label;
        try
        {
            await DropAsync(s, table);
            var created = await TryTextAsync(s, $"CREATE TABLE {table} (X {c.ColumnType})");
            Log($"{label} create column {c.ColumnType}: {created}");
            if (!created.StartsWith("OK", StringComparison.Ordinal))
            {
                return;
            }

            var insert = $"INSERT INTO {table} (X) VALUES ({c.Expr})";
            if (c.Values is { Length: > 0 } values)
            {
                var result = await TryTextAsync(s, insert, cmd =>
                {
                    Drv(s).SetArrayBindCount(cmd, values.Length);
                    AddParam(s, cmd, "C1", c.Kind, values);
                });
                Log($"{label} array of {N(values.Length)}: {result}");
                if (result.StartsWith("OK", StringComparison.Ordinal))
                {
                    await ReadbackAsync(service, s, table, c, label + " array");
                }

                await SilentAsync(s, "ROLLBACK");
            }

            foreach (var scalar in c.Scalars)
            {
                var shown = scalar is byte[] bytes ? "byte[" + N(bytes.Length) + "]" : "'" + scalar + "'";
                var result = await TryTextAsync(s, insert, cmd => AddParam(s, cmd, "C1", c.Kind, scalar));
                Log($"{label} scalar {shown}: {result}");
                if (result.StartsWith("OK", StringComparison.Ordinal))
                {
                    await ReadbackAsync(service, s, table, c, label + " scalar " + shown);
                }

                await SilentAsync(s, "ROLLBACK");
            }
        }
        catch (Exception ex)
        {
            Log($"{label} EXCEPTION {Describe(ex)}");
            await SilentAsync(s, "ROLLBACK");
        }
    }

    /// <summary>넣은 값을 SQL(TO_CHAR 등)과 StreamRowsAsync(어떤 .NET 값이 나오는지) 두 가지로 읽어 찍는다. 같은 세션이라 커밋 전 값이 보인다.</summary>
    private async Task ReadbackAsync(TiberoBulkExecutionService service, TiberoSession s, string table, MeasureCase c, string label)
    {
        if (c.ReadbackExpr is { } expr)
        {
            Log($"{label} sql readback: {await QueryTextAsync(s, $"SELECT {expr} FROM {table} ORDER BY 1", 5)}");
        }

        try
        {
            var rows = await StreamAllAsync(service, s, Query($"SELECT X FROM {table}"));
            Log($"{label} stream readback: {string.Join(" | ", rows.Select(r => Fmt(r[0])))}");
        }
        catch (Exception ex)
        {
            Log($"{label} stream readback ERR {Describe(ex)}");
        }
    }
}
