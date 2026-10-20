using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;

namespace Oranda.Perf.Ui;

/// <summary>
/// 성능 측정용 레인 전용 사용자. 상주 컨테이너에 <c>ORA_PF_&lt;hex&gt;</c> 를 만들고 <see cref="Dispose"/> 에서 지운다(비밀번호는 출력하지 않는다).
/// 환경 변수: ORANDA_IT_ADMIN_PASSWORD(필수), ORANDA_IT_ADMIN_USER(기본 SYSTEM), ORANDA_IT_HOST(127.0.0.1), ORANDA_IT_PORT(1521), ORANDA_IT_SERVICE(FREEPDB1).
/// </summary>
internal sealed class PerfDb : IDisposable
{
    private readonly string _adminConnection;

    private PerfDb(string adminConnection, string dataSource, string user, string password)
    {
        _adminConnection = adminConnection;
        DataSource = dataSource;
        User = user;
        Password = password;
    }

    public string DataSource { get; }

    public string User { get; }

    public string Password { get; }

    public string UserConnectionString => $"User Id={User};Password={Password};Data Source={DataSource};Pooling=false";

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    /// <summary>환경 변수가 없으면 예외(DB 가 필요한 시나리오는 아무것도 안 하고 통과하지 않는다).</summary>
    public static PerfDb Create()
    {
        var adminPassword = Env("ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(adminPassword))
        {
            throw new InvalidOperationException("환경 변수 ORANDA_IT_ADMIN_PASSWORD 가 없다. 이 시나리오는 Oracle 이 필요하다.");
        }

        var dataSource = $"{Env("HOST") ?? "127.0.0.1"}:{Env("PORT") ?? "1521"}/{Env("SERVICE") ?? "FREEPDB1"}";
        var admin = $"User Id={Env("ADMIN_USER") ?? "SYSTEM"};Password={adminPassword};Data Source={dataSource};Pooling=false";
        var user = "ORA_PF_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var password = "Pf#" + Guid.NewGuid().ToString("N")[..12] + "a1";
        var db = new PerfDb(admin, dataSource, user, password);
        db.Admin($"create user {user} identified by \"{password}\" quota unlimited on users");
        db.Admin($"grant create session, create table, create procedure, create sequence, create view to {user}");
        return db;
    }

    public ConnectionProfile Profile(string name = "perf") => new(name, User, DataSource, Id: Guid.NewGuid());

    public void Exec(string sql)
    {
        using var c = new OracleConnection(UserConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Admin(string sql)
    {
        using var c = new OracleConnection(_adminConnection);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            // 열려 있는 세션이 있으면 지우지 못하므로 먼저 끊는다.
            using var c = new OracleConnection(_adminConnection);
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "select sid || ',' || serial# from v$session where username = :u";
            q.Parameters.Add(new OracleParameter("u", User));
            var sessions = new List<string>();
            using (var r = q.ExecuteReader())
            {
                while (r.Read())
                {
                    sessions.Add(r.GetString(0));
                }
            }

            foreach (var s in sessions)
            {
                using var kill = c.CreateCommand();
                kill.CommandText = $"alter system kill session '{s}' immediate";
                try
                {
                    kill.ExecuteNonQuery();
                }
                catch (OracleException)
                {
                    // 이미 끝난 세션.
                }
            }

            using var drop = c.CreateCommand();
            drop.CommandText = $"drop user {User} cascade";
            drop.ExecuteNonQuery();
        }
        catch (OracleException ex)
        {
            Console.Error.WriteLine("레인 사용자 정리 실패(수동으로 지워야 한다): " + User + " / " + ex.Message.Split('\n')[0]);
        }
    }
}
