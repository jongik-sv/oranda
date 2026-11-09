using System.Globalization;
using System.Text;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 내보내기·가져오기 실측(TM0a, docs/design/tibero-driver-transfer.md). 가져오기가 쓰는 문장 형식(<c>INSERT … SELECT … FROM DUAL UNION ALL …</c>),
/// 세이브포인트, 값 리터럴(날짜·시각·간격·RAW·LOB·유니코드·빈 문자열), 배치 크기를 6.2.15 로 확인한다. 쓰기 시험이라
/// <c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돌고 <c>ORT_</c> 표 하나를 만들었다 지운다(D3). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 단계마다 결과를 <c>TM0A</c> 로 시작하는 줄로 시험 출력에 남긴다(실패한 단계도 오류 글자와 함께 남기고 다음 단계로 간다 — 실측이 목적이다).
/// 접속 정보는 출력에 싣지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTransferProbeIt(ITestOutputHelper output)
{
    private const string Columns =
        "ID NUMBER NOT NULL, V VARCHAR2(100), D DATE, TS TIMESTAMP(9), TSTZ TIMESTAMP(6) WITH TIME ZONE, " +
        "IYM INTERVAL YEAR(4) TO MONTH, IDS INTERVAL DAY(4) TO SECOND(6), R RAW(100), B BLOB, C CLOB, " +
        "N NUMBER(38,10), NV NVARCHAR2(50), L LONG, CONSTRAINT @T@_PK PRIMARY KEY (ID)";

    private const string AllColumns = "id, v, d, ts, tstz, iym, ids, r, b, c, n, nv";

    [TiberoWriteFact]
    public async Task Import_statement_shapes_savepoints_literals_and_batch_sizes()
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

        var table = TiberoItSessions.ObjectName("TM0A");
        var session = await it.OpenAsync();
        if (TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{table}'")) > 0)
        {
            await ExecAsync(session, $"DROP TABLE {table}"); // 지난 실행이 끊겨 남은 표
        }

        await ExecAsync(session, $"CREATE TABLE {table} ({Columns.Replace("@T@", table, StringComparison.Ordinal)})");
        try
        {
            await Step("한 문장 다행 INSERT … SELECT … FROM DUAL UNION ALL (형식별 리터럴, NULL 섞임)", async () =>
            {
                var n = await ExecAsync(session, $"INSERT INTO {table} ({AllColumns}) " + Row(1, full: true) + " UNION ALL " + Row(2, full: true) + " UNION ALL " + Row(3, full: false));
                Log($"INSERT 영향 행 {n}");
                await DumpAsync(session, table);
            });

            await Step("빈 문자열 리터럴 '' 은 NULL 로 저장되는가", async () =>
            {
                await ExecAsync(session, $"INSERT INTO {table} (id, v, nv) SELECT 10, '', N'' FROM DUAL");
                Log("V,NV IS NULL = " + await ScalarTextAsync(session, $"SELECT CASE WHEN v IS NULL THEN 'Y' ELSE 'N' END || CASE WHEN nv IS NULL THEN 'Y' ELSE 'N' END FROM {table} WHERE id = 10"));
            });

            await Step("DATE '…'·TIMESTAMP '…' 형 리터럴과 날짜 문자열 암묵 변환(세션 NLS_DATE_FORMAT)", async () =>
            {
                await ExecAsync(session, $"INSERT INTO {table} (id, d, ts) SELECT 11, DATE '2024-03-05', TIMESTAMP '2024-03-05 13:14:15.123456789' FROM DUAL");
                Log(await ScalarTextAsync(session, $"SELECT TO_CHAR(d, 'YYYY-MM-DD HH24:MI:SS') || ' / ' || TO_CHAR(ts, 'YYYY-MM-DD HH24:MI:SS.FF9') FROM {table} WHERE id = 11"));
                var implicitDate = await TryAsync(() => ExecAsync(session, $"INSERT INTO {table} (id, d) SELECT 12, '2024-03-05' FROM DUAL"));
                Log("문자열 → DATE 암묵 변환: " + implicitDate);
            });

            await Step("NUMBER 리터럴(지수·소수·38자리)", async () =>
            {
                await ExecAsync(session, $"INSERT INTO {table} (id, n) SELECT 13, 1E-5 FROM DUAL UNION ALL SELECT 14, 1.5E+10 FROM DUAL UNION ALL SELECT 15, .5 FROM DUAL UNION ALL SELECT 16, 1234567890123456789012345678.1234567891 FROM DUAL");
                Log(await ScalarTextAsync(session, $"SELECT LISTAGG(TO_CHAR(n), ' | ') WITHIN GROUP (ORDER BY id) FROM {table} WHERE id BETWEEN 13 AND 16"));
            });

            await Step("유니코드(VARCHAR2 한글, NVARCHAR2 N'…')", async () =>
            {
                await ExecAsync(session, $"INSERT INTO {table} (id, v, nv) SELECT 17, '한글 it''s', N'한글 it''s' FROM DUAL");
                Log(await ScalarTextAsync(session, $"SELECT v || ' / ' || nv || ' / ' || LENGTHB(v) FROM {table} WHERE id = 17"));
            });

            await Step("가져오기 변환기가 만드는 글자 열 리터럴: NVARCHAR2 에 N 없는 한글, LONG 열에 일반 리터럴·TO_CLOB", async () =>
            {
                await ExecAsync(session, $"INSERT INTO {table} (id, nv) SELECT 22, '한글 it''s' FROM DUAL");
                Log("N 없는 한글 → NVARCHAR2: " + await ScalarTextAsync(session, $"SELECT nv FROM {table} WHERE id = 22"));
                Log("LONG 열 9000자 일반 리터럴 → " + await TryAsync(() => ExecAsync(session, $"INSERT INTO {table} (id, l) SELECT 23, '{new string('x', 9000)}' FROM DUAL")));
                Log("LONG 열 TO_CLOB → " + await TryAsync(() => ExecAsync(session, $"INSERT INTO {table} (id, l) SELECT 24, TO_CLOB('abc') FROM DUAL")));
            });

            await Step("4000자 넘는 문자열 리터럴을 VARCHAR2(100) 열이 아닌 CLOB 으로(TO_CLOB 연결) 넣기", async () =>
            {
                var chunk = new string('가', 3000);
                await ExecAsync(session, $"INSERT INTO {table} (id, c) SELECT 18, TO_CLOB('{chunk}') || TO_CLOB('{chunk}') || TO_CLOB('{chunk}') FROM DUAL");
                Log("CLOB 길이 = " + await ScalarTextAsync(session, $"SELECT DBMS_LOB.GETLENGTH(c) FROM {table} WHERE id = 18"));
                Log("한 리터럴 4001자 → " + await TryAsync(() => ExecAsync(session, $"INSERT INTO {table} (id, c) SELECT 19, '{new string('a', 4001)}' FROM DUAL")));
            });

            await Step("BLOB 2000바이트 단위 TO_BLOB(HEXTORAW) 연결, RAW(100) 에 101바이트", async () =>
            {
                var hex = string.Concat(Enumerable.Repeat("AB", 2000));
                await ExecAsync(session, $"INSERT INTO {table} (id, b) SELECT 20, TO_BLOB(HEXTORAW('{hex}')) || TO_BLOB(HEXTORAW('{hex}')) FROM DUAL");
                Log("BLOB 길이 = " + await ScalarTextAsync(session, $"SELECT DBMS_LOB.GETLENGTH(b) FROM {table} WHERE id = 20"));
                Log("RAW(100) 에 101바이트 → " + await TryAsync(() => ExecAsync(session, $"INSERT INTO {table} (id, r) SELECT 21, HEXTORAW('{new string('A', 202)}') FROM DUAL")));
            });

            await Step("세이브포인트: SAVEPOINT → INSERT → ROLLBACK TO SAVEPOINT → 건수, 그 뒤 COMMIT", async () =>
            {
                var before = await CountAsync(session, table);
                await ExecAsync(session, "SAVEPOINT tm0a_sp");
                await ExecAsync(session, $"INSERT INTO {table} (id) SELECT 100 FROM DUAL UNION ALL SELECT 101 FROM DUAL");
                var mid = await CountAsync(session, table);
                await ExecAsync(session, "ROLLBACK TO SAVEPOINT tm0a_sp");
                var after = await CountAsync(session, table);
                Log($"건수 전 {before} / INSERT 뒤 {mid} / ROLLBACK TO 뒤 {after}");
                Log("COMMIT 뒤 건수 = " + await CommitCountAsync(session, table));
            });

            await Step("한 배치 안 중복 키: 문장 단위 원자성(0행 남는가)과 오류 글자·번호", async () =>
            {
                var before = await CountAsync(session, table);
                var failed = await TryAsync(() => ExecAsync(session, $"INSERT INTO {table} (id) SELECT 200 FROM DUAL UNION ALL SELECT 201 FROM DUAL UNION ALL SELECT 200 FROM DUAL"));
                Log("오류: " + failed);
                Log($"건수 전 {before} / 실패 뒤 {await CountAsync(session, table)}");
                var seq = await TryAsync(() => ExecAsync(session, $"INSERT INTO {table} (id) SELECT 1 FROM DUAL")); // 기존 키
                Log("기존 키 한 행: " + seq);
            });

            await Step("롤백은 실제로 되돌리는가(자동 커밋 여부)", async () =>
            {
                await ExecAsync(session, $"INSERT INTO {table} (id) SELECT 300 FROM DUAL");
                await session.RollbackAsync(CancellationToken.None);
                Log("ROLLBACK 뒤 id=300 건수 = " + await ScalarTextAsync(session, $"SELECT COUNT(*) FROM {table} WHERE id = 300"));
            });

            foreach (var size in new[] { 50, 500, 2000 })
            {
                await Step($"배치 크기 {size}행 한 문장 INSERT … UNION ALL", async () =>
                {
                    var start = 1000 * (size / 10);
                    var sql = new StringBuilder($"INSERT INTO {table} (id, v) ");
                    for (var i = 0; i < size; i++)
                    {
                        sql.Append(i == 0 ? "SELECT " : " UNION ALL SELECT ").Append(start + i).Append(", 'v").Append(i).Append("' FROM DUAL");
                    }

                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var n = await ExecAsync(session, sql.ToString());
                    Log($"{size}행: 영향 행 {n}, {clock.ElapsedMilliseconds} ms, 문장 길이 {sql.Length}");
                    await session.RollbackAsync(CancellationToken.None);
                });
            }
        }
        finally
        {
            await session.RollbackAsync(CancellationToken.None);
            await ExecAsync(session, $"DROP TABLE {table}");
        }
    }

    /// <summary>가져오기 변환기가 만들 리터럴 모양(날짜 TO_DATE, 시각 TO_TIMESTAMP FF6, TZ TO_TIMESTAMP_TZ, 간격 INTERVAL, TO_BLOB(HEXTORAW), TO_CLOB).</summary>
    private static string Row(int id, bool full) => full
        ? $"SELECT {id}, 'row{id}', TO_DATE('2024-03-05 13:14:15', 'YYYY-MM-DD HH24:MI:SS'), TO_TIMESTAMP('2024-03-05 13:14:15.123456', 'YYYY-MM-DD HH24:MI:SS.FF6'), " +
          "TO_TIMESTAMP_TZ('2024-03-05 13:14:15.123456 +09:00', 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM'), INTERVAL '1-2' YEAR TO MONTH, INTERVAL '3 04:05:06.789' DAY TO SECOND, " +
          "HEXTORAW('00FF10'), TO_BLOB(HEXTORAW('00FF10')), TO_CLOB('abc'), 12345.6789, N'nv' FROM DUAL"
        : $"SELECT {id}, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL FROM DUAL";

    private async Task DumpAsync(TiberoSession session, string table)
    {
        var rows = await TiberoSessionQuery.RunAsync(session,
            $"SELECT id, v, TO_CHAR(d, 'YYYY-MM-DD HH24:MI:SS'), TO_CHAR(ts, 'YYYY-MM-DD HH24:MI:SS.FF9'), TO_CHAR(tstz, 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM'), " +
            $"TO_CHAR(iym), TO_CHAR(ids), RAWTOHEX(r), DBMS_LOB.GETLENGTH(b), DBMS_LOB.SUBSTR(c, 10, 1), TO_CHAR(n), nv FROM {table} WHERE id <= 3 ORDER BY id");
        foreach (var row in rows.Rows)
        {
            Log("행: " + string.Join(" | ", row.Select(v => TiberoSessionQuery.Str(v) ?? "<null>")));
        }
    }

    private async Task Step(string name, Func<Task> body)
    {
        Log($"== {name}");
        try
        {
            await body();
        }
#pragma warning disable CA1031 // 실측: 실패한 단계도 기록하고 다음으로 간다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log("단계 실패: " + Describe(ex));
        }
    }

    private static async Task<string> TryAsync(Func<Task<int>> body)
    {
        try
        {
            return "성공, 영향 행 " + await body();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "실패 " + Describe(ex);
        }
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + Clean(ex.Message);

    private static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private void Log(string line) => output.WriteLine("TM0A " + line);

    private static async Task<long> CountAsync(TiberoSession session, string table) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM {table}")) ?? -1;

    private static async Task<string> CommitCountAsync(TiberoSession session, string table)
    {
        await session.CommitAsync(CancellationToken.None);
        return (await CountAsync(session, table)).ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? "<null>";

    private static async Task<int> ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            return await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
