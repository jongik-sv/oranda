using Microsoft.Data.Sqlite;
using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>
/// 메타데이터 캐시(meta_source·meta_object, data-model 3.2). 파일을 못 쓰는 경우(마이그레이션 실패, 더 새로운 버전)에는
/// 같은 스키마의 메모리 DB 로 동작한다(<see cref="Notice"/> 에 이유). 소유: TSK-04-04.
/// </summary>
[OrandaService(typeof(IMetadataCache))]
public sealed class SqliteMetadataCache : IMetadataCache, IDisposable
{
    private readonly Func<(string DbFile, string? BackupDirectory)?> _location;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private SqliteConnection? _sentinel;

    public SqliteMetadataCache(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _location = () => (paths.DatabaseFile, paths.BackupDirectory);
    }

    private SqliteMetadataCache(Func<(string DbFile, string? BackupDirectory)?> location)
    {
        _location = location;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일을 쓴다.</summary>
    public static SqliteMetadataCache ForFile(string dbFile, string? backupDirectory = null)
        => new(() => (dbFile, backupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbFile))!, "backup")));

    /// <summary>시험·도구용: 파일 없이 메모리만 쓴다.</summary>
    public static SqliteMetadataCache InMemory() => new(() => null);

    /// <summary>파일을 못 열어 메모리로 동작할 때 사용자에게 알릴 말. 정상이면 null.</summary>
    public string? Notice { get; private set; }

    /// <summary>true = 메모리만(앱을 닫으면 사라짐).</summary>
    public bool IsMemoryOnly { get; private set; }

    public void Dispose()
    {
        _sentinel?.Dispose();
        _gate.Dispose();
    }

    public async Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct)
    {
        return await RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT db_key, server_version, refreshed_at, last_used_at FROM meta_source WHERE db_key = $k";
            cmd.Parameters.AddWithValue("$k", dbKey);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                return null;
            }

            return new MetaSource
            {
                DbKey = r.GetString(0),
                ServerVersion = r.IsDBNull(1) ? null : r.GetString(1),
                RefreshedAt = OrandaDatabase.FromText(r.IsDBNull(2) ? null : r.GetString(2)),
                LastUsedAt = OrandaDatabase.FromText(r.GetString(3)) ?? DateTimeOffset.UtcNow,
            };
        }, ct);
    }

    public async Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct)
    {
        return await RunAsync(async c =>
        {
            await TouchAsync(c, dbKey, ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = Select + " WHERE db_key = $k AND ($o IS NULL OR owner = $o) AND ($t IS NULL OR type = $t) ORDER BY owner, type, name";
            cmd.Parameters.AddWithValue("$k", dbKey);
            cmd.Parameters.AddWithValue("$o", (object?)owner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", (object?)type ?? DBNull.Value);
            return await ReadObjectsAsync(cmd, ct);
        }, ct);
    }

    public async Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct)
    {
        return await RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = Select + " WHERE db_key = $k AND name LIKE $p ESCAPE '\\' ORDER BY name, owner, type LIMIT $n";
            cmd.Parameters.AddWithValue("$k", dbKey);
            cmd.Parameters.AddWithValue("$p", EscapeLike(namePrefix) + "%");
            cmd.Parameters.AddWithValue("$n", limit <= 0 ? -1 : limit);
            return await ReadObjectsAsync(cmd, ct);
        }, ct);
    }

    public async Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(objects);
        await RunAsync<object?>(async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO meta_source(db_key, server_version, refreshed_at, last_used_at) VALUES ($k, $v, $r, $u)
                    ON CONFLICT(db_key) DO UPDATE SET server_version = excluded.server_version,
                      refreshed_at = COALESCE(excluded.refreshed_at, meta_source.refreshed_at),
                      last_used_at = excluded.last_used_at
                    """;
                cmd.Parameters.AddWithValue("$k", source.DbKey);
                cmd.Parameters.AddWithValue("$v", (object?)source.ServerVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r", source.RefreshedAt is { } rt ? OrandaDatabase.ToText(rt) : DBNull.Value);
                cmd.Parameters.AddWithValue("$u", OrandaDatabase.ToText(source.LastUsedAt == default ? DateTimeOffset.UtcNow : source.LastUsedAt));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (objects.Count > 0)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO meta_object(db_key, owner, name, type, status, last_ddl_time, payload_json, fetched_at)
                    VALUES ($k, $o, $n, $t, $s, $d, $p, $f)
                    ON CONFLICT(db_key, owner, name, type) DO UPDATE SET status = excluded.status,
                      last_ddl_time = excluded.last_ddl_time, payload_json = excluded.payload_json, fetched_at = excluded.fetched_at
                    """;
                var pk = cmd.Parameters.Add("$k", SqliteType.Text);
                var po = cmd.Parameters.Add("$o", SqliteType.Text);
                var pn = cmd.Parameters.Add("$n", SqliteType.Text);
                var pt = cmd.Parameters.Add("$t", SqliteType.Text);
                var ps = cmd.Parameters.Add("$s", SqliteType.Text);
                var pd = cmd.Parameters.Add("$d", SqliteType.Text);
                var pp = cmd.Parameters.Add("$p", SqliteType.Text);
                var pf = cmd.Parameters.Add("$f", SqliteType.Text);
                foreach (var o in objects)
                {
                    pk.Value = source.DbKey;
                    po.Value = o.Owner;
                    pn.Value = o.Name;
                    pt.Value = o.Type;
                    ps.Value = (object?)o.Status ?? DBNull.Value;
                    pd.Value = o.LastDdlTime is { } d ? OrandaDatabase.ToText(d) : DBNull.Value;
                    pp.Value = (object?)o.PayloadJson ?? DBNull.Value;
                    pf.Value = OrandaDatabase.ToText(o.FetchedAt == default ? DateTimeOffset.UtcNow : o.FetchedAt);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            return null;
        }, ct);
    }

    /// <summary>
    /// 빈 목록 = 그 dbKey 의 객체 전부. 객체 목록이 더는 완전하다고 볼 수 없으므로 <c>refreshed_at</c> 도 비운다
    /// (다음 목록 조회가 서버를 다시 읽는다). <see cref="DbObjectRef.ObjectType"/> 이 비어 있으면 그 이름의 모든 유형.
    /// </summary>
    public async Task InvalidateAsync(string dbKey, IReadOnlyList<DbObjectRef> objects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(objects);
        await RunAsync<object?>(async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            if (objects.Count == 0)
            {
                await ExecAsync(c, tx, "DELETE FROM meta_object WHERE db_key = $k", dbKey, ct);
            }
            else
            {
                foreach (var o in objects)
                {
                    await using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM meta_object WHERE db_key = $k AND owner = $o AND name = $n AND ($t = '' OR type = $t)";
                    cmd.Parameters.AddWithValue("$k", dbKey);
                    cmd.Parameters.AddWithValue("$o", o.Owner);
                    cmd.Parameters.AddWithValue("$n", o.Name);
                    cmd.Parameters.AddWithValue("$t", o.ObjectType ?? string.Empty);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            await ExecAsync(c, tx, "UPDATE meta_source SET refreshed_at = NULL WHERE db_key = $k", dbKey, ct);
            await tx.CommitAsync(ct);
            return null;
        }, ct);
    }

    public async Task PruneAsync(MetadataCacheSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var cutoff = OrandaDatabase.ToText(DateTimeOffset.UtcNow.AddDays(-settings.RetentionDays));
        await RunAsync<object?>(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM meta_source WHERE last_used_at < $c";
            cmd.Parameters.AddWithValue("$c", cutoff);
            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }, ct);
    }

    private const string Select = "SELECT db_key, owner, name, type, status, last_ddl_time, payload_json, fetched_at FROM meta_object";

    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task ExecAsync(SqliteConnection c, SqliteTransaction tx, string sql, string dbKey, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$k", dbKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TouchAsync(SqliteConnection c, string dbKey, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE meta_source SET last_used_at = $u WHERE db_key = $k";
        cmd.Parameters.AddWithValue("$u", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$k", dbKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<MetaObject>> ReadObjectsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<MetaObject>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new MetaObject
            {
                DbKey = r.GetString(0),
                Owner = r.GetString(1),
                Name = r.GetString(2),
                Type = r.GetString(3),
                Status = r.IsDBNull(4) ? null : r.GetString(4),
                LastDdlTime = OrandaDatabase.FromText(r.IsDBNull(5) ? null : r.GetString(5)),
                PayloadJson = r.IsDBNull(6) ? null : r.GetString(6),
                FetchedAt = OrandaDatabase.FromText(r.GetString(7)) ?? default,
            });
        }

        return list;
    }

    private async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _connectionString ??= await InitializeAsync(ct);
            await using var connection = await OrandaDatabase.OpenConnectionAsync(_connectionString, ct);
            return await action(connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> InitializeAsync(CancellationToken ct)
    {
        if (_location() is { } loc)
        {
            try
            {
                var result = await OrandaDatabase.PrepareFileAsync(loc.DbFile, loc.BackupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(loc.DbFile))!, "backup"), ct);
                if (result.Writable)
                {
                    Notice = result.Notice;
                    return OrandaDatabase.FileConnectionString(loc.DbFile);
                }

                Notice = result.Notice;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                Notice = "oranda.db 를 열 수 없어 메타데이터 캐시를 메모리로만 씁니다: " + ex.Message;
            }
        }

        IsMemoryOnly = true;
        var name = "oranda-meta-" + Guid.NewGuid().ToString("N");
        var cs = new SqliteConnectionStringBuilder { DataSource = name, Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared }.ToString();
        _sentinel = await OrandaDatabase.OpenConnectionAsync(cs, ct);
        var migrated = await OrandaDatabase.MigrateAsync(_sentinel, null, ct);
        if (!migrated.Writable)
        {
            throw new InvalidOperationException(migrated.Notice);
        }

        return cs;
    }
}
