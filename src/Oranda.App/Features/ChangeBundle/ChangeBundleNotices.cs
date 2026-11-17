using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Core;
using Oranda.App.Features.Execution;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>
/// 반영 묶음 알림(설계 9.3): 자동 넣기가 DBMS 가 달라 넣지 않으면 메시지 창에 한 줄(<see cref="ChangeBundleDbmsMismatch"/>),
/// 넣으면 묶음 창 탭 제목에 건수를 잠깐 붙인다(창을 열지는 않는다). 묶음 창을 연 적이 없어도 알리도록 시작 작업으로 둔다.
/// 생성자는 등록된 셸 서비스만 받는다(시작 시험 제약).
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class ChangeBundleNotices(IServiceProvider services) : IStartupTask, IDisposable
{
    internal const string PanelId = "oranda.panel.changeBundle";

    private readonly List<IDisposable> _subscriptions = [];
    private CancellationTokenSource? _flash;
    private (string Original, string Flashed)? _flashed;

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        if (services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            _subscriptions.Add(bus.Subscribe<ChangeBundleDbmsMismatch>(this, e => UiThread.Post(() =>
                ExecutionLog.For(bus).AddMessage(MessageKind.Warning, MismatchText(e)))));
            _subscriptions.Add(bus.Subscribe<ChangeBundleChanged>(this, e =>
            {
                if (e.Added > 0 || e.Marked > 0)
                {
                    UiThread.Post(() => Flash(e));
                }
            }));
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _subscriptions.Clear();
        _flash?.Cancel();
        _flash?.Dispose();
        _flash = null;
    }

    /// <summary>메시지 창 한 줄.</summary>
    internal static string MismatchText(ChangeBundleDbmsMismatch e) =>
        $"반영 묶음 「{e.BundleName}」은 {e.BundleDbms} 묶음이라 {e.SessionDbms} 접속에서 실행한 DDL 을 넣지 않았다.";

    /// <summary>탭 제목에 붙일 건수(「반영 묶음 (+2)」·「반영 묶음 (✔1)」).</summary>
    internal static string FlashTitle(string title, int added, int marked) =>
        added > 0 ? $"{title} (+{added})" : $"{title} (✔{marked})";

    private void Flash(ChangeBundleChanged e)
    {
        if (services.GetService(typeof(IShellUi)) is not IShellUi { Docking.Layout: { } root } || FindTool(root) is not { } tool)
        {
            return;
        }

        _flash?.Cancel();
        _flash?.Dispose();
        var cts = _flash = new CancellationTokenSource();
        // 앞 알림이 아직 붙어 있으면 그 앞 제목으로 돌아간다(건수가 겹쳐 붙지 않게).
        var original = _flashed is { } prev && string.Equals(tool.Title, prev.Flashed, StringComparison.Ordinal) ? prev.Original : tool.Title ?? string.Empty;
        var flashed = FlashTitle(original, e.Added, e.Marked);
        _flashed = (original, flashed);
        tool.Title = flashed;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ChangeBundleViewModel.NoticeDuration, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            UiThread.Post(() =>
            {
                // 그 사이 언어를 바꿔 제목이 새로 붙었으면 그대로 둔다.
                if (string.Equals(tool.Title, flashed, StringComparison.Ordinal))
                {
                    tool.Title = original;
                }
            });
        });
    }

    private static PanelTool? FindTool(IDockable root)
    {
        var stack = new Stack<IDockable>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is PanelTool { Definition.Id: PanelId } tool)
            {
                return tool;
            }

            if (d is IDock { VisibleDockables: { } kids })
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }

        return null;
    }
}
