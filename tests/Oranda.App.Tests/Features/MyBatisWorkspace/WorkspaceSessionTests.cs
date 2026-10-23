using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-01 F1·F5: .xml 열기 훅, MyBatis 세션(문장 탭 ↔ 문장, 수정 표시, 새 문장·id 바꾸기·지우기, 편집 목록, 저장·병합 뒤 기준), 복구 곁 파일.
/// </summary>
public sealed class WorkspaceSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbws-" + Guid.NewGuid().ToString("N"));

    public WorkspaceSessionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteMapper(string text, string name = "EmpMapper.xml")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<(SqlEditorHost Host, SqlWorkspaceViewModel Ws, MyBatisWorkspaceSession Session)> OpenAsync(string? text = null, SqlEditorHost? host = null)
    {
        host ??= new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var path = WriteMapper(text ?? MapperSamples.EmpMapper);
        await host.OpenWorkspaceFileAsync(path, default);
        var ws = host.ActiveWorkspace!;
        return (host, ws, (MyBatisWorkspaceSession)ws.MyBatis!);
    }

    // ---- 열기 ----------------------------------------------------------------------------------

    [Fact]
    public Task Opening_a_mapper_makes_a_mybatis_workspace_with_one_entry_per_element_and_the_first_statement_open() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync();

        Assert.Equal(SqlWorkspaceKind.MyBatis, ws.DocumentKind);
        Assert.Equal("EmpMapper", ws.Name);
        Assert.Equal("com.acme.hr.EmpMapper", session.MapperNamespace);
        Assert.Equal(8, session.VisibleEntries.Count()); // sql 1 · resultMap 1 · select 3 · insert 1 · update 1 · delete 1
        Assert.Equal(["empCols", "empMap", "selectEmpList", "selectEmp", "countEmp", "insertEmp", "updateEmpSal", "deleteEmp"], session.VisibleEntries.Select(e => e.Id));
        var tab = Assert.Single(ws.Tabs);
        Assert.Equal("selectEmpList", tab.Title);
        Assert.StartsWith("<select id=\"selectEmpList\"", tab.Text, StringComparison.Ordinal);
        Assert.False(tab.IsDirty);
        Assert.Null(tab.FilePath); // design.md 2.2: 복원이 파일 전체를 다시 읽지 않게
        Assert.False(session.IsDirty);
        Assert.Single(host.Workspaces);
    });

    [Fact]
    public Task Opening_the_same_mapper_again_moves_to_its_workspace() => Ui.Run(async () =>
    {
        var (host, ws, _) = await OpenAsync();
        var other = host.NewWorkspace("other");
        Assert.Same(other, host.ActiveWorkspace);

        await host.OpenWorkspaceFileAsync(ws.SourcePath, default);
        Assert.Same(ws, host.ActiveWorkspace);
        await host.OpenFileAsync(ws.SourcePath, default);
        Assert.Same(ws, host.ActiveWorkspace);
        Assert.Equal(2, host.Workspaces.Count);
    });

    [Fact]
    public Task A_broken_or_non_mapper_xml_is_reported_and_no_workspace_is_made() => Ui.Run(async () =>
    {
        var dialogs = new RecordingDialogs();
        var host = new SqlEditorHost(dialogs: dialogs) { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };

        await host.OpenWorkspaceFileAsync(WriteMapper("<mapper><select id=\"a\">", "Broken.xml"), default);
        await host.OpenWorkspaceFileAsync(WriteMapper("<beans/>", "Beans.xml"), default);

        Assert.Empty(host.Workspaces);
        Assert.Equal(2, dialogs.Messages.Count);
    });

    [Fact]
    public Task Statements_with_the_same_id_for_different_databases_are_listed_but_not_editable() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync("""
            <mapper namespace="t.Db">
              <select id="now" databaseId="oracle">SELECT SYSDATE FROM DUAL</select>
              <select id="now" databaseId="mysql">SELECT NOW()</select>
            </mapper>
            """);

        var both = session.VisibleEntries.ToList();
        Assert.Equal(2, both.Count);
        Assert.All(both, e => Assert.True(e.IsAmbiguous));
        Assert.All(both, e => Assert.False(e.IsEditable));
        Assert.True(session.OpenStatement("now")!.IsReadOnly);
    });

    // ---- 탭·수정 표시 ---------------------------------------------------------------------------

    [Fact]
    public Task Editing_a_statement_marks_the_tab_and_session_dirty_and_undoing_clears_it() => Ui.Run(async () =>
    {
        var (_, ws, session) = await OpenAsync();
        var tab = session.OpenStatement("countEmp")!;
        var changes = 0;
        session.StateChanged += (_, _) => changes++;

        tab.Document.Insert(tab.Document.TextLength - "</select>".Length, "  -- x\n");
        Assert.True(tab.IsDirty);
        Assert.True(session.Find("countEmp")!.IsModified);
        Assert.True(session.IsDirty);
        Assert.True(ws.IsDirty);
        Assert.True(changes > 0);

        tab.Document.UndoStack.Undo();
        Assert.False(tab.IsDirty); // 기준 원문과 같아지면 수정 표시가 내려간다
        Assert.False(session.IsDirty);
    });

    [Fact]
    public Task Closing_an_edited_statement_tab_keeps_the_edit_and_reopening_shows_it_dirty() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync();
        var tab = session.OpenStatement("deleteEmp")!;
        tab.Document.Replace(0, 0, "<!-- 고침 -->");
        var edited = tab.Text;

        await host.CloseAsync(tab, default);
        Assert.DoesNotContain(tab, ws.Tabs);
        Assert.True(session.IsDirty);
        Assert.Equal(edited, session.ElementOf("deleteEmp")!.Text);

        var again = session.OpenStatement("deleteEmp")!;
        Assert.NotSame(tab, again);
        Assert.Equal(edited, again.Text);
        Assert.True(again.IsDirty);
        Assert.Same(again, session.OpenStatement("deleteEmp")); // 이미 열려 있으면 그 탭
    });

    [Fact]
    public Task Selected_statement_follows_the_active_tab_and_explicit_selection() => Ui.Run(async () =>
    {
        var (_, ws, session) = await OpenAsync();
        Assert.Equal("selectEmpList", session.SelectedStatementId);

        session.SelectStatement("empCols");
        Assert.Equal("empCols", session.SelectedStatementId);
        session.OpenStatement("insertEmp");
        Assert.Equal("insertEmp", session.SelectedStatementId);
        Assert.Equal("insertEmp", session.StatementIdOf(ws.ActiveTab!));
    });

    [Fact]
    public Task Result_maps_and_other_elements_are_listed_but_do_not_open() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();

        Assert.Null(session.OpenStatement("empMap"));
        Assert.False(session.Find("empMap")!.CanOpen);
        Assert.NotNull(session.OpenStatement("empCols")); // sql 조각은 편집할 수 있다
        Assert.Equal(2, session.UsageCount("empCols"));
    });

    // ---- 새 문장·id 바꾸기·지우기·편집 목록 -------------------------------------------------------

    [Fact]
    public Task New_statement_goes_after_its_kind_and_becomes_an_insert_edit() => Ui.Run(async () =>
    {
        var dialogs = new RecordingDialogs { Choice = "select", Prompt = "selectNew" };
        var host = new SqlEditorHost(dialogs: dialogs) { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var (_, ws, session) = await OpenAsync(host: host);

        await session.BeginNewStatementAsync(default);

        var entry = session.Find("selectNew")!;
        Assert.True(entry.IsNew);
        Assert.Equal(["selectEmpList", "selectEmp", "countEmp", "selectNew"], session.VisibleEntries.Where(e => e.Kind == MyBatisStatementKind.Select).Select(e => e.Id));
        Assert.Equal("selectNew", ws.ActiveTab!.Title);
        Assert.True(ws.ActiveTab.IsDirty);
        var insert = Assert.IsType<MapperEdit.InsertElement>(Assert.Single(session.PendingEdits()));
        Assert.Equal("countEmp", insert.AfterId);
        Assert.Contains("<select id=\"selectNew\">", insert.NewText, StringComparison.Ordinal);
    });

    [Fact]
    public Task New_statement_with_a_taken_or_bad_id_is_refused() => Ui.Run(async () =>
    {
        var dialogs = new RecordingDialogs { Choice = "insert", Prompt = "insertEmp" };
        var host = new SqlEditorHost(dialogs: dialogs) { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var (_, _, session) = await OpenAsync(host: host);

        await session.BeginNewStatementAsync(default);
        Assert.Single(dialogs.Messages);
        Assert.Throws<ArgumentException>(() => session.CreateStatement(MyBatisStatementKind.Insert, "9bad"));
        Assert.False(session.IsDirty);
    });

    [Fact]
    public Task Renaming_changes_the_id_attribute_tab_title_and_makes_a_rename_edit() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        var tab = session.OpenStatement("selectEmp")!;

        session.RenameStatement("selectEmp", "selectEmpByNo");

        Assert.Equal("selectEmpByNo", tab.Title);
        Assert.StartsWith("<select id=\"selectEmpByNo\"", tab.Text, StringComparison.Ordinal);
        Assert.Null(session.Find("selectEmp"));
        Assert.Equal(new MapperEdit.RenameElement("selectEmp", "selectEmpByNo"), Assert.Single(session.PendingEdits()));

        tab.Document.Insert(tab.Document.TextLength - "</select>".Length, " ");
        Assert.IsType<MapperEdit.ReplaceElement>(Assert.Single(session.PendingEdits())); // 원문도 고치면 바꾸기 하나
        Assert.Throws<ArgumentException>(() => session.RenameStatement("selectEmpByNo", "countEmp"));
    });

    [Fact]
    public Task Deleting_closes_the_tab_hides_the_entry_and_makes_a_delete_edit() => Ui.Run(async () =>
    {
        var (_, ws, session) = await OpenAsync();
        var tab = session.OpenStatement("updateEmpSal")!;

        session.DeleteStatement("updateEmpSal");

        Assert.DoesNotContain(tab, ws.Tabs);
        Assert.DoesNotContain(session.VisibleEntries, e => e.Id == "updateEmpSal");
        Assert.Equal(new MapperEdit.DeleteElement("updateEmpSal"), Assert.Single(session.PendingEdits()));

        var created = session.CreateStatement(MyBatisStatementKind.Delete, "deleteAll");
        session.DeleteStatement("deleteAll"); // 새 문장을 지우면 편집이 남지 않는다
        Assert.DoesNotContain(created, ws.Tabs);
        Assert.Single(session.PendingEdits());
    });

    [Fact]
    public Task Pending_edits_apply_cleanly_to_the_baseline_document() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        session.OpenStatement("countEmp")!.Document.Replace(0, 0, "<!-- c -->\n  ");
        session.RenameStatement("selectEmp", "selectOne");
        session.DeleteStatement("deleteEmp");
        session.CreateStatement(MyBatisStatementKind.Update, "touchEmp");

        var text = session.Document.Apply(session.PendingEdits());
        var reparsed = new MapperParser().Parse(text);

        Assert.Equal(
            ["empCols", "empMap", "selectEmpList", "selectOne", "countEmp", "insertEmp", "updateEmpSal", "touchEmp"],
            reparsed.Elements.Select(e => e.Id));
    });

    [Fact]
    public Task Setting_entry_texts_changes_open_tabs_and_closed_entries_with_one_state_change() => Ui.Run(async () =>
    {
        var (_, ws, session) = await OpenAsync();
        var list = ws.Tabs.Single(t => t.Title == "selectEmpList");
        var count = session.OpenStatement("countEmp")!;
        var delete = session.Find("deleteEmp")!;
        Assert.Null(delete.Tab);
        var before = (List: list.Text, Count: count.Text, Delete: delete.Text);
        int changed = 0;
        session.StateChanged += (_, _) => changed++;

        session.SetEntryTexts(
        [
            (session.Find("selectEmpList")!, list.Text.Replace("FROM EMP", "FROM EMP E2", StringComparison.Ordinal)),
            (session.Find("countEmp")!, count.Text.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal)),
            (delete, delete.Text.Replace("DELETE FROM EMP", "DELETE\n  FROM EMP", StringComparison.Ordinal)),
        ]);

        Assert.Equal(1, changed);
        Assert.True(list.IsDirty);
        Assert.True(count.IsDirty);
        Assert.Contains("COUNT(1)", count.Text, StringComparison.Ordinal);
        Assert.Null(delete.Tab);
        Assert.Contains("DELETE\n  FROM EMP", delete.Text, StringComparison.Ordinal);
        Assert.Equal(["selectEmpList", "countEmp", "deleteEmp"], session.PendingEdits().Select(e => Assert.IsType<MapperEdit.ReplaceElement>(e).Id));

        // 열린 탭은 실행 취소 1단계로 돌아오고 * 가 내려간다.
        count.Document.UndoStack.Undo();
        Assert.Equal(before.Count, count.Text);
        Assert.False(count.IsDirty);

        // 원래 글로 다시 넣으면 닫힌 요소도 고침이 없어진다.
        session.SetEntryText(delete, before.Delete);
        Assert.False(delete.IsModified);
    });

    [Fact]
    public Task Setting_texts_with_a_deleted_entry_changes_nothing() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        var count = session.Find("countEmp")!;
        var deleted = session.Find("deleteEmp")!;
        var before = count.Text;
        session.DeleteStatement("deleteEmp");

        Assert.Throws<ArgumentException>(() => session.SetEntryTexts([(count, "x"), (deleted, "y")]));

        Assert.Equal(before, count.Text);
        Assert.Equal(new MapperEdit.DeleteElement("deleteEmp"), Assert.Single(session.PendingEdits()));
    });

    // ---- 저장·병합 뒤 기준 -----------------------------------------------------------------------

    [Fact]
    public Task Rebase_after_a_partial_save_clears_written_statements_and_keeps_the_rest() => Ui.Run(async () =>
    {
        var (_, ws, session) = await OpenAsync();
        var a = session.OpenStatement("countEmp")!;
        a.Document.Replace(0, 0, "<!-- a -->");
        var b = session.OpenStatement("deleteEmp")!;
        b.Document.Replace(0, 0, "<!-- b -->");
        var written = session.Document.Apply([session.PendingEdits().First(e => e is MapperEdit.ReplaceElement { Id: "countEmp" })]);

        session.Rebase(written, ["countEmp"]);

        Assert.Equal(written, session.Document.Text);
        Assert.False(session.Find("countEmp")!.IsModified);
        Assert.False(a.IsDirty);
        Assert.True(session.Find("deleteEmp")!.IsModified);
        Assert.True(b.IsDirty);
        Assert.Contains(a, ws.Tabs);
        Assert.Equal(new MapperEdit.ReplaceElement("deleteEmp", session.Find("deleteEmp")!.FileText), Assert.Single(session.PendingEdits()));
    });

    [Fact]
    public Task Disk_text_replaces_untouched_statements_and_keeps_mine() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        var mine = session.OpenStatement("deleteEmp")!;
        mine.Document.Replace(0, 0, "<!-- mine -->");
        var theirs = session.OpenStatement("countEmp")!;
        var disk = MapperSamples.EmpMapper.Replace("SELECT COUNT(*) FROM EMP", "SELECT COUNT(1) FROM EMP", StringComparison.Ordinal);

        session.ApplyDiskText(disk, ["deleteEmp"]);

        Assert.Contains("COUNT(1)", theirs.Text, StringComparison.Ordinal);
        Assert.False(theirs.IsDirty);
        Assert.StartsWith("<!-- mine -->", mine.Text, StringComparison.Ordinal);
        Assert.True(mine.IsDirty);
        Assert.Equal(new MapperEdit.ReplaceElement("deleteEmp", session.Find("deleteEmp")!.FileText), Assert.Single(session.PendingEdits()));
    });

    [Fact]
    public Task Fragments_resolve_from_the_current_text_of_the_same_mapper() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        session.OpenStatement("empCols")!.Document.Insert("<sql id=\"empCols\">".Length, "E.MGR, ");

        var local = session.Fragments.Resolve("empCols", "com.acme.hr.EmpMapper");
        var qualified = session.Fragments.Resolve("com.acme.hr.EmpMapper.empCols", null);

        Assert.NotNull(local);
        Assert.Contains("E.MGR", local.Text, StringComparison.Ordinal);
        Assert.Equal(local.Text, qualified!.Text);
        Assert.Null(session.Fragments.Resolve("other.Mapper.empCols", null));
    });

    [Fact]
    public Task Closing_the_workspace_detaches_the_session_from_its_tabs() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync();
        var tab = ws.Tabs[0];

        await host.CloseWorkspaceAsync(ws, default);

        Assert.Null(ws.MyBatis);
        tab.Document.Insert(0, "x"); // 떼어 낸 세션은 더 이상 탭을 따라가지 않는다
        Assert.False(session.IsDirty);
    });

    // ---- 복구(F5) ---------------------------------------------------------------------------------

    [Fact]
    public Task Recovery_restores_the_open_mapper_selected_statement_and_unsaved_edits() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var hooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler(), recoveryParticipants: [participant]);
        var (host, ws, session) = await OpenAsync(host: new SqlEditorHost { MyBatisHooks = hooks });
        var tab = session.OpenStatement("countEmp")!;
        tab.Document.Replace(0, 0, "<!-- 고침 -->");
        session.RenameStatement("selectEmp", "selectOne");
        session.DeleteStatement("deleteEmp");
        session.CreateStatement(MyBatisStatementKind.Insert, "insertMore");
        session.SelectStatement("countEmp");
        var expectedEdits = session.PendingEdits();
        var saved = ws.ToWorkspace();
        await participant.SaveAsync(ws, MyBatisRecoveryTarget.Autosave, default);
        Assert.True(File.Exists(participant.PathOf(ws.Id, MyBatisRecoveryTarget.Autosave)));

        // 앱을 다시 켠 것처럼 새 호스트에서 작업 공간 저장본을 되살린다.
        var again = new SqlEditorHost { MyBatisHooks = hooks };
        var restored = again.Restore(saved, MyBatisRecoveryTarget.Autosave);

        var back = Assert.IsType<MyBatisWorkspaceSession>(restored.MyBatis);
        Assert.Equal(SqlWorkspaceKind.MyBatis, restored.DocumentKind);
        Assert.Equal(session.MapperPath, back.MapperPath);
        Assert.Equal("countEmp", back.SelectedStatementId);
        Assert.Equal(expectedEdits, back.PendingEdits());
        var countTab = restored.Tabs.Single(t => t.Title == "countEmp");
        Assert.True(countTab.IsDirty);
        Assert.Same(countTab, back.OpenStatement("countEmp"));
        Assert.True(back.Find("insertMore")!.IsNew);
        Assert.Null(back.Find("deleteEmp"));

        await participant.DeleteAsync(ws.Id, MyBatisRecoveryTarget.Autosave, default);
        Assert.False(File.Exists(participant.PathOf(ws.Id, MyBatisRecoveryTarget.Autosave)));
    });

    [Fact]
    public Task Recovery_keeps_the_saved_baseline_when_the_mapper_changed_on_disk() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var hooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler(), recoveryParticipants: [participant]);
        var (_, ws, session) = await OpenAsync(host: new SqlEditorHost { MyBatisHooks = hooks });
        session.OpenStatement("deleteEmp")!.Document.Replace(0, 0, "<!-- mine -->");
        var saved = ws.ToWorkspace();
        await participant.SaveAsync(ws, MyBatisRecoveryTarget.Saved, default);
        File.WriteAllText(session.MapperPath, MapperSamples.EmpMapper.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));

        var restored = new SqlEditorHost { MyBatisHooks = hooks }.Restore(saved);

        var back = Assert.IsType<MyBatisWorkspaceSession>(restored.MyBatis);
        Assert.Equal(session.BaselineHash, back.BaselineHash); // 병합(5.3)은 03-03 감시가 한다
        Assert.Single(back.PendingEdits());
    });

    [Fact]
    public Task Recovery_without_a_side_file_or_for_a_plain_workspace_does_nothing() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(recoveryParticipants: [participant]) };
        var plain = host.NewWorkspace("plain");

        Assert.Null(participant.Fingerprint(plain));
        await participant.SaveAsync(plain, MyBatisRecoveryTarget.Saved, default);
        Assert.False(Directory.Exists(Path.Combine(_root, "ws")));
        var restored = host.Restore(new Oranda.Core.Workspaces.Workspace { Id = "m", Name = "m", SavedAt = DateTimeOffset.UtcNow, SourcePath = WriteMapper(MapperSamples.EmpMapper) });
        Assert.Null(restored.MyBatis);
    });

    [Fact]
    public Task Recovery_fingerprint_changes_with_session_only_edits() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var (_, ws, session) = await OpenAsync();
        var first = participant.Fingerprint(ws);

        session.SelectStatement("empCols");
        var second = participant.Fingerprint(ws);
        session.DeleteStatement("deleteEmp");

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, participant.Fingerprint(ws));
    });

    // ---- 리뷰 보강(F8) ---------------------------------------------------------------------------

    [Fact]
    public Task Moving_a_statement_tab_keeps_it_tracked() => Ui.Run(async () =>
    {
        var (host, ws, session) = await OpenAsync();
        session.OpenStatement("countEmp");
        var first = ws.Tabs[0];

        Assert.True(host.MoveTab(first, 1));
        first.Document.Insert(0, "<!-- x -->");

        Assert.True(session.Find("selectEmpList")!.IsModified);
        Assert.Same(first, session.OpenStatement("selectEmpList")); // 탭이 하나 더 생기지 않는다
        Assert.Equal(2, ws.Tabs.Count);
    });

    [Fact]
    public Task Renaming_a_statement_with_a_leading_comment_changes_its_id_attribute() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        var tab = session.OpenStatement("countEmp")!;
        tab.Document.Insert(0, "<!-- a > b -->\n");

        session.RenameStatement("countEmp", "countAll");

        Assert.Contains("<select id=\"countAll\"", tab.Text, StringComparison.Ordinal);
        var replace = Assert.IsType<MapperEdit.ReplaceElement>(Assert.Single(session.PendingEdits()));
        Assert.Equal("countEmp", replace.Id);
        Assert.Contains("countAll", replace.NewText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Editing_the_id_attribute_in_the_tab_and_undoing_a_rename_keep_the_list_id_in_step() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        var tab = session.OpenStatement("selectEmp")!;
        Assert.True(MyBatisStatementText.TryFindIdValue(tab.Text, out var start, out var length));

        tab.Document.Replace(start, length, "selectById");
        Assert.Equal("selectById", session.SelectedStatementId);
        Assert.Equal("selectById", tab.Title);
        Assert.NotNull(session.Find("selectById"));

        tab.Document.Replace(start, "selectById".Length, "countEmp"); // 겹치는 id 는 받지 않고 마지막 id 를 둔다
        Assert.Equal("selectById", session.Find("selectById")!.Id);

        tab.Document.UndoStack.Undo();
        tab.Document.UndoStack.Undo();
        Assert.Equal("selectEmp", tab.Title);
        Assert.False(session.IsDirty);

        session.RenameStatement("selectEmp", "selectOne");
        tab.Document.UndoStack.Undo();
        Assert.NotNull(session.Find("selectEmp"));
        Assert.Null(session.Find("selectOne"));
        Assert.False(session.IsDirty);
    });

    [Fact]
    public Task Disk_merge_keeps_an_unsaved_new_statement_even_outside_keep_mine() => Ui.Run(async () =>
    {
        var (_, ws, session) = await OpenAsync();
        var created = session.CreateStatement(MyBatisStatementKind.Select, "selectNew");

        session.ApplyDiskText(MapperSamples.EmpMapper, []);

        Assert.True(session.Find("selectNew")!.IsNew);
        Assert.Contains(created, ws.Tabs);
        Assert.IsType<MapperEdit.InsertElement>(Assert.Single(session.PendingEdits()));
    });

    [Fact]
    public Task Database_variants_open_their_own_text_and_a_select_sharing_an_id_with_a_result_map_stays_editable() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync("""
            <mapper namespace="t.Db">
              <resultMap id="emp" type="map"/>
              <select id="emp">SELECT 1 FROM DUAL</select>
              <select id="now" databaseId="oracle">SELECT SYSDATE FROM DUAL</select>
              <select id="now" databaseId="mysql">SELECT NOW()</select>
            </mapper>
            """);

        var mysql = session.VisibleEntries.Last();
        var tab = session.OpenEntry(mysql)!;
        Assert.Contains("NOW()", tab.Text, StringComparison.Ordinal);
        Assert.True(tab.IsReadOnly);

        var select = session.Find("emp")!;
        Assert.Equal(MyBatisStatementKind.Select, select.Kind);
        Assert.True(select.IsEditable);
        Assert.NotNull(session.OpenStatement("emp"));

        session.Rebase(session.Document.Text, []);
        Assert.Contains("NOW()", tab.Text, StringComparison.Ordinal); // 다시 이어도 두 번째 변형의 글
    });

    [Fact]
    public Task New_statement_lines_follow_the_mapper_indentation() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        session.CreateStatement(MyBatisStatementKind.Delete, "deleteAll");

        var text = session.Document.Apply(session.PendingEdits());

        Assert.Contains("\n  <delete id=\"deleteAll\">\n    \n  </delete>", text, StringComparison.Ordinal);
    });

    [Fact]
    public Task Rebase_keeps_the_byte_order_mark_of_the_mapper() => Ui.Run(async () =>
    {
        var path = Path.Combine(_root, "Bom.xml");
        File.WriteAllBytes(path, MapperSamples.Bytes(MapperSamples.EmpMapper, bom: true));
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        await host.OpenWorkspaceFileAsync(path, default);
        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        Assert.True(session.Document.Format.HasBom);

        session.Rebase(session.Document.Text, []);
        session.ApplyDiskText(session.Document.Text, []);

        Assert.True(session.Document.Format.HasBom);
    });

    [Fact]
    public Task Recovery_survives_a_broken_side_file_and_falls_back_to_the_saved_baseline_when_the_mapper_cannot_be_read() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var hooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler(), recoveryParticipants: [participant]);
        var (_, ws, session) = await OpenAsync(host: new SqlEditorHost { MyBatisHooks = hooks });
        session.DeleteStatement("deleteEmp");
        var saved = ws.ToWorkspace();
        await participant.SaveAsync(ws, MyBatisRecoveryTarget.Saved, default);
        var side = participant.PathOf(ws.Id, MyBatisRecoveryTarget.Saved);
        var good = File.ReadAllText(side);

        File.WriteAllText(session.MapperPath, "<mapper><select"); // 밖에서 쓰는 중
        var fallback = new SqlEditorHost { MyBatisHooks = hooks }.Restore(saved);
        var back = Assert.IsType<MyBatisWorkspaceSession>(fallback.MyBatis);
        Assert.Equal(session.BaselineHash, back.BaselineHash);
        Assert.Null(back.Find("deleteEmp"));

        File.WriteAllText(side, good.Replace("\"Entries\":[", "\"Entries\":[null,", StringComparison.Ordinal));
        Assert.NotNull(new SqlEditorHost { MyBatisHooks = hooks }.Restore(saved).MyBatis);

        File.WriteAllText(side, good[..(good.Length / 2)]);
        Assert.Null(new SqlEditorHost { MyBatisHooks = hooks }.Restore(saved).MyBatis);
    });

    [Fact]
    public Task Reopening_a_mapper_left_without_a_session_turns_it_into_a_mybatis_workspace() => Ui.Run(async () =>
    {
        var path = WriteMapper(MapperSamples.EmpMapper);
        var host = new SqlEditorHost();
        await host.OpenWorkspaceFileAsync(path, default); // 훅이 없을 때의 읽기 전용 펼침
        var old = Assert.Single(host.Workspaces);
        Assert.Null(old.MyBatis);

        host.MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler());
        await host.OpenFileAsync(path, default);

        Assert.Same(old, Assert.Single(host.Workspaces));
        Assert.IsType<MyBatisWorkspaceSession>(old.MyBatis);
        Assert.Equal("selectEmpList", Assert.Single(old.Tabs).Title);
    });

    [Fact]
    public Task Expanding_a_statement_uses_the_registered_evaluator_from_the_app_container() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        session.OpenStatement("selectEmp")!.Document.Replace(0, 0, "<!-- 고침 -->");
        using var services = Oranda.App.Shell.Composition.AppComposition.Create(
            new Oranda.App.Shell.Composition.AppCompositionOptions { DataDirectory = Path.Combine(_root, "data"), DevBuild = true });

        var sql = Oranda.App.Features.MyBatisWorkspace.Statements.MyBatisStatementExpander.Expand(session, "selectEmp", services);

        // 02-02 계산기(지연 어셈블리)가 같은 매퍼 조각(empCols)을 세션에서 찾아 펼친다.
        Assert.NotNull(sql);
        Assert.Contains("E.EMPNO, E.ENAME", sql, StringComparison.Ordinal);
        Assert.Contains(":empNo", sql, StringComparison.Ordinal);
    });

    /// <summary>대화 상자 가짜: 고르기·묻기 답을 정해 두고 알림을 모은다.</summary>
    private sealed class RecordingDialogs : IEditorDialogs
    {
        public string? Choice { get; set; }

        public string? Prompt { get; set; }

        public List<string> Messages { get; } = [];

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult(Prompt);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult(Choice);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
