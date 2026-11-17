using Avalonia.Threading;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 새 테이블 작업 저장의 시작·종료 일(설계 18.7, TE-43, T16d).
/// <list type="bullet">
/// <item>종료 작업: 열린 「✎ 새 테이블」 탭마다 바뀐 것을 써 둔다(묻지 않음, 이 인스턴스가 쥔 채 — <see cref="TableDraftHook.SaveAllAsync"/>).</item>
/// <item>작업 공간 복원(설정 <c>workspace.restoreOnStartup</c>, SQL·PL/SQL 작업 공간과 같이 따름): 끝난 인스턴스가 열린 채 남긴 저장분을 그 접속의 작업 세션이 열릴 때
/// 탭으로 되살린다(이미 열린 세션이면 바로). 한 번에 <see cref="RestoreLimit"/> 개까지, 최근 것부터. 그 사이 다른 앱이 쥐었으면 건너뛴다.</item>
/// </list>
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class TableDraftStartup(IServiceProvider services, ShellController shell, SettingsService settings) : IStartupTask
{
    /// <summary>시작 때 되살릴 저장분 수 상한(PL/SQL 작업 공간과 같다).</summary>
    public const int RestoreLimit = 10;

    private readonly List<TableDraftEntry> _pending = [];
    private IDisposable? _subscription;

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    /// <summary>되살리기를 기다리는 저장분(시험·진단용).</summary>
    internal IReadOnlyList<TableDraftEntry> Pending => _pending;

    /// <summary>시험용: 되살린 저장분을 탭으로 여는 방법(기본 = <see cref="NewTableLauncher.OpenStoredAsync"/>).</summary>
    internal Func<ISession, StoredTableDraft, Task<bool>>? OpenStored { get; set; }

    /// <summary>마지막 되살리기(시험이 기다린다).</summary>
    internal Task LastRestore { get; private set; } = Task.CompletedTask;

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() => _ = StartAsync(CancellationToken.None));
        return Task.CompletedTask;
    }

    /// <summary>종료 작업을 걸고, 복원을 켰으면 목록을 읽어 열린 세션·앞으로 열릴 세션에 붙인다.</summary>
    internal async Task StartAsync(CancellationToken ct)
    {
        if (services.GetService(typeof(INewTableDraftHook)) is not TableDraftHook hook)
        {
            return;
        }

        shell.RegisterShutdown(hook.SaveAllAsync);
        if (!settings.Current.Workspace.RestoreOnStartup)
        {
            return;
        }

        IReadOnlyList<TableDraftEntry> entries;
        try
        {
            entries = await hook.Library.ListForRestoreAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return; // 저장소를 못 읽으면 되살리지 않는다(저장분은 소유자 노드 메뉴로 다시 연다)
        }

        Enqueue(entries);
        if (_pending.Count == 0)
        {
            return;
        }

        _subscription ??= (services.GetService(typeof(IEventBus)) as IEventBus)?.Subscribe<SessionOpened>(this, e =>
        {
            if (e.Purpose == SessionPurpose.Work)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if ((services.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions.FirstOrDefault(s => s.Id == e.SessionId) is { } session)
                    {
                        LastRestore = RestoreForAsync(session, CancellationToken.None);
                    }
                });
            }
        });

        foreach (var session in (services.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions.Where(s => s.Purpose == SessionPurpose.Work).ToList() ?? [])
        {
            LastRestore = RestoreForAsync(session, ct);
        }
    }

    /// <summary>되살릴 저장분을 기다리는 목록에 넣는다(최근 것부터 <see cref="RestoreLimit"/> 개까지).</summary>
    internal void Enqueue(IEnumerable<TableDraftEntry> entries) => _pending.AddRange(entries.Take(Math.Max(0, RestoreLimit - _pending.Count)));

    /// <summary>이 세션의 접속(이름·DBMS)이 남긴 저장분을 탭으로 되살린다. 기다리던 목록에서 빼고 하나씩 연다.</summary>
    internal async Task RestoreForAsync(ISession session, CancellationToken ct)
    {
        var mine = _pending.Where(e => e.ConnectionName == session.Profile.Name && e.Dbms == session.Profile.Dbms).ToList();
        if (mine.Count == 0)
        {
            return;
        }

        _pending.RemoveAll(mine.Contains);
        if (_pending.Count == 0)
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        if (services.GetService(typeof(INewTableDraftHook)) is not TableDraftHook hook)
        {
            return;
        }

        foreach (var entry in mine)
        {
            try
            {
                if (await hook.Library.GetAsync(entry.Id, ct).ConfigureAwait(true) is not { } stored)
                {
                    continue;
                }

                if (OpenStored is { } open)
                {
                    await open(session, stored).ConfigureAwait(true);
                    continue;
                }

                var browser = ObjectDetailCommandHandler.FindSchemaBrowser(services);
                IReadOnlyList<string> owners = browser?.CurrentSession?.DbKey == session.DbKey ? browser.AllSchemaNames : [stored.Draft.Owner];
                await NewTableLauncher.OpenStoredAsync(services, hook, session, stored, owners, () => new ObjectDetailViewModel(services), null, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // 하나를 못 되살려도 나머지는 연다(못 연 것은 소유자 노드 메뉴에 남는다).
            }
        }
    }
}
