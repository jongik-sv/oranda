using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oranda.Core.Composition;
using Oranda.Core.MyBatis;
using Oranda.Core.Security;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>
/// MyBatis 시나리오 저장소(mybatis_scenario, design.md 6장, MB-04). 문장은 정규화한 매퍼 경로 + 문장 id 로 찾고,
/// 경로로 못 찾으면 namespace + 문장 id 로 찾는다(매퍼를 옮긴 경우). 이름에 pass·pwd·secret·token 이 든 파라미터 값은 저장하지 않는다.
/// 쓰기는 호출 스레드를 떠나 백그라운드 스레드에서 한다. 파일을 쓸 수 없으면(마이그레이션 실패, 더 새로운 버전) 쓰기는 조용히 건너뛰고 <see cref="Notice"/> 에 이유를 둔다.
/// 소유: TSK-02-03.
/// </summary>
[OrandaService(typeof(IMyBatisScenarioStore))]
internal sealed class SqliteMyBatisScenarioStore : IMyBatisScenarioStore, IDisposable
{
    private const string Columns = "id, mapper_path, namespace, statement_id, name, values_json, sort_order, created_at, updated_at";

    private readonly string _dbFile;
    private readonly string _backupDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private bool _writable;

    public SqliteMyBatisScenarioStore(IAppDataPaths paths)
        : this(Require(paths).DatabaseFile, paths.BackupDirectory)
    {
    }

    private SqliteMyBatisScenarioStore(string dbFile, string backupDirectory)
    {
        _dbFile = dbFile;
        _backupDirectory = backupDirectory;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일을 쓴다.</summary>
    internal static SqliteMyBatisScenarioStore ForFile(string dbFile, string? backupDirectory = null)
        => new(dbFile, backupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbFile))!, "backup"));

    public event EventHandler<MyBatisScenarioChangedEventArgs>? Changed;

    /// <summary>사용자에게 알릴 말(손상 백업, 읽기 전용 이유). 정상이면 null.</summary>
    public string? Notice { get; private set; }

    /// <summary>false = 읽기 전용(쓰기는 건너뜀).</summary>
    public bool IsWritable => _writable;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// 비교용 경로: 절대 경로로 바꾸고 끝 구분자를 떼며, Windows 는 대소문자를 무시하려고 대문자로 바꾼다(열 추가 없이 저장·조회 때 같은 규칙).
    /// </summary>
    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0))
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    public async Task<IReadOnlyList<MyBatisScenario>> ListAsync(string mapperPath, string? ns, string statementId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(statementId);
        var path = NormalizePath(mapperPath);
        return await RunAsync(false, async c =>
        {
            var found = await QueryAsync(c, "mapper_path = $path AND statement_id = $sid", null,
                [("$path", path), ("$sid", statementId)], ct).ConfigureAwait(false);
            if (found.Count == 0 && !string.IsNullOrEmpty(ns))
            {
                found = await QueryAsync(c, "namespace = $ns AND statement_id = $sid", null,
                    [("$ns", ns), ("$sid", statementId)], ct).ConfigureAwait(false);
                if (found.Count > 0 && _writable)
                {
                    // 매퍼를 옮긴 경우: 이후 Save·MoveStatement 가 새 경로로 맞도록 찾은 행의 경로를 현재 경로로 옮긴다.
                    await using var anchor = c.CreateCommand();
                    anchor.CommandText = "UPDATE mybatis_scenario SET mapper_path = $path WHERE id = $id";
                    anchor.Parameters.AddWithValue("$path", path);
                    var idParam = anchor.Parameters.Add("$id", SqliteType.Integer);
                    for (var i = 0; i < found.Count; i++)
                    {
                        idParam.Value = found[i].Id;
                        await anchor.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    found = found.Select(f => f with { MapperPath = path }).ToList();
                }
            }

            return (IReadOnlyList<MyBatisScenario>)found;
        }, [], ct).ConfigureAwait(false);
    }

    public async Task<MyBatisScenario> SaveAsync(MyBatisScenario scenario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var path = NormalizePath(scenario.MapperPath);
        var values = DropSecrets(scenario.Values);
        var json = SerializeValues(values);
        var now = DateTimeOffset.UtcNow;
        var fallback = scenario with { MapperPath = path, Values = values };
        var saved = await RunAsync(true, async c =>
        {
            await using var tx = c.BeginTransaction(deferred: false);
            if (scenario.Id != 0)
            {
                await using var update = c.CreateCommand();
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE mybatis_scenario
                       SET mapper_path = $path, namespace = $ns, statement_id = $sid, name = $name, values_json = $json, updated_at = $now
                     WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$path", path);
                update.Parameters.AddWithValue("$ns", (object?)scenario.Namespace ?? DBNull.Value);
                update.Parameters.AddWithValue("$sid", scenario.StatementId);
                update.Parameters.AddWithValue("$name", scenario.Name);
                update.Parameters.AddWithValue("$json", json);
                update.Parameters.AddWithValue("$now", OrandaDatabase.ToText(now));
                update.Parameters.AddWithValue("$id", scenario.Id);
                if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0)
                {
                    var row = (await QueryAsync(c, "id = $id", tx, [("$id", scenario.Id)], ct).ConfigureAwait(false))[0];
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return row;
                }
            }

            await using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO mybatis_scenario(mapper_path, namespace, statement_id, name, values_json, sort_order, created_at, updated_at)
                VALUES ($path, $ns, $sid, $name, $json,
                        (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM mybatis_scenario WHERE mapper_path = $path AND statement_id = $sid),
                        $now, $now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$ns", (object?)scenario.Namespace ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sid", scenario.StatementId);
            insert.Parameters.AddWithValue("$name", scenario.Name);
            insert.Parameters.AddWithValue("$json", json);
            insert.Parameters.AddWithValue("$now", OrandaDatabase.ToText(now));
            var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var created = (await QueryAsync(c, "id = $id", tx, [("$id", id)], ct).ConfigureAwait(false))[0];
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return created;
        }, fallback, ct).ConfigureAwait(false);
        if (_writable)
        {
            RaiseChanged(saved.MapperPath, saved.StatementId);
        }

        return saved;
    }

    public async Task RenameAsync(long id, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        var target = await RunAsync(true, async c =>
        {
            var row = await FindAsync(c, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            await using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE mybatis_scenario SET name = $name, updated_at = $now WHERE id = $id";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$now", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return row;
        }, null, ct).ConfigureAwait(false);
        Notify(target);
    }

    public async Task<MyBatisScenario> DuplicateAsync(long id, string newName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newName);
        var copy = await RunAsync(true, async c =>
        {
            await using var tx = c.BeginTransaction(deferred: false);
            var origin = await FindAsync(c, id, ct, tx).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"시나리오 {id} 를 찾을 수 없다");
            await using (var shift = c.CreateCommand())
            {
                shift.Transaction = tx;
                shift.CommandText = "UPDATE mybatis_scenario SET sort_order = sort_order + 1 "
                    + "WHERE mapper_path = $path AND statement_id = $sid AND sort_order > $order";
                shift.Parameters.AddWithValue("$path", origin.MapperPath);
                shift.Parameters.AddWithValue("$sid", origin.StatementId);
                shift.Parameters.AddWithValue("$order", origin.SortOrder);
                await shift.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO mybatis_scenario(mapper_path, namespace, statement_id, name, values_json, sort_order, created_at, updated_at)
                SELECT mapper_path, namespace, statement_id, $name, values_json, sort_order + 1, $now, $now
                  FROM mybatis_scenario WHERE id = $id;
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$name", newName);
            insert.Parameters.AddWithValue("$now", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
            insert.Parameters.AddWithValue("$id", id);
            var newId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var row = (await QueryAsync(c, "id = $id", tx, [("$id", newId)], ct).ConfigureAwait(false))[0];
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return row;
        }, null, ct).ConfigureAwait(false);
        if (copy is null)
        {
            throw new InvalidOperationException("읽기 전용 상태라 시나리오를 복제할 수 없다: " + Notice);
        }

        Notify(copy);
        return copy;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var target = await RunAsync(true, async c =>
        {
            var row = await FindAsync(c, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM mybatis_scenario WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return row;
        }, null, ct).ConfigureAwait(false);
        Notify(target);
    }

    public async Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        if (orderedIds.Count == 0)
        {
            return;
        }

        var target = await RunAsync(true, async c =>
        {
            await using var tx = c.BeginTransaction(deferred: false);
            var first = await FindAsync(c, orderedIds[0], ct, tx).ConfigureAwait(false);
            if (first is null)
            {
                return null;
            }

            // 빠졌거나 다른 문장의 id 가 섞여도 순서가 겹치지 않게, 넘긴 id 뒤에 나머지 행을 이어 붙인다.
            var current = await QueryAsync(c, "mapper_path = $path AND statement_id = $sid", tx,
                [("$path", first.MapperPath), ("$sid", first.StatementId)], ct).ConfigureAwait(false);
            var inStatement = current.Select(r => r.Id).ToHashSet();
            var finalOrder = orderedIds.Where(inStatement.Contains).Distinct().ToList();
            finalOrder.AddRange(current.Select(r => r.Id).Where(i => !finalOrder.Contains(i)));
            for (var i = 0; i < finalOrder.Count; i++)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE mybatis_scenario SET sort_order = $order WHERE id = $id";
                cmd.Parameters.AddWithValue("$order", i);
                cmd.Parameters.AddWithValue("$id", finalOrder[i]);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return first;
        }, null, ct).ConfigureAwait(false);
        Notify(target);
    }

    public async Task<int> MoveStatementAsync(string mapperPath, string oldId, string newId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(oldId);
        ArgumentNullException.ThrowIfNull(newId);
        if (string.Equals(oldId, newId, StringComparison.Ordinal))
        {
            return 0;
        }

        var path = NormalizePath(mapperPath);
        var moved = await RunAsync(true, async c =>
        {
            await using var tx = c.BeginTransaction(deferred: false);

            // 새 id 에 이미 시나리오가 있으면 그 뒤로 이어 붙인다(순서 겹침 방지). 옮기는 도중 최댓값이 변하지 않게 먼저 읽는다.
            long offset;
            await using (var max = c.CreateCommand())
            {
                max.Transaction = tx;
                max.CommandText = "SELECT COALESCE(MAX(sort_order) + 1, 0) FROM mybatis_scenario WHERE mapper_path = $path AND statement_id = $new";
                max.Parameters.AddWithValue("$path", path);
                max.Parameters.AddWithValue("$new", newId);
                offset = Convert.ToInt64(await max.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE mybatis_scenario
                   SET statement_id = $new, sort_order = sort_order + $offset, updated_at = $now
                 WHERE mapper_path = $path AND statement_id = $old
                """;
            cmd.Parameters.AddWithValue("$new", newId);
            cmd.Parameters.AddWithValue("$old", oldId);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$offset", offset);
            cmd.Parameters.AddWithValue("$now", OrandaDatabase.ToText(DateTimeOffset.UtcNow));
            var count = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return count;
        }, 0, ct).ConfigureAwait(false);
        if (moved > 0)
        {
            RaiseChanged(path, oldId);
            RaiseChanged(path, newId);
        }

        return moved;
    }

    private static IAppDataPaths Require(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths;
    }

    private static Dictionary<string, MyBatisScenarioValue> DropSecrets(IReadOnlyDictionary<string, MyBatisScenarioValue> values)
    {
        var result = new Dictionary<string, MyBatisScenarioValue>(values.Count, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            result[name] = SensitiveBindNames.IsSensitive(name) ? MyBatisScenarioValue.Null : value;
        }

        return result;
    }

    private static string SerializeValues(IReadOnlyDictionary<string, MyBatisScenarioValue> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values)
            {
                writer.WriteStartObject(name);
                writer.WriteString("k", value.Kind.ToString());
                if (value.Text is not null)
                {
                    writer.WriteString("v", value.Text);
                }

                if (value.Items is not null)
                {
                    writer.WriteStartArray("i");
                    foreach (var item in value.Items)
                    {
                        writer.WriteStringValue(item);
                    }

                    writer.WriteEndArray();
                    writer.WriteString("ik", value.ItemKind.ToString());
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Dictionary<string, MyBatisScenarioValue> DeserializeValues(string json)
    {
        var result = new Dictionary<string, MyBatisScenarioValue>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var element = property.Value;
                var kind = ParseKind(element, "k", MyBatisScenarioValueKind.Null);
                var text = element.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                List<string>? items = null;
                if (element.TryGetProperty("i", out var i) && i.ValueKind == JsonValueKind.Array)
                {
                    items = i.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
                }

                result[property.Name] = new MyBatisScenarioValue(kind, text, items, ParseKind(element, "ik", MyBatisScenarioValueKind.Text));
            }
        }
        catch (JsonException)
        {
            // 깨진 값 묶음은 빈 묶음으로 읽는다(시나리오 이름·순서는 살린다).
        }

        return result;
    }

    private static MyBatisScenarioValueKind ParseKind(JsonElement element, string property, MyBatisScenarioValueKind fallback)
        => element.TryGetProperty(property, out var p)
            && Enum.TryParse<MyBatisScenarioValueKind>(p.GetString(), out var kind) ? kind : fallback;

    private static async Task<List<MyBatisScenario>> QueryAsync(
        SqliteConnection c, string where, SqliteTransaction? tx, (string Name, object Value)[] args, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM mybatis_scenario WHERE {where} ORDER BY sort_order, id";
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var list = new List<MyBatisScenario>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MyBatisScenario
            {
                Id = reader.GetInt64(0),
                MapperPath = reader.GetString(1),
                Namespace = reader.IsDBNull(2) ? null : reader.GetString(2),
                StatementId = reader.GetString(3),
                Name = reader.GetString(4),
                Values = DeserializeValues(reader.GetString(5)),
                SortOrder = reader.GetInt32(6),
                CreatedAt = OrandaDatabase.FromText(reader.GetString(7)) ?? default,
                UpdatedAt = OrandaDatabase.FromText(reader.GetString(8)) ?? default,
            });
        }

        return list;
    }

    private static async Task<MyBatisScenario?> FindAsync(SqliteConnection c, long id, CancellationToken ct, SqliteTransaction? tx = null)
    {
        var rows = await QueryAsync(c, "id = $id", tx, [("$id", id)], ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private void Notify(MyBatisScenario? target)
    {
        if (target is not null)
        {
            RaiseChanged(target.MapperPath, target.StatementId);
        }
    }

    private void RaiseChanged(string mapperPath, string statementId)
        => Changed?.Invoke(this, new MyBatisScenarioChangedEventArgs(mapperPath, statementId));

    /// <summary>
    /// 연결을 열어 <paramref name="action"/> 을 돌린다. 쓰기는 <see cref="Task.Run(Func{Task})"/> 로 백그라운드 스레드에서 한다.
    /// 읽기 전용이면 쓰기는 <paramref name="fallback"/> 을 돌려준다.
    /// </summary>
    private async Task<T> RunAsync<T>(bool write, Func<SqliteConnection, Task<T>> action, T fallback, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connectionString is null)
            {
                await InitializeAsync(ct).ConfigureAwait(false);
            }

            if (write && !_writable)
            {
                return fallback;
            }

            return await Task.Run(async () =>
            {
                await using var connection = await OrandaDatabase.OpenConnectionAsync(_connectionString!, ct).ConfigureAwait(false);
                return await action(connection).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var result = await OrandaDatabase.PrepareFileAsync(_dbFile, _backupDirectory, ct).ConfigureAwait(false);
        _writable = result.Writable;
        Notice = result.Notice;
        _connectionString = OrandaDatabase.FileConnectionString(_dbFile);
    }
}
