using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>탭별 바인드 목록(TSK-04-11): 탭마다 따로 두고, 바인드 변수 창이 활성 탭을 따라가며, 저장·복원이 섞이지 않는다.</summary>
public sealed partial class SqlEditorTests
{
    [Fact]
    public Task Each_tab_keeps_its_own_bind_list_even_for_the_same_name() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument("select :id from dual");
        var b = host.NewDocument("select :id from dual");
        SetBind(a.Binds, "ID", "1", BindDataType.Number);
        SetBind(b.Binds, "ID", "2");

        Assert.NotSame(a.Binds, b.Binds);
        Assert.Equal([("ID", "1", BindDataType.Number, BindDirection.In)], RowsOf(a.Binds));
        Assert.Equal([("ID", "2", BindDataType.Varchar2, BindDirection.In)], RowsOf(b.Binds));
        Assert.Same(a.Binds, BindVariableStore.ForTab(a, null));
        Assert.Same(BindVariableStore.For(null), BindVariableStore.ForTab(null, null));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Bind_panel_shows_the_active_tab_list_and_follows_tab_switches() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var panel = new BindVariablesViewModel(null, host);
        Assert.Same(BindVariableStore.For(null), panel.Store); // 탭이 없으면 도구 창용 기본 목록

        var a = host.NewDocument("select :x from dual");
        var b = host.NewDocument("select :y from dual");
        SetBind(a.Binds, "X", "ax");
        SetBind(b.Binds, "Y", "by");
        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        host.Active = a;
        Assert.Same(a.Binds, panel.Store);
        Assert.Equal(["X"], panel.Rows.Select(r => r.Name));

        host.Active = b;
        Assert.Same(b.Binds, panel.Store);
        Assert.Equal(["Y"], panel.Rows.Select(r => r.Name));
        Assert.Contains(nameof(BindVariablesViewModel.Rows), changed);
        Assert.Null(panel.Selected);

        // 창에서 더한 줄은 활성 탭의 목록에만 들어간다.
        panel.AddCommand.Execute(null);
        Assert.Equal(2, b.Binds.Rows.Count);
        Assert.Single(a.Binds.Rows);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Workspace_round_trip_keeps_each_tabs_binds_apart() => Ui.Run(async () =>
    {
        var store = NewStore();
        var host = new SqlEditorHost(store);
        var a = host.NewDocument("select :id from dual");
        var b = host.NewDocument("select :id, :name from dual");
        SetBind(a.Binds, "ID", "1", BindDataType.Number);
        SetBind(b.Binds, "ID", "2");
        SetBind(b.Binds, "NAME", "scott");
        await host.ShutdownAsync(default);

        var run2 = new SqlEditorHost(NewStore());
        await run2.InitializeAsync(default);

        Assert.Equal(2, run2.Tabs.Count);
        Assert.Equal([("ID", "1", BindDataType.Number, BindDirection.In)], RowsOf(run2.Tabs[0].Binds));
        Assert.Equal(
            [("ID", "2", BindDataType.Varchar2, BindDirection.In), ("NAME", "scott", BindDataType.Varchar2, BindDirection.In)],
            RowsOf(run2.Tabs[1].Binds));
    });

    [Fact]
    public Task Reopening_a_closed_tab_brings_back_its_binds() => Ui.Run(() =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs { Confirm = true });
        host.NewDocument("select 1 from dual");
        var tab = host.NewDocument("select :v from dual");
        SetBind(tab.Binds, "V", "7", BindDataType.Number);

        host.Close(tab);
        var back = host.ReopenClosed()!;

        Assert.NotSame(tab, back);
        Assert.Equal([("V", "7", BindDataType.Number, BindDirection.In)], RowsOf(back.Binds));
        return Task.CompletedTask;
    });
}
