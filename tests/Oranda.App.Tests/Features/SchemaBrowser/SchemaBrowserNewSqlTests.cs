using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>TSK-13-29 표·뷰 우클릭 「새 SQL」: SELECT 가 채워진 새 SQL 탭.</summary>
public sealed class SchemaBrowserNewSqlTests
{
    private static async Task<(SchemaBrowserTests.Harness H, SqlEditorHost Host, SchemaNode Tables)> NewAsync()
    {
        var h = new SchemaBrowserTests.Harness();
        var host = new SqlEditorHost();
        h.Vm.EditorHost = () => host;
        await h.Vm.SetSessionAsync(h.Session);
        var tables = h.Vm.Roots[1].Children[0];
        await h.Vm.ExpandAsync(tables);
        return (h, host, tables);
    }

    [Fact]
    public Task New_sql_opens_a_tab_with_select_even_without_a_sql_document() => Ui.Run(async () =>
    {
        var (h, host, tables) = await NewAsync();
        Assert.Empty(host.Tabs);
        var emp = tables.Children.First(c => c.Title == "EMP");

        Assert.True(await h.Vm.NewSqlWithSelectAsync(emp));

        var tab = Assert.Single(host.Tabs);
        Assert.Same(tab, host.Active);
        Assert.StartsWith("SELECT EMPNO", tab.Text, StringComparison.Ordinal);
        Assert.Equal(await h.Vm.BuildTextAsync(emp, SchemaInsertMode.Select), tab.Text); // 「SELECT 문 넣기」와 같은 글
        Assert.Equal(h.Session.Profile.Id, tab.ConnectionProfileId);
        Assert.Equal(tab.Text.Length, tab.Document.TextLength);
        Assert.False(tab.IsDirty);
        Assert.Empty(h.Editor.Edits); // 열려 있던 편집기를 건드리지 않는다
    });

    [Fact]
    public Task New_sql_from_a_column_node_uses_its_table_and_a_non_table_node_opens_nothing() => Ui.Run(async () =>
    {
        var (h, host, tables) = await NewAsync();
        var emp = tables.Children.First(c => c.Title == "EMP");
        await h.Vm.ExpandAsync(emp);
        var handler = new SchemaBrowserCommandHandler(h.Vm);

        var sub = emp.Children.First(); // 표 아래 폴더(열 등)에서도 그 표로 연다
        await handler.ExecuteAsync(new CommandContext("oranda.schema.newSqlWithSelect", FakeContextKeys.AllTrue, sub), CancellationToken.None);
        Assert.Contains("EMP", Assert.Single(host.Tabs).Text, StringComparison.Ordinal);

        SchemaNode? proc = null;
        foreach (var category in h.Vm.Roots[1].Children)
        {
            await h.Vm.ExpandAsync(category);
            proc ??= category.Children.FirstOrDefault(c => c.Title == "PROC1");
        }

        Assert.NotNull(proc);
        Assert.False(await h.Vm.NewSqlWithSelectAsync(proc));
        Assert.Single(host.Tabs);
    });
}
