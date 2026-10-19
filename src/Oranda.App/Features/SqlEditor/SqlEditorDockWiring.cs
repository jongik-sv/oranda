using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Composition;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 편집기 탭을 셸 도킹의 문서 영역에 붙인다. 호스트(<see cref="SqlEditorHost"/>)는 탭 목록만 들고 있으므로,
/// 탭이 열리면 문서로 열고 문서 활성이 바뀌면 호스트의 활성 탭을 맞춘다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class SqlEditorDockWiring(
    DockWorkspace docking, ContextKeyService keys, IWorkspaceStore store, SettingsService settings, ShellController shell) : IStartupTask
{
    private const string PanelId = "oranda.panel.sqlEditor";

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var host = SqlEditorHost.Current;

            // 호스트는 DI 전에 만들어지므로 대화상자·작업 공간 저장소를 여기서 넣는다(없으면 파일 열기·작업 공간이 조용히 실패한다).
            host.Dialogs = new AvaloniaEditorDialogs();
            host.Store = store;
            var workspace = settings.Current.Workspace;
            host.RestoreOnStartup = workspace.RestoreOnStartup;
            foreach (var tab in host.Tabs.ToList())
            {
                Open(tab);
            }

            host.TabOpened += (_, tab) =>
            {
                Open(tab);
                tab.PropertyChanged += (_, _) => PushKeys(host);
            };
            foreach (var tab in host.Tabs)
            {
                tab.PropertyChanged += (_, _) => PushKeys(host);
            }

            host.TabClosed += (_, tab) =>
            {
                if (docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, tab)) is { } doc)
                {
                    docking.CloseDocument(doc);
                }

                PushKeys(host);
            };

            // 탭의 × 단추로 닫으면 호스트 목록에서도 뺀다(남아 있으면 작업 공간 저장·문맥 키가 어긋난다).
            docking.DocumentClosed += (_, doc) =>
            {
                if (doc.Panel is SqlEditorViewModel vm && host.Tabs.Contains(vm))
                {
                    host.Close(vm);
                }
            };
            host.ActiveChanged += (_, _) =>
            {
                // 호스트가 활성 탭을 바꾸면(작업 공간 열기 등) 도킹의 활성 문서도 맞춘다.
                if (host.Active is { } active && docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, active)) is { } doc
                    && !ReferenceEquals(docking.ActiveDocument, doc))
                {
                    docking.Activate(doc);
                }

                PushKeys(host);
            };
            PushKeys(host);

            // 비정상 종료 복구 또는 마지막 작업 공간 열기 → 자동 저장 → 종료 때 저장(data-model 2.6).
            _ = StartWorkspaceAsync(host, TimeSpan.FromSeconds(Math.Clamp(workspace.AutosaveSeconds, 1, 10)));
            shell.RegisterShutdown(host.ShutdownAsync);
            docking.ActiveDocumentChanged += (_, _) =>
            {
                if (docking.ActiveDocument?.Panel is SqlEditorViewModel vm && host.Tabs.Contains(vm))
                {
                    host.Active = vm;
                }
            };
        });
        return Task.CompletedTask;
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

    private void Open(SqlEditorViewModel tab)
    {
        var existing = docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, tab));
        if (existing is not null)
        {
            docking.Activate(existing);
            return;
        }

        if (docking.OpenDocument(PanelId, tab, tab.DisplayTitle) is { } doc)
        {
            // 탭 머리글은 「* 이름」(수정됨)·이름 바꾸기를 따라간다.
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SqlEditorViewModel.DisplayTitle))
                {
                    doc.Title = tab.DisplayTitle;
                }
            };
        }
    }
}
