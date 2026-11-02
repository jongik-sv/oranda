using Oranda.App.Features.Binds;
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
        var panel = BindVariablesViewModel.ForHost(null, host);
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

    [Fact]
    public Task Text_tab_opened_for_a_source_tab_copies_the_binds_it_references() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var source = host.NewDocument("select :id, :other from dual");
        SetBind(source.Binds, "ID", "5", BindDataType.Number);
        SetBind(source.Binds, "OTHER", "x");

        // 새 탭 문장이 참조하는 이름 중 원본에 줄이 있는 것(ID)만 가져온다: 원본에만 있는 OTHER 도, 원본에 없는 EXTRA 의 빈 줄도 만들지 않는다
        // (빈 줄이 미리 생기면 「값 없이 실행 금지」 확인을 건너뛰게 된다).
        var opened = host.OpenTextTabWithBinds("select :id, :extra from dual", source.TabId);

        Assert.NotSame(source.Binds, opened.Binds);
        Assert.Equal([("ID", "5", BindDataType.Number, BindDirection.In)], RowsOf(opened.Binds));
        Assert.Equal(2, RowsOf(source.Binds).Length); // 원본은 그대로
        return Task.CompletedTask;
    });

    [Fact]
    public Task Text_tab_opened_after_the_source_tab_closed_gets_no_binds() => Ui.Run(() =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs { Confirm = true });
        host.NewDocument("select 1 from dual");
        var source = host.NewDocument("select :id from dual");
        SetBind(source.Binds, "ID", "5", BindDataType.Number);
        var sourceId = source.TabId;
        host.Close(source);

        var opened = host.OpenTextTabWithBinds("select :id from dual", sourceId);

        Assert.Empty(opened.Binds.Rows);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Copying_binds_to_a_tab_after_the_source_closed_does_nothing() => Ui.Run(() =>
    {
        var host = new SqlEditorHost(dialogs: new FakeDialogs { Confirm = true });
        host.NewDocument("select 1 from dual");
        var source = host.NewDocument("select :id from dual");
        source.Binds.Add("ID").Text = "5";
        var target = host.NewDocument("select :id from dual");
        host.CopyBinds(source.TabId, target.TabId);
        Assert.Equal("5", Assert.Single(target.Binds.Rows).Text);

        var other = host.NewDocument("select :id from dual");
        var closed = source.TabId;
        host.Close(source);
        host.CopyBinds(closed, other.TabId);
        Assert.Empty(other.Binds.Rows);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Copying_binds_again_keeps_the_values_the_user_already_set_in_the_target() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var source = host.NewDocument("select :id, :name from dual");
        SetBind(source.Binds, "ID", "5", BindDataType.Number);
        SetBind(source.Binds, "NAME", "scott");
        var target = host.NewDocument("select :id, :name from dual");
        SetBind(target.Binds, "ID", "99", BindDataType.Number); // 사용자가 (튜닝) 탭에서 직접 고친 값

        host.CopyBinds(source.TabId, target.TabId);

        // 있던 줄은 그대로(값·형식 보존), 없던 줄만 원본에서 가져온다.
        Assert.Equal(
            [("ID", "99", BindDataType.Number, BindDirection.In), ("NAME", "scott", BindDataType.Varchar2, BindDirection.In)],
            RowsOf(target.Binds));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Copying_binds_skips_empty_and_null_source_values_and_fills_empty_target_rows() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var source = host.NewDocument("select :a, :b, :c from dual");
        SetBind(source.Binds, "A", "1", BindDataType.Number);
        SetBind(source.Binds, "B", ""); // 가드가 만든 빈 줄·명시적 NULL
        var target = host.NewDocument("select :a, :b, :c from dual");
        SetBind(target.Binds, "A", ""); // 받는 탭의 빈 줄은 채운다

        host.CopyBinds(source.TabId, target.TabId);

        // B(원본이 빈 값)와 C(원본에 줄 없음)는 만들지 않는다: 새 탭에서 「값 없이 실행 금지」 확인이 다시 묻는다.
        Assert.Equal([("A", "1", BindDataType.Number, BindDirection.In)], RowsOf(target.Binds));
        return Task.CompletedTask;
    });
}
