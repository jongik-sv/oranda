using Oranda.Core.Catalog.Providers;
using Oranda.Core.Events;

namespace Oranda.Core.Metadata;

/// <summary>
/// 객체 하나의 세부 종류·상태 표식(스키마 브라우저 아이콘 뱃지). 목록 질의와 따로 보조 질의(<c>meta.marks_*</c>)로 읽어 캐시 행(<see cref="MetaObject.SubKind"/>·
/// <see cref="MetaObject.StateMark"/>)에 둔다. 기본인 것(일반 테이블·켜진 작업·객체 타입)은 표식이 없다.
/// <para>
/// <see cref="SubKind"/> 는 공백으로 나눈 낱말 목록이다(<see cref="ObjectMarkRows"/> 가 만든다).
/// 테이블: <c>IOT</c>·<c>CLUSTER</c>·<c>GTT_SESSION</c>·<c>GTT_TRANSACTION</c>·<c>EXTERNAL</c>·<c>OBJECT</c> 중 하나와 <c>PART:방식</c>(예 <c>PART:RANGE-HASH</c>, 간격 파티션은 <c>PART:RANGE+INTERVAL</c>).
/// 트리거: 시점 <c>BEFORE</c>·<c>AFTER</c>·<c>INSTEAD</c>·<c>COMPOUND</c>, 단위 <c>ROW</c>·<c>STATEMENT</c>, 이벤트 <c>DDL</c>·<c>DB</c>(없으면 DML).
/// 동의어: <c>TARGET:유형</c>(공백은 <c>_</c>, 예 <c>TARGET:MATERIALIZED_VIEW</c>) 또는 <c>REMOTE</c>(DB 링크).
/// 구체화 뷰: 갱신 방식 <c>FAST</c>·<c>COMPLETE</c>·<c>FORCE</c>·<c>NEVER</c>, 커밋 때 갱신이면 <c>COMMIT</c>.
/// 타입: <c>VARRAY</c>·<c>NESTED_TABLE</c>.
/// </para>
/// <para><see cref="State"/>: <c>DISABLED</c>(꺼짐)·<c>RUNNING</c>(실행 중)·<c>FAILED</c>·<c>BROKEN</c>·<c>STALE</c>(쓸 수 없음 쪽). 원래 상태 글은 <see cref="StateDetail"/>.</para>
/// </summary>
public sealed record ObjectMark(string? SubKind, string? State)
{
    /// <summary>표식이 하나도 없다.</summary>
    public bool IsEmpty => SubKind is null && State is null;

    /// <summary><see cref="SubKind"/> 낱말들.</summary>
    public IReadOnlyList<string> Tokens => SubKind is null ? [] : SubKind.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>낱말 <paramref name="token"/> 이 있는가.</summary>
    public bool Has(string token) => Tokens.Contains(token, StringComparer.Ordinal);

    /// <summary><c>접두어:값</c> 낱말의 값(예 <c>PART</c> → <c>RANGE</c>). 없으면 null.</summary>
    public string? ValueOf(string prefix)
    {
        var head = prefix + ":";
        return Tokens.FirstOrDefault(t => t.StartsWith(head, StringComparison.Ordinal)) is { } t ? t[head.Length..] : null;
    }

    /// <summary>상태 글이 <c>코드:원래 글</c> 이면 원래 글(작업의 <c>FAILED:CHAIN_STALLED</c> 등). 없으면 null.</summary>
    public string? StateDetail => State is { } s && s.IndexOf(':', StringComparison.Ordinal) is var i and > 0 ? s[(i + 1)..] : null;

    /// <summary>상태 코드(<see cref="State"/> 의 <c>:</c> 앞).</summary>
    public string? StateCode => State is { } s && s.IndexOf(':', StringComparison.Ordinal) is var i and > 0 ? s[..i] : State;

    /// <summary>캐시 행의 두 열 → 표식. 둘 다 비면 null.</summary>
    public static ObjectMark? Of(MetaObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return o.SubKind is null && o.StateMark is null ? null : new ObjectMark(o.SubKind, o.StateMark);
    }
}

/// <summary>
/// 메타데이터 제공자 → 스키마 브라우저: 소유자 <paramref name="Owner"/> 의 표식을 서버에서 다시 읽어 캐시에 넣었다. 브라우저는 그 소유자의 표식을 캐시에서 다시 읽어 붙인다
/// (목록·트리는 다시 짓지 않는다).
/// </summary>
public sealed record ObjectMarksRefreshed(string DbKey, string Owner) : OrandaEvent;

/// <summary>
/// 표식 보조 질의(<c>meta.marks_*</c>) 행 → 표식. Oracle·Tibero 가 같은 열 순서로 질의해 같은 규칙으로 읽는다(Tibero 사전에 없는 열은 NULL = 모름 = 표식 없음).
/// 질의는 모두 바인드 <c>owner</c> 하나만 받고, 표식이 있는 객체만 돌려준다(기본인 것은 SQL 이 거른다 — 트리거·구체화 뷰·동의어는 모두 표식이 있다).
/// </summary>
public static class ObjectMarkRows
{
    public const string Tables = "meta.marks_tables";
    public const string Triggers = "meta.marks_triggers";
    public const string Jobs = "meta.marks_jobs";
    public const string Synonyms = "meta.marks_synonyms";
    public const string MViews = "meta.marks_mviews";
    public const string Types = "meta.marks_types";

    /// <summary>질의 id → 객체 유형(<c>ALL_OBJECTS.OBJECT_TYPE</c>). 표식은 이 유형의 캐시 행에 붙는다.</summary>
    public static string ObjectTypeOf(string queryId) => queryId switch
    {
        Tables => "TABLE",
        Triggers => "TRIGGER",
        Jobs => "JOB",
        Synonyms => "SYNONYM",
        MViews => "MATERIALIZED VIEW",
        Types => "TYPE",
        _ => throw new ArgumentOutOfRangeException(nameof(queryId), queryId, "모르는 표식 질의"),
    };

    /// <summary>질의 하나의 행들 → (객체, 표식). 첫 열은 늘 객체 이름이다. 표식이 비는 행은 뺀다.</summary>
    public static List<(DbObjectRef Ref, ObjectMark Mark)> Read(string queryId, string owner, IEnumerable<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var type = ObjectTypeOf(queryId);
        Func<object?[], ObjectMark> read = queryId switch
        {
            Tables => Table,
            Triggers => Trigger,
            Jobs => Job,
            Synonyms => Synonym,
            MViews => MView,
            _ => CollectionType,
        };
        // 같은 객체가 여러 줄로 올 수 있다(테이블: 일반 사전·외부·객체 테이블 갈래를 UNION ALL 로 따로 읽는다) — 낱말을 합친다.
        var result = new List<(DbObjectRef Ref, ObjectMark Mark)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (Str(At(r, 0)) is not { } name)
            {
                continue;
            }

            var mark = read(r);
            if (mark.IsEmpty)
            {
                continue;
            }

            if (at.TryGetValue(name, out var i))
            {
                var old = result[i].Mark;
                result[i] = (result[i].Ref, new ObjectMark(Join([.. old.Tokens, .. mark.Tokens.Where(t => !old.Has(t))]), old.State ?? mark.State));
            }
            else
            {
                at[name] = result.Count;
                result.Add((new DbObjectRef(owner, name, type), mark));
            }
        }

        return result;
    }

    /// <summary>
    /// <c>meta.marks_tables</c>: [0] table_name [1] temporary(Y/N, Tibero YES/NO) [2] duration(SYS$SESSION·SYS$TRANSACTION, Tibero SESSION·TRANSACTION)
    /// [3] iot_type [4] cluster_name(Tibero NULL) [5] partitioning(<c>방식|하위 방식|간격이면 YES</c>, 예 <c>RANGE|NONE|YES</c>, 파티션 테이블이 아니면 NULL)
    /// [6] is_external(YES/NO) [7] is_object(YES/NO). 한 줄에서 종류는 하나만 고른다(객체 &gt; 외부 &gt; 임시 &gt; IOT &gt; 클러스터), 파티션은 따로 붙는다(IOT 도 파티션될 수 있다).
    /// </summary>
    public static ObjectMark Table(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var tokens = new List<string>(2);
        if (Yes(At(r, 7)))
        {
            tokens.Add("OBJECT");
        }
        else if (Yes(At(r, 6)))
        {
            tokens.Add("EXTERNAL");
        }
        else if (Yes(At(r, 1)))
        {
            tokens.Add(Str(At(r, 2)) is { } d && d.Contains("SESSION", StringComparison.OrdinalIgnoreCase) ? "GTT_SESSION" : "GTT_TRANSACTION");
        }
        else if (string.Equals(Str(At(r, 3)), "IOT", StringComparison.Ordinal))
        {
            tokens.Add("IOT");
        }
        else if (Str(At(r, 4)) is { Length: > 0 })
        {
            tokens.Add("CLUSTER");
        }

        if (Str(At(r, 5)) is { } partitioning && partitioning.Split('|') is [var head, ..] parts && Word(head) is { } method)
        {
            var sub = parts.Length > 1 && Word(parts[1]) is { } s && !string.Equals(s, "NONE", StringComparison.Ordinal) ? "-" + s : string.Empty;
            tokens.Add("PART:" + method + sub + (parts.Length > 2 && Yes(parts[2]) ? "+INTERVAL" : string.Empty));
        }

        return new ObjectMark(Join(tokens), null);
    }

    /// <summary>
    /// <c>meta.marks_triggers</c>: [0] trigger_name [1] trigger_type(BEFORE EACH ROW·AFTER STATEMENT·INSTEAD OF·COMPOUND·BEFORE EVENT, Tibero 는 BEFORE DDL 도)
    /// [2] base_object_type(TABLE·VIEW·SCHEMA·DATABASE, Tibero NULL) [3] triggering_event [4] status(ENABLED/DISABLED).
    /// 이벤트 트리거(<c>EVENT</c>·<c>DDL</c>)는 이벤트 글로 DB 이벤트(LOGON·STARTUP …)와 DDL 을 가른다.
    /// </summary>
    public static ObjectMark Trigger(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var type = (Str(At(r, 1)) ?? string.Empty).ToUpperInvariant();
        var tokens = new List<string>(3);
        tokens.Add(type.StartsWith("BEFORE", StringComparison.Ordinal) ? "BEFORE"
            : type.StartsWith("AFTER", StringComparison.Ordinal) ? "AFTER"
            : type.StartsWith("INSTEAD", StringComparison.Ordinal) ? "INSTEAD"
            : type.StartsWith("COMPOUND", StringComparison.Ordinal) ? "COMPOUND"
            : string.Empty);
        var eventTrigger = type.EndsWith("EVENT", StringComparison.Ordinal) || type.EndsWith("DDL", StringComparison.Ordinal)
            || Str(At(r, 2)) is "SCHEMA" or "DATABASE";
        if (eventTrigger)
        {
            tokens.Add(IsDatabaseEvent(Str(At(r, 3))) ? "DB" : "DDL");
        }
        else if (type.Contains("ROW", StringComparison.Ordinal) || type.StartsWith("INSTEAD", StringComparison.Ordinal))
        {
            tokens.Add("ROW"); // INSTEAD OF 는 늘 행 단위
        }
        else if (type.Contains("STATEMENT", StringComparison.Ordinal))
        {
            tokens.Add("STATEMENT");
        }

        var disabled = string.Equals(Str(At(r, 4)), "DISABLED", StringComparison.OrdinalIgnoreCase);
        return new ObjectMark(Join(tokens), disabled ? "DISABLED" : null);
    }

    private static readonly string[] DatabaseEvents = ["LOGON", "LOGOFF", "STARTUP", "SHUTDOWN", "SERVERERROR", "SUSPEND", "DB_ROLE_CHANGE", "CLONE", "UNPLUG", "SET CONTAINER"];

    private static bool IsDatabaseEvent(string? events)
        => events is not null && DatabaseEvents.Any(e => events.Contains(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>meta.marks_jobs</c>(Oracle 만): [0] job_name [1] enabled(TRUE/FALSE) [2] state(SCHEDULED 는 SQL 이 뺀다).
    /// 꺼짐 = DISABLED, 실행 중 = RUNNING, 실패 = FAILED(RETRY SCHEDULED·CHAIN_STALLED 포함, 원래 글은 <c>FAILED:원래</c>), BROKEN. 그 밖의 상태(완료 등)는 표식 없음.
    /// </summary>
    public static ObjectMark Job(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var state = (Str(At(r, 2)) ?? string.Empty).ToUpperInvariant();
        var code = state switch
        {
            "RUNNING" => "RUNNING",
            "BROKEN" => "BROKEN",
            "FAILED" => "FAILED",
            "RETRY SCHEDULED" or "CHAIN_STALLED" => "FAILED:" + state,
            "DISABLED" or "STOPPED" => "DISABLED",
            _ => string.Equals(Str(At(r, 1)), "FALSE", StringComparison.OrdinalIgnoreCase) ? "DISABLED" : null,
        };
        return new ObjectMark(null, code);
    }

    /// <summary>
    /// <c>meta.marks_synonyms</c>: [0] synonym_name [1] db_link(Tibero NULL) [2] target_type(대상 객체의 유형, 대상을 못 찾으면 NULL).
    /// DB 링크를 거치면 REMOTE(대상은 확인하지 않는다), 대상이 없으면 상태 BROKEN.
    /// </summary>
    public static ObjectMark Synonym(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (Str(At(r, 1)) is { Length: > 0 })
        {
            return new ObjectMark("REMOTE", null);
        }

        return Str(At(r, 2)) is { Length: > 0 } target
            ? new ObjectMark("TARGET:" + target.Replace(' ', '_'), null)
            : new ObjectMark(null, "BROKEN");
    }

    /// <summary>
    /// <c>meta.marks_mviews</c>: [0] mview_name [1] refresh_method(FAST·COMPLETE·FORCE·NEVER) [2] refresh_mode(DEMAND·COMMIT·NEVER) [3] staleness.
    /// STALE·NEEDS_COMPILE·UNUSABLE 은 상태 STALE(원래 글은 <c>STALE:원래</c>).
    /// </summary>
    public static ObjectMark MView(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var tokens = new List<string>(2);
        if (Word(At(r, 1)) is { } method)
        {
            tokens.Add(method);
        }

        if (string.Equals(Str(At(r, 2)), "COMMIT", StringComparison.OrdinalIgnoreCase))
        {
            tokens.Add("COMMIT");
        }

        var staleness = (Str(At(r, 3)) ?? string.Empty).ToUpperInvariant();
        var state = staleness switch
        {
            "STALE" => "STALE",
            "NEEDS_COMPILE" or "UNUSABLE" => "STALE:" + staleness,
            _ => null,
        };
        return new ObjectMark(Join(tokens), state);
    }

    /// <summary><c>meta.marks_types</c>: [0] type_name [1] coll_type(VARYING ARRAY → VARRAY, TABLE → NESTED_TABLE). 객체 타입은 SQL 이 뺀다(기본).</summary>
    public static ObjectMark CollectionType(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var kind = (Str(At(r, 1)) ?? string.Empty).ToUpperInvariant() switch
        {
            "VARYING ARRAY" or "VARRAY" => "VARRAY",
            "TABLE" => "NESTED_TABLE",
            _ => null,
        };
        return new ObjectMark(kind, null);
    }

    private static object? At(object?[] r, int i) => i < r.Length ? r[i] : null;

    private static string? Str(object? v) => CatalogValues.Str(v)?.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>낱말로 쓸 수 있게(공백 → <c>_</c>, 대문자).</summary>
    private static string? Word(object? v) => Str(v)?.ToUpperInvariant().Replace(' ', '_');

    private static bool Yes(object? v) => Str(v) is "Y" or "YES" or "y" or "yes";

    private static string? Join(List<string> tokens)
    {
        tokens.RemoveAll(string.IsNullOrEmpty);
        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }
}
