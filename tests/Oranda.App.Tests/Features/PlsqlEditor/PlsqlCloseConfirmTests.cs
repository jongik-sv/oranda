using Avalonia.Threading;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.DedicatedSession;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Events;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>문서 탭 닫기 확인(<see cref="DockWorkspace.CloseConfirm"/>)에 PL/SQL 저장 확인과 전용 세션 확인이 등록 순서와 상관없이 함께 산다(TSK-13-27).</summary>
public sealed class PlsqlCloseConfirmTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Each_document_gets_its_own_confirm_in_either_registration_order(bool plsqlFirst) => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var dock = h.Get<DockWorkspace>();
        var host = new SqlEditorHost();
        if (plsqlFirst)
        {
            PlsqlEditorCommandHandler.InstallCloseConfirm(dock, () => host);
        }

        var registry = DedicatedSessionCommands.Resolve(h.Services);
        if (!plsqlFirst)
        {
            PlsqlEditorCommandHandler.InstallCloseConfirm(dock, () => host);
        }

        var plain = registry.Host.NewDocument();
        registry.Host.NewWorkspace();
        var dedicatedTab = registry.Host.NewDocument();
        registry.Attach(dedicatedTab, new DedicatedFakeSession(new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1")));
        Dispatcher.UIThread.RunJobs();

        var dirtyPlsql = new PlsqlEditorViewModel();
        dirtyPlsql.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
        dirtyPlsql.Editor.Document.Insert(0, "-- mine\n");
        var cleanPlsql = new PlsqlEditorViewModel();
        cleanPlsql.StartNew(string.Empty, null);
        var dirtyDoc = dock.OpenDocument(PlsqlEditorCommands.PanelId, dirtyPlsql, dirtyPlsql.Title)!;
        var cleanDoc = dock.OpenDocument(PlsqlEditorCommands.PanelId, cleanPlsql, cleanPlsql.Title)!;
        var plainDoc = dock.Documents.Single(d => ReferenceEquals(d.Panel, registry.Host.WorkspaceOf(plain)));
        var dedicatedDoc = dock.Documents.Single(d => ReferenceEquals(d.Panel, registry.Host.WorkspaceOf(dedicatedTab)));

        Assert.NotNull(dock.CloseConfirm!(dirtyDoc));
        Assert.Null(dock.CloseConfirm(cleanDoc));
        Assert.Null(dock.CloseConfirm(plainDoc));
        Assert.NotNull(dock.CloseConfirm(dedicatedDoc));
    });
}
