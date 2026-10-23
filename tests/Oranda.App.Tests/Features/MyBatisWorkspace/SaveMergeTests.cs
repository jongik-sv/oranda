using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-03: 매퍼에 저장(바뀐 문장만 쓰기·바뀌는 부분 창·밖에서 바뀜 병합·감시·닫기 확인·복구). 소유 = TSK-03-03(docs/oranda1/design.md 「기능 Task 편집 지점」).
/// 세션은 03-01 구현과 같은 규칙(Rebase·ApplyDiskText 가 지금 id 로 맞대고 새 문장은 늘 남김)을 흉내 내는 <see cref="TestSession"/> 을 쓴다.
/// 파일은 임시 폴더에 쓰고 02-01 의 실제 파서·원자적 쓰기를 거친다.
/// </summary>
public sealed class SaveMergeTests : IDisposable
{
    private static readonly MapperParser Parser = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbsave-" + Guid.NewGuid().ToString("N"));

    public SaveMergeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- 줄 diff · 병합 계획(순수 함수) ------------------------------------------------------

    [Fact]
    public void LineDiff_marks_changed_lines_with_file_line_numbers_and_folds_far_context()
    {
        var old = string.Join('\n', Enumerable.Range(1, 12).Select(i => "line" + i));
        var now = old.Replace("line6", "line6!", StringComparison.Ordinal);

        var diff = MapperLineDiff.Build(old, now, oldFirstLine: 20, newFirstLine: 30);

        Assert.Equal((1, 1), MapperLineDiff.Count(diff));
        var removed = Assert.Single(diff, l => l.Kind == MapperDiffKind.Removed);
        Assert.Equal((25, (int?)null, "line6"), (removed.OldLine!.Value, removed.NewLine, removed.Text));
        var added = Assert.Single(diff, l => l.Kind == MapperDiffKind.Added);
        Assert.Equal(35, added.NewLine);
        Assert.Equal(MapperDiffKind.Gap, diff[0].Kind); // line1~2 는 3줄보다 멀다
        Assert.Equal(MapperDiffKind.Gap, diff[^1].Kind);
        Assert.Equal(1 + 3 + 2 + 3 + 1, diff.Count);
        var pair = Assert.Single(MapperLineDiff.SideBySide(diff), p => p.Left?.Kind == MapperDiffKind.Removed);
        Assert.Equal("line6!", pair.Right!.Text);
        Assert.Empty(MapperLineDiff.Build(old, old, 1, 1));
    }

    [Fact]
    public void MergePlan_keeps_my_edit_of_another_statement_and_reports_disk_changes()
    {
        var baseline = Parser.Parse(MapperSamples.EmpMapper);
        var disk = Parser.Parse(Change(MapperSamples.EmpMapper, "SELECT COUNT(*) FROM EMP", "SELECT COUNT(1) FROM EMP"));
        var mine = new List<MapperEdit> { Replace(baseline, "deleteEmp", "DELETE FROM EMP", "DELETE FROM EMP E") };

        var plan = MapperMerge.Plan(baseline, disk, mine, Parser);

        Assert.Empty(plan.Conflicts);
        Assert.Equal(["deleteEmp"], plan.KeepMine);
        Assert.Equal(["countEmp"], plan.DiskChanged);
    }

    [Fact]
    public void MergePlan_reports_same_statement_conflict_but_not_identical_edits()
    {
        var baseline = Parser.Parse(MapperSamples.EmpMapper);
        var diskText = Change(MapperSamples.EmpMapper, "SELECT COUNT(*) FROM EMP", "SELECT COUNT(1) FROM EMP");
        var disk = Parser.Parse(diskText);
        var different = Replace(baseline, "countEmp", "SELECT COUNT(*) FROM EMP", "SELECT COUNT(EMPNO) FROM EMP");
        var same = new MapperEdit.ReplaceElement("countEmp", disk.Find("countEmp")!.Text.ReplaceLineEndings("\r\n"));

        var conflict = Assert.Single(MapperMerge.Plan(baseline, disk, [different], Parser).Conflicts);
        Assert.Equal(("countEmp", "countEmp"), (conflict.Key, conflict.CurrentId));
        Assert.Contains("COUNT(1)", conflict.DiskText, StringComparison.Ordinal);
        Assert.Contains("COUNT(EMPNO)", conflict.MineText, StringComparison.Ordinal);

        var plan = MapperMerge.Plan(baseline, disk, [same], Parser);
        Assert.Empty(plan.Conflicts);
        Assert.Empty(plan.KeepMine); // 같은 글: 디스크 것을 받으면 된다
    }

    [Fact]
    public void MergePlan_conflicts_when_disk_deletes_my_edited_statement_and_reports_new_id_clash()
    {
        var baseline = Parser.Parse(MapperSamples.EmpMapper);
        var countEmp = baseline.Find("countEmp")!;
        var withoutCount = MapperSamples.EmpMapper.Remove(countEmp.ElementRange.Start, countEmp.ElementRange.Length);
        var diskText = withoutCount.Replace("</mapper>", "  <select id=\"selectNew\">SELECT 9 FROM DUAL</select>\n</mapper>", StringComparison.Ordinal);
        var disk = Parser.Parse(diskText);
        var mine = new List<MapperEdit>
        {
            Replace(baseline, "countEmp", "COUNT(*)", "COUNT(9)"),
            new MapperEdit.InsertElement("selectNew", "<select id=\"selectNew\">SELECT 1 FROM DUAL</select>"),
        };

        var plan = MapperMerge.Plan(baseline, disk, mine, Parser);

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Null(conflict.DiskText);
        Assert.Equal(["selectNew"], plan.NewIdClashes);
        Assert.Equal(["countEmp"], plan.KeepMineWith(new Dictionary<string, MapperMergeChoice>()).Except(["selectNew"]));
        Assert.DoesNotContain("countEmp", plan.KeepMineWith(new Dictionary<string, MapperMergeChoice> { ["countEmp"] = MapperMergeChoice.Disk }));
    }

    // ---- 저장: 바뀐 문장만 · 창 · 바로 저장 ---------------------------------------------------

    [Fact]
    public Task Save_two_statements_changes_only_the_bytes_inside_them() => Ui.Run(async () =>
    {
        var (_, ws, session, handler, _) = Setup(MapperSamples.EmpMapper);
        var original = File.ReadAllText(session.MapperPath);
        var count = session.Document.Find("countEmp")!;
        var delete = session.Document.Find("deleteEmp")!;
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(EMPNO)", StringComparison.Ordinal));
        session.Replace("deleteEmp", t => t.Replace("DELETE FROM EMP", "DELETE\n      FROM EMP", StringComparison.Ordinal));

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var expected = original[..count.ElementRange.Start]
            + count.Text.Replace("COUNT(*)", "COUNT(EMPNO)", StringComparison.Ordinal)
            + original[count.ElementRange.End..delete.ElementRange.Start]
            + delete.Text.Replace("DELETE FROM EMP", "DELETE\n      FROM EMP", StringComparison.Ordinal)
            + original[delete.ElementRange.End..];
        Assert.Equal(expected, File.ReadAllText(session.MapperPath));
        Assert.False(session.IsDirty);
        Assert.Equal(["countEmp", "deleteEmp"], session.LastRebase!.Value.Ids.Order(StringComparer.Ordinal));
        Assert.Equal(MapperHash.Compute(expected), session.BaselineHash);
    });

    [Fact]
    public Task Save_dialog_gets_items_with_check_results_and_writes_only_checked() => Ui.Run(async () =>
    {
        var (_, ws, session, handler, presenter) = Setup(MapperSamples.EmpMapper);
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        session.Insert("countDept", "<select id=\"countDept\" resultType=\"int\">\n  SELECT COUNT(*) FROM DEPT\n</select>");
        presenter.Answer = r => new SaveDiffResult([.. r.Items.Where(i => i.Key == "countDept")], r.ShowEveryTime);

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var request = Assert.Single(presenter.Requests);
        Assert.Equal("EmpMapper.xml", request.FileName);
        Assert.Equal([MapperSaveItemKind.Modified, MapperSaveItemKind.Added], request.Items.Select(i => i.Kind));
        Assert.Equal(8, request.UnchangedCount + request.Items.Count(i => i.Kind != MapperSaveItemKind.Added));
        var modified = request.Items[0];
        Assert.Equal((1, 1), (modified.Added, modified.Removed));
        var countLine = MapperSamples.EmpMapper[..MapperSamples.EmpMapper.IndexOf("<select id=\"countEmp\"", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        Assert.Equal((countLine, countLine + 2), (modified.FirstLine, modified.LastLine)); // 여는 줄 ~ 닫는 줄
        Assert.Equal(3, request.Items[1].Added);
        var check = request.Check(request.Items);
        Assert.True(check.CanWrite);
        Assert.False(check.ChangedOnDisk);

        var written = File.ReadAllText(session.MapperPath);
        Assert.Contains("countDept", written, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) FROM EMP", written, StringComparison.Ordinal); // 체크 안 한 고침은 쓰지 않는다
        Assert.True(session.IsDirty);
        Assert.Equal(["countEmp"], session.PendingEdits().Select(MapperSavePlan.KeyOf));
    });

    [Fact]
    public Task Save_dialog_cancel_writes_nothing_and_the_every_time_box_is_written_back() => Ui.Run(async () =>
    {
        var (_, ws, session, handler, presenter) = Setup(MapperSamples.EmpMapper);
        var before = File.ReadAllBytes(session.MapperPath);
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        presenter.Answer = _ => null;
        Assert.False(await handler.SaveAsync(ws, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(session.MapperPath));
        Assert.True(session.IsDirty);

        bool? stored = null;
        handler.WriteShowEveryTime = v =>
        {
            stored = v;
            return Task.CompletedTask;
        };
        presenter.Answer = r => new SaveDiffResult(r.Items, ShowEveryTime: false);
        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
        Assert.False(stored);
    });

    [Fact]
    public Task Save_without_dialog_when_every_time_is_off_but_shows_it_when_checks_fail() => Ui.Run(async () =>
    {
        var (_, ws, session, handler, presenter) = Setup(MapperSamples.EmpMapper);
        handler.ReadShowEveryTime = () => false;
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
        Assert.Empty(presenter.Requests);
        Assert.Contains("COUNT(1)", File.ReadAllText(session.MapperPath), StringComparison.Ordinal);

        // id 겹침: 창을 끄더라도 검사 결과를 보인다. 창에서 저장을 눌러도 쓰지 않는다.
        var before = File.ReadAllBytes(session.MapperPath);
        session.Insert("countEmp2", "<select id=\"selectEmp\">SELECT 2 FROM DUAL</select>");
        presenter.Answer = r => new SaveDiffResult(r.Items, r.ShowEveryTime);
        Assert.False(await handler.SaveAsync(ws, CancellationToken.None));
        var request = Assert.Single(presenter.Requests);
        Assert.Equal(["selectEmp"], request.Check(request.Items).DuplicateIds);
        Assert.Equal(before, File.ReadAllBytes(session.MapperPath));
        Assert.True(session.IsDirty);
        Assert.Contains(ws.Host.Dialogs is TestDialogs d ? d.Messages : [], m => m.Contains("selectEmp", StringComparison.Ordinal));
    });

    [Fact]
    public void Save_dialog_model_rechecks_on_every_tick_and_only_saves_a_checked_writable_set()
    {
        var doc = Parser.Parse(MapperSamples.EmpMapper);
        var edits = new List<MapperEdit>
        {
            Replace(doc, "countEmp", "COUNT(*)", "COUNT(1)"),
            new MapperEdit.InsertElement("selectEmp2", "<select id=\"selectEmp\">SELECT 2 FROM DUAL</select>"), // id 겹침
        };
        var items = MapperSavePlan.Build(doc, edits, Parser);
        var asked = new List<int>();
        var vm = new SaveDiffViewModel(new SaveDiffRequest("EmpMapper.xml", items, 7, chosen =>
        {
            asked.Add(chosen.Count);
            return doc.Check(doc.Apply([.. chosen.Select(i => i.Edit)]), doc.Hash);
        }, ShowEveryTime: true));

        Assert.Equal([2], asked);
        Assert.False(vm.CanSave); // 겹치는 id 가 든 채로는 저장 단추가 꺼진다
        Assert.Null(vm.BuildResult());

        vm.Items[1].IsChecked = false;
        Assert.Equal([2, 1], asked);
        Assert.True(vm.CanSave);
        vm.ShowEveryTime = false;
        var result = vm.BuildResult()!;
        Assert.Equal(["countEmp"], result.Checked.Select(i => i.Key));
        Assert.False(result.ShowEveryTime);

        vm.Items[0].IsChecked = false;
        Assert.False(vm.CanSave); // 하나도 체크하지 않으면 저장할 것이 없다
    }

    // ---- CRLF · BOM · 인코딩 · 쓰기 오류 -------------------------------------------------------

    [Fact]
    public Task Save_keeps_crlf_bom_and_untouched_bytes_even_when_the_edit_has_lf() => Ui.Run(async () =>
    {
        var bytes = MapperSamples.Bytes(MapperSamples.Crlf, bom: true);
        var (_, ws, session, handler, _) = Setup(bytes, "Crlf.xml");
        var a = session.Document.Find("a")!;
        session.Replace("a", t => t.ReplaceLineEndings("\n").Replace("SELECT 1", "SELECT 1,\n           2", StringComparison.Ordinal));

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var written = File.ReadAllBytes(session.MapperPath);
        Assert.Equal(bytes[..3], written[..3]); // BOM
        var text = Encoding.UTF8.GetString(written, 3, written.Length - 3);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var original = MapperSamples.Crlf;
        Assert.Equal(original[..a.ElementRange.Start], text[..a.ElementRange.Start]);
        Assert.EndsWith(original[a.ElementRange.End..], text, StringComparison.Ordinal);
        Assert.Contains("SELECT 1,\r\n           2", text, StringComparison.Ordinal);
    });

    [Fact]
    public Task Save_keeps_euc_kr_bytes_and_reports_unencodable_text_keeping_the_edit() => Ui.Run(async () =>
    {
        var cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)!;
        var source = MapperSamples.Korean.Replace("UTF-8", "EUC-KR", StringComparison.Ordinal);
        var (_, ws, session, handler, _) = Setup(cp949.GetBytes(source), "Korean.xml");
        session.Replace("byName", t => t.Replace("홍길동", "김철수", StringComparison.Ordinal));
        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
        Assert.Equal(cp949.GetBytes(source.Replace("홍길동", "김철수", StringComparison.Ordinal)), File.ReadAllBytes(session.MapperPath));

        var before = File.ReadAllBytes(session.MapperPath);
        session.Replace("byName", t => t.Replace("김철수", "김철수 😀", StringComparison.Ordinal));
        Assert.False(await handler.SaveAsync(ws, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(session.MapperPath));
        Assert.True(session.IsDirty);
        Assert.Single(((TestDialogs)ws.Host.Dialogs).Messages);
        Assert.False(File.Exists(session.MapperPath + ".oranda-tmp"));
    });

    [Fact]
    public Task Readonly_file_reports_and_keeps_edits() => Ui.Run(async () =>
    {
        var (_, ws, session, handler, _) = Setup(MapperSamples.EmpMapper);
        var before = File.ReadAllBytes(session.MapperPath);
        File.SetAttributes(session.MapperPath, FileAttributes.ReadOnly);
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));

        Assert.False(await handler.SaveAsync(ws, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(session.MapperPath));
        Assert.True(session.IsDirty);
        Assert.Null(session.LastRebase);
        Assert.Single(((TestDialogs)ws.Host.Dialogs).Messages);
    });

    // ---- 밖에서 바뀜 · 감시 ------------------------------------------------------------------

    [Fact]
    public Task Save_after_an_ide_edit_of_another_statement_keeps_both_edits() => Ui.Run(async () =>
    {
        var (_, ws, session, handler, _) = Setup(MapperSamples.EmpMapper);
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "DELETE FROM EMP WHERE", "DELETE FROM EMP E WHERE")); // IDE

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var text = File.ReadAllText(session.MapperPath);
        Assert.Contains("COUNT(1)", text, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM EMP E WHERE", text, StringComparison.Ordinal);
        Assert.False(session.IsDirty);
    });

    [Fact]
    public Task External_change_without_my_edits_reloads_after_the_debounce() => Ui.Run(() =>
    {
        var (_, ws, session, _, _) = Setup(MapperSamples.EmpMapper);
        var (watch, watcher, time) = (Watch(ws), Watchers[^1], Time);
        File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"));

        watcher.Raise();
        watcher.Raise();
        Assert.Equal(1, time.Created); // 사건 두 번 = 타이머 하나(디바운스)
        Assert.Equal(TimeSpan.FromMilliseconds(MapperWatchService.DebounceMilliseconds), time.LastDue);
        time.Fire();

        Assert.Contains("COUNT(1)", session.Document.Text, StringComparison.Ordinal);
        Assert.Equal(MapperBannerKind.Reloaded, watch.StateOf(ws)!.Banner);
        Assert.True(watch.StateOf(ws)!.IsWatching);
    });

    [Fact]
    public Task External_change_of_another_statement_merges_and_keeps_mine() => Ui.Run(() =>
    {
        var (_, ws, session, _, _) = Setup(MapperSamples.EmpMapper);
        session.Replace("deleteEmp", t => t.Replace("DELETE FROM EMP", "DELETE FROM EMP E", StringComparison.Ordinal));
        File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"));

        Watchers[^1].Raise();
        Time.Fire();

        Assert.Contains("COUNT(1)", session.Document.Text, StringComparison.Ordinal);
        Assert.Equal(["deleteEmp"], session.PendingEdits().Select(MapperSavePlan.KeyOf));
        var state = Watch(ws).StateOf(ws)!;
        Assert.Equal(MapperBannerKind.Merged, state.Banner);
        Assert.False(state.HasConflicts);
    });

    [Theory]
    [InlineData(MapperMergeChoice.Disk)]
    [InlineData(MapperMergeChoice.Mine)]
    public Task External_change_of_the_same_statement_asks_blocks_save_and_applies_the_choice(MapperMergeChoice choice) => Ui.Run(async () =>
    {
        var (_, ws, session, handler, _) = Setup(MapperSamples.EmpMapper);
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(EMPNO)", StringComparison.Ordinal));
        File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"));
        Watchers[^1].Raise();
        Time.Fire();

        var watch = Watch(ws);
        var state = watch.StateOf(ws)!;
        Assert.Equal(MapperBannerKind.Conflict, state.Banner);
        Assert.Equal("countEmp", Assert.Single(state.Conflicts).Key);
        Assert.Null(session.LastDisk); // 답을 받기 전에는 반영하지 않는다
        Assert.False(await handler.SaveAsync(ws, CancellationToken.None));
        Assert.DoesNotContain("COUNT(EMPNO)", File.ReadAllText(session.MapperPath), StringComparison.Ordinal);

        watch.ResolveAll(ws, choice);

        Assert.Equal(MapperBannerKind.Merged, state.Banner);
        Assert.Contains("COUNT(1)", session.Document.Text, StringComparison.Ordinal);
        Assert.Equal(choice == MapperMergeChoice.Mine, session.IsDirty);
        if (choice == MapperMergeChoice.Mine)
        {
            Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
            Assert.Contains("COUNT(EMPNO)", File.ReadAllText(session.MapperPath), StringComparison.Ordinal);
        }
    });

    [Fact]
    public Task Own_write_does_not_raise_an_external_change() => Ui.Run(async () =>
    {
        var (_, ws, session, handler, _) = Setup(MapperSamples.EmpMapper);
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
        session.Replace("deleteEmp", t => t.Replace("DELETE FROM EMP", "DELETE FROM EMP E", StringComparison.Ordinal));

        Watchers[^1].Raise(); // 임시 파일 → 바꾸기가 낸 사건
        Time.Fire();

        Assert.Equal(MapperBannerKind.None, Watch(ws).StateOf(ws)!.Banner);
        Assert.Null(session.LastDisk);
        Assert.True(session.IsDirty);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Deleted_file_keeps_edits_and_save_rewrites_the_same_path_or_a_picked_one(bool pick) => Ui.Run(async () =>
    {
        var (_, ws, session, handler, _) = Setup(MapperSamples.EmpMapper);
        var dialogs = (TestDialogs)ws.Host.Dialogs;
        var original = session.MapperPath;
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        File.Delete(original);
        Watchers[^1].Raise();
        Time.Fire();
        Assert.Equal(MapperBannerKind.Missing, Watch(ws).StateOf(ws)!.Banner);
        Assert.True(session.IsDirty);

        var moved = Path.Combine(_root, "moved", "EmpMapper.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        dialogs.Choose = items => pick ? items[1] : items[0];
        dialogs.SavePath = moved;
        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var target = pick ? moved : original;
        Assert.Equal(target, session.MapperPath);
        Assert.Contains("COUNT(1)", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Equal(pick, !File.Exists(original));
        Assert.Equal(MapperBannerKind.None, Watch(ws).StateOf(ws)!.Banner);
        Assert.Equal(pick ? 2 : 1, Watchers.Count); // 경로를 바꾸면 감시를 옮긴다
    });

    [Fact]
    public Task Attaching_a_session_whose_file_changed_while_the_app_was_closed_merges_at_once() => Ui.Run(() =>
    {
        // 복구(5.6): 03-01 이 기준 원문·고침을 되살려 세션을 붙인다. 앱이 꺼진 동안 바뀐 파일은 감시 사건이 없으므로 붙을 때 견준다.
        var path = Write(MapperSamples.EmpMapper);
        var session = new TestSession(path);
        session.Replace("deleteEmp", t => t.Replace("DELETE FROM EMP", "DELETE FROM EMP E", StringComparison.Ordinal));
        File.WriteAllText(path, Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"));
        var (host, watch) = NewHost();
        var ws = host.EnsureWorkspace();

        ws.MyBatis = session;

        Assert.Contains("COUNT(1)", session.Document.Text, StringComparison.Ordinal);
        Assert.Equal(["deleteEmp"], session.PendingEdits().Select(MapperSavePlan.KeyOf));
        Assert.Equal(MapperBannerKind.Merged, watch.StateOf(ws)!.Banner);
    });

    [Fact]
    public async Task Real_watcher_reports_an_atomic_replace()
    {
        var path = Write(MapperSamples.EmpMapper);
        using var watcher = new MapperFileWatcher(path);
        using var hit = new SemaphoreSlim(0);
        watcher.Changed += (_, _) => hit.Release();
        await new Oranda.Platform.MyBatis.MapperFileWriter().WriteAsync(path, Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"), MapperFileFormat.Default with { NewLine = "\n" }, CancellationToken.None);
        Assert.True(await hit.WaitAsync(TimeSpan.FromSeconds(10)), "감시가 바꾸기 사건을 내지 않았다(macOS FSEvents 는 늦을 수 있어 10초를 준다)");
    }

    // ---- 닫기 확인 · 명령 ------------------------------------------------------------------------

    [Theory]
    [InlineData(SaveChoice.Save, true, false)]
    [InlineData(SaveChoice.DontSave, true, true)]
    [InlineData(SaveChoice.Cancel, false, true)]
    public Task Closing_a_dirty_mapper_asks_save_dont_save_or_cancel(SaveChoice answer, bool closed, bool fileUnchanged) => Ui.Run(async () =>
    {
        var (host, ws, session, _, _) = Setup(MapperSamples.EmpMapper);
        var before = File.ReadAllBytes(session.MapperPath);
        var dialogs = (TestDialogs)host.Dialogs;
        dialogs.SaveAnswer = answer;
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));

        Assert.Equal(closed, await host.TryCloseWorkspaceAsync(ws, CancellationToken.None));

        Assert.Equal(["EmpMapper.xml"], dialogs.SaveAsked);
        Assert.Equal(!closed, host.Workspaces.Contains(ws));
        Assert.Equal(fileUnchanged, before.SequenceEqual(File.ReadAllBytes(session.MapperPath)));
    });

    [Fact]
    public Task Closing_after_a_partial_save_keeps_the_workspace_open() => Ui.Run(async () =>
    {
        var (host, ws, session, _, presenter) = Setup(MapperSamples.EmpMapper);
        ((TestDialogs)host.Dialogs).SaveAnswer = SaveChoice.Save;
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        session.Replace("deleteEmp", t => t.Replace("DELETE FROM EMP", "DELETE FROM EMP E", StringComparison.Ordinal));
        presenter.Answer = r => new SaveDiffResult([r.Items[0]], r.ShowEveryTime);

        Assert.False(await host.TryCloseWorkspaceAsync(ws, CancellationToken.None));
        Assert.Contains(ws, host.Workspaces);
        Assert.True(session.IsDirty);
    });

    [Fact]
    public Task Save_command_and_file_save_both_go_to_the_save_hook() => Ui.Run(async () =>
    {
        var (host, ws, session, _, _) = Setup(MapperSamples.EmpMapper);
        host.ActiveWorkspace = ws;
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        await new MyBatisSaveCommands { Host = host }.ExecuteAsync(new CommandContext("oranda.mybatis.save", FakeContextKeys.AllTrue), CancellationToken.None);
        Assert.Contains("COUNT(1)", File.ReadAllText(session.MapperPath), StringComparison.Ordinal);

        session.Replace("deleteEmp", t => t.Replace("DELETE FROM EMP", "DELETE FROM EMP E", StringComparison.Ordinal));
        Assert.True(await host.SaveAllAsync(CancellationToken.None));
        Assert.Contains("DELETE FROM EMP E", File.ReadAllText(session.MapperPath), StringComparison.Ordinal);
    });

    // ---- 03-01 실제 세션·문장 탭 ------------------------------------------------------------------

    [Fact]
    public Task Real_session_saving_two_edited_tabs_changes_only_those_statements_and_clears_dirty() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync();
        var original = File.ReadAllText(session.MapperPath);
        var count = session.Document.Find("countEmp")!;
        var delete = session.Document.Find("deleteEmp")!;
        var countTab = session.OpenStatement("countEmp")!;
        var deleteTab = session.OpenStatement("deleteEmp")!;
        EditTab(countTab, "COUNT(*)", "COUNT(EMPNO)");
        EditTab(deleteTab, "DELETE FROM EMP", "DELETE\n    FROM EMP"); // 탭 글은 파일 들여쓰기(2칸)를 걷어 낸 글: 저장 때 다시 붙는다

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var expected = original[..count.ElementRange.Start]
            + count.Text.Replace("COUNT(*)", "COUNT(EMPNO)", StringComparison.Ordinal)
            + original[count.ElementRange.End..delete.ElementRange.Start]
            + delete.Text.Replace("DELETE FROM EMP", "DELETE\n      FROM EMP", StringComparison.Ordinal)
            + original[delete.ElementRange.End..];
        Assert.Equal(expected, File.ReadAllText(session.MapperPath));
        Assert.False(session.IsDirty);
        Assert.False(countTab.IsDirty);
        Assert.False(deleteTab.IsDirty);
    });

    [Fact]
    public Task Real_session_rename_and_new_statement_are_written_and_rebased_by_their_new_ids() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync();
        session.RenameStatement("selectEmp", "selectEmpByNo");
        var created = session.CreateStatement(MyBatisStatementKind.Select, "countDept");
        EditTab(created, "</select>", "  SELECT COUNT(*) FROM DEPT\n</select>");
        var countTab = session.OpenStatement("countEmp")!;
        EditTab(countTab, "<select id=\"countEmp\"", "<select id=\"countAll\"");

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var reread = Parser.Parse(File.ReadAllBytes(session.MapperPath));
        Assert.NotNull(reread.Find("selectEmpByNo"));
        Assert.NotNull(reread.Find("countDept"));
        Assert.NotNull(reread.Find("countAll"));
        Assert.Null(reread.Find("countEmp"));
        Assert.False(session.IsDirty); // Rebase 가 지금 id 로 맞대어 세 고침을 모두 지웠다
    });

    [Fact]
    public Task Real_session_keeps_my_tab_edit_when_the_ide_changes_another_statement() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync();
        var countTab = session.OpenStatement("countEmp")!;
        var deleteTab = session.OpenStatement("deleteEmp")!;
        EditTab(countTab, "COUNT(*)", "COUNT(1)");
        File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "DELETE FROM EMP WHERE", "DELETE FROM EMP E WHERE"));
        Watchers[^1].Raise();
        Time.Fire();

        Assert.Contains("DELETE FROM EMP E WHERE", deleteTab.Text, StringComparison.Ordinal); // 안 고친 탭은 파일 것으로
        Assert.Contains("COUNT(1)", countTab.Text, StringComparison.Ordinal);
        Assert.True(countTab.IsDirty);
        Assert.Equal(MapperBannerKind.Merged, handler.Watch.StateOf(ws)!.Banner);

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
        var text = File.ReadAllText(session.MapperPath);
        Assert.Contains("COUNT(1)", text, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM EMP E WHERE", text, StringComparison.Ordinal);
    });

    [Fact]
    public Task Real_session_conflict_answered_with_disk_replaces_my_tab_text() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync();
        var countTab = session.OpenStatement("countEmp")!;
        EditTab(countTab, "COUNT(*)", "COUNT(EMPNO)");
        File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"));
        Watchers[^1].Raise();
        Time.Fire();
        Assert.Equal(MapperBannerKind.Conflict, handler.Watch.StateOf(ws)!.Banner);

        handler.Watch.ResolveAll(ws, MapperMergeChoice.Disk);

        var tab = session.OpenStatement("countEmp")!;
        Assert.Contains("COUNT(1)", tab.Text, StringComparison.Ordinal);
        Assert.False(session.IsDirty);
    });

    [Fact]
    public Task Banner_shows_over_the_statement_editor_while_the_file_is_missing_and_hides_after_saving() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, ws, session, handler) = await OpenRealAsync(factory);
        var tab = session.OpenStatement("countEmp")!;
        var window = new Window { Width = 900, Height = 600, Content = factory.CreateEditor(tab, session) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = Assert.Single(window.GetVisualDescendants().OfType<MapperBannerBar>());
            Assert.Same(bar, ((MyBatisStatementEditorView)window.Content!).BannerSlot.Content); // 편집기 위에 겹치지 않고 머리 줄 아래 한 행
            Assert.False(bar.IsVisible);

            EditTab(tab, "COUNT(*)", "COUNT(1)");
            File.Delete(session.MapperPath);
            Watchers[^1].Raise();
            Time.Fire();
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsVisible);

            ((TestDialogs)ws.Host.Dialogs).Choose = items => items[0];
            Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 리뷰 지적(저장 중 밖에서 바뀜 · 같은 지금 id · 쓸 수 없는 id · 묶음 · 새 충돌) ---------------

    [Fact]
    public Task External_change_while_the_save_dialog_is_open_is_not_overwritten() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync();
        var presenter = new TestPresenter();
        handler.Presenter = presenter;
        var countTab = session.OpenStatement("countEmp")!;
        EditTab(countTab, "COUNT(*)", "COUNT(1)");
        presenter.Answer = r =>
        {
            // 창이 떠 있는 동안 IDE 가 다른 문장을 저장하고 감시가 울린다.
            File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "DELETE FROM EMP WHERE", "DELETE FROM EMP E WHERE"));
            Watchers[^1].Raise();
            Time.Fire();
            return new SaveDiffResult(r.Items, r.ShowEveryTime);
        };

        Assert.False(await handler.SaveAsync(ws, CancellationToken.None));

        var disk = File.ReadAllText(session.MapperPath);
        Assert.Contains("DELETE FROM EMP E WHERE", disk, StringComparison.Ordinal); // IDE 고침이 그대로
        Assert.DoesNotContain("COUNT(1)", disk, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM EMP E WHERE", session.Document.Text, StringComparison.Ordinal); // 미룬 감시가 저장 뒤 병합했다
        Assert.True(countTab.IsDirty);

        presenter.Answer = r => new SaveDiffResult(r.Items, r.ShowEveryTime);
        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
        disk = File.ReadAllText(session.MapperPath);
        Assert.Contains("COUNT(1)", disk, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM EMP E WHERE", disk, StringComparison.Ordinal);
    });

    [Fact]
    public Task Partial_save_keeps_items_that_share_a_current_id_together() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync();
        var presenter = new TestPresenter();
        handler.Presenter = presenter;
        session.DeleteStatement("countEmp");
        var created = session.CreateStatement(MyBatisStatementKind.Select, "countEmp");
        EditTab(created, "</select>", "  SELECT 42 FROM DUAL\n</select>");
        presenter.Answer = r => new SaveDiffResult([r.Items.First(i => i.Kind == MapperSaveItemKind.Deleted)], r.ShowEveryTime);

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var reread = Parser.Parse(File.ReadAllBytes(session.MapperPath));
        Assert.Contains("SELECT 42", reread.Find("countEmp")!.Text, StringComparison.Ordinal);
        Assert.False(session.IsDirty);
    });

    [Fact]
    public Task An_id_the_session_cannot_use_blocks_the_save() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync();
        var before = File.ReadAllBytes(session.MapperPath);
        var tab = session.OpenStatement("countEmp")!;
        EditTab(tab, "id=\"countEmp\"", "id=\"count-emp\"");

        Assert.False(await handler.SaveAsync(ws, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(session.MapperPath));
        Assert.Contains(((TestDialogs)ws.Host.Dialogs).Messages, m => m.Contains("count-emp", StringComparison.Ordinal));
        Assert.True(session.IsDirty);
    });

    [Fact]
    public void MergePlan_treats_the_same_id_in_another_group_as_another_element()
    {
        var text = "<mapper namespace=\"t\">\n  <select id=\"x\">SELECT 1 FROM DUAL</select>\n  <resultMap id=\"x\" type=\"map\"><id column=\"A\"/></resultMap>\n</mapper>\n";
        var baseline = Parser.Parse(text);
        var disk = Parser.Parse(text.Replace("column=\"A\"", "column=\"B\"", StringComparison.Ordinal));
        var mine = new List<MapperEdit> { Replace(baseline, "x", "SELECT 1", "SELECT 2") };

        var plan = MapperMerge.Plan(baseline, disk, mine, Parser);

        Assert.Empty(plan.Conflicts); // resultMap 만 바뀌었다
        Assert.Equal(["x"], plan.KeepMine);
    }

    [Fact]
    public Task Resolving_asks_again_when_a_new_conflict_appeared_and_a_reverted_file_clears_the_banner() => Ui.Run(() =>
    {
        var (_, ws, session, _, _) = Setup(MapperSamples.EmpMapper);
        session.Replace("countEmp", t => t.Replace("COUNT(*)", "COUNT(EMPNO)", StringComparison.Ordinal));
        session.Replace("deleteEmp", t => t.Replace("DELETE FROM EMP", "DELETE FROM EMP X", StringComparison.Ordinal));
        File.WriteAllText(session.MapperPath, Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"));
        Watchers[^1].Raise();
        Time.Fire();
        var watch = Watch(ws);
        var state = watch.StateOf(ws)!;
        Assert.Equal(["countEmp"], state.Conflicts.Select(c => c.Key));

        // 띠가 떠 있는 동안 IDE 가 내가 고친 다른 문장도 고쳤다: 답은 countEmp 에만 있으므로 반영하지 않고 다시 묻는다.
        File.WriteAllText(session.MapperPath, Change(Change(MapperSamples.EmpMapper, "COUNT(*)", "COUNT(1)"), "DELETE FROM EMP WHERE", "DELETE FROM EMP E WHERE"));
        watch.ResolveAll(ws, MapperMergeChoice.Disk);
        Assert.Null(session.LastDisk);
        Assert.Equal(MapperBannerKind.Conflict, state.Banner);
        Assert.Equal(["countEmp", "deleteEmp"], state.Conflicts.Select(c => c.Key));

        // 파일이 기준으로 돌아오면 충돌도 사라진다.
        File.WriteAllText(session.MapperPath, MapperSamples.EmpMapper);
        Watchers[^1].Raise();
        Time.Fire();
        Assert.Equal(MapperBannerKind.None, state.Banner);
        Assert.Empty(state.Conflicts);
    });

    // ---- 준비 -------------------------------------------------------------------------------

    // ---- 들여쓰기(지시 mybatis-indent-1): 탭 = 파일 들여쓰기를 걷어 낸 글, 저장 = 다시 붙인 글 ---------------------------

    [Fact]
    public Task Indented_mapper_opened_in_every_tab_and_saved_keeps_the_file_bytes() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync(text: MyBatisIndentTests.Indented);
        var before = File.ReadAllBytes(session.MapperPath);
        foreach (var id in MyBatisIndentTests.StatementIds)
        {
            Assert.False(session.OpenStatement(id)!.IsDirty);
        }

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(session.MapperPath));
        Assert.False(session.IsDirty);
    });

    [Fact]
    public Task Indented_mapper_saving_one_edited_statement_writes_only_it_with_the_file_indent() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync(text: MyBatisIndentTests.Indented);
        var original = File.ReadAllText(session.MapperPath);
        session.OpenStatement("selectMoSpc");
        var deleteTab = session.OpenStatement("deleteMoSpc")!;
        EditTab(deleteTab, "</delete>", "   and DEL_YN = 'N'\n</delete>");

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var expected = original.Replace(
            "         where SPC_ID = #{spcId}\n    </delete>",
            "         where SPC_ID = #{spcId}\n       and DEL_YN = 'N'\n    </delete>",
            StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllText(session.MapperPath));
        Assert.False(deleteTab.IsDirty);
        Assert.StartsWith("<delete id=\"deleteMoSpc\">\n    delete from MO_SPC", deleteTab.Text, StringComparison.Ordinal); // 저장 뒤에도 탭은 걷어 낸 글
        Assert.EndsWith("\n   and DEL_YN = 'N'\n</delete>", deleteTab.Text, StringComparison.Ordinal);
    });

    [Fact]
    public Task Indented_crlf_mapper_with_tab_characters_keeps_line_ends_and_tabs_on_save() => Ui.Run(async () =>
    {
        var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<mapper namespace=\"m\">\r\n"
            + "\t<delete id=\"d\">\r\n\t\tdelete from T\r\n\r\n\t\t where A = 1\r\n\t</delete>\r\n"
            + "\t<select id=\"s\">\r\n\t\tselect 1 from DUAL\r\n\t</select>\r\n</mapper>\r\n";
        var (_, ws, session, handler) = await OpenRealAsync(text: text);
        var tab = session.OpenStatement("d")!;
        Assert.Equal("<delete id=\"d\">\r\n\tdelete from T\r\n\r\n\t where A = 1\r\n</delete>", tab.Text);
        EditTab(tab, "A = 1", "A = 2");

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        Assert.Equal(text.Replace("A = 1", "A = 2", StringComparison.Ordinal), File.ReadAllText(session.MapperPath));
    });

    [Fact]
    public Task Indented_mapper_merges_an_outside_change_into_dedented_tabs_and_saves_both() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync(text: MyBatisIndentTests.Indented);
        var selectTab = session.OpenStatement("selectMoSpc")!;
        var deleteTab = session.OpenStatement("deleteMoSpc")!;
        EditTab(selectTab, "select *", "select SPC_ID");
        var disk = Change(MyBatisIndentTests.Indented, "delete from MO_SPC", "delete from MO_SPC_H");
        File.WriteAllText(session.MapperPath, disk);
        Watchers[^1].Raise();
        Time.Fire();

        Assert.Equal(MapperBannerKind.Merged, handler.Watch.StateOf(ws)!.Banner);
        Assert.Equal("<delete id=\"deleteMoSpc\">\n    delete from MO_SPC_H\n     where SPC_ID = #{spcId}\n</delete>", deleteTab.Text);
        Assert.False(deleteTab.IsDirty);
        Assert.True(selectTab.IsDirty);

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));
        Assert.Equal(Change(disk, "select *", "select SPC_ID"), File.ReadAllText(session.MapperPath));
    });

    [Fact]
    public Task Indented_mapper_formatted_in_the_tab_is_saved_with_the_file_indent() => Ui.Run(async () =>
    {
        var (_, ws, session, handler) = await OpenRealAsync(text: MyBatisIndentTests.Indented);
        var tab = session.OpenStatement("selectMoSpc")!;
        var formatted = new Oranda.Language.MyBatis.Formatting.MyBatisStatementFormatter().Format(tab.Text, new Oranda.Core.Formatting.FormatterOptions());
        Assert.True(formatted.Succeeded, formatted.Message);
        Assert.EndsWith("\n</select>", formatted.Text, StringComparison.Ordinal); // 탭에서는 닫는 태그가 0칸이라 본문은 한 단만 들여 쓴다
        tab.Document.Replace(0, tab.Document.TextLength, formatted.Text);

        Assert.True(await handler.SaveAsync(ws, CancellationToken.None));

        var reread = Parser.Parse(File.ReadAllBytes(session.MapperPath));
        Assert.Equal(MyBatisStatementText.Reindent(formatted.Text, "    "), reread.Find("selectMoSpc")!.Text);
        Assert.Equal(session.Document.Find("deleteMoSpc")!.Text, Parser.Parse(MyBatisIndentTests.Indented).Find("deleteMoSpc")!.Text);
        Assert.Equal(formatted.Text, tab.Text);
        Assert.False(tab.IsDirty);
    });

    private async Task<(SqlEditorHost Host, SqlWorkspaceViewModel Ws, MyBatisWorkspaceSession Session, MyBatisSaveHandler Handler)> OpenRealAsync(MyBatisEditorViewFactory? factory = null, string? text = null)
    {
        var path = Write(text ?? MapperSamples.EmpMapper);
        var (host, watch) = NewHost();
        var handler = (MyBatisSaveHandler)host.MyBatisHooks.SaveHandler!;
        host.MyBatisHooks = new MyBatisHooks(
            openHandler: new MyBatisOpenHandler(),
            saveHandler: handler,
            editorViewFactory: factory,
            editorDecorators: factory is null ? null : [new MapperBannerDecorator(watch)]);
        Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
        var ws = Assert.Single(host.Workspaces);
        return (host, ws, Assert.IsType<MyBatisWorkspaceSession>(ws.MyBatis), handler);
    }

    private static void EditTab(SqlEditorViewModel tab, string from, string to)
    {
        int at = tab.Document.Text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, from);
        tab.Document.Replace(at, from.Length, to);
    }

    private List<TestWatcher> Watchers { get; } = [];

    private ManualTime Time { get; } = new();

    private static MapperWatchService Watch(SqlWorkspaceViewModel ws) =>
        ((MyBatisSaveHandler)ws.Host.MyBatisHooks.SaveHandler!).Watch;

    private (SqlEditorHost Host, SqlWorkspaceViewModel Ws, TestSession Session, MyBatisSaveHandler Handler, TestPresenter Presenter) Setup(string text, string name = "EmpMapper.xml") =>
        Setup(Encoding.UTF8.GetBytes(text), name);

    private (SqlEditorHost Host, SqlWorkspaceViewModel Ws, TestSession Session, MyBatisSaveHandler Handler, TestPresenter Presenter) Setup(byte[] bytes, string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        var (host, _) = NewHost();
        var ws = host.EnsureWorkspace();
        ws.SourcePath = path;
        var session = new TestSession(path);
        ws.MyBatis = session;
        var handler = (MyBatisSaveHandler)host.MyBatisHooks.SaveHandler!;
        return (host, ws, session, handler, (TestPresenter)handler.Presenter);
    }

    private (SqlEditorHost Host, MapperWatchService Watch) NewHost()
    {
        var watch = new MapperWatchService
        {
            Time = Time,
            Post = a => a(),
            CreateWatcher = _ =>
            {
                var w = new TestWatcher();
                Watchers.Add(w);
                return w;
            },
        };
        var handler = new MyBatisSaveHandler(watch) { Presenter = new TestPresenter(), ReadShowEveryTime = () => true };
        var host = new SqlEditorHost(dialogs: new TestDialogs()) { MyBatisHooks = new MyBatisHooks(saveHandler: handler) };
        watch.Attach(host);
        return (host, watch);
    }

    private string Write(string text)
    {
        var path = Path.Combine(_root, "EmpMapper.xml");
        File.WriteAllText(path, text);
        return path;
    }

    private static string Change(string text, string from, string to) => text.Replace(from, to, StringComparison.Ordinal);

    private static MapperEdit.ReplaceElement Replace(IMyBatisMapperDocument doc, string id, string from, string to) =>
        new(id, doc.Find(id)!.Text.Replace(from, to, StringComparison.Ordinal));

    /// <summary>
    /// 03-01 세션(MyBatisWorkspaceSession)과 같은 규칙: <see cref="Rebase"/> 는 지금 id 로 저장한 고침을 지우고,
    /// <see cref="ApplyDiskText"/> 는 keepMine(지금 id)의 고침과 새 문장만 남기며 디스크에서 사라진 문장의 고침은 새 문장으로 남긴다.
    /// </summary>
    private sealed class TestSession : IMyBatisWorkspaceSession
    {
        private List<MapperEdit> _edits = [];

        public TestSession(string path)
        {
            MapperPath = path;
            Document = Parser.Parse(File.ReadAllBytes(path));
        }

        public event EventHandler? StateChanged;

        public string MapperPath { get; private set; }

        public string? MapperNamespace => Document.MapperNamespace;

        public IMyBatisMapperDocument Document { get; private set; }

        public string BaselineHash => Document.Hash;

        public bool IsDirty => _edits.Count > 0;

        public string? SelectedStatementId => null;

        public IMyBatisFragmentResolver Fragments { get; } = new NoFragments();

        public (string Text, IReadOnlyCollection<string> Ids)? LastRebase { get; private set; }

        public string? LastDisk { get; private set; }

        public void Replace(string id, Func<string, string> change)
        {
            var current = _edits.OfType<MapperEdit.ReplaceElement>().FirstOrDefault(e => e.Id == id)?.NewText ?? Document.Find(id)!.Text;
            Set(new MapperEdit.ReplaceElement(id, change(current)));
        }

        public void Insert(string id, string text) => Set(new MapperEdit.InsertElement(id, text));

        public IReadOnlyList<MapperEdit> PendingEdits() => [.. _edits];

        public void Rebase(string newText, IReadOnlyCollection<string> writtenStatementIds)
        {
            LastRebase = (newText, writtenStatementIds);
            Document = ParseLike(newText);
            _edits.RemoveAll(e => writtenStatementIds.Contains(MapperSavePlan.CurrentIdOf(e, Parser)));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyDiskText(string diskText, IReadOnlyCollection<string> keepMine)
        {
            LastDisk = diskText;
            Document = ParseLike(diskText);
            var kept = new List<MapperEdit>();
            foreach (var e in _edits)
            {
                var current = MapperSavePlan.CurrentIdOf(e, Parser);
                if (e is MapperEdit.InsertElement)
                {
                    kept.Add(e);
                }
                else if (keepMine.Contains(current) && Document.Find(MapperSavePlan.KeyOf(e)) is not null)
                {
                    kept.Add(e);
                }
                else if (keepMine.Contains(current) && e is MapperEdit.ReplaceElement r)
                {
                    kept.Add(new MapperEdit.InsertElement(current, r.NewText));
                }
            }

            _edits = kept;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Relocate(string newMapperPath) => MapperPath = newMapperPath;

        public string? StatementIdOf(SqlEditorViewModel tab) => null;

        // 실제 세션처럼 지금 id(고친 원문·새 문장·바꾼 id)로 찾는다. 지운 문장은 없다.
        public MyBatisElement? ElementOf(string statementId)
        {
            foreach (var e in _edits)
            {
                if (MapperSavePlan.CurrentIdOf(e, Parser) != statementId)
                {
                    continue;
                }

                return e switch
                {
                    MapperEdit.ReplaceElement r => MapperSavePlan.ElementOfText(r.NewText, Parser),
                    MapperEdit.InsertElement i => MapperSavePlan.ElementOfText(i.NewText, Parser),
                    MapperEdit.RenameElement n => Document.Find(n.Id),
                    _ => null,
                };
            }

            return _edits.Any(e => MapperSavePlan.KeyOf(e) == statementId) ? null : Document.Find(statementId);
        }

        public void SelectStatement(string? statementId)
        {
        }

        public SqlEditorViewModel? OpenStatement(string statementId) => null;

        public Task BeginNewStatementAsync(CancellationToken ct) => Task.CompletedTask;

        private void Set(MapperEdit edit)
        {
            _edits.RemoveAll(e => MapperSavePlan.KeyOf(e) == MapperSavePlan.KeyOf(edit));
            _edits.Add(edit);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        // 03-01 ParseLike 와 같이 BOM 을 지킨다.
        private IMyBatisMapperDocument ParseLike(string text) =>
            Document.Format.HasBom ? Parser.Parse(MapperSamples.Bytes(text, bom: true)) : Parser.Parse(text);
    }

    private sealed class NoFragments : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace) => null;
    }

    private sealed class TestPresenter : ISaveDiffPresenter
    {
        public List<SaveDiffRequest> Requests { get; } = [];

        public Func<SaveDiffRequest, SaveDiffResult?> Answer { get; set; } = r => new SaveDiffResult(r.Items, r.ShowEveryTime);

        public Task<SaveDiffResult?> ShowAsync(SaveDiffRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private sealed class TestWatcher : IMapperFileWatcher
    {
        public event EventHandler? Changed;

        public bool IsActive => true;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    /// <summary>디바운스 타이머를 시험이 직접 울린다.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public int Created => _timers.Count;

        public TimeSpan LastDue { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(this, callback, state);
            _timers.Add(t);
            LastDue = dueTime;
            return t;
        }

        public void Fire()
        {
            foreach (var t in _timers.ToList())
            {
                t.Fire();
            }
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            private bool _armed = true;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.LastDue = dueTime;
                _armed = true;
                return true;
            }

            public void Fire()
            {
                if (_armed)
                {
                    _armed = false;
                    callback(state);
                }
            }

            public void Dispose() => _armed = false;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestDialogs : IEditorDialogs
    {
        public List<string> Messages { get; } = [];

        public List<string> SaveAsked { get; } = [];

        public SaveChoice SaveAnswer { get; set; } = SaveChoice.Cancel;

        public Func<IReadOnlyList<string>, string?> Choose { get; set; } = _ => null;

        public string? SavePath { get; set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult(SavePath);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult(Choose(items));

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct)
        {
            SaveAsked.Add(tabName);
            return Task.FromResult(SaveAnswer);
        }

        public Task ShowMessageAsync(string title, string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
