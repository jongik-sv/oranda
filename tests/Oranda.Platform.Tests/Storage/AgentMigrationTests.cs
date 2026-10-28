using Microsoft.Data.Sqlite;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: ai-contract. 마이그레이션 0004(history CHECK 에 'Agent' 추가 + agent_audit).
public sealed class AgentMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-agentmig-" + Guid.NewGuid().ToString("N"));

    public AgentMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Migration_0004_keeps_history_rows_and_search_and_accepts_agent_source()
    {
        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(Path.Combine(_dir, "oranda.db")), default);

        // v3 DB 를 직접 만든다(0001~0003 + 옛 출처 행).
        foreach (var name in new[] { "0001_init.sql", "0002_change_journal.sql", "0003_mybatis_scenario.sql" })
        {
            await Exec(connection, ReadScript(name));
        }

        await Exec(connection, "INSERT INTO schema_version VALUES (2, 'x', 'x'), (3, 'x', 'x')");
        await Exec(connection, "INSERT INTO history (id, profile_id, profile_name, executed_at, sql_text, statement_kind, source, status) "
            + "VALUES (41, 'p', 'dev', '2026-10-01T00:00:00Z', 'select legacy_marker from dual', 'Query', 'Extension', 'Succeeded')");
        Assert.Equal(3, await OrandaDatabase.GetVersionAsync(connection, default));

        var result = await OrandaDatabase.MigrateAsync(connection, null, default);
        Assert.True(result.Writable, result.Notice);
        Assert.True(OrandaDatabase.LatestVersion >= 4);

        // 기존 행(id·출처 그대로)과 전문 검색.
        Assert.Equal("Extension", await Scalar(connection, "SELECT source FROM history WHERE id = 41"));
        Assert.Equal(41L, await Scalar(connection, "SELECT rowid FROM history_fts WHERE history_fts MATCH '\"legacy_marker\"'"));

        // 새 출처 'Agent' 가 들어가고 새 행도 검색에 잡힌다. 모르는 출처는 계속 거부된다.
        await Exec(connection, "INSERT INTO history (profile_id, profile_name, executed_at, sql_text, statement_kind, source, status) "
            + "VALUES ('p', 'dev', '2026-10-02T00:00:00Z', 'select agent_marker from dual', 'Query', 'Agent', 'Succeeded')");
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM history_fts WHERE history_fts MATCH '\"agent_marker\"'"));
        await Assert.ThrowsAsync<SqliteException>(() => Exec(connection, "INSERT INTO history (profile_id, profile_name, executed_at, sql_text, statement_kind, source, status) "
            + "VALUES ('p', 'dev', 't', 's', 'Query', 'Bogus', 'Succeeded')"));

        // 감사 표.
        await Exec(connection, "INSERT INTO agent_audit (occurred_at, tool, access_level, verdict) VALUES ('t', 'describe_object', 'Plan', 'Allowed')");
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM agent_audit"));

        // 이미 적용된 DB 에 스크립트를 다시 실행해도 행이 그대로(두 저장소가 동시에 열 때의 경주 방어).
        await Exec(connection, ReadScript("0004_agent.sql"));
        Assert.Equal(2L, await Scalar(connection, "SELECT COUNT(*) FROM history"));
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM history_fts WHERE history_fts MATCH '\"legacy_marker\"'"));
        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM agent_audit"));
    }

    private static string ReadScript(string file)
    {
        using var stream = typeof(OrandaDatabase).Assembly.GetManifestResourceStream("Migrations/OrandaDb/" + file)
            ?? throw new InvalidOperationException(file);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task Exec(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
