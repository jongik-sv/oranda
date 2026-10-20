using System.Reflection;
using System.Text.RegularExpressions;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Extensibility;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Core.Text;
using Oranda.Core.Workspaces;
using Oranda.Platform;
using static Oranda.App.Features.SqlEditor.EditorTextOps;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>TSK-04-02 SQL 편집기: 글자 연산·구조(접기·짝)·찾기·ViewModel·탭 관리·작업 공간 복구·명령 등록·IEditorContext.</summary>
public sealed class SqlEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-ed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Apply(string text, TextOpResult? r)
        => r is null ? text : text.Remove(r.Start, r.Length).Insert(r.Start, r.NewText);

    // ---- 틀·등록 -------------------------------------------------------------------------

    [Fact]
    public void Panels_are_registered_with_views()
    {
        var editor = ShellTestHelpers.RequirePanel("oranda.panel.sqlEditor");
        Assert.Equal(ToolPanelKind.Document, editor.Kind);
        Assert.True(editor.AllowMultiple);
        Assert.Equal(DockLocation.Right, ShellTestHelpers.RequirePanel("oranda.panel.outline").DefaultLocation);
        Assert.NotNull(new SqlEditorViewModel());
        Assert.Contains(ShellTestHelpers.AppContributions.StatusBarItems, i => i.Id == "oranda.status.editor" && i.Order == 5);
    }

    [Fact]
    public void Every_command_owned_by_this_task_in_commands_md_is_registered_with_matching_conditions()
    {
        var doc = FindCommandsDoc();
        var rows = File.ReadAllLines(doc)
            .Where(l => l.StartsWith("| `oranda.", StringComparison.Ordinal) && l.Contains("TSK-04-02", StringComparison.Ordinal))
            .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        Assert.Equal(52, rows.Count);

        var registered = ShellTestHelpers.AppContributions.Commands.ToDictionary(c => c.Definition.Id);
        foreach (var r in rows)
        {
            var id = r[0].Trim('`');
            Assert.True(registered.TryGetValue(id, out var reg), $"미등록 명령: {id}");
            Assert.Equal(typeof(SqlEditorCommandHandler), reg.HandlerType);

            var enablement = r[4].StartsWith('`') ? r[4].Trim('`').Replace("\\|", "|", StringComparison.Ordinal) : null;
            Assert.Equal(enablement, reg.Definition.Enablement);
            var when = r[3].StartsWith('`') ? r[3].Trim('`') : null;
            Assert.Equal(when, reg.Definition.When);
        }
    }

    private static string FindCommandsDoc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", "design", "commands.md");
            if (File.Exists(p))
            {
                return p;
            }
        }

        throw new FileNotFoundException("docs/design/commands.md 를 찾지 못했다.");
    }

    // ---- 글자 연산 -----------------------------------------------------------------------

    [Fact]
    public void Toggle_line_comment_comments_then_uncomments_every_selected_line()
    {
        var text = "select 1\n  from dual\n\nwhere 1=1";
        var commented = Apply(text, ToggleLineComment(text, 0, text.Length));
        Assert.Equal("-- select 1\n  -- from dual\n\n-- where 1=1", commented);
        Assert.Equal(text, Apply(commented, ToggleLineComment(commented, 0, commented.Length)));
    }

    [Fact]
    public void Toggle_line_comment_with_caret_only_touches_current_line()
    {
        var text = "a\nb\nc";
        Assert.Equal("a\n-- b\nc", Apply(text, ToggleLineComment(text, 2, 0)));
    }

    [Fact]
    public void Selection_ending_at_line_start_does_not_include_next_line()
    {
        var text = "a\nb\nc";
        Assert.Equal("-- a\nb\nc", Apply(text, CommentLines(text, 0, 2)));
    }

    [Fact]
    public void Uncomment_handles_no_space_and_keeps_indent()
    {
        Assert.Equal("  x", Apply("  --x", UncommentLines("  --x", 0, 0)));
        Assert.Null(UncommentLines("plain", 0, 0));
    }

    [Fact]
    public void Block_comment_wraps_selection_and_unwraps_again()
    {
        var text = "select a, b from t";
        var wrapped = Apply(text, ToggleBlockComment(text, 7, 4));
        Assert.Equal("select /* a, b */ from t", wrapped);
        var r = ToggleBlockComment(wrapped, 7, 10);
        Assert.Equal(text, Apply(wrapped, r));
    }

    [Fact]
    public void Block_comment_without_selection_wraps_line_content()
    {
        Assert.Equal("  /* x = 1 */", Apply("  x = 1", ToggleBlockComment("  x = 1", 3, 0)));
        Assert.Null(ToggleBlockComment("   ", 1, 0));
    }

    [Fact]
    public void Indent_and_outdent_work_on_selected_lines_and_skip_blank_lines()
    {
        var text = "a\n\nb";
        var ind = Apply(text, Indent(text, 0, text.Length));
        Assert.Equal("    a\n\n    b", ind);
        Assert.Equal(text, Apply(ind, Outdent(ind, 0, ind.Length)));
        Assert.Equal("a", Apply("  a", Outdent("  a", 0, 0)));
        Assert.Equal("a", Apply("\ta", Outdent("\ta", 0, 0)));
        Assert.Null(Outdent("a", 0, 0));
    }

    [Fact]
    public void Case_changes_apply_to_selection_or_word_at_caret()
    {
        Assert.Equal("SELECT * from dual", Apply("select * from dual", ChangeCase("select * from dual", 2, 0, CaseMode.Upper)));
        Assert.Equal("select * from dual", Apply("SELECT * FROM DUAL", ChangeCase("SELECT * FROM DUAL", 0, 18, CaseMode.Lower)));
        Assert.Equal("Hello World Foo_bar", Apply("hello WORLD foo_bar", ChangeCase("hello WORLD foo_bar", 0, 19, CaseMode.Title)));
        Assert.Equal("Hello world. This is. Ok", Apply("hello world. this IS. ok", ChangeCase("hello world. this IS. ok", 0, 24, CaseMode.Sentence)));
        Assert.Equal("AbC", Apply("aBc", ChangeCase("aBc", 0, 3, CaseMode.Invert)));
        Assert.Null(ChangeCase("a  b", 2, 0, CaseMode.Upper)); // 낱말 밖
    }

    [Fact]
    public void Cycle_case_goes_upper_to_lower_to_title_to_upper()
    {
        Assert.Equal("abc", Apply("ABC", ChangeCase("ABC", 0, 3, CaseMode.Cycle)));
        Assert.Equal("Abc", Apply("abc", ChangeCase("abc", 0, 3, CaseMode.Cycle)));
        Assert.Equal("ABC", Apply("Abc", ChangeCase("Abc", 0, 3, CaseMode.Cycle)));
    }

    [Fact]
    public void Duplicate_line_copies_below_and_selects_copy()
    {
        var text = "a\nb\nc";
        var r = DuplicateLine(text, 2, 0);
        Assert.Equal("a\nb\nb\nc", Apply(text, r));
        Assert.Equal((4, 1), (r.SelectionStart, r.SelectionLength));
    }

    [Fact]
    public void Duplicate_and_delete_line_keep_crlf()
    {
        var text = "a\r\nb\r\nc";
        Assert.Equal("a\r\nb\r\nb\r\nc", Apply(text, DuplicateLine(text, 3, 0)));
        Assert.Equal("a\r\nc", Apply(text, DeleteLine(text, 3, 0)));
    }

    [Fact]
    public void Delete_line_removes_last_line_without_leaving_blank_line()
    {
        Assert.Equal("a\nb", Apply("a\nb\nc", DeleteLine("a\nb\nc", 4, 0)));
        Assert.Equal(string.Empty, Apply("only", DeleteLine("only", 1, 0)));
        Assert.Equal("c", Apply("a\nb\nc", DeleteLine("a\nb\nc", 0, 3)));
    }

    // ---- 구조: 짝·접기 --------------------------------------------------------------------

    [Fact]
    public void Pairs_cover_begin_end_if_loop_case_and_paren()
    {
        const string Text = "BEGIN\n  IF x THEN\n    FOR i IN 1..3 LOOP\n      y := (1 + CASE WHEN a THEN 1 ELSE 2 END);\n    END LOOP;\n  END IF;\nEND;";
        var kinds = SqlStructure.FindPairs(Text).Select(p => p.Kind).ToList();
        Assert.Equal(
            new[] { PairKind.Case, PairKind.Paren, PairKind.Loop, PairKind.If, PairKind.Begin },
            kinds);
        var ifPair = SqlStructure.FindPairs(Text).Single(p => p.Kind == PairKind.If);
        Assert.Equal("END IF", Text.Substring(ifPair.Close.Start, ifPair.Close.Length));
    }

    [Theory]
    [InlineData("SELECT '(' FROM dual")]
    [InlineData("-- ( BEGIN\nSELECT 1")]
    [InlineData("DROP TABLE IF EXISTS t")]
    [InlineData("END;")]
    [InlineData("SELECT \"BEGIN\" FROM t")]
    public void Strings_comments_if_exists_and_stray_end_make_no_pairs(string text)
        => Assert.Empty(SqlStructure.FindPairs(text));

    [Fact]
    public void If_not_exists_is_not_a_block()
    {
        var pairs = SqlStructure.FindPairs("CREATE TABLE IF NOT EXISTS t (a int)");
        Assert.Equal(PairKind.Paren, Assert.Single(pairs).Kind);
    }

    [Fact]
    public void Multi_line_block_comment_is_one_pair_and_fold()
    {
        const string Text = "/* a\n b */\nselect 1";
        var pair = Assert.Single(SqlStructure.FindPairs(Text), p => p.Kind == PairKind.BlockComment);
        Assert.Equal(0, pair.Open.Start);
        Assert.Equal(Text.IndexOf("*/", StringComparison.Ordinal), pair.Close.Start);
        Assert.Contains(SqlStructure.ComputeFolds(Text), f => f.Start == 0 && f.Name == "/* … */");
    }

    [Fact]
    public void Folds_exist_only_for_multi_line_regions_sorted_and_properly_nested()
    {
        const string Text = "BEGIN\n  IF a THEN\n    NULL;\n  END IF;\n  x := (1);\nEND;\n/\nSELECT 1\nFROM dual;";
        var folds = SqlStructure.ComputeFolds(Text);

        Assert.NotEmpty(folds);
        Assert.Equal(folds.OrderBy(f => f.Start).ToList(), folds);
        Assert.All(folds, f => Assert.Contains('\n', Text.Substring(f.Start, f.End - f.Start)));
        // 포함 관계만 있다(걸치는 구간 없음).
        foreach (var a in folds)
        {
            foreach (var b in folds.Where(b => b.Start > a.Start && b.Start < a.End))
            {
                Assert.True(b.End <= a.End, $"겹침: {a} {b}");
            }
        }

        Assert.Contains(folds, f => f.Start == Text.IndexOf("IF a", StringComparison.Ordinal) && f.Name.EndsWith("END IF", StringComparison.Ordinal));
        Assert.DoesNotContain(folds, f => f.Start == Text.IndexOf("(1)", StringComparison.Ordinal)); // 한 줄 괄호는 접지 않는다
        Assert.Contains(folds, f => f.Start == Text.IndexOf("SELECT 1", StringComparison.Ordinal)); // 여러 줄 문장
    }

    [Fact]
    public void Find_match_jumps_between_begin_and_end_and_between_parens()
    {
        const string Text = "BEGIN\nNULL;\nEND;";
        var pairs = SqlStructure.FindPairs(Text);
        var fromBegin = SqlStructure.FindMatch(pairs, 0);
        Assert.Equal(new TextRange(0, 5), fromBegin!.Value.Here);
        Assert.Equal("END", Text.Substring(fromBegin.Value.Other.Start, fromBegin.Value.Other.Length));
        var fromEnd = SqlStructure.FindMatch(pairs, Text.IndexOf("END", StringComparison.Ordinal) + 3);
        Assert.Equal(new TextRange(0, 5), fromEnd!.Value.Other);
        Assert.Null(SqlStructure.FindMatch(pairs, Text.IndexOf("NULL", StringComparison.Ordinal) + 1));
    }

    [Fact]
    public void Go_to_matching_bracket_moves_caret()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "select (a +\n b) from t");
        vm.Select(7, 0);
        Assert.True(vm.GoToMatchingBracket());
        Assert.Equal(vm.Text.IndexOf(')', StringComparison.Ordinal) + 1, vm.CaretOffset);
        Assert.True(vm.GoToMatchingBracket());
        Assert.Equal(7, vm.CaretOffset);
    }

    // ---- 찾기·바꾸기 ---------------------------------------------------------------------

    [Fact]
    public void Finder_plain_case_and_whole_word()
    {
        var text = "id, Idx, my_id, ID";
        Assert.Equal(4, new TextFinder(new FindOptions { Pattern = "id" }).Count(text));
        Assert.Equal(2, new TextFinder(new FindOptions { Pattern = "id", MatchCase = true }).Count(text));
        var word = new TextFinder(new FindOptions { Pattern = "id", WholeWord = true });
        Assert.Equal(2, word.Count(text));
        Assert.Equal((0, 2), word.FindNext(text, 0));
        Assert.Equal((16, 2), word.FindNext(text, 3));
        Assert.Equal((0, 2), word.FindNext(text, 17)); // 한 바퀴
        Assert.Null(word.FindNext(text, 17, wrap: false));
    }

    [Fact]
    public void Finder_regex_replace_uses_groups_and_reports_invalid_pattern()
    {
        var f = new TextFinder(new FindOptions { Pattern = @"(\w+)@(\w+)", UseRegex = true });
        var (text, n) = f.ReplaceAll("a@b c@d", "$2#$1");
        Assert.Equal(("b#a d#c", 2), (text, n));

        var bad = new TextFinder(new FindOptions { Pattern = "(", UseRegex = true });
        Assert.NotNull(bad.Error);
        Assert.False(bad.IsReady);
        Assert.Null(bad.FindNext("(", 0));
        Assert.Equal(0, bad.Count("("));

        // 정규식이 아니면 특수문자는 글자 그대로.
        Assert.Equal(1, new TextFinder(new FindOptions { Pattern = "a.c" }).Count("abc a.c"));
    }

    [Fact]
    public void Finder_previous_wraps()
    {
        var f = new TextFinder(new FindOptions { Pattern = "x" });
        Assert.Equal((3, 1), f.FindPrevious("x..x..x", 6));
        Assert.Equal((6, 1), f.FindPrevious("x..x..x", 0));
        Assert.Null(f.FindPrevious("x..x..x", 0, wrap: false));
    }

    [Fact]
    public void ViewModel_find_next_selects_matches_and_cycles()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "select a, a from a_tab");
        vm.FindText = "a";
        Assert.True(vm.HasFindText);
        vm.WholeWord = true;
        Assert.Equal("2건", vm.FindStatus);
        Assert.True(vm.FindNext());
        Assert.Equal((7, 1), (vm.SelectionStart, vm.SelectionLength));
        vm.FindNext();
        Assert.Equal(10, vm.SelectionStart);
        vm.FindNext();
        Assert.Equal(7, vm.SelectionStart); // 한 바퀴
        vm.FindPrevious();
        Assert.Equal(10, vm.SelectionStart);
    }

    [Fact]
    public void ViewModel_replace_current_and_replace_all_with_regex_are_single_undo_steps()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "emp1 emp22 dept3");
        vm.FindText = @"(\w+?)(\d+)";
        vm.UseRegex = true;
        vm.ReplaceText = "$1_$2";
        Assert.Equal(3, vm.ReplaceAll());
        Assert.Equal("emp_1 emp_22 dept_3", vm.Text);
        vm.Document.UndoStack.Undo();
        Assert.Equal("emp1 emp22 dept3", vm.Text);

        vm.UseRegex = false;
        vm.FindText = "emp";
        vm.ReplaceText = "e";
        vm.FindNext();
        Assert.True(vm.ReplaceCurrent()); // 첫 emp 를 바꾸고 다음으로
        Assert.Equal("e1 emp22 dept3", vm.Text);
        Assert.Equal(3, vm.SelectionStart);
    }

    [Fact]
    public void Read_only_blocks_edits_and_replace()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "abc") { IsReadOnly = true };
        Assert.False(vm.Apply(UncommentLines));
        Assert.False(vm.Apply(CommentLines));
        vm.FindText = "a";
        vm.ReplaceText = "x";
        Assert.Equal(0, vm.ReplaceAll());
        vm.OpenFind(replace: true);
        Assert.False(vm.IsReplaceMode);
        Assert.Equal("abc", vm.Text);
    }

    [Fact]
    public void Open_find_seeds_text_from_single_line_selection()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "select abc from t");
        vm.Select(7, 3);
        vm.OpenFind(false);
        Assert.True(vm.IsFindPanelOpen);
        Assert.Equal("abc", vm.FindText);
    }

    // ---- 북마크·이동 ---------------------------------------------------------------------

    [Fact]
    public void Bookmarks_toggle_move_with_edits_and_cycle()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "l1\nl2\nl3\nl4\nl5");
        Assert.False(vm.HasBookmark);
        vm.GoToLine(2);
        Assert.True(vm.ToggleBookmark());
        vm.GoToLine(4);
        vm.ToggleBookmark();
        Assert.Equal([2, 4], vm.BookmarkLines);

        vm.Document.Insert(0, "new\n"); // 위에 한 줄 추가 → 북마크도 아래로 밀린다
        Assert.Equal([3, 5], vm.BookmarkLines);

        vm.GoToLine(1);
        vm.GoToBookmark(+1);
        Assert.Equal(3, vm.CaretPosition.Line);
        vm.GoToBookmark(+1);
        Assert.Equal(5, vm.CaretPosition.Line);
        vm.GoToBookmark(+1);
        Assert.Equal(3, vm.CaretPosition.Line); // 끝에서 처음으로
        vm.GoToBookmark(-1);
        Assert.Equal(5, vm.CaretPosition.Line);

        Assert.False(vm.ToggleBookmark()); // 같은 줄 다시 = 해제
        Assert.Equal([3], vm.BookmarkLines);
    }

    [Fact]
    public void Go_to_line_rejects_out_of_range()
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "a\nb");
        Assert.False(vm.GoToLine(0));
        Assert.False(vm.GoToLine(3));
        Assert.True(vm.GoToLine(2));
        Assert.Equal("줄 2, 열 1", vm.PositionText);
    }

    // ---- 문서 개요 -----------------------------------------------------------------------

    [Fact]
    public void Outline_lists_statements_with_line_numbers_and_skips_leading_comments()
    {
        const string Text = "-- 첫째\nSELECT 1\n  FROM dual;\n\nUPDATE t SET a = 1;\nBEGIN\n  NULL;\nEND;\n/";
        var items = OutlineViewModel.Build(Text);
        Assert.Equal(3, items.Count);
        Assert.Equal("SELECT 1", items[0].Title);
        Assert.Equal(2, items[0].Line);
        Assert.Equal("UPDATE t SET a = 1", items[1].Title);
        Assert.Equal(5, items[1].Line);
        Assert.Equal(6, items[2].Line);
    }

    [Fact]
    public void Outline_view_model_follows_active_tab_and_goes_to_item()
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument("select 1;\nselect 2;");
        using var outline = new OutlineViewModel { Host = host };
        Assert.Equal(2, outline.Items.Count);

        outline.GoTo(outline.Items[1]);
        Assert.Equal(2, tab.CaretPosition.Line);

        host.NewDocument("delete from t;");
        Assert.Single(outline.Items);
    }

    [Fact]
    public void Status_item_shows_position_selection_and_follows_active_tab()
    {
        var host = new SqlEditorHost();
        using var status = new EditorStatusViewModel { Host = host };
        Assert.False(status.IsVisible);
        Assert.Equal(string.Empty, status.Text);

        var tab = host.NewDocument("abc\ndef");
        Assert.True(status.IsVisible);
        Assert.Equal("줄 1, 열 1", status.Text);
        tab.Select(5, 2);
        Assert.Equal("줄 2, 열 4 (선택 2자)", status.Text);
        host.Close(tab);
        Assert.False(status.IsVisible);
    }

    // ---- 작업 공간 탭 변환 ---------------------------------------------------------------

    [Fact]
    public void Workspace_tab_keeps_content_only_for_unsaved_documents()
    {
        var untitled = new SqlEditorViewModel(Guid.NewGuid(), "select 1") { Title = "SQL 1" };
        Assert.Equal("select 1", untitled.ToWorkspaceTab().Content);

        var file = new SqlEditorViewModel(Guid.NewGuid(), "from disk") { Title = "a.sql", FilePath = @"C:\x\a.sql" };
        file.MarkSaved();
        Assert.Null(file.ToWorkspaceTab().Content);

        file.Document.Insert(0, "edited ");
        var tab = file.ToWorkspaceTab();
        Assert.True(tab.IsDirty);
        Assert.Equal("edited from disk", tab.Content);
    }

    [Fact]
    public void Workspace_tab_round_trips_caret_selection_and_connection()
    {
        var profile = Guid.NewGuid();
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "line1\nline2\nline3") { Title = "SQL 7", ConnectionProfileId = profile, Encoding = "cp949" };
        vm.Select(8, 3);
        var tab = vm.ToWorkspaceTab();
        Assert.Equal(new TextPosition(2, 3), tab.SelectionStart);
        Assert.Equal(new TextPosition(2, 6), tab.Caret);

        var back = SqlEditorViewModel.FromWorkspaceTab(tab, null);
        Assert.Equal(vm.TabId, back.TabId);
        Assert.Equal((8, 3, 11), (back.SelectionStart, back.SelectionLength, back.CaretOffset));
        Assert.Equal(profile, back.ConnectionProfileId);
        Assert.Equal("cp949", back.Encoding);
        Assert.Equal(vm.IsDirty, back.IsDirty);
    }

    [Fact]
    public void Restored_file_tab_without_readable_file_opens_dirty_so_user_is_warned()
    {
        var tab = new WorkspaceTab { Id = Guid.NewGuid(), Title = "gone.sql", FilePath = @"C:\nope\gone.sql" };
        var vm = SqlEditorViewModel.FromWorkspaceTab(tab, null);
        Assert.True(vm.IsDirty);
        var ok = SqlEditorViewModel.FromWorkspaceTab(tab, "select 1");
        Assert.False(ok.IsDirty);
        Assert.Equal("select 1", ok.Text);
    }

    // ---- 탭 관리·파일 --------------------------------------------------------------------

    [Fact]
    public void Host_numbers_new_tabs_and_reuses_free_numbers()
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument();
        var b = host.NewDocument();
        Assert.Equal(["SQL 1", "SQL 2"], host.Tabs.Select(t => t.Title));
        Assert.Same(b, host.Active);
        host.Close(a);
        Assert.Equal("SQL 1", host.NewDocument().Title);
    }

    [Fact]
    public void Closing_active_tab_activates_neighbor_and_reopen_restores_content()
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument("aaa");
        var b = host.NewDocument("bbb");
        var c = host.NewDocument("ccc");
        host.Active = b;
        host.Close(b);
        Assert.Same(c, host.Active);
        host.Close(c);
        Assert.Same(a, host.Active);

        Assert.True(host.CanReopenClosed);
        Assert.Equal("ccc", host.ReopenClosed()!.Text);
        Assert.Equal("bbb", host.ReopenClosed()!.Text);
        Assert.Null(host.ReopenClosed());
        host.Close(a);
        host.Close(host.Active!);
        host.Close(host.Active!);
        Assert.Null(host.Active);
    }

    [Fact]
    public void Duplicate_copies_text_into_new_untitled_tab()
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument("select 1");
        a.ConnectionProfileId = Guid.NewGuid();
        var copy = host.Duplicate(a);
        Assert.NotEqual(a.TabId, copy.TabId);
        Assert.Equal("select 1", copy.Text);
        Assert.Equal(a.ConnectionProfileId, copy.ConnectionProfileId);
        Assert.Null(copy.FilePath);
        Assert.Same(copy, host.Active);
    }

    [Fact]
    public Task Open_edit_save_round_trip_keeps_bom_and_dedupes_open_tabs() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "q.sql");
        await File.WriteAllBytesAsync(path, FileEncoding.Encode("select '한글' from dual;", "utf-8-bom"));
        var host = new SqlEditorHost();

        var tab = (await host.OpenFileAsync(path, default))!;
        Assert.Equal("utf-8-bom", tab.Encoding);
        Assert.Equal("select '한글' from dual;", tab.Text);
        Assert.False(tab.IsDirty);
        Assert.Equal("q.sql", tab.Title);
        Assert.Same(tab, await host.OpenFileAsync(path.ToUpperInvariant(), default)); // 같은 파일은 새 탭을 만들지 않는다
        Assert.Single(host.Tabs);

        tab.Document.Insert(0, "-- x\n");
        Assert.True(tab.IsDirty);
        Assert.True(await host.SaveAsync(tab, default));
        Assert.False(tab.IsDirty);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("-- x\nselect '한글' from dual;", FileEncoding.Decode(bytes).Text);
        Assert.Empty(Directory.GetFiles(_root, "*.oranda-tmp"));
    });

    [Fact]
    public void File_encoding_detects_cp949_utf16_and_plain_utf8()
    {
        Assert.Equal(("한글 select", "cp949"), FileEncoding.Decode(FileEncoding.Encode("한글 select", "cp949")));
        Assert.Equal(("한글", "utf-8"), FileEncoding.Decode(FileEncoding.Encode("한글", "utf-8")));
        Assert.Equal(("한글", "utf-16"), FileEncoding.Decode(FileEncoding.Encode("한글", "utf-16")));
        Assert.Equal(("한글", "utf-16be"), FileEncoding.Decode(FileEncoding.Encode("한글", "utf-16be")));
    }

    [Fact]
    public Task Save_as_uses_dialog_and_cancel_keeps_tab_unsaved() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var dialogs = new FakeDialogs();
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument("select 1");
        tab.Document.Insert(0, " ");

        Assert.False(await host.SaveAsync(tab, default)); // 취소
        Assert.True(tab.IsDirty);
        Assert.Null(tab.FilePath);

        dialogs.SavePath = Path.Combine(_root, "new.sql");
        Assert.True(await host.SaveAsync(tab, default));
        Assert.Equal(dialogs.SavePath, tab.FilePath);
        Assert.Equal("new.sql", tab.Title);
        Assert.False(tab.IsDirty);
        Assert.Equal(" select 1", await File.ReadAllTextAsync(dialogs.SavePath));
    });

    [Fact]
    public Task Save_all_saves_only_dirty_tabs() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var dialogs = new FakeDialogs { SavePath = Path.Combine(_root, "all.sql") };
        var host = new SqlEditorHost(dialogs: dialogs);
        var clean = host.NewDocument("clean");
        var dirty = host.NewDocument();
        dirty.Document.Insert(0, "dirty");
        Assert.True(await host.SaveAllAsync(default));
        Assert.False(dirty.IsDirty);
        Assert.Null(clean.FilePath);
        Assert.Equal(1, dialogs.SaveCalls);
    });

    // ---- 작업 공간 저장·복구(실제 WorkspaceStore) -------------------------------------------

    private IWorkspaceStore NewStore()
    {
        var type = typeof(PlatformAssembly).Assembly.GetType("Oranda.Platform.Storage.WorkspaceStore")!;
        return (IWorkspaceStore)Activator.CreateInstance(type, new RootPaths(_root))!;
    }

    [Fact]
    public Task Force_kill_then_restart_recovers_unsaved_tabs_from_autosave() => Ui.Run(async () =>
    {
        // 1) 실행 1: 탭 2개(하나는 미저장 수정). 자동 저장이 한 번 돈 뒤 프로세스가 죽는다(Shutdown 없음).
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "saved.sql");
        await File.WriteAllTextAsync(file, "select 'disk' from dual");
        var run1 = new SqlEditorHost(NewStore());
        var scratch = run1.NewDocument("select '미저장' from scratch");
        scratch.Select(7, 5);
        var fileTab = (await run1.OpenFileAsync(file, default))!;
        fileTab.Document.Insert(0, "/* edited */ ");
        run1.Active = scratch;
        Assert.True(await run1.AutosaveAsync(default));

        // 2) 실행 2: 같은 데이터 폴더. 자동 저장이 남았으니 복구를 묻고, 예 하면 그대로 돌아온다.
        var dialogs = new FakeDialogs { Confirm = true };
        var run2 = new SqlEditorHost(NewStore(), dialogs);
        Assert.True(await run2.InitializeAsync(default));

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal(2, run2.Tabs.Count);
        Assert.Equal("select '미저장' from scratch", run2.Tabs[0].Text);
        Assert.Equal((7, 5), (run2.Tabs[0].SelectionStart, run2.Tabs[0].SelectionLength));
        Assert.Equal("/* edited */ select 'disk' from dual", run2.Tabs[1].Text);
        Assert.True(run2.Tabs[1].IsDirty);
        Assert.Equal(file, run2.Tabs[1].FilePath);
        Assert.Same(run2.Tabs[0], run2.Active);
    });

    [Fact]
    public Task Declining_recovery_discards_autosave_and_clean_exit_restores_without_asking() => Ui.Run(async () =>
    {
        var run1 = new SqlEditorHost(NewStore());
        run1.NewDocument("select 1");
        await run1.AutosaveAsync(default);

        var no = new FakeDialogs { Confirm = false };
        var run2 = new SqlEditorHost(NewStore(), no);
        Assert.False(await run2.InitializeAsync(default));
        Assert.Empty(run2.Tabs);
        Assert.Null(await NewStore().LoadAutosaveAsync("default", default)); // 거절하면 자동 저장을 지운다

        // 정상 종료: 작업 공간은 저장되고 자동 저장은 지워진다. 다음 시작은 묻지 않고 탭을 연다.
        run2.NewDocument("select 2");
        await run2.AutosaveAsync(default);
        await run2.ShutdownAsync(default);
        Assert.Null(await NewStore().LoadAutosaveAsync("default", default));

        var yes = new FakeDialogs { Confirm = true };
        var run3 = new SqlEditorHost(NewStore(), yes);
        Assert.False(await run3.InitializeAsync(default));
        Assert.Equal(0, yes.ConfirmCalls);
        Assert.Equal("select 2", Assert.Single(run3.Tabs).Text);
    });

    [Fact]
    public Task Autosave_skips_when_nothing_changed_and_writes_after_edit() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(NewStore());
        var tab = host.NewDocument("a");
        Assert.True(await host.AutosaveAsync(default));
        Assert.False(await host.AutosaveAsync(default));
        tab.Document.Insert(1, "b");
        Assert.True(await host.AutosaveAsync(default));
        Assert.Equal("ab", (await NewStore().LoadAutosaveAsync("default", default))!.Tabs[0].Content);
    });

    [Fact]
    public Task Save_and_open_named_workspace() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs { Prompt = "My Project 1!" };
        var store = NewStore();
        var host = new SqlEditorHost(store, dialogs);
        host.NewDocument("select 1");
        host.NewDocument("select 2");
        Assert.True(await host.SaveWorkspaceAsync(default));
        Assert.Equal("my-project-1", host.WorkspaceId);

        var other = new SqlEditorHost(store, new FakeDialogs { Choice = "my-project-1" });
        Assert.True(await other.OpenWorkspaceAsync(default));
        Assert.Equal(["select 1", "select 2"], other.Tabs.Select(t => t.Text));
        Assert.Equal("My Project 1!", other.WorkspaceName);
    });

    // ---- 여러 작업 공간(오렌지 SQL Tool 창 여러 개, 작업 공간마다 SQL 탭 여러 개) ----------------------

    [Fact]
    public Task Workspaces_keep_their_own_tabs_and_switching_moves_the_active_tab() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var a1 = host.NewDocument("select 1");
        var first = host.ActiveWorkspace!;
        Assert.Equal(SqlEditorHost.DefaultWorkspaceId, first.Id);
        Assert.Equal("기본", first.Name);

        var second = host.NewWorkspace();
        var b1 = host.Active!;
        var b2 = host.NewDocument("select 2");

        Assert.Equal(2, host.Workspaces.Count);
        Assert.Equal([a1], first.Tabs);
        Assert.Equal([b1, b2], second.Tabs);
        Assert.Equal([a1, b1, b2], host.Tabs);
        Assert.Equal("SQL 1", b1.Title); // 탭 번호는 작업 공간마다 따로 센다
        Assert.Same(b2, host.Active);

        host.ActiveWorkspace = first;
        Assert.Same(a1, host.Active);
        host.Active = b1; // 다른 작업 공간의 탭을 고르면 그 작업 공간으로 옮겨 간다
        Assert.Same(second, host.ActiveWorkspace);
        Assert.Same(b1, second.ActiveTab);

        second.ActiveTab = b2; // 탭 줄에서 고르기
        Assert.Same(b2, host.Active);
        first.ActiveTab = a1; // 보이지 않는 작업 공간의 탭 줄은 앱의 활성 탭을 바꾸지 않는다
        Assert.Same(b2, host.Active);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Closing_last_tab_keeps_the_workspace_and_new_sql_goes_into_it() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        host.NewDocument("a");
        var second = host.NewWorkspace();
        host.Close(host.Active!);

        Assert.True(second.IsEmpty);
        Assert.Same(second, host.ActiveWorkspace);
        Assert.Null(host.Active);
        Assert.False(host.GetContextKeys()["sqlDocActive"]);
        Assert.True(host.GetContextKeys()["sqlWorkspaceActive"]);

        var again = host.NewDocument("b");
        Assert.Equal([again], second.Tabs);
        Assert.True(second.CloseActiveInnerTab());
        Assert.False(second.CloseActiveInnerTab()); // 닫을 탭이 없으면 셸이 작업 공간 문서를 닫는다
        return Task.CompletedTask;
    });

    [Fact]
    public Task Inner_tab_cycling_stops_at_the_edges() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument("a");
        var b = host.NewDocument("b");
        var ws = host.ActiveWorkspace!;

        Assert.False(ws.CycleInnerTab(1)); // b 가 마지막이다
        Assert.True(ws.CycleInnerTab(-1));
        Assert.Same(a, host.Active);
        Assert.False(ws.CycleInnerTab(-1));
        ws.SelectEdgeInnerTab(first: false);
        Assert.Same(b, host.Active);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Workspace_file_and_mybatis_mapper_open_as_one_workspace_each() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var md = Path.Combine(_root, "월말 점검.md");
        await File.WriteAllTextAsync(md, WorkspaceFiles.WriteMarkdown("월말 점검", [new("매출", "select 1 from dual"), new("재고", "select 2 from dual")]));
        var xml = Path.Combine(_root, "UserMapper.xml");
        await File.WriteAllTextAsync(xml, """
            <mapper namespace="user">
              <select id="findAll">select * from users</select>
              <delete id="remove">delete from users where id = #{id}</delete>
            </mapper>
            """);
        var host = new SqlEditorHost();
        host.NewDocument("select 0 from dual");

        await host.OpenWorkspaceFileAsync(md, default);
        var month = host.ActiveWorkspace!;
        await host.OpenWorkspaceFileAsync(xml, default);
        var mapper = host.ActiveWorkspace!;

        Assert.Equal(3, host.Workspaces.Count);
        Assert.Equal("월말 점검", month.Name);
        Assert.Equal(["매출", "재고"], month.Tabs.Select(t => t.Title));
        Assert.Equal(Path.GetFullPath(md), month.SourcePath);
        Assert.Equal(2, mapper.Tabs.Count);
        Assert.Equal("UserMapper", mapper.Name);
        Assert.Matches("^[a-z0-9][a-z0-9-]{0,63}$", month.Id);
        Assert.NotEqual(month.Id, mapper.Id);
        Assert.Null(host.WorkspaceFilePath); // MyBatis 매퍼에는 다시 쓰지 않는다(Markdown 으로 저장할 때 경로를 묻는다)

        // 같은 파일을 다시 열면 새로 만들지 않고 그 작업 공간으로 옮겨 간다.
        await host.OpenWorkspaceFileAsync(md, default);
        Assert.Equal(3, host.Workspaces.Count);
        Assert.Same(month, host.ActiveWorkspace);
        Assert.Equal(Path.GetFullPath(md), host.WorkspaceFilePath);
    });

    [Fact]
    public Task Closing_a_workspace_saves_it_and_it_can_be_opened_again() => Ui.Run(async () =>
    {
        var store = NewStore();
        var host = new SqlEditorHost(store, new FakeDialogs { Choice = "workspace-2" });
        var a = host.NewDocument("select 'a' from dual");
        var second = host.NewWorkspace();
        host.Active!.Document.Insert(0, "select 'b' from dual");
        Assert.Equal("workspace-2", second.Id);
        await host.AutosaveAsync(default);

        await host.CloseWorkspaceAsync(second, default);

        Assert.Single(host.Workspaces);
        Assert.Equal([a], host.Tabs);
        Assert.Same(a, host.Active);
        Assert.Null(await NewStore().LoadAutosaveAsync("workspace-2", default)); // 닫으면 자동 저장을 지운다(복구 때 다시 열리지 않게)
        var saved = (await NewStore().LoadAsync("workspace-2", default))!.Value;
        Assert.Equal("select 'b' from dual", Assert.Single(saved.Tabs).Content);

        Assert.True(await host.OpenWorkspaceAsync(default));
        Assert.Equal(2, host.Workspaces.Count);
        Assert.Equal("select 'b' from dual", host.Active!.Text);
    });

    [Fact]
    public Task New_workspace_does_not_reuse_the_id_of_a_saved_closed_workspace() => Ui.Run(() =>
    {
        var host = new SqlEditorHost { ListWorkspaceIds = () => ["default", "workspace-2"] };
        var ws = host.EnsureWorkspace();
        Assert.Equal("workspace-3", ws.Id); // 닫아 둔 default·workspace-2 파일을 덮어쓰지 않는다
        return Task.CompletedTask;
    });

    [Fact]
    public Task Crash_recovery_restores_every_open_workspace_in_their_order() => Ui.Run(async () =>
    {
        var run1 = new SqlEditorHost(NewStore());
        run1.NewDocument("select 1");
        var second = run1.NewWorkspace("보고서");
        run1.Active!.Document.Insert(0, "select 2");
        Assert.True(await run1.AutosaveAsync(default));

        var dialogs = new FakeDialogs { Confirm = true };
        var run2 = new SqlEditorHost(NewStore(), dialogs)
        {
            OpenWorkspaceIds = [second.Id, "default"],
            LastWorkspaceId = "default",
        };
        Assert.True(await run2.InitializeAsync(default));

        Assert.Equal(1, dialogs.ConfirmCalls); // 작업 공간이 여러 개여도 한 번만 묻는다
        Assert.Equal(["보고서", "기본"], run2.Workspaces.Select(w => w.Name));
        Assert.Equal("default", run2.ActiveWorkspace!.Id);
        Assert.Equal(["select 1", "select 2"], run2.Tabs.Select(t => t.Text).Order());
    });

    [Fact]
    public Task Clean_restart_reopens_the_workspaces_that_were_open() => Ui.Run(async () =>
    {
        var run1 = new SqlEditorHost(NewStore());
        run1.NewDocument("select 1");
        var report = run1.NewWorkspace("보고서");
        run1.Active!.Document.Insert(0, "select 2");
        await run1.ShutdownAsync(default);
        Assert.Empty(await NewStore().ListAutosaveIdsAsync(default));

        var dialogs = new FakeDialogs { Confirm = true };
        var run2 = new SqlEditorHost(NewStore(), dialogs)
        {
            OpenWorkspaceIds = ["default", report.Id],
            LastWorkspaceId = report.Id,
        };
        Assert.False(await run2.InitializeAsync(default));

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Equal(["기본", "보고서"], run2.Workspaces.Select(w => w.Name));
        Assert.Equal("select 2", run2.Active!.Text);
    });

    [Fact]
    public Task Saving_under_the_name_of_a_closed_saved_workspace_does_not_overwrite_it() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(NewStore(), new FakeDialogs { Prompt = "Report" }) { ListWorkspaceIds = () => ["report"] };
        host.NewDocument("select 1");

        Assert.True(await host.SaveWorkspaceAsync(default));

        Assert.Equal("report-2", host.WorkspaceId);
        Assert.Equal("Report", host.WorkspaceName);
    });

    [Fact]
    public Task Workspace_events_see_the_new_active_tab_and_closed_tabs_are_never_active() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(NewStore());
        var a = host.NewDocument("a");
        var second = host.NewWorkspace();
        var seen = new List<SqlEditorViewModel?>();
        host.ActiveWorkspaceChanged += (_, _) => seen.Add(host.Active);
        var activeWhenClosed = new List<bool>();
        host.TabClosed += (_, t) => activeWhenClosed.Add(ReferenceEquals(host.Active, t));

        host.ActiveWorkspace = host.Workspaces[0];
        Assert.Equal([a], seen); // 알림을 받을 때 Active 는 이미 새 작업 공간의 탭이다

        host.ActiveWorkspace = second;
        await host.CloseWorkspaceAsync(second, default);
        Assert.Equal([false], activeWhenClosed);
        Assert.Same(a, host.Active);
    });

    [Fact]
    public Task Renaming_a_workspace_or_switching_tabs_is_autosaved() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost(NewStore(), new FakeDialogs { Prompt = "월말" });
        var a = host.NewDocument("a");
        host.NewDocument("b");
        Assert.True(await host.AutosaveAsync(default));

        await host.RenameWorkspaceAsync(host.ActiveWorkspace!, default);
        Assert.True(await host.AutosaveAsync(default));
        Assert.Equal("월말", (await NewStore().LoadAutosaveAsync("default", default))!.Name);

        host.Active = a;
        Assert.True(await host.AutosaveAsync(default));
        Assert.Equal(a.TabId, (await NewStore().LoadAutosaveAsync("default", default))!.ActiveTabId);
        Assert.False(await host.AutosaveAsync(default));
    });

    [Theory]
    [InlineData("My Project 1!", "my-project-1")]
    [InlineData("  --A--B--  ", "a-b")]
    [InlineData("한글", "workspace-")]
    public void Workspace_slug_matches_id_rule(string name, string prefix)
    {
        var slug = SqlEditorHost.SlugOf(name);
        Assert.StartsWith(prefix, slug, StringComparison.Ordinal);
        Assert.Matches("^[a-z0-9][a-z0-9-]{0,63}$", slug);
    }

    [Fact]
    public void Context_keys_follow_active_tab_state()
    {
        var host = new SqlEditorHost();
        Assert.False(host.GetContextKeys()["hasDocument"]);
        var tab = host.NewDocument("abc");
        var keys = host.GetContextKeys();
        Assert.True(keys["hasDocument"]);
        Assert.True(keys["sqlDocActive"]);
        Assert.False(keys["hasSelection"]);
        Assert.False(keys["canUndo"]);

        tab.Select(0, 2);
        tab.Document.Insert(0, "x");
        tab.FindText = "b";
        tab.CaretOffset = 0;
        keys = host.GetContextKeys();
        Assert.True(keys["canUndo"]);
        Assert.True(keys["hasFindText"]);
        Assert.False(keys["hasBookmark"]);
        tab.IsReadOnly = true;
        Assert.True(host.GetContextKeys()["readOnlyDoc"]);
    }

    // ---- 명령 처리기 ---------------------------------------------------------------------

    private static Task Run(SqlEditorHost host, string id, object? parameter = null)
        => new SqlEditorCommandHandler { Host = host }.ExecuteAsync(new CommandContext(id, FakeContextKeys.AllTrue, parameter), default);

    [Fact]
    public Task Commands_drive_the_active_editor() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost();
        await Run(host, "oranda.file.newSqlEditor");
        var tab = host.Active!;
        tab.Document.Insert(0, "select a\nfrom b");
        tab.Select(0, tab.Document.TextLength);

        await Run(host, "oranda.edit.toggleLineComment");
        Assert.Equal("-- select a\n-- from b", tab.Text);
        await Run(host, "oranda.edit.toggleLineComment");
        Assert.Equal("select a\nfrom b", tab.Text);

        await Run(host, "oranda.edit.upperCase");
        Assert.Equal("SELECT A\nFROM B", tab.Text);
        await Run(host, "oranda.edit.undo");
        Assert.Equal("select a\nfrom b", tab.Text);
        await Run(host, "oranda.edit.redo");
        Assert.Equal("SELECT A\nFROM B", tab.Text);

        await Run(host, "oranda.edit.goToLine", 2);
        Assert.Equal(2, tab.CaretPosition.Line);
        await Run(host, "oranda.edit.duplicateLine");
        Assert.Equal("SELECT A\nFROM B\nFROM B", tab.Text);
        await Run(host, "oranda.edit.deleteLine");
        Assert.Equal("SELECT A\nFROM B", tab.Text);

        await Run(host, "oranda.edit.toggleBookmark");
        Assert.True(tab.HasBookmark);
        await Run(host, "oranda.file.duplicateTab");
        Assert.Equal(2, host.Tabs.Count);
        await Run(host, "oranda.file.reopenClosed"); // 닫은 탭이 없으니 아무 일 없음
        Assert.Equal(2, host.Tabs.Count);
    });

    [Fact]
    public Task Commands_without_tabs_do_nothing_and_outline_command_raises_request() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost();
        await Run(host, "oranda.edit.upperCase");
        await Run(host, "oranda.edit.fold");
        await Run(host, "oranda.file.save");
        int requests = 0;
        host.OutlineRequested += (_, _) => requests++;
        await Run(host, "oranda.view.outline");
        await Run(host, "oranda.nav.documentOutline");
        Assert.Equal(2, requests);
    });

    [Fact]
    public Task Find_commands_open_panel_and_navigate() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument("one two one");
        await Run(host, "oranda.edit.find");
        Assert.True(tab.IsFindPanelOpen);
        tab.FindText = "one";
        await Run(host, "oranda.edit.findNext");
        Assert.Equal(0, tab.SelectionStart);
        await Run(host, "oranda.edit.findNext");
        Assert.Equal(8, tab.SelectionStart);
        await Run(host, "oranda.edit.findPrevious");
        Assert.Equal(0, tab.SelectionStart);
        await Run(host, "oranda.edit.replace");
        Assert.True(tab.IsReplaceMode);
    });

    // ---- IEditorContext ------------------------------------------------------------------

    [Fact]
    public void Editor_context_reads_active_document_and_current_statement()
    {
        var host = new SqlEditorHost();
        var ctx = new EditorContext { Host = host };
        Assert.Null(ctx.GetActiveDocument());
        Assert.Null(ctx.GetCurrentStatement());

        var tab = host.NewDocument("select 1 from dual;\nupdate t set a = 2;");
        var profile = Guid.NewGuid();
        tab.ConnectionProfileId = profile;
        tab.Select(28, 3);

        var snap = ctx.GetActiveDocument()!;
        Assert.Equal(tab.TabId, snap.TabId);
        Assert.Equal(tab.Text, snap.Text);
        Assert.Equal(new TextRange(28, 3), snap.Selection);
        Assert.Equal(31, snap.CaretOffset);
        Assert.Equal(profile, snap.ConnectionProfileId);
        Assert.Equal("SQL 1", snap.Title);

        var stmt = ctx.GetCurrentStatement()!;
        Assert.StartsWith("update t", stmt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public Task Propose_edit_applies_only_when_user_accepts_and_is_one_undo_step() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs();
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument("select * from t");
        var ctx = new EditorContext { Host = host };
        var edit = new TextEdit(new TextRange(7, 1), "a, b") { Description = "열 지정" };

        Assert.False(await ctx.ProposeEditAsync(edit, default)); // 거절
        Assert.Equal("select * from t", tab.Text);
        Assert.Equal(("*", "a, b", "열 지정"), (dialogs.PreviewBefore, dialogs.PreviewAfter, dialogs.PreviewTitle));

        dialogs.AcceptPreview = true;
        Assert.True(await ctx.ProposeEditAsync(edit, default));
        Assert.Equal("select a, b from t", tab.Text);
        tab.Document.UndoStack.Undo();
        Assert.Equal("select * from t", tab.Text);
    });

    [Fact]
    public Task Propose_edit_rejects_bad_range_read_only_stale_document_and_no_tab() => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs { AcceptPreview = true };
        var host = new SqlEditorHost(dialogs: dialogs);
        var ctx = new EditorContext { Host = host };
        Assert.False(await ctx.ProposeEditAsync(new TextEdit(new TextRange(0, 0), "x"), default)); // 탭 없음

        var tab = host.NewDocument("abc");
        Assert.False(await ctx.ProposeEditAsync(new TextEdit(new TextRange(2, 5), "x"), default)); // 범위 밖
        Assert.False(await ctx.ProposeEditAsync(new TextEdit(new TextRange(-1, 1), "x"), default));

        tab.IsReadOnly = true;
        Assert.False(await ctx.ProposeEditAsync(new TextEdit(new TextRange(0, 1), "x"), default));
        tab.IsReadOnly = false;

        dialogs.OnPreview = () => tab.Document.Insert(0, "zz"); // 미리보기 중 문서가 바뀜
        Assert.False(await ctx.ProposeEditAsync(new TextEdit(new TextRange(0, 1), "x"), default));
        Assert.Equal("zzabc", tab.Text);
        Assert.Equal(0, dialogs.SaveCalls);
    });

    [Fact]
    public Task Editor_context_has_no_execution_surface() => Ui.Run(async () =>
    {
        // 확장 지점은 읽기와 제안 삽입만. 실행(IExecutionService)을 이 계약에 섞지 않는다.
        var methods = typeof(IEditorContext).GetMethods().Select(m => m.Name).Order().ToList();
        Assert.Equal(["GetActiveDocument", "GetCurrentStatement", "ProposeEditAsync"], methods);
        Assert.NotNull(typeof(EditorContext).GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "OrandaServiceAttribute"));
        await Task.CompletedTask;
    });

    // ---- 도우미 --------------------------------------------------------------------------

    private sealed class FakeDialogs : IEditorDialogs
    {
        public string? SavePath { get; set; }

        public string? Prompt { get; set; }

        public string? Choice { get; set; }

        public bool Confirm { get; set; }

        public bool AcceptPreview { get; set; }

        public int SaveCalls { get; private set; }

        public int ConfirmCalls { get; private set; }

        public string? PreviewBefore { get; private set; }

        public string? PreviewAfter { get; private set; }

        public string? PreviewTitle { get; private set; }

        public Action? OnPreview { get; set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct)
        {
            if (SavePath is not null)
            {
                SaveCalls++;
            }

            return Task.FromResult(SavePath);
        }

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult(Prompt);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult(Choice);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct)
        {
            ConfirmCalls++;
            return Task.FromResult(Confirm);
        }

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct)
        {
            (PreviewTitle, PreviewBefore, PreviewAfter) = (title, before, after);
            OnPreview?.Invoke();
            return Task.FromResult(AcceptPreview);
        }
    }

    private sealed class RootPaths(string root) : IAppDataPaths
    {
        public string DataDirectory => root;

        public string SettingsFile => Path.Combine(root, "settings.json");

        public string ConnectionsFile => Path.Combine(root, "connections.json");

        public string LayoutFile => Path.Combine(root, "layout.json");

        public string FormatterDirectory => Path.Combine(root, "formatter");

        public string KeymapDirectory => Path.Combine(root, "keymaps");

        public string WorkspacesDirectory => Path.Combine(root, "workspaces");

        public string AutosaveDirectory => Path.Combine(root, "autosave");

        public string DatabaseFile => Path.Combine(root, "oranda.db");

        public string CredentialsDirectory => Path.Combine(root, "credentials");

        public string LogsDirectory => Path.Combine(root, "logs");

        public string BackupDirectory => Path.Combine(root, "backup");
    }
}
