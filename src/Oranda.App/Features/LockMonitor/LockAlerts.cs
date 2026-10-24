using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Events;
using Oranda.Core.Monitoring;

namespace Oranda.App.Features.LockMonitor;

/// <summary>보유자 하나의 부하: 뒤에서 기다리는 세션 수(손자까지)와 가장 오래 기다린 초.</summary>
public sealed record LockHolderLoad(SessionKey Holder, string? Username, int Waiters, long MaxWaitSeconds);

/// <summary>임계치(LK-11): 「대기 <see cref="WaitSeconds"/> 초 이상 또는 대기자 <see cref="Waiters"/> 명 이상」.</summary>
public sealed record LockAlertRule(int WaitSeconds, int Waiters)
{
    /// <summary>대기자가 없는 보유자는 넘지 않는다. 기준 값이 0 이하이면 그 조건은 끈 것으로 본다.</summary>
    public bool Exceeds(LockHolderLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        return load.Waiters > 0
            && ((WaitSeconds > 0 && load.MaxWaitSeconds >= WaitSeconds) || (Waiters > 0 && load.Waiters >= Waiters));
    }

    public string Text => string.Create(CultureInfo.InvariantCulture, $"대기 {WaitSeconds:N0} 초 이상 또는 대기자 {Waiters:N0} 명 이상");
}

/// <summary>알림 한 건. <see cref="SnapshotPath"/> 는 스냅샷을 남겼으면 그 파일.</summary>
public sealed record LockAlert(SessionKey Holder, string? Username, int Waiters, long MaxWaitSeconds, DateTimeOffset At)
{
    public string? SnapshotPath { get; init; }

    /// <summary>창 알림 본문: 「1·1287 MES_APP 이 3명을 막고 있고, 가장 오래 기다린 세션이 287 초째입니다.」.</summary>
    public string Message => string.Create(CultureInfo.InvariantCulture,
        $"{LockText.Key(Holder)} {Username ?? "-"} 이(가) {Waiters:N0}명을 막고 있고, 가장 오래 기다린 세션이 {MaxWaitSeconds:N0} 초째입니다.")
        + (SnapshotPath is null ? string.Empty : " 보유자·대기자 SQL 스냅샷을 저장했습니다.");
}

/// <summary>이번 평가 결과: 기준을 넘은 보유자 전부와 새로 알릴 한 건(없으면 null).</summary>
public sealed record LockThresholdResult(IReadOnlyList<LockHolderLoad> Exceeding, LockAlert? NewAlert)
{
    public bool IsExceeded => Exceeding.Count > 0;

    /// <summary>상태 줄 칩 「N 세션 막음」의 N: 넘은 보유자들 뒤 대기자 합.</summary>
    public int Waiters => Exceeding.Sum(e => e.Waiters);

    public long MaxWaitSeconds => Exceeding.Count == 0 ? 0 : Exceeding.Max(e => e.MaxWaitSeconds);
}

/// <summary>
/// 임계치 알림 판정(LK-11). 보유자가 기준을 처음 넘을 때 한 번 알리고, 같은 보유자는 기준 아래로 내려갔다가 다시 넘기 전까지 다시 알리지 않는다.
/// 한 번에 여럿이 새로 넘으면 가장 오래 기다린(같으면 대기자가 많은) 보유자 하나만 알린다.
/// </summary>
public sealed class LockThresholdTracker
{
    private readonly HashSet<SessionKey> _alerted = [];

    public LockThresholdResult Evaluate(IReadOnlyList<LockHolderLoad> loads, LockAlertRule rule, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(loads);
        ArgumentNullException.ThrowIfNull(rule);
        var exceeding = loads.Where(rule.Exceeds).ToList();
        _alerted.IntersectWith(exceeding.Select(e => e.Holder));
        var fresh = exceeding.Where(e => !_alerted.Contains(e.Holder))
            .OrderByDescending(e => e.MaxWaitSeconds).ThenByDescending(e => e.Waiters).ThenBy(e => e.Holder.InstanceId).ThenBy(e => e.Holder.Sid)
            .ToList();
        foreach (var f in fresh)
        {
            _alerted.Add(f.Holder);
        }

        var first = fresh.FirstOrDefault();
        return new LockThresholdResult(exceeding,
            first is null ? null : new LockAlert(first.Holder, first.Username, first.Waiters, first.MaxWaitSeconds, at));
    }

    /// <summary>접속이 바뀌었거나 알림을 껐을 때.</summary>
    public void Reset() => _alerted.Clear();

    /// <summary>트리 뿌리마다 부하를 센다.</summary>
    public static IReadOnlyList<LockHolderLoad> Loads(IReadOnlyList<LockTreeItem> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var result = new List<LockHolderLoad>();
        foreach (var root in tree.Where(t => t.Parent is null))
        {
            var under = tree.Where(t => IsUnder(t, root)).ToList();
            result.Add(new LockHolderLoad(root.Key, root.Username, under.Count, under.Count == 0 ? 0 : under.Max(t => t.WaitSeconds)));
        }

        return result;

        static bool IsUnder(LockTreeItem item, LockTreeItem root)
        {
            for (var p = item.Parent; p is not null; p = p.Parent)
            {
                if (p == root)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Lock 모니터가 임계치를 넘었는지 상태 줄 막음 칩(<see cref="BlockerAlert.BlockerWatcher"/>)에 알린다.
/// <see cref="Exceeded"/> 가 false 면 칩을 내린다(모니터를 닫거나 기준 아래로 내려감).
/// </summary>
public sealed record LockThresholdChanged(bool Exceeded, int Waiters, long MaxWaitSeconds) : OrandaEvent;

/// <summary>스냅샷 텍스트(시각·접속·기준·보유자·대기자·SQL 전문).</summary>
public static class LockSnapshotText
{
    /// <summary>한 세션 묶음: 역할(「보유자」·「대기자」), 트리 줄, SQL 전문(없으면 null).</summary>
    public sealed record Entry(string Role, LockTreeItem Item, string? SqlId, string? SqlText);

    public static string Build(LockAlert alert, string account, LockAlertRule rule, IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        sb.AppendLine("Oranda Lock 모니터 — 임계치 스냅샷 (읽기 전용 조회 결과)");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"시각: {alert.At.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"));
        sb.AppendLine("접속: " + account);
        sb.AppendLine("기준: " + rule.Text);
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"보유자: {LockText.Key(alert.Holder)} {alert.Username ?? "-"} (sid {alert.Holder.Sid}, serial# {alert.Holder.Serial}, inst {alert.Holder.InstanceId}) · 대기자 {alert.Waiters:N0} · 최대 대기 {alert.MaxWaitSeconds:N0} 초"));
        foreach (var e in entries)
        {
            sb.AppendLine();
            var item = e.Item;
            var parts = new List<string> { $"{e.Role} {item.KeyText} {item.Username}", item.StatusText };
            if (!item.IsHolder)
            {
                parts.Add(item.WaitText);
                parts.Add(item.EventText);
            }

            if (item.ObjectText.Length > 0)
            {
                parts.Add(item.ObjectText);
            }

            if (e.Role != "보유자" && item.Parent is { } p)
            {
                parts.Add(p.KeyText + " 뒤");
            }

            parts.Add("sql_id " + (e.SqlId ?? "-"));
            sb.Append("== ").AppendLine(string.Join(" · ", parts.Where(s => s.Length > 0)));
            sb.AppendLine(e.SqlText is { Length: > 0 } sql ? sql.TrimEnd() : "(SQL 을 찾지 못했습니다)");
        }

        return sb.ToString();
    }
}

/// <summary>대기 그래프 순환(데드락 힌트, LK-12). 간선 = 대기자 → 막는 세션. 세션은 인스턴스·SID 로 같게 본다.</summary>
public static class WaitCycles
{
    /// <summary>
    /// 순환을 모두 찾는다(강하게 이어진 묶음마다 하나). 각 순환은 가장 작은(인스턴스·SID) 세션부터 간선을 따라간 차례이고 처음 세션을 끝에 다시 적지 않는다.
    /// 자기 자신을 막는 간선은 무시한다.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SessionKey>> Find(IEnumerable<(SessionKey Waiter, SessionKey Blocker)> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var keys = new Dictionary<(int, long), SessionKey>();
        var next = new Dictionary<(int, long), List<(int, long)>>();
        foreach (var (w, b) in edges)
        {
            var from = (w.InstanceId, w.Sid);
            var to = (b.InstanceId, b.Sid);
            if (from == to)
            {
                continue;
            }

            Remember(from, w);
            Remember(to, b);

            if (!next.TryGetValue(from, out var list))
            {
                next[from] = list = [];
            }

            if (!list.Contains(to))
            {
                list.Add(to);
            }
        }

        // Tarjan 강결합 요소(재귀 없이).
        var index = new Dictionary<(int, long), int>();
        var low = new Dictionary<(int, long), int>();
        var onStack = new HashSet<(int, long)>();
        var stack = new Stack<(int, long)>();
        var components = new List<List<(int, long)>>();
        var counter = 0;
        foreach (var start in keys.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            if (index.ContainsKey(start))
            {
                continue;
            }

            var work = new Stack<((int, long) Node, int Child)>();
            work.Push((start, 0));
            index[start] = low[start] = counter++;
            stack.Push(start);
            onStack.Add(start);
            while (work.Count > 0)
            {
                var (node, child) = work.Pop();
                var outs = next.GetValueOrDefault(node) ?? [];
                if (child < outs.Count)
                {
                    work.Push((node, child + 1));
                    var to = outs[child];
                    if (!index.TryGetValue(to, out var seenAt))
                    {
                        index[to] = low[to] = counter++;
                        stack.Push(to);
                        onStack.Add(to);
                        work.Push((to, 0));
                    }
                    else if (onStack.Contains(to))
                    {
                        low[node] = Math.Min(low[node], seenAt);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] == index[node])
                {
                    var comp = new List<(int, long)>();
                    (int, long) x;
                    do
                    {
                        x = stack.Pop();
                        onStack.Remove(x);
                        comp.Add(x);
                    }
                    while (x != node);

                    if (comp.Count > 1)
                    {
                        components.Add(comp);
                    }
                }
            }
        }

        var cycles = new List<IReadOnlyList<SessionKey>>();
        foreach (var comp in components)
        {
            var set = comp.ToHashSet();
            var first = comp.OrderBy(k => k.Item1).ThenBy(k => k.Item2).First();
            if (PathBack(first, set, next) is { } path)
            {
                cycles.Add([.. path.Select(k => keys[k])]);
            }
        }

        return [.. cycles.OrderBy(c => c[0].InstanceId).ThenBy(c => c[0].Sid)];

        // 같은 세션이 serial# 없이(0) 먼저 나왔으면 serial# 을 아는 쪽으로 바꾼다.
        void Remember((int, long) node, SessionKey key)
        {
            if (!keys.TryGetValue(node, out var known) || (known.Serial == 0 && key.Serial != 0))
            {
                keys[node] = key;
            }
        }
    }

    /// <summary>순환을 「1·0388 → 2·1903 → 1·0388」 로.</summary>
    public static string Text(IReadOnlyList<SessionKey> cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return cycle.Count == 0 ? string.Empty : string.Join(" → ", cycle.Append(cycle[0]).Select(LockText.Key));
    }

    /// <summary>묶음 안에서 <paramref name="start"/> 에서 나가 다시 돌아오는 가장 짧은 길(너비 우선).</summary>
    private static List<(int, long)>? PathBack((int, long) start, HashSet<(int, long)> within, Dictionary<(int, long), List<(int, long)>> next)
    {
        var from = new Dictionary<(int, long), (int, long)>();
        var queue = new Queue<(int, long)>();
        queue.Enqueue(start);
        var seen = new HashSet<(int, long)> { start };
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var to in next.GetValueOrDefault(node) ?? [])
            {
                if (!within.Contains(to))
                {
                    continue;
                }

                if (to == start)
                {
                    var path = new List<(int, long)> { node };
                    for (var p = node; p != start; )
                    {
                        p = from[p];
                        path.Add(p);
                    }

                    path.Reverse();
                    return path;
                }

                if (seen.Add(to))
                {
                    from[to] = node;
                    queue.Enqueue(to);
                }
            }
        }

        return null;
    }
}

/// <summary>방치 트랜잭션 한 줄(LK-12, gv$transaction + INACTIVE).</summary>
public sealed class IdleTransactionItem(IdleTransaction row, DateTimeOffset collectedAt)
{
    public static readonly string[] Columns = ["인스턴스·SID", "사용자", "머신", "프로그램", "마지막 호출 뒤", "트랜잭션 시작", "경과", "undo", "잠금", "차단"];

    /// <summary>오른쪽 맞춤 열(숫자·단위).</summary>
    public static readonly int[] NumberColumns = [4, 6, 7, 8];

    public IdleTransaction Row { get; } = row;

    public SessionKey Key => Row.Session;

    /// <summary>트랜잭션을 연 뒤 경과 초(시작 시각이 없으면 마지막 호출 뒤 초).</summary>
    public long TransactionSeconds => Row.TransactionStart is { } t ? (long)Math.Max(0, (collectedAt - t).TotalSeconds) : Row.IdleSeconds;

    public IReadOnlyList<object?> Cells =>
    [
        LockText.Key(Row.Session),
        Row.Username ?? "-",
        Row.Machine ?? string.Empty,
        Row.Program ?? string.Empty,
        LockText.Seconds(Row.IdleSeconds),
        Row.TransactionStart?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
        LockText.Elapsed(TransactionSeconds),
        Row.UndoBlocks is { } u ? u.ToString("N0", CultureInfo.InvariantCulture) + " 블록" : string.Empty,
        Row.LockCount.ToString("N0", CultureInfo.InvariantCulture),
        Row.BlockedCount > 0 ? Row.BlockedCount.ToString("N0", CultureInfo.InvariantCulture) + "명" : LockText.Dash,
    ];

    /// <summary>위험색 칸: 「차단」 이 있는 줄.</summary>
    public static bool IsDanger(int column, IReadOnlyList<object?> cells) => column == 9 && cells[9] as string is { } s && s != LockText.Dash;

    /// <summary>
    /// 오른쪽 안내: 사람이 쓰는 도구(SQL Developer·Toad·sqlplus 등)면 「고친 뒤 커밋하지 않은 것」, 아니면 앱의 커밋 누락 의심.
    /// </summary>
    public string Advice => LockText.IsHumanTool(Row.Program)
        ? $"사람이 쓰는 도구({Row.Program})에서 고친 뒤 커밋하지 않은 것으로 보입니다."
        : "앱이 트랜잭션을 연 채 쉬고 있습니다(커밋 누락 또는 연결 풀에 돌려주기 전 커밋 빠짐 의심).";
}

/// <summary>데드락 기록 한 줄(v$diag_alert_ext 의 ORA-00060).</summary>
public sealed partial class DeadlockItem(DeadlockEvent row, DateTimeOffset now)
{
    public static readonly string[] Columns = ["시각", "인스턴스", "내용", "트레이스 파일"];

    public DeadlockEvent Row { get; } = row;

    /// <summary>오늘이면 시각만, 아니면 날짜까지.</summary>
    public string TimeText
    {
        get
        {
            var local = Row.At.ToLocalTime();
            return local.Date == now.ToLocalTime().Date
                ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : local.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>메시지 줄에서 찾은 트레이스 파일 이름(경로 빼고). 없으면 빈 글.</summary>
    public string TraceFile
    {
        get
        {
            foreach (var line in Row.MessageLines)
            {
                if (TracePattern().Match(line) is { Success: true } m)
                {
                    return m.Groups[1].Value;
                }
            }

            return string.Empty;
        }
    }

    public IReadOnlyList<object?> Cells =>
    [
        TimeText,
        Row.InstanceId.ToString(CultureInfo.InvariantCulture),
        Summary,
        TraceFile,
    ];

    /// <summary>「내용」 칸: 첫 줄에서 트레이스 경로 안내를 떼고 48 자에서 줄인다(전문은 Enter 로 편집기에).</summary>
    public string Summary
    {
        get
        {
            var first = Row.MessageLines.Count > 0 ? Row.MessageLines[0] : string.Empty;
            var cut = first.IndexOf(" More info in file", StringComparison.OrdinalIgnoreCase);
            if (cut > 0)
            {
                first = first[..cut];
            }

            return first.Length > 48 ? first[..48].TrimEnd() + " …" : first;
        }
    }

    /// <summary>편집기로 열 글(시각·인스턴스·메시지 전부).</summary>
    public string FullText => string.Create(CultureInfo.InvariantCulture, $"-- {Row.At.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} · 인스턴스 {Row.InstanceId} · v$diag_alert_ext\n")
        + string.Join("\n", Row.MessageLines);

    [GeneratedRegex(@"([^\s/\\]+\.tr[cm])", RegexOptions.IgnoreCase)]
    private static partial Regex TracePattern();
}
