using System.Data.Common;
using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 되돌리기 지점(RB-13)의 Tibero 미실측 항목을 실제 서버로 잰다(설계 15장·16.3, README 「확신이 낮은 지점」). 결과는 docs/design/tibero-driver-exec.md 10장.
/// 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c>)이고 이름이 <c>ORB_RB</c> 로 시작하는 테이블만 만들며 처음과 끝에 지운다(<c>DROP … PURGE</c>).
/// 측정 시험이라 대부분 단언하지 않고 출력만 남긴다(서버 메시지·접속 값은 싣지 않는다 — 오류는 코드·분류만).
/// <list type="bullet">
/// <item>RETURNING ROWID INTO 출력 바인드(실행 서비스 경로)</item>
/// <item>내부 저장점 <c>ORANDA$RB</c> + FOR UPDATE 뒤 사용자 문장 실패 → ROLLBACK TO 가 잠금을 푸는지(다른 세션 NOWAIT)</item>
/// <item>읽는 식·리터럴 왕복(설계 6.6 표): 형식마다 「읽는 식 → 리터럴 → WHERE c = 리터럴 이 1행」</item>
/// <item>플래시백 오류 번호(AS OF TSN 미래·1·DDL 뒤), DDL 암묵 커밋(성공·실패), 식별자 길이, 사전 열(ALL_TRIGGERS·ALL_CONSTRAINTS.DELETE_RULE)</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class RollbackDialectMeasureIt(ITestOutputHelper output)
{
    private const string T = "ORB_RB_T";
    private const string L = "ORB_RB_L";
    private const string X = "ORB_RB_X";
    private static readonly string[] Tables = [T, L, X];

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    [TiberoWriteFact]
    public async Task Returning_rowid_into_out_bind_through_the_execution_service()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var s = await it.OpenAsync();
        await CleanupAsync(s);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {T} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30))");
            var service = new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false);

            foreach (var (label, text) in new[]
                     {
                         ("ROWID", $"INSERT INTO {T} (ID, NAME) VALUES (1, 'a') RETURNING ROWID INTO :ORANDA_RID"),
                         ("ROWIDTOCHAR", $"INSERT INTO {T} (ID, NAME) VALUES (2, 'b') RETURNING ROWIDTOCHAR(ROWID) INTO :ORANDA_RID"),
                     })
            {
                var stmt = new SqlStatement(text, default, StatementKind.Dml) { BindNames = ["ORANDA_RID"], Verb = "INSERT" };
                var binds = new Dictionary<string, BindValue> { ["ORANDA_RID"] = new(BindDataType.Varchar2, null, BindDirection.Out) };
                var r = await service.ExecuteAsync(s, stmt, binds, null, CancellationToken.None);
                var value = r.OutValues.TryGetValue("ORANDA_RID", out var v) ? v as string : null;
                var found = value is null ? "-" : await ScalarTextAsync(s, $"SELECT COUNT(*) FROM {T} WHERE ROWID = CHARTOROWID('{value}')");
                output.WriteLine($"RB13 returning {label}: status={r.Status} error={r.Error?.Code}/{r.Error?.Kind} rows={r.RowsAffected} "
                                 + $"outType={(v?.GetType().Name ?? "null")} len={value?.Length} chartorowidCount={found}");
            }

            await ExecAsync(s, "ROLLBACK");
        }
        finally
        {
            await CleanupAsync(s);
        }
    }

    [TiberoWriteFact]
    public async Task Internal_savepoint_rollback_releases_the_for_update_locks_after_a_failed_statement()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var a = await it.OpenAsync();
        var b = await it.OpenAsync();
        await CleanupAsync(a);
        try
        {
            await ExecAsync(a, $"CREATE TABLE {T} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30))");
            await ExecAsync(a, $"INSERT INTO {T} SELECT LEVEL, 'n' || LEVEL FROM DUAL CONNECT BY LEVEL <= 5");
            await ExecAsync(a, "COMMIT");

            // 대조: 저장점 없이 잠그면 다른 세션 NOWAIT 가 실패한다.
            var locked = await QueryCountAsync(a, $"SELECT ROWID, ID, NAME FROM {T} WHERE ID <= 3 FOR UPDATE");
            output.WriteLine($"RB13 savepoint control: lockedRows={locked} otherNowait={await TryAsync(b, $"SELECT ID FROM {T} WHERE ID = 1 FOR UPDATE NOWAIT")}");
            await ExecAsync(a, "ROLLBACK");

            var sp = await TryAsync(a, "SAVEPOINT ORANDA$RB");
            var locked2 = await QueryCountAsync(a, $"SELECT ROWID, ID, NAME FROM {T} WHERE ID <= 3 FOR UPDATE");
            var failed = await TryAsync(a, $"UPDATE {T} SET ID = 'x' WHERE ID <= 3"); // 사용자 문장 실패(형 변환)
            var during = await TryAsync(b, $"SELECT ID FROM {T} WHERE ID = 1 FOR UPDATE NOWAIT");
            var back = await TryAsync(a, "ROLLBACK TO SAVEPOINT ORANDA$RB");
            var after = await TryAsync(b, $"SELECT ID FROM {T} WHERE ID = 1 FOR UPDATE NOWAIT");
            await ExecAsync(b, "ROLLBACK");
            var txAfter = await ScalarTextAsync(a, TiberoExecutionService.TransactionIdSql);
            output.WriteLine($"RB13 savepoint ORANDA$RB: savepoint={sp} lockedRows={locked2} userStatement={failed} otherNowaitBefore={during} "
                             + $"rollbackTo={back} otherNowaitAfter={after} txIdAfter={(txAfter is null ? "NULL" : "set")}");

            // 같은 이름 다시 걸기, 모르는 저장점.
            output.WriteLine($"RB13 savepoint again={await TryAsync(a, "SAVEPOINT ORANDA$RB")} rollbackTo={await TryAsync(a, "ROLLBACK TO ORANDA$RB")} "
                             + $"unknown={await TryAsync(a, "ROLLBACK TO SAVEPOINT ORANDA$NONE")}");

            // FOR UPDATE 잠근 행 수 = UPDATE 영향 행 수.
            var lockedRows = await QueryCountAsync(a, $"SELECT ROWID, ID FROM {T} WHERE ID BETWEEN 2 AND 4 FOR UPDATE");
            var updated = await ExecAsync(a, $"UPDATE {T} SET NAME = NAME || 'u' WHERE ID BETWEEN 2 AND 4");
            output.WriteLine($"RB13 rowcount: locked={lockedRows} updated={updated}");
            await ExecAsync(a, "ROLLBACK");
        }
        finally
        {
            await CleanupAsync(a);
        }
    }

    [TiberoWriteFact]
    public async Task Read_expressions_and_literals_round_trip_to_exactly_one_row()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var s = await it.OpenAsync();
        await CleanupAsync(s);
        try
        {
            await ExecAsync(s, $"""
                CREATE TABLE {L} (ID NUMBER PRIMARY KEY, N NUMBER, N38 NUMBER(38), NS NUMBER(10,4), BF BINARY_FLOAT, BD BINARY_DOUBLE,
                  D DATE, TS TIMESTAMP(9), TZ TIMESTAMP(9) WITH TIME ZONE, LTZ TIMESTAMP(9) WITH LOCAL TIME ZONE,
                  IDS INTERVAL DAY(9) TO SECOND(9), IYM INTERVAL YEAR(9) TO MONTH, R RAW(16), C CHAR(5), V VARCHAR2(40), NV NVARCHAR2(20), NC NCHAR(4))
                """);
            await ExecAsync(s, $"""
                INSERT INTO {L} VALUES (1, 12345678901234567890.123456789, 12345678901234567890123456789012345678, -12.5,
                  TO_BINARY_FLOAT('0.1'), TO_BINARY_DOUBLE('0.1') / 3,
                  TO_DATE('2026-11-13 14:05:10', 'YYYY-MM-DD HH24:MI:SS'),
                  TO_TIMESTAMP('2026-11-13 14:05:10.123456789', 'YYYY-MM-DD HH24:MI:SS.FF9'),
                  TO_TIMESTAMP_TZ('2026-07-01 09:00:00.000000001 Asia/Seoul', 'YYYY-MM-DD HH24:MI:SS.FF9 TZR'),
                  TO_TIMESTAMP('2026-11-13 14:05:10.987654321', 'YYYY-MM-DD HH24:MI:SS.FF9'),
                  TO_DSINTERVAL('+000000003 04:05:06.123456789'), TO_YMINTERVAL('-000000002-03'),
                  HEXTORAW('00FF10AB'), 'ab', 'it''s', N'가나다', N'한')
                """);
            await ExecAsync(s, $"INSERT INTO {L} (ID, BD, BF, TZ) VALUES (2, BINARY_DOUBLE_NAN, BINARY_FLOAT_INFINITY, "
                               + "TO_TIMESTAMP_TZ('2026-01-01 00:00:00.5 -03:30', 'YYYY-MM-DD HH24:MI:SS.FF9 TZH:TZM'))", tolerate: true);
            await ExecAsync(s, $"INSERT INTO {L} (ID, BD, D) VALUES (3, -BINARY_DOUBLE_INFINITY, TO_DATE('0001-01-01', 'YYYY-MM-DD'))", tolerate: true);

            const string Nls = "'NLS_NUMERIC_CHARACTERS=''.,'''";
            const string DateMask = "'SYYYY-MM-DD HH24:MI:SS'";
            const string TsMask = "'SYYYY-MM-DD HH24:MI:SS.FF9'";
            const string TzMask = "'SYYYY-MM-DD HH24:MI:SS.FF9 TZR'";
            var cases = new (int Id, string Column, string Read, Func<string, string> Literal)[]
            {
                (1, "N", $"TO_CHAR(N, 'TM9', {Nls})", v => v),
                (1, "N38", $"TO_CHAR(N38, 'TM9', {Nls})", v => v),
                (1, "NS", $"TO_CHAR(NS, 'TM9', {Nls})", v => v),
                (1, "BF", "TO_CHAR(BF)", v => $"TO_BINARY_FLOAT('{v}')"),
                (1, "BF", "BF", v => $"TO_BINARY_FLOAT('{v}')"),
                (1, "BF", "BF", v => $"TO_BINARY_DOUBLE('{v}')"),
                (1, "BD", "BD", v => $"TO_BINARY_DOUBLE('{v}')"),
                (1, "BD", "TO_CHAR(BD)", v => $"TO_BINARY_DOUBLE('{v}')"),
                (1, "D", $"TO_CHAR(D, {DateMask})", v => $"TO_DATE('{v}', {DateMask})"),
                (3, "D", $"TO_CHAR(D, {DateMask})", v => $"TO_DATE('{v}', {DateMask})"),
                (1, "TS", $"TO_CHAR(TS, {TsMask})", v => $"TO_TIMESTAMP('{v}', {TsMask})"),
                (1, "TZ", $"TO_CHAR(TZ, {TzMask})", v => $"TO_TIMESTAMP_TZ('{v}', {TzMask})"),
                (2, "TZ", $"TO_CHAR(TZ, {TzMask})", v => $"TO_TIMESTAMP_TZ('{v}', {TzMask})"),
                (1, "LTZ", $"TO_CHAR(SYS_EXTRACT_UTC(CAST(LTZ AS TIMESTAMP WITH TIME ZONE)), {TsMask})",
                    v => $"CAST(FROM_TZ(TO_TIMESTAMP('{v}', {TsMask}), 'UTC') AS TIMESTAMP WITH LOCAL TIME ZONE)"),
                (1, "IDS", "TO_CHAR(IDS)", v => $"TO_DSINTERVAL('{v}')"),
                (1, "IYM", "TO_CHAR(IYM)", v => $"TO_YMINTERVAL('{v}')"),
                (1, "R", "RAWTOHEX(R)", v => $"HEXTORAW('{v}')"),
                (1, "C", "C", v => Quote(v)),
                (1, "V", "V", v => Quote(v)),
                (1, "NV", "NV", v => $"UNISTR('{Unistr(v)}')"),
                (1, "NC", "NC", v => $"UNISTR('{Unistr(v)}')"),
            };

            foreach (var (id, column, read, literal) in cases)
            {
                string line;
                try
                {
                    var raw = await ScalarAsync(s, $"SELECT {read} FROM {L} WHERE ID = {id}");
                    var text = raw switch
                    {
                        double d => d.ToString("R", CultureInfo.InvariantCulture),
                        float f => f.ToString("R", CultureInfo.InvariantCulture),
                        null => null,
                        _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
                    };
                    var lit = text is null ? "NULL" : literal(text);
                    var count = await TryScalarAsync(s, $"SELECT COUNT(*) FROM {L} WHERE ID = {id} AND {column} = {lit}");
                    line = $"read={text} type={raw?.GetType().Name} literal={lit} count={count}";
                }
                catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
                {
                    line = "read error " + Code(ex);
                }

                output.WriteLine($"RB13 literal {column}#{id}: {line}");
            }

            // 특수 값 상수와 NaN 비교.
            output.WriteLine($"RB13 literal BD#2 NaN: count={await TryScalarAsync(s, $"SELECT COUNT(*) FROM {L} WHERE ID = 2 AND BD = BINARY_DOUBLE_NAN")} "
                             + $"read={await TryScalarAsync(s, $"SELECT TO_CHAR(BD) FROM {L} WHERE ID = 2")}");
            output.WriteLine($"RB13 literal BF#2 Inf: count={await TryScalarAsync(s, $"SELECT COUNT(*) FROM {L} WHERE ID = 2 AND BF = BINARY_FLOAT_INFINITY")}");
            output.WriteLine($"RB13 literal BD#3 -Inf: count={await TryScalarAsync(s, $"SELECT COUNT(*) FROM {L} WHERE ID = 3 AND BD = -BINARY_DOUBLE_INFINITY")}");
            output.WriteLine($"RB13 literal ROWID: {await RowIdRoundTripAsync(s)}");
            output.WriteLine($"RB13 large-null CASE: {await TryScalarAsync(s, $"SELECT CASE WHEN V IS NULL THEN 1 ELSE 0 END FROM {L} WHERE ID = 2")}");
            output.WriteLine($"RB13 unistr outside BMP: {await TryScalarAsync(s, "SELECT LENGTH(UNISTR('\\D83D\\DE00')) FROM DUAL")}");
        }
        finally
        {
            await CleanupAsync(s);
        }
    }

    [TiberoWriteFact]
    public async Task Flashback_errors_ddl_implicit_commit_identifier_length_and_dictionary()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var a = await it.OpenAsync();
        var b = await it.OpenAsync();
        await CleanupAsync(a);
        try
        {
            await ExecAsync(a, $"CREATE TABLE {T} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30))");
            await ExecAsync(a, $"INSERT INTO {T} VALUES (1, 'a')");
            await ExecAsync(a, "COMMIT");

            // 플래시백 오류 번호.
            var tsn = Convert.ToDecimal(await ScalarAsync(a, "SELECT TIMESTAMP_TO_TSN(SYSTIMESTAMP) FROM DUAL"), CultureInfo.InvariantCulture);
            output.WriteLine($"RB13 flashback asof now: {await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF TSN {tsn}")}");
            var exact = await TryScalarAsync(a, "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL");
            var current = await TryScalarAsync(a, "SELECT CURRENT_TSN FROM V$DATABASE");
            output.WriteLine($"RB13 flashback point methods: gscn-ts2tsn={(decimal.TryParse(exact, CultureInfo.InvariantCulture, out var e) ? (e - tsn).ToString(CultureInfo.InvariantCulture) : exact)} "
                             + $"vdb={(decimal.TryParse(current, CultureInfo.InvariantCulture, out var c) ? "ok" : current)}");
            if (decimal.TryParse(exact, CultureInfo.InvariantCulture, out var exactTsn))
            {
                output.WriteLine($"RB13 flashback asof gscn: {await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF TSN {exactTsn}")} scn={await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF SCN {exactTsn}")}");
                await Task.Delay(TimeSpan.FromSeconds(4));
                output.WriteLine($"RB13 flashback ts2tsn after 4s: diff={await TryScalarAsync(a, $"SELECT TIMESTAMP_TO_TSN(SYSTIMESTAMP) - {exactTsn} FROM DUAL")} asof={await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF TSN TIMESTAMP_TO_TSN(SYSTIMESTAMP)")}");
            }
            output.WriteLine($"RB13 flashback future: {await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF TSN {tsn + 1_000_000_000m}")}");
            output.WriteLine($"RB13 flashback tsn 1: {await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF TSN 1")}");
            output.WriteLine($"RB13 flashback negative: {await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF TSN -5")}");
            await ExecAsync(a, $"ALTER TABLE {T} ADD (MEMO VARCHAR2(10))");
            output.WriteLine($"RB13 flashback after ddl: {await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} AS OF TSN {tsn}")}");
            output.WriteLine($"RB13 flashback versions: {await TryScalarAsync(a, $"SELECT COUNT(*) FROM {T} VERSIONS BETWEEN TSN MINVALUE AND MAXVALUE")}");

            // DDL 암묵 커밋: 성공 DDL·실패 DDL 앞의 INSERT 가 다른 세션에 보이는가, 트랜잭션 id 가 남는가.
            await ExecAsync(a, $"INSERT INTO {T} (ID, NAME) VALUES (2, 'b')");
            var ddlOk = await TryAsync(a, $"CREATE TABLE {X} (A NUMBER)");
            output.WriteLine($"RB13 ddl ok: ddl={ddlOk} txId={(await ScalarTextAsync(a, TiberoExecutionService.TransactionIdSql) is null ? "NULL" : "set")} "
                             + $"otherSees={await ScalarTextAsync(b, $"SELECT COUNT(*) FROM {T} WHERE ID = 2")}");
            await ExecAsync(a, $"INSERT INTO {T} (ID, NAME) VALUES (3, 'c')");
            var ddlFail = await TryAsync(a, $"CREATE TABLE {X} (A NUMBER)"); // 이미 있음
            output.WriteLine($"RB13 ddl failed: ddl={ddlFail} txId={(await ScalarTextAsync(a, TiberoExecutionService.TransactionIdSql) is null ? "NULL" : "set")} "
                             + $"otherSees={await ScalarTextAsync(b, $"SELECT COUNT(*) FROM {T} WHERE ID = 3")}");
            foreach (var (id, ddl) in new[] { (4, "CREATE TABLE ORB_RB_Y (A NUMBERX)"), (5, "CREATE TABLEX ORB_RB_Y (A NUMBER)"), (6, $"DROP TABLE ORB_RB_NONE") })
            {
                await ExecAsync(a, $"INSERT INTO {T} (ID, NAME) VALUES ({id}, 'p')");
                var result = await TryAsync(a, ddl);
                output.WriteLine($"RB13 ddl error #{id}: ddl={result} txId={(await ScalarTextAsync(a, TiberoExecutionService.TransactionIdSql) is null ? "NULL" : "set")} "
                                 + $"otherSees={await ScalarTextAsync(b, $"SELECT COUNT(*) FROM {T} WHERE ID = {id}")}");
            }
            await ExecAsync(a, "ROLLBACK");

            // 트랜잭션 id 모양(숫자만 지움).
            await ExecAsync(a, $"UPDATE {T} SET NAME = 'z' WHERE ID = 1");
            var txid = await ScalarTextAsync(a, TiberoExecutionService.TransactionIdSql);
            output.WriteLine($"RB13 txid shape: {(txid is null ? "NULL" : new string([.. txid.Select(ch => char.IsDigit(ch) ? '9' : ch)]))}");
            await ExecAsync(a, "ROLLBACK");

            // 식별자 길이(바이트). ORB_RB_ 로 시작하는 이름만.
            foreach (var length in new[] { 30, 31, 64, 128, 129 })
            {
                var name = "ORB_RB_" + new string('I', length - "ORB_RB_".Length);
                var made = await TryAsync(a, $"CREATE TABLE {name} (A NUMBER)");
                if (made == "ok")
                {
                    await TryAsync(a, $"DROP TABLE {name} PURGE");
                }

                output.WriteLine($"RB13 identifier {length}: {made}");
            }

            // 사전 열.
            output.WriteLine("RB13 dict ALL_TRIGGERS: " + await TryScalarAsync(a,
                "SELECT COUNT(*) FROM ALL_TRIGGERS WHERE TABLE_OWNER = USER AND TABLE_NAME = 'ORB_RB_T' AND STATUS = 'ENABLED' AND TRIGGERING_EVENT IS NOT NULL AND TRIGGER_TYPE IS NOT NULL"));
            output.WriteLine("RB13 dict ALL_CONSTRAINTS.DELETE_RULE: " + await TryScalarAsync(a,
                "SELECT COUNT(*) FROM ALL_CONSTRAINTS WHERE OWNER = USER AND CONSTRAINT_TYPE = 'R' AND DELETE_RULE IN ('CASCADE', 'SET NULL')"));
            output.WriteLine("RB13 dict ALL_TAB_COLS virtual/hidden/identity: " + await TryScalarAsync(a,
                "SELECT COUNT(*) FROM ALL_TAB_COLS WHERE OWNER = USER AND TABLE_NAME = 'ORB_RB_T' AND VIRTUAL_COLUMN = 'NO' AND HIDDEN_COLUMN = 'NO'"));
            output.WriteLine("RB13 dict ALL_TAB_COLS values: " + await TryScalarAsync(a,
                "SELECT COUNT(*) || ':' || MIN(VIRTUAL_COLUMN) || '/' || MIN(HIDDEN_COLUMN) FROM ALL_TAB_COLS WHERE OWNER = USER AND TABLE_NAME = 'ORB_RB_T'"));
            output.WriteLine("RB13 virtual column ddl: " + await TryAsync(a, $"CREATE TABLE {X}2 (A NUMBER, B NUMBER GENERATED ALWAYS AS (A * 2) VIRTUAL)")
                             + " short=" + await TryAsync(a, $"CREATE TABLE {X}3 (A NUMBER, B AS (A * 2))"));
            output.WriteLine("RB13 virtual column dict: " + await TryScalarAsync(a,
                $"SELECT COUNT(*) || ':' || MAX(VIRTUAL_COLUMN) FROM ALL_TAB_COLS WHERE OWNER = USER AND TABLE_NAME IN ('{X}2', '{X}3') AND COLUMN_NAME = 'B'"));
            output.WriteLine("RB13 invisible column ddl: " + await TryAsync(a, $"CREATE TABLE {X}4 (A NUMBER, B NUMBER INVISIBLE)"));
            output.WriteLine("RB13 dict ALL_TAB_COLS IDENTITY_COLUMN: " + await TryScalarAsync(a,
                "SELECT COUNT(IDENTITY_COLUMN) FROM ALL_TAB_COLS WHERE OWNER = USER AND TABLE_NAME = 'ORB_RB_T'"));
            output.WriteLine("RB13 dict ALL_TAB_IDENTITY_COLS: " + await TryScalarAsync(a, "SELECT COUNT(*) FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = USER"));
        }
        finally
        {
            await CleanupAsync(a);
        }
    }

    [TiberoWriteFact]
    public async Task Side_effects_query_finds_the_trigger_and_cascade_fk_and_as_of_tsn_reads_the_earlier_rows()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var s = await it.OpenAsync();
        await CleanupAsync(s);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {T} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(30))");
            await ExecAsync(s, $"CREATE TABLE {X} (ID NUMBER PRIMARY KEY, T_ID NUMBER CONSTRAINT ORB_RB_X_FK REFERENCES {T} (ID) ON DELETE CASCADE)");
            await ExecAsync(s, $"CREATE OR REPLACE TRIGGER ORB_RB_TRG BEFORE UPDATE ON {T} FOR EACH ROW BEGIN NULL; END;");
            await ExecAsync(s, $"INSERT INTO {T} VALUES (1, 'a')");
            await ExecAsync(s, "COMMIT");

            var owner = (await ScalarTextAsync(s, "SELECT USER FROM DUAL"))!;
            var rows = await OnGateAsync(s, async cmd =>
            {
                cmd.CommandText = Oranda.Tibero.Rollback.TiberoRollbackValues.SideEffectsSql;
                var driver = s.Driver!;
                cmd.Parameters.Add(Oranda.Tibero.Results.TiberoBindBuilder.Create(driver, cmd, "owner", new BindValue(BindDataType.Varchar2, owner)));
                cmd.Parameters.Add(Oranda.Tibero.Results.TiberoBindBuilder.Create(driver, cmd, "name", new BindValue(BindDataType.Varchar2, T)));
                await using var reader = await cmd.ExecuteReaderAsync();
                var found = new List<string>();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(1);
                    found.Add(reader.GetString(0) + " " + name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..]); // 계정 이름은 싣지 않는다
                }

                return found;
            });
            output.WriteLine("RB13 side effects: " + string.Join(" | ", rows));
            Assert.Contains("TRIGGER ORB_RB_TRG", rows);
            Assert.Contains($"FK CASCADE {X}.ORB_RB_X_FK", rows);

            var point = Convert.ToInt64(await ScalarAsync(s, Oranda.Tibero.Rollback.TiberoRollbackValues.PointQueries[0]), CultureInfo.InvariantCulture);
            await ExecAsync(s, $"UPDATE {T} SET NAME = 'b' WHERE ID = 1");
            await ExecAsync(s, "COMMIT");
            var before = await ScalarTextAsync(s, $"SELECT NAME FROM {T} {Oranda.Tibero.Rollback.TiberoRollbackValues.AsOf(point)} WHERE ID = 1");
            output.WriteLine($"RB13 as of tsn: before={before}");
            Assert.Equal("a", before);
        }
        finally
        {
            await CleanupAsync(s);
        }
    }

    private static async Task<string> RowIdRoundTripAsync(TiberoSession s)
    {
        try
        {
            var rid = await ScalarTextAsync(s, $"SELECT ROWIDTOCHAR(ROWID) FROM {L} WHERE ID = 1");
            var count = await TryScalarAsync(s, $"SELECT COUNT(*) FROM {L} WHERE ROWID = CHARTOROWID('{rid}')");
            var inList = await TryScalarAsync(s, $"SELECT COUNT(*) FROM {L} WHERE ROWID IN (CHARTOROWID('{rid}'), CHARTOROWID('{rid}'))");
            return $"len={rid?.Length} count={count} inList={inList}";
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            return "error " + Code(ex);
        }
    }

    private static string Quote(string v) => "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Unistr(string v)
    {
        var sb = new StringBuilder();
        foreach (var ch in v)
        {
            if (ch < 0x80 && ch != '\\' && ch != '\'')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('\\').Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static string Code(Exception ex) => TiberoErrorClassifier.ToDbError(ex, null) is { } e ? $"{e.Code}/{e.Kind}" : ex.GetType().Name;


    /// <summary>문장 하나(명령 잠금 안, 이 세션 연결). 영향 행 수.</summary>
    private static async Task<int> ExecAsync(TiberoSession s, string sql, bool tolerate = false)
    {
        try
        {
            return await OnGateAsync(s, async cmd =>
            {
                cmd.CommandText = sql;
                return await cmd.ExecuteNonQueryAsync();
            });
        }
        catch (Exception ex) when (tolerate && TiberoErrorClassifier.IsDriverException(ex))
        {
            return -1;
        }
    }

    private static async Task<string> TryAsync(TiberoSession s, string sql)
    {
        try
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                await QueryCountAsync(s, sql);
            }
            else
            {
                await ExecAsync(s, sql);
            }

            return "ok";
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            return Code(ex);
        }
    }

    /// <summary>리더를 끝까지 읽고 닫는다. 행 수.</summary>
    private static Task<int> QueryCountAsync(TiberoSession s, string sql) => OnGateAsync(s, async cmd =>
    {
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var n = 0;
        while (await reader.ReadAsync())
        {
            n++;
        }

        return n;
    });

    private static Task<object?> ScalarAsync(TiberoSession s, string sql) => OnGateAsync(s, async cmd =>
    {
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0))
        {
            return (object?)null;
        }

        var type = reader.GetFieldType(0);
        return type == typeof(double) ? reader.GetDouble(0) : type == typeof(float) ? reader.GetFloat(0) : reader.GetValue(0);
    });

    private static async Task<string?> ScalarTextAsync(TiberoSession s, string sql) =>
        await ScalarAsync(s, sql) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private static async Task<string> TryScalarAsync(TiberoSession s, string sql)
    {
        try
        {
            return await ScalarTextAsync(s, sql) ?? "NULL";
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            return "error " + Code(ex);
        }
    }

    private static async Task<TResult> OnGateAsync<TResult>(TiberoSession s, Func<DbCommand, Task<TResult>> run)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand();
            return await Task.Run(() => run(cmd));
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private static async Task CleanupAsync(TiberoSession s)
    {
        await TryAsync(s, "ROLLBACK");
        await TryAsync(s, $"DROP TABLE {X} PURGE"); // 자식(FK) 먼저
        foreach (var table in Tables.Concat(["ORB_RB_Y", X + "2", X + "3", X + "4"]))
        {
            await TryAsync(s, $"DROP TABLE {table} PURGE");
        }
    }

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
}
