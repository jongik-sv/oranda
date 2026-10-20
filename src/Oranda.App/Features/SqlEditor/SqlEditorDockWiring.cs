using System.Text.RegularExpressions;
using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Composition;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 작업 공간을 셸 도킹의 문서 영역에 붙인다(오렌지처럼 문서 탭 하나 = 작업 공간 하나, 그 안에 SQL 탭 여러 개).
/// 호스트(<see cref="SqlEditorHost"/>)는 작업 공간·탭 목록만 들고 있으므로, 작업 공간이 열리면 문서로 열고
/// 문서 활성이 바뀌면 호스트의 활성 작업 공간을 맞춘다. 문서 종류 ID 는 예전 그대로 <c>oranda.panel.sqlEditor</c> 다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed partial class SqlEditorDockWiring(
    DockWorkspace docking, ContextKeyService keys, IWorkspaceStore store, SettingsService settings, ShellController shell,
    IAppDataPaths paths) : IStartupTask
{
    private const string PanelId = "oranda.panel.sqlEditor";

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var host = SqlEditorHost.Current;

            // 호스트는 DI 전에 만들어지므로 대화상자·작업 공간 저장소를 여기서 넣는다(없으면 파일 열기·작업 공간이 조용히 실패한다).
            host.Dialogs = new AvaloniaEditorDialogs();
            host.Store = store;
            host.ListWorkspaceIds = ListSavedWorkspaceIds;
            var workspace = settings.Current.Workspace;
            host.RestoreOnStartup = workspace.RestoreOnStartup;
            host.OpenWorkspaceIds = workspace.OpenWorkspaceIds;
            host.LastWorkspaceId = workspace.LastWorkspaceId;
            foreach (var ws in host.Workspaces.ToList())
            {
                Open(ws);
            }

            host.WorkspaceOpened += (_, ws) => Open(ws);
            host.WorkspaceClosed += (_, ws) =>
            {
                if (docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, ws)) is { } doc)
                {
                    docking.CloseDocument(doc);
                }

                PushKeys(host);
            };
            host.TabOpened += (_, tab) =>
            {
                tab.PropertyChanged += (_, _) => PushKeys(host);
                PushKeys(host);
            };
            foreach (var tab in host.Tabs)
            {
                tab.PropertyChanged += (_, _) => PushKeys(host);
            }

            host.TabClosed += (_, _) => PushKeys(host);

            // 문서 탭의 × 로 작업 공간을 닫으면 호스트에서도 닫는다(저장하고 자동 저장을 지운다).
            docking.DocumentClosed += async (_, doc) =>
            {
                if (doc.Panel is not SqlWorkspaceViewModel ws || !host.Workspaces.Contains(ws))
                {
                    return;
                }

                try
                {
                    await host.CloseWorkspaceAsync(ws, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 저장에 실패해도 자동 저장 파일은 남아 있다(다음 시작 때 복구로 돌아온다). 사용자에게는 알린다.
                    await host.Dialogs.ShowMessageAsync("작업 공간 닫기", $"'{ws.Name}' 을(를) 저장하지 못했다: {ex.Message}", CancellationToken.None);
                }
            };
            host.ActiveWorkspaceChanged += (_, _) => ShowActive(host);
            host.ActiveChanged += (_, _) =>
            {
                ShowActive(host);
                PushKeys(host);
            };
            PushKeys(host);

            // 비정상 종료 복구 또는 지난번 작업 공간들 열기 → 자동 저장 → 종료 때 저장(data-model 2.6).
            _ = StartWorkspaceAsync(host, TimeSpan.FromSeconds(Math.Clamp(workspace.AutosaveSeconds, 1, 10)));
            shell.RegisterShutdown(async token =>
            {
                await host.ShutdownAsync(token).ConfigureAwait(true);
                var open = host.Workspaces.Select(w => w.Id).ToList();
                var last = host.ActiveWorkspace?.Id ?? open.FirstOrDefault() ?? SqlEditorHost.DefaultWorkspaceId;
                await settings.UpdateAsync(
                    s => s with { Workspace = s.Workspace with { OpenWorkspaceIds = open, LastWorkspaceId = last } },
                    token).ConfigureAwait(true);
            });
            docking.ActiveDocumentChanged += (_, _) =>
            {
                if (docking.ActiveDocument?.Panel is SqlWorkspaceViewModel ws && host.Workspaces.Contains(ws))
                {
                    host.ActiveWorkspace = ws;
                }
            };
        });
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> ListSavedWorkspaceIds()
    {
        var dir = paths.WorkspacesDirectory;
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(id => IdPattern().IsMatch(id))
                .Order(StringComparer.Ordinal)]
            : [];
    }

    private static async Task StartWorkspaceAsync(SqlEditorHost host, TimeSpan autosave)
    {
        try
        {
            await host.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Trace.TraceError("작업 공간 복구 실패: {0}", ex);
        }

        host.StartAutosave(autosave);
    }

    /// <summary>호스트가 활성 작업 공간을 바꾸면(작업 공간 열기·탭 열기 등) 도킹의 활성 문서도 맞춘다.</summary>
    private void ShowActive(SqlEditorHost host)
    {
        if (host.ActiveWorkspace is { } ws && docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, ws)) is { } doc
            && !ReferenceEquals(docking.ActiveDocument, doc))
        {
            docking.Activate(doc);
        }
    }

    /// <summary>편집기 상태 키(sqlDocOpen·hasSelection·canUndo 등)를 셸 문맥 키로 옮긴다. 값이 같으면 Set 이 알림을 내지 않는다.</summary>
    private void PushKeys(SqlEditorHost host)
    {
        foreach (var (key, value) in host.GetContextKeys())
        {
            if (key != "hasDocument") // 문서 수는 도킹(ShellController)이 PL/SQL·객체 상세 문서까지 세어 따로 정한다.
            {
                keys.Set(key, value);
            }
        }
    }

    private void Open(SqlWorkspaceViewModel ws)
    {
        var existing = docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, ws));
        if (existing is not null)
        {
            docking.Activate(existing);
            return;
        }

        if (docking.OpenDocument(PanelId, ws, ws.DisplayTitle) is { } doc)
        {
            // 문서 탭 머리글은 작업 공간 이름(고친 SQL 탭이 있으면 「* 이름」)을 따라간다.
            ws.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SqlWorkspaceViewModel.DisplayTitle))
                {
                    doc.Title = ws.DisplayTitle;
                }
            };
        }
    }
}
