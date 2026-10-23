using System.Text;
using Avalonia.Threading;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.PlsqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 파일 열기(B-3 b1): 확장자 분기, 같은 파일 다시 열기, 같은 경로 SQL 탭, 고치지 않은 저장의 바이트 보존,
/// 컴파일 보완(끝 <c>/</c>·SHOW ERRORS 떼기, 단위가 여러 개인 파일 거부).
/// </summary>
[Collection(PlsqlStatics.Name)]
public sealed class PlsqlFilesTests : IDisposable
{
    private const string Proc = "CREATE OR REPLACE PROCEDURE p IS\nBEGIN\n  NULL;\nEND;";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-plsql-files-" + Guid.NewGuid().ToString("N"));

    public PlsqlFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        PlsqlEditorViewModel.Active = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("a.pks", true)]
    [InlineData("a.PKB", true)]
    [InlineData("a.pls", true)]
    [InlineData("/x/y/a.plb", true)]
    [InlineData("a.prc", true)]
    [InlineData("a.Fnc", true)]
    [InlineData("a.trg", true)]
    [InlineData("a.pck", false)]
    [InlineData("a.sql", false)]
    [InlineData("a.txt", false)]
    [InlineData("pkb", false)]
    public void Only_the_plsql_source_extensions_open_as_plsql(string path, bool expected) =>
        Assert.Equal(expected, PlsqlFiles.IsPlsqlFile(path));

    [Fact]
    public Task File_open_sends_plsql_extensions_to_a_workspace_tab_and_the_rest_to_sql_tabs() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var host = SqlEditorHost.Current;
        var pkb = Write("emp.pkb", Proc + "\n/\n");
        var sql = Write("q.sql", "SELECT 1 FROM dual;\n");
        var pck = Write("old.pck", Proc + "\n/\n");

        await registry.ExecuteAsync("oranda.file.open", keys, pkb);
        Dispatcher.UIThread.RunJobs();

        var workspace = Assert.Single(h.Get<Oranda.App.Shell.Docking.DockWorkspace>().Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>());
        var tab = Assert.Single(workspace.Tabs);
        Assert.Equal(pkb, tab.Editor.FilePath);
        Assert.Equal("emp.pkb", tab.Title);
        Assert.False(tab.Editor.IsDirty);
        Assert.False(tab.IsPreviewTab);
        Assert.Null(tab.Target); // 컴파일 때 머리글로 정한다
        Assert.Equal(Proc + "\n/\n", tab.Editor.Text);
        Assert.Empty(host.Tabs);

        await registry.ExecuteAsync("oranda.file.open", keys, sql);
        await registry.ExecuteAsync("oranda.file.open", keys, pck);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([sql, pck], host.Tabs.Select(t => t.FilePath));
        Assert.Single(workspace.Tabs); // .sql·.pck 는 SQL 탭이다
    });

    [Fact]
    public Task Opening_the_same_file_again_shows_the_open_tab_and_a_sql_tab_on_the_same_path_wins() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var host = SqlEditorHost.Current;
        await h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single().RunAsync(default); // SQL 작업 공간 ↔ 문서 연결
        var one = Write("one.pkb", Proc + "\n");
        var two = Write("two.pks", Proc + "\n");

        await registry.ExecuteAsync("oranda.file.open", keys, one);
        await registry.ExecuteAsync("oranda.file.open", keys, two);
        var workspace = Assert.Single(h.Get<Oranda.App.Shell.Docking.DockWorkspace>().Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>());
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Same(workspace.Tabs[1], workspace.ActiveTab);

        await registry.ExecuteAsync("oranda.file.open", keys, one);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, workspace.Tabs.Count); // 새 탭 없음
        Assert.Same(workspace.Tabs[0], workspace.ActiveTab);

        // 같은 경로의 SQL 탭이 이미 있으면 PL/SQL 로 열지 않고 그 SQL 탭을 앞으로 낸다.
        var three = Write("three.pkb", Proc + "\n");
        var sqlTab = await host.OpenFileAsync(three, default);
        Assert.NotNull(sqlTab);
        var docking = h.Get<Oranda.App.Shell.Docking.DockWorkspace>();
        await registry.ExecuteAsync("oranda.file.open", keys, one); // PL/SQL 문서를 앞으로 (SQL 탭은 여전히 SQL 작업 공간의 활성 탭)
        Dispatcher.UIThread.RunJobs();
        Assert.Same(workspace, docking.ActiveDocument!.Panel);
        Assert.Same(sqlTab, host.Active);

        await registry.ExecuteAsync("oranda.file.open", keys, three);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Same(sqlTab, host.Active);
        Assert.Same(host.WorkspaceOf(sqlTab), docking.ActiveDocument!.Panel); // 활성 탭이 그대로여도 SQL 문서가 앞으로 나온다
        Assert.Single(host.Tabs);
    });

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-8")]
    [InlineData("cp949")]
    public Task Saving_an_untouched_file_tab_keeps_the_original_bytes(string encoding) => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var host = SqlEditorHost.Current;
        var text = "CREATE OR REPLACE PROCEDURE p IS\r\nBEGIN\r\n  -- 한글 주석\r\n  NULL;\r\nEND;\r\n/\r\n";
        var bytes = encoding switch
        {
            "utf-8-bom" => [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(text)],
            "cp949" => FileEncoding.Encode(text, "cp949"),
            _ => Encoding.UTF8.GetBytes(text),
        };
        var path = Path.Combine(_dir, "enc-" + encoding + ".pkb");
        await File.WriteAllBytesAsync(path, bytes);

        Assert.True(await PlsqlFiles.OpenAsync(h.Services, host, path, null, default));
        var workspace = Assert.Single(h.Get<Oranda.App.Shell.Docking.DockWorkspace>().Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>());
        var tab = Assert.Single(workspace.Tabs);
        Assert.Equal(encoding, tab.Editor.Encoding);

        Assert.True(await host.SaveAsync(tab.Editor, default));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    });

    [Fact]
    public async Task A_file_tab_compiles_the_first_unit_without_the_trailing_slash_and_show_errors()
    {
        foreach (var tail in new[] { "\n/\nSHOW ERRORS\n", "\n/\nshow err;\n", "\n/\n\nSHO ERR\n/\n", "\n/\nSho Errors;\n-- 끝\n/* x */\n", "\nSHOW ERRORS\n", "\n/\n", "\n/\nSHOW ERRORS PACKAGE BODY pk;\n", "\n/\nsho erro\n", "\n/\nSHOW ERR PROCEDURE p\n/\n" })
        {
            var exec = new PlsqlEditorTests.FakeExec();
            var vm = new PlsqlEditorViewModel(new PlsqlEditorTests.FakeMeta(), exec);
            vm.StartFile(Path.Combine(_dir, "x.pkb"), Proc + tail, "utf-8", new PlsqlEditorTests.FakeSession());

            var outcome = await vm.CompileAsync();

            Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
            Assert.Equal(Proc, Assert.Single(exec.Statements).Text);
            Assert.Equal("x.pkb", vm.Title); // 파일 탭은 파일 이름을 그대로 둔다
            Assert.False(vm.Editor.IsDirty);
            Assert.Equal("SCOTT.P", $"{vm.Target!.Owner}.{vm.Target.Name}");
        }
    }

    [Fact]
    public async Task Compiling_a_file_tab_does_not_clear_its_unsaved_mark()
    {
        var vm = new PlsqlEditorViewModel(new PlsqlEditorTests.FakeMeta(), new PlsqlEditorTests.FakeExec());
        vm.StartFile(Path.Combine(_dir, "x.pkb"), Proc + "\n/\n", "utf-8", new PlsqlEditorTests.FakeSession());
        vm.Editor.Document.Insert(0, "-- 고침\n");
        Assert.True(vm.Editor.IsDirty);

        Assert.Equal(PlsqlCompileStatus.Compiled, (await vm.CompileAsync()).Status);

        Assert.True(vm.Editor.IsDirty); // 파일에는 쓰지 않았다
        Assert.True(vm.NeedsSaveConfirm);
    }

    [Theory]
    [InlineData("\n/\nGRANT EXECUTE ON p TO scott;\n/\n")]
    [InlineData("\n/\nGRANT EXECUTE ON p TO scott;\n")]
    [InlineData("\n/\nCREATE OR REPLACE PROCEDURE q IS BEGIN NULL; END;\n/\n")]
    [InlineData("\n/\n/* 설명 */ GRANT EXECUTE ON p TO scott;\n")]
    public async Task A_file_with_more_than_one_unit_is_not_compiled(string tail)
    {
        var exec = new PlsqlEditorTests.FakeExec();
        var vm = new PlsqlEditorViewModel(new PlsqlEditorTests.FakeMeta(), exec);
        vm.StartFile(Path.Combine(_dir, "x.pkb"), Proc + tail, "utf-8", new PlsqlEditorTests.FakeSession());

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.NothingToCompile, outcome.Status);
        Assert.Equal("단위가 여러 개인 파일은 컴파일하지 않는다 — SQL 탭에서 스크립트로 실행한다", outcome.Message);
        Assert.Empty(exec.Statements);
    }

    [Fact]
    public void Split_compile_unit_cuts_at_the_lone_slash_and_ignores_slashes_inside_block_comments()
    {
        var withComment = "CREATE OR REPLACE PROCEDURE p IS\n/* 메모\n/\n*/\nBEGIN NULL; END;\n/\nSHOW ERRORS\n";
        var (unit, more) = PlsqlSourceText.SplitCompileUnit(withComment);
        Assert.False(more);
        Assert.Equal("CREATE OR REPLACE PROCEDURE p IS\n/* 메모\n/\n*/\nBEGIN NULL; END;", unit);

        // 슬래시가 없으면 끝까지 한 단위(본문 안 ; 로 자르지 않는다), CRLF 는 LF 로.
        var (whole, wholeMore) = PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS\r\nBEGIN\r\n  x := 4\r\n/ 2;\r\n  grant := 1;\r\n  grant(1);\r\nEND;\r\n");
        Assert.False(wholeMore); // 나눗셈 줄·변수 이름 grant 는 다른 문장이 아니다
        Assert.Equal("CREATE PROCEDURE p IS\nBEGIN\n  x := 4\n/ 2;\n  grant := 1;\n  grant(1);\nEND;", whole);

        // 슬래시가 없는데 줄 머리에 GRANT·CREATE·REVOKE 문장이 이어지면 다른 문장이다(리터럴·주석 안은 제외).
        Assert.True(PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS BEGIN NULL; END;\r\nGRANT x TO y;\r\n").HasMore);
        Assert.True(PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS BEGIN NULL; END;\nCREATE PROCEDURE q IS BEGIN NULL; END;").HasMore);
        Assert.True(PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS BEGIN NULL; END;\n  revoke x FROM y").HasMore);
        Assert.False(PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS\nBEGIN\n  s := 'x\nGRANT a TO b';\n  /* \nGRANT z\n*/\n  -- GRANT a\nEND;").HasMore);

        var (_, extra) = PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS BEGIN NULL; END;\n/\nGRANT x TO y;");
        Assert.True(extra);
    }

    [Fact]
    public void Slashes_inside_literals_and_comments_are_not_unit_boundaries()
    {
        // '/*' 문자열 뒤의 단독 / 줄은 경계다.
        var (unit, more) = PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS\nBEGIN\n  v := '/*';\nEND;\n/\nSHOW ERRORS\n");
        Assert.Equal("CREATE PROCEDURE p IS\nBEGIN\n  v := '/*';\nEND;", unit);
        Assert.False(more);

        // '--' 문자열 뒤의 진짜 /* 는 주석이라 그 안의 단독 / 는 경계가 아니다.
        (unit, more) = PlsqlSourceText.SplitCompileUnit("CREATE PROCEDURE p IS\nBEGIN\n  v := '--'; /* 메모\n/\n*/\nEND;\n/\n");
        Assert.Equal("CREATE PROCEDURE p IS\nBEGIN\n  v := '--'; /* 메모\n/\n*/\nEND;", unit);
        Assert.False(more);

        // 여러 줄 문자열·'' 이스케이프·q'[…]' 와 임의 구분자, -- 줄 주석 안의 /*.
        foreach (var body in new[]
        {
            "  v := 'a\n/\nb';",
            "  v := 'it''s\n/\n';",
            "  v := q'[ 그 /* \n/\n ]';",
            "  v := q'!x\n/\n!';",
            "  v := nq'{x\n/\n}';",
            "  -- /*\n  v := 1;",
        })
        {
            var text = "CREATE PROCEDURE p IS\nBEGIN\n" + body + "\nEND;\n/\n";
            var (u, m) = PlsqlSourceText.SplitCompileUnit(text);
            Assert.Equal(text[..^3], u);
            Assert.False(m);
        }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}
