using Avalonia.Threading;
using Oranda.App.Shell.Docking;
using Oranda.Core.Composition;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 편집기 탭을 셸 도킹의 문서 영역에 붙인다. 호스트(<see cref="SqlEditorHost"/>)는 탭 목록만 들고 있으므로,
/// 탭이 열리면 문서로 열고 문서 활성이 바뀌면 호스트의 활성 탭을 맞춘다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class SqlEditorDockWiring(DockWorkspace docking) : IStartupTask
{
    private const string PanelId = "oranda.panel.sqlEditor";

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var host = SqlEditorHost.Current;
            foreach (var tab in host.Tabs.ToList())
            {
                Open(tab);
            }

            host.TabOpened += (_, tab) => Open(tab);
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

    private void Open(SqlEditorViewModel tab)
    {
        var existing = docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, tab));
        if (existing is not null)
        {
            docking.Activate(existing);
            return;
        }

        docking.OpenDocument(PanelId, tab, tab.Title);
    }
}
