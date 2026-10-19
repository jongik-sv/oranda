using System.Text.RegularExpressions;
using Avalonia.Controls;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Contract;

// 시험 어셈블리에만 있는 가짜 기여. 셸 파일을 고치지 않고 수집되는지 본다.
[OrandaToolPanel("oranda.panel.testFake", ToolPanelKind.Tool, DockLocation.Bottom, Order = 999)]
public sealed class FakeTestPanelViewModel : ToolPanelViewModelBase
{
}

[OrandaCommand("oranda.test.fakeRun", CommandCategory.Tools, Enablement = "hasSession", Aliases = ["oranda.test.oldFakeRun"])]
[OrandaCommand("oranda.test.fakeOther", CommandCategory.Tools)]
public sealed class FakeTestCommandHandler : ICommandHandler
{
    public static List<string> Calls { get; } = [];

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(context.CommandId);
        }

        return Task.CompletedTask;
    }
}

[OrandaStatusBarItem("oranda.status.testFake", 99, CollapseRank = 5)]
public sealed class FakeTestStatusBarItem : IStatusBarItem
{
    public bool IsVisible => true;
}

public partial class RegistryContractTests
{
    private static readonly ShellContributions TestAssembly = ShellContributions.Scan(typeof(RegistryContractTests).Assembly);

    [Fact]
    public void Registries_implement_core_contracts()
    {
        Assert.True(typeof(ICommandRegistry).IsAssignableFrom(typeof(CommandRegistry)));
        Assert.True(typeof(IToolPanelRegistry).IsAssignableFrom(typeof(ToolPanelRegistry)));
        Assert.True(typeof(IStatusBarRegistry).IsAssignableFrom(typeof(StatusBarRegistry)));
    }

    [Fact]
    public void Tool_panel_in_other_assembly_is_collected_by_scan()
    {
        var registry = new ToolPanelRegistry(TestAssembly.Panels, includeDevBuildOnly: false);

        var panel = registry.Find("oranda.panel.testFake");

        Assert.NotNull(panel);
        Assert.Equal(typeof(FakeTestPanelViewModel), panel.ViewModelType);
        Assert.Equal("Panel.oranda.panel.testFake", panel.Title);
        Assert.Equal(DockLocation.Bottom, panel.DefaultLocation);
        Assert.Null(panel.DefaultSize);
    }

    [Fact]
    public async Task Command_handler_is_collected_and_runs_through_registry()
    {
        var registry = new CommandRegistry(TestAssembly.Commands);

        Assert.Equal("Cmd.oranda.test.fakeRun", registry.Find("oranda.test.fakeRun")!.Title);
        Assert.Equal("oranda.test.fakeRun", registry.Find("oranda.test.oldFakeRun")!.Id);
        Assert.False(registry.CanExecute("oranda.test.fakeRun", FakeContextKeys.AllFalse));
        Assert.False(await registry.ExecuteAsync("oranda.test.fakeRun", FakeContextKeys.AllFalse));
        Assert.False(await registry.ExecuteAsync("oranda.test.unknown", FakeContextKeys.AllTrue));

        Assert.True(await registry.ExecuteAsync("oranda.test.oldFakeRun", FakeContextKeys.AllTrue));
        Assert.True(await registry.ExecuteAsync("oranda.test.fakeOther", FakeContextKeys.AllFalse));

        lock (FakeTestCommandHandler.Calls)
        {
            Assert.Contains("oranda.test.fakeRun", FakeTestCommandHandler.Calls);
            Assert.Contains("oranda.test.fakeOther", FakeTestCommandHandler.Calls);
        }
    }

    [Fact]
    public void Status_bar_item_is_collected()
    {
        var registry = new StatusBarRegistry(TestAssembly.StatusBarItems);

        var item = Assert.Single(registry.Items, i => i.Id == "oranda.status.testFake");
        Assert.Equal(5, item.CollapseRank);
        Assert.Equal(typeof(FakeTestStatusBarItem), item.ItemType);
    }

    [Fact]
    public void Panels_load_in_location_then_order_then_id()
    {
        var defs = new[]
        {
            Panel("oranda.panel.c", DockLocation.Right, 1),
            Panel("oranda.panel.b", DockLocation.Left, 20),
            Panel("oranda.panel.a", DockLocation.Left, 20),
            Panel("oranda.panel.d", DockLocation.Left, 10),
            Panel("oranda.panel.e", DockLocation.Document, 50),
        };

        var ids = new ToolPanelRegistry(defs, includeDevBuildOnly: false).Panels.Select(p => p.Id);

        Assert.Equal(["oranda.panel.e", "oranda.panel.d", "oranda.panel.a", "oranda.panel.b", "oranda.panel.c"], ids);
    }

    [Fact]
    public void Status_bar_items_load_in_order()
    {
        var items = new[]
        {
            new StatusBarItemDefinition("oranda.status.b", 2, 0, typeof(FakeTestStatusBarItem)),
            new StatusBarItemDefinition("oranda.status.a", 1, 0, typeof(FakeTestStatusBarItem)),
        };

        Assert.Equal(["oranda.status.a", "oranda.status.b"], new StatusBarRegistry(items).Items.Select(i => i.Id));
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var p = Panel("oranda.panel.dup", DockLocation.Left, 0);
        Assert.Throws<InvalidOperationException>(() => new ToolPanelRegistry([p, p], includeDevBuildOnly: false));

        var registry = new CommandRegistry(TestAssembly.Commands);
        var clash = new CommandDefinition { Id = "oranda.test.oldFakeRun", Title = "t", Category = CommandCategory.Tools };
        Assert.Throws<InvalidOperationException>(() => registry.Register(clash, new FakeTestCommandHandler()));
    }

    [Fact]
    public void Dev_build_only_panels_are_skipped_in_release()
    {
        var dev = Panel("oranda.panel.dev", DockLocation.Right, 0) with { DevBuildOnly = true };

        Assert.Empty(new ToolPanelRegistry([dev], includeDevBuildOnly: false).Panels);
        Assert.Single(new ToolPanelRegistry([dev], includeDevBuildOnly: true).Panels);
    }

    [Fact]
    public void Runtime_registration_can_be_undone()
    {
        var registry = new ToolPanelRegistry([], includeDevBuildOnly: false);
        var changes = 0;
        registry.PanelsChanged += (_, _) => changes++;

        var handle = registry.Register(Panel("oranda.panel.ext", DockLocation.Right, 0));
        Assert.NotNull(registry.Find("oranda.panel.ext"));

        handle.Dispose();
        handle.Dispose();
        Assert.Null(registry.Find("oranda.panel.ext"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void App_panels_follow_contract()
    {
        var contributions = ShellTestHelpers.AppContributions;
        _ = ShellTestHelpers.CreatePanelRegistry();
        _ = ShellTestHelpers.CreateCommandRegistry();
        Assert.NotEmpty(contributions.Panels);

        foreach (var panel in contributions.Panels)
        {
            Assert.Matches(PanelId(), panel.Id);
            Assert.True(typeof(ToolPanelViewModelBase).IsAssignableFrom(panel.ViewModelType), panel.Id);

            var viewName = panel.ViewModelType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            var view = panel.ViewModelType.Assembly.GetType(viewName);
            Assert.True(view is not null && typeof(Control).IsAssignableFrom(view), $"{panel.Id}: View '{viewName}' 가 없다.");
            Assert.Equal(panel.Kind == ToolPanelKind.Document, panel.DefaultLocation == DockLocation.Document);
        }
    }

    private static ToolPanelDefinition Panel(string id, DockLocation location, int order) => new()
    {
        Id = id,
        Title = "Panel." + id,
        Kind = location == DockLocation.Document ? ToolPanelKind.Document : ToolPanelKind.Tool,
        DefaultLocation = location,
        ViewModelType = typeof(FakeTestPanelViewModel),
        Order = order,
    };

    [GeneratedRegex("^oranda\\.panel\\.[a-z][A-Za-z0-9]*$")]
    private static partial Regex PanelId();
}
