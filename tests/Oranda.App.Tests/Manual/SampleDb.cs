using System.Text;
using Oracle.ManagedDataAccess.Client;
using OracleConnectionStringBuilder = Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 샘플 DB 접속과 스크립트 설치. 접속 값은 환경 변수(<c>ORANDA_MANUAL_DB_HOST</c>·<c>PORT</c>·<c>SERVICE</c>·<c>USER</c>·<c>PASSWORD</c>)로만 받는다.
/// 값은 코드·로그·메시지에 남기지 않는다. 환경 변수가 없으면 <see cref="Configured"/> 가 false 이고 쿼리 화면 캡처는 건너뛴다.
/// </summary>
internal static class SampleDb
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_MANUAL_DB_" + name) is { Length: > 0 } v ? v : null;

    private static readonly string[] Required = ["HOST", "SERVICE", "USER", "PASSWORD"];

    internal static bool Configured => Required.All(n => Env(n) is not null);

    /// <summary>접속 문자열(비밀번호 포함). 로그나 메시지에 쓰지 않는다.</summary>
    internal static string ConnectionString => new OracleConnectionStringBuilder
    {
        UserID = Env("USER"),
        Password = Env("PASSWORD"),
        DataSource = $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}",
        ConnectionTimeout = 15,
    }.ConnectionString;

    /// <summary>
    /// 샘플 스크립트가 만드는 객체 이름. <c>reset</c> 은 이 이름만 지우고, 계정에 이 밖의 객체가 있으면 아무것도 지우지 않고 실패한다
    /// (환경 변수가 엉뚱한 계정을 가리켜도 그 계정의 데이터를 지우지 않게).
    /// </summary>
    private static readonly string[] SampleObjects =
    [
        "DEPARTMENTS", "EMPLOYEES", "CUSTOMERS", "PRODUCTS", "ORDERS", "ORDER_ITEMS", "V_ORDER_SUMMARY", "V_DEPT_SALARY", "SAMPLE_PKG",
    ];

    /// <summary>샘플 스크립트 폴더(<c>site/sample-db</c>).</summary>
    internal static string ScriptDir => Path.Combine(ManualSite.RepoRoot, "site", "sample-db");

    /// <summary>스크립트 한 파일을 문장 단위로 나눈다. PL/SQL 블록은 한 줄짜리 <c>/</c> 로, 나머지는 줄 끝 <c>;</c> 로 끝난다.</summary>
    internal static IReadOnlyList<string> Split(string script)
    {
        var statements = new List<string>();
        var buffer = new StringBuilder();
        foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (buffer.Length == 0 && (line.Length == 0 || line.TrimStart().StartsWith("--", StringComparison.Ordinal)))
            {
                continue;
            }

            if (line == "/")
            {
                Flush(statements, buffer, trimSemicolon: false);
                continue;
            }

            buffer.Append(line).Append('\n');
            if (line.EndsWith(';') && !IsPlsql(buffer.ToString()))
            {
                Flush(statements, buffer, trimSemicolon: true);
            }
        }

        Flush(statements, buffer, trimSemicolon: false);
        return statements;
    }

    private static bool IsPlsql(string text)
    {
        var head = text.TrimStart().ToUpperInvariant();
        return head.StartsWith("DECLARE", StringComparison.Ordinal)
            || head.StartsWith("BEGIN", StringComparison.Ordinal)
            || head.StartsWith("CREATE OR REPLACE PACKAGE", StringComparison.Ordinal)
            || head.StartsWith("CREATE PACKAGE", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(head, @"^CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|TRIGGER|TYPE)\b");
    }

    private static void Flush(List<string> statements, StringBuilder buffer, bool trimSemicolon)
    {
        var text = buffer.ToString().Trim();
        buffer.Clear();
        if (text.Length == 0)
        {
            return;
        }

        statements.Add(trimSemicolon ? text.TrimEnd(';') : text);
    }

    /// <summary>샘플 스크립트 4개를 번호 순서로 실행한다. 계정에 객체가 이미 있으면 실패한다(<paramref name="reset"/> 이 true 면 먼저 비운다).</summary>
    internal static async Task InstallAsync(bool reset)
    {
        await using var connection = new OracleConnection(ConnectionString);
        await connection.OpenAsync();
        if (reset)
        {
            await DropSampleObjectsAsync(connection);
        }

        var existing = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM user_objects WHERE object_name NOT LIKE 'BIN$%'"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(existing == 0, "샘플 계정에 이미 객체가 있다. 다시 만들려면 ORANDA_MANUAL_DB_INSTALL=reset 으로 실행한다.");

        foreach (var file in Directory.GetFiles(ScriptDir, "0*.sql").Order(StringComparer.Ordinal))
        {
            foreach (var statement in Split(await File.ReadAllTextAsync(file)))
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.BindByName = true;
                    await command.ExecuteNonQueryAsync();
                }
                catch (OracleException ex)
                {
                    throw new Xunit.Sdk.XunitException($"{Path.GetFileName(file)} 실행 실패: {ex.Message}{Environment.NewLine}{statement[..Math.Min(200, statement.Length)]}");
                }
            }
        }

        // PL/SQL 컴파일 오류는 예외 없이 INVALID 로 남으므로 따로 확인한다.
        var invalid = Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM user_objects WHERE status <> 'VALID'"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(invalid == 0, $"INVALID 객체 {invalid}개");
    }

    /// <summary>샘플 객체(뷰 → 패키지 → 표)만 지운다. 이 밖의 객체가 있으면 아무것도 지우지 않고 실패한다.</summary>
    private static async Task DropSampleObjectsAsync(OracleConnection connection)
    {
        var existing = new List<(string Name, string Type)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT object_name, object_type FROM user_objects WHERE object_name NOT LIKE 'BIN$%'";
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existing.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        // 표에 딸린 인덱스·시퀀스(IDENTITY)·LOB 는 표와 함께 사라지므로 이름으로 거르지 않는다. 표·뷰·패키지의 이름만 본다.
        var foreign = existing.Where(o => o.Type is "TABLE" or "VIEW" or "PACKAGE" or "PACKAGE BODY" or "PROCEDURE" or "FUNCTION" or "TRIGGER" or "TYPE")
            .Where(o => !SampleObjects.Contains(o.Name, StringComparer.Ordinal))
            .ToList();
        Assert.True(foreign.Count == 0, $"샘플이 아닌 객체 {foreign.Count}개가 있어 reset 을 중단한다(샘플 계정이 맞는지 환경 변수를 확인한다).");

        foreach (var (type, sql) in new[]
        {
            ("VIEW", "DROP VIEW \"{0}\""),
            ("PACKAGE", "DROP PACKAGE \"{0}\""),
            ("TABLE", "DROP TABLE \"{0}\" CASCADE CONSTRAINTS PURGE"),
        })
        {
            foreach (var (name, _) in existing.Where(o => o.Type == type))
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, name);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    internal static async Task<object?> ScalarAsync(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
