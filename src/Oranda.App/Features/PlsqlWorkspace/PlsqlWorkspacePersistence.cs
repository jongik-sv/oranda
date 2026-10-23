using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;
using Oranda.Core.Storage;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간 저장(결정 59 안 B-2, 결정 71): 열린 작업 공간을 <c>{data}/plsql-workspaces/{id}.json</c> 과 열린 목록 <c>open.json</c> 에 쓴다.
/// 자동 저장은 SQL 작업 공간과 같은 주기(설정 <c>workspace.autosaveSeconds</c>)로 돌고, 지난번에 쓴 JSON 과 같으면 쓰지 않는다. 앱을 닫을 때 한 번 더 쓴다.
/// 미리보기만 담은 작업 공간·미리보기 탭은 쓰지 않는다. 글과 상태는 UI 스레드에서만 읽는다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class PlsqlWorkspacePersistence(
    DockWorkspace docking, SettingsService settings, ShellController shell, IAppDataPaths paths, IServiceProvider services) : IStartupTask
{
    /// <summary>작업 공간마다 마지막으로 쓴 JSON(자동 저장이 바뀌었는지 견준다).</summary>
    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);

    /// <summary>이번 실행에서 열린 적 있는 작업 공간 ID(앞 실행 색인에서 이어 붙일 것을 가른다).</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private string? _writtenIndex;

    /// <summary>앞 실행이 남긴 열린 목록(처음 쓸 때 한 번 읽는다). 이번 실행에서 열지 않은 ID 는 잃지 않게 뒤에 이어 붙인다.</summary>
    private PlsqlWorkspaceIndex? _previousIndex;
    private bool _previousLoaded;

    /// <summary>돌고 있거나 마지막으로 돈 저장. 자동 저장은 겹치면 건너뛰고, 종료 저장은 이것을 기다린 뒤 한 번 더 쓴다.</summary>
    private Task _pending = Task.CompletedTask;
    private DispatcherTimer? _timer;

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    /// <summary>파일 저장소(<c>{data}/plsql-workspaces</c>, 손상·닫힌 파일은 <c>{data}/backup/plsql-workspaces</c>: 닫힌 파일 개수 제한이 다른 백업을 지우지 않게 따로 둔다).</summary>
    public PlsqlWorkspaceStore Store { get; } = new(
        Path.Combine(paths.DataDirectory, "plsql-workspaces"), Path.Combine(paths.BackupDirectory, "plsql-workspaces"));

    private PlsqlWorkspaceRouter Router => PlsqlWorkspaceRouter.For(docking, services);

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ = Router; // 셸 서비스로 라우터를 먼저 만들어 둔다(탭 줄 「+」가 명령을 부를 수 있게)
            var seconds = Math.Clamp(settings.Current.Workspace.AutosaveSeconds, 1, 10);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _timer.Tick += async (_, _) => await SaveChangedAsync(CancellationToken.None).ConfigureAwait(true);
            _timer.Start();
            shell.RegisterShutdown(async token =>
            {
                _timer?.Stop();
                await FlushAsync(token).ConfigureAwait(true);
            });
        });
        return Task.CompletedTask;
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

            if (!_previousLoaded)
            {
                // 읽지 못하면(권한·잠금) 아무것도 쓰지 않고 다음 차례에 다시 읽는다: 그냥 쓰면 앞 목록을 잃는다.
                _previousIndex = await Store.LoadIndexAsync(ct).ConfigureAwait(true);
                _previousLoaded = true;

                // 같은 목록이면 다시 쓰지 않는다(이번 실행에서 아무것도 열지 않았으면 open.json 을 건드리지 않는다).
                _writtenIndex = _previousIndex is null ? null : PlsqlWorkspaceStore.Serialize(_previousIndex);
            }

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

            var carried = _previousIndex?.OpenIds.Where(id => PlsqlWorkspaceStore.IsValidId(id) && !_seen.Contains(id)).Distinct(StringComparer.Ordinal).ToList() ?? [];
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
