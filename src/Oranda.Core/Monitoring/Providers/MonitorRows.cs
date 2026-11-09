using System.Globalization;
using Oranda.Core.Plans;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Core.Monitoring.Providers;

/// <summary>
/// 모니터 질의 행 → 모니터 모델 조립(DBMS 무관). 공급자는 카탈로그 <c>session.*</c>·<c>lock.*</c>·<c>sql.*</c> 행을 읽어 이 함수에 넘긴다.
/// 열 순서는 카탈로그 계약(같은 id = 같은 열)이다.
/// </summary>
public static class MonitorRows
{
    /// <summary>델타(SESS-06)로 보는 누적 통계 이름. <c>session.list</c> 의 23~27 열 순서와 같다.</summary>
    public static readonly IReadOnlyList<string> DeltaStatNames =
    [
        "CPU used by this session",
        "session logical reads",
        "physical reads",
        "execute count",
        "redo size",
    ];

    /// <summary>잠긴 행 값 문자열 상한(긴 문자열·LOB 는 자른다).</summary>
    public const int LockedValueMaxLength = 4000;

    /// <summary>미리보기 글자 수 상한.</summary>
    public const int TopSqlPreviewLength = 200;

    /// <summary><see cref="LockFilter.Types"/> 가 비었을 때 보는 잠금 유형.</summary>
    private static readonly string[] DefaultLockTypes = ["TM", "TX", "UL"];

    /// <summary><c>session.list</c>·<c>session.detail</c> 공통 열 0~22.</summary>
    public static SessionRow ToSessionRow(object?[] r)
    {
        var key = new SessionKey(Int(r[0]) ?? 1, Long(r[1]) ?? 0, Long(r[2]) ?? 0);
        SessionKey? blocker = Long(r[19]) is { } bs ? new SessionKey(Int(r[18]) ?? key.InstanceId, bs, 0) : null;
        return new SessionRow(key, Str(r[3]) ?? string.Empty)
        {
            Username = Str(r[4]),
            OsUser = Str(r[5]),
            Machine = Str(r[6]),
            Program = Str(r[7]),
            Module = Str(r[8]),
            Action = Str(r[9]),
            ClientIdentifier = Str(r[10]),
            SqlId = Str(r[11]),
            PrevSqlId = Str(r[13]),
            Event = Str(r[14]),
            WaitClass = Str(r[15]),
            SecondsInWait = Long(r[16]),
            LastCallElapsedSeconds = Long(r[17]),
            BlockingSession = blocker,
            LogonTime = r[20] is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : null,
        };
    }

    public static BlockingRow ToBlockingRow(object?[] r)
    {
        var key = new SessionKey(Int(r[0]) ?? 1, Long(r[1]) ?? 0, Long(r[2]) ?? 0);
        SessionKey? blocker = Long(r[8]) is { } bs ? new SessionKey(Int(r[7]) ?? key.InstanceId, bs, 0) : null;
        return new BlockingRow(key, blocker)
        {
            Username = Str(r[3]),
            Event = Str(r[4]),
            SecondsInWait = Long(r[5]),
            SqlId = Str(r[6]),
            LockedObject = Str(r[11]),
            Status = r.Length > 12 ? Str(r[12]) : null,
        };
    }

    /// <summary>
    /// 블로킹 행 목록으로 트리를 만든다. 간선 = 직접 블로커(<c>blocking_session</c>). 뿌리 = 목록 안에 블로커가 없는 세션(최종 블로커).
    /// 순환(교착)이 있으면 순환 안의 가장 작은 키를 뿌리로 삼아 끊는다.
    /// </summary>
    public static BlockingTree BuildBlockingTree(IEnumerable<BlockingRow> source)
    {
        var rows = new Dictionary<SessionKey, BlockingRow>();
        var bySid = new Dictionary<(int, long), SessionKey>();
        foreach (var r in source)
        {
            if (rows.TryAdd(r.Key, r))
            {
                bySid.TryAdd((r.Key.InstanceId, r.Key.Sid), r.Key);
            }
        }

        // 직접 블로커를 목록 안의 키로 바꾼다(없거나 자기 자신이면 뿌리).
        SessionKey? ParentOf(BlockingRow r) =>
            r.Blocker is { } b && bySid.TryGetValue((b.InstanceId, b.Sid), out var p) && p != r.Key ? p : null;

        var children = new Dictionary<SessionKey, List<SessionKey>>();
        var rootKeys = new List<SessionKey>();
        foreach (var r in rows.Values)
        {
            if (ParentOf(r) is { } p)
            {
                if (!children.TryGetValue(p, out var list))
                {
                    children[p] = list = [];
                }

                list.Add(r.Key);
            }
            else
            {
                rootKeys.Add(r.Key);
            }
        }

        var placed = new HashSet<SessionKey>();
        var roots = new List<BlockingNode>();
        foreach (var key in Ordered(rootKeys))
        {
            roots.Add(Build(key));
        }

        // 순환(교착): 아직 놓이지 않은 세션이 남으면 그중 가장 작은 키를 뿌리로 삼는다.
        foreach (var key in Ordered(rows.Keys))
        {
            if (!placed.Contains(key))
            {
                roots.Add(Build(key));
            }
        }

        return new BlockingTree(roots);

        BlockingNode Build(SessionKey key)
        {
            placed.Add(key);
            var r = rows[key];
            var waiters = new List<BlockingNode>();
            if (children.TryGetValue(key, out var list))
            {
                foreach (var w in Ordered(list))
                {
                    if (!placed.Contains(w))
                    {
                        waiters.Add(Build(w));
                    }
                }
            }

            return new BlockingNode(key, waiters)
            {
                Username = r.Username,
                Event = r.Event,
                SecondsInWait = r.SecondsInWait,
                SqlId = r.SqlId,
                LockedObject = r.LockedObject,
                Status = r.Status,
            };
        }

        static List<SessionKey> Ordered(IEnumerable<SessionKey> keys) =>
            [.. keys.OrderBy(k => k.InstanceId).ThenBy(k => k.Sid).ThenBy(k => k.Serial)];
    }

    /// <summary>직전·이번 누적값 차이. 직전 값이 없거나 줄었으면(세션 재사용 등) 그 통계는 뺀다.</summary>
    public static IReadOnlyDictionary<string, long> ComputeDeltas(long?[] before, long?[] now)
    {
        var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < DeltaStatNames.Count && i < before.Length && i < now.Length; i++)
        {
            if (before[i] is { } b && now[i] is { } n && n >= b)
            {
                deltas[DeltaStatNames[i]] = n - b;
            }
        }

        return deltas;
    }

    /// <summary>오류 메시지의 첫 줄(상세 구획·잠긴 행 오류 표시용).</summary>
    public static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }

    /// <summary>열 <paramref name="at"/>·+1·+2 = inst_id, sid, serial#.</summary>
    public static SessionKey Key(object?[] r, int at) => new(Int(r[at]) ?? 1, Long(r[at + 1]) ?? 0, Long(r[at + 2]) ?? 0);

    public static DateTimeOffset? ServerDate(object? v) =>
        v is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : null;

    /// <summary><c>lock.list</c> 열 0~24(24 = status, 없으면 null).</summary>
    public static LockRow ToLockRow(object?[] r)
    {
        var key = Key(r, 0);
        var owner = Str(r[13]);
        var name = Str(r[14]);
        SessionKey? blocker = Long(r[22]) is { } bs ? new SessionKey(Int(r[21]) ?? key.InstanceId, bs, Long(r[23]) ?? 0) : null;
        return new LockRow(key, Str(r[6]) ?? string.Empty)
        {
            Username = Str(r[3]),
            Machine = Str(r[4]),
            Program = Str(r[5]),
            Id1 = Long(r[7]) ?? 0,
            Id2 = Long(r[8]) ?? 0,
            HeldMode = Int(r[9]) ?? 0,
            RequestedMode = Int(r[10]) ?? 0,
            CtimeSeconds = Long(r[11]) ?? 0,
            IsBlocking = (Int(r[12]) ?? 0) > 0,
            ObjectName = name is null ? null : owner is null ? name : owner + "." + name,
            TransactionStart = ServerDate(r[15]),
            UndoBlocks = Long(r[16]),
            Event = Str(r[17]),
            SecondsInWait = Long(r[18]),
            SqlId = Str(r[19]),
            PrevSqlId = Str(r[20]),
            BlockingSession = blocker,
            Status = r.Length > 24 ? Str(r[24]) : null,
        };
    }

    /// <summary><c>lock.idle_transactions</c> 열 0~11(11 = 잠금 수).</summary>
    public static IdleTransaction ToIdleTransaction(object?[] r) => new(Key(r, 0))
    {
        Username = Str(r[3]),
        Machine = Str(r[4]),
        Program = Str(r[5]),
        TransactionStart = ServerDate(r[6]),
        IdleSeconds = Long(r[7]) ?? 0,
        UndoBlocks = Long(r[8]),
        BlockedCount = Int(r[9]) ?? 0,
        PrevSqlId = Str(r[10]),
        LockCount = r.Length > 11 ? Int(r[11]) ?? 0 : 0,
    };

    /// <summary><c>lock.deadlocks</c> 열 0~2. 시각은 UTC, 메시지는 빈 줄을 뺀 줄 목록.</summary>
    public static DeadlockEvent ToDeadlockEvent(object?[] r)
    {
        var at = r[0] is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : DateTimeOffset.MinValue;
        var lines = (Str(r[2]) ?? string.Empty).ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new DeadlockEvent(at, Int(r[1]) ?? 1, lines);
    }

    /// <summary>잠금 유형 목록을 <c>,TM,TX,</c> 꼴 바인드로. 비면 기본 유형. 영문자·숫자가 아닌 글자는 버린다.</summary>
    public static string LockTypesBind(IReadOnlyList<string> types)
    {
        var picked = types
            .Select(t => new string([.. (t ?? string.Empty).Trim().ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        IEnumerable<string> list = picked.Count == 0 ? DefaultLockTypes : picked;
        return "," + string.Join(',', list) + ",";
    }

    /// <summary>열 값 하나를 화면용 문자열로. 날짜는 ISO, 이진은 16진(앞 64바이트), 긴 문자열은 <see cref="LockedValueMaxLength"/> 에서 자른다.</summary>
    public static string? FormatLockedValue(object? value)
    {
        var text = value switch
        {
            null or DBNull => null,
            DateTime dt => dt.ToString(dt.TimeOfDay.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
            byte[] bytes => "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 64)) + (bytes.Length > 64 ? $"… ({bytes.Length} bytes)" : string.Empty),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        return text is { Length: > LockedValueMaxLength } ? text[..LockedValueMaxLength] + "…" : text;
    }

    /// <summary>바인드 행(이름·위치·형·값·캡처 시각·캡처 여부)을 위치 순으로 모은다. 자식이 여럿이면 같은 위치는 최근 캡처만 남긴다.</summary>
    public static IReadOnlyList<CapturedBind> TopSqlBinds(IEnumerable<object?[]> rows)
    {
        var byPosition = new SortedDictionary<int, CapturedBind>();
        foreach (var r in rows)
        {
            var position = Int(r[1]) ?? 0;
            var captured = r[4] is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : (DateTimeOffset?)null;
            var wasCaptured = !string.Equals(Str(r[5]), "NO", StringComparison.OrdinalIgnoreCase);
            var bind = new CapturedBind(Str(r[0]) ?? string.Empty, position, Str(r[2]), wasCaptured ? Str(r[3]) : null) { CapturedAt = captured };
            if (!byPosition.TryGetValue(position, out var old) || (bind.CapturedAt ?? DateTimeOffset.MinValue) >= (old.CapturedAt ?? DateTimeOffset.MinValue))
            {
                byPosition[position] = bind;
            }
        }

        return [.. byPosition.Values];
    }

    public static TopSqlRow ToTopSqlRow(object?[] r)
    {
        var preview = (Str(r[3]) ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (preview.Length > TopSqlPreviewLength)
        {
            preview = preview[..TopSqlPreviewLength];
        }

        return new TopSqlRow(new CursorRef(Str(r[1]) ?? string.Empty, Int(r[2]), Int(r[0])), preview)
        {
            Executions = Long(r[4]) ?? 0,
            ElapsedSeconds = (Long(r[5]) ?? 0) / 1_000_000d,
            CpuSeconds = (Long(r[6]) ?? 0) / 1_000_000d,
            BufferGets = Long(r[7]) ?? 0,
            DiskReads = Long(r[8]) ?? 0,
            RowsProcessed = Long(r[9]) ?? 0,
            ParsingSchema = Str(r[10]),
            Module = Str(r[11]),
            LastActiveTime = r[12] is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : null,
        };
    }
}

/// <summary>
/// 블로킹 트리 재료 한 행. <see cref="Blocker"/> 의 Serial 은 서버가 주지 않아 0 이다 — 간선 대조는 (인스턴스, SID) 로 한다.
/// </summary>
public sealed record BlockingRow(SessionKey Key, SessionKey? Blocker)
{
    public string? Username { get; init; }

    public string? Event { get; init; }

    public long? SecondsInWait { get; init; }

    public string? SqlId { get; init; }

    public string? LockedObject { get; init; }

    public string? Status { get; init; }
}
