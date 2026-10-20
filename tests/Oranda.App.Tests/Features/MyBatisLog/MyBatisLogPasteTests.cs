using Oranda.App.Features.Execution;
using Oranda.App.Features.MyBatisLog;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.MyBatisLog;

public class MyBatisLogPasteTests
{
    private const string Log = "DEBUG ==>  Preparing: SELECT * FROM emp WHERE id = ? AND name = ?\nDEBUG ==> Parameters: 7(Integer), a, b(String)\n";

    [Fact]
    public void Inserts_sql_at_caret_and_fills_bind_window()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "-- head\n");
        vm.Select(vm.Document.TextLength, 0);
        var binds = new BindVariableStore();

        var r = MyBatisLogPaste.Apply(vm, binds, Log);

        Assert.Equal(new MyBatisLogPasteResult(1, 2), r);
        Assert.Equal("-- head\nSELECT * FROM emp WHERE id = :p1 AND name = :p2", vm.Text);
        Assert.Equal(vm.Document.TextLength, vm.CaretOffset);
        var p1 = binds.Find("p1")!;
        Assert.Equal("7", p1.Text);
        Assert.Equal(BindDataType.Number, p1.Type);
        var p2 = binds.Find("p2")!;
        Assert.Equal("a, b", p2.Text);
        Assert.Equal(BindDataType.Varchar2, p2.Type);
    }

    [Fact]
    public void Several_pairs_become_terminated_statements_with_distinct_names()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), string.Empty);
        var binds = new BindVariableStore();
        var log = "==>  Preparing: DELETE FROM a WHERE x = ?\n==> Parameters: 1(Long)\n==>  Preparing: DELETE FROM b WHERE y = ?\n==> Parameters: null\n";

        var r = MyBatisLogPaste.Apply(vm, binds, log);

        Assert.Equal(new MyBatisLogPasteResult(2, 2), r);
        Assert.Equal("DELETE FROM a WHERE x = :p1;\n\nDELETE FROM b WHERE y = :p2;", vm.Text);
        Assert.Equal(string.Empty, binds.Find("p2")!.Text);
    }

    [Fact]
    public void Selection_is_replaced()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "xx OLD yy");
        vm.Select(3, 3);

        MyBatisLogPaste.Apply(vm, new BindVariableStore(), "==>  Preparing: SELECT 1 FROM dual\n==> Parameters: ");

        Assert.Equal("xx SELECT 1 FROM dual yy", vm.Text);
    }

    [Fact]
    public void Text_without_log_changes_nothing()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "keep");

        Assert.Null(MyBatisLogPaste.Apply(vm, new BindVariableStore(), "select 1"));
        Assert.Equal("keep", vm.Text);
    }
}
