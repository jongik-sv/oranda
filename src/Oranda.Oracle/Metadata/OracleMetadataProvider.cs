using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>
/// 메타데이터 제공자 본체(스키마·객체·테이블·프로시저 인자, dba_/all_ 자동 선택). DDL·상세는 .Details.cs(TSK-07-03), 소스·오류는 .Source.cs(TSK-08-01). 소유: TSK-04-04.
/// <para>
/// 쿼리는 카탈로그 <c>meta.*</c> 에서 꺼낸다. 본문의 <c>@PFX@</c> 를 <c>CanQueryDictionary</c> 면 <c>dba</c>, 아니면 <c>all</c> 로 바꾼다
/// (user_ 뷰는 쓰지 않는다: all_ 뷰가 자기 객체를 모두 포함하고 owner 열 처리가 같아진다).
/// </para>
/// <para>
/// 객체 목록은 메모리가 아닌 <see cref="IMetadataCache"/>(SQLite) 에 서버 전체 목록으로 둔다. 캐시가 있으면 서버를 읽지 않고 답하고,
/// 뒤에서 <see cref="RefreshAsync"/> 가 <c>last_ddl_time</c>·상태 변경을 감지해 <see cref="MetadataInvalidated"/> 를 낸다.
/// </para>
/// </summary>
[OrandaService(typeof(IMetadataProvider))]
internal sealed partial class OracleMetadataProvider : IMetadataProvider
{
    /// <summary>
    /// payload_json 형식 버전(맨 위 <c>"v"</c>). 다르면 캐시가 없는 것으로 본다.
    /// 2: 외래키 제약에 참조 키 이름(<see cref="ConstraintInfo.ReferencedConstraint"/>)을 더했다.
    /// </summary>
    internal const int PayloadVersion = 2;

    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ConditionalWeakTable<OracleSession, SemaphoreSlim> Gates = new();

    private readonly IMetadataCache _cache;
    private readonly IEventBus _events;
    private readonly TimeSpan _backgroundRefreshInterval;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRefresh = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TimeSpan> _refreshCost = new(StringComparer.Ordinal);

    public OracleMetadataProvider(IMetadataCache cache, IEventBus events)
        : this(cache, events, TimeSpan.FromSeconds(60))
    {
    }

    /// <param name="backgroundRefreshInterval">캐시로 답한 뒤 백그라운드 갱신을 다시 시작하는 최소 간격. <see cref="Timeout.InfiniteTimeSpan"/> = 하지 않음(시험용).</param>
    internal OracleMetadataProvider(IMetadataCache cache, IEventBus events, TimeSpan backgroundRefreshInterval)
    {
        _cache = cache;
        _events = events;
        _backgroundRefreshInterval = backgroundRefreshInterval;
    }

    public async Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct)
    {
        var rows = await QueryAsync(s, "meta.schemas", _ => { }, ct);
        return [.. rows.Select(r => Str(r[0])!)];
    }

    public async Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        // 생성 시각은 캐시에 없다. 이 조건만 서버에 직접 묻는다.
        // INVALID 상태도 서버에 묻는다: 캐시의 상태는 마지막 갱신 때 값이라, 표를 지우거나 열을 바꿔 새로 INVALID 가 된 객체를 놓친다
        // (캐시로 답하면 「INVALID 만」 거르기가 비어 나온다). 서버 질의는 소유자·유형으로 좁혀 보내므로 작다.
        if (q.CreatedAfter is not null || q.InvalidOnly)
        {
            return ToRefs(await FetchObjectsAsync(s, q, ct));
        }

        var source = await TryCacheAsync(() => _cache.GetSourceAsync(s.DbKey, ct));
        if (source?.RefreshedAt is not null)
        {
            var cached = await TryCacheAsync(() => _cache.GetObjectsAsync(s.DbKey, q.Owner, null, ct));
            if (cached is not null)
            {
                StartBackgroundRefresh(s);
                return ToRefs(FilterCached(cached, q));
            }
        }

        // 캐시 없음: 전체 목록을 한 번 받아 캐시에 채운 뒤 거기서 답한다.
        var all = await RefreshAsync(s, ct);
        return ToRefs(FilterCached(all, q));
    }

    /// <summary>
    /// 서버의 전체 객체 목록을 받아 캐시와 비교한다. 새로 생기거나 <c>last_ddl_time</c>·상태가 바뀐 것은 상세(payload)를 비우고,
    /// 사라진 것은 지운다. 이전 갱신이 있었고 바뀐 것이 있으면 <see cref="MetadataInvalidated"/> 를 낸다.
    /// </summary>
    /// <returns>갱신 후 캐시에 있는 전체 목록(캐시를 못 쓰면 서버에서 받은 목록).</returns>
    internal async Task<IReadOnlyList<MetaObject>> RefreshAsync(ISession s, CancellationToken ct)
    {
        var dbKey = s.DbKey;
        var gate = _refreshGates.GetOrAdd(dbKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var fetchedAt = DateTimeOffset.UtcNow;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var rows = await FetchObjectsAsync(s, new ObjectQuery(), ct);
            _refreshCost[dbKey] = timer.Elapsed;
            var fresh = rows.Select(r => ToMeta(dbKey, r, fetchedAt)).ToList();

            var previousSource = await TryCacheAsync(() => _cache.GetSourceAsync(dbKey, ct));
            var cached = previousSource is null
                ? []
                : await TryCacheAsync(() => _cache.GetObjectsAsync(dbKey, null, null, ct)) ?? [];
            var cachedByKey = cached.ToDictionary(o => (o.Owner, o.Name, o.Type));
            var freshKeys = fresh.Select(o => (o.Owner, o.Name, o.Type)).ToHashSet();

            var vanished = cached.Where(o => !freshKeys.Contains((o.Owner, o.Name, o.Type))).ToList();
            var changed = new List<MetaObject>();
            var added = new List<MetaObject>();
            foreach (var o in fresh)
            {
                if (!cachedByKey.TryGetValue((o.Owner, o.Name, o.Type), out var old))
                {
                    added.Add(o);
                }
                else if (old.LastDdlTime != o.LastDdlTime || !string.Equals(old.Status, o.Status, StringComparison.Ordinal))
                {
                    changed.Add(o);
                }
            }

            var source = new MetaSource
            {
                DbKey = dbKey,
                ServerVersion = s.Server.Version,
                RefreshedAt = fetchedAt,
                LastUsedAt = fetchedAt,
            };
            var stored = await TryCacheAsync(async () =>
            {
                if (vanished.Count > 0)
                {
                    await _cache.InvalidateAsync(dbKey, [.. vanished.Select(ToRef)], ct);
                }

                await _cache.UpsertAsync(source, [.. added, .. changed], ct);
                return true;
            });

            _lastRefresh[dbKey] = DateTimeOffset.UtcNow; // 읽기를 마친 때부터 간격을 센다

            if (previousSource?.RefreshedAt is not null && (vanished.Count + changed.Count + added.Count) > 0)
            {
                _events.Publish(new MetadataInvalidated(dbKey, [.. vanished.Concat(changed).Concat(added).Select(ToRef)]));
            }

            if (!stored)
            {
                return fresh;
            }

            // 바뀌지 않은 항목의 payload 를 보존하려고 캐시 값을 다시 합친다.
            var result = fresh.Select(o => cachedByKey.TryGetValue((o.Owner, o.Name, o.Type), out var old)
                && old.LastDdlTime == o.LastDdlTime && string.Equals(old.Status, o.Status, StringComparison.Ordinal)
                    ? old
                    : o);
            return [.. result];
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);

        var cachedRow = await FindCachedAsync(s, table, ct);
        if (cachedRow?.PayloadJson is { } json && TryReadPayload(json, "table", out TableInfo? hit))
        {
            return hit!;
        }

        void BindTable(OracleCommand c)
        {
            c.Parameters.Add("owner", table.Owner);
            c.Parameters.Add("name", table.Name);
        }

        var colRows = await QueryAsync(s, "meta.columns", BindTable, ct);
        if (colRows.Count == 0)
        {
            throw new InvalidOperationException($"테이블 또는 뷰를 찾을 수 없습니다: {table.Owner}.{table.Name}");
        }

        var consRows = await QueryAsync(s, "meta.constraints", BindTable, ct);
        var idxRows = await QueryAsync(s, "meta.indexes", BindTable, ct);
        var commentRows = await QueryAsync(s, "meta.table_comment", BindTable, ct);

        var info = new TableInfo(table, [.. colRows.Select(ToColumn)])
        {
            Constraints = BuildConstraints(consRows),
            Indexes = BuildIndexes(idxRows),
            Comment = commentRows.Count > 0 ? Str(commentRows[0][0]) : null,
        };

        await StorePayloadAsync(s, cachedRow, "table", info, ct);
        return info;
    }

    public async Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        // 패키지는 명세(PACKAGE)로 본다. 본문 참조가 와도 같은 멤버를 돌려준다.
        var normalized = string.Equals(obj.ObjectType, "PACKAGE BODY", StringComparison.OrdinalIgnoreCase)
            ? obj with { ObjectType = "PACKAGE" }
            : obj;

        var cachedRow = await FindCachedAsync(s, normalized, ct);
        if (cachedRow?.PayloadJson is { } json && TryReadPayload(json, "procs", out List<ProcedureInfo>? hit))
        {
            return hit!;
        }

        void Bind(OracleCommand c)
        {
            c.Parameters.Add("owner", normalized.Owner);
            c.Parameters.Add("name", normalized.Name);
        }

        var members = await QueryAsync(s, "meta.procedures", Bind, ct);
        var args = await QueryAsync(s, "meta.arguments", Bind, ct);

        var list = BuildProcedures(normalized, members, args);
        await StorePayloadAsync(s, cachedRow, "procs", list, ct);
        return list;
    }

    // ---- 카탈로그 쿼리 실행 ----

    private static OracleSession AsOracle(ISession s)
        => s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));

    private static string ViewPrefix(ISession s) => s.Server.Capabilities.CanQueryDictionary ? "dba" : "all";

    internal static async Task<List<object?[]>> QueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        var session = AsOracle(s);
        var query = Catalog.Value.Get(id, s.Server);
        var sql = query.Sql.Replace("@PFX@", ViewPrefix(s), StringComparison.Ordinal);

        var gate = Gates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLONGFetchSize = -1;
            cmd.CommandText = sql;
            bind(cmd);

            var rows = new List<object?[]>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Task<List<object?[]>> FetchObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        => QueryAsync(s, "meta.objects", c =>
        {
            c.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = (object?)q.Owner ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("types", OracleDbType.Varchar2)
            {
                Value = q.ObjectTypes.Count == 0 ? DBNull.Value : "|" + string.Join('|', q.ObjectTypes) + "|",
            });
            c.Parameters.Add(new OracleParameter("name_pattern", OracleDbType.Varchar2) { Value = (object?)q.NamePattern ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("invalid_only", OracleDbType.Int32) { Value = q.InvalidOnly ? 1 : 0 });
            c.Parameters.Add(new OracleParameter("created_after", OracleDbType.Date)
            {
                Value = q.CreatedAfter is { } ca ? ca.UtcDateTime : DBNull.Value,
            });
            c.Parameters.Add(new OracleParameter("modified_after", OracleDbType.Date)
            {
                Value = q.ModifiedAfter is { } ma ? ma.UtcDateTime : DBNull.Value,
            });
            c.Parameters.Add(new OracleParameter("max_rows", OracleDbType.Int32) { Value = q.Limit ?? int.MaxValue });
        }, ct);

    // ---- 캐시 보조 ----

    /// <summary>캐시 오류는 기능을 막지 않는다(캐시는 언제든 다시 만들 수 있다). 취소는 그대로 전파한다.</summary>
    private static async Task<T?> TryCacheAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    private void StartBackgroundRefresh(ISession s)
    {
        if (_backgroundRefreshInterval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // 전체 목록 읽기가 오래 걸리는 서버(11g 비 DBA 의 all_objects 는 분 단위)는 그만큼 드물게 갱신한다:
        // 읽는 동안 같은 접속의 다른 질의(INVALID 거르기 포함)가 그 뒤에 줄을 선다. 읽는 데 걸린 시간의 30 배 간격, 최소는 기본 간격.
        var interval = _refreshCost.TryGetValue(s.DbKey, out var cost) && cost * 30 > _backgroundRefreshInterval ? cost * 30 : _backgroundRefreshInterval;
        if (_lastRefresh.TryGetValue(s.DbKey, out var last) && now - last < interval)
        {
            return;
        }

        _lastRefresh[s.DbKey] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(s, CancellationToken.None);
            }
            catch (Exception ex) when (ex is OracleException or InvalidOperationException or ObjectDisposedException)
            {
                // 세션이 닫혔거나 끊겼다. 다음 조회 때 다시 시도한다.
            }
        });
    }

    private async Task<MetaObject?> FindCachedAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        var found = await TryCacheAsync(() => _cache.FindByPrefixAsync(s.DbKey, obj.Name, 500, ct));
        return found?.FirstOrDefault(o =>
            string.Equals(o.Owner, obj.Owner, StringComparison.Ordinal)
            && string.Equals(o.Name, obj.Name, StringComparison.Ordinal)
            && (string.IsNullOrEmpty(obj.ObjectType) || string.Equals(o.Type, obj.ObjectType, StringComparison.Ordinal)));
    }

    private async Task StorePayloadAsync<T>(ISession s, MetaObject? row, string field, T value, CancellationToken ct)
    {
        if (row is null)
        {
            return; // 목록에 없는 객체는 캐시 행이 없으므로 상세만 저장하지 않는다.
        }

        var payload = new Dictionary<string, object> { ["v"] = PayloadVersion, [field] = value! };
        var source = new MetaSource
        {
            DbKey = s.DbKey,
            ServerVersion = s.Server.Version,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
        await TryCacheAsync(async () =>
        {
            await _cache.UpsertAsync(source, [row with { PayloadJson = JsonSerializer.Serialize(payload, Json) }], ct);
            return true;
        });
    }

    private static bool TryReadPayload<T>(string json, string field, out T? value)
    {
        value = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != PayloadVersion
                || !doc.RootElement.TryGetProperty(field, out var element))
            {
                return false;
            }

            value = element.Deserialize<T>(Json);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<MetaObject> FilterCached(IEnumerable<MetaObject> cached, ObjectQuery q)
    {
        var types = q.ObjectTypes.Count == 0 ? null : new HashSet<string>(q.ObjectTypes, StringComparer.Ordinal);
        var pattern = q.NamePattern is null ? null : LikeToRegex(q.NamePattern);
        var result = cached.Where(o =>
            (q.Owner is null || string.Equals(o.Owner, q.Owner, StringComparison.Ordinal))
            && (types is null || types.Contains(o.Type))
            && (pattern is null || pattern.IsMatch(o.Name))
            && (q.ModifiedAfter is null || o.LastDdlTime > q.ModifiedAfter))
            .OrderBy(o => o.Owner, StringComparer.Ordinal)
            .ThenBy(o => o.Type, StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal);
        return q.Limit is { } limit ? result.Take(limit) : result;
    }

    /// <summary>SQL LIKE(<c>%</c>, <c>_</c>, 이스케이프 <c>\</c>) 를 정규식으로 옮긴다. 서버 쿼리와 같은 뜻(대소문자 구분)이다.</summary>
    internal static System.Text.RegularExpressions.Regex LikeToRegex(string like)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < like.Length; i++)
        {
            var ch = like[i];
            if (ch == '\\' && i + 1 < like.Length)
            {
                sb.Append(System.Text.RegularExpressions.Regex.Escape(like[++i].ToString()));
            }
            else if (ch == '%')
            {
                sb.Append("(?s:.*)");
            }
            else if (ch == '_')
            {
                sb.Append("(?s:.)");
            }
            else
            {
                sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
            }
        }

        return new System.Text.RegularExpressions.Regex(sb.Append('$').ToString(), System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static List<DbObjectRef> ToRefs(IEnumerable<MetaObject> objects) => [.. objects.Select(ToRef)];

    private static List<DbObjectRef> ToRefs(List<object?[]> rows)
        => [.. rows.Select(r => new DbObjectRef(Str(r[0])!, Str(r[1])!, Str(r[2])!))];

    private static DbObjectRef ToRef(MetaObject o) => new(o.Owner, o.Name, o.Type);

    private static MetaObject ToMeta(string dbKey, object?[] r, DateTimeOffset fetchedAt) => new()
    {
        DbKey = dbKey,
        Owner = Str(r[0])!,
        Name = Str(r[1])!,
        Type = Str(r[2])!,
        Status = Str(r[3]),
        LastDdlTime = r[4] is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null,
        FetchedAt = fetchedAt,
    };

    // ---- 행 → 모델 ----

    private static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static bool Yes(object? v) => string.Equals(Str(v), "YES", StringComparison.Ordinal);

    private static ColumnInfo ToColumn(object?[] r) => new(Str(r[0])!, Int(r[1]) ?? 0, Str(r[2])!)
    {
        Length = Int(r[3]),
        Precision = Int(r[4]),
        Scale = Int(r[5]),
        IsNullable = !string.Equals(Str(r[6]), "N", StringComparison.Ordinal),
        DefaultValue = Str(r[7])?.Trim(),
        IsVirtual = Yes(r[8]),
        IsIdentity = Yes(r[9]),
        IsInvisible = Yes(r[10]),
        Domain = Str(r[11]),
        Comment = Str(r[12]),
    };

    /// <summary>
    /// 제약 행 → 제약 목록. NOT NULL 검사 제약은 열 정보에 있으므로 뺀다. 19 이상은 SQL 이 이미 거르고,
    /// 11g 는 조건 열이 LONG 이라 SQL 에서 거르지 못해 여기서 뺀다(meta/constraints.11.sql).
    /// </summary>
    internal static List<ConstraintInfo> BuildConstraints(List<object?[]> rows)
    {
        var result = new List<ConstraintInfo>();
        foreach (var group in rows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal))
        {
            var first = group.First();
            var kind = Str(first[1]) switch
            {
                "P" => ConstraintKind.PrimaryKey,
                "U" => ConstraintKind.Unique,
                "R" => ConstraintKind.ForeignKey,
                _ => ConstraintKind.Check,
            };
            if (kind == ConstraintKind.Check && IsNotNullCheck(Str(first[3])))
            {
                continue;
            }

            var columns = group.Where(r => r[6] is not null).Select(r => Str(r[6])!).ToList();
            result.Add(new ConstraintInfo(group.Key, kind, columns)
            {
                IsEnabled = string.Equals(Str(first[2]), "ENABLED", StringComparison.Ordinal),
                Condition = kind == ConstraintKind.Check ? Str(first[3]) : null,
                ReferencedTable = kind == ConstraintKind.ForeignKey && first[4] is not null && first[5] is not null
                    ? new DbObjectRef(Str(first[4])!, Str(first[5])!, "TABLE")
                    : null,
                ReferencedConstraint = kind == ConstraintKind.ForeignKey && first.Length > 8 ? Str(first[8]) : null,
            });
        }

        return result;
    }

    /// <summary>SQL 의 <c>search_condition_vc LIKE '%IS NOT NULL'</c> 과 같은 판정(대소문자 구분, 끝 일치).</summary>
    internal static bool IsNotNullCheck(string? condition)
        => condition is not null && condition.EndsWith("IS NOT NULL", StringComparison.Ordinal);

    private static List<IndexInfo> BuildIndexes(List<object?[]> rows)
        => [.. rows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal).Select(g => new IndexInfo(
            g.Key,
            string.Equals(Str(g.First()[1]), "UNIQUE", StringComparison.Ordinal),
            [.. g.Select(r => Str(r[3])!)])
        {
            IndexType = Str(g.First()[2]),
        })];

    private static List<ProcedureInfo> BuildProcedures(DbObjectRef obj, List<object?[]> members, List<object?[]> args)
    {
        // 인자를 (프로시저 이름, 오버로드) 로 묶는다.
        var argGroups = args
            .GroupBy(r => (Name: Str(r[0])!, Overload: Str(r[1])))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ProcedureInfo>();
        var isPackage = string.Equals(obj.ObjectType, "PACKAGE", StringComparison.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            var name = Str(m[0]) ?? obj.Name;
            var overload = Str(m[1]);
            argGroups.TryGetValue((name, overload), out var rows);
            var info = BuildProcedure(name, overload, rows ?? []);
            if (isPackage || info is not null)
            {
                result.Add(info ?? new ProcedureInfo(name, false, []));
            }
        }

        if (members.Count == 0 && !isPackage)
        {
            // 단독 프로시저·함수인데 all_procedures 에 행이 없는 경우(권한)에도 인자 행이 있으면 답한다.
            foreach (var g in argGroups.OrderBy(g => g.Key.Name, StringComparer.Ordinal))
            {
                if (BuildProcedure(g.Key.Name, g.Key.Overload, g.Value) is { } info)
                {
                    result.Add(info);
                }
            }
        }

        return result;
    }

    private static ProcedureInfo? BuildProcedure(string name, string? overload, List<object?[]> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var ret = rows.FirstOrDefault(r => Int(r[2]) == 0 && r[3] is null);
        var arguments = rows
            .Where(r => r[3] is not null && r[5] is not null)
            .Select(r => new ArgumentInfo(Str(r[3])!, Int(r[2]) ?? 0, Str(r[5])!, Str(r[4]) switch
            {
                "OUT" => ArgumentDirection.Out,
                "IN/OUT" => ArgumentDirection.InOut,
                _ => ArgumentDirection.In,
            })
            {
                HasDefault = string.Equals(Str(r[6]), "Y", StringComparison.Ordinal),
                TypeName = r[8] is null ? null : string.Join('.', new[] { Str(r[7]), Str(r[8]), Str(r[9]) }.Where(x => x is not null)),
            })
            .ToList();
        return new ProcedureInfo(name, ret is not null, arguments)
        {
            Overload = int.TryParse(overload, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null,
            ReturnType = ret is null ? null : Str(ret[5]),
        };
    }
}
