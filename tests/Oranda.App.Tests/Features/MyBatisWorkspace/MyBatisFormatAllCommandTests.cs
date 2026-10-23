using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.App.Features.MyBatisWorkspace.Format;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Formatting;
using Oranda.Core.Shell;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 「매퍼 전체 포맷」(<c>oranda.mybatis.formatAll</c>)·「매퍼 전체 포맷 되돌리기」(<c>oranda.mybatis.undoFormatAll</c>):
/// 열린 탭·닫힌 요소가 섞인 매퍼에서 바뀐 요소만 저장 안 한 변경(*)과 「xml 에 저장」 편집이 되고, 저장하면 파일 들여쓰기가 남는다.
/// 실패·읽기 전용·포맷하는 사이 바뀐 요소는 원문을 두고 결과 알림에 사유가 붙는다. 되돌리기는 그 사이 고친 요소를 건너뛴다.
/// </summary>
public sealed partial class MyBatisFormatAllCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbfmtall-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _notices = [];

    public MyBatisFormatAllCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Both_commands_sit_in_the_mybatis_toolbar_and_edit_menu_and_ignore_a_read_only_active_tab()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        var format = registry.Find(MyBatisFormatAllCommands.FormatAll)!;
        var undo = registry.Find(MyBatisFormatAllCommands.UndoFormatAll)!;

        Assert.Equal("formatAll", format.Icon);
        Assert.Equal(["toolbar:mybatis/formatAll@36", "menu:edit/mybatisFormatAll@406"], format.Placements);
        Assert.Equal(["menu:edit/mybatisUndoFormatAll@407"], undo.Placements);
        foreach (var id in new[] { MyBatisFormatAllCommands.FormatAll, MyBatisFormatAllCommands.UndoFormatAll })
        {
            Assert.True(registry.CanExecute(id, Keys("mybatisDocActive")));
            Assert.True(registry.CanExecute(id, Keys("mybatisDocActive", "readOnlyDoc"))); // 활성 탭이 같은 id 문장(읽기 전용)이어도 켜진다
            Assert.False(registry.CanExecute(id, Keys("mybatisDocActive", MyBatisFormatAllCommands.BusyKey)));
            Assert.False(registry.CanExecute(id, Keys("sqlDocActive")));
        }
    }

    [Fact]
    public void Save_to_xml_is_off_while_format_all_runs()
    {
        // 포맷하는 사이 저장하면 저장 끝의 기준 다시 싣기가 막 적용한 포맷 글을 덮는다.
        var registry = ShellTestHelpers.CreateCommandRegistry();

        Assert.True(registry.CanExecute("oranda.mybatis.save", Keys("mybatisDocActive", "mybatisDocDirty")));
        Assert.False(registry.CanExecute("oranda.mybatis.save", Keys("mybatisDocActive", "mybatisDocDirty", MyBatisFormatAllCommands.BusyKey)));
    }

    [Fact]
    public Task Formats_open_and_closed_elements_and_only_changed_ones_become_pending_edits() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync(MapperSamples.EmpMapper);
        var list = ws.Tabs.Single(t => t.Title == "selectEmpList");
        var one = session.OpenStatement("selectEmp")!;
        var formatter = new BodyUpper(["selectEmpList", "selectEmp", "deleteEmp", "insertEmp", "updateEmpSal"])
        {
            Fail = { ["insertEmp"] = FormatFailure.MarkupUnsupported },
            Throw = { "updateEmpSal" },
        };

        await RunAsync(host, formatter);

        // sql 조각·문장 7개(resultMap 은 빼고): 바뀜 3 · 그대로 2(본문이 이미 대문자인 empCols·countEmp) · 원문 유지 2.
        Assert.Equal(7, formatter.Calls);
        var notice = Assert.Single(_notices);
        Assert.StartsWith("매퍼 전체 포맷을 마쳤습니다: 3개 정리, 2개는 이미 정리됨, 2개는 원문 유지.", notice, StringComparison.Ordinal);
        Assert.Contains("· insertEmp: 포맷할 수 없는 MyBatis 태그 모양입니다", notice, StringComparison.Ordinal);
        Assert.Contains("· updateEmpSal: 포매터 내부 오류가 났습니다", notice, StringComparison.Ordinal);

        Assert.True(list.IsDirty);
        Assert.True(one.IsDirty);
        Assert.Contains("E.EMPNO = #{EMPNO}", one.Text, StringComparison.Ordinal);
        Assert.Null(session.Find("deleteEmp")!.Tab);
        Assert.Contains("#{EMPNO}", session.Find("deleteEmp")!.Text, StringComparison.Ordinal);
        Assert.Equal(
            ["selectEmpList", "selectEmp", "deleteEmp"],
            session.PendingEdits().Select(e => Assert.IsType<MapperEdit.ReplaceElement>(e).Id));
        Assert.False(session.Find("insertEmp")!.IsModified);
        Assert.False(session.Find("updateEmpSal")!.IsModified);

        // 열린 탭은 탭마다 실행 취소 1단계다.
        one.Document.UndoStack.Undo();
        Assert.False(one.IsDirty);
    });

    [Fact]
    public Task Saving_after_format_all_keeps_the_file_indent_of_each_element() => Ui.Run(async () =>
    {
        var (host, _, session) = await OpenAsync(MyBatisIndentTests.Indented);

        await RunAsync(host, new BodyUpper(MyBatisIndentTests.StatementIds));

        var written = session.Document.Apply(session.PendingEdits());
        Assert.Contains(
            "    <select id=\"selectMoSpc\" resultType=\"map\">\n        SELECT *\n          FROM MO_SPC\n         WHERE SPC_ID = #{SPCID}\n    </select>\n",
            written,
            StringComparison.Ordinal);
        Assert.Contains("    <delete id=\"deleteMoSpc\">\n        DELETE FROM MO_SPC\n         WHERE SPC_ID = #{SPCID}\n    </delete>\n", written, StringComparison.Ordinal);
        Assert.Contains("    <update id=\"shallow\">\n        UPDATE MO_SPC\n  SET A = 1\n    </update>\n", written, StringComparison.Ordinal); // 왕복되지 않는 요소는 원문 공간 그대로

        session.Rebase(written, MyBatisIndentTests.StatementIds);
        Assert.False(session.IsDirty);
        Assert.Empty(session.PendingEdits());
    });

    [Fact]
    public Task Read_only_elements_with_a_shared_id_are_kept_with_the_reason() => Ui.Run(async () =>
    {
        const string mapper = """
            <?xml version="1.0" encoding="UTF-8"?>
            <mapper namespace="t.Dup">
              <select id="pick" databaseId="oracle">select 1 from dual</select>
              <select id="pick" databaseId="h2">select 1</select>
              <select id="other">select 2 from dual</select>
            </mapper>

            """;
        var (host, _, session) = await OpenAsync(mapper);
        var formatter = new BodyUpper(["pick", "other"]);

        await RunAsync(host, formatter);

        Assert.Equal(1, formatter.Calls);
        var notice = Assert.Single(_notices);
        Assert.StartsWith("매퍼 전체 포맷을 마쳤습니다: 1개 정리, 0개는 이미 정리됨, 2개는 원문 유지.", notice, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(notice, "· pick: 읽기 전용\\(같은 id 가 여럿\\)"));
        Assert.Equal(["other"], session.PendingEdits().Select(e => Assert.IsType<MapperEdit.ReplaceElement>(e).Id));
    });

    [Fact]
    public Task An_element_edited_while_formatting_is_kept_and_a_rebuilt_mapper_is_not_touched() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync(MapperSamples.EmpMapper);
        var list = ws.Tabs.Single(t => t.Title == "selectEmpList");

        // 옵션을 읽는 사이(글을 잡아 둔 뒤) 탭을 고친다.
        await RunAsync(host, new BodyUpper(["selectEmpList", "deleteEmp"]), _ =>
        {
            list.Document.Insert(0, "<!-- typed -->");
            return Task.FromResult(new FormatterOptions());
        });

        var notice = Assert.Single(_notices);
        Assert.StartsWith("매퍼 전체 포맷을 마쳤습니다: 1개 정리, 5개는 이미 정리됨, 1개는 원문 유지.", notice, StringComparison.Ordinal);
        Assert.Contains("· selectEmpList: 포맷하는 사이 글이 바뀌었습니다", notice, StringComparison.Ordinal);
        Assert.StartsWith("<!-- typed -->", list.Text, StringComparison.Ordinal);
        Assert.Contains("#{empNo}", list.Text, StringComparison.Ordinal);
        Assert.True(session.Find("deleteEmp")!.IsModified);

        // 포맷하는 사이 저장 뒤 기준이 바뀌면(요소를 새로 짓는다) 아무것도 바꾸지 않는다.
        _notices.Clear();
        var (host2, _, session2) = await OpenAsync(MapperSamples.EmpMapper, "Other.xml");
        await RunAsync(host2, new BodyUpper(["countEmp"]), _ =>
        {
            session2.Rebase(session2.Document.Text, []);
            return Task.FromResult(new FormatterOptions());
        });

        Assert.Equal("포맷하는 사이 매퍼가 바뀌어 적용하지 않았습니다.", Assert.Single(_notices));
        Assert.False(session2.IsDirty);
    });

    [Fact]
    public Task Busy_key_is_on_while_formatting_and_a_second_run_is_ignored() => Ui.Run(async () =>
    {
        var (host, _, session) = await OpenAsync(MapperSamples.EmpMapper);
        var keys = new ContextKeyService();
        var formatter = new BodyUpper(["deleteEmp"]);
        bool busyInside = false;
        MyBatisFormatAllCommands handler = null!;
        handler = new MyBatisFormatAllCommands
        {
            Host = host,
            Formatter = formatter,
            Keys = keys,
            OptionsProvider = async _ =>
            {
                busyInside = keys.Get(MyBatisFormatAllCommands.BusyKey);
                await handler.ExecuteAsync(Context(MyBatisFormatAllCommands.FormatAll), default); // 진행 중 두 번째 실행은 아무것도 하지 않는다
                return new FormatterOptions();
            },
            Notify = Collect,
        };

        await handler.ExecuteAsync(Context(MyBatisFormatAllCommands.FormatAll), default);

        Assert.True(busyInside);
        Assert.False(keys.Get(MyBatisFormatAllCommands.BusyKey));
        Assert.Equal(7, formatter.Calls);
        Assert.Single(_notices);
        Assert.True(session.Find("deleteEmp")!.IsModified);
    });

    [Fact]
    public Task Kept_reasons_are_listed_up_to_twenty_then_counted() => Ui.Run(async () =>
    {
        var mapper = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<mapper namespace=\"t.Many\">\n");
        var ids = Enumerable.Range(1, 25).Select(i => string.Create(CultureInfo.InvariantCulture, $"s{i:00}")).ToList();
        foreach (var id in ids)
        {
            mapper.Append(CultureInfo.InvariantCulture, $"  <select id=\"{id}\">select {id} from dual</select>\n");
        }

        var (host, _, _) = await OpenAsync(mapper.Append("</mapper>\n").ToString());
        var formatter = new BodyUpper(ids);
        foreach (var id in ids)
        {
            formatter.Fail[id] = FormatFailure.MarkupMismatch;
        }

        await RunAsync(host, formatter);

        var notice = Assert.Single(_notices);
        Assert.Equal(MyBatisFormatAllCommands.MaxListed, Regex.Count(notice, "^· s\\d\\d: ", RegexOptions.Multiline));
        Assert.Contains("· s20: ", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("· s21: ", notice, StringComparison.Ordinal);
        Assert.EndsWith("외 5개가 더 있습니다.", notice, StringComparison.Ordinal);
    });

    [Fact]
    public Task Undo_reverts_untouched_elements_and_skips_ones_edited_since() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync(MapperSamples.EmpMapper);
        var list = ws.Tabs.Single(t => t.Title == "selectEmpList");
        var one = session.OpenStatement("selectEmp")!;
        var delete = session.Find("deleteEmp")!;
        var before = (List: list.Text, Delete: delete.Text);
        await RunAsync(host, new BodyUpper(["selectEmpList", "selectEmp", "deleteEmp"]));
        one.Document.Insert(0, "<!-- mine -->");
        _notices.Clear();

        await Handler(host, new BodyUpper([])).ExecuteAsync(Context(MyBatisFormatAllCommands.UndoFormatAll), default);

        var notice = Assert.Single(_notices);
        Assert.StartsWith("전체 포맷을 되돌렸습니다: 2개 되돌림, 1개는 그 사이 고쳐서 건너뜀.", notice, StringComparison.Ordinal);
        Assert.EndsWith("\n· selectEmp", notice, StringComparison.Ordinal);
        Assert.Equal(before.List, list.Text);
        Assert.False(list.IsDirty);
        Assert.Equal(before.Delete, delete.Text);
        Assert.False(delete.IsModified);
        Assert.StartsWith("<!-- mine -->", one.Text, StringComparison.Ordinal);
        Assert.Contains("#{EMPNO}", one.Text, StringComparison.Ordinal);
        Assert.Equal(["selectEmp"], session.PendingEdits().Select(e => Assert.IsType<MapperEdit.ReplaceElement>(e).Id));

        // 직전 한 번분만: 다시 되돌릴 것이 없다.
        _notices.Clear();
        await Handler(host, new BodyUpper([])).ExecuteAsync(Context(MyBatisFormatAllCommands.UndoFormatAll), default);
        Assert.Equal("되돌릴 전체 포맷이 없습니다.", Assert.Single(_notices));
    });

    [Fact]
    public Task A_later_run_that_changes_nothing_leaves_nothing_to_undo() => Ui.Run(async () =>
    {
        var (host, _, session) = await OpenAsync(MapperSamples.EmpMapper);
        await RunAsync(host, new BodyUpper(["deleteEmp"]));
        await RunAsync(host, new BodyUpper([]));
        _notices.Clear();

        await Handler(host, new BodyUpper([])).ExecuteAsync(Context(MyBatisFormatAllCommands.UndoFormatAll), default);

        Assert.Equal("되돌릴 전체 포맷이 없습니다.", Assert.Single(_notices));
        Assert.True(session.Find("deleteEmp")!.IsModified);
    });

    [Fact]
    public Task Saving_or_opening_another_mapper_drops_the_undo_list() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync(MapperSamples.EmpMapper);
        var undo = Handler(host, new BodyUpper([]));

        // 저장(기준 문서가 바뀜) 뒤에는 되돌리지 않는다.
        await RunAsync(host, new BodyUpper(["deleteEmp"]));
        var written = session.Document.Apply(session.PendingEdits());
        session.Rebase(written, ["deleteEmp"]);
        _notices.Clear();
        await undo.ExecuteAsync(Context(MyBatisFormatAllCommands.UndoFormatAll), default);
        Assert.Equal("되돌릴 전체 포맷이 없습니다.", Assert.Single(_notices));
        Assert.Contains("#{EMPNO}", session.Find("deleteEmp")!.Text, StringComparison.Ordinal);

        // 다른 매퍼를 열었다가 돌아와도 되돌리지 않는다.
        await RunAsync(host, new BodyUpper(["selectEmp"]));
        await host.OpenWorkspaceFileAsync(WriteMapper(MapperSamples.EmpMapper, "Other.xml"), default);
        Assert.NotSame(session, host.ActiveWorkspace!.MyBatis);
        host.ActiveWorkspace = ws;
        _notices.Clear();
        await undo.ExecuteAsync(Context(MyBatisFormatAllCommands.UndoFormatAll), default);
        Assert.Equal("되돌릴 전체 포맷이 없습니다.", Assert.Single(_notices));
        Assert.True(session.Find("selectEmp")!.IsModified);
    });

    private string WriteMapper(string text, string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<(SqlEditorHost Host, SqlWorkspaceViewModel Ws, MyBatisWorkspaceSession Session)> OpenAsync(string text, string name = "Mapper.xml")
    {
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        await host.OpenWorkspaceFileAsync(WriteMapper(text, name), default);
        var ws = host.ActiveWorkspace!;
        return (host, ws, Assert.IsType<MyBatisWorkspaceSession>(ws.MyBatis));
    }

    private Task RunAsync(SqlEditorHost host, ISqlFormatter formatter, Func<CancellationToken, Task<FormatterOptions>>? options = null) =>
        Handler(host, formatter, options).ExecuteAsync(Context(MyBatisFormatAllCommands.FormatAll), default);

    private MyBatisFormatAllCommands Handler(SqlEditorHost host, ISqlFormatter formatter, Func<CancellationToken, Task<FormatterOptions>>? options = null) => new()
    {
        Host = host,
        Formatter = formatter,
        OptionsProvider = options ?? (_ => Task.FromResult(new FormatterOptions())),
        Notify = Collect,
    };

    private Task Collect(string message)
    {
        _notices.Add(message);
        return Task.CompletedTask;
    }

    private static CommandContext Context(string id) => new(id, FakeContextKeys.AllTrue);

    private static KeySet Keys(params string[] on) => new(on);

    [GeneratedRegex("id=\"(\\w+)\"")]
    private static partial Regex IdAttribute();

    private sealed class KeySet(IEnumerable<string> on) : IContextKeys
    {
        private readonly HashSet<string> _on = new(on, StringComparer.Ordinal);

        public bool Evaluate(string? expression) =>
            string.IsNullOrEmpty(expression) || ContextKeyReasons.Evaluate(expression, _on.Contains);
    }

    /// <summary>
    /// 고른 id 의 본문(첫 <c>&gt;</c> 와 마지막 <c>&lt;</c> 사이)만 대문자로 바꾸는 포매터(태그는 그대로). 고르지 않은 id 는 그대로 둔다(이미 정리됨).
    /// <see cref="Fail"/> 는 실패 결과, <see cref="Throw"/> 는 예외. 스레드 풀에서 불리므로 셈은 Interlocked 로 한다.
    /// </summary>
    private sealed class BodyUpper(IEnumerable<string> ids) : IMyBatisStatementFormatter
    {
        private readonly HashSet<string> _ids = new(ids, StringComparer.Ordinal);
        private int _calls;

        public Dictionary<string, FormatFailure> Fail { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Throw { get; } = new(StringComparer.Ordinal);

        public int Calls => _calls;

        public FormatResult Format(string text, FormatterOptions o, TextRange? range = null)
        {
            Interlocked.Increment(ref _calls);
            var whole = new TextRange(0, text.Length);
            var id = IdAttribute().Match(text).Groups[1].Value;
            if (Throw.Contains(id))
            {
                throw new InvalidOperationException("시험");
            }

            if (Fail.TryGetValue(id, out var failure))
            {
                return new FormatResult(text, whole, failure);
            }

            if (!_ids.Contains(id))
            {
                return new FormatResult(text, whole);
            }

            int open = text.IndexOf('>', StringComparison.Ordinal) + 1, close = text.LastIndexOf('<');
            return new FormatResult(string.Concat(text.AsSpan(0, open), text[open..close].ToUpperInvariant(), text.AsSpan(close)), whole);
        }
    }
}
