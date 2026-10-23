using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Language;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;
using Oranda.Language;
using Oranda.Language.Editing;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.SqlPlus;
using Oranda.Oracle.Transfer;

namespace Oranda.Oracle.IntegrationTests.Integration;

// 소유: TSK-11-04. 보안·안전장치 교차 시험(DB 쪽). 운영 라벨·읽기 전용 프로필이 편집기 실행·그리드 편집·가져오기·스키마 관리·
// PL/SQL·SQL*Plus(@파일·EXEC)·모니터 Kill 의 모든 경로에서 같은 정책 관문 결과를 내는지 실제 Oracle 로 확인한다.
// 상주 컨테이너(oracle-26ai-free)를 쓰며 환경 변수: ORANDA_IT_HOST, ORANDA_IT_PORT(1521), ORANDA_IT_SERVICE,
// ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD(사용자를 만들 수 있는 계정). 시험 사용자는 실행마다 고유 이름으로 만들고 끝나면 지운다.
// 환경 변수가 없으면 시험은 아무것도 안 하고 끝난다 — 그 경우 「돌렸다」 로 세지 않는다. ORANDA_IT_REQUIRE=1 이면 없을 때 실패한다.

/// <summary>실제 DB 없이 도는 분류·정책 교차 시험: 언어 서비스가 만든 문장을 정책 관문에 넣어 읽기 전용·운영 라벨 결과를 본다.</summary>
public sealed class SafetyCrossPolicyTests
{
    private static readonly ServerInfo Server = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, false, false, false, false));

    private static readonly LanguageService Language = new();

    private static async Task<PolicyDecision> JudgeAsync(string text, ConnectionEnvironment env, bool readOnly)
    {
        var profile = new ConnectionProfile("p", "u", "h:1521/s", Environment: env, ReadOnly: readOnly);
        var statements = Language.Split(text, SplitOptions.Default);
        Assert.NotEmpty(statements);
        PolicyDecision? worst = null;
        foreach (var stmt in statements)
        {
            var d = await new ExecutionPolicy().EvaluateAsync(new PolicyContext(profile, Server, HistorySource.Editor), stmt, default);
            if (worst is null || d.Kind > worst.Kind)
            {
                worst = d;
            }
        }

        return worst!;
    }

    /// <summary>상태를 바꾸는 문장 모음. 읽기 전용은 모두 거부, 운영 라벨은 모두 거부 또는 확인이어야 한다.</summary>
    public static IEnumerable<object[]> Mutating() =>
    [
        ["insert into t values (1)"],
        ["INSERT ALL INTO t VALUES (1) INTO t VALUES (2) SELECT 1 FROM dual"],
        ["update t set a = 1"],
        ["UPDATE t SET a = 1 WHERE 1 = 1"],
        ["delete t"],
        ["delete from t where id = 1"],
        ["merge into t using dual on (1 = 1) when matched then update set a = 1"],
        ["lock table t in exclusive mode"],
        ["call p()"],
        ["create table t (a number)"],
        ["create or replace procedure p as begin null; end;"],
        ["CREATE /* c */ INDEX i ON t(a)"],
        ["-- c\nDROP TABLE t"],
        ["/* c */ DROP TABLE t"],
        ["alter table t add c number"],
        ["alter session set nls_date_format = 'YYYY'"],
        ["alter system kill session '1,2'"],
        ["alter user x identified by y"],
        ["truncate table t"],
        ["rename a to b"],
        ["comment on table t is 'x'"],
        ["grant dba to x"],
        ["revoke dba from x"],
        ["analyze table t compute statistics"],
        ["audit select on t"],
        ["noaudit select on t"],
        ["flashback table t to before drop"],
        ["purge recyclebin"],
        ["associate statistics with columns t.c using x"],
        ["begin null; end;"],
        ["declare x number; begin null; end;"],
        ["<<lbl>> begin null; end;"],
        ["foo"],
        ["table t"],
        ["  \n  delete from t where id = 1"],
        ["DELETE\nFROM t WHERE id = 1"],
        ["DeLeTe FROM t WHERE id = 1"],
        ["alter database open"],
        ["create user u identified by pw"],
        ["drop user u cascade"],
        ["select 1 from dual; delete from t where id = 1"],
        ["select 1 from dual;\nselect 2 from dual;\ndrop table t"],
    ];

    [Theory]
    [MemberData(nameof(Mutating))]
    public async Task Read_only_profile_denies_every_mutating_statement_shape(string text)
    {
        foreach (var env in new[] { ConnectionEnvironment.Development, ConnectionEnvironment.Production })
        {
            var d = await JudgeAsync(text, env, readOnly: true);

            Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
            Assert.Equal(PolicyRule.ReadOnlyProfile, d.Rule);
        }
    }

    [Theory]
    [MemberData(nameof(Mutating))]
    public async Task Production_label_never_lets_a_mutating_statement_through_unasked(string text)
    {
        var d = await JudgeAsync(text, ConnectionEnvironment.Production, readOnly: false);

        Assert.NotEqual(PolicyDecisionKind.Allow, d.Kind);
    }

    [Theory]
    [InlineData("select 1 from dual")]
    [InlineData("SELECT * FROM t WHERE id = 1 FOR UPDATE")]
    [InlineData("with x as (select 1 a from dual) select a from x")]
    [InlineData("(select 1 from dual)")]
    [InlineData("explain plan for delete from t where id = 1")]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("savepoint s1")]
    [InlineData("set transaction read write")]
    public async Task Read_only_profile_still_allows_reading_and_transaction_control(string text)
    {
        var d = await JudgeAsync(text, ConnectionEnvironment.Development, readOnly: true);

        Assert.Equal(PolicyDecisionKind.Allow, d.Kind);
    }

    [Theory]
    [InlineData("select 1 from dual")]
    [InlineData("with x as (select 1 a from dual) select a from x")]
    [InlineData("commit")]
    [InlineData("rollback")]
    public async Task Production_label_does_not_ask_for_plain_reads(string text)
    {
        Assert.Equal(PolicyDecisionKind.Allow, (await JudgeAsync(text, ConnectionEnvironment.Production, readOnly: false)).Kind);
    }

    [Fact]
    public async Task Dangerous_commands_carry_the_retype_name_in_every_environment()
    {
        foreach (var env in new[] { ConnectionEnvironment.Development, ConnectionEnvironment.Test, ConnectionEnvironment.Production })
        {
            var drop = await JudgeAsync("drop table scott.emp", env, readOnly: false);
            Assert.Equal(PolicyDecisionKind.Confirm, drop.Kind);
            Assert.Equal("scott.emp", drop.RetypeText, ignoreCase: true);

            var truncate = await JudgeAsync("truncate table emp", env, readOnly: false);
            Assert.Equal("emp", truncate.RetypeText, ignoreCase: true);

            var sys = await JudgeAsync("alter system set open_cursors = 500", env, readOnly: false);
            Assert.NotNull(sys.RetypeText);
        }
    }

    [Fact]
    public async Task Unfiltered_update_and_delete_ask_even_on_development_but_filtered_ones_do_not()
    {
        Assert.Equal(PolicyRule.UnfilteredDml, (await JudgeAsync("delete from t", ConnectionEnvironment.Development, false)).Rule);
        Assert.Equal(PolicyRule.UnfilteredDml, (await JudgeAsync("update t set a = 1", ConnectionEnvironment.Test, false)).Rule);
        Assert.Equal(PolicyDecisionKind.Allow, (await JudgeAsync("delete from t where id = 1", ConnectionEnvironment.Development, false)).Kind);
    }

    [Fact]
    public async Task License_tagged_query_is_denied_without_a_declared_pack_even_on_a_read_only_profile_order()
    {
        // 규칙 순서: 읽기 전용 → 라이선스. 읽기 전용이 아닌 일반 접속에서 팩 선언이 없으면 라이선스 규칙이 거부한다.
        var profile = new ConnectionProfile("p", "u", "h:1521/s");
        var stmt = new SqlStatement("SELECT * FROM v$active_session_history", default, StatementKind.Query) { Verb = "SELECT" };

        var d = await new ExecutionPolicy().EvaluateAsync(
            new PolicyContext(profile, Server, HistorySource.Monitor) { RequiredLicense = LicensePack.Diagnostics }, stmt, default);

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.License, d.Rule);
    }

    [Fact]
    public async Task Policy_result_does_not_depend_on_the_requesting_feature()
    {
        // 같은 문장·같은 접속이면 출처(편집기·그리드·스크립트·모니터·확장·이력)와 상관없이 같은 판단이어야 한다.
        var profile = new ConnectionProfile("p", "u", "h:1521/s", Environment: ConnectionEnvironment.Production);
        var ro = profile with { ReadOnly = true };
        var stmt = new SqlStatement("UPDATE t SET a = 1 WHERE id = 1", default, StatementKind.Dml) { Verb = "UPDATE" };
        var policy = new ExecutionPolicy();

        foreach (var p in new[] { profile, ro })
        {
            var decisions = new HashSet<(PolicyDecisionKind, PolicyRule, string?)>();
            foreach (var src in Enum.GetValues<HistorySource>())
            {
                var d = await policy.EvaluateAsync(new PolicyContext(p, Server, src), stmt, default);
                decisions.Add((d.Kind, d.Rule, d.RetypeText));
            }

            Assert.Single(decisions);
        }
    }
}

/// <summary>시험 사용자 생성·정리와 정답 확인용 직접 연결. 시험 클래스가 공유한다.</summary>
public sealed class SafetyDbFixture : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>환경 변수가 있으면 true.</summary>
    public bool Enabled { get; private set; }

    public string User => "ORANDA_SC_" + _suffix;

    /// <summary>ALTER SYSTEM 권한을 가진 두 번째 사용자(Kill 시험).</summary>
    public string PowerUser => "ORANDA_SK_" + _suffix;

    public string Password { get; } = "Zx" + Guid.NewGuid().ToString("N")[..20] + "#9";

    public static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string AdminConnectionString => new ConnectionProfile("admin", Env("ADMIN_USER")!, DataSource).BuildConnectionString(Env("ADMIN_PASSWORD")!);

    public async Task InitializeAsync()
    {
        Enabled = Env("HOST") is not null && Env("SERVICE") is not null && Env("ADMIN_USER") is not null && Env("ADMIN_PASSWORD") is not null;
        if (!Enabled)
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            }

            return;
        }

        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            $"CREATE USER {User} IDENTIFIED BY \"{Password}\"",
            $"GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, UNLIMITED TABLESPACE TO {User}",
            $"CREATE USER {PowerUser} IDENTIFIED BY \"{Password}\"",
            $"GRANT CREATE SESSION, ALTER SYSTEM TO {PowerUser}",
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

        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        foreach (var user in new[] { User, PowerUser })
        {
            var sessions = new List<(long Sid, long Serial)>();
            await using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT sid, serial# FROM v$session WHERE username = :u";
                q.Parameters.Add(new OracleParameter("u", user));
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
                if (await TryAsync(conn, $"DROP USER {user} CASCADE"))
                {
                    break;
                }

                await Task.Delay(500);
            }
        }
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

    /// <summary>시험 사용자로 직접 연 연결에서 문장을 실행한다(앱 경로를 거치지 않는 정답 확인·준비용).</summary>
    public async Task SetupAsync(string sql, string? user = null)
    {
        await using var conn = await OpenAsync(user);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string> ScalarAsync(string sql)
    {
        await using var conn = await OpenAsync(null);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static async Task<string> AdminScalarAsync(string sql)
    {
        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public async Task<OracleConnection> OpenAsync(string? user)
    {
        var conn = new OracleConnection(new ConnectionProfile("direct", user ?? User, DataSource).BuildConnectionString(Password));
        await conn.OpenAsync();
        return conn;
    }
}

/// <summary>정책 판단 기록.</summary>
internal sealed class RecordingPolicy : IExecutionPolicy
{
    private readonly ExecutionPolicy _inner = new();

    public List<(HistorySource Source, string Text, PolicyDecision Decision)> Seen { get; } = [];

    public async ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct)
    {
        var d = await _inner.EvaluateAsync(ctx, stmt, ct);
        lock (Seen)
        {
            Seen.Add((ctx.Source, stmt.Text, d));
        }

        return d;
    }
}

/// <summary>확인 질문에 차례로 답한다. 정해 둔 답이 떨어지면 <see cref="DefaultAnswer"/>.</summary>
internal sealed class ScriptedInteraction : IExecutionInteraction
{
    private readonly Queue<bool> _answers = new();

    public bool DefaultAnswer { get; set; } = true;

    public List<(SqlStatement Statement, PolicyDecision Decision)> Confirms { get; } = [];

    public void Enqueue(params bool[] answers)
    {
        foreach (var a in answers)
        {
            _answers.Enqueue(a);
        }
    }

    public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct)
    {
        Confirms.Add((stmt, decision));
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : DefaultAnswer);
    }

    public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>());

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
        Task.FromResult(ScriptErrorAction.Stop);
}

public enum SafeMode
{
    Dev,
    Prod,
    ReadOnly,
    ProdReadOnly,
}

/// <summary>
/// 안전장치 교차 시험 한 건의 실행 장치: 실제 세션 관리자·실행 엔진·정책·SQL*Plus 처리기·언어 서비스, 접속 모드 4가지(개발·운영·읽기 전용·운영+읽기 전용).
/// </summary>
internal sealed class SafetyRig
{
    public static readonly Dictionary<string, BindValue> NoBinds = [];

    private readonly SafetyDbFixture _db;
    private readonly Dictionary<(SafeMode, string), OracleSession> _sessions = [];
    private readonly OracleSessionManager _manager;

    public SafetyRig(SafetyDbFixture db)
    {
        _db = db;
        Bus = new EventBus();
        _manager = new OracleSessionManager(Bus);
        Language = new LanguageService();
        var processor = new SqlPlusCommandProcessor(() => Exec!, () => Language, () => Bus);
        Exec = new OracleExecutionService(Policy, Ui, Bus, processor, pageSize: 100);
    }

    public EventBus Bus { get; }

    public LanguageService Language { get; }

    public RecordingPolicy Policy { get; } = new();

    public ScriptedInteraction Ui { get; } = new();

    public OracleExecutionService Exec { get; }

    public string Unique { get; } = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    public string Table => "T_" + Unique;

    public string Proc => "P_" + Unique;

    public static ConnectionProfile ProfileFor(SafetyDbFixture db, SafeMode mode, string? user = null, LicensePack pack = LicensePack.None) => new(
        "sc-" + mode, user ?? db.User, SafetyDbFixture.DataSource,
        Environment: mode is SafeMode.Prod or SafeMode.ProdReadOnly ? ConnectionEnvironment.Production : ConnectionEnvironment.Development,
        ReadOnly: mode is SafeMode.ReadOnly or SafeMode.ProdReadOnly,
        LicensePack: pack,
        Id: Guid.NewGuid());

    public async Task<OracleSession> SessionAsync(SafeMode mode, string? user = null, LicensePack pack = LicensePack.None)
    {
        var key = (mode, (user ?? string.Empty) + "|" + pack);
        if (_sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        using var cred = new SecureCredential(_db.Password);
        var s = (OracleSession)await _manager.OpenAsync(ProfileFor(_db, mode, user, pack), cred, SessionPurpose.Work, default);
        _sessions[key] = s;
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

    public async Task CloseAsync()
    {
        foreach (var s in _sessions.Values)
        {
            await s.DisposeAsync();
        }
    }
}

[Trait("Category", "Integration")]
public sealed class SafetyCrossDbTests : IClassFixture<SafetyDbFixture>, IAsyncLifetime
{
    private readonly SafetyDbFixture _db;
    private SafetyRig _rig = null!;
    private string _file = string.Empty;

    public SafetyCrossDbTests(SafetyDbFixture db) => _db = db;

    private string T => _rig.Table;

    public async Task InitializeAsync()
    {
        _rig = new SafetyRig(_db);
        if (!_db.Enabled)
        {
            return;
        }

        _file = Path.Combine(Path.GetTempPath(), "oranda-sc-" + _rig.Unique + ".sql");
        await File.WriteAllTextAsync(_file, $"INSERT INTO {T} VALUES (9, 'file');\n");
        await _db.SetupAsync($"CREATE TABLE {T} (id NUMBER PRIMARY KEY, v VARCHAR2(20))");
        await _db.SetupAsync($"INSERT INTO {T} VALUES (1, 'a')");
        await _db.SetupAsync($"CREATE PROCEDURE {_rig.Proc} AS BEGIN INSERT INTO {T} VALUES (7, 'proc'); END;");
        await _db.SetupAsync($"CREATE TABLE {T}_OLD (x NUMBER)");
        await _db.SetupAsync($"DROP TABLE {T}_OLD");
    }

    public async Task DisposeAsync()
    {
        await _rig.CloseAsync();
        if (_file.Length > 0)
        {
            File.Delete(_file);
        }
    }

    private sealed record Case(string Text, string Snapshot, bool DevChanges = true, bool Danger = false, bool InnerOnly = false, bool ProdAsks = true);

    private Dictionary<string, Case> Cases() => BuildCases(T, _rig.Proc, _file);

    private static Dictionary<string, Case> BuildCases(string T, string proc, string file) => new()
    {
        ["insert"] = new($"INSERT INTO {T} VALUES (2, 'b')", RowsOf(T)),
        ["update"] = new($"UPDATE {T} SET v = 'z' WHERE id = 1", RowsOf(T)),
        ["delete"] = new($"DELETE FROM {T} WHERE id = 1", RowsOf(T)),
        ["merge"] = new($"MERGE INTO {T} t USING (SELECT 1 id, 'm' v FROM dual) s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = s.v", RowsOf(T)),
        ["lowercase-with-line-comment"] = new($"-- note\n  insert into {T} values (3, 'lc')", RowsOf(T)),
        ["block-comment-and-hint"] = new($"/* x */ INSERT /*+ PARALLEL(1) */ INTO {T} VALUES (4, 'h')", RowsOf(T)),
        ["plsql-block"] = new($"BEGIN INSERT INTO {T} VALUES (5, 'blk'); END;", RowsOf(T)),
        ["plsql-dynamic-ddl"] = new($"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {T}'; END;", TableCount(T)),
        ["declare-block"] = new($"DECLARE n NUMBER; BEGIN DELETE FROM {T}; COMMIT; END;", RowsOf(T)),
        ["call"] = new($"CALL {proc}()", RowsOf(T)),
        ["exec"] = new($"EXEC {proc}", RowsOf(T), InnerOnly: true),
        ["at-file"] = new("@" + file, RowsOf(T), InnerOnly: true),
        ["create-table-as"] = new($"CREATE TABLE {T}_C AS SELECT * FROM {T}", TableCount(T + "_C")),
        ["alter-table"] = new($"ALTER TABLE {T} ADD (c2 NUMBER)", $"SELECT COUNT(*) FROM user_tab_columns WHERE table_name = '{T}'"),
        ["comment-on"] = new($"COMMENT ON TABLE {T} IS 'cm'", $"SELECT NVL(MAX(comments), '-') FROM user_tab_comments WHERE table_name = '{T}'"),
        ["grant"] = new($"GRANT SELECT ON {T} TO PUBLIC", $"SELECT COUNT(*) FROM user_tab_privs_made WHERE table_name = '{T}'"),
        ["truncate"] = new($"TRUNCATE TABLE {T}", RowsOf(T), Danger: true),
        ["drop"] = new($"DROP TABLE {T}", TableCount(T), Danger: true),
        ["purge-recyclebin"] = new("PURGE RECYCLEBIN", $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{T}_OLD'"),
        ["flashback"] = new($"FLASHBACK TABLE {T}_OLD TO BEFORE DROP", TableCount(T + "_OLD")),
        ["rename"] = new($"RENAME {T} TO {T}_R", TableCount(T)),
        ["unclassifiable"] = new($"FOO BAR {T}", RowsOf(T), DevChanges: false),
        ["lock-table"] = new($"LOCK TABLE {T} IN EXCLUSIVE MODE", RowsOf(T), DevChanges: false),
        ["alter-session"] = new("ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY'", "SELECT 1 FROM dual", DevChanges: false, ProdAsks: false),
        ["alter-system"] = new("ALTER SYSTEM FLUSH SHARED_POOL", "SELECT 1 FROM dual", DevChanges: false, Danger: true),
    };

    private string Rows() => RowsOf(T);

    private static string RowsOf(string t) => $"SELECT COUNT(*) || '/' || NVL(MAX(v), '-') FROM {t}";

    private static string TableCount(string name) => $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{name}'";

    public static IEnumerable<object[]> CaseNames() =>
        BuildCases("T", "P", "F").Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> CaseNamesByReadOnlyMode() =>
        CaseNames().SelectMany(c => new[] { SafeMode.ReadOnly, SafeMode.ProdReadOnly }.Select(m => new object[] { c[0], m }));

    private static async Task CommitOrRollbackAsync(ISession s, bool commit)
    {
        if (commit)
        {
            await s.CommitAsync(default);
        }
        else
        {
            await s.RollbackAsync(default);
        }
    }

    // ---- 읽기 전용(운영 라벨 유무와 상관없이) --------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNamesByReadOnlyMode))]
    public async Task Read_only_profile_blocks_every_editor_path_and_nothing_changes(string name, SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var c = Cases()[name];
        var before = await _db.ScalarAsync(c.Snapshot);
        var session = await _rig.SessionAsync(mode);

        var results = await _rig.RunTextAsync(session, c.Text);

        // 정책 관문이 읽기 전용 규칙으로 거부했다(바깥 문장 또는 SQL*Plus 안쪽 문장).
        Assert.Contains(_rig.Policy.Seen, s => s.Decision is { Kind: PolicyDecisionKind.Deny, Rule: PolicyRule.ReadOnlyProfile });
        if (!c.InnerOnly)
        {
            Assert.All(results, r => Assert.Equal(ExecutionStatus.Denied, r.Status));
        }

        Assert.Empty(_rig.Ui.Confirms);
        Assert.False(session.HasPendingTransaction);
        Assert.Equal(before, await _db.ScalarAsync(c.Snapshot));
    }

    // ---- 운영 라벨 ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Production_label_asks_first_and_declining_changes_nothing(string name)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var c = Cases()[name];
        var before = await _db.ScalarAsync(c.Snapshot);
        var session = await _rig.SessionAsync(SafeMode.Prod);
        _rig.Ui.DefaultAnswer = false;

        var results = await _rig.RunTextAsync(session, c.Text);

        if (!c.ProdAsks)
        {
            // ALTER SESSION 은 운영 라벨이어도 묻지 않고 돈다.
            Assert.Empty(_rig.Ui.Confirms);
            return;
        }

        Assert.NotEmpty(_rig.Ui.Confirms);
        Assert.Equal(PolicyRule.ProductionChange, _rig.Ui.Confirms[0].Decision.Rule);
        if (!c.InnerOnly)
        {
            Assert.Equal(ExecutionStatus.Cancelled, Assert.Single(results).Status);
        }

        if (c.Danger)
        {
            Assert.False(string.IsNullOrEmpty(_rig.Ui.Confirms[0].Decision.RetypeText));
        }

        Assert.False(session.HasPendingTransaction);
        Assert.Equal(before, await _db.ScalarAsync(c.Snapshot));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Production_label_runs_after_approval_and_the_change_is_real(string name)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var c = Cases()[name];
        var before = await _db.ScalarAsync(c.Snapshot);
        var session = await _rig.SessionAsync(SafeMode.Prod);
        _rig.Ui.DefaultAnswer = true;

        var results = await _rig.RunTextAsync(session, c.Text);
        await session.CommitAsync(default);

        Assert.Equal(c.ProdAsks, _rig.Ui.Confirms.Count > 0);
        Assert.All(results, r => Assert.NotEqual(ExecutionStatus.Denied, r.Status));
        Assert.All(results, r => Assert.NotEqual(ExecutionStatus.Cancelled, r.Status));
        var after = await _db.ScalarAsync(c.Snapshot);
        if (c.DevChanges)
        {
            Assert.NotEqual(before, after);
        }
        else
        {
            Assert.Equal(before, after);
        }
    }

    // ---- 개발 접속: 같은 문장이 물음 없이 돌아야 한다(위험 명령만 이름 재입력) ---------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Development_profile_runs_without_asking_except_dangerous_commands(string name)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var c = Cases()[name];
        var before = await _db.ScalarAsync(c.Snapshot);
        var session = await _rig.SessionAsync(SafeMode.Dev);

        var results = await _rig.RunTextAsync(session, c.Text);
        await session.CommitAsync(default);

        Assert.All(results, r => Assert.NotEqual(ExecutionStatus.Denied, r.Status));
        if (c.Danger)
        {
            Assert.All(_rig.Ui.Confirms, q => Assert.Equal(PolicyRule.DangerousCommand, q.Decision.Rule));
            Assert.NotEmpty(_rig.Ui.Confirms);
        }
        else
        {
            Assert.Empty(_rig.Ui.Confirms);
        }

        var after = await _db.ScalarAsync(c.Snapshot);
        if (c.DevChanges)
        {
            Assert.NotEqual(before, after);
        }
    }

    // ---- 스크립트·되돌림 -------------------------------------------------------------------------

    [Fact]
    public async Task Production_script_declined_midway_stops_and_rolling_back_restores_the_original()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var session = await _rig.SessionAsync(SafeMode.Prod);
        _rig.Ui.Enqueue(true, false);
        var statements = _rig.Language.Split($"INSERT INTO {T} VALUES (10, 'x');\nINSERT INTO {T} VALUES (11, 'y');\nINSERT INTO {T} VALUES (12, 'z');", SplitOptions.Default);

        var results = new List<ExecutionResult>();
        await foreach (var r in _rig.Exec.ExecuteScriptAsync(session, statements, ScriptErrorMode.Stop, new ExecutionOptions { Source = HistorySource.Script }, default))
        {
            results.Add(r);
        }

        Assert.Equal([ExecutionStatus.Succeeded, ExecutionStatus.Cancelled], results.Select(r => r.Status));
        Assert.Equal(2, _rig.Ui.Confirms.Count);
        Assert.True(session.HasPendingTransaction);
        Assert.Equal(before, await _db.ScalarAsync(Rows())); // 미커밋이라 다른 연결에는 아직 안 보인다.

        await session.RollbackAsync(default);
        Assert.False(session.HasPendingTransaction);
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }

    [Theory]
    [InlineData(SafeMode.ReadOnly)]
    [InlineData(SafeMode.ProdReadOnly)]
    public async Task Read_only_script_is_denied_per_statement_and_stops(SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var session = await _rig.SessionAsync(mode);
        var statements = _rig.Language.Split($"SELECT 1 FROM dual;\nINSERT INTO {T} VALUES (10, 'x');\nSELECT 2 FROM dual;", SplitOptions.Default);

        var results = new List<ExecutionResult>();
        await foreach (var r in _rig.Exec.ExecuteScriptAsync(session, statements, ScriptErrorMode.Stop, new ExecutionOptions { Source = HistorySource.Script }, default))
        {
            if (r.ResultSet is not null)
            {
                await r.ResultSet.DisposeAsync();
            }

            results.Add(r);
        }

        Assert.Equal([ExecutionStatus.Succeeded, ExecutionStatus.Denied], results.Select(r => r.Status));
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }

    [Theory]
    [InlineData(HistorySource.Editor)]
    [InlineData(HistorySource.GridEdit)]
    [InlineData(HistorySource.Script)]
    [InlineData(HistorySource.Monitor)]
    [InlineData(HistorySource.Extension)]
    public async Task Policy_gate_result_is_the_same_whatever_feature_asks(HistorySource source)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var ro = await _rig.SessionAsync(SafeMode.ReadOnly);
        var prod = await _rig.SessionAsync(SafeMode.Prod);
        _rig.Ui.DefaultAnswer = false;
        var stmt = _rig.Language.Split($"UPDATE {T} SET v = 'q' WHERE id = 1", SplitOptions.Default)[0];

        var denied = await _rig.Exec.ExecuteAsync(ro, stmt, SafetyRig.NoBinds, new ExecutionOptions { Source = source }, default);
        var cancelled = await _rig.Exec.ExecuteAsync(prod, stmt, SafetyRig.NoBinds, new ExecutionOptions { Source = source }, default);

        Assert.Equal(ExecutionStatus.Denied, denied.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, denied.Policy?.Rule);
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(PolicyRule.ProductionChange, cancelled.Policy?.Rule);
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }

    // ---- 그리드 편집 ----------------------------------------------------------------------------

    private RowChange[] GridChanges() =>
    [
        new RowChange(RowChangeKind.Update, 0, new DbObjectRef(_db.User, T, "TABLE"))
        {
            Key = new Dictionary<string, object?> { ["ID"] = 1m },
            NewValues = new Dictionary<string, object?> { ["V"] = "g" },
        },
        new RowChange(RowChangeKind.Insert, 1, new DbObjectRef(_db.User, T, "TABLE"))
        {
            NewValues = new Dictionary<string, object?> { ["ID"] = 2m, ["V"] = "n" },
        },
    ];

    [Theory]
    [InlineData(SafeMode.ReadOnly)]
    [InlineData(SafeMode.ProdReadOnly)]
    public async Task Grid_edit_on_read_only_profile_reports_every_row_and_sends_nothing(SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var session = await _rig.SessionAsync(mode);

        var result = await new RowEditor(_rig.Exec).ApplyAsync(session, GridChanges(), commit: true, default);

        Assert.Equal(0, result.AppliedCount);
        Assert.False(result.Committed);
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.Equal("ORANDA-READONLY", f.Error?.Code));
        Assert.Empty(_rig.Policy.Seen); // 서버로 가는 문장이 하나도 없다(세이브포인트 포함).
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }

    [Fact]
    public async Task Grid_edit_in_production_declined_rolls_the_whole_batch_back()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var session = await _rig.SessionAsync(SafeMode.Prod);
        _rig.Ui.Enqueue(true, false); // 첫 행은 승인, 둘째 행은 거절

        var result = await new RowEditor(_rig.Exec).ApplyAsync(session, GridChanges(), commit: true, default);

        Assert.Equal(2, _rig.Ui.Confirms.Count);
        Assert.All(_rig.Ui.Confirms, q => Assert.Equal(PolicyRule.ProductionChange, q.Decision.Rule));
        Assert.False(result.Committed);
        Assert.NotEmpty(result.Failures);
        await session.RollbackAsync(default);
        Assert.Equal(before, await _db.ScalarAsync(Rows()));

        // 확인 없이 처리된 행이 없는지: 같은 세션에서 다시 읽어도 첫 행이 'a' 그대로(배치 세이브포인트로 되돌렸다).
        Assert.Equal("a", await _db.ScalarAsync($"SELECT v FROM {T} WHERE id = 1"));
    }

    [Fact]
    public async Task Grid_edit_in_production_approved_applies_and_commits()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.Prod);

        var result = await new RowEditor(_rig.Exec).ApplyAsync(session, GridChanges(), commit: true, default);

        Assert.Equal(2, result.AppliedCount);
        Assert.True(result.Committed);
        Assert.Equal(2, _rig.Ui.Confirms.Count);
        Assert.Equal("2/n", await _db.ScalarAsync(Rows()));
        Assert.Equal("g", await _db.ScalarAsync($"SELECT v FROM {T} WHERE id = 1"));
    }

    [Fact]
    public async Task Grid_edit_on_development_applies_without_asking()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.Dev);

        var result = await new RowEditor(_rig.Exec).ApplyAsync(session, GridChanges(), commit: true, default);

        Assert.Equal(2, result.AppliedCount);
        Assert.Empty(_rig.Ui.Confirms);
        Assert.Equal("2/n", await _db.ScalarAsync(Rows()));
    }

    // ---- 가져오기 --------------------------------------------------------------------------------

    private OracleDataTransferService Transfer() => new(_rig.Exec, new Oracle.Metadata.OracleMetadataProvider(new Metadata.MemoryMetadataCache(), _rig.Bus, Timeout.InfiniteTimeSpan));

    private ImportOptions ImportOpts(bool commitAtEnd = true) => new(TransferFormat.Csv, new DbObjectRef(_db.User, T, "TABLE"),
        [new ColumnMapping("ID", "ID"), new ColumnMapping("V", "V")])
    {
        BatchSize = 2,
        CommitAtEnd = commitAtEnd,
    };

    private static MemoryStream Csv() => new("ID,V\n20,x\n21,y\n22,z\n"u8.ToArray());

    [Theory]
    [InlineData(SafeMode.ReadOnly)]
    [InlineData(SafeMode.ProdReadOnly)]
    public async Task Import_on_read_only_profile_is_blocked_before_any_row_is_written(SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var session = await _rig.SessionAsync(mode);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Transfer().ImportAsync(session, ImportOpts(), Csv(), null, default));

        Assert.Contains(nameof(PolicyRule.ReadOnlyProfile), ex.Message, StringComparison.Ordinal);
        Assert.Empty(_rig.Ui.Confirms);
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }

    [Fact]
    public async Task Import_in_production_asks_per_batch_and_a_declined_batch_leaves_nothing()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var session = await _rig.SessionAsync(SafeMode.Prod);
        _rig.Ui.Enqueue(true, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfer().ImportAsync(session, ImportOpts(), Csv(), null, default));

        Assert.Equal(2, _rig.Ui.Confirms.Count); // 3행 / 배치 2 = 확인 2번
        Assert.False(session.HasPendingTransaction);
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }

    [Fact]
    public async Task Import_in_production_with_commit_per_batch_keeps_only_the_approved_batches()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.Prod);
        _rig.Ui.Enqueue(true, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfer().ImportAsync(session, ImportOpts(commitAtEnd: false), Csv(), null, default));

        // 사용자가 「배치마다 커밋」 을 골랐으므로 승인한 첫 배치(2행)만 남는다.
        Assert.Equal("3/y", await _db.ScalarAsync(Rows()));
    }

    [Fact]
    public async Task Import_in_production_approved_inserts_all_rows()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.Prod);

        var summary = await Transfer().ImportAsync(session, ImportOpts(), Csv(), null, default);

        Assert.Equal(3, summary.Rows);
        Assert.Equal("4/z", await _db.ScalarAsync(Rows()));
    }

    [Fact]
    public async Task Import_on_development_inserts_without_asking()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.Dev);

        var summary = await Transfer().ImportAsync(session, ImportOpts(), Csv(), null, default);

        Assert.Equal(3, summary.Rows);
        Assert.Empty(_rig.Ui.Confirms);
    }

    // ---- 팩 선언(라이선스) 조합 ------------------------------------------------------------------------

    public static IEnumerable<object[]> ModesAndPacks() =>
        Enum.GetValues<SafeMode>().SelectMany(m => Enum.GetValues<LicensePack>().Select(p => new object[] { m, p }));

    [Theory]
    [MemberData(nameof(ModesAndPacks))]
    public async Task License_tagged_query_follows_the_declared_pack_in_every_mode(SafeMode mode, LicensePack declared)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode, pack: declared);
        var stmt = _rig.Language.Split("SELECT 1 FROM dual", SplitOptions.Default)[0];
        var held = declared;
        if (ExecutionPolicy.ParseServerPackAccess(session.Server.ManagementPackAccess) is { } serverPack && serverPack < held)
        {
            held = serverPack;
        }

        foreach (var required in new[] { LicensePack.Diagnostics, LicensePack.DiagnosticsAndTuning })
        {
            var r = await _rig.Exec.ExecuteAsync(session, stmt, SafetyRig.NoBinds, new ExecutionOptions { Source = HistorySource.Monitor, RequiredLicense = required }, default);
            if (r.ResultSet is not null)
            {
                await r.ResultSet.DisposeAsync();
            }

            if (required > held)
            {
                Assert.Equal(ExecutionStatus.Denied, r.Status);
                Assert.Equal(PolicyRule.License, r.Policy?.Rule);
            }
            else
            {
                Assert.Equal(ExecutionStatus.Succeeded, r.Status);
            }
        }

        Assert.Empty(_rig.Ui.Confirms); // 질의는 운영 라벨에서도 묻지 않는다
    }

    /// <summary>
    /// 관측(결함 DEF-08, 중간): 읽기 전용 프로필은 문장마다 <c>SET TRANSACTION READ ONLY</c> 로 시작하므로, 다른 세션이 방금 만든(바꾼) 표를 읽으면
    /// 몇 초 동안 ORA-01466(표 정의가 바뀌었다)으로 실패한다. 일반 접속은 영향이 없다. 지금 동작을 고정한다(고치면 첫 시도 실패 단정을 뒤집는다).
    /// </summary>
    [Fact]
    public async Task Observed_read_only_profile_cannot_read_a_just_created_table_for_a_few_seconds()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var ro = await _rig.SessionAsync(SafeMode.ReadOnly);
        var dev = await _rig.SessionAsync(SafeMode.Dev);
        var firstAttemptFailures = 0;
        for (var i = 0; i < 3; i++)
        {
            var name = $"TP_{_rig.Unique}_{i}";
            await _db.SetupAsync($"CREATE TABLE {name} (x NUMBER)");
            var stmt = _rig.Language.Split($"SELECT * FROM {name}", SplitOptions.Default)[0];

            var onDev = await _rig.Exec.ExecuteAsync(dev, stmt, SafetyRig.NoBinds, null, default);
            if (onDev.ResultSet is not null)
            {
                await onDev.ResultSet.DisposeAsync();
            }

            Assert.Equal(ExecutionStatus.Succeeded, onDev.Status); // 같은 순간 일반 접속은 읽는다

            ExecutionResult last;
            var attempts = 0;
            do
            {
                last = await _rig.Exec.ExecuteAsync(ro, stmt, SafetyRig.NoBinds, null, default);
                if (last.ResultSet is not null)
                {
                    await last.ResultSet.DisposeAsync();
                }

                if (last.Status != ExecutionStatus.Succeeded)
                {
                    Assert.Equal(1466, last.Error?.Number);
                    if (attempts == 0)
                    {
                        firstAttemptFailures++;
                    }

                    await Task.Delay(500);
                }
            }
            while (last.Status != ExecutionStatus.Succeeded && ++attempts < 30);

            Assert.Equal(ExecutionStatus.Succeeded, last.Status); // 잠시 뒤에는 읽힌다
        }

        Assert.True(firstAttemptFailures >= 2, $"첫 시도 실패 {firstAttemptFailures}/3");
    }

    // ---- 자동 커밋 끔·서버 추적 정보(SAFE-04·05) --------------------------------------------------------

    [Fact]
    public async Task Auto_commit_is_off_changes_stay_invisible_to_others_until_commit_and_rollback_undoes_them()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.Dev);

        await _rig.RunTextAsync(session, $"INSERT INTO {T} VALUES (31, 'c1')");
        Assert.True(session.HasPendingTransaction);
        Assert.Equal("1/a", await _db.ScalarAsync(Rows())); // 다른 연결에는 아직 안 보인다
        await session.RollbackAsync(default);
        Assert.False(session.HasPendingTransaction);
        Assert.Equal("1/a", await _db.ScalarAsync(Rows()));

        await _rig.RunTextAsync(session, $"INSERT INTO {T} VALUES (32, 'c2')");
        await session.CommitAsync(default);
        Assert.Equal("2/c2", await _db.ScalarAsync(Rows()));
    }

    /// <summary>
    /// SAFE-05: 서버 감사에서 Oranda 세션을 알아보게 module·action·client_identifier 를 채운다. 관측(결함 DEF-09, 낮음): PRD 는 「도구명과 OS 사용자」 라고
    /// 했지만 client_identifier 에는 OS 사용자 이름이 없다(<c>oranda:</c> + 세션 id 앞 8자). OS 사용자는 서버가 v$session.osuser 에 따로 적는다.
    /// </summary>
    [Fact]
    public async Task Server_side_identity_names_the_tool_and_session_purpose()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.Dev);
        var sid = await SafetyDbFixture.AdminScalarAsync(
            $"SELECT MAX(sid) FROM v$session WHERE username = '{_db.User}' AND module = 'Oranda' AND action = 'Work'");
        Assert.False(string.IsNullOrEmpty(sid));
        var identifier = await SafetyDbFixture.AdminScalarAsync($"SELECT client_identifier FROM v$session WHERE sid = {sid}");

        Assert.StartsWith("oranda:", identifier, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, identifier, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OracleSessionManager.ModuleName, "Oranda");
        Assert.NotNull(session);
    }

    // ---- 내보내기 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(SafeMode.ReadOnly)]
    [InlineData(SafeMode.ProdReadOnly)]
    public async Task Export_of_a_query_works_on_read_only_but_a_change_statement_is_refused(SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode);
        var query = _rig.Language.Split($"SELECT id, v FROM {T}", SplitOptions.Default)[0];
        using var output = new MemoryStream();

        // DEF-08: 방금 만든 표를 읽기 전용 트랜잭션(SET TRANSACTION READ ONLY)으로 읽으면 몇 초 동안 ORA-01466 이 날 수 있다. 가라앉을 때까지 다시 시도한다.
        TransferSummary? summary = null;
        for (var attempt = 0; attempt < 15 && summary is null; attempt++)
        {
            output.SetLength(0);
            try
            {
                summary = await Transfer().ExportAsync(session, query, SafetyRig.NoBinds, new ExportOptions(TransferFormat.Csv), output, null, default);
            }
            catch (InvalidOperationException e) when (e.Message.Contains("ORA-01466", StringComparison.Ordinal))
            {
                await Task.Delay(1000);
            }
        }

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Rows);
        Assert.Contains("a", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);

        var change = _rig.Language.Split($"DELETE FROM {T} WHERE id = 1", SplitOptions.Default)[0];
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Transfer().ExportAsync(session, change, SafetyRig.NoBinds, new ExportOptions(TransferFormat.Csv), new MemoryStream(), null, default));
        Assert.Contains(nameof(PolicyRule.ReadOnlyProfile), ex.Message, StringComparison.Ordinal);
        Assert.Equal("1/a", await _db.ScalarAsync(Rows()));
    }

    /// <summary>
    /// 관측(결함 DEF-06, 낮음): 가져오기의 INSERT 는 이력에 남기지 않는다(<c>SkipHistory</c>). 운영 접속에 대량 데이터를 넣어도 이력에는 흔적이 없다.
    /// 지금 동작을 고정한다. 고치면 이 단정을 뒤집는다.
    /// </summary>
    [Fact]
    public async Task Observed_import_inserts_are_not_recorded_in_history()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var skipped = new List<bool>();
        using var sub = _rig.Bus.Subscribe<StatementExecuted>(this, e =>
        {
            if (e.Statement.Text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(e.Options.SkipHistory);
            }
        });
        var session = await _rig.SessionAsync(SafeMode.Prod);

        await Transfer().ImportAsync(session, ImportOpts(), Csv(), null, default);

        Assert.NotEmpty(skipped);
        Assert.All(skipped, Assert.True);
    }

    // ---- 스키마 관리(Purge·Flashback·컴파일) ---------------------------------------------------------

    private static RecycleBinItem BinItem(string bin, string original) => new(bin, original, "TABLE", null, null, true, true, null);

    private async Task<RecycleBinItem> RecycledAsync()
    {
        var session = await _rig.SessionAsync(SafeMode.Dev);
        var items = await new OracleSchemaAdminProvider().ListRecycleBinAsync(session, default);
        var item = items.Single(i => i.OriginalName == T + "_OLD");
        return BinItem(item.ObjectName, item.OriginalName);
    }

    [Theory]
    [InlineData("purge")]
    [InlineData("purge-all")]
    [InlineData("flashback")]
    [InlineData("compile")]
    public async Task Schema_management_statements_are_denied_on_read_only_and_asked_in_production(string action)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var item = await RecycledAsync();
        SqlStatement stmt = action switch
        {
            "purge" => SchemaStatements.PurgeItem(item),
            "purge-all" => SchemaStatements.PurgeAll(),
            "flashback" => SchemaStatements.Flashback(item),
            _ => SchemaStatements.Compile(new DbObjectRef(_db.User, _rig.Proc, "PROCEDURE"))!,
        };
        var binCount = $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{T}_OLD'";
        var before = await _db.ScalarAsync(binCount);

        foreach (var mode in new[] { SafeMode.ReadOnly, SafeMode.ProdReadOnly })
        {
            var denied = await _rig.Exec.ExecuteAsync(await _rig.SessionAsync(mode), stmt, SafetyRig.NoBinds, new ExecutionOptions { Source = HistorySource.Editor }, default);
            Assert.Equal(ExecutionStatus.Denied, denied.Status);
            Assert.Equal(PolicyRule.ReadOnlyProfile, denied.Policy?.Rule);
        }

        Assert.Empty(_rig.Ui.Confirms);

        _rig.Ui.DefaultAnswer = false;
        var declined = await _rig.Exec.ExecuteAsync(await _rig.SessionAsync(SafeMode.Prod), stmt, SafetyRig.NoBinds, new ExecutionOptions { Source = HistorySource.Editor }, default);
        Assert.Equal(ExecutionStatus.Cancelled, declined.Status);
        var q = Assert.Single(_rig.Ui.Confirms);
        Assert.Equal(PolicyRule.ProductionChange, q.Decision.Rule);
        if (action is "purge" or "purge-all")
        {
            Assert.False(string.IsNullOrEmpty(q.Decision.RetypeText)); // 되돌릴 수 없는 Purge 는 이름 재입력
        }

        Assert.Equal(before, await _db.ScalarAsync(binCount));
    }

    [Fact]
    public async Task Purge_in_production_after_approval_really_empties_the_recycle_bin_entry()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var item = await RecycledAsync();
        var session = await _rig.SessionAsync(SafeMode.Prod);

        var r = await _rig.Exec.ExecuteAsync(session, SchemaStatements.PurgeItem(item), SafetyRig.NoBinds, new ExecutionOptions { Source = HistorySource.Editor }, default);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal("0", await _db.ScalarAsync($"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{T}_OLD'"));
    }

    // ---- 모니터 Kill·트레이스 ----------------------------------------------------------------------

    private async Task<(OracleConnection Victim, SessionKey Key)> VictimAsync()
    {
        var victim = await _db.OpenAsync(null);
        var marker = "SCKILL_" + _rig.Unique;
        await using (var cmd = victim.CreateCommand())
        {
            cmd.CommandText = $"BEGIN DBMS_APPLICATION_INFO.SET_MODULE('{marker}', 'x'); END;";
            await cmd.ExecuteNonQueryAsync();
        }

        var sid = long.Parse(await SafetyDbFixture.AdminScalarAsync($"SELECT sid FROM v$session WHERE module = '{marker}'"), CultureInfo.InvariantCulture);
        var serial = long.Parse(await SafetyDbFixture.AdminScalarAsync($"SELECT serial# FROM v$session WHERE module = '{marker}'"), CultureInfo.InvariantCulture);
        return (victim, new SessionKey(1, sid, serial));
    }

    private static Task<string> VictimStatusAsync(SessionKey key) => SafetyDbFixture.AdminScalarAsync(
        string.Create(CultureInfo.InvariantCulture, $"SELECT NVL(MAX(status), 'GONE') FROM v$session WHERE sid = {key.Sid} AND serial# = {key.Serial}"));

    [Theory]
    [InlineData(SafeMode.ReadOnly)]
    [InlineData(SafeMode.ProdReadOnly)]
    public async Task Kill_and_trace_on_read_only_profile_are_denied_and_the_victim_lives(SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var (victim, key) = await VictimAsync();
        await using (victim)
        {
            var monitor = await _rig.SessionAsync(mode, _db.PowerUser);
            var service = new OracleMonitorService(_rig.Exec);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.KillAsync(monitor, key, true, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetTraceAsync(monitor, key, true, new TraceOptions(), default));

            Assert.Empty(_rig.Ui.Confirms);
            Assert.Equal("INACTIVE", await VictimStatusAsync(key));
        }
    }

    [Fact]
    public async Task Kill_in_production_asks_with_the_session_id_and_declining_keeps_the_victim()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var (victim, key) = await VictimAsync();
        await using (victim)
        {
            var monitor = await _rig.SessionAsync(SafeMode.Prod, _db.PowerUser);
            _rig.Ui.DefaultAnswer = false;

            await Assert.ThrowsAsync<OperationCanceledException>(() => new OracleMonitorService(_rig.Exec).KillAsync(monitor, key, false, default));

            var q = Assert.Single(_rig.Ui.Confirms);
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"{key.Sid},{key.Serial}"), q.Decision.RetypeText);
            Assert.Equal("INACTIVE", await VictimStatusAsync(key));
        }
    }

    [Theory]
    [InlineData(SafeMode.Dev)]
    [InlineData(SafeMode.Prod)]
    public async Task Kill_after_confirmation_really_kills_the_victim(SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var (victim, key) = await VictimAsync();
        await using (victim)
        {
            var monitor = await _rig.SessionAsync(mode, _db.PowerUser);

            await new OracleMonitorService(_rig.Exec).KillAsync(monitor, key, false, default);

            Assert.Single(_rig.Ui.Confirms); // 개발 접속에서도 위험 명령이라 한 번 묻는다.
            Assert.NotEqual("INACTIVE", await VictimStatusAsync(key));
        }
    }

    // ---- 이중 방어와 한계(관측) ----------------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE {T} SET v = 'bad' WHERE id = 1")]
    [InlineData("DELETE FROM {T} WHERE id = 1")]
    [InlineData("INSERT INTO {T} VALUES (50, 'bad')")]
    [InlineData("MERGE INTO {T} t USING (SELECT 1 id FROM dual) s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = 'bad'")]
    [InlineData("SELECT * FROM {T} WHERE id = 1 FOR UPDATE")]
    public async Task Server_read_only_transaction_also_blocks_misclassified_changes(string template)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var session = await _rig.SessionAsync(SafeMode.ReadOnly);
        // 분류가 틀려 정책 관문을 지났다고 가정: 질의로 표시한 문장을 보낸다.
        var stmt = new SqlStatement(template.Replace("{T}", T, StringComparison.Ordinal), default, StatementKind.Query) { Verb = "SELECT" };

        var r = await _rig.Exec.ExecuteAsync(session, stmt, SafetyRig.NoBinds, null, default);

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Equal(1456, r.Error?.Number);
        Assert.False(session.HasPendingTransaction);
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }

    [Fact]
    public async Task Server_read_only_transaction_rejects_set_transaction_read_write()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.ReadOnly);
        var stmt = _rig.Language.Split("SET TRANSACTION READ WRITE", SplitOptions.Default)[0];

        var r = await _rig.Exec.ExecuteAsync(session, stmt, SafetyRig.NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Failed, r.Status); // ORA-01453: 이미 읽기 전용 트랜잭션이 시작됨

        var write = await _rig.Exec.ExecuteAsync(session, _rig.Language.Split($"INSERT INTO {T} VALUES (60, 'w')", SplitOptions.Default)[0], SafetyRig.NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Denied, write.Status);
        Assert.Equal("1/a", await _db.ScalarAsync(Rows()));
    }

    /// <summary>
    /// 한계(결함 아님, 결과서 「알려진 한계」): 분류가 틀린 DDL 은 서버 읽기 전용 트랜잭션이 막지 못한다(DDL 은 암묵 커밋으로 트랜잭션을 끝낸다).
    /// 지금 분류기는 이런 DDL 을 모두 DDL 로 분류하므로 정책 관문이 막는다. 서버 쪽 방어는 DML 에만 유효하다는 사실을 고정한다.
    /// </summary>
    [Fact]
    public async Task Observed_misclassified_ddl_is_not_stopped_by_the_server_read_only_transaction()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(SafeMode.ReadOnly);
        var stmt = new SqlStatement($"CREATE TABLE {T}_MC (x NUMBER)", default, StatementKind.Query) { Verb = "SELECT" };

        var r = await _rig.Exec.ExecuteAsync(session, stmt, SafetyRig.NoBinds, null, default);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal("1", await _db.ScalarAsync(TableCount(T + "_MC")));
    }

    /// <summary>
    /// 한계(결함 아님): 읽기 전용 프로필에서 SELECT 안에서 부르는 자율 트랜잭션(PRAGMA AUTONOMOUS_TRANSACTION) 함수는 DB 에 쓴다.
    /// 문장은 질의라 정책 관문이 허용하고 서버 읽기 전용 트랜잭션은 별도 트랜잭션을 막지 못한다. DB 쪽 권한(읽기 전용 계정)이 최종 방어다.
    /// </summary>
    [Fact]
    public async Task Observed_autonomous_function_in_a_select_writes_even_on_a_read_only_profile()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var f = "F_" + _rig.Unique;
        await _db.SetupAsync($"CREATE FUNCTION {f} RETURN NUMBER AS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO {T} VALUES (70, 'auto'); COMMIT; RETURN 1; END;");
        var session = await _rig.SessionAsync(SafeMode.ReadOnly);

        var results = await _rig.RunTextAsync(session, $"SELECT {f} FROM dual");

        Assert.Equal(ExecutionStatus.Succeeded, Assert.Single(results).Status);
        Assert.Equal("2/auto", await _db.ScalarAsync(Rows()));
    }

    /// <summary>
    /// 관측(결함 DEF-04, 낮음): SAVEPOINT 하나만 실행해도 세션이 「미커밋」 으로 표시된다(Oracle 이 LOCAL_TRANSACTION_ID 를 돌려준다).
    /// 그리드 편집·LOB 저장은 먼저 SAVEPOINT 를 잡으므로, 운영 확인을 거절해 아무것도 안 바뀌었는데도 「미커밋」 표시·종료 시 커밋 질문이 남는다.
    /// 지금 동작을 고정한다. 고치면 첫 단정을 뒤집는다.
    /// </summary>
    [Theory]
    [InlineData(SafeMode.Dev)]
    [InlineData(SafeMode.Prod)]
    public async Task Observed_savepoint_alone_marks_the_session_uncommitted_until_rollback(SafeMode mode)
    {
        if (!_db.Enabled)
        {
            return;
        }

        var session = await _rig.SessionAsync(mode);
        var sp = new SqlStatement("SAVEPOINT SC_PROBE", default, StatementKind.TransactionControl) { Verb = "SAVEPOINT" };

        await _rig.Exec.ExecuteAsync(session, sp, SafetyRig.NoBinds, null, default);
        Assert.True(session.HasPendingTransaction);

        await session.RollbackAsync(default);
        Assert.False(session.HasPendingTransaction);
    }

    // ---- 이력 다시 실행: 정책 판단 입력 ------------------------------------------------------------------

    [Fact]
    public async Task Rerun_of_a_recorded_dml_text_gets_the_same_gate_as_typing_it()
    {
        if (!_db.Enabled)
        {
            return;
        }

        var before = await _db.ScalarAsync(Rows());
        var text = $"DELETE FROM {T} WHERE id = 1;\nUPDATE {T} SET v = 'r' WHERE id = 1";

        var ro = await _rig.SessionAsync(SafeMode.ReadOnly);
        var typed = await _rig.RunTextAsync(ro, text);
        var rerun = new List<ExecutionResult>();
        await foreach (var r in _rig.Exec.ExecuteScriptAsync(ro, _rig.Language.Split(text, SplitOptions.Default), ScriptErrorMode.Stop, new ExecutionOptions { Source = HistorySource.Editor }, default))
        {
            rerun.Add(r);
        }

        Assert.All(typed, r => Assert.Equal(ExecutionStatus.Denied, r.Status));
        Assert.Equal(ExecutionStatus.Denied, Assert.Single(rerun).Status); // 스크립트(Stop)는 첫 거부에서 멈춘다
        Assert.Equal(before, await _db.ScalarAsync(Rows()));
    }
}
