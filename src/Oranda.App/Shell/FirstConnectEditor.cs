using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Shell.Docking;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 첫 접속 흐름(TSK-09-09, PRD UX-13, D-018): 작업 접속(<see cref="SessionPurpose.Work"/>)이 열렸는데 열린 문서가 하나도 없으면
/// 그 접속으로 새 SQL 편집기를 연다(「접속 → 새 SQL 편집기」 두 번의 클릭을 덜어 준다). 복원된 탭이 있으면 문서가 있어 열지 않고,
/// 전용 세션·모니터 접속은 목적이 달라 열지 않는다. 새 탭은 지금 접속을 따르므로(<c>oranda.file.newSqlEditor</c>) 접속을 마친 뒤
/// UI 스레드의 낮은 순위로 미뤄 연다. 앱(서비스 묶음)마다 하나만 둔다 — 시작 화면이 처음 만들어질 때 <see cref="Install"/> 한다.
/// </summary>
public sealed class FirstConnectEditor : IDisposable
{
    /// <summary>새 SQL 편집기 명령(시작 화면 첫 단추와 같다).</summary>
    public const string NewSqlEditorCommandId = "oranda.file.newSqlEditor";

    private static readonly ConditionalWeakTable<IServiceProvider, FirstConnectEditor> Installed = new();

    private readonly Func<int> _openDocuments;
    private readonly Func<Task> _openEditor;
    private readonly IDisposable _subscription;

    /// <param name="events">접속 성공(<see cref="SessionOpened"/>)을 듣는 버스.</param>
    /// <param name="openDocuments">지금 열린 문서 수.</param>
    /// <param name="openEditor">새 SQL 편집기를 연다.</param>
    public FirstConnectEditor(IEventBus events, Func<int> openDocuments, Func<Task> openEditor)
    {
        ArgumentNullException.ThrowIfNull(events);
        _openDocuments = openDocuments;
        _openEditor = openEditor;
        _subscription = events.Subscribe<SessionOpened>(this, OnOpened);
    }

    /// <summary>이 실행에서 연 SQL 편집기 수(시험용).</summary>
    public int Opened { get; private set; }

    /// <summary>
    /// 서비스 묶음에 한 번만 붙인다. 이벤트 버스·셸·명령 레지스트리가 없으면 아무 일도 하지 않는다(null).
    /// 접속 서비스(Oracle 어셈블리)는 깨우지 않는다 — 버스 이벤트만 듣는다.
    /// </summary>
    public static FirstConnectEditor? Install(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Installed.TryGetValue(services, out var existing))
        {
            return existing;
        }

        if (services.GetService(typeof(IEventBus)) is not IEventBus events
            || services.GetService(typeof(DockWorkspace)) is not DockWorkspace docking
            || services.GetService(typeof(ICommandRegistry)) is not ICommandRegistry registry
            || services.GetService(typeof(IContextKeys)) is not IContextKeys keys)
        {
            return null;
        }

        var editor = new FirstConnectEditor(events, () => docking.Documents.Count, () => registry.ExecuteAsync(NewSqlEditorCommandId, keys));
        Installed.AddOrUpdate(services, editor);
        return editor;
    }

    /// <summary>서비스 묶음에 붙은 것(시험용). 없으면 null.</summary>
    public static FirstConnectEditor? For(IServiceProvider services) =>
        Installed.TryGetValue(services, out var editor) ? editor : null;

    // 발행은 접속을 연 스레드에서 온다: UI 스레드로 넘기고, 접속 서비스가 지금 접속을 바꾼 뒤에 연다.
    private void OnOpened(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = OpenIfEmptyAsync(), DispatcherPriority.Background);
    }

    private async Task OpenIfEmptyAsync()
    {
        if (_openDocuments() != 0)
        {
            return;
        }

        Opened++;
        try
        {
            await _openEditor().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("첫 접속 SQL 편집기 열기 중 예외: {0}", ex);
        }
    }

    public void Dispose() => _subscription.Dispose();
}
