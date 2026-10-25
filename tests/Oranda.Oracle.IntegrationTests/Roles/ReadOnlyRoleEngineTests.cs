using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.SqlPlus;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Roles;

// 소유: TSK-07-08. 조회 전용(R6) Role 실행 차단을 실제 엔진 조립(세션 관리자 + 실행 엔진 + 정책 관문 + 언어 서비스 + SQL*Plus 처리기 + 실제 드라이버)으로 본다.
// 게이트만 시험 대역이다(실제 FeatureGate 는 Oranda.App 에 있고 App 시험 RoleDbFlowTests 가 같은 시나리오를 실제 게이트로 돈다).
// DB: 26ai 상주 컨테이너(ORANDA_IT_HOST/PORT/SERVICE/ADMIN_USER/ADMIN_PASSWORD)와 11.2 컨테이너 oranda-ora11g(ORANDA_IT_11G_DATASOURCE/ORANDA_IT_11G_SYSTEM_PASSWORD).
// 환경 변수가 없으면 아무것도 하지 않는다(「돌렸다」 로 세지 않는다). ORANDA_IT_REQUIRE=1 이면 없을 때 실패한다. 시험 사용자는 실행마다 고유 이름으로 만들고 끝나면 지운다.

/// <summary>시험용 기능 게이트: 조회 전용 여부만 바꾼다(나머지는 모두 켬).</summary>
internal sealed class ToggleGate : IFeatureGate
{
    public bool ReadOnlyRole { get; set; }

    public IReadOnlyCollection<string> PendingRestart => [];

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public bool IsEnabled(string featureId) => true;

    public bool IsCommandEnabled(string commandId, Type handlerType) => true;

    public bool IsPanelEnabled(string panelId, Type viewModelType) => true;

    public bool IsTypeEnabled(Type registeredType) => true;
}

/// <summary>DB 한 대의 접속 정보와 시험 사용자. 26ai·11g 두 대가 같은 시험을 돈다.</summary>
public abstract class RoleDbFixtureBase : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public bool Enabled { get; private set; }

    public string User => "ORANDA_RL_" + _suffix;

    public string Password { get; } = "Zx" + Guid.NewGuid().ToString("N")[..20] + "#9";

    public string DataSource { get; private set; } = string.Empty;

    protected abstract string Label { get; }

    private string AdminPassword { get; set; } = string.Empty;

    private string AdminUser { get; set; } = "SYSTEM";

    protected static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    /// <summary>접속 정보를 환경 변수에서 읽는다. 없으면 false.</summary>
    protected abstract bool TryRead(out string dataSource, out string adminUser, out string adminPassword);

    public async Task InitializeAsync()
    {
        if (!TryRead(out var ds, out var admin, out var pw))
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException($"{Label} 접속 환경 변수가 없다.");
            }

            return;
        }

        Enabled = true;
        (DataSource, AdminUser, AdminPassword) = (ds, admin, pw);
        await using var conn = await AdminAsync();
        foreach (var sql in new[]
        {
            $"CREATE USER {User} IDENTIFIED BY \"{Password}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users",
            $"GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, CREATE SEQUENCE, SELECT_CATALOG_ROLE TO {User}",
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await AdminAsync();
        var sessions = new List<(long Sid, long Serial)>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT sid, serial# FROM v$session WHERE username = :u";
            q.Parameters.Add(new OracleParameter("u", User));
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                sessions.Add((r.GetInt64(0), r.GetInt64(1)));
            }
        }

        foreach (var (sid, serial) in sessions)
        {
            await TryAsync(conn, string.Create(CultureInfo.InvariantCulture, $"ALTER SYSTEM KILL SESSION '{sid},{serial}' IMMEDIATE"));
        }

        for (var i = 0; i < 20; i++)
        {
            if (await TryAsync(conn, $"DROP USER {User} CASCADE"))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    private async Task<OracleConnection> AdminAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("admin", AdminUser, DataSource).BuildConnectionString(AdminPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<bool> TryAsync(OracleConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (OracleException)
        {
            return false;
        }
    }

    public async Task<OracleConnection> OpenUserAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("direct", User, DataSource).BuildConnectionString(Password));
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>시험 사용자로 직접 연 연결에서 문장 하나(앱 경로를 거치지 않는 준비·정답 확인용).</summary>
    public async Task SetupAsync(string sql)
    {
        await using var conn = await OpenUserAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string> ScalarAsync(string sql)
    {
        await using var conn = await OpenUserAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

public sealed class Role26aiFixture : RoleDbFixtureBase
{
    protected override string Label => "26ai(ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD)";

    protected override bool TryRead(out string dataSource, out string adminUser, out string adminPassword)
    {
        (dataSource, adminUser, adminPassword) = (string.Empty, string.Empty, string.Empty);
        if (Env("HOST") is not { } host || Env("SERVICE") is not { } service || Env("ADMIN_USER") is not { } user || Env("ADMIN_PASSWORD") is not { } pw)
        {
            return false;
        }

        (dataSource, adminUser, adminPassword) = ($"{host}:{Env("PORT") ?? "1521"}/{service}", user, pw);
        return true;
    }
}

public sealed class Role11gFixture : RoleDbFixtureBase
{
    protected override string Label => "11.2(ORANDA_IT_11G_DATASOURCE/ORANDA_IT_11G_SYSTEM_PASSWORD)";

    protected override bool TryRead(out string dataSource, out string adminUser, out string adminPassword)
    {
        (dataSource, adminUser, adminPassword) = (string.Empty, "SYSTEM", string.Empty);
        if (Env("11G_DATASOURCE") is not { Length: > 0 } ds || Env("11G_SYSTEM_PASSWORD") is not { Length: > 0 } pw)
        {
            return false;
        }

        // 개발 PC 의 podman 포트 중계는 긴급(OOB) 데이터를 버린다. 실행 취소를 쓰지 않는 이 시험에는 영향이 없지만 다른 11g 시험과 같게 맞춘다.
        if (Env("11G_DISABLE_OOB") == "1")
        {
            OracleConfiguration.DisableOOB = true;
        }

        (dataSource, adminPassword) = (ds, pw);
        return true;
    }
}

/// <summary>조회 전용 Role 차단 시험 한 벌의 실행 장치.</summary>
internal sealed class RoleEngineRig : IAsyncDisposable
{
    public static readonly Dictionary<string, BindValue> NoBinds = [];

    private readonly RoleDbFixtureBase _db;
    private readonly OracleSessionManager _manager;
    private readonly List<OracleSession> _sessions = [];

    public RoleEngineRig(RoleDbFixtureBase db)
    {
        _db = db;
        Bus = new EventBus();
        _manager = new OracleSessionManager(Bus);
        Language = new LanguageService();
        Policy = new ExecutionPolicy(Gate);
        var processor = new SqlPlusCommandProcessor(() => Exec!, () => Language, () => Bus);
        Exec = new OracleExecutionService(Policy, Ui, Bus, processor, pageSize: 100);
    }

    public EventBus Bus { get; }

    public LanguageService Language { get; }

    public ToggleGate Gate { get; } = new();

    public ExecutionPolicy Policy { get; }

    public ScriptedInteraction Ui { get; } = new();

    public OracleExecutionService Exec { get; }

    public string Unique { get; } = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    public string Table => "T_" + Unique;

    public async Task<OracleSession> SessionAsync(bool readOnlyProfile = false, ConnectionEnvironment env = ConnectionEnvironment.Development)
    {
        using var cred = new SecureCredential(_db.Password);
        var profile = new ConnectionProfile("role-" + env, _db.User, _db.DataSource, Environment: env, ReadOnly: readOnlyProfile, Id: Guid.NewGuid());
        var s = (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
        _sessions.Add(s);
        return s;
    }

    /// <summary>편집기 실행과 같은 방식: 텍스트를 나눠 문장마다 엔진에 보낸다.</summary>
    public async Task<List<ExecutionResult>> RunTextAsync(ISession session, string text, HistorySource source = HistorySource.Editor)
    {
        var results = new List<ExecutionResult>();
        foreach (var stmt in Language.Split(text, SplitOptions.Default))
        {
            var r = await Exec.ExecuteAsync(session, stmt, NoBinds, new ExecutionOptions { Source = source }, default);
            if (r.ResultSet is not null)
            {
                await r.ResultSet.DisposeAsync();
            }

            results.Add(r);
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }
    }
}

/// <summary>DB 없이 도는 시험 거리: 실제 분류기(언어 서비스) + 정책 관문. R6 단독/아님·프로필 읽기 전용과의 차이.</summary>
public sealed class ReadOnlyRoleClassificationTests(ITestOutputHelper output)
{
    private static readonly LanguageService Language = new();

    private static readonly ServerInfo Server = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, false, false, false, false));

    private static async Task<PolicyDecision> JudgeAsync(string text, bool viewerOnly, bool readOnlyProfile = false)
    {
        var stmt = Assert.Single(Language.Split(text, SplitOptions.Default));
        var policy = new ExecutionPolicy(new ToggleGate { ReadOnlyRole = viewerOnly });
        var profile = new ConnectionProfile("p", "u", "h:1521/s", ReadOnly: readOnlyProfile);
        return await policy.EvaluateAsync(new PolicyContext(profile, Server, HistorySource.Editor), stmt, default);
    }

    [Theory]
    [InlineData("update t set a = 1 where id = 1")]
    [InlineData("update t set a = 1")]
    [InlineData("delete from t")]
    [InlineData("insert into t values (1)")]
    [InlineData("insert all into t values (1) into t values (2) select 1 from dual")]
    [InlineData("merge into t using dual on (1 = 1) when matched then update set a = 1")]
    [InlineData("create table t2 (a number)")]
    [InlineData("create or replace view v as select 1 a from dual")]
    [InlineData("drop table t")]
    [InlineData("truncate table t")]
    [InlineData("alter table t add b number")]
    [InlineData("alter session set nls_date_format = 'YYYY'")]
    [InlineData("alter system flush shared_pool")]
    [InlineData("grant select on t to public")]
    [InlineData("comment on table t is 'x'")]
    [InlineData("lock table t in exclusive mode")]
    [InlineData("begin null; end;")]
    [InlineData("declare x number; begin x := 1; end;")]
    [InlineData("<<l>> begin null; end;")]
    [InlineData("call p()")]
    [InlineData("exec p")]
    [InlineData("create or replace procedure p as begin null; end;")]
    [InlineData("flashback table t to before drop")]
    [InlineData("purge recyclebin")]
    [InlineData("analyze table t compute statistics")]
    [InlineData("select 1 from dual; delete from t")] // 한 덩어리로 나뉘지 않아도 쓰기 문장이 섞이면 문장 단위로 걸린다(아래 Split)
    public async Task Viewer_only_denies_the_statement_but_not_when_another_role_is_chosen(string text)
    {
        foreach (var stmt in Language.Split(text, SplitOptions.Default))
        {
            var viewer = new ExecutionPolicy(new ToggleGate { ReadOnlyRole = true });
            var open = new ExecutionPolicy(new ToggleGate { ReadOnlyRole = false });
            var profile = new ConnectionProfile("p", "u", "h:1521/s");
            var d = await viewer.EvaluateAsync(new PolicyContext(profile, Server, HistorySource.Editor), stmt, default);
            var o = await open.EvaluateAsync(new PolicyContext(profile, Server, HistorySource.Editor), stmt, default);
            if (d.Kind == PolicyDecisionKind.Deny)
            {
                Assert.Equal(PolicyRule.ReadOnlyRole, d.Rule);
                Assert.Contains("조회 전용 Role 에서는", d.Message, StringComparison.Ordinal);
                Assert.NotEqual(PolicyRule.ReadOnlyRole, o.Rule); // 다른 Role 을 함께 고르면 이 규칙으로는 거부하지 않는다
            }
            else
            {
                Assert.True(stmt.Kind is StatementKind.Query or StatementKind.SqlPlus or StatementKind.TransactionControl, $"{stmt.Kind}"); // 거부 아님 = 읽는 문장·SQL*Plus(EXEC 는 안쪽에서 다시 관문을 지난다)
            }
        }
    }

    [Theory]
    [InlineData("select * from t", true)]
    [InlineData("with x as (select 1 a from dual) select * from x", true)]
    [InlineData("select * from t for update", true)] // 행 잠금도 질의로 본다(읽기 전용 프로필과 같은 기준, 아래 DB 시험이 잠금 확인)
    [InlineData("commit", true)]
    [InlineData("rollback", true)]
    [InlineData("savepoint a", true)]
    [InlineData("set transaction read only", true)]
    [InlineData("explain plan for update t set a = 1 where id = 1", true)]
    [InlineData("set serveroutput on", true)]
    [InlineData("desc t", true)]
    [InlineData("show user", true)]
    public async Task Viewer_only_lets_queries_transaction_control_sqlplus_and_explain_through(string text, bool allowed)
    {
        var d = await JudgeAsync(text, viewerOnly: true);

        Assert.Equal(allowed, d.Kind != PolicyDecisionKind.Deny);
    }

    [Theory]
    [InlineData("set serveroutput on size 1000000")] // 바깥 문장은 통과, 안쪽 블록이 거부된다(엔진 시험이 본다)
    [InlineData("set autotrace on")]
    [InlineData("exec dbms_output.put_line('x')")]
    [InlineData("alter session set statistics_level = all")]
    [InlineData("alter session set events '10053 trace name context forever, level 1'")]
    [InlineData("alter session set sql_trace = true")]
    public async Task Observed_statements_that_viewer_only_treats_like_a_read_only_profile(string text)
    {
        // followups(07-05): R6 에서 SET SERVEROUTPUT SIZE·계획 통계 재실행·옵티마이저 트레이스도 막힌다 — 읽기 전용 프로필과 같은지 견준다.
        var viewer = await JudgeAsync(text, viewerOnly: true);
        var profile = await JudgeAsync(text, viewerOnly: false, readOnlyProfile: true);
        var both = await JudgeAsync(text, viewerOnly: true, readOnlyProfile: true);
        output.WriteLine($"{text} => R6 {viewer.Kind}/{viewer.Rule} · 읽기 전용 프로필 {profile.Kind}/{profile.Rule} · 둘 다 {both.Kind}/{both.Rule}");

        Assert.Equal(profile.Kind == PolicyDecisionKind.Deny, viewer.Kind == PolicyDecisionKind.Deny); // 같은 기준
        if (profile.Kind == PolicyDecisionKind.Deny)
        {
            Assert.Equal(PolicyRule.ReadOnlyProfile, both.Rule); // 둘 다면 프로필 규칙의 문구가 먼저
        }
        Assert.Equal(PolicyDecisionKind.Allow, (await JudgeAsync(text, viewerOnly: false)).Kind); // 다른 Role 을 함께 고르면 풀린다
    }
}

/// <summary>실제 DB 에서 조회 전용 Role 실행 차단. 26ai 와 11.2 에서 같은 시험을 돈다.</summary>
public abstract class ReadOnlyRoleEngineTestsBase(RoleDbFixtureBase db, ITestOutputHelper output)
{
    private const string Denied = "조회 전용 Role 에서는";

    protected RoleDbFixtureBase Db { get; } = db;

    private bool Skip() => !Db.Enabled;

    private async Task<long> CountAsync(string sql) => Convert.ToInt64(await Db.ScalarAsync(sql), CultureInfo.InvariantCulture);

    private static async Task<long> UserCallsAsync(RoleEngineRig rig, OracleSession session)
    {
        var r = await rig.Exec.ExecuteAsync(
            session,
            Assert.Single(rig.Language.Split("SELECT value FROM v$mystat WHERE statistic# = (SELECT statistic# FROM v$statname WHERE name = 'user calls')", SplitOptions.Default)),
            RoleEngineRig.NoBinds,
            new ExecutionOptions { Source = HistorySource.Monitor },
            default);
        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        var page = await r.ResultSet!.GetPageAsync(0, default);
        await r.ResultSet.DisposeAsync();
        return Convert.ToInt64(page.Columns[0][0], CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Select_runs_and_every_write_is_denied_without_reaching_the_server()
    {
        if (Skip())
        {
            return;
        }

        await using var rig = new RoleEngineRig(Db);
        var t = rig.Table;
        var fresh = "N_" + rig.Unique;
        await Db.SetupAsync($"CREATE TABLE {t} (id NUMBER PRIMARY KEY, v VARCHAR2(30))");
        await Db.SetupAsync($"INSERT INTO {t} VALUES (1, 'a')");
        await Db.SetupAsync("COMMIT");
        rig.Gate.ReadOnlyRole = true; // R6 단독
        var session = await rig.SessionAsync();
        output.WriteLine($"서버 {session.Server.Version}");

        // SELECT 는 실행되고 행이 온다.
        var ok = await rig.Exec.ExecuteAsync(session, Assert.Single(rig.Language.Split($"SELECT id, v FROM {t}", SplitOptions.Default)), RoleEngineRig.NoBinds, new ExecutionOptions { Source = HistorySource.Editor }, default);
        Assert.Equal(ExecutionStatus.Succeeded, ok.Status);
        var firstPage = await ok.ResultSet!.GetPageAsync(0, default);
        Assert.Equal(1, firstPage.RowCount);
        Assert.Equal("a", Convert.ToString(firstPage.Columns[1][0], CultureInfo.InvariantCulture));
        await ok.ResultSet.DisposeAsync();

        string[] writes =
        [
            $"UPDATE {t} SET v = 'x' WHERE id = 1",
            $"UPDATE {t} SET v = 'x'",
            $"DELETE FROM {t} WHERE id = 1",
            $"INSERT INTO {t} VALUES (2, 'b')",
            $"MERGE INTO {t} d USING dual s ON (d.id = 3) WHEN NOT MATCHED THEN INSERT (id, v) VALUES (3, 'm')",
            $"CREATE TABLE {fresh} (x NUMBER)",
            $"ALTER TABLE {t} ADD (extra NUMBER)",
            $"TRUNCATE TABLE {t}",
            $"DROP TABLE {t}",
            $"BEGIN INSERT INTO {t} VALUES (4, 'blk'); COMMIT; END;",
            $"DECLARE n NUMBER; BEGIN DELETE FROM {t}; END;",
            $"CREATE OR REPLACE PROCEDURE {fresh} AS BEGIN NULL; END;",
            $"COMMENT ON TABLE {t} IS 'x'",
            $"LOCK TABLE {t} IN EXCLUSIVE MODE",
            "ALTER SESSION SET nls_date_format = 'YYYY'",
        ];
        var objectsBefore = await CountAsync("SELECT COUNT(*) FROM user_objects");

        // 서버가 받은 호출 수(user calls)로 「드라이버를 부르기 전에 거부」 를 확인한다: 거부 사이에 끼워도 호출 수가 늘지 않는다.
        var baseline1 = await UserCallsAsync(rig, session);
        var baseline2 = await UserCallsAsync(rig, session);
        var step = baseline2 - baseline1;
        var before = await UserCallsAsync(rig, session);
        foreach (var source in Enum.GetValues<HistorySource>())
        {
            foreach (var sql in writes)
            {
                var stmt = Assert.Single(rig.Language.Split(sql, SplitOptions.Default));
                var r = await rig.Exec.ExecuteAsync(session, stmt, RoleEngineRig.NoBinds, new ExecutionOptions { Source = source }, default);
                Assert.True(r.Status == ExecutionStatus.Denied && r.Policy?.Rule == PolicyRule.ReadOnlyRole, $"{source} / {sql} => {r.Status}/{r.Policy?.Rule}");
                Assert.Contains(Denied, r.Policy!.Message, StringComparison.Ordinal);
                Assert.Null(r.ResultSet);
                Assert.False(session.HasPendingTransaction);
            }
        }

        var after = await UserCallsAsync(rig, session);
        output.WriteLine($"user calls: 조회 두 번 사이 {step}, 거부 {writes.Length * Enum.GetValues<HistorySource>().Length}건 사이 {after - before}");
        Assert.Equal(step, after - before);
        Assert.Empty(rig.Ui.Confirms); // 확인 창도 뜨지 않았다

        // 서버에는 아무것도 들어가지 않았다.
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {t}"));
        Assert.Equal("a", await Db.ScalarAsync($"SELECT v FROM {t} WHERE id = 1"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM user_tab_columns WHERE table_name = '{t}' AND column_name = 'EXTRA'"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM user_objects WHERE object_name = '{fresh}'"));
        Assert.Equal(objectsBefore, await CountAsync("SELECT COUNT(*) FROM user_objects"));

        // 스크립트 실행(F5)도 같은 관문: 중간의 쓰기만 거부되고 뒤의 조회는 (오류 때 계속 모드면) 이어진다.
        var script = new List<ExecutionResult>();
        await foreach (var r in rig.Exec.ExecuteScriptAsync(session, rig.Language.Split($"SELECT 1 FROM dual;\nDELETE FROM {t};\nSELECT 2 FROM dual;", SplitOptions.Default), ScriptErrorMode.Continue, new ExecutionOptions { Source = HistorySource.Editor }, default))
        {
            if (r.ResultSet is not null)
            {
                await r.ResultSet.DisposeAsync();
            }

            script.Add(r);
        }

        Assert.Equal([ExecutionStatus.Succeeded, ExecutionStatus.Denied, ExecutionStatus.Succeeded], script.Select(r => r.Status));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {t}"));

        // 트랜잭션 제어·EXPLAIN 은 막지 않는다.
        foreach (var sql in new[] { "COMMIT", "ROLLBACK", "SAVEPOINT s1", $"EXPLAIN PLAN FOR SELECT * FROM {t} WHERE id = 1" })
        {
            var r = Assert.Single(await rig.RunTextAsync(session, sql));
            Assert.True(r.Status == ExecutionStatus.Succeeded, $"{sql} => {r.Status} {r.Error?.Message} {r.Policy?.Message}");
        }

        await rig.RunTextAsync(session, "ROLLBACK");
        await Db.SetupAsync($"DROP TABLE {t} PURGE");
    }

    [Fact]
    public async Task Sqlplus_exec_and_script_file_paths_pass_through_the_same_gate_inside()
    {
        // 「엔진 밖 쓰기 경로」 점검(TRD 10.7.4): EXEC 와 @파일 은 SQL*Plus 문장으로 분류되어 바깥 관문은 통과하지만, 처리기가 안쪽에서
        // 실행 엔진을 다시 불러(PL/SQL 블록·파일 안 문장) 같은 정책 관문을 지난다.
        if (Skip())
        {
            return;
        }

        await using var rig = new RoleEngineRig(Db);
        var t = rig.Table;
        var proc = "P_" + rig.Unique;
        await Db.SetupAsync($"CREATE TABLE {t} (id NUMBER PRIMARY KEY, v VARCHAR2(30))");
        await Db.SetupAsync($"INSERT INTO {t} VALUES (1, 'a')");
        await Db.SetupAsync("COMMIT");
        await Db.SetupAsync($"CREATE OR REPLACE PROCEDURE {proc} AS BEGIN INSERT INTO {t} VALUES (7, 'proc'); COMMIT; END;");
        var file = Path.Combine(Path.GetTempPath(), $"oranda-role-{rig.Unique}.sql");
        await File.WriteAllTextAsync(file, $"DELETE FROM {t};\nINSERT INTO {t} VALUES (8, 'file');\nCOMMIT;\n");
        rig.Gate.ReadOnlyRole = true;
        var session = await rig.SessionAsync();
        try
        {
            var exec = Assert.Single(await rig.RunTextAsync(session, $"EXEC {proc}"));
            Assert.Equal(ExecutionStatus.Denied, exec.Status);
            Assert.Equal(PolicyRule.ReadOnlyRole, exec.Policy?.Rule);

            var results = await rig.RunTextAsync(session, "@" + file);
            output.WriteLine("@파일 결과: " + string.Join(" | ", results.Select(r => $"{r.Status} {r.Error?.Code} {r.Error?.Message} {r.Policy?.Message}")));
            Assert.Contains(results, r => (r.Error?.Message ?? r.Policy?.Message ?? string.Empty).Contains(Denied, StringComparison.Ordinal)); // 파일 안 쓰기 문장이 조회 전용 문구로 거부됐다
            Assert.DoesNotContain(results, r => r.Status == ExecutionStatus.Succeeded && r.RowsAffected > 0);
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {t}"));
            Assert.Equal("a", await Db.ScalarAsync($"SELECT v FROM {t} WHERE id = 1"));
            Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM {t} WHERE id IN (7, 8)"));

            // SET SERVEROUTPUT ON|OFF [SIZE n]: 처리기가 안쪽에서 DBMS_OUTPUT.ENABLE/DISABLE 블록을 보내므로 그 블록이 관문에서 거부된다 —
            // 읽기 전용 프로필도 같다(followups 07-05). 거부 문구는 사용자가 친 명령이 아니라 내부 블록을 말한다(결함 D-3).
            foreach (var sql in new[] { "SET SERVEROUTPUT ON", "SET SERVEROUTPUT ON SIZE 100000", "SET SERVEROUTPUT OFF" })
            {
                var set = Assert.Single(await rig.RunTextAsync(session, sql));
                output.WriteLine($"{sql} => {set.Status} / {set.Policy?.Message}");
                Assert.Equal(ExecutionStatus.Denied, set.Status);
                Assert.Equal(PolicyRule.ReadOnlyRole, set.Policy?.Rule);
            }

            // R6 + 다른 Role: 같은 EXEC 가 돈다.
            rig.Gate.ReadOnlyRole = false;
            var allowed = Assert.Single(await rig.RunTextAsync(session, $"EXEC {proc}"));
            Assert.Equal(ExecutionStatus.Succeeded, allowed.Status);
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {t} WHERE id = 7"));
        }
        finally
        {
            File.Delete(file);
            await Db.SetupAsync($"DROP PROCEDURE {proc}");
            await Db.SetupAsync($"DROP TABLE {t} PURGE");
        }
    }

    [Fact]
    public async Task Choosing_viewer_together_with_another_role_lets_the_same_statements_run()
    {
        if (Skip())
        {
            return;
        }

        await using var rig = new RoleEngineRig(Db);
        var t = rig.Table;
        var made = "M_" + rig.Unique;
        await Db.SetupAsync($"CREATE TABLE {t} (id NUMBER PRIMARY KEY, v VARCHAR2(30))");
        await Db.SetupAsync($"INSERT INTO {t} VALUES (1, 'a')");
        await Db.SetupAsync("COMMIT");
        var session = await rig.SessionAsync();
        rig.Gate.ReadOnlyRole = false; // R6 + R1

        foreach (var sql in new[]
        {
            $"CREATE TABLE {made} (x NUMBER)", // DDL 은 앞선 미커밋을 암묵 커밋하므로 맨 앞에 둔다
            $"UPDATE {t} SET v = 'u' WHERE id = 1",
            $"INSERT INTO {t} VALUES (2, 'b')",
            $"DELETE FROM {t} WHERE id = 2",
            $"BEGIN INSERT INTO {t} VALUES (5, 'blk'); END;",
        })
        {
            var r = Assert.Single(await rig.RunTextAsync(session, sql));
            Assert.True(r.Status == ExecutionStatus.Succeeded, $"{sql} => {r.Status} {r.Error?.Message} {r.Policy?.Message}");
        }

        Assert.Equal("a", await Db.ScalarAsync($"SELECT v FROM {t} WHERE id = 1")); // 미커밋: 다른 연결에는 아직 안 보인다
        await rig.RunTextAsync(session, "COMMIT");
        Assert.Equal("u", await Db.ScalarAsync($"SELECT v FROM {t} WHERE id = 1"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM user_tables WHERE table_name = '{made}'"));

        // 되돌림: 다시 R6 단독 → 같은 세션의 다음 문장부터 거부.
        rig.Gate.ReadOnlyRole = true;
        var denied = Assert.Single(await rig.RunTextAsync(session, $"DELETE FROM {t} WHERE id = 5"));
        Assert.Equal(ExecutionStatus.Denied, denied.Status);
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {t} WHERE id = 5"));
        rig.Gate.ReadOnlyRole = false;
        var deleted = Assert.Single(await rig.RunTextAsync(session, $"DELETE FROM {t} WHERE id = 5"));
        Assert.Equal(ExecutionStatus.Succeeded, deleted.Status);
        await rig.RunTextAsync(session, "COMMIT");

        await Db.SetupAsync($"DROP TABLE {t} PURGE");
        await Db.SetupAsync($"DROP TABLE {made} PURGE");
    }

    [Fact]
    public async Task Viewer_only_with_a_read_only_profile_gives_the_profile_message_and_never_touches_data()
    {
        if (Skip())
        {
            return;
        }

        await using var rig = new RoleEngineRig(Db);
        var t = rig.Table;
        await Db.SetupAsync($"CREATE TABLE {t} (id NUMBER PRIMARY KEY, v VARCHAR2(30))");
        await Db.SetupAsync($"INSERT INTO {t} VALUES (1, 'a')");
        await Db.SetupAsync("COMMIT");
        rig.Gate.ReadOnlyRole = true;
        var session = await rig.SessionAsync(readOnlyProfile: true);

        var r = Assert.Single(await rig.RunTextAsync(session, $"UPDATE {t} SET v = 'x' WHERE id = 1"));

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, r.Policy?.Rule); // 프로필 규칙이 먼저(문구가 접속 이름을 말한다)
        Assert.Equal("a", await Db.ScalarAsync($"SELECT v FROM {t} WHERE id = 1"));
        await Db.SetupAsync($"DROP TABLE {t} PURGE");
    }

    [Fact]
    public async Task Observation_viewer_only_has_no_database_level_read_only_guard_unlike_a_read_only_profile()
    {
        // 결과서 관찰: 읽기 전용 프로필은 문장마다 DBMS_TRANSACTION.READ_ONLY 로 DB 쪽에서도 막는다(SAFE-02 이중 방어).
        // R6 는 정책 관문만이다(D-006 「Role 은 보안이 아니라 편의」): 질의로 분류되는 SELECT … FOR UPDATE 는 행 잠금을 쥐고,
        // 질의에서 부르는 자율 트랜잭션 함수의 쓰기는 막지 못한다. 지금 동작을 고정한다(고치면 이 단정을 뒤집는다).
        if (Skip())
        {
            return;
        }

        await using var rig = new RoleEngineRig(Db);
        var t = rig.Table;
        var fn = "F_" + rig.Unique;
        await Db.SetupAsync($"CREATE TABLE {t} (id NUMBER PRIMARY KEY, v VARCHAR2(30))");
        await Db.SetupAsync($"INSERT INTO {t} VALUES (1, 'a')");
        await Db.SetupAsync("COMMIT");
        await Db.SetupAsync($"CREATE OR REPLACE FUNCTION {fn} RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO {t} VALUES (99, 'side'); COMMIT; RETURN 1; END;");
        rig.Gate.ReadOnlyRole = true;
        var session = await rig.SessionAsync();
        var profileSession = await rig.SessionAsync(readOnlyProfile: true);

        // 1. FOR UPDATE: R6 는 통과해 행 잠금을 쥔다. 읽기 전용 프로필은 DB 가 ORA-01456 으로 거부한다.
        var lockRun = Assert.Single(await rig.RunTextAsync(session, $"SELECT * FROM {t} WHERE id = 1 FOR UPDATE"));
        Assert.Equal(ExecutionStatus.Succeeded, lockRun.Status);
        var locked = await IsRowLockedAsync(t);
        output.WriteLine($"R6 SELECT … FOR UPDATE 뒤 다른 세션의 NOWAIT 잠금: {(locked ? "잠겨 있음(ORA-00054)" : "잠기지 않음")}");
        Assert.True(locked);
        await rig.RunTextAsync(session, "ROLLBACK");
        Assert.False(await IsRowLockedAsync(t));

        var profileRun = Assert.Single(await rig.RunTextAsync(profileSession, $"SELECT * FROM {t} WHERE id = 1 FOR UPDATE"));
        output.WriteLine($"읽기 전용 프로필 SELECT … FOR UPDATE: {profileRun.Status} {profileRun.Error?.Number} {profileRun.Error?.Message}");
        Assert.NotEqual(ExecutionStatus.Succeeded, profileRun.Status);
        Assert.False(await IsRowLockedAsync(t));

        // 2. 자율 트랜잭션 함수: R6 에서는 SELECT 로 불려 서버에 행이 들어간다.
        var before = await CountAsync($"SELECT COUNT(*) FROM {t}");
        var call = await rig.Exec.ExecuteAsync(session, Assert.Single(rig.Language.Split($"SELECT {fn}() FROM dual", SplitOptions.Default)), RoleEngineRig.NoBinds, new ExecutionOptions { Source = HistorySource.Editor }, default);
        if (call.ResultSet is not null)
        {
            await call.ResultSet.GetPageAsync(0, default);
            await call.ResultSet.DisposeAsync();
        }

        var after = await CountAsync($"SELECT COUNT(*) FROM {t}");
        output.WriteLine($"자율 트랜잭션 함수를 SELECT 로 호출: 상태 {call.Status}, 행 수 {before} → {after}");
        Assert.Equal(before + 1, after);

        await Db.SetupAsync($"DROP FUNCTION {fn}");
        await Db.SetupAsync($"DROP TABLE {t} PURGE");
    }

    private async Task<bool> IsRowLockedAsync(string table)
    {
        await using var conn = await Db.OpenUserAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {table} WHERE id = 1 FOR UPDATE NOWAIT";
        try
        {
            await cmd.ExecuteScalarAsync();
            return false;
        }
        catch (OracleException ex) when (ex.Number == 54)
        {
            return true;
        }
    }
}

[Trait("Category", "Integration")]
public sealed class ReadOnlyRoleEngine26aiTests(Role26aiFixture db, ITestOutputHelper output) : ReadOnlyRoleEngineTestsBase(db, output), IClassFixture<Role26aiFixture>;

[Trait("Category", "Integration")]
public sealed class ReadOnlyRoleEngine11gTests(Role11gFixture db, ITestOutputHelper output) : ReadOnlyRoleEngineTestsBase(db, output), IClassFixture<Role11gFixture>;
