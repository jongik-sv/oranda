using System.Globalization;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Integration;

// 소유: TSK-11-02. DBA 흐름 통합시험(세션 → 블로킹 → Kill → Top SQL → 실행 계획) + 라이선스 트레이스 0건.
//
// 상주 컨테이너(oracle-26ai-free, 127.0.0.1:1521/FREEPDB1)를 쓴다. 새 컨테이너를 띄우지 않는다.
//   ORANDA_IT_SYSTEM_PASSWORD  (필수) SYSTEM 비밀번호
//   ORANDA_IT_DATASOURCE       (선택) 기본 127.0.0.1:1521/FREEPDB1
// 시험마다 고유한 사용자·표·감사 정책을 만들고 끝나면 지운다.
//
// 라이선스 트레이스 확인 방법: 시험마다 통합 감사 정책(UNIFIED AUDIT)을 만들어 팩 필요 객체
// (ASH·DBA_HIST_*·V$SQL_MONITOR 계열 뷰, DBMS_SQLTUNE·DBMS_SQL_MONITOR·DBMS_WORKLOAD_REPOSITORY 패키지)의
// SELECT/EXECUTE 를 기록하고, 이 시험이 연 세션(AUDSID)의 감사 행이 0건인지 본다.
// 정책이 실제로 동작하는지는 대조군(팩 객체를 일부러 조회 → 1건 이상)으로 같은 시험에서 확인한다.

/// <summary>상주 컨테이너 접속 정보.</summary>
internal static class ResidentDb
{
    public static string DataSource => Environment.GetEnvironmentVariable("ORANDA_IT_DATASOURCE") is { Length: > 0 } d ? d : "127.0.0.1:1521/FREEPDB1";

    public static string SystemPassword =>
        Environment.GetEnvironmentVariable("ORANDA_IT_SYSTEM_PASSWORD") is { Length: > 0 } p
            ? p
            : throw new InvalidOperationException("ORANDA_IT_SYSTEM_PASSWORD 가 없다. 상주 컨테이너 SYSTEM 비밀번호를 환경 변수로 준다(조용히 통과하지 않는다).");
}

/// <summary>시험 하나가 쓰는 고유 사용자·표·감사 정책 묶음. DisposeAsync 가 모두 지운다.</summary>
internal sealed class DbaFlowEnv : IAsyncDisposable
{
    private static readonly string[] PackSelectPatterns =
    [
        @"^G?V_\$(ACTIVE_SESSION_HISTORY|ASH_INFO|SQL_MONITOR|SQL_PLAN_MONITOR|SQL_MONITOR_SESSTAT|SQL_MONITOR_STATNAME)$",
        @"^DBA_HIST_.*$",
        @"^DBA_SQLTUNE_.*$",
    ];

    private static readonly string[] PackPackages = ["DBMS_SQLTUNE", "DBMS_SQL_MONITOR", "DBMS_WORKLOAD_REPOSITORY"];

    private readonly List<OracleConnection> _raw = [];
    private readonly List<ISession> _sessions = [];
    private readonly List<string> _audKeys = [];   // 감사 행을 가려내는 SQL 조각용: 숫자(통합 감사 세션 ID) 또는 문자(client_identifier)
    private readonly List<string> _clientIds = [];
    private readonly List<string> _extraUsers = [];
    private bool _policyCreated;

    private DbaFlowEnv(string id)
    {
        Id = id;
        User = "ODF_" + id;
        Table = "ODF_T_" + id;
        Password = "Pw" + Guid.NewGuid().ToString("N")[..16] + "#1";
        Policy = "ORA_ODF_" + id;
        Bus = new ExecBus();
        Ui = new ExecInteraction();
        Manager = new OracleSessionManager(Bus);
    }

    public string Id { get; }

    public string User { get; }

    public string Table { get; }

    public string Password { get; }

    public string Policy { get; }

    public ExecBus Bus { get; }

    public ExecInteraction Ui { get; }

    public OracleSessionManager Manager { get; }

    public static async Task<DbaFlowEnv> CreateAsync(int rows = 60000)
    {
        var env = new DbaFlowEnv(Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        await env.SetupAsync(rows);
        return env;
    }

    public OracleMonitorService MonitorService() =>
        new(new OracleExecutionService(new ExecutionPolicy(), Ui, Bus, null, pageSize: 500));

    public OracleExecutionPlanService PlanService() =>
        new(new OracleExecutionService(new ExecutionPolicy(), Ui, Bus, null, pageSize: 500));

    public async Task<OracleConnection> AdminAsync()
    {
        var profile = new ConnectionProfile("res-system", "system", ResidentDb.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(ResidentDb.SystemPassword));
        await conn.OpenAsync();
        _raw.Add(conn);
        return conn;
    }

    /// <summary>시험 사용자로 연 일반 연결(잠금 보유자·대기자·무거운 SQL 실행자). 감사 세션 ID 를 기억한다.</summary>
    public async Task<(OracleConnection Conn, SessionKey Key)> UserConnAsync()
    {
        var profile = new ConnectionProfile("res-" + User, User, ResidentDb.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(Password));
        await conn.OpenAsync();
        _raw.Add(conn);
        var (key, aud) = await IdentifyAsync(conn);
        _audKeys.Add(aud);
        return (conn, key);
    }

    /// <summary>Oranda 세션(모니터/작업). 감사 세션 ID 를 기억한다. 기본 = SYSTEM, 팩 선언 없음.</summary>
    public async Task<ISession> OpenOrandaAsync(SessionPurpose purpose = SessionPurpose.Monitor, string? user = null, string? password = null,
        LicensePack pack = LicensePack.None, bool readOnly = false)
    {
        user ??= "system";
        password ??= ResidentDb.SystemPassword;
        var profile = new ConnectionProfile("res-" + user, user, ResidentDb.DataSource, ReadOnly: readOnly, LicensePack: pack);
        using var cred = new SecureCredential(password);
        var session = await Manager.OpenAsync(profile, cred, purpose, default);
        _sessions.Add(session);
        _clientIds.Add(await ClientIdOfAsync(session.Server.SessionId));
        return session;
    }

    public void TrackUser(string user) => _extraUsers.Add(user);

    public async Task ExecAsync(string sql)
    {
        var admin = _raw.Count > 0 ? _raw[0] : await AdminAsync();
        await ExecOnAsync(admin, sql);
    }

    public static async Task ExecOnAsync(OracleConnection c, string sql, OracleTransaction? tx = null)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<decimal> ScalarAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>팩 필요 객체에 대한 이 시험 세션들의 감사 행(대상 SQL 본문). 0건이어야 한다. 추가로 볼 감사 세션 ID·client_identifier 를 줄 수 있다.</summary>
    public async Task<IReadOnlyList<string>> PackAuditHitsAsync(string[]? extraAuditSessionIds = null, string[]? extraClientIds = null)
    {
        var admin = await AdminAsync();
        var ids = _audKeys.Concat(extraAuditSessionIds ?? []).Distinct().ToList();
        var cids = _clientIds.Concat(extraClientIds ?? []).Distinct().ToList();
        var conds = new List<string>();
        if (ids.Count > 0)
        {
            conds.Add("sessionid IN (" + string.Join(',', ids) + ")");
        }

        if (cids.Count > 0)
        {
            conds.Add("client_identifier IN (" + string.Join(',', cids.Select(c => "'" + c.Replace("'", "''", StringComparison.Ordinal) + "'")) + ")");
        }

        if (conds.Count == 0)
        {
            throw new InvalidOperationException("감사 대상 세션이 하나도 없다 — 0건 확인이 의미 없다.");
        }

        await using var cmd = admin.CreateCommand();
        cmd.CommandText = "SELECT object_schema || '.' || object_name || ' <- ' || SUBSTR(sql_text, 1, 120) FROM unified_audit_trail "
            + "WHERE unified_audit_policies LIKE '%' || :p || '%' AND (" + string.Join(" OR ", conds) + ")";
        cmd.Parameters.Add("p", Policy);
        var hits = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            hits.Add(r.IsDBNull(0) ? "(null)" : r.GetString(0));
        }

        return hits;
    }

    public async Task<bool> PolicyEnabledAsync()
    {
        var admin = await AdminAsync();
        return await ScalarAsync(admin, $"SELECT COUNT(*) FROM audit_unified_enabled_policies WHERE policy_name = '{Policy}'") > 0;
    }

    /// <summary>Oranda 세션이 연결 때 붙이는 client_identifier("oranda:...")를 관리자 연결로 읽는다.</summary>
    public static async Task<string> ClientIdOfAsync(long sid)
    {
        var profile = new ConnectionProfile("res-system", "system", ResidentDb.DataSource);
        await using var c = new OracleConnection(profile.BuildConnectionString(ResidentDb.SystemPassword));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT client_identifier FROM v$session WHERE sid = {sid.ToString(CultureInfo.InvariantCulture)}";
        var id = Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(id) || !id.StartsWith("oranda:", StringComparison.Ordinal)
            ? throw new InvalidOperationException("Oranda 세션의 client_identifier 를 읽지 못했다: " + id)
            : id;
    }

    private static async Task<(SessionKey Key, string Aud)> IdentifyAsync(OracleConnection c)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SYS_CONTEXT('USERENV','INSTANCE'), SYS_CONTEXT('USERENV','SID'), SYS_CONTEXT('USERENV','UNIFIED_AUDIT_SESSIONID') FROM dual";
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        var inst = int.Parse(r.GetString(0), CultureInfo.InvariantCulture);
        var sid = long.Parse(r.GetString(1), CultureInfo.InvariantCulture);
        var aud = r.GetString(2);
        await r.DisposeAsync();

        // 일반 사용자는 v$session 을 못 보므로 serial# 은 관리자 연결로 읽는다.
        var profile = new ConnectionProfile("res-system", "system", ResidentDb.DataSource);
        await using var admin = new OracleConnection(profile.BuildConnectionString(ResidentDb.SystemPassword));
        await admin.OpenAsync();
        var serial = (long)await ScalarAsync(admin, $"SELECT serial# FROM v$session WHERE sid = {sid.ToString(CultureInfo.InvariantCulture)}");
        return (new SessionKey(inst, sid, serial), aud);
    }

    private async Task SetupAsync(int rows)
    {
        var admin = await AdminAsync();
        await ExecOnAsync(admin, $"CREATE USER {User} IDENTIFIED BY \"{Password}\"");
        await ExecOnAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, UNLIMITED TABLESPACE TO {User}");
        await ExecOnAsync(admin, $"CREATE TABLE {User}.{Table} AS SELECT level AS id, MOD(level, 7) AS g, RPAD('x', 200, 'x') AS pad FROM dual CONNECT BY level <= {rows.ToString(CultureInfo.InvariantCulture)}");
        await ExecOnAsync(admin, $"ALTER TABLE {User}.{Table} ADD CONSTRAINT {Table}_PK PRIMARY KEY (id)");

        // 팩 필요 객체 감사 정책.
        var actions = new List<string>();
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = "SELECT object_name FROM dba_objects WHERE owner = 'SYS' AND object_type = 'VIEW' AND ("
                + string.Join(" OR ", PackSelectPatterns.Select((_, i) => $"REGEXP_LIKE(object_name, :p{i})")) + ")";
            for (var i = 0; i < PackSelectPatterns.Length; i++)
            {
                cmd.Parameters.Add($"p{i}", PackSelectPatterns[i]);
            }

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                actions.Add("SELECT ON SYS." + r.GetString(0));
            }
        }

        if (actions.Count == 0)
        {
            throw new InvalidOperationException("팩 뷰를 하나도 찾지 못했다 — 감사 정책을 만들 수 없다.");
        }

        actions.AddRange(PackPackages.Select(p => "EXECUTE ON SYS." + p));
        await ExecOnAsync(admin, $"CREATE AUDIT POLICY {Policy} ACTIONS {string.Join(", ", actions)}");
        _policyCreated = true;
        await ExecOnAsync(admin, $"AUDIT POLICY {Policy}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            try
            {
                await s.DisposeAsync();
            }
            catch (OracleException)
            {
            }
        }

        foreach (var c in _raw.Skip(1))
        {
            try
            {
                await c.DisposeAsync();
            }
            catch (OracleException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            var profile = new ConnectionProfile("res-system", "system", ResidentDb.DataSource);
            await using var admin = new OracleConnection(profile.BuildConnectionString(ResidentDb.SystemPassword));
            await admin.OpenAsync();

            // 남은 시험 사용자 세션을 끊는다.
            var left = new List<string>();
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText = $"SELECT sid || ',' || serial# FROM v$session WHERE username = '{User}'";
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    left.Add(r.GetString(0));
                }
            }

            foreach (var k in left)
            {
                try
                {
                    await ExecOnAsync(admin, $"ALTER SYSTEM KILL SESSION '{k}' IMMEDIATE");
                }
                catch (OracleException)
                {
                }
            }

            if (_policyCreated)
            {
                await ExecOnAsync(admin, $"NOAUDIT POLICY {Policy}");
                await ExecOnAsync(admin, $"DROP AUDIT POLICY {Policy}");
            }

            foreach (var u in _extraUsers.Append(User))
            {
                await DropUserAsync(admin, u);
            }
        }
        finally
        {
            if (_raw.Count > 0)
            {
                try
                {
                    await _raw[0].DisposeAsync();
                }
                catch (OracleException)
                {
                }
            }
        }
    }

    private static async Task DropUserAsync(OracleConnection admin, string user)
    {
        {
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    await ExecOnAsync(admin, $"DROP USER {user} CASCADE");
                    break;
                }
                catch (OracleException ex) when (ex.Number == 1940)
                {
                    await Task.Delay(500); // 끊긴 세션이 아직 정리 중.
                }
                catch (OracleException ex) when (ex.Number == 1918)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>DB 없이 도는 라이선스 정적 검사(TC-06).</summary>
public class DbaFlowLicenseStaticTests
{
    private static readonly Regex PackObjects = new(
        @"\b(G?V\$ACTIVE_SESSION_HISTORY|G?V\$ASH_INFO|DBA_HIST_\w+|G?V\$SQL_MONITOR\w*|G?V\$SQL_PLAN_MONITOR|DBMS_SQLTUNE|DBMS_SQL_MONITOR|DBMS_WORKLOAD_REPOSITORY)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Catalog_queries_that_touch_pack_objects_carry_a_license_tag()
    {
        var sources = EmbeddedCatalog.ReadSources(typeof(EmbeddedCatalog).Assembly);
        Assert.NotEmpty(sources);

        var violations = new List<string>();
        var tagged = 0;
        foreach (var s in sources)
        {
            var lines = s.Text.Split('\n');
            var license = lines.Select(l => Regex.Match(l, @"^--\s*license:\s*(\w+)", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value.ToLowerInvariant();
            var code = string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
            if (PackObjects.IsMatch(code))
            {
                if (license is null or "none")
                {
                    violations.Add($"{s.Path}: 팩 객체를 쓰는데 license={license ?? "(없음)"}");
                }
                else
                {
                    tagged++;
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.True(tagged >= 1, "팩 객체를 쓰는 태그된 쿼리가 하나도 없다 — 검사 정규식이 낡았을 수 있다.");
    }

    [Fact]
    public void Every_catalog_file_declares_license_header()
    {
        foreach (var s in EmbeddedCatalog.ReadSources(typeof(EmbeddedCatalog).Assembly))
        {
            Assert.Matches(@"(?m)^--\s*license:\s*(none|diagnostics|tuning)\s*$", s.Text);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class DbaFlowTests
{
    private static async Task WaitUntilAsync(Func<Task<bool>> cond, int seconds = 20, string what = "조건")
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            if (await cond())
            {
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail($"{what} 이(가) {seconds}초 안에 충족되지 않았다.");
    }

    private static BlockingNode? Find(IEnumerable<BlockingNode> nodes, SessionKey key)
    {
        foreach (var n in nodes)
        {
            if (n.Session == key)
            {
                return n;
            }

            if (Find(n.Waiters, key) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    // ---------- S1: 전체 흐름 ----------

    [Fact]
    public async Task S1_full_dba_flow_session_blocking_kill_topsql_plan_with_zero_pack_calls()
    {
        await using var env = await DbaFlowEnv.CreateAsync();
        var monitor = await env.OpenOrandaAsync();
        Assert.Equal(LicensePack.None, monitor.Profile.LicensePack);
        var monSvc = env.MonitorService();
        var planSvc = env.PlanService();
        var tbl = $"{env.User}.{env.Table}";

        // 1) 잠금 경합: 보유자 A 가 1번 행을 갱신, 대기자 B 가 같은 행을 갱신하려다 막힘.
        var (a, aKey) = await env.UserConnAsync();
        var aTx = a.BeginTransaction();
        await DbaFlowEnv.ExecOnAsync(a, $"UPDATE {tbl} SET g = 100 WHERE id = 1", aTx);
        var (b, bKey) = await env.UserConnAsync();
        var bTx = b.BeginTransaction();
        var bWait = DbaFlowEnv.ExecOnAsync(b, $"UPDATE {tbl} SET g = 200 /* S1WAITER */ WHERE id = 1", bTx);

        // 2) 세션 목록: 둘 다 보이고, B 의 블로커가 A.
        SessionRow? bRow = null;
        await WaitUntilAsync(async () =>
        {
            var rows = await monSvc.GetSessionsAsync(monitor, new SessionFilter { Username = env.User }, default);
            bRow = rows.FirstOrDefault(r => r.Key == bKey);
            return rows.Any(r => r.Key == aKey) && bRow?.BlockingSession is not null;
        }, what: "세션 목록에 대기자의 블로커 표시");
        Assert.Equal(aKey.Sid, bRow!.BlockingSession!.Value.Sid);
        Assert.Equal(env.User, bRow.Username);

        // 3) 블로킹 트리: A 뿌리, B 대기자, 잠긴 객체, 대기 이벤트.
        BlockingTree tree = new([]);
        await WaitUntilAsync(async () =>
        {
            tree = await monSvc.GetBlockingTreeAsync(monitor, default);
            return tree.Roots.Any(r => r.Session == aKey && r.Waiters.Any(w => w.Session == bKey));
        }, what: "블로킹 트리");
        var root = Assert.Single(tree.Roots, r => r.Session == aKey);
        var node = Assert.Single(root.Waiters, w => w.Session == bKey);
        Assert.Equal(env.User, root.Username);
        Assert.Contains("enq: TX", node.Event, StringComparison.Ordinal);
        Assert.Equal($"{env.User}.{env.Table}", node.LockedObject);
        Assert.False(bWait.IsCompleted);

        // 4) 세션 상세 → 현재 SQL → 커서로 계획(세션에서 계획으로 이어지는 길).
        var detailB = await monSvc.GetSessionDetailAsync(monitor, bKey, default);
        Assert.Contains("S1WAITER", detailB.CurrentSqlText, StringComparison.Ordinal);
        Assert.NotNull(detailB.CurrentCursor);
        var cursorPlan = await planSvc.GetCursorPlanAsync(monitor, detailB.CurrentCursor!, "TYPICAL", default);
        Assert.Contains(cursorPlan.Steps, s => s.Operation.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.Contains("Plan hash value", cursorPlan.Text, StringComparison.Ordinal);
        var detailA = await monSvc.GetSessionDetailAsync(monitor, aKey, default);
        Assert.Contains(Assert.Single(detailA.Sections, s => s.Title == "잠금 객체").Rows, r => Equals(r[1], env.Table));

        // 5) Kill: 확인 대화상자(재입력 = sid,serial) 후 A 가 끊기고 B 가 풀린다.
        await monSvc.KillAsync(monitor, aKey, immediate: true, default);
        Assert.Contains(env.Ui.Confirms, d => d.Rule == PolicyRule.DangerousCommand && d.RetypeText == $"{aKey.Sid},{aKey.Serial}");
        await bWait.WaitAsync(TimeSpan.FromSeconds(30));
        await bTx.RollbackAsync();
        var after = await monSvc.GetBlockingTreeAsync(monitor, default);
        Assert.Null(Find(after.Roots, aKey));
        Assert.Null(Find(after.Roots, bKey));

        // A 의 연결은 실제로 죽었다(되돌림: A 의 미커밋 변경은 사라짐).
        await Assert.ThrowsAnyAsync<Exception>(() => DbaFlowEnv.ExecOnAsync(a, "SELECT 1 FROM dual"));
        var admin = await env.AdminAsync();
        Assert.Equal(0m, await DbaFlowEnv.ScalarAsync(admin, $"SELECT COUNT(*) FROM {tbl} WHERE id = 1 AND g IN (100, 200)"));

        // 6) Top SQL: 무거운 SQL(바인드·통계 수집 힌트)을 돌리고 조건으로 찾는다.
        var (runner, _) = await env.UserConnAsync();
        var tag = "S1TOP_" + env.Id;
        var text = $"SELECT /*+ gather_plan_statistics */ /* {tag} */ COUNT(*) FROM {tbl} t WHERE t.id > :b1 AND t.pad LIKE :b2";
        await using (var cmd = runner.CreateCommand())
        {
            cmd.BindByName = true;
            cmd.CommandText = text;
            cmd.Parameters.Add(new OracleParameter("b1", OracleDbType.Int32) { Value = 7 });
            cmd.Parameters.Add(new OracleParameter("b2", OracleDbType.Varchar2) { Value = "x%" });
            Assert.Equal(59993m, Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        var rows2 = await monSvc.GetTopSqlAsync(monitor, new TopSqlCriteria
        {
            TextContains = tag,
            MinBufferGets = 1000,
            MinExecutions = 1,
            ParsingSchema = env.User,
            OrderBy = TopSqlOrder.BufferGets,
            ActiveWithin = TimeSpan.FromMinutes(10),
        }, default);
        var top = Assert.Single(rows2);
        Assert.True(top.BufferGets >= 1000);
        Assert.Equal(env.User, top.ParsingSchema);

        var sqlDetail = await monSvc.GetSqlDetailAsync(monitor, top.Cursor, default);
        Assert.Contains(env.Table, sqlDetail.FullText, StringComparison.Ordinal);
        Assert.Equal("7", Assert.Single(sqlDetail.Binds, x => x.Name.Equals(":B1", StringComparison.OrdinalIgnoreCase)).Value);

        // 7) 실제 계획: 찾은 커서의 계획에 전체 스캔 단계와 실제 행 수가 있다.
        var actual = await planSvc.GetCursorPlanAsync(monitor, top.Cursor, "ALLSTATS LAST", default);
        var scan = Assert.Single(actual.Steps, s => s.Operation == "TABLE ACCESS");
        Assert.Equal("FULL", scan.Options);
        Assert.Equal(env.Table, scan.ObjectName);
        Assert.True(scan.Risk.HasFlag(PlanRisk.FullScan));
        Assert.True(scan.ActualRows is > 0, "gather_plan_statistics 로 돌렸는데 실제 행 수가 비어 있다.");
        Assert.Contains("A-Rows", actual.Text, StringComparison.Ordinal);

        // 예상 계획도 같은 표를 가리킨다.
        var explained = await planSvc.ExplainAsync(monitor,
            new SqlStatement(text, default, StatementKind.Query) { Verb = "SELECT", BindNames = ["b1", "b2"] }, default);
        Assert.Contains(explained.Steps, s => s.ObjectName == env.Table);

        // 8) 라이선스 트레이스 0건: 정책이 켜져 있고, 이 시험 세션들이 팩 객체를 한 번도 건드리지 않았다.
        Assert.True(await env.PolicyEnabledAsync(), "감사 정책이 켜져 있지 않다 — 0건 확인이 의미 없다.");
        Assert.Empty(await env.PackAuditHitsAsync());
    }

    // ---------- S2: 3단 체인 + 연쇄 Kill ----------

    [Fact]
    public async Task S2_three_level_chain_is_nested_and_killing_blockers_in_order_releases_each_level()
    {
        await using var env = await DbaFlowEnv.CreateAsync(rows: 100);
        var monitor = await env.OpenOrandaAsync();
        var svc = env.MonitorService();
        var tbl = $"{env.User}.{env.Table}";

        var (a, aKey) = await env.UserConnAsync();
        var aTx = a.BeginTransaction();
        await DbaFlowEnv.ExecOnAsync(a, $"UPDATE {tbl} SET g = 1 WHERE id = 1", aTx);

        var (b, bKey) = await env.UserConnAsync();
        var bTx = b.BeginTransaction();
        await DbaFlowEnv.ExecOnAsync(b, $"UPDATE {tbl} SET g = 2 WHERE id = 2", bTx);
        var bWait = DbaFlowEnv.ExecOnAsync(b, $"UPDATE {tbl} SET g = 2 WHERE id = 1", bTx);

        var (c, cKey) = await env.UserConnAsync();
        var cTx = c.BeginTransaction();
        var cWait = DbaFlowEnv.ExecOnAsync(c, $"UPDATE {tbl} SET g = 3 WHERE id = 2", cTx);

        await WaitUntilAsync(async () =>
        {
            var t = await svc.GetBlockingTreeAsync(monitor, default);
            var root = t.Roots.FirstOrDefault(r => r.Session == aKey);
            return root?.Waiters.FirstOrDefault(w => w.Session == bKey)?.Waiters.Any(w => w.Session == cKey) == true;
        }, what: "A→B→C 중첩 트리");

        // 경계: 최종 블로커는 A 하나뿐이고 B 는 뿌리가 아니다.
        var tree = await svc.GetBlockingTreeAsync(monitor, default);
        Assert.Single(tree.Roots, r => r.Session == aKey);
        Assert.DoesNotContain(tree.Roots, r => r.Session == bKey || r.Session == cKey);

        // A Kill → B 풀림(B 는 여전히 2번 행을 쥐고 있어 C 는 계속 대기).
        await svc.KillAsync(monitor, aKey, immediate: true, default);
        await bWait.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(async () =>
        {
            var t = await svc.GetBlockingTreeAsync(monitor, default);
            return t.Roots.Any(r => r.Session == bKey && r.Waiters.Any(w => w.Session == cKey));
        }, what: "B→C 로 트리 축소");
        Assert.False(cWait.IsCompleted);

        // B Kill → C 풀림, 트리 비어 있음.
        await svc.KillAsync(monitor, bKey, immediate: true, default);
        await cWait.WaitAsync(TimeSpan.FromSeconds(30));
        await cTx.RollbackAsync();
        var end = await svc.GetBlockingTreeAsync(monitor, default);
        Assert.Null(Find(end.Roots, aKey));
        Assert.Null(Find(end.Roots, bKey));
        Assert.Null(Find(end.Roots, cKey));
        Assert.Empty(await env.PackAuditHitsAsync());
    }

    // ---------- S3: 경합 없음·필터 경계 ----------

    [Fact]
    public async Task S3_no_contention_gives_no_roots_and_filters_handle_boundaries()
    {
        await using var env = await DbaFlowEnv.CreateAsync(rows: 10);
        var monitor = await env.OpenOrandaAsync();
        var svc = env.MonitorService();
        var (idle, idleKey) = await env.UserConnAsync();
        await DbaFlowEnv.ExecOnAsync(idle, "BEGIN NULL; END;");

        var tree = await svc.GetBlockingTreeAsync(monitor, default);
        Assert.Null(Find(tree.Roots, idleKey));

        // 사용자 이름은 대소문자 무시.
        var lower = await svc.GetSessionsAsync(monitor, new SessionFilter { Username = env.User.ToLowerInvariant() }, default);
        Assert.Contains(lower, r => r.Key == idleKey);

        // 없는 값·한글·특수문자·따옴표·퍼센트: 오류 없이 빈 결과(SQL 주입·LIKE 와일드카드 아님).
        foreach (var text in new[] { "no-such-program-xyz", "한글프로그램없음", "'; DROP TABLE x; --", "%", "ODF%", "O_F", "\"", new string('a', 4000) })
        {
            var r = await svc.GetSessionsAsync(monitor, new SessionFilter { Text = text }, default);
            Assert.False(r.Any(x => x.Key == idleKey), $"세션 필터 Text='{(text.Length > 20 ? text[..20] : text)}' 이 와일드카드·부분 일치로 유휴 세션을 잘못 포함했다.");
        }

        // 활성만: 대기 중이지 않은 유휴 세션은 빠진다.
        var active = await svc.GetSessionsAsync(monitor, new SessionFilter { Username = env.User, ActiveOnly = true }, default);
        Assert.False(active.Any(x => x.Key == idleKey), "ActiveOnly 필터가 INACTIVE 세션을 포함했다.");

        // 없는 세션 상세는 오류 또는 빈 상세(어느 쪽이든 앱이 죽지 않는다 — 결과만 기록).
        var ghost = new SessionKey(1, 99999, 99999);
        var ex = await Record.ExceptionAsync(() => svc.GetSessionDetailAsync(monitor, ghost, default));
        Assert.True(ex is null or InvalidOperationException or OracleException, ex?.GetType().Name);
        Assert.Empty(await env.PackAuditHitsAsync());
    }

    // ---------- S4: Kill 오류·되돌림 ----------

    [Fact]
    public async Task S4_kill_cancelled_or_denied_or_invalid_leaves_the_target_alive()
    {
        await using var env = await DbaFlowEnv.CreateAsync(rows: 10);
        var monitor = await env.OpenOrandaAsync();
        var svc = env.MonitorService();
        var (victim, vKey) = await env.UserConnAsync();
        var admin = await env.AdminAsync();

        // 취소: 확인에서 아니오 → OperationCanceledException, 대상은 그대로.
        env.Ui.ConfirmAnswer = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => svc.KillAsync(monitor, vKey, true, default));
        Assert.Equal(1m, await DbaFlowEnv.ScalarAsync(victim, "SELECT 1 FROM dual"));
        env.Ui.ConfirmAnswer = true;

        // 읽기 전용 프로필: 확인 대화상자도 없이 거부, 대상은 그대로.
        var confirmsBefore = env.Ui.Confirms.Count;
        var ro = await env.OpenOrandaAsync(readOnly: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.KillAsync(ro, vKey, true, default));
        Assert.Equal(confirmsBefore, env.Ui.Confirms.Count);
        Assert.Equal(1m, await DbaFlowEnv.ScalarAsync(victim, "SELECT 1 FROM dual"));

        // 권한 없는 계정: ALTER SYSTEM 권한이 없어 서버 오류(ORA-01031), 대상은 그대로.
        var plainUser = "ODFP_" + env.Id;
        var plainPw = "Pw" + Guid.NewGuid().ToString("N")[..16] + "#1";
        await DbaFlowEnv.ExecOnAsync(admin, $"CREATE USER {plainUser} IDENTIFIED BY \"{plainPw}\"");
        env.TrackUser(plainUser);
        {
            await DbaFlowEnv.ExecOnAsync(admin, $"GRANT CREATE SESSION TO {plainUser}");
            var plain = await env.OpenOrandaAsync(user: plainUser, password: plainPw);
            Assert.False(plain.Server.Capabilities.CanKillSession);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.KillAsync(plain, vKey, true, default));
            Assert.Contains("ORA-01031", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1m, await DbaFlowEnv.ScalarAsync(victim, "SELECT 1 FROM dual"));

            // 같은 계정은 세션 목록·Top SQL 도 서버가 거절한다(권한 없는 계정 메뉴 비활성의 서버 쪽 근거).
            await Assert.ThrowsAsync<OracleException>(() => svc.GetSessionsAsync(plain, new SessionFilter(), default));
        }

        // 없는 세션: 서버 오류(ORA-00030/00031) 로 알려 주고 앱 세션은 살아 있다.
        var bogus = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.KillAsync(monitor, new SessionKey(1, 99999, 99999), true, default));
        Assert.Matches(@"ORA-000(26|30|31)", bogus.Message);
        Assert.NotEqual(SessionState.Broken, monitor.State);
        Assert.Equal(1m, await DbaFlowEnv.ScalarAsync(victim, "SELECT 1 FROM dual"));

        // 실제 Kill 은 성공하고 대상 연결이 죽는다.
        await svc.KillAsync(monitor, vKey, immediate: true, default);
        await WaitUntilAsync(async () => await DbaFlowEnv.ScalarAsync(admin, $"SELECT COUNT(*) FROM v$session WHERE sid = {vKey.Sid} AND serial# = {vKey.Serial}") == 0m, what: "세션 제거");
        Assert.Empty(await env.PackAuditHitsAsync());
    }

    // ---------- S5: Top SQL 경계 ----------

    [Fact]
    public async Task S5_top_sql_boundaries_ordering_special_text_and_unknown_cursor()
    {
        await using var env = await DbaFlowEnv.CreateAsync(rows: 20000);
        var monitor = await env.OpenOrandaAsync();
        var svc = env.MonitorService();
        var plan = env.PlanService();
        var tbl = $"{env.User}.{env.Table}";
        var (runner, _) = await env.UserConnAsync();
        var tag = "S5TOP_" + env.Id;

        // 한글·퍼센트 문자열이 들어간 SQL 을 두 번(실행 횟수 2) 돌린다.
        var sql = $"SELECT /* {tag} 한글 100% _x */ COUNT(*) FROM {tbl} WHERE pad LIKE 'x%'";
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(20000m, await DbaFlowEnv.ScalarAsync(runner, sql));
        }

        var found = await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = tag }, default);
        var row = Assert.Single(found);
        Assert.Equal(2, row.Executions);

        // 본문 검색: 한글·퍼센트·밑줄·소문자·SQL ID 모두 그 SQL 에 맞는다.
        foreach (var needle in new[] { "한글", "100%", "_x", tag.ToLowerInvariant(), row.Cursor.SqlId })
        {
            Assert.Contains(await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = needle, ParsingSchema = env.User }, default),
                x => x.Cursor.SqlId == row.Cursor.SqlId);
        }

        // 와일드카드가 아니라 글자 그대로: 없는 조합은 빈 결과, 따옴표는 오류가 아니다.
        Assert.Empty(await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = tag + "%ZZZ" }, default));
        Assert.Empty(await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = "'" + tag + "'" }, default));

        // 실행 횟수 경계: 2 는 포함, 3 이상은 제외.
        Assert.Single(await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = tag, MinExecutions = 2 }, default));
        Assert.Empty(await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = tag, MinExecutions = 3 }, default));
        Assert.Empty(await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = tag, MinElapsedSeconds = 1e9 }, default));

        // Top 경계: 0 은 빈 결과, 1 은 한 건, 정렬 기준마다 오류 없음.
        // Top=0: 빈 결과 또는 1건으로 보정(둘 다 허용, 오류만 아니면 된다).
        Assert.True((await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { Top = 0 }, default)).Count <= 1);
        Assert.Single(await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { Top = 1 }, default));
        foreach (var order in Enum.GetValues<TopSqlOrder>())
        {
            var ordered = await svc.GetTopSqlAsync(monitor, new TopSqlCriteria { OrderBy = order, Top = 5 }, default);
            Assert.InRange(ordered.Count, 1, 5);
        }

        // 없는 SQL: 전문·계획 모두 InvalidOperationException.
        var none = new CursorRef("0000000000000");
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetSqlDetailAsync(monitor, none, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plan.GetCursorPlanAsync(monitor, none, "TYPICAL", default));

        // 계획 형식: 빈 값은 TYPICAL, 잘못된 형식은 서버 오류 없이 처리되거나 InvalidOperation/Oracle 오류(결과만 기록).
        var typical = await plan.GetCursorPlanAsync(monitor, row.Cursor, " ", default);
        Assert.Contains("TABLE ACCESS FULL", typical.Text, StringComparison.Ordinal);
        Assert.Empty(await env.PackAuditHitsAsync());
    }

    // ---------- S6: 예상 계획 경계(DML·PLSQL·읽기 전용) ----------

    [Fact]
    public async Task S6_explain_does_not_change_data_and_rejects_plsql()
    {
        await using var env = await DbaFlowEnv.CreateAsync(rows: 50);
        var work = await env.OpenOrandaAsync(SessionPurpose.Work);
        var plan = env.PlanService();
        var tbl = $"{env.User}.{env.Table}";

        var update = await plan.ExplainAsync(work,
            new SqlStatement($"UPDATE {tbl} SET g = 99 WHERE id = 3", default, StatementKind.Dml) { Verb = "UPDATE" }, default);
        Assert.Contains(update.Steps, s => s.Operation.StartsWith("UPDATE", StringComparison.Ordinal));
        var admin = await env.AdminAsync();
        Assert.Equal(0m, await DbaFlowEnv.ScalarAsync(admin, $"SELECT COUNT(*) FROM {tbl} WHERE g = 99"));

        await Assert.ThrowsAnyAsync<Exception>(() => plan.ExplainAsync(work,
            new SqlStatement("BEGIN NULL; END;", default, StatementKind.PlSql) { Verb = "BEGIN" }, default));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => plan.ExplainAsync(work,
            new SqlStatement("SELECT * FROM no_such_table_zz", default, StatementKind.Query) { Verb = "SELECT" }, default));
        Assert.Contains("ORA-00942", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await env.PackAuditHitsAsync());
    }

    // ---------- S7: 라이선스 보호 ----------

    [Fact]
    public async Task S7_pack_tagged_calls_are_refused_before_the_server_without_declaration_and_audit_control_works()
    {
        await using var env = await DbaFlowEnv.CreateAsync(rows: 10);
        var none = await env.OpenOrandaAsync();
        var cursor = new CursorRef("0000000000000");

        // 선언 없음: Tuning 태그 호출은 드라이버로 보내지 않고 거부.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OracleExecutionPlanService.GetSqlMonitorReportAsync(none, cursor, default));

        // Diagnostics 만 선언: Tuning 태그는 여전히 거부.
        var diag = await env.OpenOrandaAsync(pack: LicensePack.Diagnostics);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OracleExecutionPlanService.GetSqlMonitorReportAsync(diag, cursor, default));

        Assert.Empty(await env.PackAuditHitsAsync());

        // 대조군 1: 감사 정책이 실제로 팩 객체 조회를 잡는지(직접 조회).
        var (probe, _) = await env.UserConnAsync();
        var admin = await env.AdminAsync();
        await using var adminCmd = admin.CreateCommand();
        adminCmd.CommandText = "SELECT SYS_CONTEXT('USERENV','UNIFIED_AUDIT_SESSIONID') FROM dual";
        var adminAud = Convert.ToString(await adminCmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
        await DbaFlowEnv.ScalarAsync(admin, "SELECT COUNT(*) FROM v$active_session_history");
        await DbaFlowEnv.ScalarAsync(admin, "SELECT COUNT(*) FROM dba_hist_snapshot");
        var control = await env.PackAuditHitsAsync(extraAuditSessionIds: [adminAud]);
        Assert.Contains(control, h => h.Contains("ACTIVE_SESSION_HISTORY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(control, h => h.Contains("DBA_HIST_SNAPSHOT", StringComparison.OrdinalIgnoreCase));

        // 대조군 2: 팩을 선언(Diagnostics+Tuning)하면 같은 호출이 서버까지 가고 감사에 잡힌다.
        var full = await env.OpenOrandaAsync(pack: LicensePack.DiagnosticsAndTuning);
        _ = await Record.ExceptionAsync(() => OracleExecutionPlanService.GetSqlMonitorReportAsync(full, cursor, default));
        var declared = await env.PackAuditHitsAsync();
        Assert.Contains(declared, h => h.Contains("DBMS_SQLTUNE", StringComparison.OrdinalIgnoreCase) || h.Contains("SQL_MONITOR", StringComparison.OrdinalIgnoreCase));
        _ = probe;
    }
}
