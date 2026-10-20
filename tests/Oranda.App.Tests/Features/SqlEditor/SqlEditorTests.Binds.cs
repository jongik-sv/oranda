using System.Text;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Workspaces;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>
/// 바인드 변수 값 저장(EXEC-03): 탭이 참조하는 바인드의 값이 작업 공간(<c>tabs[].binds</c>)과 .sql 끝 블록에 함께 저장되고,
/// 다시 열면 바인드 변수 목록에 들어간다.
/// </summary>
public sealed partial class SqlEditorTests
{
    private const string EmpNoLine = "-- {\"name\":\"EMPNO\",\"type\":\"Number\",\"direction\":\"In\",\"value\":\"1002\",\"isNull\":false}";

    private static readonly string[] SecretNamesAndValues =
        ["P_PASSWORD", "API_TOKEN", "MY_SECRET", "tiger-value-1", "token-value-2", "secret-value-3"];

    private static BindVariableRow SetBind(
        BindVariableStore binds, string name, string text, BindDataType type = BindDataType.Varchar2, BindDirection direction = BindDirection.In)
    {
        var row = binds.Find(name) ?? binds.Add(name);
        row.Text = text;
        row.Type = type;
        row.Direction = direction;
        return row;
    }

    private static (string, string, BindDataType, BindDirection)[] RowsOf(BindVariableStore binds) =>
        [.. binds.Rows.Select(r => (r.Name, r.Text, r.Type, r.Direction))];

    // ---- 작업 공간 ---------------------------------------------------------------------------

    [Fact]
    public Task Workspace_keeps_bind_values_of_its_tabs_and_restore_puts_them_back() => Ui.Run(async () =>
    {
        var store = NewStore();
        var host = new SqlEditorHost(store);
        var tab = host.NewDocument("select * from emp where empno = :empno and hired > :since and note = :note and dept = :dept");
        SetBind(tab.Binds, "EMPNO", "1002", BindDataType.Number);
        SetBind(tab.Binds, "SINCE", string.Empty, BindDataType.Date); // NULL 날짜
        SetBind(tab.Binds, "NOTE", "a\nb */ 'c' & \"d\"");
        tab.Binds.Ensure(["DEPT"]); // 실행이 만들어 둔 빈 줄(빈 값·문자열·IN)은 남기지 않는다
        SetBind(tab.Binds, "UNUSED", "x"); // 탭이 참조하지 않는 이름도 남기지 않는다
        await host.ShutdownAsync(default);

        var json = await File.ReadAllTextAsync(Path.Combine(_root, "workspaces", "default.json"));
        Assert.Contains("\"binds\": [", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\": \"DEPT\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("UNUSED", json, StringComparison.Ordinal);

        // 복원하면 저장한 값·형식·NULL 이 그 탭의 목록으로 돌아온다.
        host.Restore((await store.LoadAsync("default", default))!.Value);

        Assert.Equal(
            [
                ("EMPNO", "1002", BindDataType.Number, BindDirection.In),
                ("SINCE", string.Empty, BindDataType.Date, BindDirection.In),
                ("NOTE", "a\nb */ 'c' & \"d\"", BindDataType.Varchar2, BindDirection.In),
            ],
            RowsOf(Assert.Single(host.Tabs).Binds));
    });

    [Fact]
    public Task Old_workspace_file_without_binds_still_restores() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(Path.Combine(_root, "workspaces"));
        await File.WriteAllTextAsync(Path.Combine(_root, "workspaces", "default.json"), """
            {
              "schemaVersion": 1,
              "id": "default",
              "name": "기본",
              "savedAt": "2026-10-19T09:30:12Z",
              "tabs": [ { "id": "7a8b9c0d-1e2f-4a3b-8c4d-5e6f7a8b9c0d", "title": "SQL 1", "content": "select :x from dual", "isDirty": true } ]
            }
            """);
        var host = new SqlEditorHost(NewStore(), new FakeDialogs());

        Assert.False(await host.InitializeAsync(default));

        Assert.Equal("select :x from dual", Assert.Single(host.Tabs).Text);
        Assert.Empty(host.Tabs[0].Binds.Rows);
    });

    [Fact]
    public Task Changing_only_a_bind_value_is_autosaved_and_recovery_brings_it_back() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(NewStore());
        var tab = host.NewDocument("select :x from dual");
        var x = SetBind(tab.Binds, "X", "1");
        Assert.True(await host.AutosaveAsync(default));
        Assert.False(await host.AutosaveAsync(default));

        x.Text = "2"; // 글은 그대로, 값만
        Assert.True(host.ActiveWorkspace!.ChangedSinceAutosave(out _));
        Assert.True(await host.AutosaveAsync(default));
        x.Type = BindDataType.Number;
        Assert.True(await host.AutosaveAsync(default));
        x.Direction = BindDirection.InOut;
        Assert.True(await host.AutosaveAsync(default));
        SetBind(tab.Binds, "UNRELATED", "z"); // 탭이 참조하지 않는 이름은 표지에 들지 않는다
        Assert.False(await host.AutosaveAsync(default));

        // 비정상 종료(Shutdown 없음) 뒤 복구: 마지막 자동 저장의 값이 돌아온다.
        var run2 = new SqlEditorHost(NewStore(), new FakeDialogs { Confirm = true });
        Assert.True(await run2.InitializeAsync(default));
        Assert.Equal([("X", "2", BindDataType.Number, BindDirection.InOut)], RowsOf(Assert.Single(run2.Tabs).Binds));
    });

    // ---- .sql 파일 ---------------------------------------------------------------------------

    [Fact]
    public Task Sql_save_appends_one_bind_block_and_open_hides_it_and_fills_the_bind_list() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "emp.sql");
        const string body = "select *\r\n  from emp\r\n where empno = :empno;\r\n";
        var original = FileEncoding.Encode(body, "utf-8-bom");
        await File.WriteAllBytesAsync(path, original);

        var host = new SqlEditorHost();
        var tab = (await host.OpenFileAsync(path, default))!;
        SetBind(tab.Binds, "EMPNO", "1002", BindDataType.Number);
        Assert.True(await host.SaveAsync(tab, default));
        Assert.False(tab.IsDirty);
        Assert.Equal(body, tab.Text);

        // 본문은 바이트 그대로(BOM·CRLF·끝 줄바꿈), 그 뒤에 줄바꿈 하나와 블록 하나.
        var saved = await File.ReadAllBytesAsync(path);
        var block = "\r\n" + SqlFileBinds.Marker + "\r\n" + EmpNoLine + "\r\n";
        Assert.Equal(original.Concat(Encoding.UTF8.GetBytes(block)).ToArray(), saved);

        Assert.True(await host.SaveAsync(tab, default)); // 다시 저장해도 블록은 하나
        Assert.Equal(saved, await File.ReadAllBytesAsync(path));

        var other = new SqlEditorHost();
        var reopened = (await other.OpenFileAsync(path, default))!;
        Assert.Equal(body, reopened.Text);
        Assert.False(reopened.IsDirty);
        Assert.Equal([("EMPNO", "1002", BindDataType.Number, BindDirection.In)], RowsOf(reopened.Binds));

        Assert.True(await other.SaveAsync(reopened, default)); // 연 그대로 저장해도 같은 바이트
        Assert.Equal(saved, await File.ReadAllBytesAsync(path));
    });

    [Fact]
    public Task Sql_without_bind_values_saves_exactly_the_same_bytes_as_before() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var noRefs = Path.Combine(_root, "plain.sql");
        var noValues = Path.Combine(_root, "novalue.sql");
        var plainBytes = FileEncoding.Encode("select '한글' from dual\n", "cp949");
        var noValueBytes = FileEncoding.Encode("select :id from dual", "utf-8");
        await File.WriteAllBytesAsync(noRefs, plainBytes);
        await File.WriteAllBytesAsync(noValues, noValueBytes);
        var host = new SqlEditorHost();
        var noRefsTab = (await host.OpenFileAsync(noRefs, default))!;
        var noValuesTab = (await host.OpenFileAsync(noValues, default))!;
        SetBind(noRefsTab.Binds, "OTHER", "1"); // 파일이 참조하지 않는 값
        noValuesTab.Binds.Ensure(["ID"]); // 참조하지만 값이 없는 빈 줄

        Assert.True(await host.SaveAsync(noRefsTab, default));
        Assert.True(await host.SaveAsync(noValuesTab, default));

        Assert.Equal(plainBytes, await File.ReadAllBytesAsync(noRefs));
        Assert.Equal(noValueBytes, await File.ReadAllBytesAsync(noValues));
    });

    [Fact]
    public Task Tricky_bind_values_round_trip_through_a_sql_file() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "tricky.sql");
        var host = new SqlEditorHost(dialogs: new FakeDialogs { SavePath = path });
        var tab = host.NewDocument("select :a, :b, :c, :d, :e, :f from dual");
        SetBind(tab.Binds, "A", "line1\nline2\r\nline3");
        SetBind(tab.Binds, "B", "*/ select 1 from dual; /*");
        SetBind(tab.Binds, "C", "it's \"quoted\" \\ back");
        SetBind(tab.Binds, "D", "R&D &1 &&2");
        SetBind(tab.Binds, "E", "한글 값", BindDataType.NVarchar2);
        SetBind(tab.Binds, "F", string.Empty, BindDataType.RefCursor, BindDirection.Out);
        Assert.True(await host.SaveAsync(tab, default));

        var text = await File.ReadAllTextAsync(path);
        var block = text[text.IndexOf(SqlFileBinds.Marker, StringComparison.Ordinal)..];
        Assert.Equal(7, block.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // 표지 + 값 6줄(값 안의 줄바꿈은 이스케이프)
        Assert.All(block.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.StartsWith("-- ", l, StringComparison.Ordinal));

        var other = new SqlEditorHost();
        var reopened = (await other.OpenFileAsync(path, default))!;
        Assert.Equal("select :a, :b, :c, :d, :e, :f from dual", reopened.Text);
        Assert.Equal(RowsOf(tab.Binds), RowsOf(reopened.Binds));
    });

    [Fact]
    public Task Saving_as_a_non_sql_file_writes_no_block() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "notes.txt");
        var host = new SqlEditorHost(dialogs: new FakeDialogs { SavePath = path });
        var tab = host.NewDocument("select :a from dual");
        SetBind(tab.Binds, "A", "1");

        Assert.True(await host.SaveAsync(tab, default));

        Assert.Equal("select :a from dual", await File.ReadAllTextAsync(path));
    });

    [Fact]
    public Task Restored_sql_file_tab_hides_the_block_and_workspace_values_win() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "r.sql");
        await File.WriteAllTextAsync(path, "select :a, :b from dual;\n");
        var host = new SqlEditorHost(NewStore());
        var tab = (await host.OpenFileAsync(path, default))!;
        SetBind(tab.Binds, "A", "from file");
        SetBind(tab.Binds, "B", "file only");
        Assert.True(await host.SaveAsync(tab, default));
        SetBind(tab.Binds, "A", "later"); // 파일은 저장하지 않고 작업 공간에만 남는 값
        await host.ShutdownAsync(default);

        var run2 = new SqlEditorHost(NewStore());
        Assert.False(await run2.InitializeAsync(default));

        var back = Assert.Single(run2.Tabs);
        Assert.Equal("select :a, :b from dual;\n", back.Text);
        Assert.False(back.IsDirty);
        Assert.Equal("later", back.Binds.Find("A")!.Text);
        Assert.Equal("file only", back.Binds.Find("B")!.Text);
    });

    [Fact]
    public Task Secret_looking_binds_are_never_written_to_disk() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "login.sql");
        var host = new SqlEditorHost(NewStore(), new FakeDialogs { SavePath = path });
        var tab = host.NewDocument("select * from users where name = :user_name and hash = :p_password and t = :api_token and s = :my_secret");
        SetBind(tab.Binds, "USER_NAME", "scott");
        SetBind(tab.Binds, "P_PASSWORD", "tiger-value-1");
        SetBind(tab.Binds, "API_TOKEN", "token-value-2");
        SetBind(tab.Binds, "MY_SECRET", "secret-value-3");
        Assert.True(await host.SaveAsync(tab, default));
        tab.Document.Insert(0, " "); // 작업 공간에 글도 담기게
        await host.ShutdownAsync(default);

        var sql = await File.ReadAllTextAsync(path);
        var block = sql[sql.IndexOf(SqlFileBinds.Marker, StringComparison.Ordinal)..];
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "workspaces", "default.json"));
        var binds = json[json.IndexOf("\"binds\"", StringComparison.Ordinal)..];
        foreach (var saved in new[] { block, binds })
        {
            Assert.Contains("scott", saved, StringComparison.Ordinal);
            foreach (var secret in SecretNamesAndValues)
            {
                Assert.DoesNotContain(secret, saved, StringComparison.OrdinalIgnoreCase);
            }
        }
    });
}
