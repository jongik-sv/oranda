using Microsoft.Data.Sqlite;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;

namespace Oranda.Platform.TableEdit;

/// <summary>
/// 새 테이블 작업 저장소(<c>oranda.db</c> 0007, 테이블 편집기 설계 18.7·TE-43). 모델은 <see cref="TableDraftJson"/> 한 줄로 넣는다.
/// 파일을 쓸 수 없으면(마이그레이션 실패, 더 새로운 버전) 쓰기는 조용히 건너뛴다.
/// <see cref="Oranda.Platform.ChangeBundle.SqliteChangeBundleStore"/> 와 같은 꼴이다.
/// </summary>
[OrandaService(typeof(ITableDraftStore))]
internal sealed class SqliteTableDraftStore : ITableDraftStore, IDisposable
{
    private const string EntrySelect = "SELECT id, connection_name, dbms, owner, name, updated_at, open_by FROM table_draft";

    private readonly string _dbFile;
    private readonly string _backupDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private bool _writable;

    public SqliteTableDraftStore(IAppDataPaths paths)
        : this(Require(paths).DatabaseFile, paths.BackupDirectory)
    {
    }

    private SqliteTableDraftStore(string dbFile, string backupDirectory)
    {
        _dbFile = dbFile;
        _backupDirectory = backupDirectory;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일을 쓴다.</summary>
    internal static SqliteTableDraftStore ForFile(string dbFile, string? backupDirectory = null)
        => new(dbFile, backupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbFile))!, "backup"));

    public bool IsWritable => _writable;

    public void Dispose() => _gate.Dispose();

    public Task<IReadOnlyList<TableDraftEntry>> ListAsync(string? connectionName, string? owner, CancellationToken ct)
        => RunAsync<IReadOnlyList<TableDraftEntry>>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = EntrySelect
                + " WHERE ($conn IS NULL OR connection_name = $conn) AND ($owner IS NULL OR owner = $owner)"
                + " ORDER BY updated_at DESC, id DESC";
            cmd.Parameters.AddWithValue("$conn", (object?)connectionName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owner", (object?)owner ?? DBNull.Value);
            var list = new List<TableDraftEntry>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (ReadEntry(r) is { } entry)
                {
                    list.Add(entry);
                }
            }

            return list;
        }, [], ct);

    public Task<StoredTableDraft?> GetAsync(long id, CancellationToken ct)
        => RunAsync<StoredTableDraft?>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = EntrySelect.Replace(" FROM", ", model_json FROM", StringComparison.Ordinal) + " WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || ReadEntry(r) is not { } entry)
            {
                return null;
            }

            return TableDraftJson.TryDeserialize(r.GetString(7)) is { } draft ? new StoredTableDraft(entry, draft) : null;
        }, null, ct);

    public Task<TableDraftEntry?> SaveAsync(
        long? id, string connectionName, Dbms dbms, TableDraft draft, string instance, bool keepOpen, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var json = TableDraftJson.Serialize(draft);
        return RunAsync<TableDraftEntry?>(true, async c =>
        {
            var now = OrandaDatabase.ToText(DateTimeOffset.UtcNow);
            var openBy = keepOpen ? instance : null;
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            long? saved = null;
            if (id is { } existing)
            {
                // 이 인스턴스가 쥔 행만 덮어쓴다.
                await using var update = Command(c, tx, """
                    UPDATE table_draft SET connection_name = $conn, dbms = $dbms, owner = $owner, name = $name, model_json = $json,
                                           updated_at = $now, open_by = $openBy
                     WHERE id = $id AND open_by = $instance
                    RETURNING id;
                    """);
                update.Parameters.AddWithValue("$id", existing);
                BindRow(update, connectionName, dbms, draft, json, now, openBy, instance);
                saved = await update.ExecuteScalarAsync(ct) as long?;
            }

            if (saved is null)
            {
                // 새 탭이거나, 그 사이 지워졌거나 남이 쥔 행: 새 id 로 넣는다.
                await using var insert = Command(c, tx, """
                    INSERT INTO table_draft(connection_name, dbms, owner, name, model_json, updated_at, open_by)
                    VALUES ($conn, $dbms, $owner, $name, $json, $now, $openBy)
                    RETURNING id;
                    """);
                BindRow(insert, connectionName, dbms, draft, json, now, openBy, instance);
                saved = (long)(await insert.ExecuteScalarAsync(ct))!;
            }

            await tx.CommitAsync(ct);
            return new TableDraftEntry(saved.Value, connectionName, dbms, draft.Owner, draft.Name ?? string.Empty,
                OrandaDatabase.FromText(now) ?? DateTimeOffset.UtcNow, openBy);
        }, null, ct);
    }

    public Task<bool> ClaimAsync(long id, string? expectedOpenBy, string instance, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        return ChangeAsync("UPDATE table_draft SET open_by = $instance WHERE id = $id AND open_by IS $expected", id, expectedOpenBy, instance, ct);
    }

    public Task ReleaseAsync(long id, string instance, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        return ChangeAsync("UPDATE table_draft SET open_by = NULL WHERE id = $id AND open_by = $instance", id, null, instance, ct);
    }

    public Task<bool> DeleteAsync(long id, string? expectedOpenBy, CancellationToken ct)
        => ChangeAsync("DELETE FROM table_draft WHERE id = $id AND open_by IS $expected", id, expectedOpenBy, null, ct);

    /// <summary>한 줄. 모르는 DBMS 값(손으로 고친 파일 등)은 건너뛴다.</summary>
    private static TableDraftEntry? ReadEntry(SqliteDataReader r)
        => Enum.TryParse<Dbms>(r.GetString(2), out var dbms)
            ? new TableDraftEntry(r.GetInt64(0), r.GetString(1), dbms, r.GetString(3), r.GetString(4),
                OrandaDatabase.FromText(r.GetString(5)) ?? DateTimeOffset.MinValue, r.IsDBNull(6) ? null : r.GetString(6))
            : null;

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void BindRow(SqliteCommand cmd, string connectionName, Dbms dbms, TableDraft draft, string json, string now, string? openBy, string instance)
    {
        cmd.Parameters.AddWithValue("$conn", connectionName);
        cmd.Parameters.AddWithValue("$dbms", dbms.ToString());
        cmd.Parameters.AddWithValue("$owner", draft.Owner);
        cmd.Parameters.AddWithValue("$name", draft.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$openBy", (object?)openBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$instance", instance);
    }

    /// <summary>행 하나를 바꾸는 문장. 바뀌었으면 참. 쓸 수 없으면 거짓.</summary>
    private Task<bool> ChangeAsync(string sql, long id, string? expected, string? instance, CancellationToken ct)
        => RunAsync(true, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$expected", (object?)expected ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$instance", (object?)instance ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }, false, ct);

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
        catch (SqliteException) when (!_writable)
        {
            return fallback; // 0007 을 적용하지 못한 파일: 표가 없을 수 있다
        }
        finally
        {
            _gate.Release();
        }
    }
}
