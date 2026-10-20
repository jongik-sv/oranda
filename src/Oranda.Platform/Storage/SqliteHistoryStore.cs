using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Text;

namespace Oranda.Platform.Storage;

/// <summary>
/// 실행 이력(history·history_fts, data-model 3.2·3.4·5.3). 기록 전 SQL 의 비밀은 <see cref="ISecretRedactor"/> 로 가린다.
/// 파일을 쓸 수 없으면(마이그레이션 실패, 더 새로운 버전) 읽기 전용이다 — 쓰기는 조용히 건너뛰고 <see cref="Notice"/> 에 이유를 둔다.
/// 소유: TSK-06-06.
/// </summary>
[OrandaService(typeof(IHistoryStore))]
internal sealed class SqliteHistoryStore : IHistoryStore, IDisposable
{
    /// <summary>sql_text 최대 UTF-8 바이트(data-model 3.4).</summary>
    internal const int MaxSqlBytes = 256 * 1024;

    private const int MaxErrorChars = 1000;

    private readonly string _dbFile;
    private readonly string _backupDirectory;
    private readonly ISecretRedactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private bool _writable;
    private Task _vacuum = Task.CompletedTask;

    public SqliteHistoryStore(IAppDataPaths paths, ISecretRedactor redactor)
        : this(Require(paths).DatabaseFile, paths.BackupDirectory, redactor)
    {
    }

    private SqliteHistoryStore(string dbFile, string backupDirectory, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _dbFile = dbFile;
        _backupDirectory = backupDirectory;
        _redactor = redactor;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일을 쓴다.</summary>
    internal static SqliteHistoryStore ForFile(string dbFile, ISecretRedactor redactor, string? backupDirectory = null)
        => new(dbFile, backupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbFile))!, "backup"), redactor);

    /// <summary>사용자에게 알릴 말(손상 백업, 읽기 전용 이유). 정상이면 null.</summary>
    public string? Notice { get; private set; }

    /// <summary>false = 읽기 전용(새 기록·지우기 안 함).</summary>
    public bool IsWritable => _writable;

    /// <summary>마지막으로 시작한 백그라운드 VACUUM(시험이 기다리는 용도).</summary>
    internal Task PendingVacuum => _vacuum;

    public void Dispose() => _gate.Dispose();

    public async Task<long> AddAsync(HistoryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (sql, truncated) = Truncate(_redactor.Redact(entry.SqlText ?? string.Empty));
        var error = entry.ErrorMessage is null ? null : LimitError(_redactor.Redact(entry.ErrorMessage));
        return await RunAsync(true, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO history(profile_id, profile_name, db_key, executed_at, sql_text, sql_truncated, statement_kind,
                                    source, status, elapsed_ms, rows_affected, error_code, error_message, binds_json)
                VALUES ($pid, $pname, $db, $at, $sql, $trunc, $kind, $src, $status, $ms, $rows, $ecode, $emsg, $binds);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$pid", entry.ProfileId.ToString("D"));
            cmd.Parameters.AddWithValue("$pname", entry.ProfileName ?? string.Empty);
            cmd.Parameters.AddWithValue("$db", (object?)entry.DbKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", OrandaDatabase.ToText(entry.ExecutedAt == default ? DateTimeOffset.UtcNow : entry.ExecutedAt));
            cmd.Parameters.AddWithValue("$sql", sql);
            cmd.Parameters.AddWithValue("$trunc", truncated || entry.SqlTruncated ? 1 : 0);
            cmd.Parameters.AddWithValue("$kind", entry.StatementKind.ToString());
            cmd.Parameters.AddWithValue("$src", entry.Source.ToString());
            cmd.Parameters.AddWithValue("$status", entry.Status.ToString());
            cmd.Parameters.AddWithValue("$ms", (object?)entry.ElapsedMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rows", (object?)entry.RowsAffected ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ecode", (object?)entry.ErrorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$emsg", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$binds", (object?)entry.BindsJson ?? DBNull.Value);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }, 0L, ct);
    }

    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await RunAsync(false, async c =>
        {
            var where = new List<string>();
            await using var cmd = c.CreateCommand();
            var text = query.Text?.Trim();
            var from = "FROM history h";
            if (!string.IsNullOrEmpty(text))
            {
                if (text.Length >= 3)
                {
                    from += " JOIN history_fts f ON f.rowid = h.id";
                    where.Add("history_fts MATCH $match");
                    cmd.Parameters.AddWithValue("$match", "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                }
                else
                {
                    where.Add("h.sql_text LIKE $like ESCAPE '\\'");
                    cmd.Parameters.AddWithValue("$like", "%" + SqlLike.Escape(text) + "%");
                }
            }

            if (query.ProfileId is { } pid)
            {
                where.Add("h.profile_id = $pid");
                cmd.Parameters.AddWithValue("$pid", pid.ToString("D"));
            }

            if (query.From is { } f1)
            {
                where.Add("h.executed_at >= $from");
                cmd.Parameters.AddWithValue("$from", OrandaDatabase.ToText(f1));
            }

            if (query.To is { } t1)
            {
                where.Add("h.executed_at <= $to");
                cmd.Parameters.AddWithValue("$to", OrandaDatabase.ToText(t1));
            }

            if (query.Status is { } st)
            {
                where.Add("h.status = $status");
                cmd.Parameters.AddWithValue("$status", st.ToString());
            }

            if (query.BeforeId is { } before)
            {
                where.Add("h.id < $before");
                cmd.Parameters.AddWithValue("$before", before);
            }

            cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100_000));
            cmd.CommandText = "SELECT h.id, h.profile_id, h.profile_name, h.db_key, h.executed_at, h.sql_text, h.sql_truncated, "
                + "h.statement_kind, h.source, h.status, h.elapsed_ms, h.rows_affected, h.error_code, h.error_message, h.binds_json "
                + from
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
                + " ORDER BY h.executed_at DESC, h.id DESC LIMIT $limit";

            var list = new List<HistoryEntry>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(Read(r));
            }

            return (IReadOnlyList<HistoryEntry>)list;
        }, [], ct);
    }

    public async Task DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        await RunAsync<object?>(true, async c =>
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            foreach (var id in ids)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM history WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return null;
        }, null, ct);
    }

    public async Task ClearAsync(Guid? profileId, CancellationToken ct)
    {
        var done = await RunAsync(true, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = profileId is null ? "DELETE FROM history" : "DELETE FROM history WHERE profile_id = $pid";
            if (profileId is { } p)
            {
                cmd.Parameters.AddWithValue("$pid", p.ToString("D"));
            }

            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, false, ct);

        if (done)
        {
            _vacuum = Task.Run(VacuumAsync, CancellationToken.None);
        }
    }

    public async Task PruneAsync(HistorySettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await RunAsync<object?>(true, async c =>
        {
            if (settings.MaxAgeDays > 0)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM history WHERE executed_at < $cutoff";
                cmd.Parameters.AddWithValue("$cutoff", OrandaDatabase.ToText(DateTimeOffset.UtcNow.AddDays(-settings.MaxAgeDays)));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (settings.MaxEntries > 0)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM history WHERE id IN "
                    + "(SELECT id FROM history ORDER BY executed_at DESC, id DESC LIMIT -1 OFFSET $max)";
                cmd.Parameters.AddWithValue("$max", settings.MaxEntries);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return null;
        }, null, ct);
    }

    private static IAppDataPaths Require(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths;
    }

    private static HistoryEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ProfileId = Guid.TryParse(r.GetString(1), out var g) ? g : Guid.Empty,
        ProfileName = r.GetString(2),
        DbKey = r.IsDBNull(3) ? null : r.GetString(3),
        ExecutedAt = OrandaDatabase.FromText(r.GetString(4)) ?? default,
        SqlText = r.GetString(5),
        SqlTruncated = r.GetInt64(6) != 0,
        StatementKind = Enum.TryParse<StatementKind>(r.GetString(7), out var k) ? k : StatementKind.Other,
        Source = Enum.TryParse<HistorySource>(r.GetString(8), out var s) ? s : HistorySource.Editor,
        Status = Enum.TryParse<HistoryStatus>(r.GetString(9), out var st) ? st : HistoryStatus.Failed,
        ElapsedMs = r.IsDBNull(10) ? null : r.GetInt64(10),
        RowsAffected = r.IsDBNull(11) ? null : r.GetInt64(11),
        ErrorCode = r.IsDBNull(12) ? null : r.GetString(12),
        ErrorMessage = r.IsDBNull(13) ? null : r.GetString(13),
        BindsJson = r.IsDBNull(14) ? null : r.GetString(14),
    };

    /// <summary>첫 줄만, 1,000자까지(data-model 5.3 표).</summary>
    private static string LimitError(string message)
    {
        var line = message.Split('\n', 2)[0].TrimEnd('\r');
        return line.Length <= MaxErrorChars ? line : line[..MaxErrorChars];
    }

    /// <summary>UTF-8 256 KiB 를 넘으면 앞부분만(글자 중간에서 자르지 않는다).</summary>
    internal static (string Text, bool Truncated) Truncate(string sql)
    {
        if (Encoding.UTF8.GetByteCount(sql) <= MaxSqlBytes)
        {
            return (sql, false);
        }

        var bytes = Encoding.UTF8.GetBytes(sql);
        var length = MaxSqlBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--; // 글자 중간이면 그 글자 앞까지.
        }

        return (Encoding.UTF8.GetString(bytes, 0, length), true);
    }

    private async Task VacuumAsync()
    {
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_connectionString is null || !_writable)
            {
                return;
            }

            await using var c = await OrandaDatabase.OpenConnectionAsync(_connectionString, CancellationToken.None);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException)
        {
            // VACUUM 실패는 기능에 영향이 없다(파일 크기만 안 줄어듦).
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunAsync<T>(bool write, Func<SqliteConnection, Task<T>> action, T fallback, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connectionString is null)
            {
                await InitializeAsync(ct);
            }

            if (write && !_writable)
            {
                return fallback;
            }

            await using var connection = await OrandaDatabase.OpenConnectionAsync(_connectionString!, ct);
            return await action(connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var result = await OrandaDatabase.PrepareFileAsync(_dbFile, _backupDirectory, ct);
        _writable = result.Writable;
        Notice = result.Notice;
        _connectionString = OrandaDatabase.FileConnectionString(_dbFile);
    }
}
