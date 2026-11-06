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
/// 대상 쓰기 계정(<c>CP_{tag}_W</c>, 소유자 아님)을 만들고 끝나면 지운다. 세션은 <see cref="SessionPurpose.Dedicated"/> 로 연다.
/// </summary>
internal sealed class CopyDbScope : IAsyncDisposable
{
    private static readonly string[] AllTablePrivileges = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    private readonly OracleFreeFixture _db;
    private readonly OracleSessionManager _manager;

    private CopyDbScope(OracleFreeFixture db, string tag)
    {
        _db = db;
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

    public string Password { get; } = "Zx" + Guid.NewGuid().ToString("N")[..20] + "#9";

    public ExecBus Bus { get; }

    public ExecInteraction Ui { get; }

    public IMetadataProvider Metadata { get; }

    /// <summary>시험 한 건용 고유 태그. 사용자 이름이 30자(11g) 안에 들게 짧게 만든다.</summary>
    public static string NewTag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static async Task<CopyDbScope> CreateAsync(OracleFreeFixture db, string tag)
    {
        var scope = new CopyDbScope(db, tag);
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

    public CopyPrecheckService Precheck() => new(Bulk(), Metadata, new ExecutionPolicy());

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

    private async Task<ISession> OpenAsync(string user, string name, ConnectionEnvironment env, bool readOnly)
    {
        var profile = new ConnectionProfile(name, user, _db.DataSource, Environment: env, ReadOnly: readOnly);
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

    /// <summary>관리자(SYSTEM)로 문장을 실행한다.</summary>
    public async Task ExecAdminAsync(string sql)
    {
        await using var conn = await OpenAdminAsync();
        await ExecAsync(conn, sql);
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
        foreach (var user in new[] { SrcUser, DstUser, WriterUser })
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
        foreach (var user in new[] { SrcUser, DstUser })
        {
            await ExecAsync(admin, $"CREATE USER {user} IDENTIFIED BY \"{Password}\" DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE TRIGGER, CREATE SEQUENCE TO {user}");
        }

        await ExecAsync(admin, $"CREATE USER {WriterUser} IDENTIFIED BY \"{Password}\"");
        await ExecAsync(admin, $"GRANT CREATE SESSION TO {WriterUser}");
    }

    private async Task<OracleConnection> OpenAdminAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("admin", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private async Task ExecAsAsync(string user, string sql)
    {
        await using var conn = new OracleConnection(new ConnectionProfile("direct", user, _db.DataSource).BuildConnectionString(Password));
        await conn.OpenAsync();
        await ExecAsync(conn, sql);
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
