using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Oranda.Platform.Storage;

/// <summary>
/// oranda.db 열기·PRAGMA·마이그레이션(Migrations/OrandaDb/NNNN_*.sql)·백업(data-model 3.1, 4.2). 이력 저장소도 이것을 쓴다.
/// 마이그레이션이 실패하거나 DB 가 앱보다 새 버전이면 <see cref="OpenResult.Writable"/> 이 false 다(캐시는 메모리만, 이력은 읽기 전용).
/// 소유: TSK-04-04.
/// </summary>
internal static class OrandaDatabase
{
    private const string ResourcePrefix = "Migrations/OrandaDb/";
    private const int KeepBackups = 2;

    private static readonly Lazy<IReadOnlyList<Script>> Scripts = new(ReadScripts);

    /// <summary>
    /// 파일마다 점검·마이그레이션을 한 줄로 세운다: 앱 시작 직후 여러 저장소(이력·일지·메타데이터 캐시·되돌리기 시작 정리 …)가 동시에 처음 열면
    /// 한 저장소가 저널 모드를 바꾸거나 마이그레이션하는 동안 다른 저장소의 점검이 잠금에 걸리고, 같은 스크립트를 함께 돌렸다.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> PrepareGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal); // Windows 경로는 대소문자를 가리지 않는다

    /// <summary>열기 결과. <see cref="Notice"/> 는 사용자에게 알릴 말(없으면 null).</summary>
    internal sealed record OpenResult(bool Writable, int Version, string? Notice);

    private sealed record Script(int Version, string Description, string Sql);

    /// <summary>앱이 아는 가장 큰 마이그레이션 번호.</summary>
    public static int LatestVersion => Scripts.Value.Count == 0 ? 0 : Scripts.Value[^1].Version;

    /// <summary>연결마다 거는 PRAGMA 를 건 연결을 연다(data-model 3.1). 메모리 DB 에는 WAL 을 걸지 않는다.</summary>
    public static async Task<SqliteConnection> OpenConnectionAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var memory = connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase);
            var pragmas = (memory ? string.Empty : "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; ")
                + "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await ExecAsync(connection, pragmas, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static string FileConnectionString(string dbFile)
        => new SqliteConnectionStringBuilder { DataSource = dbFile, Pooling = false }.ToString();

    /// <summary>
    /// 파일 DB 를 점검·마이그레이션한다. 손상(quick_check 실패)이면 <c>backup/oranda.db.corrupt-시각</c> 으로 옮기고 새로 만든다.
    /// 적용할 스크립트가 있으면 먼저 <c>VACUUM INTO</c> 백업(최근 2개 유지).
    /// </summary>
    public static async Task<OpenResult> PrepareFileAsync(string dbFile, string backupDirectory, CancellationToken ct)
    {
        var gate = PrepareGates.GetOrAdd(Path.GetFullPath(dbFile), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await PrepareFileCoreAsync(dbFile, backupDirectory, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<OpenResult> PrepareFileCoreAsync(string dbFile, string backupDirectory, CancellationToken ct)
    {
        string? notice = null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbFile))!);
        if (File.Exists(dbFile) && !await IsHealthyAsync(dbFile, ct))
        {
            Directory.CreateDirectory(backupDirectory);
            var corrupt = Path.Combine(backupDirectory, "oranda.db.corrupt-" + Stamp());
            File.Move(dbFile, corrupt, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(dbFile + suffix))
                {
                    File.Delete(dbFile + suffix);
                }
            }

            notice = "oranda.db 가 손상되어 새로 만들었습니다(이력이 유실될 수 있음): " + corrupt;
        }

        await using var connection = await OpenConnectionAsync(FileConnectionString(dbFile), ct);
        var result = await MigrateAsync(connection, backupDirectory, ct);
        return notice is null ? result : result with { Notice = notice + (result.Notice is null ? string.Empty : " / " + result.Notice) };
    }

    /// <summary>열려 있는 연결(메모리 DB 포함)에 마이그레이션을 적용한다. <paramref name="backupDirectory"/> 가 null 이면 백업은 건너뛴다.</summary>
    public static async Task<OpenResult> MigrateAsync(SqliteConnection connection, string? backupDirectory, CancellationToken ct)
    {
        var scripts = Scripts.Value;
        var current = await GetVersionAsync(connection, ct);
        if (current > LatestVersion)
        {
            return new OpenResult(false, current, $"oranda.db 가 더 새로운 버전(v{current})이라 읽기 전용으로 엽니다.");
        }

        var pending = scripts.Where(s => s.Version > current).ToList();
        if (pending.Count == 0)
        {
            return new OpenResult(true, current, null);
        }

        if (backupDirectory is not null && current > 0)
        {
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(backupDirectory, $"oranda.db.v{current}.{Stamp()}.bak");
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM INTO $path";
            cmd.Parameters.AddWithValue("$path", backup);
            await cmd.ExecuteNonQueryAsync(ct);
            TrimBackups(backupDirectory);
        }

        foreach (var script in pending)
        {
            await ExecAsync(connection, "BEGIN IMMEDIATE", ct);
            try
            {
                // 쓰기 잠금을 잡은 뒤 다시 본다: 다른 프로세스(두 번째 앱 창)가 그 사이에 이 스크립트를 적용했을 수 있다.
                var now = await GetVersionAsync(connection, ct);
                if (now >= script.Version)
                {
                    await ExecAsync(connection, "COMMIT", ct);
                    current = now;
                    if (current > LatestVersion)
                    {
                        return new OpenResult(false, current, $"oranda.db 가 더 새로운 버전(v{current})이라 읽기 전용으로 엽니다.");
                    }

                    continue;
                }

                await ExecAsync(connection, script.Sql, ct);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO schema_version(version, applied_at, description) "
                    + "VALUES ($v, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), $d)";
                cmd.Parameters.AddWithValue("$v", script.Version);
                cmd.Parameters.AddWithValue("$d", script.Description);
                await cmd.ExecuteNonQueryAsync(ct);
                await ExecAsync(connection, "COMMIT", ct);
                current = script.Version;
            }
            catch (SqliteException ex)
            {
                await ExecAsync(connection, "ROLLBACK", CancellationToken.None);
                return new OpenResult(false, current, $"oranda.db 마이그레이션 {script.Version:D4} 실패: {ex.Message}");
            }
        }

        return new OpenResult(true, current, null);
    }

    /// <summary><c>SELECT MAX(version) FROM schema_version</c>. 테이블이 없으면 0.</summary>
    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_version'";
        if (await exists.ExecuteScalarAsync(ct) is null)
        {
            return 0;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>ISO 8601 UTC 밀리초(data-model 3.1).</summary>
    public static string ToText(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? FromText(string? text)
        => string.IsNullOrEmpty(text)
            ? null
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static async Task<bool> IsHealthyAsync(string dbFile, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqliteConnection(FileConnectionString(dbFile));
            await connection.OpenAsync(ct);
            // 다른 연결·프로세스가 잠시 잠금을 쥔 것(저널 모드 바꾸기·마이그레이션·백업)은 손상이 아니다: 기다리고, 그래도 바쁘면 건강하다고 본다.
            await using (var wait = connection.CreateCommand())
            {
                wait.CommandText = "PRAGMA busy_timeout = 5000";
                await wait.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            return string.Equals(await cmd.ExecuteScalarAsync(ct) as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
        {
            return true; // 손상으로 옮기면 사용자 데이터가 새 파일로 바뀐다. 진짜 손상이면 다음 열기에서 다시 본다
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

    private static void TrimBackups(string backupDirectory)
    {
        var old = new DirectoryInfo(backupDirectory).GetFiles("oranda.db.v*.bak")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(KeepBackups);
        foreach (var file in old)
        {
            file.Delete();
        }
    }

    private static List<Script> ReadScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var list = new List<Script>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var path = name.Replace('\\', '/');
            if (!path.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var file = path[ResourcePrefix.Length..];
            if (file.Length < 6 || file[4] != '_'
                || !int.TryParse(file.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"마이그레이션 이름 형식 오류(NNNN_설명.sql): {file}");
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add(new Script(version, file[5..^4], reader.ReadToEnd()));
        }

        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
            {
                throw new InvalidOperationException($"마이그레이션 번호가 1부터 빈틈없이 이어지지 않는다: {list[i].Version:D4}");
            }
        }

        return list;
    }
}
