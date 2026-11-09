using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Integration;
using Xunit.Abstractions;

// 측정 시험: 서버가 거부하는 것도 사실이므로 실패는 던지지 않고 번호·메시지를 찍는다(준비 단계 실패만 시험을 실패시킨다).
#pragma warning disable CA1031

namespace Oranda.Tibero.Tests.Execution;

/// <summary>
/// Tibero 6.2.15 로 데이터 복사(Oracle 의 Transfer/Copy) 이식에 필요한 드라이버·서버 동작을 측정한다(TM0b). 결과는 시험 로그의
/// <c>MEASURE &lt;주제&gt;: &lt;값&gt;</c> 줄로 읽는다(설계 문서에 옮길 사실). 맞고 틀림을 단정하지 않는다 — 안 되는 것은 오류 번호·메시지를 찍는다.
/// <list type="bullet">
/// <item>쓰기 시험은 <c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돈다. 만드는 객체는 모두 <c>ORT_TBM_*</c> 이고 끝에 지운다(DROP … PURGE, 실패는 무시).</item>
/// <item>카탈로그 시험(<see cref="Measure_copy_catalog_views_and_columns"/>)은 읽기만 한다.</item>
/// <item>실행: <c>dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~TiberoBulkMeasureIt" --logger "console;verbosity=detailed"</c>
/// (서버·드라이버 환경 변수 <c>ORANDA_IT_TIBERO_*</c>, <c>ORANDA_TIBERO_DRIVER</c> 필요).</item>
/// <item>접속 정보는 어디에도 찍지 않는다. 커밋·롤백은 SQL 문장으로만 보낸다(트랜잭션 객체 메서드 금지).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoBulkMeasureIt(ITestOutputHelper output)
{
    // ---------------------------------------------------------------------------------------------------------------
    // 1. 배열 바인드
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>ArrayBindCount 로 NUMBER·VARCHAR2·DATE·CLOB(4000자 초과)·BLOB 을 배열로 넣는다: object[] 와 형식 배열, 전체와 열별.</summary>
    [Fact]
    public async Task Measure_array_bind_types_rows_affected_and_readback()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TBM_ARR");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, VC VARCHAR2(100), DT DATE, CL CLOB, BL BLOB)");
        try
        {
            var unit = "abc가나다";
            var clobs = new[]
            {
                "0:" + string.Concat(Enumerable.Repeat(unit, 1000)), // 6002자, 다바이트 문자 포함
                "1:" + new string('x', 4999),
                "2:" + new string('y', 70000),
            };
            var blobs = new[] { Pattern(100), Pattern(3000), Pattern(40000) };
            var cols = new[]
            {
                new ArrayCol("ID", TiberoParamKind.Decimal, new decimal[] { 1, 2, 3 }),
                new ArrayCol("VC", TiberoParamKind.Varchar2, new string?[] { "a", null, new string('v', 100) }),
                new ArrayCol("DT", TiberoParamKind.Date, new[] { new DateTime(2026, 1, 2, 3, 4, 5), new DateTime(1900, 1, 1), new DateTime(2026, 12, 31, 23, 59, 59) }),
                new ArrayCol("CL", TiberoParamKind.Clob, clobs),
                new ArrayCol("BL", TiberoParamKind.Blob, blobs),
            };
            var subsets = new (string Label, string[] Names)[]
            {
                ("all5", ["ID", "VC", "DT", "CL", "BL"]),
                ("number_varchar2", ["ID", "VC"]),
                ("number_date", ["ID", "DT"]),
                ("number_clob", ["ID", "CL"]),
                ("number_blob", ["ID", "BL"]),
            };

            foreach (var typed in new[] { false, true })
            {
                foreach (var (label, names) in subsets)
                {
                    await SilentAsync(s, "ROLLBACK");
                    var chosen = names.Select(n => cols.First(c => c.Name == n)).ToList();
                    var topic = $"array_bind.insert.{(typed ? "typed_arrays" : "object_arrays")}.{label}";
                    if (await TryInsertArrayAsync(s, t, topic, chosen, typed, 3))
                    {
                        Measure(topic + ".readback", (await QueryTableAsync(s, ReadbackSql(t))).Show(5));
                    }
                }
            }

            // 개수와 배열 길이가 어긋나면.
            var idVc = new[] { cols[0], cols[1] };
            await SilentAsync(s, "ROLLBACK");
            await TryInsertArrayAsync(s, t, "array_bind.count3_but_arrays_of_2", idVc, false, 3, take: 2);
            await SilentAsync(s, "ROLLBACK");
            await TryInsertArrayAsync(s, t, "array_bind.count2_but_arrays_of_3", idVc, false, 2, take: 3);
            Measure("array_bind.rows_after_mismatch_tests", (await QueryTableAsync(s, $"SELECT COUNT(*) AS N FROM {t}")).Show());

            // 배열 DML 이 끝난 뒤 트랜잭션은 열려 있는가(커밋 전에는 다른 세션에 안 보인다).
            await SilentAsync(s, "ROLLBACK");
            await TryInsertArrayAsync(s, t, "array_bind.insert.for_visibility_check", idVc, false, 3);
            var other = await it.OpenAsync(SessionPurpose.Monitor);
            Measure("array_bind.visible_to_other_session_before_commit", await ScalarAsync(other, $"SELECT COUNT(*) FROM {t}") ?? "(null)");
            await TryExecAsync(s, "array_bind.commit", "COMMIT");
            Measure("array_bind.visible_to_other_session_after_commit", await ScalarAsync(other, $"SELECT COUNT(*) FROM {t}") ?? "(null)");
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    /// <summary>배치 크기(1·100·1000·10000 행)와 LOB 배치의 시간·성공 여부. 복사 배치 크기를 정하는 근거.</summary>
    [Fact]
    public async Task Measure_array_bind_batch_sizes()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TBM_ARB");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, VC VARCHAR2(100), CL CLOB, BL BLOB)");
        try
        {
            foreach (var n in new[] { 1, 100, 1000, 10000 })
            {
                await SilentAsync(s, "ROLLBACK");
                var id = new ArrayCol("ID", TiberoParamKind.Decimal, Enumerable.Range(1, n).Select(i => (decimal)i).ToArray());
                var vc = new ArrayCol("VC", TiberoParamKind.Varchar2, Enumerable.Range(1, n).Select(i => "row " + i.ToString(CultureInfo.InvariantCulture)).ToArray());
                var ok = await TryInsertArrayAsync(s, t, $"array_bind.batch.number_varchar2.rows_{n}", [id, vc], true, n);
                if (!ok)
                {
                    break; // 이보다 큰 크기는 의미 없다.
                }
            }

            foreach (var n in new[] { 10, 100 })
            {
                await SilentAsync(s, "ROLLBACK");
                var id = new ArrayCol("ID", TiberoParamKind.Decimal, Enumerable.Range(1, n).Select(i => (decimal)i).ToArray());
                var cl = new ArrayCol("CL", TiberoParamKind.Clob, Enumerable.Range(1, n).Select(i => i.ToString(CultureInfo.InvariantCulture) + ":" + new string('c', 5000)).ToArray());
                var bl = new ArrayCol("BL", TiberoParamKind.Blob, Enumerable.Range(1, n).Select(_ => Pattern(3000)).ToArray());
                await TryInsertArrayAsync(s, t, $"array_bind.batch.number_clob5000.rows_{n}", [id, cl], true, n);
                await SilentAsync(s, "ROLLBACK");
                await TryInsertArrayAsync(s, t, $"array_bind.batch.number_blob3000.rows_{n}", [id, bl], true, n);
            }

            await SilentAsync(s, "ROLLBACK");
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    /// <summary>
    /// 배열 바인드 값 모양(#28 재측정): 열 하나씩, 매개변수 형식 × 값 배열 모양(형식 배열·object[]·글자 object[])마다 들어가는지와 되읽은 값.
    /// #28 에서 object[] 의 decimal 이 InvalidCast(Decimal→String), byte[][] BLOB 이 NullReferenceException 이었다 — 어느 모양이 되는지 가린다.
    /// </summary>
    [Fact]
    public async Task Measure_array_bind_value_shapes()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TBM_ARS");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (NU NUMBER, VC VARCHAR2(100), DT DATE, CL CLOB, BL BLOB, RW RAW(2000))");
        try
        {
            var big = "가" + new string('c', 5000);
            var shapes = new (string Label, string Column, TiberoParamKind Kind, Array Values, string Readback)[]
            {
                ("number.decimal_typed", "NU", TiberoParamKind.Decimal, new decimal[] { 1.5m, 2, 3 }, "TO_CHAR(SUM(NU))"),
                ("number.decimal_objects", "NU", TiberoParamKind.Decimal, new object[] { 1.5m, 2m, DBNull.Value }, "TO_CHAR(SUM(NU))"),
                ("number.string_objects_kind_decimal", "NU", TiberoParamKind.Decimal, new object[] { "1.5", "2", DBNull.Value }, "TO_CHAR(SUM(NU))"),
                ("number.string_typed_kind_decimal", "NU", TiberoParamKind.Decimal, new string?[] { "1.5", "2", null }, "TO_CHAR(SUM(NU))"),
                ("number.string_objects_kind_varchar2", "NU", TiberoParamKind.Varchar2, new object[] { "1.5", "2", DBNull.Value }, "TO_CHAR(SUM(NU))"),
                ("number.int64_objects", "NU", TiberoParamKind.Int64, new object[] { 1L, 2L, DBNull.Value }, "TO_CHAR(SUM(NU))"),
                ("number.int64_typed", "NU", TiberoParamKind.Int64, new long[] { 1, 2, 3 }, "TO_CHAR(SUM(NU))"),
                ("number.double_objects", "NU", TiberoParamKind.Double, new object[] { 1.5d, 2d, DBNull.Value }, "TO_CHAR(SUM(NU))"),
                ("varchar2.string_objects", "VC", TiberoParamKind.Varchar2, new object[] { "a", DBNull.Value, "한글" }, "COUNT(VC) || '/' || MAX(VC)"),
                ("varchar2.string_typed", "VC", TiberoParamKind.Varchar2, new string?[] { "a", null, "한글" }, "COUNT(VC) || '/' || MAX(VC)"),
                ("date.datetime_objects", "DT", TiberoParamKind.Date, new object[] { new DateTime(2026, 1, 2, 3, 4, 5), DBNull.Value, new DateTime(1900, 1, 1) }, "COUNT(DT) || '/' || TO_CHAR(MAX(DT), 'YYYY-MM-DD HH24:MI:SS')"),
                ("date.datetime_typed", "DT", TiberoParamKind.Date, new[] { new DateTime(2026, 1, 2, 3, 4, 5), new DateTime(1900, 1, 1), new DateTime(2000, 2, 29) }, "COUNT(DT) || '/' || TO_CHAR(MAX(DT), 'YYYY-MM-DD HH24:MI:SS')"),
                ("clob.string_objects", "CL", TiberoParamKind.Clob, new object[] { big, DBNull.Value, "x" }, "COUNT(CL) || '/' || MAX(LENGTH(CL))"),
                ("clob.string_typed", "CL", TiberoParamKind.Clob, new string?[] { big, null, "x" }, "COUNT(CL) || '/' || MAX(LENGTH(CL))"),
                ("blob.bytes_objects_kind_blob", "BL", TiberoParamKind.Blob, new object[] { Pattern(3000), DBNull.Value, Pattern(10) }, "COUNT(BL) || '/' || MAX(DBMS_LOB.GETLENGTH(BL))"),
                ("blob.bytes_typed_kind_blob_no_null", "BL", TiberoParamKind.Blob, new[] { Pattern(3000), Pattern(10), Pattern(1) }, "COUNT(BL) || '/' || MAX(DBMS_LOB.GETLENGTH(BL))"),
                ("blob.bytes_objects_kind_raw", "BL", TiberoParamKind.Raw, new object[] { Pattern(1000), DBNull.Value, Pattern(10) }, "COUNT(BL) || '/' || MAX(DBMS_LOB.GETLENGTH(BL))"),
                ("blob.bytes_typed_kind_longraw", "BL", TiberoParamKind.LongRaw, new[] { Pattern(3000), Pattern(10), Pattern(1) }, "COUNT(BL) || '/' || MAX(DBMS_LOB.GETLENGTH(BL))"),
                ("raw.bytes_objects", "RW", TiberoParamKind.Raw, new object[] { Pattern(100), DBNull.Value, Pattern(2000) }, "COUNT(RW) || '/' || MAX(LENGTH(RW))"),
                ("raw.bytes_typed", "RW", TiberoParamKind.Raw, new[] { Pattern(100), Pattern(1), Pattern(2000) }, "COUNT(RW) || '/' || MAX(LENGTH(RW))"),
            };

            foreach (var (label, column, kind, values, readback) in shapes)
            {
                await SilentAsync(s, "ROLLBACK");
                var topic = "array_bind.shape." + label;
                var ok = await TryExecAsync(s, topic, $"INSERT INTO {t} ({column}) VALUES (:V)", cmd =>
                {
                    Drv(s).SetArrayBindCount(cmd, values.Length);
                    AddParam(s, cmd, "V", kind, values);
                });
                if (ok)
                {
                    Measure(topic + ".readback", (await QueryTableAsync(s, $"SELECT COUNT(*) AS N, {readback} AS R FROM {t}")).Show(1));
                }
            }

            await SilentAsync(s, "ROLLBACK");
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    /// <summary>
    /// 배열 바인드 NULL·이진 대안(#36 보충): #36 에서 object[] 는 모두 InvalidCast, NULL 이 되는 건 string[]·CLOB string[] 뿐, BLOB·RAW 배열은 형식 배열도 실패였다.
    /// 여기서 가린다 — nullable 형식 배열(decimal?[]·long?[]·DateTime?[]), 글자 배열 + 변환 함수(TO_NUMBER·TO_DATE·TO_TIMESTAMP·HEXTORAW),
    /// BLOB·RAW 한 행씩 스칼라 바인드, 형식 배열로 개수 ≠ 배열 길이, 형식 배열 뒤 커밋 전 다른 세션 가시성.
    /// </summary>
    [Fact]
    public async Task Measure_array_bind_nulls_and_binary_alternatives()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TBM_ARN");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER, NU NUMBER, DT DATE, TS TIMESTAMP, BL BLOB, RW RAW(2000))");
        try
        {
            const string sum = "TO_CHAR(SUM(NU))";
            const string dates = "COUNT(DT) || '/' || TO_CHAR(MIN(DT), 'YYYY-MM-DD HH24:MI:SS') || '/' || TO_CHAR(MAX(DT), 'YYYY-MM-DD HH24:MI:SS')";
            const string stamps = "COUNT(TS) || '/' || TO_CHAR(MAX(TS), 'YYYY-MM-DD HH24:MI:SS.FF7')";
            // #42: RAWTOHEX(UTL_RAW.SUBSTR(RW, 1, 4)) 되읽기가 ODP-15115 → 길이만 본다(값은 아래 raw_head 로 따로).
            const string raws = "COUNT(RW) || '/' || MAX(LENGTH(RW))";
            var stamp = new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567);
            var shapes = new (string Label, string Column, string Expr, TiberoParamKind Kind, Array Values, string Readback)[]
            {
                ("number.decimal_nullable_typed", "NU", ":V", TiberoParamKind.Decimal, new decimal?[] { 1.5m, null, 2m }, sum),
                ("number.int64_nullable_typed", "NU", ":V", TiberoParamKind.Int64, new long?[] { 1, null, 2 }, sum),
                ("number.string_typed_kind_decimal_wide", "NU", ":V", TiberoParamKind.Decimal,
                    new string?[] { "12345678901234567890.123456789", null, "-0.000001" }, "TO_CHAR(MAX(NU)) || '/' || TO_CHAR(MIN(NU))"),
                ("number.string_typed_to_number", "NU", "TO_NUMBER(:V)", TiberoParamKind.Varchar2, new string?[] { "1.5", null, "2" }, sum),
                ("date.datetime_nullable_typed", "DT", ":V", TiberoParamKind.Date,
                    new DateTime?[] { new DateTime(2026, 1, 2, 3, 4, 5), null, new DateTime(1900, 1, 1) }, dates),
                ("date.string_typed_to_date", "DT", "TO_DATE(:V, 'YYYY-MM-DD HH24:MI:SS')", TiberoParamKind.Varchar2,
                    new string?[] { "2026-01-02 03:04:05", null, "1900-01-01 00:00:00" }, dates),
                ("timestamp.datetime_typed", "TS", ":V", TiberoParamKind.TimeStamp, new[] { stamp, stamp.AddDays(-1), stamp.AddDays(-2) }, stamps),
                ("timestamp.datetime_nullable_typed", "TS", ":V", TiberoParamKind.TimeStamp, new DateTime?[] { stamp, null, stamp.AddDays(-2) }, stamps),
                ("timestamp.string_typed_to_timestamp", "TS", "TO_TIMESTAMP(:V, 'YYYY-MM-DD HH24:MI:SS.FF7')", TiberoParamKind.Varchar2,
                    new string?[] { "2026-01-02 03:04:05.1234567", null, "1900-01-01 00:00:00.0000000" }, stamps),
                ("raw.hex_typed_hextoraw", "RW", "HEXTORAW(:V)", TiberoParamKind.Varchar2,
                    new string?[] { Convert.ToHexString(Pattern(100)), null, Convert.ToHexString(Pattern(2000)) }, raws),
            };

            foreach (var (label, column, expr, kind, values, readback) in shapes)
            {
                await SilentAsync(s, "ROLLBACK");
                var topic = "array_bind.null_shape." + label;
                var ok = await TryExecAsync(s, topic, $"INSERT INTO {t} ({column}) VALUES ({expr})", cmd =>
                {
                    Drv(s).SetArrayBindCount(cmd, values.Length);
                    AddParam(s, cmd, "V", kind, values);
                });
                if (ok)
                {
                    await ReadbackAsync(s, topic + ".readback", $"SELECT COUNT(*) AS N, {readback} AS R FROM {t}", 1);
                }
            }

            // BLOB·RAW 한 행씩(배열 아님): 스칼라 byte[] 와 NULL.
            var singles = new (string Label, string Column, TiberoParamKind Kind, object? Value)[]
            {
                ("blob.bytes_3000", "BL", TiberoParamKind.Blob, Pattern(3000)),
                ("blob.bytes_40000", "BL", TiberoParamKind.Blob, Pattern(40000)),
                ("blob.null", "BL", TiberoParamKind.Blob, null),
                ("blob.empty", "BL", TiberoParamKind.Blob, Array.Empty<byte>()),
                ("raw.bytes_2000", "RW", TiberoParamKind.Raw, Pattern(2000)),
                ("raw.null", "RW", TiberoParamKind.Raw, null),
            };
            foreach (var (label, column, kind, value) in singles)
            {
                await SilentAsync(s, "ROLLBACK");
                var topic = "single_bind." + label;
                var ok = await TryExecAsync(s, topic, $"INSERT INTO {t} ({column}) VALUES (:V)", cmd => AddParam(s, cmd, "V", kind, value));
                if (ok)
                {
                    var readback = column == "BL"
                        ? "COUNT(BL) || '/' || MAX(DBMS_LOB.GETLENGTH(BL)) || '/' || MIN(RAWTOHEX(DBMS_LOB.SUBSTR(BL, 4, 1)))"
                        : raws;
                    await ReadbackAsync(s, topic + ".readback", $"SELECT COUNT(*) AS N, {readback} AS R FROM {t}", 1);
                }
            }

            // 같은 준비문을 행마다 다시 실행(BLOB 열이 있는 표의 한 행씩 경로): 매개변수 값만 바꿔 3번.
            await SilentAsync(s, "ROLLBACK");
            await s.CommandGate.EnterAsync(CancellationToken.None);
            try
            {
                await using var cmd = s.CreateCommand($"INSERT INTO {t} (ID, BL) VALUES (:ID, :BL)");
                AddParam(s, cmd, "ID", TiberoParamKind.Decimal, 0m);
                AddParam(s, cmd, "BL", TiberoParamKind.Blob, null);
                var results = new List<string>();
                foreach (var (id, blob) in new (decimal Id, byte[]? Blob)[] { (1, Pattern(3000)), (2, null), (3, Pattern(10)) })
                {
                    cmd.Parameters[0].Value = id;
                    cmd.Parameters[1].Value = (object?)blob ?? DBNull.Value;
                    try
                    {
                        results.Add("rows=" + await cmd.ExecuteNonQueryAsync());
                    }
                    catch (Exception ex)
                    {
                        results.Add("ERR " + Describe(ex));
                    }
                }

                Measure("single_bind.reused_command", string.Join(" | ", results));
            }
            finally
            {
                s.CommandGate.Release();
            }

            await ReadbackAsync(s, "single_bind.reused_command.readback", $"SELECT ID, DBMS_LOB.GETLENGTH(BL) AS LEN FROM {t} WHERE ID IS NOT NULL ORDER BY ID", 5);

            // 형식 배열로 개수 ≠ 배열 길이.
            foreach (var (label, count, length) in new[] { ("count3_but_arrays_of_2", 3, 2), ("count2_but_arrays_of_3", 2, 3) })
            {
                await SilentAsync(s, "ROLLBACK");
                var ids = Enumerable.Range(1, length).Select(i => (decimal)i).ToArray();
                await TryExecAsync(s, "array_bind.typed." + label, $"INSERT INTO {t} (ID) VALUES (:V)", cmd =>
                {
                    Drv(s).SetArrayBindCount(cmd, count);
                    AddParam(s, cmd, "V", TiberoParamKind.Decimal, ids);
                });
                Measure("array_bind.typed." + label + ".ids", await IdsAsync(s, t));
            }

            // 형식 배열 DML 뒤 트랜잭션은 열려 있나(커밋 전 다른 세션에 안 보임).
            await SilentAsync(s, "ROLLBACK");
            await TryExecAsync(s, "array_bind.typed.for_visibility_check", $"INSERT INTO {t} (ID) VALUES (:V)", cmd =>
            {
                Drv(s).SetArrayBindCount(cmd, 3);
                AddParam(s, cmd, "V", TiberoParamKind.Decimal, new decimal[] { 1, 2, 3 });
            });
            var other = await it.OpenAsync(SessionPurpose.Monitor);
            Measure("array_bind.typed.visible_to_other_session_before_commit", await ScalarAsync(other, $"SELECT COUNT(*) FROM {t}") ?? "(null)");
            await TryExecAsync(s, "array_bind.typed.commit", "COMMIT");
            Measure("array_bind.typed.visible_to_other_session_after_commit", await ScalarAsync(other, $"SELECT COUNT(*) FROM {t}") ?? "(null)");
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    /// <summary>배열 한 건이 제약을 어기면: 예외 번호·메시지·배열 오류 목록, 몇 행이 들어갔나(부분 적용?), 트랜잭션이 살아 있나.</summary>
    [Fact]
    public async Task Measure_array_bind_constraint_violation_partial_rows_and_transaction_state()
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

        var s = await it.OpenAsync();
        var other = await it.OpenAsync(SessionPurpose.Monitor);
        var t = TiberoItSessions.ObjectName("TBM_ARE");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, VC VARCHAR2(10))");
        try
        {
            await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (201, 'seed')");
            await ExecAsync(s, "COMMIT");

            var scenarios = new (string Label, decimal[] Ids, string?[] Vcs)[]
            {
                ("pk_dup_in_middle", [200, 201, 202], ["a", "b", "c"]),
                ("pk_dup_at_last", [202, 203, 201], ["a", "b", "c"]),
                ("pk_dup_at_first", [201, 220, 221], ["a", "b", "c"]),
                ("value_too_large_in_middle", [210, 211, 212], ["a", "this value is far too long", "c"]),
                ("pk_dup_inside_batch", [230, 231, 230], ["a", "b", "c"]),
            };

            foreach (var typed in new[] { false, true })
            {
                foreach (var (label, ids, vcs) in scenarios)
                {
                    var topic = $"array_bind.violation.{(typed ? "typed_arrays" : "object_arrays")}.{label}";
                    await SilentAsync(s, "ROLLBACK");
                    await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (1, 'pre')"); // 배치 앞의 미커밋 작업
                    Measure(topic + ".batch", await TryBatchAsync(s, t, ids, vcs, typed));
                    Measure(topic + ".ids_after_failure", await IdsAsync(s, t));
                    Measure(topic + ".other_session_ids", await IdsAsync(other, t));
                    Measure(topic + ".next_statement", await TryTextAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (2, 'next')"));
                    Measure(topic + ".ids_after_next_statement", await IdsAsync(s, t));
                    await TryExecAsync(s, topic + ".rollback", "ROLLBACK");
                    Measure(topic + ".ids_after_rollback", await IdsAsync(s, t));
                }
            }
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2. 읽기 전용 트랜잭션
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>원본을 읽기 전용 트랜잭션으로 읽는다: PSM 블록으로 SET TRANSACTION READ ONLY → SELECT(다른 세션의 커밋이 안 보이나) → 쓰기 거부 → ROLLBACK.</summary>
    [Fact]
    public async Task Measure_read_only_transaction_in_psm_block()
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

        var s = await it.OpenAsync();
        var other = await it.OpenAsync(SessionPurpose.Monitor);
        var t = TiberoItSessions.ObjectName("TBM_RO");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER)");
        try
        {
            await ExecAsync(s, $"INSERT INTO {t} (ID) VALUES (1)");
            await ExecAsync(s, "COMMIT");

            await TryExecAsync(s, "read_only.top_level_set_transaction", "SET TRANSACTION READ ONLY");
            await SilentAsync(s, "ROLLBACK");
            await TryExecAsync(s, "read_only.execute_immediate", "BEGIN EXECUTE IMMEDIATE 'SET TRANSACTION READ ONLY'; END;");
            await SilentAsync(s, "ROLLBACK");

            // 미커밋 DML 이 있을 때 앞의 ROLLBACK 없이.
            await ExecAsync(s, $"INSERT INTO {t} (ID) VALUES (2)");
            await TryExecAsync(s, "read_only.block_without_rollback_while_dml_pending", "BEGIN SET TRANSACTION READ ONLY; END;");
            await SilentAsync(s, "ROLLBACK");

            // 복사가 쓸 형태.
            await TryExecAsync(s, "read_only.block_rollback_then_set_transaction", "BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;");
            Measure("read_only.count_first_read", await ScalarAsync(s, $"SELECT COUNT(*) FROM {t}") ?? "(null)");
            Measure("read_only.local_transaction_id_inside", await ScalarAsync(s, "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL") ?? "(null)");

            await ExecAsync(other, $"INSERT INTO {t} (ID) VALUES (3)");
            await ExecAsync(other, "COMMIT");
            Measure("read_only.count_after_other_session_commit", await ScalarAsync(s, $"SELECT COUNT(*) FROM {t}") ?? "(null)");
            Measure("read_only.count_seen_by_other_session", await ScalarAsync(other, $"SELECT COUNT(*) FROM {t}") ?? "(null)");

            await TryExecAsync(s, "read_only.insert_inside", $"INSERT INTO {t} (ID) VALUES (4)");
            await TryExecAsync(s, "read_only.ddl_inside", $"COMMENT ON TABLE {t} IS 'ro'");
            await TryExecAsync(s, "read_only.select_for_update_inside", $"BEGIN FOR r IN (SELECT ID FROM {t} FOR UPDATE) LOOP NULL; END LOOP; END;");
            Measure("read_only.count_after_failed_write", await ScalarAsync(s, $"SELECT COUNT(*) FROM {t}") ?? "(null)");

            await TryExecAsync(s, "read_only.rollback_ends_it", "ROLLBACK");
            Measure("read_only.count_after_rollback", await ScalarAsync(s, $"SELECT COUNT(*) FROM {t}") ?? "(null)");
            await TryExecAsync(s, "read_only.insert_after_rollback", $"INSERT INTO {t} (ID) VALUES (5)");
            await SilentAsync(s, "ROLLBACK");

            // COMMIT 으로도 끝나는가.
            await TryExecAsync(s, "read_only.block_again", "BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;");
            await TryExecAsync(s, "read_only.commit_ends_it", "COMMIT");
            await TryExecAsync(s, "read_only.insert_after_commit", $"INSERT INTO {t} (ID) VALUES (6)");
            await SilentAsync(s, "ROLLBACK");
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 3. 세이브포인트·암묵 커밋
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>SAVEPOINT → INSERT → ROLLBACK TO: 그 뒤 행만 사라지나, 실패한 배열 배치 뒤 되돌리기, 자동 커밋이 없나.</summary>
    [Fact]
    public async Task Measure_savepoint_rollback_to_and_failed_array_batch()
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

        var s = await it.OpenAsync();
        var other = await it.OpenAsync(SessionPurpose.Monitor);
        var t = TiberoItSessions.ObjectName("TBM_SVP");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, VC VARCHAR2(10))");
        try
        {
            await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (1, 'kept')");
            await TryExecAsync(s, "savepoint.create", "SAVEPOINT s1");
            await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (2, 'gone')");
            await TryExecAsync(s, "savepoint.rollback_to", "ROLLBACK TO s1");
            Measure("savepoint.ids_after_rollback_to", await IdsAsync(s, t));
            Measure("savepoint.other_session_ids_uncommitted", await IdsAsync(other, t));

            // 같은 세이브포인트를 되돌린 뒤 다시 쓸 수 있나(복사는 배치마다 같은 이름으로 건다).
            await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (3, 'gone')");
            await TryExecAsync(s, "savepoint.rollback_to_savepoint_syntax_reuse", "ROLLBACK TO SAVEPOINT s1");
            Measure("savepoint.ids_after_reuse", await IdsAsync(s, t));

            // 복사가 쓰는 이름·문장 모양.
            await TryExecAsync(s, "savepoint.copy_name_create", "SAVEPOINT ORANDA_COPY_BATCH");
            await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (4, 'gone')");
            await TryExecAsync(s, "savepoint.copy_name_rollback_to_savepoint", "ROLLBACK TO SAVEPOINT ORANDA_COPY_BATCH");
            Measure("savepoint.ids_after_copy_name", await IdsAsync(s, t));

            // 겹친 세이브포인트: 앞 것으로 되돌리면 뒤 것은 없어진다.
            await TryExecAsync(s, "savepoint.nested_a", "SAVEPOINT sa");
            await TryExecAsync(s, "savepoint.nested_b", "SAVEPOINT sb");
            await TryExecAsync(s, "savepoint.nested_rollback_to_a", "ROLLBACK TO sa");
            await TryExecAsync(s, "savepoint.nested_rollback_to_b_after_a", "ROLLBACK TO sb");

            // 실패한 배열 배치 → 세이브포인트로 되돌리고 한 행씩.
            foreach (var typed in new[] { false, true })
            {
                var mode = typed ? "typed_arrays" : "object_arrays";
                await TryExecAsync(s, $"savepoint.failed_batch.{mode}.savepoint", "SAVEPOINT ORANDA_COPY_BATCH");
                Measure($"savepoint.failed_batch.{mode}.batch", await TryBatchAsync(s, t, [10, 1, 11], ["a", "b", "c"], typed));
                Measure($"savepoint.failed_batch.{mode}.ids_before_rollback_to", await IdsAsync(s, t));
                await TryExecAsync(s, $"savepoint.failed_batch.{mode}.rollback_to", "ROLLBACK TO SAVEPOINT ORANDA_COPY_BATCH");
                Measure($"savepoint.failed_batch.{mode}.ids_after_rollback_to", await IdsAsync(s, t));
                await TryExecAsync(s, $"savepoint.failed_batch.{mode}.row_10", $"INSERT INTO {t} (ID, VC) VALUES (10, 'row')");
                await TryExecAsync(s, $"savepoint.failed_batch.{mode}.row_1_dup", $"INSERT INTO {t} (ID, VC) VALUES (1, 'dup')");
                await TryExecAsync(s, $"savepoint.failed_batch.{mode}.row_11", $"INSERT INTO {t} (ID, VC) VALUES (11, 'row')");
                Measure($"savepoint.failed_batch.{mode}.ids_after_row_by_row", await IdsAsync(s, t));
                await TryExecAsync(s, $"savepoint.failed_batch.{mode}.cleanup_rollback_to", "ROLLBACK TO SAVEPOINT ORANDA_COPY_BATCH");
                Measure($"savepoint.failed_batch.{mode}.ids_after_cleanup", await IdsAsync(s, t));
            }

            // 커밋 뒤에는 세이브포인트가 없다.
            await TryExecAsync(s, "savepoint.commit", "COMMIT");
            Measure("savepoint.other_session_ids_after_commit", await IdsAsync(other, t));
            await TryExecAsync(s, "savepoint.rollback_to_after_commit", "ROLLBACK TO SAVEPOINT ORANDA_COPY_BATCH");
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    /// <summary>DDL·그 밖 문장이 열려 있던 트랜잭션을 확정하나: INSERT(미커밋) → 문장 → 다른 세션에서 보이나. 실패한 DDL 도 확인한다.</summary>
    [Fact]
    public async Task Measure_implicit_commit_of_ddl_and_other_statements()
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

        var s = await it.OpenAsync();
        var other = await it.OpenAsync(SessionPurpose.Monitor);
        var t = TiberoItSessions.ObjectName("TBM_DDL");
        var ix = TiberoItSessions.ObjectName("TBM_DIX");
        var seq = TiberoItSessions.ObjectName("TBM_DSQ");
        await DropTableAsync(s, t);
        await SilentAsync(s, $"DROP SEQUENCE {seq}");
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER, VC VARCHAR2(10))");
        try
        {
            var steps = new (string Label, string Sql)[]
            {
                ("select", $"SELECT COUNT(*) FROM {t}"),
                ("savepoint", "SAVEPOINT sx"),
                ("alter_table_add_column", $"ALTER TABLE {t} ADD (X1 NUMBER)"),
                ("alter_table_add_column_fails_duplicate", $"ALTER TABLE {t} ADD (X1 NUMBER)"),
                ("create_index", $"CREATE INDEX {ix} ON {t} (ID)"),
                ("create_index_fails_duplicate", $"CREATE INDEX {ix} ON {t} (ID)"),
                ("comment_on_table", $"COMMENT ON TABLE {t} IS 'tbm'"),
                ("create_sequence", $"CREATE SEQUENCE {seq}"),
                ("alter_sequence", $"ALTER SEQUENCE {seq} INCREMENT BY 2"),
                ("alter_session_nls", "ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD HH24:MI:SS'"),
                ("lock_table", $"LOCK TABLE {t} IN EXCLUSIVE MODE"),
                ("alter_table_disable_all_triggers", $"ALTER TABLE {t} DISABLE ALL TRIGGERS"),
                ("drop_sequence", $"DROP SEQUENCE {seq}"),
                ("drop_index", $"DROP INDEX {ix}"),
            };

            var n = 0;
            foreach (var (label, sql) in steps)
            {
                n++;
                await SilentAsync(s, "ROLLBACK");
                await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES ({n.ToString(CultureInfo.InvariantCulture)}, 'pending')");
                var where = $"WHERE ID = {n.ToString(CultureInfo.InvariantCulture)}";
                var before = await ScalarAsync(other, $"SELECT COUNT(*) FROM {t} {where}");
                var result = await TryTextAsync(s, sql);
                var after = await ScalarAsync(other, $"SELECT COUNT(*) FROM {t} {where}");
                await SilentAsync(s, "ROLLBACK");
                var own = await ScalarAsync(s, $"SELECT COUNT(*) FROM {t} {where}");
                Measure($"implicit_commit.{label}", $"{result} | other_before={before} other_after={after} own_after_rollback={own} (other_after=1 or own_after_rollback=1 means the statement committed)");
            }
        }
        finally
        {
            await SilentAsync(s, $"DROP SEQUENCE {seq}");
            await DropTableAsync(s, t);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 4. 트리거·FK
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>트리거 끄기·켜기(표 전체·개별)와 FK 끄기·ENABLE NOVALIDATE·ENABLE VALIDATE(부모 없는 행이 있을 때), FK 다시 붙이기 문장, 참조당한 부모 DROP.</summary>
    [Fact]
    public async Task Measure_triggers_and_foreign_key_enable_disable()
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

        var s = await it.OpenAsync();
        var par = TiberoItSessions.ObjectName("TBM_PAR");
        var chd = TiberoItSessions.ObjectName("TBM_CHD");
        var trg = TiberoItSessions.ObjectName("TBM_TRG");
        var fk = TiberoItSessions.ObjectName("TBM_FK");
        await DropTableAsync(s, chd);
        await DropTableAsync(s, par);
        await ExecAsync(s, $"CREATE TABLE {par} (ID NUMBER PRIMARY KEY, VC VARCHAR2(10))");
        await ExecAsync(s, $"CREATE TABLE {chd} (ID NUMBER PRIMARY KEY, PID NUMBER, NOTE VARCHAR2(20), CONSTRAINT {fk} FOREIGN KEY (PID) REFERENCES {par} (ID))");
        try
        {
            await ExecAsync(s, $"INSERT INTO {par} (ID, VC) VALUES (1, 'p1')");
            await ExecAsync(s, $"INSERT INTO {par} (ID, VC) VALUES (2, 'p2')");
            await ExecAsync(s, "COMMIT");

            // ---- 트리거
            var triggerCreated = await TryExecAsync(
                s,
                "trigger.create_before_insert",
                $"CREATE OR REPLACE TRIGGER {trg} BEFORE INSERT ON {chd} FOR EACH ROW BEGIN :NEW.NOTE := 'TRG'; END;");
            if (triggerCreated)
            {
                var triggerStatus = $"SELECT STATUS FROM ALL_TRIGGERS WHERE OWNER = USER AND TRIGGER_NAME = '{trg}'";
                var id = 10;
                async Task InsertChildAsync(string label, int pid)
                {
                    id++;
                    var idText = id.ToString(CultureInfo.InvariantCulture);
                    var result = await TryTextAsync(s, $"INSERT INTO {chd} (ID, PID) VALUES ({idText}, {pid.ToString(CultureInfo.InvariantCulture)})");
                    var note = await ScalarAsync(s, $"SELECT NOTE FROM {chd} WHERE ID = {idText}");
                    Measure(label, $"{result} | NOTE={(note ?? "(null or no row)")}");
                }

                await TryQueryAsync(s, "trigger.status_initial", triggerStatus);
                await InsertChildAsync("trigger.insert_with_trigger_enabled", 1);
                await TryExecAsync(s, "trigger.alter_table_disable_all", $"ALTER TABLE {chd} DISABLE ALL TRIGGERS");
                await TryQueryAsync(s, "trigger.status_after_disable_all", triggerStatus);
                await InsertChildAsync("trigger.insert_after_disable_all", 1);
                await TryExecAsync(s, "trigger.alter_table_enable_all", $"ALTER TABLE {chd} ENABLE ALL TRIGGERS");
                await TryQueryAsync(s, "trigger.status_after_enable_all", triggerStatus);
                await InsertChildAsync("trigger.insert_after_enable_all", 1);
                await TryExecAsync(s, "trigger.alter_trigger_disable", $"ALTER TRIGGER {trg} DISABLE");
                await TryQueryAsync(s, "trigger.status_after_alter_disable", triggerStatus);
                await InsertChildAsync("trigger.insert_after_alter_disable", 1);
                await TryExecAsync(s, "trigger.alter_trigger_enable", $"ALTER TRIGGER {trg} ENABLE");
                await TryQueryAsync(s, "trigger.status_after_alter_enable", triggerStatus);
                await InsertChildAsync("trigger.insert_after_alter_enable", 1);
                await TryExecAsync(s, "trigger.alter_trigger_quoted_disable", $"ALTER TRIGGER \"{trg}\" DISABLE");
                await TryExecAsync(s, "trigger.alter_trigger_quoted_enable", $"ALTER TRIGGER \"{trg}\" ENABLE");

                // 복사가 트리거 본문을 받는 방법(DBMS_METADATA).
                var ddl = $"DBMS_METADATA.GET_DDL('TRIGGER', '{trg}', USER)";
                await TryQueryAsync(s, "trigger.get_ddl_length_and_head", $"SELECT DBMS_LOB.GETLENGTH({ddl}) AS LEN, DBMS_LOB.SUBSTR({ddl}, 300, 1) AS HEAD FROM DUAL");
                await TryQueryAsync(s, "trigger.get_ddl_tail", $"SELECT DBMS_LOB.SUBSTR({ddl}, 120, GREATEST(DBMS_LOB.GETLENGTH({ddl}) - 119, 1)) AS TAIL FROM DUAL");
                var owner = await ScalarAsync(s, "SELECT USER FROM DUAL");
                await TryQueryAsync(
                    s,
                    "trigger.get_ddl_bound_name_owner",
                    "SELECT DBMS_LOB.GETLENGTH(DBMS_METADATA.GET_DDL('TRIGGER', :NAME, :OWN)) AS LEN FROM DUAL",
                    cmd =>
                    {
                        AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, trg);
                        AddParam(s, cmd, "OWN", TiberoParamKind.Varchar2, owner);
                    });
            }

            // ---- FK
            var fkStatus = $"SELECT STATUS, VALIDATED, DEFERRABLE, DEFERRED, DELETE_RULE FROM ALL_CONSTRAINTS WHERE OWNER = USER AND CONSTRAINT_NAME = '{fk}'";
            var fkStatusShort = $"SELECT STATUS, DEFERRABLE, DEFERRED, DELETE_RULE FROM ALL_CONSTRAINTS WHERE OWNER = USER AND CONSTRAINT_NAME = '{fk}'";
            async Task FkStatusAsync(string label)
            {
                var (ok, _) = await TryQueryTextAsync(s, fkStatus, null, 3);
                await TryQueryAsync(s, label, ok ? fkStatus : fkStatusShort);
            }

            var orphanCount = $"SELECT COUNT(*) FROM {chd} c WHERE c.PID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {par} p WHERE p.ID = c.PID)";
            await FkStatusAsync("fk.status_initial");
            Measure("fk.insert_orphan_while_enabled", await TryTextAsync(s, $"INSERT INTO {chd} (ID, PID) VALUES (900, 999)"));
            await TryExecAsync(s, "fk.disable_constraint", $"ALTER TABLE {chd} DISABLE CONSTRAINT \"{fk}\"");
            await FkStatusAsync("fk.status_after_disable");
            Measure("fk.insert_orphan_while_disabled", await TryTextAsync(s, $"INSERT INTO {chd} (ID, PID) VALUES (901, 999)"));
            Measure("fk.orphan_count_query", await ScalarAsync(s, orphanCount) ?? "(null)");
            await TryExecAsync(s, "fk.enable_validate_with_orphans", $"ALTER TABLE {chd} ENABLE VALIDATE CONSTRAINT \"{fk}\"");
            await FkStatusAsync("fk.status_after_failed_validate");
            await TryExecAsync(s, "fk.enable_novalidate_with_orphans", $"ALTER TABLE {chd} ENABLE NOVALIDATE CONSTRAINT \"{fk}\"");
            await FkStatusAsync("fk.status_after_novalidate");
            Measure("fk.insert_orphan_after_novalidate", await TryTextAsync(s, $"INSERT INTO {chd} (ID, PID) VALUES (902, 999)"));
            await TryExecAsync(s, "fk.delete_orphans", $"DELETE FROM {chd} WHERE PID = 999");
            await TryExecAsync(s, "fk.enable_validate_after_orphans_removed", $"ALTER TABLE {chd} ENABLE VALIDATE CONSTRAINT \"{fk}\"");
            await FkStatusAsync("fk.status_after_validate");

            // 자식 표를 다시 만들 때처럼 FK 를 떼고 다시 붙인다(RecreateStep.ForeignKeyDdl 모양).
            await TryExecAsync(s, "fk.drop_constraint", $"ALTER TABLE {chd} DROP CONSTRAINT \"{fk}\"");
            await TryExecAsync(s, "fk.add_on_delete_cascade_disabled", $"ALTER TABLE {chd} ADD CONSTRAINT \"{fk}\" FOREIGN KEY (\"PID\") REFERENCES {par} (\"ID\") ON DELETE CASCADE DISABLE");
            await FkStatusAsync("fk.status_cascade_disabled");
            await TryExecAsync(s, "fk.drop_constraint_2", $"ALTER TABLE {chd} DROP CONSTRAINT \"{fk}\"");
            await TryExecAsync(s, "fk.add_on_delete_set_null", $"ALTER TABLE {chd} ADD CONSTRAINT \"{fk}\" FOREIGN KEY (\"PID\") REFERENCES {par} (\"ID\") ON DELETE SET NULL");
            await FkStatusAsync("fk.status_set_null");
            await TryExecAsync(s, "fk.drop_constraint_3", $"ALTER TABLE {chd} DROP CONSTRAINT \"{fk}\"");
            await TryExecAsync(s, "fk.add_deferrable_initially_deferred", $"ALTER TABLE {chd} ADD CONSTRAINT \"{fk}\" FOREIGN KEY (\"PID\") REFERENCES {par} (\"ID\") DEFERRABLE INITIALLY DEFERRED");
            await FkStatusAsync("fk.status_deferrable_deferred");
            await TryExecAsync(s, "fk.drop_constraint_4", $"ALTER TABLE {chd} DROP CONSTRAINT \"{fk}\"");
            await TryExecAsync(s, "fk.add_deferrable_initially_immediate", $"ALTER TABLE {chd} ADD CONSTRAINT \"{fk}\" FOREIGN KEY (\"PID\") REFERENCES {par} (\"ID\") DEFERRABLE INITIALLY IMMEDIATE");
            await FkStatusAsync("fk.status_deferrable_immediate");

            // 참조당하는 부모를 CASCADE 없이 지우면(복사의 DROP TABLE 이 ORA-02449 에 대응하는 경우).
            await TryExecAsync(s, "fk.drop_referenced_parent_without_cascade", $"DROP TABLE {par}");
            await TryExecAsync(s, "fk.truncate_referenced_parent", $"TRUNCATE TABLE {par}");

            // 부모 키 열이 되는 ALL_CONS_COLUMNS 조회(복사가 FK 열을 읽는 모양).
            await TryQueryAsync(
                s,
                "fk.cons_columns_join",
                "SELECT cc.column_name, pc.column_name, p.owner, p.table_name FROM all_constraints c"
                + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
                + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
                + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
                + " WHERE c.owner = USER AND c.constraint_name = :NAME ORDER BY cc.position",
                cmd => AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, fk));
        }
        finally
        {
            await DropTableAsync(s, chd);
            await DropTableAsync(s, par);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 5. 시퀀스
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>NEXTVAL 반복·한 번에 여러 개 소비(CONNECT BY LEVEL, 서브쿼리, 바인드)·ALTER SEQUENCE INCREMENT BY 요령·ALL_SEQUENCES 값.</summary>
    [Fact]
    public async Task Measure_sequence_nextval_bulk_consumption_and_alter_increment()
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

        var s = await it.OpenAsync();
        var seq = TiberoItSessions.ObjectName("TBM_SEQ");
        await SilentAsync(s, $"DROP SEQUENCE {seq}");
        await ExecAsync(s, $"CREATE SEQUENCE {seq} START WITH 1 INCREMENT BY 1 CACHE 20");
        try
        {
            var user = await ScalarAsync(s, "SELECT USER FROM DUAL") ?? string.Empty;
            var info = $"SELECT LAST_NUMBER, INCREMENT_BY, CACHE_SIZE FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = USER AND SEQUENCE_NAME = '{seq}'";

            var values = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                values.Add(await ScalarAsync(s, $"SELECT {seq}.NEXTVAL FROM DUAL") ?? "(null)");
            }

            Measure("sequence.nextval_from_dual_x3", string.Join(",", values));
            await TryQueryAsync(s, "sequence.all_sequences_after_3", info);
            await TryQueryAsync(s, "sequence.currval", $"SELECT {seq}.CURRVAL FROM DUAL");
            await TryQueryAsync(s, "sequence.multirow_level_subquery", $"SELECT {seq}.NEXTVAL AS V FROM (SELECT LEVEL FROM DUAL CONNECT BY LEVEL <= 5)", null, 10);
            await TryQueryAsync(s, "sequence.multirow_connect_by_literal", $"SELECT {seq}.NEXTVAL AS V FROM DUAL CONNECT BY LEVEL <= 5", null, 10);
            await TryQueryAsync(
                s,
                "sequence.multirow_connect_by_bind_decimal",
                $"SELECT {seq}.NEXTVAL AS V FROM DUAL CONNECT BY LEVEL <= :N",
                cmd => AddParam(s, cmd, "N", TiberoParamKind.Decimal, 5m),
                10);
            await TryQueryAsync(
                s,
                "sequence.multirow_connect_by_bind_int32",
                $"SELECT {seq}.NEXTVAL AS V FROM DUAL CONNECT BY LEVEL <= :N",
                cmd => AddParam(s, cmd, "N", TiberoParamKind.Int32, 5),
                10);
            await TryQueryAsync(
                s,
                "sequence.multirow_level_subquery_bind",
                $"SELECT {seq}.NEXTVAL AS V FROM (SELECT LEVEL FROM DUAL CONNECT BY LEVEL <= :N)",
                cmd => AddParam(s, cmd, "N", TiberoParamKind.Decimal, 5m),
                10);
            await TryQueryAsync(s, "sequence.quoted_qualified_nextval", $"SELECT \"{user}\".\"{seq}\".NEXTVAL AS V FROM DUAL");
            await TryQueryAsync(s, "sequence.all_sequences_after_bulk", info);

            // 행을 끝까지 읽어야 소비되나: 1000 행짜리 문장에서 한 행만 읽고 닫은 뒤 다음 값.
            var before = await ScalarAsync(s, $"SELECT {seq}.NEXTVAL FROM DUAL");
            string? firstOnly;
            try
            {
                firstOnly = await ReadFirstRowOnlyAsync(s, $"SELECT {seq}.NEXTVAL FROM DUAL CONNECT BY LEVEL <= 1000");
            }
            catch (Exception ex)
            {
                firstOnly = "ERR " + Describe(ex);
            }

            var after = await ScalarAsync(s, $"SELECT {seq}.NEXTVAL FROM DUAL");
            Measure("sequence.partial_read_consumption", $"nextval_before={before} first_row_of_1000={firstOnly} nextval_after={after} (gap shows how many were consumed by the one-row read)");

            // 바인드로 ALL_SEQUENCES(복사의 SequenceInfo 모양).
            await TryQueryAsync(
                s,
                "sequence.all_sequences_bound",
                "SELECT LAST_NUMBER, INCREMENT_BY FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN AND SEQUENCE_NAME = :SEQ",
                cmd =>
                {
                    AddParam(s, cmd, "OWN", TiberoParamKind.Varchar2, user);
                    AddParam(s, cmd, "SEQ", TiberoParamKind.Varchar2, seq);
                });

            // ALTER SEQUENCE INCREMENT BY 요령(Oracle 복사 코드는 쓰지 않고 NEXTVAL 을 소비한다 — 대안으로 측정).
            await TryExecAsync(s, "sequence.alter_increment_by_100", $"ALTER SEQUENCE {seq} INCREMENT BY 100");
            await TryQueryAsync(s, "sequence.nextval_with_increment_100", $"SELECT {seq}.NEXTVAL AS V FROM DUAL");
            await TryQueryAsync(s, "sequence.all_sequences_after_alter", info);
            await TryExecAsync(s, "sequence.alter_increment_by_1", $"ALTER SEQUENCE {seq} INCREMENT BY 1");
            await TryQueryAsync(s, "sequence.nextval_after_restoring_increment", $"SELECT {seq}.NEXTVAL AS V FROM DUAL");
            await TryExecAsync(s, "sequence.alter_increment_by_negative", $"ALTER SEQUENCE {seq} INCREMENT BY -1");
            await TryExecAsync(s, "sequence.alter_increment_back_to_1", $"ALTER SEQUENCE {seq} INCREMENT BY 1");

            // NEXTVAL 이 트랜잭션을 여나(대기 중 표시에 영향).
            await SilentAsync(s, "ROLLBACK");
            await ScalarAsync(s, $"SELECT {seq}.NEXTVAL FROM DUAL");
            Measure("sequence.local_transaction_id_after_nextval", await ScalarAsync(s, "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL") ?? "(null: no transaction opened)");
            await SilentAsync(s, "ROLLBACK");
            var afterRollback = await ScalarAsync(s, $"SELECT {seq}.NEXTVAL FROM DUAL");
            Measure("sequence.nextval_not_rolled_back", "next value after ROLLBACK=" + afterRollback);
        }
        finally
        {
            await SilentAsync(s, $"DROP SEQUENCE {seq}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 6. IDENTITY
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>IDENTITY 열 문법이 되나(Tibero 6 는 안 될 것으로 예상), ALL_TAB_COLUMNS 의 IDENTITY_COLUMN 열, 시퀀스 DEFAULT 대안.</summary>
    [Fact]
    public async Task Measure_identity_columns_syntax_and_catalog()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TBM_IDT");
        var seq = TiberoItSessions.ObjectName("TBM_IDS");
        await DropTableAsync(s, t);
        await SilentAsync(s, $"DROP SEQUENCE {seq}");
        try
        {
            var variants = new (string Label, string Definition)[]
            {
                ("generated_always", "ID NUMBER GENERATED ALWAYS AS IDENTITY, VC VARCHAR2(10)"),
                ("generated_by_default", "ID NUMBER GENERATED BY DEFAULT AS IDENTITY, VC VARCHAR2(10)"),
                ("generated_by_default_on_null", "ID NUMBER GENERATED BY DEFAULT ON NULL AS IDENTITY, VC VARCHAR2(10)"),
                ("generated_always_start_with", "ID NUMBER GENERATED ALWAYS AS IDENTITY (START WITH 100 INCREMENT BY 5), VC VARCHAR2(10)"),
            };

            foreach (var (label, definition) in variants)
            {
                await DropTableAsync(s, t);
                if (!await TryExecAsync(s, $"identity.create.{label}", $"CREATE TABLE {t} ({definition})"))
                {
                    continue;
                }

                await TryExecAsync(s, $"identity.insert_without_id.{label}", $"INSERT INTO {t} (VC) VALUES ('a')");
                await TryQueryAsync(s, $"identity.row_after_insert.{label}", $"SELECT ID, VC FROM {t}");
                await TryQueryAsync(s, $"identity.catalog_identity_cols.{label}", $"SELECT COLUMN_NAME, GENERATION_TYPE FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = USER AND TABLE_NAME = '{t}'");
                await TryQueryAsync(s, $"identity.catalog_tab_columns.{label}", $"SELECT COLUMN_NAME, IDENTITY_COLUMN FROM ALL_TAB_COLUMNS WHERE OWNER = USER AND TABLE_NAME = '{t}'");
                await TryExecAsync(s, $"identity.modify_start_with_limit_value.{label}", $"ALTER TABLE {t} MODIFY (\"ID\" GENERATED BY DEFAULT AS IDENTITY (START WITH LIMIT VALUE))");
            }

            // ALL_TAB_COLUMNS 에 IDENTITY_COLUMN 열이 있나(열 이름 목록).
            var columns = await TryColumnNamesAsync(s, "ALL_TAB_COLUMNS");
            Measure("identity.all_tab_columns_columns", columns is null ? "(view not readable)" : string.Join(",", columns));
            Measure("identity.all_tab_columns_has_IDENTITY_COLUMN", columns is null ? "unknown" : columns.Contains("IDENTITY_COLUMN", StringComparer.OrdinalIgnoreCase).ToString());
            var identityView = await TryColumnNamesAsync(s, "ALL_TAB_IDENTITY_COLS");
            Measure("identity.all_tab_identity_cols_columns", identityView is null ? "(view missing or not readable)" : string.Join(",", identityView));

            // 대안: 열 기본값이 시퀀스 NEXTVAL.
            await DropTableAsync(s, t);
            await ExecAsync(s, $"CREATE SEQUENCE {seq}");
            if (await TryExecAsync(s, "identity.alternative_default_nextval.create", $"CREATE TABLE {t} (ID NUMBER DEFAULT {seq}.NEXTVAL, VC VARCHAR2(10))"))
            {
                await TryExecAsync(s, "identity.alternative_default_nextval.insert", $"INSERT INTO {t} (VC) VALUES ('a')");
                await TryQueryAsync(s, "identity.alternative_default_nextval.row", $"SELECT ID, VC FROM {t}");
                await TryQueryAsync(s, "identity.alternative_default_catalog", $"SELECT COLUMN_NAME, DATA_DEFAULT FROM ALL_TAB_COLUMNS WHERE OWNER = USER AND TABLE_NAME = '{t}' AND COLUMN_NAME = 'ID'");
            }

            await SilentAsync(s, "ROLLBACK");
        }
        finally
        {
            await DropTableAsync(s, t);
            await SilentAsync(s, $"DROP SEQUENCE {seq}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 7. 휴지통
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>DROP TABLE(PURGE 없음) 뒤 휴지통 뷰, "BIN$…" 조회, FLASHBACK TABLE … TO BEFORE DROP [RENAME TO], PURGE TABLE, PURGE RECYCLEBIN.</summary>
    [Fact]
    public async Task Measure_recycle_bin_drop_flashback_and_purge()
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

        var s = await it.OpenAsync();
        var a = TiberoItSessions.ObjectName("TBM_RB");
        var b = TiberoItSessions.ObjectName("TBM_RB2");
        await DropTableAsync(s, a);
        await DropTableAsync(s, b);
        await SilentAsync(s, $"PURGE TABLE {a}");
        await SilentAsync(s, $"PURGE TABLE {b}");
        try
        {
            async Task<Table?> RecycleRowsAsync(string original)
            {
                try
                {
                    return await QueryTableAsync(s, $"SELECT * FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME = '{original}'");
                }
                catch (Exception ex)
                {
                    Measure("recycle_bin.user_recyclebin_query_failed", Describe(ex));
                    return null;
                }
            }

            async Task CreateOneRowAsync(string name)
            {
                await ExecAsync(s, $"CREATE TABLE {name} (ID NUMBER)");
                await ExecAsync(s, $"INSERT INTO {name} (ID) VALUES (1)");
                await ExecAsync(s, "COMMIT");
            }

            await TryQueryAsync(s, "recycle_bin.user_recyclebin_columns", "SELECT * FROM USER_RECYCLEBIN WHERE ROWNUM <= 1");
            await TryQueryAsync(s, "recycle_bin.recyclebin_synonym_columns", "SELECT * FROM RECYCLEBIN WHERE ROWNUM <= 1");
            await TryQueryAsync(s, "recycle_bin.parameters", "SELECT NAME, VALUE FROM V$PARAMETER WHERE UPPER(NAME) LIKE '%RECYCLE%'");
            await TryExecAsync(s, "recycle_bin.alter_session_recyclebin_on", "ALTER SESSION SET RECYCLEBIN = ON");
            await TryExecAsync(s, "recycle_bin.alter_session_use_recyclebin", "ALTER SESSION SET USE_RECYCLEBIN = Y");

            await CreateOneRowAsync(a);
            await TryExecAsync(s, "recycle_bin.drop_without_purge", $"DROP TABLE {a}");
            var rows = await RecycleRowsAsync(a);
            Measure("recycle_bin.rows_after_drop", rows is null ? "(unreadable)" : rows.Show(5));
            var bin = rows is { Rows.Count: > 0 } ? rows.Value(0, "OBJECT_NAME") : null;
            if (bin is not null)
            {
                await TryQueryAsync(s, "recycle_bin.select_from_bin_name", $"SELECT COUNT(*) AS N FROM \"{bin}\"");
            }
            else
            {
                Measure("recycle_bin.select_from_bin_name", "skipped: no USER_RECYCLEBIN row for the dropped table (recycle bin off or view differs)");
            }

            await TryQueryAsync(s, "recycle_bin.select_dropped_table", $"SELECT COUNT(*) AS N FROM {a}");
            await TryExecAsync(s, "recycle_bin.flashback_to_before_drop", $"FLASHBACK TABLE {a} TO BEFORE DROP");
            await TryQueryAsync(s, "recycle_bin.rows_after_flashback", $"SELECT COUNT(*) AS N FROM {a}");
            var after = await RecycleRowsAsync(a);
            Measure("recycle_bin.entries_after_flashback", after is null ? "(unreadable)" : "count=" + after.Rows.Count);

            // RENAME TO 로 옛 표를 다른 이름으로.
            await DropTableAsync(s, b);
            if (await ScalarAsync(s, $"SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = '{a}'") == "1")
            {
                await TryExecAsync(s, "recycle_bin.drop_again", $"DROP TABLE {a}");
            }

            await TryExecAsync(s, "recycle_bin.flashback_rename_to", $"FLASHBACK TABLE {a} TO BEFORE DROP RENAME TO {b}");
            await TryQueryAsync(s, "recycle_bin.rows_after_flashback_rename", $"SELECT COUNT(*) AS N FROM {b}");
            await TryQueryAsync(s, "recycle_bin.original_name_exists_after_rename", $"SELECT COUNT(*) AS N FROM USER_TABLES WHERE TABLE_NAME = '{a}'");

            // PURGE TABLE (원래 이름).
            await TryExecAsync(s, "recycle_bin.drop_for_purge_by_name", $"DROP TABLE {b}");
            await TryExecAsync(s, "recycle_bin.purge_table_by_original_name", $"PURGE TABLE {b}");
            var purged = await RecycleRowsAsync(b);
            Measure("recycle_bin.entries_after_purge_by_name", purged is null ? "(unreadable)" : "count=" + purged.Rows.Count);

            // PURGE TABLE "BIN$…" (휴지통 이름).
            await CreateOneRowAsync(a);
            await TryExecAsync(s, "recycle_bin.drop_for_purge_by_bin", $"DROP TABLE {a}");
            var binRows = await RecycleRowsAsync(a);
            var bin2 = binRows is { Rows.Count: > 0 } ? binRows.Value(0, "OBJECT_NAME") : null;
            if (bin2 is not null)
            {
                await TryExecAsync(s, "recycle_bin.purge_table_by_bin_name", $"PURGE TABLE \"{bin2}\"");
                var gone = await RecycleRowsAsync(a);
                Measure("recycle_bin.entries_after_purge_by_bin_name", gone is null ? "(unreadable)" : "count=" + gone.Rows.Count);
            }

            // DROP TABLE … PURGE 는 휴지통에 남기지 않나.
            await CreateOneRowAsync(a);
            await TryExecAsync(s, "recycle_bin.drop_with_purge", $"DROP TABLE {a} PURGE");
            var direct = await RecycleRowsAsync(a);
            Measure("recycle_bin.entries_after_drop_with_purge", direct is null ? "(unreadable)" : "count=" + direct.Rows.Count);

            // PURGE RECYCLEBIN 은 계정의 휴지통 전체를 비우므로, ORT_ 가 아닌 항목이 있으면 건드리지 않는다.
            await CreateOneRowAsync(a);
            await TryExecAsync(s, "recycle_bin.drop_for_purge_recyclebin", $"DROP TABLE {a}");
            var foreign = await ScalarAsync(s, "SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME NOT LIKE 'ORT\\_%' ESCAPE '\\'");
            if (foreign == "0")
            {
                await TryExecAsync(s, "recycle_bin.purge_recyclebin", "PURGE RECYCLEBIN");
                Measure("recycle_bin.entries_after_purge_recyclebin", await ScalarAsync(s, "SELECT COUNT(*) FROM USER_RECYCLEBIN") ?? "(null)");
            }
            else
            {
                Measure("recycle_bin.purge_recyclebin", $"skipped: the recycle bin holds {foreign ?? "?"} entries that are not ORT_ tables (not touched)");
            }
        }
        finally
        {
            await DropTableAsync(s, a);
            await DropTableAsync(s, b);
            await SilentAsync(s, $"PURGE TABLE {a}");
            await SilentAsync(s, $"PURGE TABLE {b}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 8. MERGE
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>MERGE INTO … USING (SELECT … FROM DUAL) … WHEN MATCHED / NOT MATCHED: 리터럴·바인드·배열 바인드, 영향 행 수.</summary>
    [Fact]
    public async Task Measure_merge_using_dual_literal_bind_and_array()
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

        var s = await it.OpenAsync();
        var t = TiberoItSessions.ObjectName("TBM_MRG");
        await DropTableAsync(s, t);
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, VC VARCHAR2(20))");
        try
        {
            await ExecAsync(s, $"INSERT INTO {t} (ID, VC) VALUES (1, 'old')");
            var contents = $"SELECT ID, VC FROM {t} ORDER BY ID";

            await TryExecAsync(
                s,
                "merge.literal_insert_branch",
                $"MERGE INTO {t} T USING (SELECT 2 AS ID, 'two' AS VC FROM DUAL) S ON (T.ID = S.ID) WHEN MATCHED THEN UPDATE SET T.VC = S.VC WHEN NOT MATCHED THEN INSERT (ID, VC) VALUES (S.ID, S.VC)");
            await TryExecAsync(
                s,
                "merge.literal_update_branch",
                $"MERGE INTO {t} T USING (SELECT 2 AS ID, 'two-upd' AS VC FROM DUAL) S ON (T.ID = S.ID) WHEN MATCHED THEN UPDATE SET T.VC = S.VC WHEN NOT MATCHED THEN INSERT (ID, VC) VALUES (S.ID, S.VC)");
            Measure("merge.contents_after_literal", (await QueryTableAsync(s, contents)).Show(10));

            // 복사 빌더가 만드는 모양(큰따옴표 식별자, USING 의 바인드 별칭).
            var shaped =
                $"MERGE INTO {t} T USING (SELECT :C1 AS \"ID\", :C2 AS \"VC\" FROM DUAL) S ON (T.\"ID\" = S.\"ID\")"
                + " WHEN MATCHED THEN UPDATE SET T.\"VC\" = S.\"VC\" WHEN NOT MATCHED THEN INSERT (\"ID\", \"VC\") VALUES (S.\"ID\", S.\"VC\")";
            await TryExecAsync(
                s,
                "merge.bind_scalar_insert_branch",
                shaped,
                cmd =>
                {
                    AddParam(s, cmd, "C1", TiberoParamKind.Decimal, 3m);
                    AddParam(s, cmd, "C2", TiberoParamKind.Varchar2, "three");
                });
            await TryExecAsync(
                s,
                "merge.bind_scalar_update_branch",
                shaped,
                cmd =>
                {
                    AddParam(s, cmd, "C1", TiberoParamKind.Decimal, 3m);
                    AddParam(s, cmd, "C2", TiberoParamKind.Varchar2, "three-upd");
                });
            await TryExecAsync(
                s,
                "merge.bind_null_value",
                shaped,
                cmd =>
                {
                    AddParam(s, cmd, "C1", TiberoParamKind.Decimal, 3m);
                    AddParam(s, cmd, "C2", TiberoParamKind.Varchar2, null);
                });
            Measure("merge.contents_after_scalar_binds", (await QueryTableAsync(s, contents)).Show(10));

            // 배열 바인드: 1(있음)·4·5(없음).
            foreach (var typed in new[] { false, true })
            {
                var mode = typed ? "typed_arrays" : "object_arrays";
                var ids = new ArrayCol("C1", TiberoParamKind.Decimal, MergeIds);
                var vcs = new ArrayCol("C2", TiberoParamKind.Varchar2, MergeValues);
                await TryExecAsync(
                    s,
                    $"merge.array_bind.{mode}",
                    shaped,
                    cmd =>
                    {
                        Drv(s).SetArrayBindCount(cmd, 3);
                        AddParam(s, cmd, "C1", ids.Kind, typed ? ids.Typed : ids.Objects(3));
                        AddParam(s, cmd, "C2", vcs.Kind, typed ? vcs.Typed : vcs.Objects(3));
                    });
                Measure($"merge.contents_after_array.{mode}", (await QueryTableAsync(s, contents)).Show(10));
                await SilentAsync(s, "ROLLBACK");
            }

            // 키만 있는 표처럼 UPDATE 갈래가 없을 때.
            await TryExecAsync(
                s,
                "merge.insert_only_branch",
                $"MERGE INTO {t} T USING (SELECT 90 AS ID FROM DUAL) S ON (T.ID = S.ID) WHEN NOT MATCHED THEN INSERT (ID) VALUES (S.ID)");
            await TryExecAsync(
                s,
                "merge.insert_only_branch_second_time",
                $"MERGE INTO {t} T USING (SELECT 90 AS ID FROM DUAL) S ON (T.ID = S.ID) WHEN NOT MATCHED THEN INSERT (ID) VALUES (S.ID)");
            await SilentAsync(s, "ROLLBACK");
        }
        finally
        {
            await DropTableAsync(s, t);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 9. 카탈로그 뷰·열 (읽기 전용)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Oracle 복사 카탈로그 조회(CopyCatalogQueries·CopyRunner·RecreateStep·CopyPrecheckService)가 쓰는 뷰·열이 Tibero 에 있나. 읽기만 한다.</summary>
    [Fact]
    public async Task Measure_copy_catalog_views_and_columns()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var s = await it.OpenAsync();
        await TryQueryAsync(s, "catalog.server_version", "SELECT * FROM V$VERSION");
        var user = await ScalarAsync(s, "SELECT USER FROM DUAL") ?? string.Empty;

        foreach (var (view, columns) in CopyViews)
        {
            var all = await TryColumnNamesAsync(s, view);
            if (all is null)
            {
                Measure($"catalog.view.{view}", "MISSING_OR_NOT_READABLE (see the SELECT * probe below)");
                await TryQueryAsync(s, $"catalog.view.{view}.select_star", $"SELECT * FROM {view} WHERE ROWNUM <= 1", null, 0);
                continue;
            }

            var missing = columns.Where(c => !all.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            Measure($"catalog.view.{view}", $"present; missing_used_columns=[{string.Join(",", missing)}]; all_columns=[{string.Join(",", all)}]");
            var (ok, text) = await TryQueryTextAsync(s, $"SELECT {string.Join(", ", columns)} FROM {view} WHERE ROWNUM <= 1", null, 0);
            Measure($"catalog.view.{view}.select_used_columns", ok ? "OK " + text : text);
        }

        // Oracle 복사가 실제로 보내는 문장(바인드 포함)을 그대로. 존재하지 않는 표 이름으로 문장이 파싱·실행되는지만 본다.
        void Own(DbCommand cmd) => AddParam(s, cmd, "OWN", TiberoParamKind.Varchar2, user);
        void OwnTbl(DbCommand cmd)
        {
            Own(cmd);
            AddParam(s, cmd, "TBL", TiberoParamKind.Varchar2, "ORT_NO_SUCH_TABLE");
        }

        var statements = new (string Label, string Sql, Action<DbCommand>? Prepare)[]
        {
            ("session_user", "SELECT USER FROM DUAL", null),
            ("session_roles", "SELECT ROLE FROM SESSION_ROLES", null),
            ("session_privs", "SELECT PRIVILEGE FROM SESSION_PRIVS", null),
            ("table_privileges", "SELECT GRANTEE, PRIVILEGE FROM ALL_TAB_PRIVS WHERE TABLE_SCHEMA = :OWN AND TABLE_NAME = :TBL", OwnTbl),
            ("enabled_triggers", "SELECT OWNER, TRIGGER_NAME FROM ALL_TRIGGERS WHERE TABLE_OWNER = :OWN AND TABLE_NAME = :TBL AND STATUS = 'ENABLED' ORDER BY OWNER, TRIGGER_NAME", OwnTbl),
            (
                "referencing_foreign_keys",
                "SELECT c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME FROM ALL_CONSTRAINTS c "
                + "JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME "
                + "WHERE c.CONSTRAINT_TYPE = 'R' AND c.STATUS = 'ENABLED' AND p.OWNER = :OWN AND p.TABLE_NAME = :TBL "
                + "AND NOT (c.OWNER = p.OWNER AND c.TABLE_NAME = p.TABLE_NAME) "
                + "ORDER BY c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME",
                OwnTbl
            ),
            ("identity_columns", "SELECT COLUMN_NAME, GENERATION_TYPE FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = :OWN AND TABLE_NAME = :TBL", OwnTbl),
            (
                "sequence_info",
                "SELECT LAST_NUMBER, INCREMENT_BY FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN AND SEQUENCE_NAME = :SEQ",
                cmd =>
                {
                    Own(cmd);
                    AddParam(s, cmd, "SEQ", TiberoParamKind.Varchar2, "ORT_NO_SUCH_SEQ");
                }
            ),
            (
                "sequence_increment_cache",
                "SELECT increment_by, last_number, cache_size FROM all_sequences WHERE sequence_owner = :OWNER AND sequence_name = :NAME",
                cmd =>
                {
                    AddParam(s, cmd, "OWNER", TiberoParamKind.Varchar2, user);
                    AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, "ORT_NO_SUCH_SEQ");
                }
            ),
            ("sequence_names", "SELECT SEQUENCE_NAME FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN ORDER BY SEQUENCE_NAME", Own),
            (
                "parameter_value",
                "SELECT VALUE FROM V$PARAMETER WHERE NAME = :PNAME",
                cmd => AddParam(s, cmd, "PNAME", TiberoParamKind.Varchar2, "db_name")
            ),
            (
                "table_exists",
                "SELECT COUNT(*) FROM all_tables WHERE owner = :OWNER AND table_name = :NAME",
                cmd =>
                {
                    AddParam(s, cmd, "OWNER", TiberoParamKind.Varchar2, user);
                    AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, "ORT_NO_SUCH_TABLE");
                }
            ),
            (
                "foreign_key_columns",
                "SELECT cc.column_name, pc.column_name, p.owner, p.table_name FROM all_constraints c"
                + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
                + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
                + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
                + " WHERE c.owner = :OWNER AND c.constraint_name = :NAME ORDER BY cc.position",
                cmd =>
                {
                    AddParam(s, cmd, "OWNER", TiberoParamKind.Varchar2, user);
                    AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, "ORT_NO_SUCH_FK");
                }
            ),
            (
                "triggers_of_table",
                "SELECT owner, trigger_name, status FROM all_triggers WHERE table_owner = :OWN AND table_name = :TBL AND base_object_type = 'TABLE' ORDER BY trigger_name",
                OwnTbl
            ),
            (
                "grants_by_owner_twice_bound",
                "SELECT grantee, privilege, grantable FROM all_tab_privs WHERE table_schema = :OWN AND table_name = :TBL AND grantor = :OWN ORDER BY grantee, privilege",
                OwnTbl
            ),
            (
                "referencing_fk_columns",
                "SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred"
                + " FROM all_constraints c JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
                + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
                + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
                + " WHERE c.constraint_type = 'R' AND p.owner = :OWN AND p.table_name = :TBL AND NOT (c.owner = p.owner AND c.table_name = p.table_name)"
                + " ORDER BY c.owner, c.table_name, c.constraint_name, cc.position",
                OwnTbl
            ),
            (
                "source_foreign_keys",
                "SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred, p.table_name"
                + " FROM all_constraints c JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
                + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
                + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
                + " WHERE c.constraint_type = 'R' AND c.owner = :OWN AND c.table_name = :TBL AND p.owner = c.owner ORDER BY c.constraint_name, cc.position",
                OwnTbl
            ),
            (
                "recycle_bin_of_table",
                "SELECT object_name FROM user_recyclebin WHERE original_name = :TBL AND type = 'TABLE' AND operation = 'DROP'",
                cmd => AddParam(s, cmd, "TBL", TiberoParamKind.Varchar2, "ORT_NO_SUCH_TABLE")
            ),
            ("tablespace_of_table", "SELECT tablespace_name FROM all_tables WHERE owner = :OWN AND table_name = :TBL", OwnTbl),
            (
                "constraint_owner",
                "SELECT table_name FROM all_constraints WHERE owner = :OWN AND constraint_name = :NAME",
                cmd =>
                {
                    Own(cmd);
                    AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, "ORT_NO_SUCH_CONSTRAINT");
                }
            ),
            (
                "index_owner",
                "SELECT table_name FROM all_indexes WHERE owner = :OWN AND index_name = :NAME",
                cmd =>
                {
                    Own(cmd);
                    AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, "ORT_NO_SUCH_INDEX");
                }
            ),
            ("column_privilege_count", "SELECT COUNT(*) FROM all_col_privs WHERE table_schema = :OWN AND table_name = :TBL", OwnTbl),
            ("foreign_grantor_count", "SELECT COUNT(*) FROM all_tab_privs WHERE table_schema = :OWN AND table_name = :TBL AND grantor <> :OWN", OwnTbl),
            ("mview_log_count", "SELECT COUNT(*) FROM all_mview_logs WHERE log_owner = :OWN AND master = :TBL", OwnTbl),
            ("vpd_policy_count", "SELECT COUNT(*) FROM all_policies WHERE object_owner = :OWN AND object_name = :TBL", OwnTbl),
            ("fga_policy_count", "SELECT COUNT(*) FROM all_audit_policies WHERE object_schema = :OWN AND object_name = :TBL", OwnTbl),
            (
                "object_exists",
                "SELECT COUNT(*) FROM all_objects WHERE owner = :OWN AND object_name = :NAME",
                cmd =>
                {
                    Own(cmd);
                    AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, "ORT_NO_SUCH_OBJECT");
                }
            ),
            ("trigger_count_on_table", "SELECT COUNT(*) FROM all_triggers WHERE table_owner = :OWN AND table_name = :TBL AND base_object_type = 'TABLE'", OwnTbl),
            (
                "plain_table_not_mview",
                "SELECT COUNT(*) FROM all_tables t WHERE t.owner = :OWN AND t.table_name = :TBL AND NOT EXISTS (SELECT 1 FROM all_mviews m WHERE m.owner = t.owner AND m.mview_name = t.table_name)",
                OwnTbl
            ),
            (
                "get_ddl_trigger_missing_object",
                "SELECT DBMS_METADATA.GET_DDL('TRIGGER', :NAME, :OWN) FROM DUAL",
                cmd =>
                {
                    AddParam(s, cmd, "NAME", TiberoParamKind.Varchar2, "ORT_NO_SUCH_TRIGGER");
                    Own(cmd);
                }
            ),
            ("get_ddl_table_length", "SELECT DBMS_LOB.GETLENGTH(DBMS_METADATA.GET_DDL('TABLE', TABLE_NAME, OWNER)) AS LEN FROM ALL_TABLES WHERE OWNER = USER AND ROWNUM <= 1", null),
        };

        foreach (var (label, sql, prepare) in statements)
        {
            await TryQueryAsync(s, "catalog.statement." + label, sql, prepare, 0);
        }

        // 바인드 규칙: 쓰지 않는 바인드·없는 바인드.
        await TryQueryAsync(s, "catalog.bind.unused_extra_parameter", "SELECT 1 AS X FROM DUAL", cmd => AddParam(s, cmd, "UNUSED", TiberoParamKind.Varchar2, "x"));
        await TryQueryAsync(s, "catalog.bind.missing_parameter", "SELECT 1 AS X FROM DUAL WHERE :MISSING IS NULL");
        await TryQueryAsync(
            s,
            "catalog.bind.same_name_used_twice_one_parameter",
            "SELECT 1 AS X FROM DUAL WHERE :A = 'x' OR :A = 'y'",
            cmd => AddParam(s, cmd, "A", TiberoParamKind.Varchar2, "x"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 도우미
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly decimal[] MergeIds = [1, 4, 5];

    private static readonly string[] MergeValues = ["one-upd", "four", "five"];

    private static readonly (string View, string[] Columns)[] CopyViews =
    [
        ("DUAL", ["DUMMY"]),
        ("SESSION_ROLES", ["ROLE"]),
        ("SESSION_PRIVS", ["PRIVILEGE"]),
        ("ALL_TAB_PRIVS", ["GRANTOR", "GRANTEE", "TABLE_SCHEMA", "TABLE_NAME", "PRIVILEGE", "GRANTABLE", "TYPE"]),
        ("ALL_COL_PRIVS", ["TABLE_SCHEMA", "TABLE_NAME"]),
        ("ALL_TRIGGERS", ["OWNER", "TRIGGER_NAME", "TABLE_OWNER", "TABLE_NAME", "BASE_OBJECT_TYPE", "STATUS"]),
        ("ALL_CONSTRAINTS", ["OWNER", "CONSTRAINT_NAME", "CONSTRAINT_TYPE", "TABLE_NAME", "R_OWNER", "R_CONSTRAINT_NAME", "DELETE_RULE", "STATUS", "DEFERRABLE", "DEFERRED", "VALIDATED"]),
        ("ALL_CONS_COLUMNS", ["OWNER", "CONSTRAINT_NAME", "TABLE_NAME", "COLUMN_NAME", "POSITION"]),
        ("ALL_TAB_IDENTITY_COLS", ["OWNER", "TABLE_NAME", "COLUMN_NAME", "GENERATION_TYPE"]),
        ("ALL_TAB_COLUMNS", ["OWNER", "TABLE_NAME", "COLUMN_NAME", "IDENTITY_COLUMN"]),
        ("ALL_SEQUENCES", ["SEQUENCE_OWNER", "SEQUENCE_NAME", "LAST_NUMBER", "INCREMENT_BY", "CACHE_SIZE"]),
        ("V$PARAMETER", ["NAME", "VALUE"]),
        ("ALL_TABLES", ["OWNER", "TABLE_NAME", "TABLESPACE_NAME"]),
        ("ALL_INDEXES", ["OWNER", "INDEX_NAME", "TABLE_NAME"]),
        ("ALL_MVIEW_LOGS", ["LOG_OWNER", "MASTER"]),
        ("ALL_MVIEWS", ["OWNER", "MVIEW_NAME"]),
        ("ALL_POLICIES", ["OBJECT_OWNER", "OBJECT_NAME"]),
        ("ALL_AUDIT_POLICIES", ["OBJECT_SCHEMA", "OBJECT_NAME"]),
        ("ALL_OBJECTS", ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS"]),
        ("USER_RECYCLEBIN", ["OBJECT_NAME", "ORIGINAL_NAME", "TYPE", "OPERATION"]),
        ("RECYCLEBIN", ["OBJECT_NAME", "ORIGINAL_NAME", "TYPE", "OPERATION"]),
    ];

    private void Measure(string topic, string value) => output.WriteLine("MEASURE " + topic + ": " + value);

    private static TiberoDriver Drv(TiberoSession s) =>
        s.Driver ?? throw new InvalidOperationException("이 시험은 실제 드라이버 세션이 필요하다.");

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static string Flat(string? text) =>
        text is null ? "(null)" : text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>예외를 서버 번호·메시지(드라이버 예외가 아니면 형식 이름)와, 있으면 배열 바인드 오류 목록으로 적는다.</summary>
    /// <summary>되읽기 질의를 보내 표를 MEASURE 로 남긴다. 실패해도 던지지 않고 ERR 로 남긴다(#42: 되읽기 예외가 Fact 를 멈췄다).</summary>
    private async Task ReadbackAsync(TiberoSession s, string topic, string sql, int showRows)
    {
        try
        {
            Measure(topic, (await QueryTableAsync(s, sql)).Show(showRows));
        }
        catch (Exception ex)
        {
            Measure(topic, "ERR " + Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var error = TiberoErrorClassifier.ToDbError(ex, null);
        var head = error is null
            ? $"{ex.GetType().Name} {Flat(ex.Message)}"
            : $"number={error.Number.ToString(CultureInfo.InvariantCulture)} {Flat(error.Message)}";
        return Cut(head, 500) + ArrayErrors(ex);
    }

    // 드라이버 예외의 Errors 컬렉션(항목마다 Number·ArrayBindIndex·Message)을 이름으로만 읽는다(드라이버 형식을 컴파일 때 참조하지 않는다).
    private static string ArrayErrors(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var errors = e.GetType().GetProperty("Errors")?.GetValue(e);
            if (errors is string || errors is not IEnumerable items)
            {
                continue;
            }

            var sb = new StringBuilder();
            var count = 0;
            foreach (var item in items)
            {
                if (item is null)
                {
                    continue;
                }

                if (++count > 10)
                {
                    sb.Append(" ...");
                    break;
                }

                sb.Append(" [number=").Append(Prop(item, "Number")).Append(" array_index=").Append(Prop(item, "ArrayBindIndex"))
                    .Append(' ').Append(Cut(Flat(Prop(item, "Message")), 160)).Append(']');
            }

            return count == 0 ? string.Empty : " ERRORS(" + count.ToString(CultureInfo.InvariantCulture) + "):" + sb;
        }

        return string.Empty;
    }

    private static string Prop(object item, string name)
    {
        try
        {
            return Convert.ToString(item.GetType().GetProperty(name)?.GetValue(item), CultureInfo.InvariantCulture) ?? "(null)";
        }
        catch (Exception)
        {
            return "(unreadable)";
        }
    }

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

    /// <summary>문장을 보내고 "OK rows=n ms=t" 또는 "ERR number=… 메시지" 를 돌려준다(던지지 않는다).</summary>
    private static async Task<string> TryTextAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var n = await ExecAsync(s, sql, prepare);
            return $"OK rows={n.ToString(CultureInfo.InvariantCulture)} ms={sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private async Task<bool> TryExecAsync(TiberoSession s, string topic, string sql, Action<DbCommand>? prepare = null)
    {
        var text = await TryTextAsync(s, sql, prepare);
        Measure(topic, text);
        return text.StartsWith("OK", StringComparison.Ordinal);
    }

    private sealed record Table(string[] Names, List<string?[]> Rows)
    {
        public string? Value(int row, string column)
        {
            var index = Array.FindIndex(Names, n => string.Equals(n, column, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? null : Rows[row][index];
        }

        public string Show(int maxRows = 5)
        {
            var sb = new StringBuilder();
            sb.Append("rows=").Append(Rows.Count).Append(" cols=[").AppendJoin(',', Names).Append(']');
            foreach (var row in Rows.Take(maxRows))
            {
                sb.Append(" | ").AppendJoin(", ", row.Select(v => v is null ? "NULL" : Flat(v)));
            }

            return Cut(sb.ToString(), 900);
        }

        public string ShowColumnsOnly() => "rows=" + Rows.Count + " cols=[" + string.Join(',', Names) + "]";
    }

    private static async Task<Table> QueryTableAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null, int max = 100_000)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            prepare?.Invoke(cmd);
            await using var reader = await cmd.ExecuteReaderAsync();
            var names = new string[reader.FieldCount];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = reader.GetName(i);
            }

            var rows = new List<string?[]>();
            while (rows.Count < max && await reader.ReadAsync())
            {
                var row = new string?[names.Length];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(row);
            }

            return new Table(names, rows);
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private static async Task<string?> ScalarAsync(TiberoSession s, string sql)
    {
        var table = await QueryTableAsync(s, sql, null, 1);
        return table.Rows.Count == 0 ? null : table.Rows[0][0];
    }

    /// <summary>조회를 보내고 (성공?, "rows=… cols=[…] | 값…" 또는 "ERR …"). <paramref name="showRows"/> = 0 이면 값은 싣지 않고 열 이름만(카탈로그 값 노출 방지).</summary>
    private static async Task<(bool Ok, string Text)> TryQueryTextAsync(TiberoSession s, string sql, Action<DbCommand>? prepare, int showRows)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var table = await QueryTableAsync(s, sql, prepare, Math.Max(showRows, 1));
            var shown = showRows == 0 ? table.ShowColumnsOnly() : table.Show(showRows);
            return (true, $"OK {shown} ms={sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex)
        {
            return (false, "ERR " + Describe(ex));
        }
    }

    private async Task TryQueryAsync(TiberoSession s, string topic, string sql, Action<DbCommand>? prepare = null, int showRows = 5)
    {
        var (_, text) = await TryQueryTextAsync(s, sql, prepare, showRows);
        Measure(topic, text);
    }

    private static async Task<List<string>?> TryColumnNamesAsync(TiberoSession s, string view)
    {
        try
        {
            var table = await QueryTableAsync(s, $"SELECT * FROM {view} WHERE ROWNUM <= 1", null, 1);
            return [.. table.Names];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> ReadFirstRowOnlyAsync(TiberoSession s, string sql)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            await using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) : null;
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    /// <summary>표의 ID 목록(오름차순). 표나 세션이 이상하면 ERR 문장.</summary>
    private static async Task<string> IdsAsync(TiberoSession s, string table)
    {
        try
        {
            var rows = (await QueryTableAsync(s, $"SELECT ID FROM {table} ORDER BY ID")).Rows;
            return rows.Count == 0 ? "ids=(none)" : "ids=" + string.Join(",", rows.Select(r => r[0]));
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static string ReadbackSql(string table) =>
        $"SELECT ID, VC, TO_CHAR(DT, 'YYYY-MM-DD HH24:MI:SS') AS DT, DBMS_LOB.GETLENGTH(CL) AS CL_LEN, DBMS_LOB.SUBSTR(CL, 4, 1) AS CL_HEAD, DBMS_LOB.GETLENGTH(BL) AS BL_LEN, RAWTOHEX(DBMS_LOB.SUBSTR(BL, 4, 1)) AS BL_HEAD FROM {table} ORDER BY ID";

    private static async Task DropTableAsync(TiberoSession s, string table)
    {
        await SilentAsync(s, "ROLLBACK");
        if (await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS PURGE"))
        {
            return;
        }

        await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS");
    }

    /// <summary>ID NUMBER, VC VARCHAR2 두 열 표에 배열 INSERT 한 번. 결과는 "OK rows=…" 또는 "ERR …".</summary>
    private static async Task<string> TryBatchAsync(TiberoSession s, string table, decimal[] ids, string?[] vcs, bool typed)
    {
        var id = new ArrayCol("ID", TiberoParamKind.Decimal, ids);
        var vc = new ArrayCol("VC", TiberoParamKind.Varchar2, vcs);
        return await TryTextAsync(
            s,
            $"INSERT INTO {table} (ID, VC) VALUES (:ID, :VC)",
            cmd =>
            {
                Drv(s).SetArrayBindCount(cmd, ids.Length);
                AddParam(s, cmd, id.Name, id.Kind, typed ? id.Typed : id.Objects(ids.Length));
                AddParam(s, cmd, vc.Name, vc.Kind, typed ? vc.Typed : vc.Objects(ids.Length));
            });
    }

    private async Task<bool> TryInsertArrayAsync(TiberoSession s, string table, string topic, IReadOnlyList<ArrayCol> cols, bool typed, int count, int? take = null)
    {
        var names = cols.Select(c => c.Name).ToList();
        var sql = $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select(n => ":" + n))})";
        return await TryExecAsync(
            s,
            topic,
            sql,
            cmd =>
            {
                Drv(s).SetArrayBindCount(cmd, count);
                foreach (var c in cols)
                {
                    AddParam(s, cmd, c.Name, c.Kind, typed ? c.Typed : c.Objects(take ?? count));
                }
            });
    }

    /// <summary>배열 바인드 한 열. <see cref="Typed"/> = 형식 배열(decimal[]·string[]·DateTime[]·byte[][]), <see cref="Objects"/> = Oracle 경로처럼 object[](NULL 은 DBNull).</summary>
    private sealed record ArrayCol(string Name, TiberoParamKind Kind, Array Typed)
    {
        public object[] Objects(int take) => [.. Typed.Cast<object?>().Select(v => v ?? DBNull.Value).Take(take)];
    }
}
