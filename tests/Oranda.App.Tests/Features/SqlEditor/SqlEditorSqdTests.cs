using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>TSK-04-10: Orange .sqd 를 작업 공간으로 열기(문서 탭·SQL 탭·바인드·읽기 전용·다시 열기·끌어 놓기).</summary>
public sealed partial class SqlEditorTests
{
    private string WriteSqd(string name, params string[] workspaceLines)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, WorkspaceFilesSqdTests.Sqd(WorkspaceFilesSqdTests.WithHeader(workspaceLines)));
        return path;
    }

    [Fact]
    public Task Opening_a_sqd_creates_a_workspace_with_one_sql_tab_per_statement_and_fills_the_binds() => Ui.Run(async () =>
    {
        var sqd = WriteSqd(
            "월간 작업.sqd",
            "COUNT=2",
            "NAME=사원 조회", "STMT=2", "select * from emp", " where empno = :EMPNO and ename like :ENAME", "VAR=2", ":EMPNO=0,7369", ":ENAME=0,",
            "NAME=부서", "STMT=1", "select * from dept where deptno = :D", "VAR=1", ":D=0,10,20");
        var host = new SqlEditorHost();
        host.NewDocument("select 0 from dual");

        var file = await host.OpenWorkspaceFileAsync(sqd, default);

        var ws = host.ActiveWorkspace!;
        Assert.Equal(2, host.Workspaces.Count);
        Assert.Equal("월간 작업", ws.Name);
        Assert.Equal(Path.GetFullPath(sqd), ws.SourcePath);
        Assert.Equal(["사원 조회", "부서"], ws.Tabs.Select(t => t.Title));
        Assert.Equal("select * from emp\n where empno = :EMPNO and ename like :ENAME", ws.Tabs[0].Text.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Same(ws.Tabs[0], host.Active);
        Assert.Empty(file!.Notes);

        var rows = host.Binds.Rows.ToDictionary(r => r.Name.Trim(), r => r.Text, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("7369", rows["EMPNO"]);
        Assert.Equal(string.Empty, rows["ENAME"]);
        Assert.Equal("10,20", rows["D"]);
    });

    [Fact]
    public Task A_sqd_workspace_is_read_only_saving_asks_for_a_markdown_name_and_leaves_the_sqd_unchanged() => Ui.Run(async () =>
    {
        var sqd = WriteSqd("원본.sqd", "COUNT=1", "NAME=a", "STMT=1", "select 1 from dual", "VAR=0");
        var before = File.ReadAllBytes(sqd);
        var md = Path.Combine(_root, "복사본.md");
        var dialogs = new FakeDialogs { SavePath = md };
        var host = new SqlEditorHost(NewStore(), dialogs);
        await host.OpenWorkspaceFileAsync(sqd, default);

        Assert.Null(host.WorkspaceFilePath);
        Assert.True(await host.SaveWorkspaceFileAsync(askPath: false, default));

        Assert.Equal(1, dialogs.SaveCalls);
        Assert.Equal(before, File.ReadAllBytes(sqd));
        Assert.Contains("select 1 from dual", await File.ReadAllTextAsync(md), StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(md), host.WorkspaceFilePath);
    });

    [Fact]
    public Task Opening_the_same_sqd_again_moves_to_the_existing_workspace() => Ui.Run(async () =>
    {
        var sqd = WriteSqd("같은 파일.sqd", "COUNT=1", "NAME=a", "STMT=1", "select 1 from dual", "VAR=0");
        var host = new SqlEditorHost();
        await host.OpenWorkspaceFileAsync(sqd, default);
        var first = host.ActiveWorkspace!;
        host.NewWorkspace();
        Assert.NotSame(first, host.ActiveWorkspace);
        var count = host.Workspaces.Count;

        await host.OpenWorkspaceFileAsync(sqd, default);

        Assert.Equal(count, host.Workspaces.Count);
        Assert.Same(first, host.ActiveWorkspace);
    });

    [Fact]
    public Task A_broken_sqd_opens_what_it_can_and_shows_the_notes_and_a_non_sqd_file_is_a_message_not_a_crash() => Ui.Run(async () =>
    {
        var broken = WriteSqd("깨짐.sqd", "COUNT=3", "NAME=a", "STMT=1", "select 1 from dual", "VAR=0", "NAME=b", "STMT=9", "select 2 from dual");
        Directory.CreateDirectory(_root);
        var junk = Path.Combine(_root, "아님.sqd");
        await File.WriteAllTextAsync(junk, "이건 작업 공간이 아니다");
        var dialogs = new FakeDialogs();
        var host = new SqlEditorHost(NewStore(), dialogs);

        await SqlEditorCommandHandler.OpenWorkspaceFileAsync(host, broken, default);
        Assert.Equal(["a", "b"], host.ActiveWorkspace!.Tabs.Select(t => t.Title));
        Assert.Single(dialogs.Messages);
        Assert.Contains("COUNT", dialogs.Messages[0], StringComparison.Ordinal);

        await SqlEditorCommandHandler.OpenWorkspaceFileAsync(host, junk, default);
        Assert.Equal(2, dialogs.Messages.Count);
        Assert.Contains("열지 못했다", dialogs.Messages[1], StringComparison.Ordinal);
    });

    [Fact]
    public Task Dropped_or_command_line_paths_open_only_existing_sqd_files() => Ui.Run(async () =>
    {
        var a = WriteSqd("a.sqd", "COUNT=1", "NAME=x", "STMT=1", "select 1 from dual", "VAR=0");
        var b = WriteSqd("b.SQD", "COUNT=1", "NAME=y", "STMT=1", "select 2 from dual", "VAR=0");
        var other = Path.Combine(_root, "c.sql");
        await File.WriteAllTextAsync(other, "select 3 from dual");
        var host = new SqlEditorHost();

        Assert.Equal([Path.GetFullPath(a), Path.GetFullPath(b)], DroppedWorkspaceFiles.Pick([a, other, null, Path.Combine(_root, "없음.sqd"), b, a]));
        Assert.False(await DroppedWorkspaceFiles.OpenAsync(host, [other], default));
        Assert.Empty(host.Workspaces);

        Assert.True(await DroppedWorkspaceFiles.OpenAsync(host, [a, b], default));
        Assert.Equal(2, host.Workspaces.Count);
        Assert.Equal("b", host.ActiveWorkspace!.Name);
    });

    [Fact]
    public Task The_real_orange_sample_opens_as_19_sql_tabs() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost();

        var file = await host.OpenWorkspaceFileAsync(WorkspaceFilesSqdTests.SamplePath, default);

        Assert.Equal(19, host.ActiveWorkspace!.Tabs.Count);
        Assert.Equal("라인길이 계산 공정.sql", host.ActiveWorkspace.Tabs[0].Title);
        Assert.Empty(file!.Notes);
        Assert.Contains(host.Binds.Rows, r => r.Name.Trim() == "WORD_DH" && r.Text == "2022-07-12");
    });
}
