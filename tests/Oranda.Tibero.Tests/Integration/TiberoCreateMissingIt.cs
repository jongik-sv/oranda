using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero → Tibero 「없으면 만들기」(xdb-copy XC8b) 통합 시험: 두 번째 Tibero 계정이 있어야 한다(같은 서버·DB, 다른 계정).
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_WRITE=1</c> + 첫 계정 <c>ORANDA_IT_TIBERO_*</c> + 두 번째 계정 <c>ORANDA_IT_TIBERO_USER2</c>·<c>ORANDA_IT_TIBERO_PASSWORD2</c>
/// (주소·포트·DB 는 첫 계정과 같다). 하나라도 없으면 건너뛴다(<c>ORANDA_IT_REQUIRE=1</c> 이면 본문이 던진다).</item>
/// <item>시나리오: 첫 계정에 <c>ORT_</c> 원본 표 둘(PK 있음·없음; CHECK·DEFAULT·일반 인덱스·표/열 주석; 글자·숫자·DATE·TIMESTAMP·LOB)을 만들고 행을 넣은 뒤,
/// 두 번째 계정으로 「비우고 넣기 + 없으면 만들기」로 복사한다. 사전 점검이 만들기를 막지 않고, 표가 만들어지고, 값·제약·기본값·인덱스·주석이 따라오는지 본다.
/// 두 번째 실행(표가 이미 있음)은 대상의 남은 행을 비우고 다시 넣는다.</item>
/// <item>끝에 두 계정 모두 <c>DROP TABLE … PURGE</c>. 접속 값(계정·주소·DB)은 단언 메시지에 넣지 않는다 — 점검 항목은 코드만 싣는다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCreateMissingIt(ITestOutputHelper output)
{
    private static readonly string TableA = TiberoItSessions.ObjectName("CM_A");
    private static readonly string TableB = TiberoItSessions.ObjectName("CM_B");

    [TiberoCreateMissingFact]
    public async Task Two_accounts_create_if_missing_with_delete_all_insert_creates_loads_and_keeps_structure_then_reruns_on_the_existing_tables()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var user2 = Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_USER2");
        var password2 = Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_PASSWORD2");
        if (string.IsNullOrEmpty(user2) || string.IsNullOrEmpty(password2) || TiberoItSessions.SkipWrite())
        {
            Assert.False(it.Env.RequireMode, "the second account (ORANDA_IT_TIBERO_USER2/PASSWORD2) and the write switch are required");
            return;
        }

        var opened = new List<ISession>();
        var setup = await it.OpenAsync();
        TiberoSession? verify = null;
        try
        {
            verify = await OpenSecondAsync(it, user2, password2, SessionPurpose.Work, readOnly: false);
            opened.Add(verify);

            // 대상 계정에 DROP 을 막는 DDL 트리거가 있으면 만든 표를 지울 수 없다(#141: <owner>_NO_DROP_TRUNCATE, ODP-20102 / TBR-15163).
            // 트리거를 끄거나 돌아가지 않고, 표를 만들기 전에 이유를 남기고 건너뛴다(결정 59).
            if (await DropGuardAsync(verify) is { } guard)
            {
                output.WriteLine("XC8b skipped: the target account has a DDL trigger that refuses DROP — " + guard);
                return;
            }

            // 앞 실행이 남긴 표(두 계정, 이 시험이 만드는 이름만)를 먼저 지운다. 남으면 만들기 시나리오가 성립하지 않으므로 지우기 오류를 싣고 멈춘다.
            var dropFailures = await DropBothAsync(setup, verify);
            if (await LeftoverAsync(setup) + await LeftoverAsync(verify) > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                dropFailures = await DropBothAsync(setup, verify);
            }

            // 카탈로그로 못 본 트리거라도 지우기가 트리거 오류로 거부되면 같은 이유로 건너뛴다.
            if (dropFailures.FirstOrDefault(IsTriggerRefusal) is { } refused)
            {
                output.WriteLine("XC8b skipped: DROP was refused by a DDL trigger — " + refused);
                return;
            }

            Assert.True(await LeftoverAsync(setup) + await LeftoverAsync(verify) == 0,
                "tables left by an earlier run could not be dropped: " + string.Join(" | ", dropFailures));

            await CreateSourcesAsync(setup);
            await Task.Delay(TimeSpan.FromSeconds(2)); // 방금 만든 표를 읽기 전용 트랜잭션이 읽을 수 있도록 DDL 시각을 지나게 한다.

            // ---- 1회차: 대상 계정에 표가 없다 → 만들고 넣는다 ----
            var first = await CopyAsync(it, user2, password2, opened, expectCreate: true);
            Assert.Equal(CopyOutcome.Succeeded, first.Outcome);
            Assert.True(first.Tables.Count == 2 && first.Tables.All(t => t.Outcome == CopyOutcome.Succeeded), "first run: both tables must succeed");

            await AssertSameDataAsync(setup, verify, TableA, FingerprintA);
            await AssertSameDataAsync(setup, verify, TableB, FingerprintB);
            await AssertStructureAsync(setup, verify);

            // ---- 2회차: 표가 이미 있다 → 만들지 않고, 대상의 남은 행을 비우고 다시 넣는다 ----
            await ExecAsync(verify, $"INSERT INTO {TableB} (ID, NM, AMT) VALUES (999, 'stray', 1)");
            await ExecAsync(verify, "COMMIT");
            Assert.Equal(4L, await CountAsync(verify, TableB));

            var second = await CopyAsync(it, user2, password2, opened, expectCreate: false);
            Assert.Equal(CopyOutcome.Succeeded, second.Outcome);
            Assert.Equal(3L, await CountAsync(verify, TableB)); // 999 는 지워졌다
            await AssertSameDataAsync(setup, verify, TableA, FingerprintA);
            await AssertSameDataAsync(setup, verify, TableB, FingerprintB);
        }
        finally
        {
            // 복사에 쓴 대상 세션(두 번째 계정)이 열려 있으면 DROP 이 막혀 표가 남는다(#138: 두 번째 계정에 ORT_CM_A·B 가 남음).
            // 그 세션들을 먼저 닫고, 확인용 세션으로 두 계정을 지운 뒤 남은 표가 있는지 보고 한 번 더 지운다.
            foreach (var session in opened.Where(o => !ReferenceEquals(o, verify)))
            {
                await session.DisposeAsync();
            }

            await DropBothAsync(setup, verify);
            if (await LeftoverAsync(setup) + (verify is null ? 0 : await LeftoverAsync(verify)) > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await DropBothAsync(setup, verify);
            }

            if (verify is not null)
            {
                await verify.DisposeAsync();
            }
        }
    }

    /// <summary>이 시험이 만드는 표(<see cref="TableA"/>·<see cref="TableB"/>)가 세션 계정에 몇 개 남았는가. 읽지 못하면 0(정리를 막지 않는다).</summary>
    /// <summary>
    /// 세션 계정에 걸리는 켜진 스키마·DB 단위 DDL 트리거(DROP·TRUNCATE·DDL 사건, 표 트리거 아님) 이름과 사건. 없거나 카탈로그를 못 읽으면 null.
    /// Tibero ALL_TRIGGERS 에는 BASE_OBJECT_TYPE 이 없어(-8026) 표 트리거가 아닌 것(TABLE_NAME 없음)으로 가른다.
    /// </summary>
    private static async Task<string?> DropGuardAsync(TiberoSession session)
    {
        try
        {
            var rows = (await TiberoSessionQuery.RunAsync(session,
                "SELECT TRIGGER_NAME, TRIGGERING_EVENT FROM ALL_TRIGGERS WHERE TABLE_NAME IS NULL AND STATUS = 'ENABLED'"
                + " AND (UPPER(TRIGGERING_EVENT) LIKE '%DROP%' OR UPPER(TRIGGERING_EVENT) LIKE '%TRUNCATE%' OR UPPER(TRIGGERING_EVENT) LIKE '%DDL%')")).Rows;
            if (rows.Count == 0)
            {
                return null;
            }

            // 트리거 이름에 계정 이름이 들어갈 수 있다(<owner>_NO_DROP_TRUNCATE) — 출력에서 가린다.
            var owner = session.Server.SessionUser;
            var text = string.Join(", ", rows.Select(r => $"{TiberoSessionQuery.Str(r[0])}({TiberoSessionQuery.Str(r[1])})"));
            return string.IsNullOrEmpty(owner) ? text : text.Replace(owner, "<owner>", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>지우기 실패 글이 DDL 트리거의 거부(사용자 정의 오류: ODP-20102 / TBR-15163)인가.</summary>
    private static bool IsTriggerRefusal(string failure) =>
        failure.Contains("20102", StringComparison.Ordinal) || failure.Contains("15163", StringComparison.Ordinal);

    private static Task<long> LeftoverAsync(TiberoSession session) => LeftoverAsync(session, null);

    /// <summary><paramref name="table"/> 하나(null 이면 둘 다)가 세션 계정에 남았는가. 읽지 못하면 0.</summary>
    private static async Task<long> LeftoverAsync(TiberoSession session, string? table)
    {
        var names = table is null ? $"'{TableA}', '{TableB}'" : $"'{table}'";
        try
        {
            return await ScalarAsync(session, $"SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME IN ({names})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    // ---- 복사 ----

    private async Task<CopyRunResult> CopyAsync(TiberoItSessions it, string user2, string password2, List<ISession> opened, bool expectCreate)
    {
        var source = await it.OpenAsync(SessionPurpose.Dedicated, it.Profile() with { ReadOnly = true });
        opened.Add(source);
        var target = await OpenSecondAsync(it, user2, password2, SessionPurpose.Dedicated, readOnly: false);
        opened.Add(target);
        var sessions = new CopySessions(source, target);
        var sourceOwner = source.Server.SessionUser;
        var targetOwner = target.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(sourceOwner) || string.IsNullOrEmpty(targetOwner), "the session users are unknown");

        var job = new CopyJob
        {
            Id = "xc8b-it", Name = "xc8b-it", Source = new ConnectionRef("a", sourceOwner!), Target = new ConnectionRef("b", targetOwner!),
            SourceOwner = sourceOwner!, TargetOwner = targetOwner!, MaskSalt = "salt",
            Tables =
            [
                new TableRule(TableA, RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true },
                new TableRule(TableB, RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true },
            ],
        };
        var period = new ResolvedPeriod(null, null);
        var policy = new AllowAll();
        var bulk = new TiberoBulkExecutionService(policy, new NoQuestions(), it.Events);
        var execution = new TiberoExecutionService(policy, new NoQuestions(), it.Events, null, dbmsOutput: false);

        var precheck = new TiberoCopyPrecheckService(bulk, new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync), policy);
        var report = await precheck.RunAsync(sessions, job, period, null, CancellationToken.None);
        var codes = string.Join(",", report.Items.Select(i => $"{i.Code}:{i.Level}"));
        output.WriteLine("XC8b precheck items: " + codes);
        Assert.False(report.Items.Any(i => i.Code == CopyCheckCodes.CreateBlocked), "precheck must not block the creation, items: " + codes);
        Assert.False(report.HasErrors, "precheck has errors, items: " + codes);
        Assert.Equal(2, report.Order.Count);
        Assert.All(report.Order, p => Assert.Equal(expectCreate, p.CreateMissing));
        Assert.Equal(expectCreate, report.Items.Any(i => i.Code == CopyCheckCodes.CreateTable));
        if (expectCreate)
        {
            Assert.Contains(report.Preview, p => p.Sql.StartsWith("CREATE TABLE", StringComparison.Ordinal) && p.Side == PreviewSide.Target);
            Assert.DoesNotContain(report.Preview, p => p.Sql.StartsWith("DELETE FROM", StringComparison.Ordinal)); // 새 표는 지우지 않는다
        }

        var runner = new TiberoCopyRunner(bulk, execution, new MemoryRunLog());
        var result = await runner.RunAsync(sessions, job, report, NoDecisions(), period, null, new CopyRunControl(), CancellationToken.None);
        output.WriteLine("XC8b run outcome: " + result.Outcome + ", tables: " + string.Join(",", result.Tables.Select(t => $"{t.Table}:{t.SourceRows}->{t.TargetRows}:{t.Outcome}")));
        return result;
    }

    // ---- 원본 ----

    private static async Task CreateSourcesAsync(TiberoSession s)
    {
        await ExecAsync(s, $"CREATE TABLE {TableA} (ID NUMBER(10) NOT NULL, NM VARCHAR2(20 CHAR) DEFAULT 'x' NOT NULL, CD CHAR(4 BYTE), AMT NUMBER(12,2),"
                           + $" DT DATE, TS TIMESTAMP(6), DOC CLOB, CONSTRAINT {TableA}_PK PRIMARY KEY (ID), CONSTRAINT {TableA}_CK CHECK (AMT >= 0))");
        await ExecAsync(s, $"CREATE INDEX {TableA}_IX ON {TableA} (NM)");
        await ExecAsync(s, $"COMMENT ON TABLE {TableA} IS 'create missing A'");
        await ExecAsync(s, $"COMMENT ON COLUMN {TableA}.NM IS 'name of A'");
        await ExecAsync(s, $"INSERT INTO {TableA} (ID, NM, CD, AMT, DT, TS, DOC) VALUES (1, 'alpha', 'ab', 10.5, DATE '2026-11-01', TIMESTAMP '2026-11-01 10:20:30.123456', TO_CLOB('clob one'))");
        await ExecAsync(s, $"INSERT INTO {TableA} (ID, NM, CD, AMT, DT, TS, DOC) VALUES (2, '한글이름', 'cd', 0, NULL, NULL, NULL)");
        await ExecAsync(s, $"INSERT INTO {TableA} (ID, AMT) VALUES (3, 7.25)"); // NM 은 DEFAULT 'x'

        await ExecAsync(s, $"CREATE TABLE {TableB} (ID NUMBER(10), NM VARCHAR2(20 BYTE) DEFAULT 'y', AMT NUMBER(12,2), DT DATE, TS TIMESTAMP(6), BL BLOB,"
                           + $" CONSTRAINT {TableB}_CK CHECK (AMT >= 0))");
        await ExecAsync(s, $"CREATE INDEX {TableB}_IX ON {TableB} (ID, NM)");
        await ExecAsync(s, $"COMMENT ON TABLE {TableB} IS 'create missing B'");
        await ExecAsync(s, $"COMMENT ON COLUMN {TableB}.AMT IS 'amount of B'");
        await ExecAsync(s, $"INSERT INTO {TableB} (ID, NM, AMT, DT, TS, BL) VALUES (1, 'one', 1.5, DATE '2026-11-02', TIMESTAMP '2026-11-02 01:02:03.5', TO_BLOB(HEXTORAW('00FF10')))");
        await ExecAsync(s, $"INSERT INTO {TableB} (ID, NM, AMT) VALUES (2, 'two', 2)");
        await ExecAsync(s, $"INSERT INTO {TableB} (ID, NM, AMT) VALUES (2, 'two', 2)"); // PK 가 없어 같은 행이 겹쳐도 들어간다
        await ExecAsync(s, "COMMIT");
    }

    // ---- 확인 ----

    private const string FingerprintA =
        "SELECT ID || '|' || NM || '|' || CD || '|' || TO_CHAR(AMT) || '|' || TO_CHAR(DT, 'YYYY-MM-DD HH24:MI:SS') || '|' || TO_CHAR(TS, 'YYYY-MM-DD HH24:MI:SS.FF6')"
        + " || '|' || DBMS_LOB.GETLENGTH(DOC) FROM {0} ORDER BY ID";

    private const string FingerprintB =
        "SELECT ID || '|' || NM || '|' || TO_CHAR(AMT) || '|' || TO_CHAR(DT, 'YYYY-MM-DD HH24:MI:SS') || '|' || TO_CHAR(TS, 'YYYY-MM-DD HH24:MI:SS.FF6')"
        + " || '|' || DBMS_LOB.GETLENGTH(BL) FROM {0} ORDER BY ID, NM";

    /// <summary>두 계정의 같은 이름 표를 같은 질의로 읽어 행이 같은지 본다(질의 결과 글을 비교한다).</summary>
    private static async Task AssertSameDataAsync(TiberoSession source, TiberoSession target, string table, string queryFormat)
    {
        var sql = string.Format(CultureInfo.InvariantCulture, queryFormat, table);
        var expected = (await TiberoSessionQuery.RunAsync(source, sql)).Rows.Select(r => TiberoSessionQuery.Str(r[0])).ToList();
        var actual = (await TiberoSessionQuery.RunAsync(target, sql)).Rows.Select(r => TiberoSessionQuery.Str(r[0])).ToList();
        Assert.True(expected.Count > 0, table + ": the source must have rows");
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"{table}: rows differ. expected [{string.Join(" ; ", expected)}] actual [{string.Join(" ; ", actual)}]");
    }

    /// <summary>만들어진 표(두 번째 계정)가 원본(첫 계정)의 구조를 따라간다: 열 형식·길이·NOT NULL, PK·CHECK·DEFAULT·인덱스·주석.</summary>
    private static async Task AssertStructureAsync(TiberoSession source, TiberoSession target)
    {
        foreach (var table in new[] { TableA, TableB })
        {
            // 열: 이름·형식·글자 길이·정밀도·눈금·NOT NULL 이 같다(바이트 길이·CHAR_USED 는 뺀다 — BYTE 열이 글자 단위로 만들어지는 것은 알려진 차이).
            var columns = $"SELECT column_name || ':' || data_type || ':' || NVL(TO_CHAR(char_length), '-') || ':' || NVL(TO_CHAR(data_precision), '-') || ':' || NVL(TO_CHAR(data_scale), '-')"
                          + $" || ':' || nullable FROM user_tab_columns WHERE table_name = '{table}' ORDER BY column_id";
            var expected = await TextsAsync(source, columns);
            var actual = await TextsAsync(target, columns);
            Assert.True(expected.Count > 0 && expected.SequenceEqual(actual, StringComparer.Ordinal),
                $"{table}: columns differ. expected [{string.Join(" ; ", expected)}] actual [{string.Join(" ; ", actual)}]");

            // CHECK(이름 있는 제약은 이름 그대로)·인덱스·주석.
            Assert.Equal(1L, await ScalarAsync(target, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{table}' AND constraint_name = '{table}_CK' AND constraint_type = 'C'"));
            Assert.Equal(1L, await ScalarAsync(target, $"SELECT COUNT(*) FROM user_indexes WHERE table_name = '{table}' AND index_name = '{table}_IX'"));
            Assert.Equal(await TextsAsync(source, $"SELECT comments FROM user_tab_comments WHERE table_name = '{table}'"),
                await TextsAsync(target, $"SELECT comments FROM user_tab_comments WHERE table_name = '{table}'"));
            var commented = $"SELECT column_name || ':' || comments FROM user_col_comments WHERE table_name = '{table}' AND comments IS NOT NULL ORDER BY column_name";
            var expectedComments = await TextsAsync(source, commented);
            Assert.NotEmpty(expectedComments);
            Assert.Equal(expectedComments, await TextsAsync(target, commented));
        }

        // PK 는 PK 가 있는 표(A)에만 있고 이름 그대로 만들어진다.
        Assert.Equal(1L, await ScalarAsync(target, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{TableA}' AND constraint_name = '{TableA}_PK' AND constraint_type = 'P'"));
        Assert.Equal(0L, await ScalarAsync(target, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{TableB}' AND constraint_type = 'P'"));

        // DEFAULT.
        Assert.Equal("'x'", (await TextsAsync(target, $"SELECT data_default FROM user_tab_columns WHERE table_name = '{TableA}' AND column_name = 'NM'")).Single()?.Trim());
        Assert.Equal("'y'", (await TextsAsync(target, $"SELECT data_default FROM user_tab_columns WHERE table_name = '{TableB}' AND column_name = 'NM'")).Single()?.Trim());

        // CHECK 는 실제로 동작한다(음수 AMT 거부).
        var rejected = await Assert.ThrowsAnyAsync<Exception>(() => ExecAsync(target, $"INSERT INTO {TableA} (ID, AMT) VALUES (100, -1)"));
        Assert.NotNull(rejected);
        await ExecAsync(target, "ROLLBACK");
    }

    // ---- 도우미 ----

    private static async Task<TiberoSession> OpenSecondAsync(TiberoItSessions it, string user, string password, SessionPurpose purpose, bool readOnly)
    {
        var profile = new ConnectionProfile("it2", user, $"{it.Env.Host}:{it.Env.Port}/{it.Env.DbName}", ConnectTimeoutSeconds: 15, ReadOnly: readOnly, Dbms: Dbms.Tibero);
        using var credential = new SecureCredential(password);
        return (TiberoSession)await it.Manager.OpenAsync(profile, credential, purpose, CancellationToken.None);
    }

    /// <summary>
    /// 두 계정에서 이 시험이 만드는 표만 지운다(소유자를 붙여 그 세션 계정의 표만). 남은 표가 지우기에 실패하면(#139: 두 번째 계정에 ORT_CM_A·B 가 남음)
    /// 그 오류를 시험 출력에 남긴다 — 계정 이름은 가린다. 돌려주는 값 = 지우기 실패 글(없으면 빈 목록).
    /// </summary>
    private async Task<List<string>> DropBothAsync(TiberoSession first, TiberoSession? second)
    {
        var failures = new List<string>();
        foreach (var session in new[] { first, second })
        {
            if (session is null)
            {
                continue;
            }

            await SilentAsync(session, "ROLLBACK");
            var owner = session.Server.SessionUser;
            foreach (var table in new[] { TableA, TableB })
            {
                var name = string.IsNullOrEmpty(owner) ? table : $"\"{owner}\".{table}";
                try
                {
                    await ExecAsync(session, $"DROP TABLE {name} CASCADE CONSTRAINTS PURGE");
                }
#pragma warning disable CA1031 // 정리 단계: 없는 표 지우기 실패는 무시하고, 남은 표가 있으면 그 오류를 남긴다.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    if (await LeftoverAsync(session, table) > 0)
                    {
                        var text = string.IsNullOrEmpty(owner) ? ex.Message : ex.Message.Replace(owner, "<owner>", StringComparison.OrdinalIgnoreCase);
                        failures.Add($"{table}: {text}");
                    }
                }
            }
        }

        foreach (var failure in failures)
        {
            output.WriteLine("XC8b cleanup: drop failed " + failure);
        }

        return failures;
    }

    private static async Task<List<string?>> TextsAsync(TiberoSession s, string sql) =>
        [.. (await TiberoSessionQuery.RunAsync(s, sql)).Rows.Select(r => TiberoSessionQuery.Str(r[0]))];

    private static async Task<long> ScalarAsync(TiberoSession s, string sql) =>
        long.Parse((await TextsAsync(s, sql)).Single()!, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static Task<long> CountAsync(TiberoSession s, string table) => ScalarAsync(s, $"SELECT COUNT(*) FROM {table}");

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private static async Task SilentAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
#pragma warning disable CA1031 // 정리 단계: 없는 객체 지우기 실패는 무시한다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static CopyDecisions NoDecisions() =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(), new HashSet<string>(StringComparer.Ordinal), false);

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }

    /// <summary>실행 기록을 버리는 가짜.</summary>
    private sealed class MemoryRunLog : ICopyRunLog
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

/// <summary>두 번째 Tibero 계정(<c>ORANDA_IT_TIBERO_USER2</c>·<c>PASSWORD2</c>)과 쓰기 시험 조건이 있을 때만 도는 시험(<see cref="TiberoItGate"/>).</summary>
public sealed class TiberoCreateMissingFactAttribute : FactAttribute
{
    public TiberoCreateMissingFactAttribute()
    {
        Skip = TiberoItGate.Reason(write: true, kill: false)
               ?? (Environment.GetEnvironmentVariable("ORANDA_IT_REQUIRE") == "1" || SecondAccountConfigured()
                   ? null
                   : "두 번째 계정 환경 변수(ORANDA_IT_TIBERO_USER2, ORANDA_IT_TIBERO_PASSWORD2)가 없어 건너뜀.");
    }

    private static bool SecondAccountConfigured() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_USER2"))
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_PASSWORD2"));
}
