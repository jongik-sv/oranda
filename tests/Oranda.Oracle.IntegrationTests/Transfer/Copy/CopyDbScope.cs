using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 Task 8. T9·T10 의 통합 시험도 이 도우미를 그대로 쓴다.

/// <summary>
/// 접속 간 복사 통합 시험용 사용자 셋. 공유 컨테이너 하나 안에 원본 소유자(<c>CP_{tag}_SRC</c>), 대상 소유자(<c>CP_{tag}_DST</c>),
/// 대상 쓰기 계정(<c>CP_{tag}_W</c>, 소유자 아님), 다른 계정(<c>CP_{tag}_APP</c>, 시노님·표마다 계정 시험용)을 만들고 끝나면 지운다.
/// 세션은 <see cref="SessionPurpose.Dedicated"/> 로 연다. 26ai(<see cref="OracleFreeFixture"/>)와 11.2(<see cref="Oracle11gFixture"/>) 모두 쓴다.
/// 만든 PUBLIC 시노님(<see cref="CreatePublicSynonymAsync"/>)은 사용자를 지우기 전에 따로 지운다(사용자와 함께 지워지지 않는다).
/// </summary>
internal sealed class CopyDbScope : IAsyncDisposable
{
    private static readonly string[] AllTablePrivileges = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    private readonly string _dataSource;
    private readonly string _systemPassword;
    private readonly OracleSessionManager _manager;
    private readonly List<string> _publicSynonyms = [];

    private CopyDbScope(string dataSource, string systemPassword, string tag)
    {
        _dataSource = dataSource;
        _systemPassword = systemPassword;
        Tag = tag;
        Bus = new ExecBus();
        Ui = new ExecInteraction();
        _manager = new OracleSessionManager(Bus);
        Metadata = new OracleMetadataProvider(new MemoryMetadataCache(), Bus, Timeout.InfiniteTimeSpan);
    }

    public string Tag { get; }

    /// <summary>원본 소유자. 원본 세션도 이 계정으로 연다.</summary>
    public string SrcUser => $"CP_{Tag}_SRC";

    /// <summary>대상 소유자. 대상 세션은 기본으로 이 계정으로 연다.</summary>
    public string DstUser => $"CP_{Tag}_DST";

    /// <summary>대상 쓰기 계정(CREATE SESSION 뿐, 대상 표의 소유자가 아니다).</summary>
    public string WriterUser => $"CP_{Tag}_W";

    /// <summary>다른 계정(표 소유자, CREATE TABLE). 원본의 시노님이 가리키는 표·작업 기본 계정이 아닌 표의 계정.</summary>
    public string AppUser => $"CP_{Tag}_APP";

    public string Password { get; } = "Zx" + Guid.NewGuid().ToString("N")[..20] + "#9";

    public ExecBus Bus { get; }

    public ExecInteraction Ui { get; }

    public IMetadataProvider Metadata { get; }

    /// <summary>시험 한 건용 고유 태그. 사용자 이름이 30자(11g) 안에 들게 짧게 만든다.</summary>
    public static string NewTag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static Task<CopyDbScope> CreateAsync(OracleFreeFixture db, string tag) => CreateAsync(db.DataSource, db.SystemPassword, tag);

    public static Task<CopyDbScope> CreateAsync(Oracle11gFixture db, string tag) => CreateAsync(db.DataSource, db.SystemPassword, tag);

    private static async Task<CopyDbScope> CreateAsync(string dataSource, string systemPassword, string tag)
    {
        var scope = new CopyDbScope(dataSource, systemPassword, tag);
        try
        {
            await scope.CreateUsersAsync();
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }

        return scope;
    }

    // ---- 서비스 ----

    public OracleBulkExecutionService Bulk() => new(new ExecutionPolicy(), Ui, Bus);

    public CopyPrecheckService Precheck() => new OracleCopyPrecheckService(Bulk(), Metadata, new ExecutionPolicy());

    // ---- 세션 ----

    /// <summary>원본 세션: <c>profile with { ReadOnly = true }</c>, 원본 소유자 계정.</summary>
    public Task<ISession> OpenSourceAsync() => OpenAsync(SrcUser, $"cp-{Tag}-src", ConnectionEnvironment.Development, readOnly: true);

    /// <param name="asWriter">참이면 쓰기 계정(소유자 아님), 아니면 대상 소유자.</param>
    /// <param name="env">대상 접속의 환경 라벨.</param>
    /// <param name="readOnly">읽기 전용 프로필(쓰기 거부 시험용).</param>
    public Task<ISession> OpenTargetAsync(bool asWriter, ConnectionEnvironment env, bool readOnly = false)
        => OpenAsync(asWriter ? WriterUser : DstUser, $"cp-{Tag}-dst-{env}".ToUpperInvariant(), env, readOnly);

    public async Task<CopySessions> OpenSessionsAsync(bool asWriter = false, ConnectionEnvironment env = ConnectionEnvironment.Test)
        => new(await OpenSourceAsync(), await OpenTargetAsync(asWriter, env));

    /// <summary>시험 사용자 아무나로 세션을 연다(대상 접속 계정을 바꿔 보는 시험).</summary>
    public Task<ISession> OpenUserAsync(string user, ConnectionEnvironment env, bool readOnly = false)
        => OpenAsync(user, $"cp-{Tag}-{user}-{env}".ToUpperInvariant(), env, readOnly);

    /// <summary>
    /// 마지막 DDL 뒤 세션을 열기까지 기다리는 시간. 복사는 원본을 <c>SET TRANSACTION READ ONLY</c> 로 읽는데, DDL 시각→SCN 대응이 초 단위라
    /// 방금 만든(바꾼) 표는 몇 초 동안 ORA-01466 으로 읽지 못한다(서버 규칙, 26ai·11.2 에서 CREATE TABLE 뒤 1~2초 잼, CI 는 더 느릴 수 있다).
    /// </summary>
    private static readonly TimeSpan DdlSettle = TimeSpan.FromSeconds(3);

    private long _lastDdl;

    private static bool IsDdl(string sql) =>
        System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*(CREATE|ALTER|DROP|TRUNCATE|GRANT|REVOKE|RENAME|COMMENT)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void NoteDdl(string sql)
    {
        if (IsDdl(sql))
        {
            _lastDdl = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    // 마지막 DDL 뒤 DdlSettle 이 지나지 않았으면 남은 만큼 기다린다(그 뒤에 여는 읽기 전용 트랜잭션이 ORA-01466 을 내지 않게).
    private async Task SettleAfterDdlAsync()
    {
        if (_lastDdl != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastDdl) is var since && since < DdlSettle)
        {
            await Task.Delay(DdlSettle - since);
        }
    }

    private async Task<ISession> OpenAsync(string user, string name, ConnectionEnvironment env, bool readOnly)
    {
        await SettleAfterDdlAsync();
        var profile = new ConnectionProfile(name, user, _dataSource, Environment: env, ReadOnly: readOnly);
        using var cred = new SecureCredential(Password);
        return await _manager.OpenAsync(profile, cred, SessionPurpose.Dedicated, default);
    }

    // ---- 작업 ----

    /// <summary>원본 소유자 = <see cref="SrcUser"/>, 대상 소유자 = <see cref="DstUser"/> 인 작업.</summary>
    public CopyJob Job(params TableRule[] rules) => new()
    {
        Id = "job-" + Tag,
        Name = "copy " + Tag,
        Source = new ConnectionRef("source", SrcUser),
        Target = new ConnectionRef("target", DstUser),
        SourceOwner = SrcUser,
        TargetOwner = DstUser,
        Tables = rules,
        MaskSalt = MaskFunctions.NewSalt(),
    };

    // ---- 표·권한 ----

    /// <summary>원본 소유자 계정으로 문장을 실행한다. 표 이름은 소유자 없이 쓴다.</summary>
    public Task ExecSourceAsync(string sql) => ExecAsAsync(SrcUser, sql);

    /// <summary>대상 소유자 계정으로 문장을 실행한다.</summary>
    public Task ExecTargetAsync(string sql) => ExecAsAsync(DstUser, sql);

    /// <summary>다른 계정(<see cref="AppUser"/>)으로 문장을 실행한다.</summary>
    public Task ExecAppAsync(string sql) => ExecAsAsync(AppUser, sql);

    /// <summary>PUBLIC 시노님을 만든다(관리자). 끝날 때 지운다. 이름은 다른 시험과 겹치지 않게 태그를 넣어 짓는다.</summary>
    public async Task CreatePublicSynonymAsync(string name, string target)
    {
        _publicSynonyms.Add(name);
        await ExecAdminAsync($"CREATE PUBLIC SYNONYM {name} FOR {target}");
    }

    /// <summary>관리자(SYSTEM)로 문장을 실행한다.</summary>
    public async Task ExecAdminAsync(string sql)
    {
        await using var conn = await OpenAdminAsync();
        await ExecAsync(conn, sql);
        NoteDdl(sql);
    }

    /// <summary>원본 소유자 스키마에 표를 만든다. <paramref name="columns"/> 는 괄호 안의 열·제약 정의.</summary>
    public Task CreateSourceTableAsync(string table, string columns) => ExecSourceAsync($"CREATE TABLE {table} ({columns})");

    public Task CreateTargetTableAsync(string table, string columns) => ExecTargetAsync($"CREATE TABLE {table} ({columns})");

    /// <summary>
    /// 대상 표(<see cref="DstUser"/> 소유)에 대한 권한을 쓰기 계정에 준다. 권한을 적지 않으면 SELECT·INSERT·UPDATE·DELETE 모두
    /// (트리거·FK 를 끄는 권한은 주지 않는다).
    /// </summary>
    public Task GrantWriterAsync(string table, params string[] privileges)
        => ExecTargetAsync($"GRANT {string.Join(", ", privileges.Length == 0 ? AllTablePrivileges : privileges)} ON {table} TO {WriterUser}");

    /// <summary>한 소유자의 표 건수(앱 경로를 거치지 않는 정답 확인).</summary>
    public async Task<long> CountAsync(string owner, string table)
    {
        await using var conn = await OpenAdminAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {owner}.{table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // ---- 정리 ----

    public async ValueTask DisposeAsync()
    {
        // 열어 둔 세션을 먼저 닫고 연결 풀을 비운다. 안 그러면 DROP USER 가 ORA-01940(접속 중인 사용자)로 실패한다.
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        OracleConnection.ClearAllPools();

        await using var admin = await OpenAdminAsync();
        foreach (var synonym in _publicSynonyms)
        {
            await TryAsync(admin, $"DROP PUBLIC SYNONYM {synonym}");
        }

        foreach (var user in new[] { SrcUser, DstUser, WriterUser, AppUser })
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (await TryAsync(admin, $"DROP USER {user} CASCADE") || !await UserExistsAsync(admin, user))
                {
                    break;
                }

                await KillSessionsAsync(admin, user);
                await Task.Delay(500);
            }
        }
    }

    // ---- 내부 ----

    private async Task CreateUsersAsync()
    {
        await using var admin = await OpenAdminAsync();
        foreach (var user in new[] { SrcUser, DstUser, AppUser })
        {
            await ExecAsync(admin, $"CREATE USER {user} IDENTIFIED BY \"{Password}\" DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE TRIGGER, CREATE SEQUENCE, CREATE SYNONYM TO {user}");
        }

        await ExecAsync(admin, $"CREATE USER {WriterUser} IDENTIFIED BY \"{Password}\"");
        await ExecAsync(admin, $"GRANT CREATE SESSION TO {WriterUser}");
    }

    private async Task<OracleConnection> OpenAdminAsync()
    {
        var conn = new ConnectionProfile("admin", "system", _dataSource).CreateConnection(_systemPassword);
        await conn.OpenAsync();
        return conn;
    }

    private async Task ExecAsAsync(string user, string sql)
    {
        await using var conn = new ConnectionProfile("direct", user, _dataSource).CreateConnection(Password);
        await conn.OpenAsync();
        await ExecAsync(conn, sql);
        NoteDdl(sql);
    }

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TryAsync(OracleConnection conn, string sql)
    {
        try
        {
            await ExecAsync(conn, sql);
            return true;
        }
        catch (OracleException)
        {
            return false;
        }
    }

    private static async Task<bool> UserExistsAsync(OracleConnection conn, string user)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ALL_USERS WHERE USERNAME = :u";
        cmd.Parameters.Add(new OracleParameter("u", user));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task KillSessionsAsync(OracleConnection conn, string user)
    {
        var sessions = new List<(long Sid, long Serial)>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT SID, SERIAL# FROM V$SESSION WHERE USERNAME = :u";
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
    }
}
