using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Language.Errors;
using Oranda.Core.History;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LockMonitor;

public sealed partial class LockMonitorViewModel
{
    // ---- 세션 종료·연결 끊기·이동 ---------------------------------------------------------------

    /// <summary>확인 창 내용: 대상, 사용자·트랜잭션, 「이 세션 때문에 N개 세션이 기다립니다」 + 대기자 목록, 보낼 문장.</summary>
    public LockKillRequest? BuildKillRequest(bool disconnect)
    {
        if (SelectedKey is not { } key || _session is not { } session)
        {
            return null;
        }

        var item = _allTree.FirstOrDefault(t => t.Key == key);
        var facts = _facts.GetValueOrDefault(key, LockSessionFacts.Empty);
        var idle = IdleTransactions.FirstOrDefault(i => i.Key == key);
        var profile = session.Profile;
        var rac = session.Server.IsRac;
        var waiters = item is null ? [] : Descendants(item).ToList();
        var line = string.Join(" · ", waiters.Select(w =>
        {
            var behind = w.Parent is { } p && p.Key != key ? ", " + p.KeyText + " 뒤" : string.Empty;
            return $"{w.KeyText} {w.Username} ({LockText.Seconds(w.Node.SecondsInWait)}{behind})";
        }));
        var facts2 = new List<KeyValuePair<string, string>>
        {
            new("세션", string.Create(CultureInfo.InvariantCulture, $"{key.Sid}, {key.Serial}, @{key.InstanceId}")),
            new("사용자", string.Join(" · ", new[]
            {
                item?.Username ?? facts.Username ?? idle?.Row.Username, facts.Machine ?? idle?.Row.Machine, facts.Program ?? idle?.Row.Program,
            }.Where(v => !string.IsNullOrEmpty(v)))),
        };
        if ((facts.TransactionStart ?? idle?.Row.TransactionStart) is { } t && _snapshot is { } snap)
        {
            var undo = (facts.UndoBlocks ?? idle?.Row.UndoBlocks) is { } u ? $" · undo {u.ToString("N0", CultureInfo.InvariantCulture)} 블록" : string.Empty;
            facts2.Add(new("트랜잭션", LockText.Seconds((long)Math.Max(0, (snap.CollectedAt - t).TotalSeconds)) + undo));
        }

        string? statement;
        if (disconnect)
        {
            statement = SessionMonitorCommands.DisconnectStatement(_services, session, key, immediate: true, rac)?.Text;
        }
        else if (PreviewKill(session, key) is { } preview)
        {
            // 서비스가 실제로 보낼 문장 그대로 — 재입력 이름이 격자 값과 다르면(교정된 SERIAL#) 한 줄 덧붙인다.
            statement = preview.Text;
            if (LockText.KillTargetNote(key, preview.DangerousTarget) is { } note)
            {
                facts2.Add(new("재입력 이름", note));
            }
        }
        else
        {
            statement = LockText.KillStatement(_services, session, key, rac);
        }

        if (statement is null)
        {
            // 이 DBMS·버전에 KILL/DISCONNECT 문장이 없다 — 확인 창을 열지 않고 이유를 보인다.
            StatusText = CatalogRouting.Unavailable(session);
            return null;
        }

        var verb = disconnect ? "연결을 끊을까요?" : "종료할까요?";
        return new LockKillRequest(key, disconnect)
        {
            Question = $"세션 {LockText.Key(key)} {(disconnect ? "의 " : "을 ")}{verb}",
            HeaderTitle = $"{LockText.Environment(profile.Environment)} {profile.User.ToUpperInvariant()}@{profile.Name}",
            HeaderDetail = "· " + (session.Server.DatabaseName ?? session.Server.ServiceName ?? profile.Name)
                + (rac ? string.Create(CultureInfo.InvariantCulture, $" (RAC 인스턴스 {key.InstanceId})") : string.Empty),
            Environment = profile.Environment,
            EnvironmentColor = profile.Color,
            Facts = facts2,
            WaiterCount = waiters.Count,
            WaiterLine = line,
            Note = disconnect
                ? "연결을 끊으면 열린 트랜잭션은 롤백됩니다. undo 가 크면 롤백에 시간이 걸리고 그동안 잠금이 남을 수 있습니다."
                : "종료하면 열린 트랜잭션은 롤백됩니다. undo 가 크면 롤백에 시간이 걸리고 그동안 잠금이 남을 수 있습니다.",
            Statement = statement,
            AiLine = AiKillLine(key), // 앞서 받은 풀이의 결론 한 줄(새 요청 없음, 없으면 숨김)
        };
    }

    /// <summary>서비스가 KILL 로 보낼 문장(보내지 않는다). 서비스가 없거나 미리보기를 지원하지 않으면 null — 예전 카탈로그 문장으로 돌아간다.</summary>
    private SqlStatement? PreviewKill(ISession session, SessionKey key)
    {
        var monitor = _monitor ??= _services?.GetService(typeof(IMonitorService)) as IMonitorService;
        if (monitor is null)
        {
            return null;
        }

        try
        {
            return monitor.PreviewKill(session, key, immediate: true);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private IEnumerable<LockTreeItem> Descendants(LockTreeItem root)
    {
        foreach (var i in _allTree)
        {
            for (var p = i.Parent; p is not null; p = p.Parent)
            {
                if (p == root)
                {
                    yield return i;
                    break;
                }
            }
        }
    }

    /// <summary>고른 세션 종료(Delete). 확인 창 뒤 세션 모니터와 같은 Kill 경로(정책 관문)를 탄다.</summary>
    public Task KillAsync(CancellationToken ct = default) => ChangeSessionAsync(false, ct);

    /// <summary>고른 세션 연결 끊기(실행 엔진의 위험 명령 확인을 거친다).</summary>
    public Task DisconnectAsync(CancellationToken ct = default) => ChangeSessionAsync(true, ct);

    private async Task ChangeSessionAsync(bool disconnect, CancellationToken ct)
    {
        if (!CanKill || BuildKillRequest(disconnect) is not { } request)
        {
            return;
        }

        if (ConfirmKill is null || !await ConfirmKill(request))
        {
            StatusText = $"{request.ActionText} 을(를) 취소했습니다.";
            return;
        }

        var done = await RunAsync(async (monitor, session) =>
        {
            if (!disconnect)
            {
                await monitor.KillAsync(session, request.Target, true, ct);
                return;
            }

            var exec = Execution ?? _services?.GetService(typeof(IExecutionService)) as IExecutionService
                ?? throw new InvalidOperationException("실행 엔진이 없습니다.");
            var stmt = SessionMonitorCommands.DisconnectStatement(_services, session, request.Target, immediate: true, session.Server.IsRac)
                ?? throw new InvalidOperationException(CatalogRouting.Unavailable(session));
            var result = await exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(),
                new ExecutionOptions { Source = HistorySource.Monitor }, ct);
            switch (result.Status)
            {
                case ExecutionStatus.Cancelled:
                    throw new OperationCanceledException();
                case ExecutionStatus.Denied:
                    throw new UnauthorizedAccessException(result.Policy?.Message ?? "정책 관문이 막았습니다.");
                case ExecutionStatus.Failed:
                    throw new InvalidOperationException(result.Error?.Message ?? "실행하지 못했습니다.");
            }
        }, ct, reportAsBanner: false, isSessionChange: true);

        if (done)
        {
            await RefreshAsync(ct);
            PostToUi(() => StatusText = $"세션 {LockText.Key(request.Target)} 을(를) {(disconnect ? "끊었습니다" : "종료했습니다")}.");
        }
    }

    /// <summary>「세션 모니터로」.</summary>
    public async Task GoToSessionAsync()
    {
        if (SelectedKey is { } key && GoToSessionMonitor is { } go)
        {
            await go(key);
        }
    }
}
