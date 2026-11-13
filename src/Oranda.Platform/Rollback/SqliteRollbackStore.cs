using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Security;
using Oranda.Core.Storage;
using Oranda.Core.Text;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Rollback;

/// <summary>
/// 되돌리기 지점 저장소(설계 12장, RD-10). 메타데이터는 oranda.db 의 rollback_group·rollback_statement(마이그레이션 0005),
/// 문장 글·바인드·원복 단계는 <c>{data}/rollback/{yyyy-MM}/{id:D8}.orb</c> 암호화 파일(<see cref="IRollbackVault"/>, AAD <c>orb1:{id}:{db_key}</c>).
/// 요약·이유는 <see cref="ISecretRedactor"/> 로 가린 뒤 쓴다. DB 를 쓸 수 없으면(마이그레이션 실패·더 새 버전) 저장은 조용히 건너뛴다.
/// 파일은 임시 파일에 쓴 뒤 이름을 바꾸고, 행과 파일은 한 트랜잭션 안에서 만든다(파일 쓰기 실패 = 행도 남기지 않고 예외).
/// 소유: RB-21.
/// </summary>
[OrandaService(typeof(IRollbackStore))]
internal sealed class SqliteRollbackStore : IRollbackStore, IDisposable
{
    internal const string FileExtension = ".orb";

    private const int MaxSummaryChars = 200;
    private const int MaxReasonChars = 500;
    private const string ReasonSeparator = ": ";

    private readonly string _dbFile;
    private readonly string _backupDirectory;
    private readonly string _rollbackDirectory;
    private readonly IRollbackVault _vault;
    private readonly ISecretRedactor _redactor;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _processStartedAt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private bool _writable;

    public SqliteRollbackStore(IAppDataPaths paths, IRollbackVault vault, ISecretRedactor redactor)
        : this(Require(paths).DatabaseFile, paths.BackupDirectory, paths.RollbackDirectory, vault, redactor, TimeProvider.System,
            new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime))
    {
    }

    private SqliteRollbackStore(string dbFile, string backupDirectory, string rollbackDirectory, IRollbackVault vault,
        ISecretRedactor redactor, TimeProvider time, DateTimeOffset processStartedAt)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(redactor);
        _dbFile = dbFile;
        _backupDirectory = backupDirectory;
        _rollbackDirectory = rollbackDirectory;
        _vault = vault;
        _redactor = redactor;
        _time = time;
        _processStartedAt = processStartedAt;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일과 원복 폴더를 쓴다.</summary>
    internal static SqliteRollbackStore ForFile(string dbFile, string rollbackDirectory, IRollbackVault vault, ISecretRedactor redactor,
        TimeProvider? time = null, DateTimeOffset? processStartedAt = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbFile))!;
        var clock = time ?? TimeProvider.System;
        return new(dbFile, Path.Combine(dir, "backup"), rollbackDirectory, vault, redactor, clock, processStartedAt ?? clock.GetUtcNow());
    }

    /// <summary>사용자에게 알릴 말(손상 백업, 읽기 전용 이유). 정상이면 null.</summary>
    public string? Notice { get; private set; }

    /// <summary>DB 를 아직 열지 않았으면 true 로 본다(처음 쓸 때 연다). 열어 본 뒤 쓸 수 없으면 false.</summary>
    public bool CanStoreScripts => _vault.IsDurable && (_connectionString is null || _writable);

    public void Dispose() => _gate.Dispose();

    public async Task<long> SaveAsync(RollbackGroup group, RollbackScriptDocument? document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        var stateReason = EncodeReason(group);
        var statements = group.Statements.Select(s => s with
        {
            Summary = Limit(_redactor.Redact(s.Summary ?? string.Empty), MaxSummaryChars),
            Reason = s.Reason is null ? null : Limit(_redactor.Redact(s.Reason), MaxReasonChars),
        }).ToList();

        var wantsFile = document is not null && _vault.IsDurable;
        if (wantsFile)
        {
            // 키를 처음 만들 때(DPAPI·키체인 저장)는 oranda.db 쓰기 잠금 밖에서 한다. 실패하면 행도 만들지 않고 예외.
            await _vault.ProtectAsync(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, ct);
        }

        return await RunAsync(true, async c =>
        {
            string? temp = null;
            long id;
            string path;
            await ExecAsync(c, "BEGIN IMMEDIATE", ct);
            try
            {
                id = await UpsertGroupAsync(c, group, stateReason, ct);
                await ReplaceStatementsAsync(c, id, statements, ct);
                if (!wantsFile)
                {
                    await ExecAsync(c, "COMMIT", ct);
                    return id;
                }

                var relative = RelativePath(id, group.StartedAt);
                path = Path.Combine(_rollbackDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                temp = await WriteTempFileAsync(path, id, group.DbKey, document! with { GroupId = id }, ct);
                await SetScriptFileAsync(c, id, relative, ct);
                await ExecAsync(c, "COMMIT", ct);
            }
            catch
            {
                if (temp is not null)
                {
                    TryDelete(temp);
                }

                await TryRollbackAsync(c);
                throw;
            }

            // 커밋한 뒤에 이름을 바꾼다: 행이 되돌려졌는데 파일만 새 내용으로 바뀌는 일이 없다.
            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                if (group.Id == 0)
                {
                    // 새 묶음: 없는 파일을 가리키지 않게 한다. 덮어쓰기면 이전 파일이 그대로 남아 행과 맞다.
                    await SetScriptFileAsync(c, id, null, CancellationToken.None);
                }

                throw;
            }

            return id;
        }, group.Id, ct);
    }

    private static async Task SetScriptFileAsync(SqliteConnection c, long id, string? relative, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE rollback_group SET script_file = $file WHERE id = $id";
        cmd.Parameters.AddWithValue("$file", (object?)relative ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TryRollbackAsync(SqliteConnection c)
    {
        try
        {
            await ExecAsync(c, "ROLLBACK", CancellationToken.None);
        }
        catch (SqliteException)
        {
            // SQLITE_FULL·IOERR 처럼 SQLite 가 이미 되돌린 트랜잭션이면 "no transaction is active". 원래 예외를 남긴다.
        }
    }

    public async Task<IReadOnlyList<RollbackGroup>> SearchAsync(RollbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await RunAsync<IReadOnlyList<RollbackGroup>>(false, async c =>
        {
            var where = new List<string>();
            await using var cmd = c.CreateCommand();
            if (query.Text?.Trim() is { Length: > 0 } text)
            {
                where.Add("(g.profile_name LIKE $like ESCAPE '\\' OR g.server_name LIKE $like ESCAPE '\\' OR g.tables LIKE $like ESCAPE '\\' "
                    + "OR g.reason LIKE $like ESCAPE '\\' OR g.transaction_id LIKE $like ESCAPE '\\' "
                    + "OR EXISTS (SELECT 1 FROM rollback_statement s WHERE s.group_id = g.id AND s.summary LIKE $like ESCAPE '\\'))");
                cmd.Parameters.AddWithValue("$like", "%" + SqlLike.Escape(text) + "%");
            }

            if (!string.IsNullOrEmpty(query.DbKey))
            {
                where.Add("g.db_key = $db");
                cmd.Parameters.AddWithValue("$db", query.DbKey);
            }

            if (query.Table is { } table)
            {
                where.Add("EXISTS (SELECT 1 FROM rollback_statement s WHERE s.group_id = g.id AND s.owner = $owner AND s.name = $name)");
                cmd.Parameters.AddWithValue("$owner", table.Owner);
                cmd.Parameters.AddWithValue("$name", table.Name);
            }

            cmd.CommandText = SelectGroup + (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where))
                + " ORDER BY COALESCE(g.ended_at, g.started_at) DESC, g.id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));
            var groups = new List<RollbackGroup>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    groups.Add(ReadGroup(reader));
                }
            }

            return await AttachStatementsAsync(c, groups, ct);
        }, [], ct);
    }

    public async Task<RollbackGroup?> GetAsync(long id, CancellationToken ct)
    {
        return await RunAsync<RollbackGroup?>(false, async c =>
        {
            RollbackGroup group;
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = SelectGroup + " WHERE g.id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    return null;
                }

                group = ReadGroup(reader);
            }

            return (await AttachStatementsAsync(c, [group], ct))[0];
        }, null, ct);
    }

    /// <summary>파일이 없으면 null. 키가 없거나 파일이 변조·손상됐으면 <see cref="System.Security.Cryptography.CryptographicException"/>.</summary>
    public async Task<RollbackScriptDocument?> LoadDocumentAsync(long id, CancellationToken ct)
    {
        var located = await RunAsync<(string File, string DbKey)?>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT script_file, db_key FROM rollback_group WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) && !reader.IsDBNull(0) ? (reader.GetString(0), reader.GetString(1)) : null;
        }, null, ct);
        if (located is not { } found)
        {
            return null;
        }

        var path = Path.Combine(_rollbackDirectory, found.File.Replace('/', Path.DirectorySeparatorChar));
        byte[] cipher;
        try
        {
            cipher = await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 파일이 없거나 보관 정리가 막 지웠다.
            return null;
        }

        var plain = await _vault.UnprotectAsync(cipher, Associated(id, found.DbKey), ct);
        try
        {
            return JsonSerializer.Deserialize(plain, RollbackJsonContext.Default.RollbackScriptDocument)
                ?? throw new System.Security.Cryptography.CryptographicException("원복 파일 내용이 비어 있습니다.");
        }
        catch (JsonException ex)
        {
            throw new System.Security.Cryptography.CryptographicException("원복 파일 내용을 읽지 못했습니다.", ex);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// 앱 시작 때: 앞선 실행이 「열림」으로 남긴 묶음(7.4, RD-7)을 「확인 필요 · 앱이 끝나 결과를 모름」으로 바꾼다. 바꾼 수.
    /// 이 프로세스가 시작한 뒤 열린 묶음(정리 작업보다 먼저 저장된 이번 실행의 묶음)은 건드리지 않는다.
    /// </summary>
    public async Task<int> MarkOrphansAsync(CancellationToken ct)
    {
        return await RunAsync(true, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE rollback_group
                   SET state = $state, end_kind = $kind, end_source = $source, reason = $reason
                 WHERE state = $open AND started_at < $since
                """;
            cmd.Parameters.AddWithValue("$since", OrandaDatabase.ToText(_processStartedAt));
            cmd.Parameters.AddWithValue("$state", nameof(RollbackScriptState.Unverified));
            cmd.Parameters.AddWithValue("$kind", nameof(TransactionEndKind.Unknown));
            cmd.Parameters.AddWithValue("$source", nameof(TransactionEndSource.AppRestart));
            cmd.Parameters.AddWithValue("$reason", OrphanReason);
            cmd.Parameters.AddWithValue("$open", nameof(RollbackScriptState.Open));
            return await cmd.ExecuteNonQueryAsync(ct);
        }, 0, ct);
    }

    /// <summary>
    /// 보관 기간(12.3)이 지난 묶음의 행과 파일을 지우고, 표에 없는 <c>.orb</c> 파일과 남은 임시 파일을 지운다.
    /// 기간은 묶음이 끝난 시각(열림이면 시작 시각)에서 센다. 0 = 설정의 「모두 지우기」.
    /// 「열림」 묶음(지금 열린 트랜잭션, 7.4)은 지우지 않는다: 커밋하면 그 원복 스크립트가 필요하다.
    /// </summary>
    public async Task PruneAsync(int retentionDays, CancellationToken ct)
    {
        var days = Math.Clamp(retentionDays, 0, RollbackSettings.MaxRetentionDays);
        await RunAsync<object?>(true, async c =>
        {
            var cutoff = OrandaDatabase.ToText(_time.GetUtcNow().AddDays(-days));
            var expiredFiles = new List<string>();
            await using (var select = c.CreateCommand())
            {
                select.CommandText = "SELECT script_file FROM rollback_group WHERE COALESCE(ended_at, started_at) < $cutoff AND state <> $open AND script_file IS NOT NULL";
                select.Parameters.AddWithValue("$cutoff", cutoff);
                select.Parameters.AddWithValue("$open", nameof(RollbackScriptState.Open));
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    expiredFiles.Add(reader.GetString(0));
                }
            }

            await using (var delete = c.CreateCommand())
            {
                delete.CommandText = "DELETE FROM rollback_group WHERE COALESCE(ended_at, started_at) < $cutoff AND state <> $open";
                delete.Parameters.AddWithValue("$cutoff", cutoff);
                delete.Parameters.AddWithValue("$open", nameof(RollbackScriptState.Open));
                await delete.ExecuteNonQueryAsync(ct);
            }

            foreach (var file in expiredFiles)
            {
                TryDelete(Path.Combine(_rollbackDirectory, file.Replace('/', Path.DirectorySeparatorChar)));
            }

            await DeleteOrphanFilesAsync(c, ct);
            return null;
        }, null, ct);
    }

    /// <summary>표에 없는 파일을 지우기 전에 기다리는 시간.</summary>
    internal static readonly TimeSpan OrphanMinAge = TimeSpan.FromHours(24);

    internal const string OrphanReason = "앱이 끝나 결과를 모름";

    private const string SelectGroup = """
        SELECT g.id, g.started_at, g.ended_at, g.profile_id, g.profile_name, g.db_key, g.server_name, g.environment, g.dbms,
               g.point_name, g.base_point, g.commit_point, g.transaction_id, g.end_kind, g.end_source, g.state, g.reason,
               g.step_count, g.rows_total, g.tables, g.origin, g.origin_group_id, g.script_file, g.expires_at
          FROM rollback_group g
        """;

    /// <summary>파일의 AAD(RD-9). 파일을 다른 묶음 자리로 옮기면 풀리지 않는다.</summary>
    internal static byte[] Associated(long id, string dbKey)
        => Encoding.UTF8.GetBytes("orb1:" + id.ToString(CultureInfo.InvariantCulture) + ":" + dbKey);

    /// <summary><c>{yyyy-MM}/{id:D8}.orb</c>(시작 시각 UTC 의 달). 표에는 '/' 로 적는다.</summary>
    internal static string RelativePath(long id, DateTimeOffset startedAt)
        => startedAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + "/"
            + id.ToString("D8", CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>암호화해 <paramref name="path"/> 옆 임시 파일에 쓰고 그 경로를 돌려준다. 이름 바꾸기는 부른 쪽이 커밋 뒤에 한다.</summary>
    private async Task<string> WriteTempFileAsync(string path, long id, string dbKey, RollbackScriptDocument document, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(document, RollbackJsonContext.Default.RollbackScriptDocument);
        byte[] cipher;
        try
        {
            cipher = await _vault.ProtectAsync(plain, Associated(id, dbKey), ct);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, cipher, ct);
            return temp;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task<long> UpsertGroupAsync(SqliteConnection c, RollbackGroup g, string? reason, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        // 덮어쓸 때 script_file 은 그대로 둔다(문서 없이 상태만 고치는 저장).
        cmd.CommandText = (g.Id == 0
            ? "INSERT INTO rollback_group(" + GroupColumns + ") VALUES (" + GroupValues + ")"
            : "INSERT INTO rollback_group(id, " + GroupColumns + ") VALUES ($id, " + GroupValues + ") "
                + "ON CONFLICT(id) DO UPDATE SET " + string.Join(", ", GroupColumns.Split(", ").Select(col => col + " = excluded." + col)))
            + "; SELECT " + (g.Id == 0 ? "last_insert_rowid()" : "$id");
        if (g.Id != 0)
        {
            cmd.Parameters.AddWithValue("$id", g.Id);
        }

        var point = g.BasePoint ?? g.CommitPoint;
        cmd.Parameters.AddWithValue("$started", OrandaDatabase.ToText(g.StartedAt));
        cmd.Parameters.AddWithValue("$ended", g.EndedAt is { } ended ? OrandaDatabase.ToText(ended) : DBNull.Value);
        cmd.Parameters.AddWithValue("$pid", g.ProfileId.ToString("D"));
        cmd.Parameters.AddWithValue("$pname", g.ProfileName ?? string.Empty);
        cmd.Parameters.AddWithValue("$db", g.DbKey);
        cmd.Parameters.AddWithValue("$server", (object?)g.ServerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$env", g.Environment.ToString());
        cmd.Parameters.AddWithValue("$dbms", g.Dbms.ToString());
        cmd.Parameters.AddWithValue("$pointName", (object?)point?.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$base", (object?)g.BasePoint?.Number ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$commit", (object?)g.CommitPoint?.Number ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tx", (object?)g.TransactionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$endKind", (object?)g.EndKind?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$endSource", (object?)g.EndSource?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", g.State.ToString());
        cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stmts", g.Statements.Count);
        cmd.Parameters.AddWithValue("$steps", g.StepCount);
        cmd.Parameters.AddWithValue("$rows", g.RowsTotal);
        cmd.Parameters.AddWithValue("$tables", string.Join('\n', g.Tables.Select(t => t.Owner + "." + t.Name)));
        cmd.Parameters.AddWithValue("$origin", g.Origin.ToString());
        cmd.Parameters.AddWithValue("$originId", (object?)g.OriginGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expires", g.ExpiresAt is { } exp ? OrandaDatabase.ToText(exp) : DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private const string GroupColumns = "started_at, ended_at, profile_id, profile_name, db_key, server_name, environment, dbms, point_name, "
        + "base_point, commit_point, transaction_id, end_kind, end_source, state, reason, statement_count, step_count, rows_total, tables, "
        + "origin, origin_group_id, expires_at";

    private const string GroupValues = "$started, $ended, $pid, $pname, $db, $server, $env, $dbms, $pointName, $base, $commit, $tx, "
        + "$endKind, $endSource, $state, $reason, $stmts, $steps, $rows, $tables, $origin, $originId, $expires";

    private static async Task ReplaceStatementsAsync(SqliteConnection c, long id, List<RollbackStatementInfo> statements, CancellationToken ct)
    {
        await using (var delete = c.CreateCommand())
        {
            delete.CommandText = "DELETE FROM rollback_statement WHERE group_id = $id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(ct);
        }

        if (statements.Count == 0)
        {
            return;
        }

        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rollback_statement(group_id, seq, executed_at, source, verb, owner, name, summary, rows_affected, locked_rows,
                                           before_point, state, reason, step_count, invalidated)
            VALUES ($id, $seq, $at, $source, $verb, $owner, $name, $summary, $rows, $locked, $before, $state, $reason, $steps, $inv)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var seq = cmd.Parameters.Add("$seq", SqliteType.Integer);
        var at = cmd.Parameters.Add("$at", SqliteType.Text);
        var source = cmd.Parameters.Add("$source", SqliteType.Text);
        var verb = cmd.Parameters.Add("$verb", SqliteType.Text);
        var owner = cmd.Parameters.Add("$owner", SqliteType.Text);
        var name = cmd.Parameters.Add("$name", SqliteType.Text);
        var summary = cmd.Parameters.Add("$summary", SqliteType.Text);
        var rows = cmd.Parameters.Add("$rows", SqliteType.Integer);
        var locked = cmd.Parameters.Add("$locked", SqliteType.Integer);
        var before = cmd.Parameters.Add("$before", SqliteType.Integer);
        var state = cmd.Parameters.Add("$state", SqliteType.Text);
        var reason = cmd.Parameters.Add("$reason", SqliteType.Text);
        var steps = cmd.Parameters.Add("$steps", SqliteType.Integer);
        var inv = cmd.Parameters.Add("$inv", SqliteType.Integer);
        foreach (var s in statements)
        {
            seq.Value = s.Seq;
            at.Value = OrandaDatabase.ToText(s.ExecutedAt);
            source.Value = s.Source.ToString();
            verb.Value = s.Verb.ToString();
            owner.Value = (object?)s.Table?.Owner ?? DBNull.Value;
            name.Value = (object?)s.Table?.Name ?? DBNull.Value;
            summary.Value = s.Summary;
            rows.Value = (object?)s.RowsAffected ?? DBNull.Value;
            locked.Value = (object?)s.LockedRows ?? DBNull.Value;
            before.Value = (object?)s.BeforePoint?.Number ?? DBNull.Value;
            state.Value = s.State.ToString();
            reason.Value = (object?)s.Reason ?? DBNull.Value;
            steps.Value = s.StepCount;
            inv.Value = s.Invalidated ? 1 : 0;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<IReadOnlyList<RollbackGroup>> AttachStatementsAsync(SqliteConnection c, List<RollbackGroup> groups, CancellationToken ct)
    {
        if (groups.Count == 0)
        {
            return groups;
        }

        var byGroup = groups.ToDictionary(g => g.Id, _ => new List<RollbackStatementInfo>());
        var pointNames = groups.ToDictionary(g => g.Id, g => (g.BasePoint ?? g.CommitPoint)?.Name);
        await using (var cmd = c.CreateCommand())
        {
            var names = new List<string>();
            var i = 0;
            foreach (var g in groups)
            {
                var p = "$g" + i++.ToString(CultureInfo.InvariantCulture);
                names.Add(p);
                cmd.Parameters.AddWithValue(p, g.Id);
            }

            cmd.CommandText = """
                SELECT group_id, seq, executed_at, source, verb, owner, name, summary, rows_affected, locked_rows, before_point,
                       state, reason, step_count, invalidated
                  FROM rollback_statement WHERE group_id IN (
                """ + string.Join(", ", names) + ") ORDER BY group_id, seq";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var groupId = r.GetInt64(0);
                var executedAt = OrandaDatabase.FromText(r.GetString(2)) ?? default;
                var owner = r.IsDBNull(5) ? null : r.GetString(5);
                var name = r.IsDBNull(6) ? null : r.GetString(6);
                var pointName = pointNames[groupId];
                byGroup[groupId].Add(new RollbackStatementInfo(
                    r.GetInt32(1),
                    executedAt,
                    Parse(r.GetString(4), DmlVerb.Other),
                    owner is null || name is null ? null : new DbObjectRef(owner, name, "TABLE"),
                    r.IsDBNull(7) ? string.Empty : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetInt64(8),
                    r.IsDBNull(10) || pointName is null ? null : new ChangePoint(r.GetInt64(10), pointName, executedAt, string.Empty),
                    Parse(r.GetString(11), RollbackScriptState.Incomplete),
                    r.IsDBNull(12) ? null : r.GetString(12),
                    r.GetInt32(13),
                    r.GetInt64(14) != 0)
                {
                    Source = Parse(r.GetString(3), default(Oranda.Core.History.HistorySource)),
                    LockedRows = r.IsDBNull(9) ? null : r.GetInt64(9),
                });
            }
        }

        return groups.Select(g => g with { Statements = byGroup[g.Id] }).ToList();
    }

    private static RollbackGroup ReadGroup(SqliteDataReader r)
    {
        var startedAt = OrandaDatabase.FromText(r.GetString(1)) ?? default;
        var endedAt = r.IsDBNull(2) ? null : OrandaDatabase.FromText(r.GetString(2));
        var pointName = r.IsDBNull(9) ? null : r.GetString(9);
        var state = Parse(r.GetString(15), RollbackScriptState.Incomplete);
        var (reason, stateReason) = DecodeReason(state, r.IsDBNull(16) ? null : r.GetString(16));
        return new RollbackGroup
        {
            Id = r.GetInt64(0),
            StartedAt = startedAt,
            EndedAt = endedAt,
            ProfileId = Guid.TryParse(r.GetString(3), out var pid) ? pid : Guid.Empty,
            ProfileName = r.GetString(4),
            DbKey = r.GetString(5),
            ServerName = r.IsDBNull(6) ? null : r.GetString(6),
            Environment = Parse(r.GetString(7), ConnectionEnvironment.Production),
            Dbms = Parse(r.GetString(8), default(Dbms)),
            // 표에는 번호와 이름만 있다. 읽은 시각은 묶음 시작·끝 시각으로 채운다.
            BasePoint = r.IsDBNull(10) || pointName is null ? null : new ChangePoint(r.GetInt64(10), pointName, startedAt, string.Empty),
            CommitPoint = r.IsDBNull(11) || pointName is null ? null : new ChangePoint(r.GetInt64(11), pointName, endedAt ?? startedAt, string.Empty),
            TransactionId = r.IsDBNull(12) ? null : r.GetString(12),
            EndKind = r.IsDBNull(13) ? null : Parse(r.GetString(13), TransactionEndKind.Unknown),
            EndSource = r.IsDBNull(14) ? null : Parse(r.GetString(14), TransactionEndSource.AppRestart),
            State = state,
            Reason = reason,
            StateReason = stateReason,
            StepCount = r.GetInt32(17),
            RowsTotal = r.GetInt64(18),
            Tables = ParseTables(r.GetString(19)),
            Origin = Parse(r.GetString(20), RollbackOrigin.Execution),
            OriginGroupId = r.IsDBNull(21) ? null : r.GetInt64(21),
            HasScriptFile = !r.IsDBNull(22),
            ExpiresAt = r.IsDBNull(23) ? null : OrandaDatabase.FromText(r.GetString(23)),
        };
    }

    /// <summary>reason 칸 하나에 <see cref="NotCreatedReason"/> 과 가린 이유 글을 함께 담는다: <c>OverLimit</c>, <c>Failed: 글</c>, 이유 없이 글만이면 <c>: 글</c>.</summary>
    private string? EncodeReason(RollbackGroup g)
    {
        var text = g.StateReason is null ? null : Limit(_redactor.Redact(g.StateReason), MaxReasonChars);
        if (g.Reason == NotCreatedReason.None)
        {
            // 글이 우연히 "Failed: …" 로 시작해도 이유로 읽히지 않게 구분자를 앞에 붙인다.
            return text is null ? null : ReasonSeparator + text;
        }

        return text is null ? g.Reason.ToString() : g.Reason + ReasonSeparator + text;
    }

    private static (NotCreatedReason Reason, string? Text) DecodeReason(RollbackScriptState state, string? stored)
    {
        if (stored is null)
        {
            return (NotCreatedReason.None, null);
        }

        if (stored.StartsWith(ReasonSeparator, StringComparison.Ordinal))
        {
            return (NotCreatedReason.None, stored[ReasonSeparator.Length..]);
        }

        var cut = stored.IndexOf(ReasonSeparator, StringComparison.Ordinal);
        var head = cut < 0 ? stored : stored[..cut];
        if (head.Length > 0 && char.IsAsciiLetterUpper(head[0]) && Enum.TryParse<NotCreatedReason>(head, out var reason)
            && Enum.IsDefined(reason) && reason != NotCreatedReason.None)
        {
            return (reason, cut < 0 ? null : stored[(cut + ReasonSeparator.Length)..]);
        }

        return (NotCreatedReason.None, stored);
    }

    private static List<DbObjectRef> ParseTables(string text)
    {
        var list = new List<DbObjectRef>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dot = line.IndexOf('.', StringComparison.Ordinal);
            list.Add(dot < 0 ? new DbObjectRef(string.Empty, line, "TABLE") : new DbObjectRef(line[..dot], line[(dot + 1)..], "TABLE"));
        }

        return list;
    }

    private async Task DeleteOrphanFilesAsync(SqliteConnection c, CancellationToken ct)
    {
        // oranda.db 를 손상으로 새로 만든 실행(Notice 있음)에서는 표가 비어 모든 파일이 고아로 보인다. 백업 DB 를 되살릴 수 있게 남긴다.
        if (Notice is not null || !Directory.Exists(_rollbackDirectory))
        {
            return;
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT script_file FROM rollback_group WHERE script_file IS NOT NULL";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                known.Add(Path.GetFullPath(Path.Combine(_rollbackDirectory, reader.GetString(0).Replace('/', Path.DirectorySeparatorChar))));
            }
        }

        // 하루 넘게 손대지 않은 파일만 지운다: 앱을 둘 띄운 경우 다른 인스턴스가 막 쓴(아직 커밋 전) 파일을 지우지 않는다.
        var olderThan = _time.GetUtcNow().UtcDateTime - OrphanMinAge;
        foreach (var file in Directory.EnumerateFiles(_rollbackDirectory, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) > olderThan)
            {
                continue;
            }

            var orb = file.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase);
            var temp = file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            if ((orb && !known.Contains(Path.GetFullPath(file))) || temp)
            {
                TryDelete(file);
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(_rollbackDirectory))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (IOException)
            {
                // 빈 달 폴더 정리 실패는 무시한다.
            }
        }
    }

    private static T Parse<T>(string text, T fallback)
        where T : struct, Enum
        => Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) ? value : fallback;

    private static string Limit(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 다음 정리 때 다시 지운다.
        }
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
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
