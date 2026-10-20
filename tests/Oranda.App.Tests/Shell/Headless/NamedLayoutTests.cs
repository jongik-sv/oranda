using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>이름 붙인 레이아웃(Toad Desktop Layout 처럼): 지금 배치를 이름으로 저장하고, 다른 배치에서 그 이름으로 되돌리고, 지운다.</summary>
public class NamedLayoutTests
{
    [Fact]
    public Task Layout_saved_by_name_is_applied_and_deleted_through_commands() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var dock = h.Get<DockWorkspace>();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var store = h.Get<NamedLayoutStore>();

        dock.HideTool("oranda.panel.schemaBrowser");
        dock.ShowTool("oranda.panel.outline");
        Assert.True(await registry.ExecuteAsync("oranda.window.saveLayoutAs", keys, "점검용"));
        Assert.Equal(["점검용"], await store.ListNamesAsync(default));
        Assert.Single(Directory.GetFiles(store.Directory, "*.json"));

        dock.Reset();
        Assert.True(dock.IsToolVisible("oranda.panel.schemaBrowser"));
        Assert.False(dock.IsToolVisible("oranda.panel.outline"));

        Assert.True(await registry.ExecuteAsync("oranda.window.applyLayout", keys, "점검용"));
        Assert.False(dock.IsToolVisible("oranda.panel.schemaBrowser"));
        Assert.True(dock.IsToolVisible("oranda.panel.outline"));

        // 같은 이름으로 다시 저장하면 덮어쓴다(한글 이름도 같은 파일).
        dock.Reset();
        Assert.True(await registry.ExecuteAsync("oranda.window.saveLayoutAs", keys, "점검용"));
        Assert.Single(Directory.GetFiles(store.Directory, "*.json"));
        Assert.Null((await store.LoadAsync("점검용", default))!.Window); // 창 위치는 싣지 않는다

        Assert.True(await registry.ExecuteAsync("oranda.window.deleteLayout", keys, "점검용"));
        Assert.Empty(await store.ListNamesAsync(default));
    });
}
