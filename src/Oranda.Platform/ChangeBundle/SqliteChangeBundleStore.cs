using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;
using Bundle = Oranda.Core.ChangeBundle.ChangeBundleInfo;

namespace Oranda.Platform.ChangeBundle;

/// <summary>
/// 반영 묶음 저장소(<c>oranda.db</c> 0006, 테이블 편집기 설계 9.1). 문장 전문은 <see cref="ISecretRedactor"/> 로 비밀을 가린 뒤 저장하고(TE-2),
/// 해시는 가린 글로 계산한다. 파일을 쓸 수 없으면(마이그레이션 실패, 더 새로운 버전) 쓰기는 조용히 건너뛴다.
/// <see cref="Oranda.Platform.Journal.SqliteChangeJournalStore"/> 와 같은 꼴이다.
/// </summary>
[OrandaService(typeof(IChangeBundleStore))]
internal sealed class SqliteChangeBundleStore : IChangeBundleStore, IDisposable
{
    private readonly string _dbFile;
    private readonly string _backupDirectory;
    private readonly ISecretRedactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private bool _writable;

    public SqliteChangeBundleStore(IAppDataPaths paths, ISecretRedactor redactor)
        : this(Require(paths).DatabaseFile, paths.BackupDirectory, redactor)
    {
    }

    private SqliteChangeBundleStore(string dbFile, string backupDirectory, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _dbFile = dbFile;
        _backupDirectory = backupDirectory;
        _redactor = redactor;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일을 쓴다.</summary>
    internal static SqliteChangeBundleStore ForFile(string dbFile, ISecretRedactor redactor, string? backupDirectory = null)
        => new(dbFile, backupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbFile))!, "backup"), redactor);

    public bool IsWritable => _writable;

    public void Dispose() => _gate.Dispose();

    public Task<IReadOnlyList<Bundle>> ListBundlesAsync(CancellationToken ct)
        => RunAsync<IReadOnlyList<Bundle>>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = BundleSelect + " ORDER BY id";
            return await ReadBundlesAsync(cmd, ct);
        }, [], ct);

    public Task<Bundle?> GetBundleAsync(long id, CancellationToken ct)
        => RunAsync(false, c => GetBundleCoreAsync(c, id, ct), null, ct);

    public Task<Bundle?> GetCurrentAsync(CancellationToken ct)
        => RunAsync<Bundle?>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = BundleSelect + " WHERE is_current = 1";
            return (await ReadBundlesAsync(cmd, ct)).FirstOrDefault();
        }, null, ct);

    public Task<Bundle?> CreateAsync(string name, Dbms? dbms, IReadOnlyList<string> targetOwners, bool makeCurrent, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(targetOwners);
        return RunAsync<Bundle?>(true, async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            if (makeCurrent)
            {
                await ExecAsync(c, tx, "UPDATE change_bundle SET is_current = 0 WHERE is_current = 1", ct);
            }

            var now = OrandaDatabase.ToText(DateTimeOffset.UtcNow);
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO change_bundle(name, created_at, updated_at, dbms, target_owners, is_current)
                VALUES ($name, $now, $now, $dbms, $owners, $current);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", name.Trim());
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$dbms", (object?)dbms?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owners", OwnersJson(targetOwners));
            cmd.Parameters.AddWithValue("$current", makeCurrent ? 1 : 0);
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            await tx.CommitAsync(ct);
            return await GetBundleCoreAsync(c, id, ct);
        }, null, ct);
    }

    public Task RenameAsync(long id, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WriteAsync("UPDATE change_bundle SET name = $name, updated_at = $now WHERE id = $id",
            p => { p.AddWithValue("$id", id); p.AddWithValue("$name", name.Trim()); }, ct);
    }

    public Task UpdateTargetAsync(long id, Dbms? dbms, IReadOnlyList<string> targetOwners, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetOwners);
        return WriteAsync("UPDATE change_bundle SET dbms = $dbms, target_owners = $owners, updated_at = $now WHERE id = $id", p =>
        {
            p.AddWithValue("$id", id);
            p.AddWithValue("$dbms", (object?)dbms?.ToString() ?? DBNull.Value);
            p.AddWithValue("$owners", OwnersJson(targetOwners));
        }, ct);
    }

    public Task SetCurrentAsync(long? id, CancellationToken ct)
        => RunAsync<object?>(true, async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            await ExecAsync(c, tx, "UPDATE change_bundle SET is_current = 0 WHERE is_current = 1", ct);
            if (id is { } value)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE change_bundle SET is_current = 1 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return null;
        }, null, ct);

    public Task DeleteAsync(long id, CancellationToken ct)
        => WriteAsync("DELETE FROM change_bundle WHERE id = $id", p => p.AddWithValue("$id", id), ct, touch: false);

    public Task<IReadOnlyList<ChangeBundleItem>> ListItemsAsync(long bundleId, CancellationToken ct)
        => RunAsync<IReadOnlyList<ChangeBundleItem>>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = ItemSelect + " WHERE bundle_id = $b ORDER BY position, id";
            cmd.Parameters.AddWithValue("$b", bundleId);
            return await ReadItemsAsync(cmd, ct);
        }, [], ct);

    public Task<ChangeBundleItem?> FindSameAsync(long bundleId, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = StatementHash.Compute(_redactor.Redact(text));
        return RunAsync<ChangeBundleItem?>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = ItemSelect + " WHERE bundle_id = $b AND statement_hash = $h ORDER BY position, id LIMIT 1";
            cmd.Parameters.AddWithValue("$b", bundleId);
            cmd.Parameters.AddWithValue("$h", hash);
            return (await ReadItemsAsync(cmd, ct)).FirstOrDefault();
        }, null, ct);
    }

    public Task<ChangeBundleItem?> AddItemAsync(long bundleId, NewChangeBundleItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (text, cautions) = Redact(item.Text, item.Cautions);
        return RunAsync<ChangeBundleItem?>(true, async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            var now = OrandaDatabase.ToText(DateTimeOffset.UtcNow);
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO change_bundle_item(bundle_id, position, statement_text, source, target_owner, target_name, checked, added_at,
                                               statement_hash, cautions, warning)
                SELECT $b, COALESCE((SELECT MAX(position) FROM change_bundle_item WHERE bundle_id = $b), 0) + 1, $text, $source, $owner, $name, 1, $now,
                       $hash, $cautions, $warning
                 WHERE EXISTS (SELECT 1 FROM change_bundle WHERE id = $b);
                SELECT CASE WHEN changes() = 0 THEN NULL ELSE last_insert_rowid() END;
                """;
            cmd.Parameters.AddWithValue("$b", bundleId);
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$source", item.Source.ToString());
            cmd.Parameters.AddWithValue("$owner", (object?)item.TargetOwner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)item.TargetName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$hash", StatementHash.Compute(text));
            cmd.Parameters.AddWithValue("$cautions", (int)cautions);
            cmd.Parameters.AddWithValue("$warning", (object?)item.Warning ?? DBNull.Value);
            if (await cmd.ExecuteScalarAsync(ct) is not long id)
            {
                return null; // 묶음이 없다
            }

            await TouchAsync(c, tx, bundleId, ct);
            await tx.CommitAsync(ct);
            await using var read = c.CreateCommand();
            read.CommandText = ItemSelect + " WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            return (await ReadItemsAsync(read, ct)).FirstOrDefault();
        }, null, ct);
    }

    public Task UpdateItemTextAsync(long itemId, string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var redacted = _redactor.Redact(text);
        var secret = !string.Equals(redacted, text, StringComparison.Ordinal);
        return RunAsync<object?>(true, async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE change_bundle_item
                   SET statement_text = $text, statement_hash = $hash,
                       cautions = cautions | $bits
                 WHERE id = $id;
                DELETE FROM change_bundle_run WHERE item_id = $id;
                UPDATE change_bundle SET updated_at = $now WHERE id = (SELECT bundle_id FROM change_bundle_item WHERE id = $id);
                """;
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$text", redacted);
            cmd.Parameters.AddWithValue("$hash", StatementHash.Compute(redacted));
            cmd.Parameters.AddWithValue("$bits", secret ? (int)ChangeBundleCautions.SecretRedacted : 0);
            cmd.Parameters.AddWithValue("$now", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }, null, ct);
    }

    public Task SetCheckedAsync(long itemId, bool isChecked, CancellationToken ct)
        => WriteAsync("UPDATE change_bundle_item SET checked = $c WHERE id = $id",
            p => { p.AddWithValue("$id", itemId); p.AddWithValue("$c", isChecked ? 1 : 0); }, ct, itemTouch: true);

    public Task ReorderAsync(long bundleId, IReadOnlyList<long> itemIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return RunAsync<object?>(true, async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            var current = new List<long>();
            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT id FROM change_bundle_item WHERE bundle_id = $b ORDER BY position, id";
                read.Parameters.AddWithValue("$b", bundleId);
                await using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    current.Add(reader.GetInt64(0));
                }
            }

            var known = current.ToHashSet();
            var order = itemIds.Where(known.Contains).Distinct().ToList();
            order.AddRange(current.Where(id => !order.Contains(id)));
            for (var i = 0; i < order.Count; i++)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE change_bundle_item SET position = $p WHERE id = $id";
                cmd.Parameters.AddWithValue("$p", i + 1);
                cmd.Parameters.AddWithValue("$id", order[i]);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await TouchAsync(c, tx, bundleId, ct);
            await tx.CommitAsync(ct);
            return null;
        }, null, ct);
    }

    public Task DeleteItemAsync(long itemId, CancellationToken ct)
        => RunAsync<object?>(true, async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE change_bundle SET updated_at = $now WHERE id = (SELECT bundle_id FROM change_bundle_item WHERE id = $id);
                DELETE FROM change_bundle_item WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$now", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }, null, ct);

    public Task AddRunAsync(ChangeBundleRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(run.ConnectionName);
        return RunAsync<object?>(true, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO change_bundle_run(item_id, environment, connection_name, ran_at, succeeded)
                SELECT $item, $env, $conn, $at, $ok WHERE EXISTS (SELECT 1 FROM change_bundle_item WHERE id = $item)
                """;
            cmd.Parameters.AddWithValue("$item", run.ItemId);
            cmd.Parameters.AddWithValue("$env", run.Environment.ToString());
            cmd.Parameters.AddWithValue("$conn", run.ConnectionName);
            cmd.Parameters.AddWithValue("$at", OrandaDatabase.ToText(run.RanAt == default ? DateTimeOffset.UtcNow : run.RanAt));
            cmd.Parameters.AddWithValue("$ok", run.Succeeded ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }, null, ct);
    }

    public Task<IReadOnlyDictionary<long, ChangeBundleRunMarks>> GetRunMarksAsync(long bundleId, CancellationToken ct)
        => RunAsync<IReadOnlyDictionary<long, ChangeBundleRunMarks>>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT r.item_id, r.environment, MAX(r.ran_at)
                  FROM change_bundle_run r JOIN change_bundle_item i ON i.id = r.item_id
                 WHERE i.bundle_id = $b AND r.succeeded = 1
                 GROUP BY r.item_id, r.environment
                """;
            cmd.Parameters.AddWithValue("$b", bundleId);
            var marks = new Dictionary<long, ChangeBundleRunMarks>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var at = OrandaDatabase.FromText(reader.GetString(2));
                var m = marks.GetValueOrDefault(id, ChangeBundleRunMarks.None);
                marks[id] = Enum.TryParse<ConnectionEnvironment>(reader.GetString(1), out var env) ? env switch
                {
                    ConnectionEnvironment.Development => m with { Development = at },
                    ConnectionEnvironment.Test => m with { Test = at },
                    _ => m with { Production = at },
                } : m;
            }

            return marks;
        }, new Dictionary<long, ChangeBundleRunMarks>(), ct);

    private const string BundleSelect = "SELECT id, name, created_at, updated_at, dbms, target_owners, is_current FROM change_bundle";

    private const string ItemSelect = """
        SELECT id, bundle_id, position, statement_text, source, target_owner, target_name, checked, added_at, statement_hash, cautions, warning
          FROM change_bundle_item
        """;

    private (string Text, ChangeBundleCautions Cautions) Redact(string text, ChangeBundleCautions cautions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var redacted = _redactor.Redact(text);
        return string.Equals(redacted, text, StringComparison.Ordinal) ? (text, cautions) : (redacted, cautions | ChangeBundleCautions.SecretRedacted);
    }

    private static async Task<Bundle?> GetBundleCoreAsync(SqliteConnection c, long id, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = BundleSelect + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (await ReadBundlesAsync(cmd, ct)).FirstOrDefault();
    }

    private static async Task<List<Bundle>> ReadBundlesAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<Bundle>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Bundle(
                r.GetInt64(0),
                r.GetString(1),
                OrandaDatabase.FromText(r.GetString(2)) ?? default,
                OrandaDatabase.FromText(r.GetString(3)) ?? default,
                r.IsDBNull(4) ? null : Enum.TryParse<Dbms>(r.GetString(4), out var d) ? d : null,
                ParseOwners(r.GetString(5)),
                r.GetInt64(6) == 1));
        }

        return list;
    }

    private static async Task<List<ChangeBundleItem>> ReadItemsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<ChangeBundleItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ChangeBundleItem(
                r.GetInt64(0),
                r.GetInt64(1),
                r.GetInt32(2),
                r.GetString(3),
                Enum.TryParse<ChangeBundleItemSource>(r.GetString(4), out var s) ? s : ChangeBundleItemSource.Manual,
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.GetInt64(7) == 1,
                OrandaDatabase.FromText(r.GetString(8)) ?? default,
                r.GetString(9),
                (ChangeBundleCautions)r.GetInt32(10),
                r.IsDBNull(11) ? null : r.GetString(11)));
        }

        return list;
    }

    private static string OwnersJson(IReadOnlyList<string> owners)
        => JsonSerializer.Serialize(owners.Where(o => !string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.Ordinal).ToArray());

    private static string[] ParseOwners(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task ExecAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TouchAsync(SqliteConnection c, SqliteTransaction tx, long bundleId, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE change_bundle SET updated_at = $now WHERE id = $b";
        cmd.Parameters.AddWithValue("$b", bundleId);
        cmd.Parameters.AddWithValue("$now", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 쓰기 문장 하나. <paramref name="touch"/> = 묶음 행(<c>$id</c>)의 고친 때, <paramref name="itemTouch"/> = 문장 행(<c>$id</c>)이 든 묶음의 고친 때.
    /// </summary>
    private Task<object?> WriteAsync(string sql, Action<SqliteParameterCollection> bind, CancellationToken ct, bool touch = true, bool itemTouch = false)
        => RunAsync<object?>(true, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql + (itemTouch
                ? "; UPDATE change_bundle SET updated_at = $now WHERE id = (SELECT bundle_id FROM change_bundle_item WHERE id = $id)"
                : string.Empty);
            bind(cmd.Parameters);
            if (touch || itemTouch)
            {
                cmd.Parameters.AddWithValue("$now", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
            }

            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }, null, ct);

    private static IAppDataPaths Require(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths;
    }

    private async Task<T> RunAsync<T>(bool write, Func<SqliteConnection, Task<T>> action, T fallback, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connectionString is null)
            {
                var result = await OrandaDatabase.PrepareFileAsync(_dbFile, _backupDirectory, ct);
                _writable = result.Writable;
                _connectionString = OrandaDatabase.FileConnectionString(_dbFile);
            }

            if (write && !_writable)
            {
                return fallback;
            }

            await using var connection = await OrandaDatabase.OpenConnectionAsync(_connectionString, ct);
            return await action(connection);
        }
        finally
        {
            _gate.Release();
        }
    }
}
