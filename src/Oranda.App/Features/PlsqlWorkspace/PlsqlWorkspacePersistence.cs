using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간 저장(결정 59 안 B-2, 결정 71): 열린 작업 공간을 <c>{data}/plsql-workspaces/{id}.json</c> 과 열린 목록 <c>open.json</c> 에 쓴다.
/// 자동 저장은 SQL 작업 공간과 같은 주기(설정 <c>workspace.autosaveSeconds</c>)로 돌고, 지난번에 쓴 JSON 과 같으면 쓰지 않는다. 앱을 닫을 때 한 번 더 쓴다.
/// 미리보기만 담은 작업 공간·미리보기 탭은 쓰지 않는다. 글과 상태는 UI 스레드에서만 읽는다.
/// 시작 때는 열린 목록 앞 <see cref="RestoreLimit"/> 개를 되살리고(설정 <c>workspace.restoreOnStartup</c> 을 SQL 작업 공간과 같이 따른다), 저장한 접속의 세션이 열리면 탭에 붙인다.
/// 사용자가 닫은 작업 공간 파일은 backup 으로 옮긴다.
/// 기능 나누기(TRD 10.7.3): 이 작업은 plsqlEditor 에 매달려 돌고(라우터 = PL/SQL 편집 탭이 열리는 문서 자리), plsqlWorkspace 가 꺼져 있으면
/// 라우터만 만들고 복원·자동 저장 타이머·종료 저장·닫은 파일 옮기기를 하지 않는다. 판단은 시작 때 한 번(실행 중 바뀜은 다음 실행, D-010).
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class PlsqlWorkspacePersistence(
    DockWorkspace docking, SettingsService settings, ShellController shell, IAppDataPaths paths, IServiceProvider services, IFeatureGate? gate = null) : IStartupTask
{
    /// <summary>시작 때 되살리는 작업 공간 수(결정 71 덧붙임: 열린 목록 앞에서부터). 넘는 것은 열지 않고 열린 목록에 남긴다.</summary>
    public const int RestoreLimit = 10;

    /// <summary>작업 공간마다 마지막으로 쓴 JSON(자동 저장이 바뀌었는지 견준다).</summary>
    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);

    /// <summary>이번 실행에서 열린 적 있는 작업 공간 ID(앞 실행 색인에서 이어 붙일 것을 가른다).</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>시작 때 파일이 없거나 손상이라 열린 목록에서 빼는 ID.</summary>
    private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);

    /// <summary>세션을 붙이려고 줄 세운 탭(같은 탭을 두 번 줄 세우지 않게).</summary>
    private readonly HashSet<PlsqlEditorViewModel> _attaching = [];

    private string? _writtenIndex;

    /// <summary>앞 실행이 남긴 열린 목록(처음 쓸 때 한 번 읽는다). 이번 실행에서 열지 않은 ID 는 잃지 않게 뒤에 이어 붙인다.</summary>
    private PlsqlWorkspaceIndex? _previousIndex;
    private bool _previousLoaded;

    /// <summary>돌고 있거나 마지막으로 돈 저장·복원·옮기기. 자동 저장은 겹치면 건너뛰고, 종료 저장은 이것을 기다린 뒤 한 번 더 쓴다.</summary>
    private Task _pending = Task.CompletedTask;
    private DispatcherTimer? _timer;
    private bool _wired;
    private bool _shuttingDown;

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    /// <summary>
    /// 이번 실행에서 작업 공간 저장·복원을 하는지(<see cref="RunAsync"/> 가 정한다, 그 전에는 null). 거짓이면 라우터만 있다.
    /// 시험·진단용.
    /// </summary>
    internal bool? Persisting { get; private set; }

    /// <summary>자동 저장 타이머가 돌고 있는지(시험·진단용).</summary>
    internal bool AutosaveRunning => _timer?.IsEnabled == true;

    /// <summary>파일 저장소(<c>{data}/plsql-workspaces</c>, 손상·닫힌 파일은 <c>{data}/backup/plsql-workspaces</c>: 닫힌 파일 개수 제한이 다른 백업을 지우지 않게 따로 둔다).</summary>
    public PlsqlWorkspaceStore Store { get; } = new(
        Path.Combine(paths.DataDirectory, "plsql-workspaces"), Path.Combine(paths.BackupDirectory, "plsql-workspaces"));

    private PlsqlWorkspaceRouter Router => PlsqlWorkspaceRouter.For(docking, services);

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ = Router; // 셸 서비스로 라우터를 먼저 만들어 둔다(탭 줄 「+」가 명령을 부를 수 있게)
            Persisting = gate?.IsEnabled(FeatureIds.PlsqlWorkspace) ?? true;
            if (Persisting != true)
            {
                return; // 작업 공간 저장 꺼짐: 탭은 열리되 파일에 쓰지도 되살리지도 않는다
            }

            shell.RegisterShutdown(ShutdownAsync);
            Wire();
            if (settings.Current.Workspace.RestoreOnStartup)
            {
                _pending = RestoreAsync(CancellationToken.None);
            }

            var seconds = Math.Clamp(settings.Current.Workspace.AutosaveSeconds, 1, 10);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _timer.Tick += async (_, _) => await SaveChangedAsync(CancellationToken.None).ConfigureAwait(true);
            _timer.Start();
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 문서 닫기(→ backup 으로 옮기기)와 세션 열림(→ 기다리는 탭에 붙이기)을 구독한다. 한 번만.
    /// 세션 열림은 이벤트 버스로 받는다: 접속 서비스는 첫 접속 명령 때에야 만들어지므로 시작 때는 구독할 수 없다.
    /// 세션 관리자는 세션을 목록에 넣은 뒤 알리고, 알림은 백그라운드 스레드에서 올 수 있다.
    /// </summary>
    internal void Wire()
    {
        if (_wired)
        {
            return;
        }

        _wired = true;
        docking.DocumentClosed += OnDocumentClosed;
        _ = (services.GetService(typeof(IEventBus)) as IEventBus)?.Subscribe<SessionOpened>(this, e =>
        {
            if (e.Purpose == SessionPurpose.Work)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if ((services.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions.FirstOrDefault(s => s.Id == e.SessionId) is { } session)
                    {
                        AttachOpened(session);
                    }
                });
            }
        });
    }

    /// <summary>
    /// 시작 복원: 열린 목록 차례로 파일이 읽히는 작업 공간을 최근 <see cref="RestoreLimit"/> 개까지 연다. 없거나 손상된 파일은 목록에서 빼고,
    /// 상한을 넘는 것과 새 버전 앱이 쓴 파일은 열지 않고 목록에 남긴다. 목록 자체가 새 버전 앱 것이면 아무것도 열지 않는다(이번 실행은 쓰지 않으므로 고친 글을 잃는다).
    /// 다른 문서가 활성이 아니었으면 마지막 활성 작업 공간을 앞으로 낸다. 이미 열린 접속이 있으면 바로 붙인다.
    /// </summary>
    internal async Task RestoreAsync(CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(Store.Folder))
            {
                return;
            }

            await LoadPreviousIndexAsync(ct).ConfigureAwait(true);
            if (_previousIndex is not { } index || Store.IsIndexReadOnly)
            {
                return;
            }

            PlsqlWorkspaceViewModel? last = null;
            var restored = new List<PlsqlWorkspaceViewModel>();
            foreach (var id in index.OpenIds.Where(PlsqlWorkspaceStore.IsValidId).Distinct(StringComparer.Ordinal))
            {
                if (restored.Count == RestoreLimit)
                {
                    break;
                }

                var file = await Store.LoadAsync(id, ct).ConfigureAwait(true);
                if (file is null)
                {
                    if (!Store.IsReadOnly(id))
                    {
                        _dropped.Add(id); // 없거나 손상(backup 으로 옮김). 새 버전 앱 파일은 이 판으로 못 읽어도 목록에 남긴다.
                    }

                    continue;
                }

                if (Store.IsReadOnly(id) || Router.Import(file) is not { } workspace)
                {
                    continue;
                }

                restored.Add(workspace);
                _seen.Add(id);
                _written[id] = PlsqlWorkspaceStore.Serialize(workspace.Capture());
                if (id == index.LastId)
                {
                    last = workspace;
                }
            }

            // 같은 시작 단계에서 SQL 작업 공간도 문서를 연다. 지금 활성이 없거나 이번에 연 작업 공간일 때만 마지막 활성 작업 공간을 앞으로 내고,
            // 그렇지 않아도 다음 탭이 들어가고 다음 저장의 lastId 가 될 작업 공간은 그것으로 둔다.
            last ??= restored.Count > 0 ? restored[0] : null;
            if (last is not null)
            {
                if ((docking.ActiveDocument?.Panel is not { } active || (active is PlsqlWorkspaceViewModel shown && restored.Contains(shown))) && Router.DocumentOf(last) is { } doc)
                {
                    docking.Activate(doc);
                }

                Router.Remember(last);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceError("PL/SQL 작업 공간 복원 실패: {0}", ex);
        }
        finally
        {
            AttachOpened(null);
        }
    }

    /// <summary>
    /// 바뀐 작업 공간과 열린 목록을 쓴다(자동 저장). 앞 저장이 아직 돌고 있으면 새로 쓰지 않고 그 저장을 돌려준다.
    /// PL/SQL 작업 공간을 한 번도 연 적 없고 폴더도 없으면 아무것도 만들지 않는다.
    /// </summary>
    internal Task SaveChangedAsync(CancellationToken ct) => _pending.IsCompleted ? _pending = SaveCoreAsync(ct) : _pending;

    /// <summary>종료 저장: 돌고 있는 자동 저장을 기다린 뒤 지금 글로 한 번 더 쓴다(자동 저장이 읽은 뒤 고친 글을 잃지 않게).</summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            await _pending.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        await (_pending = SaveCoreAsync(ct)).ConfigureAwait(true);
    }

    /// <summary>앱 종료 작업: 이 뒤로 닫히는 문서는 backup 으로 옮기지 않는다(다음 시작에 되살린다). 자동 저장을 멈추고 한 번 더 쓴다.</summary>
    internal Task ShutdownAsync(CancellationToken ct)
    {
        _shuttingDown = true;
        _timer?.Stop();
        return FlushAsync(ct);
    }

    /// <summary>
    /// 접속을 기다리는 복원 탭에 저장한 접속의 작업 세션을 붙인다. <paramref name="opened"/> 가 null 이면 이미 열린 접속에서 찾는다
    /// (접속 서비스가 아직 없으면 열린 접속도 없다: 세션 관리자를 불러 Oracle 어셈블리를 깨우지 않는다).
    /// 같은 접속의 탭은 하나씩 차례로 서버와 견준다(한 연결에 명령을 겹쳐 보내지 않게).
    /// </summary>
    internal void AttachOpened(ISession? opened)
    {
        var waiting = Router.Workspaces.SelectMany(w => w.Tabs).Where(t => t.AwaitingSession && !_attaching.Contains(t)).ToList();
        foreach (var group in waiting.GroupBy(t => t.RestoredProfileId!.Value))
        {
            var session = opened is not null
                ? opened.Profile.Id == group.Key ? opened : null
                : services.GetRegisteredOrDefault<IConnectionService>()?.SessionOf(group.Key);
            if (Usable(session) && session!.Profile.Id == group.Key)
            {
                var tabs = group.ToList();
                tabs.ForEach(t => _attaching.Add(t));
                _ = AttachInTurnAsync(tabs, session);
            }
        }
    }

    private static bool Usable(ISession? session) =>
        session is { Purpose: SessionPurpose.Work, State: not (SessionState.Broken or SessionState.Closed) };

    /// <summary>탭마다 아직 열려 있고 세션이 쓸 만한지 다시 보고 붙인다. 서버 확인은 10초까지 기다린다(멈춘 탭이 뒤 탭을 붙잡지 않게).</summary>
    private async Task AttachInTurnAsync(List<PlsqlEditorViewModel> tabs, ISession session)
    {
        foreach (var tab in tabs)
        {
            try
            {
                if (Usable(session) && Router.WorkspaceOf(tab) is not null)
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await tab.AttachSessionAsync(session, limit.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                tab.StatusText = "서버 소스를 10초 안에 확인하지 못했다";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("PL/SQL 탭에 세션을 붙이지 못했다: {0}", ex);
            }
            finally
            {
                _attaching.Remove(tab);
            }
        }
    }

    /// <summary>사용자가 닫은 작업 공간 파일을 backup 으로 옮긴다(돌던 저장 뒤에, 앱 종료 중이면 옮기지 않는다).</summary>
    private void OnDocumentClosed(object? sender, PanelDocument doc)
    {
        if (_shuttingDown || doc.Panel is not PlsqlWorkspaceViewModel workspace)
        {
            return;
        }

        _written.Remove(workspace.Id);
        _pending = RetireAfterAsync(_pending, workspace.Id);
    }

    private async Task RetireAfterAsync(Task previous, string id)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await Store.RetireAsync(id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceError("닫은 PL/SQL 작업 공간을 backup 으로 옮기지 못했다: {0}", ex);
        }
    }

    /// <summary>
    /// 앞 실행의 열린 목록을 한 번 읽는다. 읽지 못하면(권한·잠금) 예외가 그대로 나가 이번 차례는 아무것도 쓰지 않고 다음 차례에 다시 읽는다: 그냥 쓰면 앞 목록을 잃는다.
    /// </summary>
    private async Task LoadPreviousIndexAsync(CancellationToken ct)
    {
        if (_previousLoaded)
        {
            return;
        }

        _previousIndex = await Store.LoadIndexAsync(ct).ConfigureAwait(true);
        _previousLoaded = true;

        // 같은 목록이면 다시 쓰지 않는다(이번 실행에서 아무것도 열지 않았으면 open.json 을 건드리지 않는다).
        _writtenIndex = _previousIndex is null ? null : PlsqlWorkspaceStore.Serialize(_previousIndex);
    }

    /// <summary>
    /// 실패는 기록만 하고 다음 차례에 다시 쓴다(타이머 Tick 이 async void 라 예외가 새면 앱이 죽는다, 일반 SQL 자동 저장과 같다).
    /// 열린 목록을 쓸 수 없으면(새 버전 앱의 목록, 아직 읽지 못함) 작업 공간 파일도 쓰지 않는다: 어느 목록에도 오르지 않는 파일만 남는다.
    /// </summary>
    private async Task SaveCoreAsync(CancellationToken ct)
    {
        try
        {
            var open = Router.Workspaces.Where(w => !w.IsTransient).ToList();
            if (open.Count == 0 && _writtenIndex is null && !Directory.Exists(Store.Folder))
            {
                return;
            }

            await LoadPreviousIndexAsync(ct).ConfigureAwait(true);
            if (Store.IsIndexReadOnly)
            {
                return;
            }

            foreach (var workspace in open)
            {
                if (!Router.Workspaces.Contains(workspace))
                {
                    continue; // 앞 쓰기를 기다리는 사이 닫혔다
                }

                _seen.Add(workspace.Id);
                var file = workspace.Capture();
                var json = PlsqlWorkspaceStore.Serialize(file);
                if (_written.TryGetValue(workspace.Id, out var last) && string.Equals(last, json, StringComparison.Ordinal))
                {
                    continue;
                }

                await Store.SaveAsync(file, ct).ConfigureAwait(true);
                _written[workspace.Id] = json;
            }

            foreach (var gone in _written.Keys.Where(id => !open.Exists(w => w.Id == id)).ToList())
            {
                _written.Remove(gone);
            }

            open = open.FindAll(Router.Workspaces.Contains); // 쓰는 사이 닫힌 작업 공간은 목록에 올리지 않는다
            var carried = _previousIndex?.OpenIds
                .Where(id => PlsqlWorkspaceStore.IsValidId(id) && !_seen.Contains(id) && !_dropped.Contains(id))
                .Distinct(StringComparer.Ordinal).ToList() ?? [];
            if (open.Count == 0 && carried.Count == 0 && _writtenIndex is null)
            {
                return; // 적을 것도 지울 것도 없다
            }

            var carriedLast = _previousIndex?.LastId is { } previousLast && carried.Contains(previousLast) ? previousLast : carried.FirstOrDefault();
            var lastId = Router.LastActive is { } active && open.Contains(active) ? active.Id : open.FirstOrDefault()?.Id ?? carriedLast;
            var index = new PlsqlWorkspaceIndex { OpenIds = [.. open.Select(w => w.Id), .. carried], LastId = lastId };
            var indexJson = PlsqlWorkspaceStore.Serialize(index);
            if (!string.Equals(indexJson, _writtenIndex, StringComparison.Ordinal))
            {
                await Store.SaveIndexAsync(index, ct).ConfigureAwait(true);
                _writtenIndex = indexJson;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceError("PL/SQL 작업 공간 저장 실패(다음 자동 저장 때 다시): {0}", ex);
        }
    }
}
