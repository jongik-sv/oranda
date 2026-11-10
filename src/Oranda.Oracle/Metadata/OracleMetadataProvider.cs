using System.Globalization;
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
/// 객체 목록 캐시 흐름(<see cref="CachedMetadataProvider.ListObjectsAsync"/>·<see cref="CachedMetadataProvider.FindObjectsByPrefixAsync"/>·갱신·payload)은
/// Core <see cref="CachedMetadataProvider"/> 에 있다. 여기서는 서버 목록 읽기(<c>meta.objects</c>)와 백그라운드 갱신이 삼킬 실패만 정한다.
/// </para>
/// </summary>
[OrandaService(typeof(IMetadataProvider), Dbms = Dbms.Oracle)]
internal sealed partial class OracleMetadataProvider : CachedMetadataProvider, IMetadataProvider
{
    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    public OracleMetadataProvider(IMetadataCache cache, IEventBus events)
        : this(cache, events, DefaultBackgroundRefreshInterval)
    {
    }

    /// <param name="backgroundRefreshInterval">캐시로 답한 뒤 백그라운드 갱신을 다시 시작하는 최소 간격. <see cref="Timeout.InfiniteTimeSpan"/> = 하지 않음(시험용).</param>
    internal OracleMetadataProvider(IMetadataCache cache, IEventBus events, TimeSpan backgroundRefreshInterval)
        : base(cache, events, backgroundRefreshInterval)
    {
    }

    public async Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct)
    {
        var rows = await QueryAsync(s, "meta.schemas", _ => { }, ct);
        return [.. rows.Select(r => Str(r[0])!)];
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

    public async Task<IReadOnlyDictionary<string, string>> ListTableCommentsAsync(ISession s, string owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(owner);
        var rows = await QueryAsync(s, "meta.table_comments", c => c.Parameters.Add("owner", owner), ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (Str(r[0]) is { } name && Str(r[1]) is { Length: > 0 } comment)
            {
                result[name] = comment;
            }
        }

        return result;
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
        // 빈 목록은 믿지 않고 다시 읽는다: 인자 없는 단독 프로시저를 빈 목록으로 저장한 예전 캐시가 남아 있을 수 있다(TSK-12-02).
        if (cachedRow?.PayloadJson is { } json && TryReadPayload(json, "procs", out List<ProcedureInfo>? hit) && hit!.Count > 0)
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

    /// <param name="rewrite">본문의 호출 쪽 자리표(<c>@LONGNAME@</c> 등)를 바꾼다. 없으면 <c>@PFX@</c> 만 바꾼다.</param>
    internal static async Task<List<object?[]>> QueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct, Func<string, string>? rewrite = null)
    {
        var session = AsOracle(s);
        var query = Catalog.Value.Get(id, s.Server);
        var sql = query.Sql.Replace("@PFX@", ViewPrefix(s), StringComparison.Ordinal);
        if (rewrite is not null)
        {
            sql = rewrite(sql);
        }

        // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다. 결과 집합처럼 커서를 연 뒤에는 행 하나 읽는 동안만 쥔다:
        // 전체 목록 읽기(11g 비 DBA 의 all_objects 는 분 단위)가 그 사이 사용자의 실행을 끝까지 막지 않는다(서버 왕복 사이에 실행이 돈다).
        // 즉시성 범위(백그라운드 갱신·자동 완성)에서는 읽는 사이에 실행이 잠금을 잡으면 다음 행에서 바쁨으로 그만두고 실행에 양보한다.
        // 커서를 여는 첫 잠금은 지금 흐름의 한도(SessionWait.CurrentLimit)로, 행마다 다시 잡는 이어 잠금은 이어 읽기 한도(SessionWait.Resume)로 기다린다:
        // 뒤 갱신은 시작을 0 으로 두어 사용자 실행 뒤에 줄을 서지 않고(D-041), 시작한 읽기는 짧은 질의 하나에 끊기지 않는다.
        var gate = session.CommandGate;
        OracleCommand? cmd = null;
        OracleDataReader? reader = null;
        await gate.EnterAsync(ct);
        try
        {
            cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLONGFetchSize = -1;
            cmd.CommandText = sql;
            bind(cmd);
            reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            if (reader is null && cmd is not null)
            {
                await cmd.DisposeAsync();
            }

            gate.Release();
        }

        try
        {
            using var resume = SessionWait.Resume(); // 이미 연 커서의 이어 읽기
            var rows = new List<object?[]>();
            while (true)
            {
                await gate.EnterAsync(ct);
                try
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        return rows;
                    }

                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
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
        }
        finally
        {
            await gate.EnterForCleanupAsync(); // 커서 닫기도 같은 연결로 간다
            try
            {
                await reader.DisposeAsync();
                await cmd!.DisposeAsync();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // ---- 객체 목록 캐시(CachedMetadataProvider) ----

    /// <summary>
    /// 서버의 전체 객체 목록을 받아 캐시와 비교한다(<see cref="CachedMetadataProvider.RefreshObjectListAsync"/>). 시험이 직접 부른다.
    /// </summary>
    /// <returns>갱신 후 캐시에 있는 전체 목록(캐시를 못 쓰면 서버에서 받은 목록).</returns>
    internal Task<IReadOnlyList<MetaObject>> RefreshAsync(ISession s, CancellationToken ct) => RefreshObjectListAsync(s, ct);

    protected override async Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        var dbKey = s.DbKey;
        var rows = await QueryAsync(s, "meta.objects", c =>
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
        return [.. rows.Select(r => ToMeta(dbKey, r, fetchedAt))];
    }

    /// <summary>세션이 닫혔거나 끊겼거나 바쁘다(<see cref="SessionBusyException"/> 는 <see cref="InvalidOperationException"/>).</summary>
    protected override bool IsIgnorableBackgroundRefreshFailure(Exception ex)
        => ex is OracleException or InvalidOperationException or ObjectDisposedException;

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
        ByteLength = r.Length > 13 ? Int(r[13]) : null, // 문자 열의 data_length(바이트, TR1). 열이 없는 예전 행 모양이면 모름
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

    internal static List<ProcedureInfo> BuildProcedures(DbObjectRef obj, List<object?[]> members, List<object?[]> args)
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

            // 인자 행이 하나도 없는 프로시저(인자 없는 단독 프로시저 포함)도 호출할 수 있는 대상이다.
            var info = BuildProcedure(name, overload, rows ?? []);
            result.Add(info ?? new ProcedureInfo(name, false, [])
            {
                Overload = int.TryParse(overload, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null,
            });
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
