using Avalonia.Threading;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorHost 의 일부: 시작 복원·자동 저장·종료(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SqlEditorHost
{
    /// <summary>
    /// 시작 때 한 번. 자동 저장 파일이 남아 있으면 비정상 종료로 보고 한 번 묻고, 예 하면 그 작업 공간들을 모두 복구한다(data-model 2.6).
    /// 없으면 지난번 열려 있던 작업 공간들(<see cref="OpenWorkspaceIds"/>)을 다시 연다. 복구했으면 true.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var autos = new List<Workspace>();
        foreach (var id in await _store.ListAutosaveIdsAsync(ct).ConfigureAwait(true))
        {
            Workspace? auto;
            try
            {
                auto = await _store.LoadAutosaveAsync(id, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                System.Diagnostics.Trace.TraceError("자동 저장 '{0}' 을(를) 읽지 못했다: {1}", id, ex.Message);
                continue; // 한 파일이 나빠도 나머지 작업 공간은 복구한다
            }

            if (auto is { Tabs.Count: > 0 })
            {
                autos.Add(auto);
            }
            else
            {
                await _store.DeleteAutosaveAsync(id, ct).ConfigureAwait(true); // 빈 자동 저장은 복구할 것이 없다
            }
        }

        if (autos.Count > 0)
        {
            var yes = await Dialogs.ConfirmAsync("지난 실행이 정상 종료되지 않았다. 저장하지 않은 편집 내용을 복구할까요?", ct).ConfigureAwait(true);
            if (yes)
            {
                foreach (var auto in autos.OrderBy(a => OrderOf(a.Id)))
                {
                    Restore(auto);
                }

                ActivateLast();
                return true;
            }

            foreach (var auto in autos)
            {
                await _store.DeleteAutosaveAsync(auto.Id, ct).ConfigureAwait(true);
            }
        }

        if (RestoreOnStartup)
        {
            foreach (var id in OpenWorkspaceIds.Distinct(StringComparer.Ordinal))
            {
                if (await LoadSavedAsync(id, ct).ConfigureAwait(true) is { } saved)
                {
                    Restore(saved);
                }
            }

            ActivateLast();
        }

        return false;
    }

    private int OrderOf(string id)
    {
        var i = OpenWorkspaceIds.ToList().IndexOf(id);
        return i < 0 ? int.MaxValue : i;
    }

    private void ActivateLast()
    {
        if (LastWorkspaceId is { } last && Workspaces.FirstOrDefault(w => w.Id == last) is { } ws)
        {
            ActiveWorkspace = ws;
        }
    }

    private async Task<Workspace?> LoadSavedAsync(string id, CancellationToken ct)
    {
        try
        {
            return (await _store!.LoadAsync(id, ct).ConfigureAwait(true))?.Value;
        }
        catch (ArgumentException)
        {
            return null; // 설정에 잘못된 ID 가 들어 있어도 시작은 막지 않는다
        }
    }

    /// <summary>
    /// 열린 작업 공간마다 바뀐 것이 있으면 <c>autosave/{id}.json</c> 에 쓴다. 하나라도 썼으면 true.
    /// UI 스레드에서 부른다(스냅샷 때문).
    /// </summary>
    public async Task<bool> AutosaveAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return false;
        }

        var wrote = false;
        foreach (var ws in Workspaces.ToList())
        {
            if (!Workspaces.Contains(ws) || !ws.ChangedSinceAutosave(out var stamp))
            {
                continue; // 앞 작업 공간을 쓰는 동안 닫혔으면 건너뛴다
            }

            await _store.AutosaveAsync(ws.ToWorkspace(), ct).ConfigureAwait(true);
            ws.MarkAutosaved(stamp);
            wrote = true;
            if (!Workspaces.Contains(ws))
            {
                await _store.DeleteAutosaveAsync(ws.Id, ct).ConfigureAwait(true); // 쓰는 사이 닫혔다: 다음 시작에 복구로 다시 나타나지 않게
            }
        }

        return wrote;
    }

    /// <summary>자동 저장 타이머를 켠다(기본 10 초, 설정 <c>workspace.autosaveSeconds</c> 1~10).</summary>
    public void StartAutosave(TimeSpan interval)
    {
        StopAutosave();
        if (_store is null)
        {
            return;
        }

        var cts = _autosaveCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => AutosaveAsync(cts.Token)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 디스크 오류 등은 다음 틱에 다시 시도한다(한 번 실패로 자동 저장이 멈추면 복구할 것이 없어진다).
                        System.Diagnostics.Trace.TraceError("자동 저장 실패: {0}", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Dispose() => StopAutosave();

    public void StopAutosave()
    {
        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        _autosaveCts = null;
    }

    /// <summary>
    /// 정상 종료: 열린 작업 공간을 모두 저장하고 자동 저장 파일을 지운다. 지워야 다음 시작이 「정상」으로 보인다.
    /// 다음 시작 때 다시 열 목록은 <see cref="Workspaces"/>·<see cref="WorkspaceId"/> 로 셸이 설정에 적는다.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        StopAutosave();
        if (_store is null)
        {
            return;
        }

        foreach (var ws in Workspaces.ToList())
        {
            await _store.SaveAsync(ws.ToWorkspace(), ct).ConfigureAwait(true);
            await _store.DeleteAutosaveAsync(ws.Id, ct).ConfigureAwait(true);
        }
    }
}
