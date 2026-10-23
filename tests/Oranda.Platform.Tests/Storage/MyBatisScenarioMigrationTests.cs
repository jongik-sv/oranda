using Microsoft.Data.Sqlite;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-01-02. 마이그레이션 0003(mybatis_scenario 빈 표).
public sealed class MyBatisScenarioMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-mbscenario-" + Guid.NewGuid().ToString("N"));

    public MyBatisScenarioMigrationTests() => Directory.CreateDirectory(_dir);

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
    public async Task Migration_0003_creates_empty_table_and_is_safe_to_reapply()
    {
        var file = Path.Combine(_dir, "oranda.db");
        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(file), default);
        var first = await OrandaDatabase.MigrateAsync(connection, null, default);
        Assert.True(first.Writable);
        Assert.Equal(OrandaDatabase.LatestVersion, await OrandaDatabase.GetVersionAsync(connection, default));
        Assert.True(OrandaDatabase.LatestVersion >= 3);

        Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'mybatis_scenario'"));
        Assert.Equal(0L, await Scalar(connection, "SELECT COUNT(*) FROM mybatis_scenario"));

        // 이미 적용된 DB 에 다시 적용해도 그대로.
        var again = await OrandaDatabase.MigrateAsync(connection, null, default);
        Assert.True(again.Writable);
        Assert.Equal(0L, await Scalar(connection, "SELECT COUNT(*) FROM mybatis_scenario"));
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
