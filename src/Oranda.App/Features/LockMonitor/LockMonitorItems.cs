using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Oranda.App.Features.ExecutionPlan;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;

namespace Oranda.App.Features.LockMonitor;

/// <summary>Lock 모니터 화면의 글 모양(초·모드·세션 표기)을 한 곳에서 만든다.</summary>
public static class LockText
{
    public const string Dash = "—";

    /// <summary>「1·1287」 — 인스턴스·SID(SID 는 네 자리 맞춤).</summary>
    public static string Key(SessionKey k) => string.Create(CultureInfo.InvariantCulture, $"{k.InstanceId}·{k.Sid:0000}");

    /// <summary>「287 초」. 없으면 —.</summary>
    public static string Seconds(long? s) => s is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) + " 초" : Dash;

    /// <summary>gv$lock lmode·request 0~6 → 「X (6)」. 0 이면 —.</summary>
    public static string Mode(int mode) => mode switch
    {
        1 => "N (1)",
        2 => "SS (2)",
        3 => "SX (3)",
        4 => "S (4)",
        5 => "SSX (5)",
        6 => "X (6)",
        _ => Dash,
    };

    /// <summary><c>ALTER SYSTEM KILL SESSION 'sid,serial#[,@inst]' IMMEDIATE</c>.</summary>
    public static string KillStatement(SessionKey k, bool rac)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"{k.Sid},{k.Serial}");
        if (rac)
        {
            id += string.Create(CultureInfo.InvariantCulture, $",@{k.InstanceId}");
        }

        return $"ALTER SYSTEM KILL SESSION '{id}' IMMEDIATE";
    }

    /// <summary>접속 환경 이름(운영·테스트·개발).</summary>
    public static string Environment(ConnectionEnvironment env) => env switch
    {
        ConnectionEnvironment.Production => "운영",
        ConnectionEnvironment.Test => "테스트",
        _ => "개발",
    };

    /// <summary>CSV 한 칸(쉼표·따옴표·줄바꿈이 있으면 큰따옴표로 감싼다).</summary>
    public static string Csv(string? value)
    {
        var v = value ?? string.Empty;
        return v.IndexOfAny([',', '"', '\n', '\r']) < 0 ? v : "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>TSV 한 칸(탭·줄바꿈은 빈칸으로).</summary>
    public static string Tsv(string? value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>대기 이벤트가 행 잠금 대기인지(잠긴 행 탭을 쓸 수 있는지).</summary>
    public static bool IsRowLockWait(string? ev) => ev?.Contains("row lock contention", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>경과: 1 분 아래는 「45 초」, 1 시간 아래는 「17 분」, 그 위는 「2 시간 5 분」.</summary>
    public static string Elapsed(long seconds)
    {
        var s = Math.Max(0, seconds);
        return s < 60 ? string.Create(CultureInfo.InvariantCulture, $"{s} 초")
            : s < 3600 ? string.Create(CultureInfo.InvariantCulture, $"{s / 60} 분")
            : string.Create(CultureInfo.InvariantCulture, $"{s / 3600:N0} 시간 {s % 3600 / 60} 분");
    }

    /// <summary>사람이 손으로 SQL 을 치는 도구 프로그램 이름인지(방치 트랜잭션 안내 문구).</summary>
    public static bool IsHumanTool(string? program)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return false;
        }

        string[] tools = ["SQL Developer", "sqldeveloper", "Toad", "PL/SQL Developer", "plsqldev", "sqlplus", "DBeaver", "Orange", "Oranda", "DataGrip", "SQL*Plus", "sqlcl"];
        return tools.Any(t => program.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>gv$session.status 가 INACTIVE 인지.</summary>
    public static bool IsInactive(string? status) => string.Equals(status, "INACTIVE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 대기 트리 한 줄(보유자 루트 → 대기자). 앞쪽 순회 차례로 펼친 줄이며 안내선은 계획 창의 <see cref="PlanTreeLines"/> 를 그대로 쓴다.
/// 상태는 gv$session.status(<see cref="BlockingNode.Status"/>, 없으면 잠금 목록의 <see cref="LockRow.Status"/>)를 그대로 보인다.
/// </summary>
public sealed class LockTreeItem
{
    internal LockTreeItem(BlockingNode node, int depth, LockTreeItem? parent, LockSessionFacts facts)
    {
        Node = node;
        Depth = depth;
        Parent = parent;
        Facts = facts;
    }

    public BlockingNode Node { get; }

    public SessionKey Key => Node.Session;

    public int Depth { get; }

    public LockTreeItem? Parent { get; }

    internal LockSessionFacts Facts { get; }

    /// <summary>안내선(├ └ │). 펼친 목록을 만들 때 채운다.</summary>
    public PlanTreeLines? TreeLines { get; internal set; }

    public bool HasChildren => Node.Waiters.Count > 0;

    public bool IsExpanded { get; internal set; } = true;

    /// <summary>뿌리(최종 보유자). 계획 창 「가장 비싼 단계」 와 같은 주황 띠로 그린다.</summary>
    public bool IsHolder => Depth == 0;

    /// <summary>이 세션 뒤에서 기다리는 세션 수(손자까지).</summary>
    public int WaiterTotal => Count(Node) - 1;

    public string ExpanderGlyph => !HasChildren ? string.Empty : IsExpanded ? "▾" : "▸";

    public string IconKey => IsHolder ? "lock" : "timer";

    public string KeyText => LockText.Key(Key);

    public string Username => Node.Username ?? Facts.Username ?? "-";

    /// <summary>gv$session.status. 트리·잠금 목록 어디에도 없으면 null.</summary>
    public string? Status => Node.Status ?? Facts.Status;

    /// <summary>INACTIVE(트랜잭션만 열린 채 쉬는 세션). 상태 글자를 경고색으로.</summary>
    public bool IsInactive => LockText.IsInactive(Status);

    public string StatusText => Status ?? LockText.Dash;

    public string WaitText => IsHolder ? LockText.Dash : LockText.Seconds(Node.SecondsInWait);

    public long WaitSeconds => IsHolder ? 0 : Node.SecondsInWait ?? 0;

    public string EventText => IsHolder
        ? string.Create(CultureInfo.InvariantCulture, $"보유 · 대기자 {WaiterTotal}")
        : Node.Event ?? string.Empty;

    public string ObjectText => Node.LockedObject
        ?? (IsHolder ? Node.Waiters.Select(w => w.LockedObject).FirstOrDefault(o => o is not null) : null)
        ?? Facts.TmObject
        ?? string.Empty;

    /// <summary>현재 SQL. 없으면 직전 SQL 앞에 「(prev)」.</summary>
    public string SqlText => Node.SqlId is { Length: > 0 } s ? s : Facts.PrevSqlId is { Length: > 0 } p ? "(prev) " + p : string.Empty;

    public bool SqlIsPrevious => string.IsNullOrEmpty(Node.SqlId) && !string.IsNullOrEmpty(Facts.PrevSqlId);

    public string? CurrentOrPreviousSqlId => Node.SqlId is { Length: > 0 } s ? s : Facts.PrevSqlId;

    public string ToolTip => IsInactive && IsHolder
        ? "INACTIVE — 실행 중인 SQL 없이 트랜잭션만 열려 있습니다(커밋 빠짐 의심)"
        : $"{KeyText} {Username} · {EventText}";

    private static int Count(BlockingNode n) => 1 + n.Waiters.Sum(Count);
}

/// <summary>잠금 목록에서 모은 세션별 정보(트리 줄·영향 순위·종료 확인이 쓴다).</summary>
public sealed record LockSessionFacts(string? Username, string? Machine, string? Program, string? PrevSqlId, string? TmObject,
    DateTimeOffset? TransactionStart, long? UndoBlocks, string? Status = null)
{
    public static readonly LockSessionFacts Empty = new(null, null, null, null, null, null, null);

    public static Dictionary<SessionKey, LockSessionFacts> From(IEnumerable<LockRow> rows)
    {
        var map = new Dictionary<SessionKey, LockSessionFacts>();
        foreach (var g in rows.GroupBy(r => r.Session))
        {
            var tx = g.FirstOrDefault(r => r.TransactionStart is not null);
            map[g.Key] = new LockSessionFacts(
                g.Select(r => r.Username).FirstOrDefault(v => v is not null),
                g.Select(r => r.Machine).FirstOrDefault(v => v is not null),
                g.Select(r => r.Program).FirstOrDefault(v => v is not null),
                g.Select(r => r.PrevSqlId).FirstOrDefault(v => v is not null),
                g.Where(r => r.LockType == "TM").Select(r => r.ObjectName).FirstOrDefault(v => v is not null),
                tx?.TransactionStart,
                tx?.UndoBlocks,
                g.Select(r => r.Status).FirstOrDefault(v => v is not null));
        }

        return map;
    }
}

/// <summary>잠금 목록 한 줄. <see cref="Cells"/> 는 복사·CSV 글, 화면 그리드 열은 <see cref="LockMonitorView"/> 가 정한다.</summary>
public sealed class LockItem(LockRow row, string objectText)
{
    public static readonly string[] Columns =
        ["인스턴스·SID", "사용자", "유형", "객체 / 자원", "보유", "요청", "ctime", "block", "트랜잭션 시작", "undo", "id1", "id2"];

    public LockRow Row { get; } = row;

    public SessionKey Key => Row.Session;

    public IReadOnlyList<object?> Cells =>
    [
        LockText.Key(Row.Session),
        Row.Username ?? "-",
        Row.LockType,
        objectText,
        LockText.Mode(Row.HeldMode),
        LockText.Mode(Row.RequestedMode),
        LockText.Seconds(Row.CtimeSeconds),
        Row.IsBlocking ? "차단" : LockText.Dash,
        Row.TransactionStart?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
        Row.UndoBlocks is { } u ? u.ToString("N0", CultureInfo.InvariantCulture) + " 블록" : string.Empty,
        Row.Id1.ToString(CultureInfo.InvariantCulture),
        Row.Id2.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>위험색 칸: block 「차단」, 요청 모드가 있는 칸.</summary>
    public static bool IsDanger(int column, IReadOnlyList<object?> cells) =>
        (column == 7 && cells[7] as string == "차단") || (column == 5 && cells[5] as string is { } s && s != LockText.Dash);

    /// <summary>
    /// 칸 「객체 / 자원」: TM 은 객체 이름, TX 는 같은 자원(id1·id2)을 기다리는 대기자가 기다리는 객체 + (행)·(블록 ITL).
    /// </summary>
    internal static string ObjectFor(LockRow row, IReadOnlyList<LockRow> all, IReadOnlyDictionary<SessionKey, BlockingNode> nodes)
    {
        if (row.ObjectName is { Length: > 0 } name)
        {
            return name;
        }

        if (row.LockType != "TX")
        {
            return string.Empty;
        }

        foreach (var r in all)
        {
            if (r.LockType == "TX" && r.Id1 == row.Id1 && r.Id2 == row.Id2 && (r.RequestedMode > 0 || ReferenceEquals(r, row))
                && nodes.TryGetValue(r.Session, out var n) && n.LockedObject is { Length: > 0 } obj)
            {
                var ev = n.Event ?? r.Event;
                var suffix = ev?.Contains("ITL", StringComparison.OrdinalIgnoreCase) == true ? " (블록 ITL)"
                    : LockText.IsRowLockWait(ev) ? " (행)" : string.Empty;
                return obj + suffix;
            }
        }

        return "(트랜잭션)";
    }
}

/// <summary>보유자 영향 순위 한 줄.</summary>
public sealed class ImpactItem(int rank, BlockerImpact impact, LockTreeItem? holder, LockSessionFacts facts, DateTimeOffset collectedAt)
{
    public static readonly string[] Columns = ["#", "보유자", "사용자", "상태", "대기자", "최대 대기", "합계 대기", "트랜잭션 경과", "프로그램"];

    public BlockerImpact Impact { get; } = impact;

    public SessionKey Key => Impact.Holder;

    /// <summary>순위(1 부터).</summary>
    public int Rank => rank;

    /// <summary>보유자 트랜잭션을 연 뒤 경과 초. 시작 시각을 모르면 null.</summary>
    public long? TransactionSeconds => facts.TransactionStart is { } t ? (long)Math.Max(0, (collectedAt - t).TotalSeconds) : null;

    public IReadOnlyList<object?> Cells =>
    [
        rank.ToString(CultureInfo.InvariantCulture),
        LockText.Key(Impact.Holder),
        holder?.Username ?? facts.Username ?? "-",
        Impact.Status ?? holder?.Status ?? facts.Status ?? LockText.Dash,
        Impact.WaiterCount.ToString("N0", CultureInfo.InvariantCulture),
        LockText.Seconds(Impact.MaxWaitSeconds),
        LockText.Seconds(Impact.TotalWaitSeconds),
        TransactionSeconds is { } elapsed ? LockText.Seconds(elapsed) : LockText.Dash,
        facts.Program ?? string.Empty,
    ];
}

/// <summary>DDL 잠금 한 줄(dba_ddl_locks).</summary>
public sealed class DdlLockItem(DdlLockRow row)
{
    public static readonly string[] Columns = ["인스턴스·SID", "객체", "유형", "보유", "요청"];

    public DdlLockRow Row { get; } = row;

    public SessionKey Key => Row.Session;

    public bool IsWaiting => Row.ModeRequested is { Length: > 0 } m && !m.Equals("None", StringComparison.OrdinalIgnoreCase);

    public bool IsExclusive => Row.ModeHeld?.Equals("Exclusive", StringComparison.OrdinalIgnoreCase) == true;

    public IReadOnlyList<object?> Cells =>
    [
        LockText.Key(Row.Session),
        Row.Owner + "." + Row.Name,
        Row.Type,
        Row.ModeHeld ?? "None",
        Row.ModeRequested ?? "None",
    ];
}

/// <summary>원인 힌트 카드의 무게(왼쪽 띠 색).</summary>
public enum LockHintSeverity
{
    Danger,
    Warning,
    Info,
    Muted,
}

/// <summary>원인 힌트 카드 하나(LK-08). <see cref="CopySql"/> 는 확인용 SELECT(읽기 전용) — 화면은 복사만 한다.</summary>
public sealed record LockHint(LockHintSeverity Severity, string Title, string Body, string? Todo, string? CopySql = null)
{
    public string IconKey => Severity switch
    {
        LockHintSeverity.Danger => "lock",
        LockHintSeverity.Warning => "warn",
        _ => "info",
    };

    public bool IsDanger => Severity == LockHintSeverity.Danger;

    public bool IsWarning => Severity == LockHintSeverity.Warning;

    public bool IsInfo => Severity == LockHintSeverity.Info;

    public bool IsMuted => Severity == LockHintSeverity.Muted;

    public bool HasTodo => Todo is not null;

    public bool HasCopy => CopySql is not null;
}

/// <summary>대기 이벤트로 원인 힌트를 만든다(TX row lock · TM contention(FK 인덱스) · ITL · library cache).</summary>
public static class LockHints
{
    public static IReadOnlyList<LockHint> Build(IReadOnlyList<LockTreeItem> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var hints = new List<LockHint>();
        var waiters = all.Where(i => !i.IsHolder).ToList();

        var row = waiters.Where(w => LockText.IsRowLockWait(w.Node.Event)).ToList();
        if (row.Count > 0)
        {
            var holders = row.Select(Root).Distinct().ToList();
            var idle = holders.Where(h => h.IsInactive).ToList();
            var body = new StringBuilder("다른 세션이 같은 행을 고친 뒤 커밋하지 않았습니다.");
            if (idle.Count > 0)
            {
                body.Append(" 보유자 ").Append(string.Join(", ", idle.Select(h => h.KeyText)))
                    .Append(" 은(는) 지금 실행 중인 SQL 없이 트랜잭션만 열고 있습니다.");
            }

            hints.Add(new LockHint(LockHintSeverity.Danger,
                $"enq: TX - row lock contention · 대기 {row.Count} 세션",
                body.ToString(),
                "앱 담당자에게 커밋 누락을 확인. 급하면 보유자를 세션 종료(롤백됨)."));
        }

        foreach (var g in waiters.Where(w => w.Node.Event?.Contains("enq: TM", StringComparison.OrdinalIgnoreCase) == true)
                     .GroupBy(w => w.ObjectText))
        {
            var obj = g.Key.Length > 0 ? g.Key : "(객체 모름)";
            hints.Add(new LockHint(LockHintSeverity.Warning,
                $"enq: TM - contention · {obj}",
                $"부모 표의 행을 지우거나 키를 바꿀 때 자식 표 {obj} 전체에 잠금을 요청했습니다. 외래 키 열에 인덱스가 없을 때 흔합니다.",
                $"{obj} 의 외래 키 열에 인덱스가 있는지 확인",
                g.Key.Length > 0 ? ForeignKeyIndexCheck(g.Key) : null));
        }

        foreach (var g in waiters.Where(w => w.Node.Event?.Contains("allocate ITL", StringComparison.OrdinalIgnoreCase) == true)
                     .GroupBy(w => w.ObjectText))
        {
            hints.Add(new LockHint(LockHintSeverity.Info,
                $"enq: TX - allocate ITL entry · {(g.Key.Length > 0 ? g.Key : "(객체 모름)")}",
                "블록 안 트랜잭션 자리(ITL)가 모자라 기다립니다. 같은 블록을 동시에 고치는 세션이 많습니다.",
                "대기가 잦으면 INITRANS·PCTFREE 를 늘리는 재구성을 DBA 와 검토."));
        }

        var library = waiters.Where(w => w.Node.Event?.StartsWith("library cache", StringComparison.OrdinalIgnoreCase) == true).ToList();
        hints.Add(library.Count > 0
            ? new LockHint(LockHintSeverity.Warning,
                $"library cache lock · pin · 대기 {library.Count} 세션",
                "DDL·컴파일과 실행이 겹쳤습니다. 컴파일 중인 객체를 쓰는 세션이 기다립니다.",
                "「DDL 잠금」 탭에서 Exclusive 를 잡은 세션을 확인.")
            : new LockHint(LockHintSeverity.Muted, "library cache lock · pin",
                "지금 없음. DDL·컴파일과 실행이 겹치면 여기 보이고 「DDL 잠금」 탭으로 이어집니다.", null));
        return hints;
    }

    /// <summary>표(<c>OWNER.NAME</c>)의 외래 키 가운데 앞머리 인덱스가 없는 열을 찾는 SELECT(all_* 뷰, 읽기 전용).</summary>
    public static string ForeignKeyIndexCheck(string ownerDotName)
    {
        ArgumentNullException.ThrowIfNull(ownerDotName);
        var dot = ownerDotName.IndexOf('.', StringComparison.Ordinal);
        var owner = dot < 0 ? string.Empty : ownerDotName[..dot];
        var name = dot < 0 ? ownerDotName : ownerDotName[(dot + 1)..];
        static string Q(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "SELECT c.constraint_name, cc.column_name, cc.position\n"
            + "  FROM all_constraints c\n"
            + "  JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name\n"
            + " WHERE c.constraint_type = 'R'\n"
            + (owner.Length > 0 ? "   AND c.owner = " + Q(owner) + "\n" : string.Empty)
            + "   AND c.table_name = " + Q(name) + "\n"
            + "   AND NOT EXISTS (SELECT 1 FROM all_ind_columns ic\n"
            + "                    WHERE ic.table_owner = cc.owner AND ic.table_name = cc.table_name\n"
            + "                      AND ic.column_name = cc.column_name AND ic.column_position = cc.position)\n"
            + " ORDER BY c.constraint_name, cc.position";
    }

    private static LockTreeItem Root(LockTreeItem i)
    {
        while (i.Parent is { } p)
        {
            i = p;
        }

        return i;
    }
}

/// <summary>세션 종료·연결 끊기 확인 창에 보일 내용(시안 b). 기본 단추는 취소.</summary>
public sealed record LockKillRequest(SessionKey Target, bool Disconnect)
{
    /// <summary>「세션 1·1287 을 종료할까요?」.</summary>
    public string Question { get; init; } = string.Empty;

    /// <summary>머리 띠: 「운영 MESADM@MESPRD」.</summary>
    public string HeaderTitle { get; init; } = string.Empty;

    /// <summary>머리 띠 작은 글: 「· MESPRD (RAC 인스턴스 1)」.</summary>
    public string HeaderDetail { get; init; } = string.Empty;

    public ConnectionEnvironment Environment { get; init; }

    /// <summary>이름·값 줄(세션·사용자·트랜잭션).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Facts { get; init; } = [];

    /// <summary>이 세션 뒤에서 기다리는 세션 수(손자까지).</summary>
    public int WaiterCount { get; init; }

    /// <summary>「이 세션 때문에 3개 세션이 기다립니다.」. 대기자가 없으면 null.</summary>
    public string? WaiterHeadline => WaiterCount > 0
        ? string.Create(CultureInfo.InvariantCulture, $"이 세션 때문에 {WaiterCount}개 세션이 기다립니다.")
        : null;

    /// <summary>대기자 목록 한 줄: 「1·1422 MES_APP (287 초) · 2·0388 MES_RPT (141 초, 1·1422 뒤)」.</summary>
    public string WaiterLine { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public string Statement { get; init; } = string.Empty;

    /// <summary>실행 단추 글(「세션 종료」·「연결 끊기」).</summary>
    public string ActionText => Disconnect ? "연결 끊기" : "세션 종료";

    /// <summary>시험·로그용 한 덩이 글.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(HeaderTitle + " " + HeaderDetail);
        sb.AppendLine(Question);
        foreach (var f in Facts)
        {
            sb.Append(f.Key).Append(": ").AppendLine(f.Value);
        }

        if (WaiterHeadline is { } h)
        {
            sb.AppendLine(h);
            sb.AppendLine(WaiterLine);
        }

        sb.AppendLine(Note);
        sb.Append(Statement);
        return sb.ToString();
    }
}
