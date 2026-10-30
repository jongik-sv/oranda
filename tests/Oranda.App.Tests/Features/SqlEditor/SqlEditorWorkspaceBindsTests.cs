using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Workspaces;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>작업 공간 파일(.workspace.md)에 바인드 변수 값을 저장하고 다시 열면 바인드 변수 창에 채운다.</summary>
public sealed partial class SqlEditorTests
{
    [Fact]
    public Task Saving_a_workspace_file_keeps_each_tabs_bind_values_and_opening_it_restores_them() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "바인드.workspace.md");
        var host = new SqlEditorHost(NewStore(), new FakeDialogs { SavePath = path });
        var emp = host.NewDocument("select * from emp where empno = :EMPNO and pwd = :PASSWORD");
        emp.Binds.Restore([
            new WorkspaceBind { Name = "EMPNO", Type = BindDataType.Number, Value = "7369" },
            new WorkspaceBind { Name = "PASSWORD", Value = "tiger" },
        ]);
        host.NewDocument("select * from dept where deptno = :D").Binds.Restore([new WorkspaceBind { Name = "D", Value = "10" }]);

        Assert.True(await host.SaveWorkspaceFileAsync(askPath: true, default));
        var md = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("tiger", md, StringComparison.Ordinal); // 비밀로 보이는 이름은 남기지 않는다

        var other = new SqlEditorHost();
        await other.OpenWorkspaceFileAsync(path, default);

        var tabs = other.ActiveWorkspace!.Tabs;
        Assert.Equal("select * from emp where empno = :EMPNO and pwd = :PASSWORD", tabs[^2].Text);
        var empRow = Assert.Single(tabs[^2].Binds.Rows);
        Assert.Equal(("EMPNO", "7369", BindDataType.Number), (empRow.Name.Trim(), empRow.Text, empRow.Type));
        Assert.Equal("10", Assert.Single(tabs[^1].Binds.Rows).Text);
    });
}
