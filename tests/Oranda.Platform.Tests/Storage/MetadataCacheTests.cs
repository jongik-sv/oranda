using Microsoft.Data.Sqlite;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-04-04. oranda.db 마이그레이션·메타데이터 캐시.
public sealed class MetadataCacheTests : IDisposable
{
    private const string Key = "FREE/FREEPDB1/APP";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-test-" + Guid.NewGuid().ToString("N"));

    public MetadataCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private string RawConnectionString => $"Data Source={DbFile};Pooling=False";

    private string DbFile => Path.Combine(_dir, "oranda.db");

    private static MetaObject Obj(string owner, string name, string type, string? status = "VALID", DateTimeOffset? ddl = null, string? payload = null)
        => new() { DbKey = Key, Owner = owner, Name = name, Type = type, Status = status, LastDdlTime = ddl, PayloadJson = payload, FetchedAt = DateTimeOffset.UtcNow };

    private static MetaSource Source(DateTimeOffset? refreshed = null, DateTimeOffset? used = null)
        => new() { DbKey = Key, ServerVersion = "23.26.0.0.0", RefreshedAt = refreshed, LastUsedAt = used ?? DateTimeOffset.UtcNow };

    [Fact]
    public async Task Migration_creates_schema_and_records_version()
    {
        using var cache = SqliteMetadataCache.ForFile(DbFile);
        Assert.Null(await cache.GetSourceAsync(Key, default));

        await using var c = new SqliteConnection(RawConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_version";
        Assert.Equal((long)OrandaDatabase.LatestVersion, await cmd.ExecuteScalarAsync());
        cmd.CommandText = "SELECT group_concat(name) FROM sqlite_master WHERE name IN ('history','history_fts','meta_source','meta_object','schema_version')";
        var names = (string?)await cmd.ExecuteScalarAsync();
        Assert.Contains("meta_object", names, StringComparison.Ordinal);
        Assert.Contains("history", names, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reopen_applies_no_migration_and_keeps_data()
    {
        var ddl = new DateTimeOffset(2026, 10, 1, 3, 4, 5, TimeSpan.Zero);
        using (var first = SqliteMetadataCache.ForFile(DbFile))
        {
            await first.UpsertAsync(Source(refreshed: DateTimeOffset.UtcNow), [Obj("APP", "EMP", "TABLE", ddl: ddl)], default);
        }

        using var second = SqliteMetadataCache.ForFile(DbFile);
        var list = await second.GetObjectsAsync(Key, null, null, default);
        var emp = Assert.Single(list);
        Assert.Equal("EMP", emp.Name);
        Assert.Equal(ddl, emp.LastDdlTime);
        Assert.NotNull((await second.GetSourceAsync(Key, default))!.RefreshedAt);
        Assert.False(second.IsMemoryOnly);
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "backup")) ? Directory.GetFiles(Path.Combine(_dir, "backup")) : []);
    }

    [Fact]
    public async Task Upsert_updates_existing_and_filters_work()
    {
        using var cache = SqliteMetadataCache.InMemory();
        await cache.UpsertAsync(Source(DateTimeOffset.UtcNow),
            [Obj("APP", "EMP", "TABLE"), Obj("APP", "EMP_V", "VIEW"), Obj("HR", "EMPLOYEES", "TABLE"), Obj("APP", "A_B", "TABLE")], default);
        await cache.UpsertAsync(Source(), [Obj("APP", "EMP", "TABLE", status: "INVALID", payload: "{\"v\":1}")], default);

        Assert.Equal(4, (await cache.GetObjectsAsync(Key, null, null, default)).Count);
        Assert.Equal(3, (await cache.GetObjectsAsync(Key, "APP", null, default)).Count);
        Assert.Equal(3, (await cache.GetObjectsAsync(Key, null, "TABLE", default)).Count);
        var emp = (await cache.GetObjectsAsync(Key, "APP", "TABLE", default)).Single(o => o.Name == "EMP");
        Assert.Equal("INVALID", emp.Status);
        Assert.Equal("{\"v\":1}", emp.PayloadJson);

        // refreshed_at 은 null 로 덮이지 않는다.
        Assert.NotNull((await cache.GetSourceAsync(Key, default))!.RefreshedAt);
    }

    [Fact]
    public async Task FindByPrefix_matches_name_start_only_and_escapes_wildcards()
    {
        using var cache = SqliteMetadataCache.InMemory();
        await cache.UpsertAsync(Source(), [Obj("APP", "EMP", "TABLE"), Obj("HR", "EMPLOYEES", "TABLE"), Obj("APP", "DEPT", "TABLE"), Obj("APP", "A_B", "TABLE"), Obj("APP", "AXB", "TABLE")], default);

        Assert.Equal(["EMP", "EMPLOYEES"], (await cache.FindByPrefixAsync(Key, "EM", 10, default)).Select(o => o.Name));
        Assert.Single(await cache.FindByPrefixAsync(Key, "EM", 1, default));
        Assert.Equal(["A_B"], (await cache.FindByPrefixAsync(Key, "A_", 10, default)).Select(o => o.Name));
        Assert.Empty(await cache.FindByPrefixAsync(Key, "MP", 10, default));
    }

    [Fact]
    public async Task Invalidate_removes_given_objects_and_marks_list_stale()
    {
        using var cache = SqliteMetadataCache.InMemory();
        await cache.UpsertAsync(Source(DateTimeOffset.UtcNow), [Obj("APP", "EMP", "TABLE"), Obj("APP", "EMP", "SYNONYM"), Obj("APP", "DEPT", "TABLE")], default);

        await cache.InvalidateAsync(Key, [new DbObjectRef("APP", "EMP", "TABLE")], default);
        Assert.Equal(["DEPT", "EMP"], (await cache.GetObjectsAsync(Key, null, null, default)).Select(o => o.Name).Order());
        Assert.Null((await cache.GetSourceAsync(Key, default))!.RefreshedAt);

        await cache.InvalidateAsync(Key, [], default);
        Assert.Empty(await cache.GetObjectsAsync(Key, null, null, default));
    }

    [Fact]
    public async Task Prune_deletes_unused_sources_with_their_objects()
    {
        using var cache = SqliteMetadataCache.InMemory();
        await cache.UpsertAsync(Source(used: DateTimeOffset.UtcNow.AddDays(-40)), [Obj("APP", "EMP", "TABLE")], default);
        var other = new MetaSource { DbKey = "OTHER/-/X", LastUsedAt = DateTimeOffset.UtcNow };
        await cache.UpsertAsync(other, [new MetaObject { DbKey = "OTHER/-/X", Owner = "X", Name = "T", Type = "TABLE" }], default);

        await cache.PruneAsync(new MetadataCacheSettings { RetentionDays = 30 }, default);

        Assert.Null(await cache.GetSourceAsync(Key, default));
        Assert.Empty(await cache.GetObjectsAsync(Key, null, null, default));
        Assert.NotNull(await cache.GetSourceAsync("OTHER/-/X", default));
        Assert.Single(await cache.GetObjectsAsync("OTHER/-/X", null, null, default));
    }

    [Fact]
    public async Task Future_version_database_falls_back_to_memory_without_touching_file()
    {
        using (var seed = SqliteMetadataCache.ForFile(DbFile))
        {
            await seed.GetSourceAsync(Key, default);
        }

        await using (var c = new SqliteConnection(RawConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO schema_version VALUES (99, '2099-01-01T00:00:00.000Z', 'future')";
            await cmd.ExecuteNonQueryAsync();
        }

        using var cache = SqliteMetadataCache.ForFile(DbFile);
        await cache.UpsertAsync(Source(), [Obj("APP", "EMP", "TABLE")], default);

        Assert.True(cache.IsMemoryOnly);
        Assert.NotNull(cache.Notice);
        Assert.Single(await cache.GetObjectsAsync(Key, null, null, default));

        using var reopened = SqliteMetadataCache.ForFile(DbFile);
        await reopened.UpsertAsync(Source(), [], default);
        Assert.Empty(await reopened.GetObjectsAsync(Key, null, null, default)); // 파일에는 쓰지 않았다.
    }

    [Fact]
    public async Task Corrupt_database_is_moved_aside_and_recreated()
    {
        await File.WriteAllBytesAsync(DbFile, [.. System.Text.Encoding.ASCII.GetBytes("this is not a sqlite file at all, just text padding "), .. new byte[200]]);

        using var cache = SqliteMetadataCache.ForFile(DbFile);
        await cache.UpsertAsync(Source(), [Obj("APP", "EMP", "TABLE")], default);

        Assert.False(cache.IsMemoryOnly);
        Assert.NotNull(cache.Notice);
        Assert.Single(await cache.GetObjectsAsync(Key, null, null, default));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "backup"), "oranda.db.corrupt-*"));
    }

    [Fact]
    public async Task Concurrent_operations_do_not_fail()
    {
        using var cache = SqliteMetadataCache.ForFile(DbFile);
        await cache.UpsertAsync(Source(), [], default);
        var tasks = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? cache.UpsertAsync(Source(), [Obj("APP", "T" + i, "TABLE")], default)
            : cache.GetObjectsAsync(Key, null, null, default));
        await Task.WhenAll(tasks);
        Assert.Equal(10, (await cache.GetObjectsAsync(Key, null, null, default)).Count);
    }
}
