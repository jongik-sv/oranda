using System.Diagnostics;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Execution;

// 소유: plsql-compile-hang 회차 phc-it 레인(측정). 다른 세션이 실행 중인 프로시저를 CREATE OR REPLACE 하면 컴파일이
// library cache pin/lock 을 기다린다. 그 대기를 앱의 실제 실행 경로(OracleExecutionService + 토큰 취소 = OracleCommand.Cancel)가
// 끊는지, 취소 뒤 세션을 다시 쓸 수 있는지, 대기 중인 세션 뒤에 다른 실행이 줄 서는지, 그냥 닫으면 서버 세션이 얼마나 남는지를 잰다.
// 상주 컨테이너(oracle-26ai-free, 11g 는 oranda-ora11g)를 쓴다. 환경 변수 = SafetyDbFixture 와 같다(ORANDA_IT_HOST/PORT/SERVICE/ADMIN_*).
// 잰 값은 시험 출력과(ORANDA_IT_PHC_REPORT 가 있으면) 그 파일에 「PHC|키=값」 줄로 남긴다.
// 서버가 브레이크를 언제 읽는지는 서버 sqlnet.ora(DISABLE_OOB·BREAK_POLL_SKIP)에 달려 있어(D-021), 끊는 시간은 단정하지 않고
// ORANDA_IT_PHC_EXPECT_BREAK=1 일 때만 5 s 이내를 단정한다.

/// <summary>시험 사용자(프로시저 소유)와 관찰 사용자(SELECT_CATALOG_ROLE)를 만들고 끝나면 지운다.</summary>
public sealed class PlsqlCompileHangFixture : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public bool Enabled { get; private set; }

    /// <summary>프로시저를 소유하고 실행·컴파일하는 사용자(CREATE SESSION, CREATE PROCEDURE 만 — V$ 권한 없음).</summary>
    public string User => "ORANDA_PHC_" + _suffix;

    /// <summary>막는 세션 찾기 질의를 읽는 사용자(CREATE SESSION, SELECT_CATALOG_ROLE).</summary>
    public string Observer => "ORANDA_PHO_" + _suffix;

    public string Password { get; } = "Qx" + Guid.NewGuid().ToString("N")[..20] + "#7";

    public static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    public static string AdminConnectionString =>
        new ConnectionProfile("admin", Env("ADMIN_USER")!, DataSource).BuildConnectionString(Env("ADMIN_PASSWORD")!);

    public string ConnectionString(string user) => new ConnectionProfile("phc", user, DataSource).BuildConnectionString(Password);

    public async Task InitializeAsync()
    {
        Enabled = SafetyDbGate.Reason() is null && Env("HOST") is not null;
        if (!Enabled)
        {
            return;
        }

        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            $"CREATE USER {User} IDENTIFIED BY \"{Password}\"",
            $"GRANT CREATE SESSION, CREATE PROCEDURE TO {User}",
            $"CREATE USER {Observer} IDENTIFIED BY \"{Password}\"",
            $"GRANT CREATE SESSION, SELECT_CATALOG_ROLE TO {Observer}",
        })
        {
            await PhcSql.RunAsync(conn, sql);
        }

        // DBMS_SESSION.SLEEP 은 18c 부터. 그 전(11g)은 DBMS_LOCK.SLEEP: SYSTEM 은 그 실행 권한을 직접 줄 수 없어 EXECUTE_CATALOG_ROLE 을 주고
        // 호출자 권한 + 동적 PL/SQL 로 부른다(롤 권한은 실행 때만 통한다).
        await using (var v = conn.CreateCommand())
        {
            v.CommandText = "SELECT TO_NUMBER(REGEXP_SUBSTR(version, '^[0-9]+')) FROM v$instance";
            PhcSql.SleepRoutine = Convert.ToInt32(await v.ExecuteScalarAsync(), CultureInfo.InvariantCulture) >= 18 ? "DBMS_SESSION.SLEEP" : "DBMS_LOCK.SLEEP";
        }

        if (PhcSql.SleepRoutine == "DBMS_LOCK.SLEEP")
        {
            await PhcSql.RunAsync(conn, $"GRANT EXECUTE_CATALOG_ROLE TO {User}");
        }

        await using var owner = new OracleConnection(ConnectionString(User));
        await owner.OpenAsync();
        await PhcSql.RunAsync(owner, PhcSql.SleepProcedure("v1"));
        await using var errors = owner.CreateCommand();
        errors.CommandText = "SELECT LISTAGG(line || ': ' || text, ' | ') WITHIN GROUP (ORDER BY sequence) FROM user_errors WHERE name = 'P_PHC_SLEEP'";
        var text = Convert.ToString(await errors.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("시험 프로시저 컴파일 오류: " + text);
        }
    }

    public async Task DisposeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        foreach (var user in new[] { User, Observer })
        {
            await PhcSql.KillUserSessionsAsync(conn, user);
            for (var i = 0; i < 40; i++)
            {
                if (await PhcSql.TryAsync(conn, $"DROP USER {user} CASCADE"))
                {
                    break;
                }

                await Task.Delay(500);
            }
        }
    }
}

[Trait("Category", "Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "xUnit 이 IAsyncLifetime.DisposeAsync 로 정리한다. 관리자 접속·_raw 도 거기서 닫는다.")]
public sealed class PlsqlCompileHangTests : IClassFixture<PlsqlCompileHangFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    private readonly PlsqlCompileHangFixture _db;
    private readonly ITestOutputHelper _out;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly List<OracleConnection> _raw = [];
    private readonly List<Task> _background = [];
    private OracleSessionManager _manager = null!;
    private OracleConnection _admin = null!;

    public PlsqlCompileHangTests(PlsqlCompileHangFixture db, ITestOutputHelper output)
    {
        _db = db;
        _out = output;
    }

    public async Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        if (_db.Enabled)
        {
            _admin = new OracleConnection(PlsqlCompileHangFixture.AdminConnectionString);
            await _admin.OpenAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (!_db.Enabled)
        {
            return;
        }

        // 띄운 세션(A·C 등)을 모두 끊는다: 서버 쪽은 KILL, 앱 쪽은 닫기. 뒤 시험이 남은 잠·대기를 물려받지 않게 한다.
        await PhcSql.KillUserSessionsAsync(_admin, _db.User);
        await Task.WhenAny(Task.WhenAll(_background.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))), Task.Delay(TimeSpan.FromSeconds(30)));
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        foreach (var c in _raw)
        {
            await c.DisposeAsync();
        }

        await _admin.DisposeAsync();
    }

    private void Report(string key, object? value)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"PHC|{Env("SERVICE")}|{key}={value}");
        _out.WriteLine(text);
        var file = Env("PHC_REPORT");
        if (!string.IsNullOrEmpty(file))
        {
            lock (ReportLock)
            {
                File.AppendAllText(file, text + Environment.NewLine);
            }
        }
    }

    private static readonly object ReportLock = new();

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static bool ExpectBreak => Env("PHC_EXPECT_BREAK") == "1";

    private OracleExecutionService Service() => new(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500);

    private async Task<OracleSession> OpenAppSessionAsync()
    {
        var profile = new ConnectionProfile("phc-b", _db.User, PlsqlCompileHangFixture.DataSource);
        using var cred = new SecureCredential(_db.Password);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private async Task<OracleConnection> OpenRawAsync(string user)
    {
        var c = new OracleConnection(_db.ConnectionString(user));
        await c.OpenAsync();
        _raw.Add(c);
        return c;
    }

    /// <summary>세션 A: 프로시저 안에서 <paramref name="seconds"/> 초 잔다. 서버에서 잠드는 것(PL/SQL lock timer)을 확인한 뒤 SID 를 돌려준다.</summary>
    private async Task<long> StartSleeperAsync(int seconds)
    {
        var a = await OpenRawAsync(_db.User);
        var sid = await PhcSql.SidAsync(a);
        var cmd = a.CreateCommand();
        cmd.CommandText = $"BEGIN p_phc_sleep({seconds.ToString(CultureInfo.InvariantCulture)}); END;";
        _background.Add(Task.Run(async () =>
        {
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (OracleException ex)
            {
                // 정리에서 KILL 하면 ORA-00028 등으로 끝난다.
                Report("a.error", "ORA-" + ex.Number.ToString("D5", CultureInfo.InvariantCulture));
            }
            finally
            {
                cmd.Dispose();
            }
        }));
        var row = await WaitForAsync(sid, r => r.Event == "PL/SQL lock timer", TimeSpan.FromSeconds(15));
        Assert.True(row is not null, "세션 A 가 프로시저 안에서 잠들지 않았다");
        return sid;
    }

    /// <summary>세션 B(앱 경로)가 프로시저를 CREATE OR REPLACE 한다. 편집기 컴파일과 같은 호출(Ddl, HistorySource.Editor).</summary>
    private Task<ExecutionResult> StartCompile(OracleSession b, CancellationToken ct)
    {
        var stmt = new SqlStatement(PhcSql.SleepProcedure(Guid.NewGuid().ToString("N")[..8]), default, StatementKind.Ddl) { Verb = "CREATE" };
        return Task.Run(() => Service().ExecuteAsync(b, stmt, new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = Core.History.HistorySource.Editor }, ct), CancellationToken.None);
    }

    private async Task<PhcSessionRow?> WaitForAsync(long sid, Func<PhcSessionRow, bool> cond, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        PhcSessionRow? last = null;
        while (sw.Elapsed < limit)
        {
            last = await PhcSql.SessionAsync(_admin, sid);
            if (last is not null && cond(last))
            {
                return last;
            }

            await Task.Delay(Poll);
        }

        Report($"wait_timeout.sid{sid}", last?.Event);
        return null;
    }

    private static bool IsLibraryCacheWait(PhcSessionRow r) => r.Event.StartsWith("library cache", StringComparison.Ordinal);

    [SafetyDbFact]
    public async Task Compile_of_running_procedure_waits_and_app_cancel_releases_the_wait()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var aSid = await StartSleeperAsync(40);
        await using var b = await OpenAppSessionAsync();
        var bSid = b.Server.SessionId;
        Report("server_version", b.Server.Version);

        using var cts = new CancellationTokenSource();
        var compile = StartCompile(b, cts.Token);
        var waiting = await WaitForAsync(bSid, IsLibraryCacheWait, TimeSpan.FromSeconds(15));
        Assert.NotNull(waiting);
        Report("b.event", waiting.Event);
        Report("b.wait_class", waiting.WaitClass);
        Report("b.blocking_session", waiting.BlockingSession);
        Report("b.blocking_session_is_a", waiting.BlockingSession == aSid);
        Assert.False(compile.IsCompleted);

        // 세션 C: B 가 기다리는 동안 같은 프로시저를 실행하면(1 s 잠) B 뒤에 줄 서는가.
        var c = await OpenRawAsync(_db.User);
        var cSid = await PhcSql.SidAsync(c);
        var cSw = Stopwatch.StartNew();
        var cRun = Task.Run(async () =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "BEGIN p_phc_sleep(1); END;";
            await cmd.ExecuteNonQueryAsync();
            return cSw.Elapsed;
        });
        _background.Add(cRun);
        var cWait = await WaitForAsync(cSid, IsLibraryCacheWait, TimeSpan.FromSeconds(6));
        Report("c.queued_behind_b", cWait is not null);
        Report("c.event", cWait?.Event);
        Report("c.blocking_session", cWait?.BlockingSession);
        Report("c.blocking_session_is_b", cWait?.BlockingSession == bSid);
        Report("c.done_before_b_cancel", cRun.IsCompleted);

        // 같은 앱 세션 B 에 다른 문장을 보내면 명령 잠금 뒤에 줄 선다(3 s 뒤 스스로 포기).
        using (var gateCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            var gSw = Stopwatch.StartNew();
            var queued = await Service().ExecuteAsync(b, new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query) { Verb = "SELECT" },
                new Dictionary<string, BindValue>(), null, gateCts.Token);
            Report("b.same_session_second_statement", $"{queued.Status} after {gSw.ElapsedMilliseconds} ms");
            await DisposeResultAsync(queued);
        }

        // 취소: 앱이 보내는 것과 같다(토큰 → OracleCommand.Cancel → 브레이크).
        var cancelAtC = cSw.Elapsed;
        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        var finished = await Task.WhenAny(compile, Task.Delay(TimeSpan.FromSeconds(60))) == compile;
        var elapsed = sw.Elapsed;
        var aStillSleeping = (await PhcSql.SessionAsync(_admin, aSid))?.Event == "PL/SQL lock timer";
        Report("cancel.finished_within_60s", finished);
        Report("cancel.a_still_sleeping", aStillSleeping);
        var bAfter = await PhcSql.SessionAsync(_admin, bSid);
        Report("b.after_cancel.event", bAfter?.Event);
        Report("b.after_cancel.status", bAfter?.Status);
        ExecutionResult? r = null;
        if (finished)
        {
            r = await compile;
            await DisposeResultAsync(r);
            Report("cancel.elapsed_ms", (long)elapsed.TotalMilliseconds);
            Report("cancel.status", r.Status);
            Report("cancel.error", r.Error?.Code);
            Report("cancel.error_codes", r.Error is null ? null : string.Join(",", System.Text.RegularExpressions.Regex.Matches(r.Error.Message, @"ORA-\d{5}").Select(m => m.Value).Distinct()));
            Report("cancel.broke_wait", r.Status == ExecutionStatus.Cancelled && aStillSleeping);
        }
        else
        {
            // 클라이언트가 브레이크 응답을 기다리며 멈췄다. 서버 쪽 B 를 끊어(KILL) 클라이언트가 풀리는지 본다.
            var killSw = Stopwatch.StartNew();
            await PhcSql.KillSessionAsync(_admin, bSid);
            var released = await Task.WhenAny(compile, Task.Delay(TimeSpan.FromSeconds(30))) == compile;
            Report("cancel.stuck_released_by_kill", released);
            Report("cancel.stuck_released_by_kill_ms", released ? killSw.ElapsedMilliseconds : -1);
            if (released)
            {
                r = await compile;
                Report("cancel.after_kill.status", r.Status);
                Report("cancel.after_kill.error", r.Error?.Code);
                await DisposeResultAsync(r);
            }
        }

        var cDone = await Task.WhenAny(cRun, Task.Delay(TimeSpan.FromSeconds(45))) == cRun && cRun.IsCompletedSuccessfully;
        Report("c.total_ms", cDone ? (long)cRun.Result.TotalMilliseconds : -1);
        Report("c.after_b_cancel_ms", cDone ? (long)(cRun.Result - cancelAtC).TotalMilliseconds : -1);

        // 취소 뒤 같은 세션을 바로 다시 쓴다(멈춘 채라면 10 s 뒤 포기).
        var reuseSw = Stopwatch.StartNew();
        using var reuseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reuse = await Service().ExecuteAsync(b, new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query) { Verb = "SELECT" },
            new Dictionary<string, BindValue>(), null, reuseCts.Token);
        Report("reuse.status", reuse.Status);
        Report("reuse.error", reuse.Error?.Code);
        Report("reuse.elapsed_ms", reuseSw.ElapsedMilliseconds);
        Report("reuse.session_state", b.State);
        await DisposeResultAsync(reuse);

        Assert.True(IsLibraryCacheWait(waiting));
        Assert.Equal(aSid, waiting.BlockingSession);
        if (finished)
        {
            Assert.Equal(ExecutionStatus.Succeeded, reuse.Status);
        }

        if (ExpectBreak)
        {
            Assert.True(finished);
            Assert.Equal(ExecutionStatus.Cancelled, r!.Status);
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"취소 반영 {elapsed.TotalMilliseconds:F0} ms");
        }
    }

    [SafetyDbFact]
    public async Task Closing_app_session_while_compile_waits_leaves_server_session_for_measured_time()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var aSid = await StartSleeperAsync(100);
        var b = await OpenAppSessionAsync();
        var bSid = b.Server.SessionId;
        var bSerial = (await PhcSql.SessionAsync(_admin, bSid))!.Serial;
        var compile = StartCompile(b, CancellationToken.None);
        _background.Add(compile);
        Assert.NotNull(await WaitForAsync(bSid, IsLibraryCacheWait, TimeSpan.FromSeconds(15)));

        // 취소 없이 그냥 닫는다(편집기 창·접속 닫기).
        var sw = Stopwatch.StartNew();
        var dispose = b.DisposeAsync().AsTask();
        long? goneAfterMs = null;
        long? disposeMs = null;
        long? compileEndMs = null;
        string? lastEvent = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (disposeMs is null && dispose.IsCompleted)
            {
                disposeMs = sw.ElapsedMilliseconds;
            }

            if (compileEndMs is null && compile.IsCompleted)
            {
                compileEndMs = sw.ElapsedMilliseconds;
            }

            var row = await PhcSql.SessionAsync(_admin, bSid);
            if (row is null || row.Serial != bSerial)
            {
                goneAfterMs = sw.ElapsedMilliseconds;
                break;
            }

            lastEvent = row.Event;
            await Task.Delay(Poll);
        }

        var aStillSleeping = (await PhcSql.SessionAsync(_admin, aSid))?.Event == "PL/SQL lock timer";
        Report("close.dispose_returned_ms", disposeMs ?? -1);
        Report("close.compile_task_ended_ms", compileEndMs ?? -1);
        Report("close.server_session_gone_ms", goneAfterMs ?? -1);
        Report("close.server_session_last_event", lastEvent);
        Report("close.a_still_sleeping", aStillSleeping);
        if (goneAfterMs is null)
        {
            var row = await PhcSql.SessionAsync(_admin, bSid);
            Report("close.after_60s.event", row?.Event);
            Report("close.after_60s.status", row?.Status);
        }

        // 앱 쪽 닫기는 서버를 기다리지 않고 돌아와야 한다(UI 정지 원인, OracleSession.DisposeAsync 의 TimedClose).
        Assert.True(dispose.IsCompleted, "세션 닫기가 60 s 안에 돌아오지 않았다");
    }

    [SafetyDbFact]
    public async Task Compile_blocker_query_from_another_session_names_the_running_session()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var aSid = await StartSleeperAsync(30);
        await using var b = await OpenAppSessionAsync();
        var bSid = b.Server.SessionId;
        using var cts = new CancellationTokenSource();
        var compile = StartCompile(b, cts.Token);
        _background.Add(compile);
        Assert.NotNull(await WaitForAsync(bSid, IsLibraryCacheWait, TimeSpan.FromSeconds(15)));

        var sql = EmbeddedCatalog.Text("plsql.compile_blocker");

        // 관찰 사용자(SELECT_CATALOG_ROLE)로 다른 세션에서 읽는다.
        var observer = await OpenRawAsync(_db.Observer);
        await using (var cmd = observer.CreateCommand())
        {
            cmd.BindByName = true;
            cmd.CommandText = sql;
            cmd.Parameters.Add(new OracleParameter("sid", bSid));
            await using var rd = await cmd.ExecuteReaderAsync();
            Assert.True(await rd.ReadAsync(), "기다리는 세션 행이 없다");
            string? S(string col) => rd.IsDBNull(rd.GetOrdinal(col)) ? null : Convert.ToString(rd.GetValue(rd.GetOrdinal(col)), CultureInfo.InvariantCulture);
            Report("blocker.waiting_event", S("WAITING_EVENT"));
            Report("blocker.blocking_session_status", S("BLOCKING_SESSION_STATUS"));
            Report("blocker.blocking_sid", S("BLOCKING_SID"));
            Report("blocker.blocking_username_is_user", S("BLOCKING_USERNAME") == _db.User);
            Report("blocker.blocking_event", S("BLOCKING_EVENT"));
            Report("blocker.running_object_is_proc", S("RUNNING_OBJECT") == _db.User + ".P_PHC_SLEEP");
            Report("blocker.program_present", !string.IsNullOrEmpty(S("BLOCKING_PROGRAM")));
            Report("blocker.machine_present", !string.IsNullOrEmpty(S("BLOCKING_MACHINE")));

            Assert.StartsWith("library cache", S("WAITING_EVENT"), StringComparison.Ordinal);
            Assert.Equal(aSid.ToString(CultureInfo.InvariantCulture), S("BLOCKING_SID"));
            Assert.Equal(_db.User, S("BLOCKING_USERNAME"));
            Assert.Equal("PL/SQL lock timer", S("BLOCKING_EVENT"));
            Assert.Equal(_db.User + ".P_PHC_SLEEP", S("RUNNING_OBJECT"));
            Assert.False(string.IsNullOrEmpty(S("BLOCKING_PROGRAM")));
            Assert.False(string.IsNullOrEmpty(S("BLOCKING_MACHINE")));
            Assert.False(await rd.ReadAsync());
        }

        // 기다리는 세션이 아닌 SID 면 막는 세션이 비어 있다(A 는 아무도 기다리지 않는다).
        await using (var cmd = observer.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Parameters.Add(new OracleParameter("sid", aSid));
            await using var rd = await cmd.ExecuteReaderAsync();
            Assert.True(await rd.ReadAsync());
            Assert.True(rd.IsDBNull(rd.GetOrdinal("BLOCKING_SID")));
        }

        // V$ 권한이 없는 사용자(CREATE SESSION·CREATE PROCEDURE 만)는 ORA-00942.
        var plain = await OpenRawAsync(_db.User);
        await using (var cmd = plain.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Parameters.Add(new OracleParameter("sid", bSid));
            var ex = await Assert.ThrowsAsync<OracleException>(() => cmd.ExecuteReaderAsync());
            Report("blocker.no_privilege_error", "ORA-" + ex.Number.ToString("D5", CultureInfo.InvariantCulture));
            Assert.Equal(942, ex.Number);
        }

        await cts.CancelAsync();
        await Task.WhenAny(compile, Task.Delay(TimeSpan.FromSeconds(40)));
    }

    [SafetyDbFact]
    public async Task Object_users_precheck_counts_the_session_running_the_procedure_from_v_session()
    {
        if (!_db.Enabled)
        {
            return;
        }

        // 컴파일 전 사전 확인(meta.object_users). v$access 는 공유 풀이 큰 서버에서 한 번에 수 분~15분 걸렸다: v$session 으로 센다.
        var sql = EmbeddedCatalog.Text("meta.object_users", ("owner", _db.User), ("name", "P_PHC_SLEEP"));
        Assert.DoesNotContain("v$access", sql, StringComparison.OrdinalIgnoreCase);

        await using var admin = new OracleConnection(PlsqlCompileHangFixture.AdminConnectionString);
        await admin.OpenAsync();

        async Task<(int Count, long Ms)> CountAsync()
        {
            var sw = Stopwatch.StartNew();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = sql;
            var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            return (n, sw.ElapsedMilliseconds);
        }

        var (before, _) = await CountAsync();
        Assert.Equal(0, before);

        await StartSleeperAsync(20);
        var (running, ms) = await CountAsync();
        Report("object_users.running", running);
        Report("object_users.ms", ms);
        Assert.Equal(1, running);
        Assert.True(ms < 5000, $"사전 확인이 {ms} ms 걸렸다(상한 5 s)");
    }

    private static async Task DisposeResultAsync(ExecutionResult r)
    {
        if (r.ResultSet is not null)
        {
            await r.ResultSet.DisposeAsync();
        }
    }
}

internal sealed record PhcSessionRow(long Sid, long Serial, string Event, string? WaitClass, long? BlockingSession, string Status);

internal static class PhcSql
{
    /// <summary>시험 프로시저. <paramref name="tag"/> 를 주석에 넣어 매번 다른 원문을 보낸다(같은 원문이면 서버가 다시 컴파일하지 않을 수 있다).</summary>
    public static string SleepProcedure(string tag) => SleepRoutine == "DBMS_SESSION.SLEEP"
        ? $"CREATE OR REPLACE PROCEDURE p_phc_sleep(n IN NUMBER DEFAULT 30) AS\nBEGIN\n  -- {tag}\n  DBMS_SESSION.SLEEP(n);\nEND;"
        : $"CREATE OR REPLACE PROCEDURE p_phc_sleep(n IN NUMBER DEFAULT 30) AUTHID CURRENT_USER AS\nBEGIN\n  -- {tag}\n  EXECUTE IMMEDIATE 'BEGIN DBMS_LOCK.SLEEP(:n); END;' USING n;\nEND;";

    /// <summary>서버 버전에 맞는 잠 루틴(픽스처가 정한다).</summary>
    public static string SleepRoutine { get; set; } = "DBMS_SESSION.SLEEP";

    public static async Task RunAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<bool> TryAsync(OracleConnection conn, string sql)
    {
        try
        {
            await RunAsync(conn, sql);
            return true;
        }
        catch (OracleException)
        {
            return false;
        }
    }

    public static async Task<long> SidAsync(OracleConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SYS_CONTEXT('USERENV', 'SID') FROM dual";
        return long.Parse(Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
    }

    public static async Task<PhcSessionRow?> SessionAsync(OracleConnection admin, long sid)
    {
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = "SELECT sid, serial#, event, wait_class, blocking_session, status FROM v$session WHERE sid = :sid";
        cmd.Parameters.Add(new OracleParameter("sid", sid));
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
        {
            return null;
        }

        return new PhcSessionRow(
            Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(rd.GetValue(1), CultureInfo.InvariantCulture),
            rd.IsDBNull(2) ? "" : rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3),
            rd.IsDBNull(4) ? null : Convert.ToInt64(rd.GetValue(4), CultureInfo.InvariantCulture),
            rd.IsDBNull(5) ? "" : rd.GetString(5));
    }

    public static async Task KillSessionAsync(OracleConnection admin, long sid)
    {
        var row = await SessionAsync(admin, sid);
        if (row is not null)
        {
            await TryAsync(admin, string.Create(CultureInfo.InvariantCulture, $"ALTER SYSTEM KILL SESSION '{row.Sid},{row.Serial}' IMMEDIATE"));
        }
    }

    public static async Task KillUserSessionsAsync(OracleConnection admin, string user)
    {
        var sessions = new List<(long Sid, long Serial)>();
        await using (var q = admin.CreateCommand())
        {
            q.CommandText = "SELECT sid, serial# FROM v$session WHERE username = :u";
            q.Parameters.Add(new OracleParameter("u", user));
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                sessions.Add((Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture)));
            }
        }

        foreach (var (sid, serial) in sessions)
        {
            await TryAsync(admin, string.Create(CultureInfo.InvariantCulture, $"ALTER SYSTEM KILL SESSION '{sid},{serial}' IMMEDIATE"));
        }
    }
}
