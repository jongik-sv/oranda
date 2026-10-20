using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Oranda.Core.Composition;
using Oranda.Core.Journal;
using Oranda.Core.Security;
using Oranda.Core.Storage;
using Oranda.Core.Text;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Journal;

/// <summary>
/// 변경 일지(change_journal, data-model 3.2.1·3.4). 기록 전 DDL·요약의 비밀은 <see cref="ISecretRedactor"/> 로 가리고,
/// 해시는 가린 뒤 본문으로 계산한다. 파일을 쓸 수 없으면(마이그레이션 실패, 더 새로운 버전) 쓰기는 조용히 건너뛴다.
/// 소유: TSK-13-07.
/// </summary>
[OrandaService(typeof(IChangeJournalStore))]
internal sealed class SqliteChangeJournalStore : IChangeJournalStore, IDisposable
{
    /// <summary>previous_ddl 최대 UTF-8 바이트. 넘으면 본문을 쌓지 않고 실패 행으로 남긴다.</summary>
    internal const int MaxDdlBytes = 2 * 1024 * 1024;

    private const int MaxSummaryChars = 200;
    private const int MaxReasonChars = 500;

    private readonly string _dbFile;
    private readonly string _backupDirectory;
    private readonly ISecretRedactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private bool _writable;

    public SqliteChangeJournalStore(IAppDataPaths paths, ISecretRedactor redactor)
        : this(Require(paths).DatabaseFile, paths.BackupDirectory, redactor)
    {
    }

    private SqliteChangeJournalStore(string dbFile, string backupDirectory, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _dbFile = dbFile;
        _backupDirectory = backupDirectory;
        _redactor = redactor;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일을 쓴다.</summary>
    internal static SqliteChangeJournalStore ForFile(string dbFile, ISecretRedactor redactor, string? backupDirectory = null)
        => new(dbFile, backupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbFile))!, "backup"), redactor);

    /// <summary>사용자에게 알릴 말(손상 백업, 읽기 전용 이유). 정상이면 null.</summary>
    public string? Notice { get; private set; }

    public bool IsWritable => _writable;

    public void Dispose() => _gate.Dispose();

    public async Task<long?> AddAsync(JournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var failure = entry.FailureReason is null ? null : Limit(_redactor.Redact(entry.FailureReason), MaxReasonChars);
        string? ddl = null;
        if (entry.PreviousDdl is { } raw)
        {
            // 저장 소스(PL/SQL)는 가리지 않는다: 변수 이름 password 같은 평범한 코드를 ***로 바꾸면 되돌리기 본문이 깨진다.
            // 비밀이 들어갈 수 있는 USER·DB_LINK 같은 DDL 만 가린다.
            ddl = IsStoredSource(entry.ObjectType) ? raw : _redactor.Redact(raw);
            if (Encoding.UTF8.GetByteCount(ddl) > MaxDdlBytes)
            {
                ddl = null;
                failure = $"DDL 이 {MaxDdlBytes / 1024 / 1024} MiB 를 넘어 보관하지 않았다";
            }
        }

        if (ddl is null && failure is null)
        {
            failure = "보관할 DDL 이 비어 있다";
        }

        var hash = ddl is null ? null : JournalHash.Compute(ddl);
        var summary = entry.StatementSummary is null ? null : Limit(_redactor.Redact(entry.StatementSummary), MaxSummaryChars);
        return await RunAsync(true, async c =>
        {
            if (hash is not null)
            {
                await using var last = c.CreateCommand();
                last.CommandText = """
                    SELECT content_hash FROM change_journal
                     WHERE db_key = $db AND owner = $owner AND name = $name AND object_type = $type AND failure_reason IS NULL
                     ORDER BY id DESC LIMIT 1
                    """;
                last.Parameters.AddWithValue("$db", entry.DbKey);
                last.Parameters.AddWithValue("$owner", entry.Owner);
                last.Parameters.AddWithValue("$name", entry.Name);
                last.Parameters.AddWithValue("$type", entry.ObjectType);
                if (await last.ExecuteScalarAsync(ct) is string previous && string.Equals(previous, hash, StringComparison.Ordinal))
                {
                    return (long?)null;
                }
            }

            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO change_journal(captured_at, profile_id, profile_name, db_key, server_name, owner, name, object_type,
                                           cause, statement_summary, previous_ddl, content_hash, failure_reason)
                VALUES ($at, $pid, $pname, $db, $server, $owner, $name, $type, $cause, $summary, $ddl, $hash, $failure);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$at", OrandaDatabase.ToText(entry.CapturedAt == default ? DateTimeOffset.UtcNow : entry.CapturedAt));
            cmd.Parameters.AddWithValue("$pid", entry.ProfileId.ToString("D"));
            cmd.Parameters.AddWithValue("$pname", entry.ProfileName ?? string.Empty);
            cmd.Parameters.AddWithValue("$db", entry.DbKey);
            cmd.Parameters.AddWithValue("$server", (object?)entry.ServerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owner", entry.Owner);
            cmd.Parameters.AddWithValue("$name", entry.Name);
            cmd.Parameters.AddWithValue("$type", entry.ObjectType);
            cmd.Parameters.AddWithValue("$cause", entry.Cause.ToString());
            cmd.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ddl", (object?)ddl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }, null, ct);
    }

    public async Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await RunAsync<IReadOnlyList<JournalEntry>>(false, async c =>
        {
            var where = new List<string>();
            await using var cmd = c.CreateCommand();
            if (query.Text?.Trim() is { Length: > 0 } text)
            {
                where.Add("(owner LIKE $like ESCAPE '\\' OR name LIKE $like ESCAPE '\\' OR statement_summary LIKE $like ESCAPE '\\' "
                    + "OR previous_ddl LIKE $like ESCAPE '\\')");
                cmd.Parameters.AddWithValue("$like", "%" + SqlLike.Escape(text) + "%");
            }

            Filter(cmd, where, "db_key", "$db", query.DbKey);
            Filter(cmd, where, "owner", "$owner", query.Owner);
            Filter(cmd, where, "name", "$name", query.Name);
            Filter(cmd, where, "object_type", "$type", query.ObjectType);
            if (query.OnlySucceeded)
            {
                where.Add("failure_reason IS NULL");
            }

            cmd.CommandText = Select + (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where))
                + " ORDER BY id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));
            var list = new List<JournalEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(Read(reader));
            }

            return list;
        }, [], ct);
    }

    public async Task<JournalEntry?> GetAsync(long id, CancellationToken ct)
    {
        return await RunAsync<JournalEntry?>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = Select + " WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Read(reader) : null;
        }, null, ct);
    }

    public async Task PruneAsync(JournalRetention retention, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retention);
        await RunAsync<object?>(true, async c =>
        {
            if (retention.MaxAgeDays > 0)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM change_journal WHERE captured_at < $cutoff";
                cmd.Parameters.AddWithValue("$cutoff", OrandaDatabase.ToText(DateTimeOffset.UtcNow.AddDays(-retention.MaxAgeDays)));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (retention.MaxVersionsPerObject > 0)
            {
                // 성공 행만 객체별로 센다. 실패 행은 기간으로만 정리한다.
                await using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM change_journal WHERE id IN (
                      SELECT id FROM (
                        SELECT id, ROW_NUMBER() OVER (PARTITION BY db_key, owner, name, object_type ORDER BY id DESC) AS rn
                          FROM change_journal WHERE failure_reason IS NULL)
                       WHERE rn > $max)
                    """;
                cmd.Parameters.AddWithValue("$max", retention.MaxVersionsPerObject);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return null;
        }, null, ct);
    }

    private const string Select = """
        SELECT id, captured_at, profile_id, profile_name, db_key, server_name, owner, name, object_type, cause,
               statement_summary, previous_ddl, content_hash, failure_reason
          FROM change_journal
        """;

    private static bool IsStoredSource(string objectType)
        => objectType is "PACKAGE" or "PACKAGE BODY" or "PROCEDURE" or "FUNCTION" or "TRIGGER" or "TYPE" or "TYPE BODY"
            or "LIBRARY" or "JAVA SOURCE";

    private static void Filter(SqliteCommand cmd, List<string> where, string column, string parameter, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            where.Add($"{column} = {parameter}");
            cmd.Parameters.AddWithValue(parameter, value);
        }
    }

    private static JournalEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        CapturedAt = OrandaDatabase.FromText(r.GetString(1)) ?? default,
        ProfileId = Guid.TryParse(r.GetString(2), out var g) ? g : Guid.Empty,
        ProfileName = r.GetString(3),
        DbKey = r.GetString(4),
        ServerName = r.IsDBNull(5) ? null : r.GetString(5),
        Owner = r.GetString(6),
        Name = r.GetString(7),
        ObjectType = r.GetString(8),
        Cause = Enum.TryParse<JournalCause>(r.GetString(9), out var cause) ? cause : JournalCause.Ddl,
        StatementSummary = r.IsDBNull(10) ? null : r.GetString(10),
        PreviousDdl = r.IsDBNull(11) ? null : r.GetString(11),
        ContentHash = r.IsDBNull(12) ? null : r.GetString(12),
        FailureReason = r.IsDBNull(13) ? null : r.GetString(13),
    };

    private static string Limit(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max];
    }

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
                Notice = result.Notice;
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
