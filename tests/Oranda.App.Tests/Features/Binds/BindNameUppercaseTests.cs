using Oranda.App.Features.Binds;
using Oranda.Core.Execution;
using Oranda.Core.Workspaces;

namespace Oranda.App.Tests.Features.Binds;

// 소유: TSK-04-12. 바인드 목록 이름은 대문자로 둔다(큰따옴표 이름은 그대로).

public sealed class BindNameUppercaseTests
{
    [Fact]
    public void Names_from_execution_restore_and_editing_become_uppercase_and_still_bind()
    {
        var store = new BindVariableStore();

        Assert.Equal(["id"], store.Ensure(["id", "ID"]));
        Assert.Equal("ID", Assert.Single(store.Rows).Name);

        store.Restore([new WorkspaceBind { Name = "dept_no", Type = BindDataType.Number, Direction = BindDirection.In, Value = "10" }]);
        Assert.Equal("DEPT_NO", store.Rows[1].Name);
        Assert.Equal("10", store.Rows[1].Text);

        var typed = store.Add("x");
        typed.Name = "from_dt";
        Assert.Equal("FROM_DT", typed.Name);

        store.Rows[0].Text = "7";
        Assert.True(store.TryBuild(["id", "dept_no", "from_dt"], out var values, out var error), error);
        Assert.Equal("7", values["id"].Value);
        Assert.Equal(10m, values["DEPT_NO"].Value);
    }

    [Fact]
    public void Prune_drops_names_no_longer_in_the_text_and_keeps_values_of_the_rest()
    {
        var store = new BindVariableStore();
        store.Ensure(["emp", "emp_no"]);
        store.Rows[1].Text = "7839";

        Assert.True(store.Prune(["EMP_NO"]));
        var row = Assert.Single(store.Rows);
        Assert.Equal(("EMP_NO", "7839", 1), (row.Name, row.Text, row.Number));
        Assert.False(store.Prune(["emp_no"]));
    }

    [Fact]
    public void Quoted_names_keep_their_case()
    {
        var row = new BindVariableRow("\"MixedCase\"");
        Assert.Equal("\"MixedCase\"", row.Name);
        row.Name = "\"lower\"";
        Assert.Equal("\"lower\"", row.Name);
    }
}
