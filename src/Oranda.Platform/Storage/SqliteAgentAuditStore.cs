using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Composition;
using Oranda.Core.Security;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>
/// AI 도구 호출 감사 기록(<c>agent_audit</c> 표, 마이그레이션 0004). 인자 요약·사유는 저장 전에 한 번 더 비밀을 가리고 길이를 자른다.
/// 보관 기간(<see cref="MaxAgeDays"/>)과 최대 줄 수(<see cref="MaxRows"/>)를 넘은 기록은 첫 쓰기와 그 뒤 한 시간에 한 번 지운다.
/// 파일을 쓸 수 없으면(더 새로운 버전 등) 쓰기는 건너뛰고 <see cref="Notice"/> 에 이유를 둔다.
/// </summary>
[OrandaService(typeof(IAgentAuditStore))]
internal sealed partial class SqliteAgentAuditStore : IAgentAuditStore, IDisposable
{
    internal const int MaxAgeDays = 90;
    internal const int MaxRows = 50_000;
    internal const int MaxArgumentChars = 2000;
    internal const int MaxReasonChars = 500;
    private const int MaxNameChars = 200;
    private const int MaxListLimit = 1000;

    private static readonly JsonSerializerOptions JsonOutput = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly string _dbFile;
    private readonly string _backupDirectory;
    private readonly ISecretRedactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private bool _writable;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public SqliteAgentAuditStore(IAppDataPaths paths, ISecretRedactor redactor)
        : this(Require(paths).DatabaseFile, paths.BackupDirectory, redactor)
    {
    }

    private SqliteAgentAuditStore(string dbFile, string backupDirectory, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _dbFile = dbFile;
        _backupDirectory = backupDirectory;
        _redactor = redactor;
    }

    /// <summary>시험·도구용: 지정한 oranda.db 파일을 쓴다.</summary>
    internal static SqliteAgentAuditStore ForFile(string dbFile, ISecretRedactor redactor)
        => new(dbFile, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbFile))!, "backup"), redactor);

    /// <summary>사용자에게 알릴 말(읽기 전용 이유). 정상이면 null.</summary>
    public string? Notice { get; private set; }

    public void Dispose() => _gate.Dispose();

    public async ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var arguments = entry.Arguments is null ? null : Limit(Scrub(entry.Arguments), MaxArgumentChars);
        var reason = entry.Reason is null ? null : Limit(Scrub(entry.Reason), MaxReasonChars);

        await RunAsync(true, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_audit(occurred_at, profile_id, profile_name, client, run_id, tool, arguments,
                                        access_level, verdict, reason, result_chars, elapsed_ms)
                VALUES ($at, $pid, $pname, $client, $run, $tool, $args, $level, $verdict, $reason, $chars, $ms);
                """;
            cmd.Parameters.AddWithValue("$at", OrandaDatabase.ToText(entry.OccurredAt == default ? DateTimeOffset.UtcNow : entry.OccurredAt));
            cmd.Parameters.AddWithValue("$pid", entry.ProfileId is { } id ? id.ToString("D") : DBNull.Value);
            cmd.Parameters.AddWithValue("$pname", (object?)Limit(entry.ProfileName, MaxNameChars) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$client", (object?)Limit(entry.Client, MaxNameChars) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$run", (object?)Limit(entry.RunId, MaxNameChars) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tool", Limit(entry.Tool, MaxNameChars) ?? string.Empty);
            cmd.Parameters.AddWithValue("$args", (object?)arguments ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$level", entry.AccessLevel.ToString());
            cmd.Parameters.AddWithValue("$verdict", entry.Verdict.ToString());
            cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$chars", (object?)entry.ResultChars ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ms", (object?)entry.ElapsedMs ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            if (DateTimeOffset.UtcNow - _lastPurge >= PurgeInterval)
            {
                try
                {
                    await PurgeAsync(c, DateTimeOffset.UtcNow.AddDays(-MaxAgeDays), ct);
                    _lastPurge = DateTimeOffset.UtcNow;
                }
                catch (Exception ex) when (ex is SqliteException or OperationCanceledException)
                {
                    // 정리 실패나 취소는 방금 쓴 기록을 잃게 하지 않는다. 다음 쓰기에서 다시 시도한다.
                }
            }

            return 0;
        }, 0, ct);
    }

    public async ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct)
    {
        var count = Math.Clamp(limit, 1, MaxListLimit);
        return await RunAsync<IReadOnlyList<AgentAuditEntry>>(false, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, occurred_at, profile_id, profile_name, client, run_id, tool, arguments, access_level, verdict, reason, result_chars, elapsed_ms "
                + "FROM agent_audit "
                + (profileId is null ? string.Empty : "WHERE profile_id = $pid ")
                + "ORDER BY occurred_at DESC, id DESC LIMIT $limit";
            if (profileId is { } id)
            {
                cmd.Parameters.AddWithValue("$pid", id.ToString("D"));
            }

            cmd.Parameters.AddWithValue("$limit", count);
            var list = new List<AgentAuditEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(Read(reader));
            }

            return list;
        }, [], ct);
    }

    /// <summary>시험용: <paramref name="cutoff"/> 보다 오래된 기록과 <see cref="MaxRows"/> 를 넘는 오래된 기록을 지운다.</summary>
    internal async Task PurgeAsync(DateTimeOffset cutoff, CancellationToken ct)
        => await RunAsync(true, async c =>
        {
            await PurgeAsync(c, cutoff, ct);
            return 0;
        }, 0, ct);

    private static async Task PurgeAsync(SqliteConnection c, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM agent_audit WHERE occurred_at < $cutoff;
            DELETE FROM agent_audit WHERE id IN
              (SELECT id FROM agent_audit ORDER BY occurred_at DESC, id DESC LIMIT -1 OFFSET $max);
            """;
        cmd.Parameters.AddWithValue("$cutoff", OrandaDatabase.ToText(cutoff));
        cmd.Parameters.AddWithValue("$max", MaxRows);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IAppDataPaths Require(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths;
    }

    private static AgentAuditEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        OccurredAt = OrandaDatabase.FromText(r.GetString(1)) ?? default,
        ProfileId = !r.IsDBNull(2) && Guid.TryParse(r.GetString(2), out var g) ? g : null,
        ProfileName = r.IsDBNull(3) ? null : r.GetString(3),
        Client = r.IsDBNull(4) ? null : r.GetString(4),
        RunId = r.IsDBNull(5) ? null : r.GetString(5),
        Tool = r.GetString(6),
        Arguments = r.IsDBNull(7) ? null : r.GetString(7),
        AccessLevel = Enum.TryParse<AiAccessLevel>(r.GetString(8), out var level) ? level : AiAccessLevel.Off,
        Verdict = Enum.TryParse<AiToolJudgement>(r.GetString(9), out var verdict) ? verdict : AiToolJudgement.Failed,
        Reason = r.IsDBNull(10) ? null : r.GetString(10),
        ResultChars = r.IsDBNull(11) ? null : r.GetInt32(11),
        ElapsedMs = r.IsDBNull(12) ? null : r.GetInt64(12),
    };

    /// <summary>
    /// 인자 요약이 JSON 이면 문자열 값마다 가리고 비밀 이름의 키는 값을 통째로 <c>***</c> 로 바꾼다(JSON 문자열 안의 SQL 은
    /// 공용 가림기가 따옴표 식별자로 보고 지나치므로). JSON 이 아니면 글 전체를 가린다.
    /// </summary>
    private string Scrub(string text)
    {
        var start = text.AsSpan().TrimStart();
        if (start.Length > 0 && start[0] is '{' or '[')
        {
            try
            {
                if (JsonNode.Parse(text) is { } node)
                {
                    return ScrubNode(node)?.ToJsonString(JsonOutput) ?? string.Empty;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // JSON 모양이 아니거나 노드를 다시 달 수 없으면 글로 가린다.
            }
        }

        return ScrubText(text);
    }

    private JsonNode? ScrubNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    var replaced = value is not null && SecretKey().IsMatch(key) ? JsonValue.Create("***") : ScrubNode(value);
                    if (!ReferenceEquals(replaced, value))
                    {
                        obj[key] = replaced;
                    }
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    var replaced = ScrubNode(item);
                    if (!ReferenceEquals(replaced, item))
                    {
                        array[i] = replaced;
                    }
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(ScrubText(text));
            default:
                return node;
        }
    }

    /// <summary>SQL 비밀(IDENTIFIED BY 등)은 공용 가림기로, 그 밖의 Bearer 토큰·<c>키=값</c>·<c>user/pass@</c> 는 정규식으로 가린다. Bearer 를 먼저 가려야 <c>Authorization: Bearer x</c> 의 토큰이 남지 않는다.</summary>
    private string ScrubText(string text)
    {
        var redacted = _redactor.Redact(text);
        redacted = BearerToken().Replace(redacted, "$1***");
        redacted = KeyValueSecret().Replace(redacted, "$1=***");
        return ConnectCredential().Replace(redacted, "$1/***@");
    }

    private static string? Limit(string? text, int maxChars)
        => text is null || text.Length <= maxChars ? text : text[..maxChars];

    [GeneratedRegex("""["']?\b([\w-]*(?:password|passwd|pwd|secret|api[_-]?key|token|authorization)[\w-]*)\b["']?\s*[:=]\s*("[^"]*"|'[^']*'|[^\s,;&}\]]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"^(?:[\w-]*[_-])?(?:password|passwd|pwd|secret|api[_-]?key|token|authorization)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKey();

    [GeneratedRegex(@"\b([A-Za-z][\w$#]*)/[^\s@/]+@", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectCredential();

    [GeneratedRegex(@"(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

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
