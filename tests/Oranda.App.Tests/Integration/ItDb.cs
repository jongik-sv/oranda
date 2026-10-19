using Oracle.ManagedDataAccess.Client;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// 통합 시험(TSK-11-01) 공용 DB 준비: 상주 컨테이너 26ai(FREEPDB1)에 시험마다 고유한 사용자를 만들고, 끝나면 지운다.
/// 환경 변수: ORANDA_IT_ADMIN_PASSWORD (필수 — 없으면 <see cref="Enabled"/> 가 거짓이고 시험은 아무것도 하지 않는다),
/// ORANDA_IT_ADMIN_USER(기본 SYSTEM), ORANDA_IT_HOST(기본 127.0.0.1), ORANDA_IT_PORT(기본 1521), ORANDA_IT_SERVICE(기본 FREEPDB1).
/// </summary>
public sealed class ItDb : IDisposable
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    public ItDb()
    {
        var adminPassword = Env("ADMIN_PASSWORD");
        Enabled = !string.IsNullOrEmpty(adminPassword);
        if (!Enabled)
        {
            return;
        }

        Host = Env("HOST") ?? "127.0.0.1";
        Port = Env("PORT") ?? "1521";
        Service = Env("SERVICE") ?? "FREEPDB1";
        DataSource = $"{Host}:{Port}/{Service}";
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        User = "ORA_IT_" + suffix;
        Password = "It#" + Guid.NewGuid().ToString("N")[..12] + "a1";
        AdminConnectionString = $"User Id={Env("ADMIN_USER") ?? "SYSTEM"};Password={adminPassword};Data Source={DataSource};Pooling=false";
        Admin($"create user {User} identified by \"{Password}\" quota unlimited on users");
        Admin($"grant create session, create table, create procedure, create sequence, create view to {User}");
    }

    public bool Enabled { get; }

    public string Host { get; } = string.Empty;

    public string Port { get; } = string.Empty;

    public string Service { get; } = string.Empty;

    public string DataSource { get; } = string.Empty;

    public string User { get; } = string.Empty;

    public string Password { get; } = string.Empty;

    private string AdminConnectionString { get; } = string.Empty;

    /// <summary>시험 사용자로 직접 연결 문자열(앱을 거치지 않는 「다른 세션」·검증용).</summary>
    public string UserConnectionString => $"User Id={User};Password={Password};Data Source={DataSource};Pooling=false";

    private void Admin(string sql)
    {
        using var c = new OracleConnection(AdminConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>시험 사용자로 SQL 한 문장(자동 커밋). 앱 밖에서 서버 상태를 바꾸거나 읽을 때 쓴다.</summary>
    public int Exec(string sql)
    {
        using var c = new OracleConnection(UserConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>시험 사용자로 값 하나를 읽는다(앱의 미커밋 변경은 보이지 않는다).</summary>
    public object? Scalar(string sql)
    {
        using var c = new OracleConnection(UserConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public string Text(string sql) => Convert.ToString(Scalar(sql), System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";

    public void Dispose()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            // 열린 세션이 남아 있으면 지울 수 없으니 먼저 끊는다.
            using var c = new OracleConnection(AdminConnectionString);
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"select sid, serial# from v$session where username = '{User}'";
                var kills = new List<string>();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        kills.Add($"alter system kill session '{r.GetInt32(0)},{r.GetInt32(1)}' immediate");
                    }
                }

                foreach (var k in kills)
                {
                    try
                    {
                        using var kc = c.CreateCommand();
                        kc.CommandText = k;
                        kc.ExecuteNonQuery();
                    }
                    catch (OracleException)
                    {
                        // 이미 끝난 세션.
                    }
                }
            }

            using var drop = c.CreateCommand();
            drop.CommandText = $"drop user {User} cascade";
            drop.ExecuteNonQuery();
        }
        catch (OracleException)
        {
            // 정리 실패는 결과서에 「잔존 사용자」 로 남기도록 시험 끝 확인(DropLeftovers)이 본다.
        }
    }
}
