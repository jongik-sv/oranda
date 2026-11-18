using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Features.LockMonitor.Ai;
using Oranda.Core.Monitoring;

namespace Oranda.App.Features.LockMonitor;

/// <summary>
/// 락 모니터 「AI 풀이」(설계 §3.2): 고른 줄이 든 차단 사슬(뿌리 보유자부터 트리 차례)을 모아 보낸다. 막는 쪽 SQL 은 상세 칸이 읽어 둔 글을 쓴다.
/// KILL 확인 창에는 받은 풀이의 결론 한 줄을 다시 보인다(<see cref="LockKillRequest.AiLine"/>).
/// </summary>
public sealed partial class LockMonitorViewModel
{
    /// <summary>풀이 패널(앱 연결 전에는 null). 화면은 ViewLocator 로 그린다.</summary>
    public LockExplainPanelViewModel? AiExplain { get; private set; }

    /// <summary>AI 연결을 잇는다(셸 생성자와 시험).</summary>
    internal void UseAi(AiInPlaceHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        AiExplain = new LockExplainPanelViewModel(host, LockExplainKind.LockChain, AiInput, () => _session?.Profile);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedKey) or nameof(IsOpen) or nameof(State))
            {
                AiExplain.Refresh();
            }
        };
        OnPropertyChanged(nameof(AiExplain));
    }

    /// <summary>고른 줄의 사슬. 고른 줄이 트리에 없으면(잠금 목록·방치 탭만) null.</summary>
    internal LockExplainInput? AiInput()
    {
        if (SelectedKey is not { } key || _allTree.FirstOrDefault(t => t.Key == key) is not { } item)
        {
            return null;
        }

        var root = item;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        var chain = new List<LockTreeItem> { root };
        chain.AddRange(Descendants(root));
        var sessions = chain.Select(t => Session(t, t == root ? HolderSql : t == item ? WaiterSql : null)).ToList();
        return new LockExplainInput(LockExplainKind.LockChain, sessions) { CollectedAt = _snapshot?.CollectedAt };
    }

    private LockExplainSession Session(LockTreeItem t, string? sql)
    {
        var rows = _snapshot?.Rows.Where(r => r.Session == t.Key).ToList() ?? [];
        var held = rows.Select(r => r.HeldMode).DefaultIfEmpty(0).Max();
        var requested = rows.Select(r => r.RequestedMode).DefaultIfEmpty(0).Max();
        return new LockExplainSession(t.Key)
        {
            BlockerKey = t.Parent?.Key,
            Username = t.Node.Username ?? t.Facts.Username,
            Machine = t.Facts.Machine,
            Program = t.Facts.Program,
            Status = t.Status,
            Event = t.IsHolder ? null : t.Node.Event,
            SecondsInWait = t.IsHolder ? null : t.Node.SecondsInWait,
            LockedObject = string.IsNullOrEmpty(t.ObjectText) ? null : t.ObjectText,
            HeldMode = held > 0 ? LockText.Mode(held) : null,
            RequestedMode = requested > 0 ? LockText.Mode(requested) : null,
            SqlId = t.Node.SqlId,
            PrevSqlId = t.Facts.PrevSqlId,
            SqlText = t.SqlIsPrevious ? null : sql,
            PrevSqlText = t.SqlIsPrevious ? sql : null,
            TransactionStart = t.Facts.TransactionStart,
            UndoBlocks = t.Facts.UndoBlocks,
        };
    }

    /// <summary>KILL 확인 창 한 줄(받은 풀이가 없거나 그 세션을 담지 않았으면 null).</summary>
    private string? AiKillLine(SessionKey key) => AiExplain?.KillLineFor(key);
}
