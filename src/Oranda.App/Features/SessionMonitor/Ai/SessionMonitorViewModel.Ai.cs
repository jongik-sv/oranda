using System;
using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Features.LockMonitor.Ai;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>
/// 세션 모니터 「대기 풀이」(설계 §3.2): 고른 세션 한 줄(대기 이벤트·대기 시간·막는 세션)과, 상세를 읽어 두었으면 그 현재·직전 SQL 을 보낸다.
/// 띠·「더 깊게」 는 락 모니터와 같은 패널(<see cref="LockExplainPanelViewModel"/>)이다.
/// </summary>
public sealed partial class SessionMonitorViewModel
{
    /// <summary>풀이 패널(앱 연결 전에는 null).</summary>
    public LockExplainPanelViewModel? AiExplain { get; private set; }

    /// <summary>AI 연결을 잇는다(셸 생성자와 시험).</summary>
    internal void UseAi(AiInPlaceHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        AiExplain = new LockExplainPanelViewModel(host, LockExplainKind.SessionWait, AiInput, () => _session?.Profile);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedSession) or nameof(IsOpen) or nameof(Detail))
            {
                AiExplain.Refresh();
            }
        };
        OnPropertyChanged(nameof(AiExplain));
    }

    /// <summary>고른 세션 한 줄. 고른 줄이 없으면 null.</summary>
    internal LockExplainInput? AiInput()
    {
        if (SelectedSession is not { Row: var row })
        {
            return null;
        }

        var detail = Detail is { } d && d.Session.Key == row.Key ? d : null; // 다른 줄의 상세는 쓰지 않는다
        var session = new LockExplainSession(row.Key)
        {
            BlockerKey = row.BlockingSession,
            Username = row.Username,
            OsUser = row.OsUser,
            Machine = row.Machine,
            Program = row.Program,
            Module = row.Module,
            Status = row.Status,
            Event = row.Event,
            WaitClass = row.WaitClass,
            SecondsInWait = row.SecondsInWait,
            LastCallElapsedSeconds = row.LastCallElapsedSeconds,
            SqlId = row.SqlId,
            PrevSqlId = row.PrevSqlId,
            SqlText = detail?.CurrentSqlText,
            PrevSqlText = detail?.PreviousSqlText,
        };
        return new LockExplainInput(LockExplainKind.SessionWait, [session]);
    }
}
