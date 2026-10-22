using System.Text;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.LobRowView;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Features.Execution;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.LobRowView;

/// <summary>TSK-06-05: LOB 편집기(파일 저장·불러오기·읽기 전용 차단), 행 보기, LOB 저장 대상(PK/ROWID) 규칙. DB 없이 도는 시험.</summary>
[Collection("GridEditStatics")]
public sealed class LobRowViewTests : IDisposable
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-lobvm-" + Guid.NewGuid().ToString("N")[..8]);

    public LobRowViewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ResultGridCommands.Active = null;
        GridEditCommands.Active = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static FakeSession Session(bool readOnly = false) => new(new ConnectionProfile("dev", "scott", "h:1521/s", ReadOnly: readOnly));

    private static LobSaveTarget Target() => new(Emp, "DOC", new Dictionary<string, object?> { ["ID"] = 1m });

    private static string BigText(int chars)
    {
        var sb = new StringBuilder(chars + 16);
        while (sb.Length < chars)
        {
            sb.Append("가나다 ABC 😀 라마바\n");
        }

        return sb.ToString();
    }

    // ---- LobContent ----------------------------------------------------------------------

    [Fact]
    public void Hex_dump_has_offset_hex_and_ascii()
    {
        var dump = LobContent.HexDump("Hello, 한글 and more text\0"u8, 0);
        Assert.StartsWith("00000000  48 65 6C 6C 6F 2C 20 ED", dump, StringComparison.Ordinal);
        Assert.Contains("|Hello, ", dump, StringComparison.Ordinal);
        Assert.Contains("00000010", dump, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0 }, "image/bmp")]
    [InlineData(new byte[] { 1, 2, 3, 4 }, null)]
    public void Image_types_are_detected_by_magic(byte[] head, string? mime)
        => Assert.Equal(mime, LobContent.DetectImageMime(head));

    [Fact]
    public async Task Utf8_text_file_save_is_byte_exact_and_other_encodings_round_trip()
    {
        var text = BigText(300_000);
        var utf8 = Encoding.UTF8.GetBytes(text);
        var path = Path.Combine(_dir, "a.txt");
        await LobContent.SaveTextFileAsync(new MemoryStream(utf8), path, LobContent.GetEncoding("UTF-8"), default);
        Assert.Equal(utf8, await File.ReadAllBytesAsync(path));

        foreach (var name in new[] { "UTF-8 (BOM)", "UTF-16 LE", "UTF-16 BE" })
        {
            var p = Path.Combine(_dir, name.Replace(' ', '_').Replace('(', '_').Replace(')', '_') + ".txt");
            await LobContent.SaveTextFileAsync(new MemoryStream(utf8), p, LobContent.GetEncoding(name), default);
            Assert.Equal(text, await LobContent.ReadTextFileAsync(p, default));
        }
    }

    // ---- LobWriter -----------------------------------------------------------------------

    [Fact]
    public async Task Writer_builds_bound_update_with_clob_bind_through_execution_engine()
    {
        var exec = new FakeExecution();
        var s = Session();
        var r = await new LobWriter(exec).WriteAsync(s, Target(), new BindValue(BindDataType.Clob, "새 글"), commit: true, default);

        Assert.True(r.Success);
        Assert.True(r.Committed);
        Assert.Equal(1, s.Commits);
        var update = Assert.Single(exec.Statements, x => x.Kind == StatementKind.Dml);
        Assert.StartsWith("UPDATE SCOTT.EMP SET DOC = :v1 WHERE ID = :k1", update.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("새 글", update.Text, StringComparison.Ordinal);
        Assert.Equal(BindDataType.Clob, exec.LastBinds!["v1"].Type);
        Assert.Equal("새 글", exec.LastBinds["v1"].Value);
        Assert.Equal(1m, exec.LastBinds["k1"].Value);
    }

    [Fact]
    public async Task Writer_uses_rowid_key_and_rolls_back_when_not_exactly_one_row()
    {
        var exec = new FakeExecution
        {
            Handler = (st, _) => Task.FromResult(new ExecutionResult(st, ExecutionStatus.Succeeded) { RowsAffected = st.Kind == StatementKind.Dml ? 0 : null }),
        };
        var target = new LobSaveTarget(Emp, "DOC", new Dictionary<string, object?> { ["ROWID"] = "AAAB" });
        var s = Session();

        var r = await new LobWriter(exec).WriteAsync(s, target, new BindValue(BindDataType.Blob, new byte[] { 1 }), commit: true, default);

        Assert.False(r.Success);
        Assert.Equal(0, s.Commits);
        Assert.Contains(exec.Statements, x => x.Text.Contains("WHERE ROWID = :rid", StringComparison.Ordinal));
        Assert.Contains(exec.Statements, x => x.Text == "ROLLBACK TO SAVEPOINT " + LobWriter.Savepoint);
    }

    [Fact]
    public async Task Writer_refuses_read_only_profile_without_touching_engine()
    {
        var exec = new FakeExecution();
        var r = await new LobWriter(exec).WriteAsync(Session(readOnly: true), Target(), new BindValue(BindDataType.Clob, "x"), false, default);

        Assert.False(r.Success);
        Assert.Empty(exec.Statements);
    }

    [Fact]
    public async Task Writer_refuses_missing_key()
    {
        var exec = new FakeExecution();
        var noKey = new LobSaveTarget(Emp, "DOC", new Dictionary<string, object?>());
        var r = await new LobWriter(exec).WriteAsync(Session(), noKey, new BindValue(BindDataType.Clob, "x"), false, default);

        Assert.False(r.Success);
        Assert.Empty(exec.Statements);
    }

    // ---- LOB 편집기 ----------------------------------------------------------------------

    [Fact]
    public async Task Small_clob_loads_from_preview_and_is_editable_when_target_exists()
    {
        var vm = new LobEditorViewModel(new FakeAccessor(), new LobWriter(new FakeExecution()));
        var lob = new LobValue(LobKind.Clob, 3) { TextPreview = "abc" };

        await vm.AttachAsync(new LobEditorSource("DOC", Session(), lob, null, Target(), null));

        Assert.True(vm.IsText);
        Assert.Equal("abc", vm.Text);
        Assert.False(vm.IsPartial);
        Assert.True(vm.CanEditText);
        Assert.False(vm.CanSaveToDatabase);
        vm.Text = "abcd";
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSaveToDatabase);
    }

    [Fact]
    public async Task Large_clob_shows_only_head_and_saves_whole_content_to_file_via_stream()
    {
        var text = BigText(LobContent.MaxEditChars + 250_000);
        var accessor = new FakeAccessor { Utf8 = Encoding.UTF8.GetBytes(text) };
        var vm = new LobEditorViewModel(accessor, new LobWriter(new FakeExecution()));
        var lob = new LobValue(LobKind.Clob, text.Length) { TextPreview = text[..4000], IsTruncated = true, Locator = new object() };

        await vm.AttachAsync(new LobEditorSource("DOC", Session(), lob, null, Target(), null));

        Assert.True(vm.IsPartial);
        Assert.Equal(LobContent.MaxEditChars, vm.Text.Length);
        Assert.False(vm.CanEditText);
        Assert.False(vm.CanSaveToDatabase);

        var path = Path.Combine(_dir, "big.txt");
        await vm.SaveToFileAsync(path);
        Assert.Equal(accessor.Utf8, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, accessor.Opens); // 편집기 앞부분 읽기 + 파일 저장 각 1번
    }

    [Fact]
    public async Task Blob_shows_hex_and_image_preview_and_saves_exact_bytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var accessor = new FakeAccessor { Utf8 = png };
        var vm = new LobEditorViewModel(accessor, new LobWriter(new FakeExecution()));
        var lob = new LobValue(LobKind.Blob, png.Length) { BinaryPreview = png.AsMemory(0, 8), IsTruncated = true, Locator = new object() };

        await vm.AttachAsync(new LobEditorSource("IMG", Session(), lob, null, Target(), null));

        Assert.True(vm.IsBinary);
        Assert.NotNull(vm.ImageBytes);
        Assert.Contains("89 50 4E 47", vm.HexText, StringComparison.Ordinal);

        var path = Path.Combine(_dir, "img.bin");
        await vm.SaveToFileAsync(path);
        Assert.Equal(png, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Load_from_file_then_save_to_database_sends_blob_bind()
    {
        var exec = new FakeExecution();
        var vm = new LobEditorViewModel(new FakeAccessor(), new LobWriter(exec));
        var lob = new LobValue(LobKind.Blob, 2) { BinaryPreview = new byte[] { 9, 9 } };
        await vm.AttachAsync(new LobEditorSource("B", Session(), lob, null, Target(), null));
        var file = Path.Combine(_dir, "in.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);

        await vm.LoadFromFileAsync(file);
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSaveToDatabase);
        var r = await vm.SaveToDatabaseAsync(commit: false);

        Assert.True(r.Success);
        Assert.False(vm.IsDirty);
        Assert.Equal(BindDataType.Blob, exec.LastBinds!["v1"].Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, exec.LastBinds["v1"].Value);
    }

    [Fact]
    public async Task Edited_clob_saves_as_clob_bind_and_text_file_uses_selected_encoding()
    {
        var exec = new FakeExecution();
        var vm = new LobEditorViewModel(new FakeAccessor(), new LobWriter(exec));
        await vm.AttachAsync(new LobEditorSource("C", Session(), new LobValue(LobKind.Clob, 1) { TextPreview = "x" }, null, Target(), null));
        vm.Text = "바뀐 글";
        vm.FileEncodingName = "UTF-16 LE";
        var path = Path.Combine(_dir, "e.txt");

        await vm.SaveToFileAsync(path);
        Assert.Equal("바뀐 글", await LobContent.ReadTextFileAsync(path, default));
        Assert.Equal([0xFF, 0xFE], (await File.ReadAllBytesAsync(path))[..2]);

        var r = await vm.SaveToDatabaseAsync(commit: true);
        Assert.True(r.Success);
        Assert.Equal(BindDataType.Clob, exec.LastBinds!["v1"].Type);
        Assert.Equal("바뀐 글", exec.LastBinds["v1"].Value);
    }

    [Fact]
    public async Task Read_only_profile_blocks_editing_and_saving_but_allows_view_and_file_save()
    {
        var exec = new FakeExecution();
        var vm = new LobEditorViewModel(new FakeAccessor(), new LobWriter(exec));
        await vm.AttachAsync(new LobEditorSource("C", Session(readOnly: true), new LobValue(LobKind.Clob, 3) { TextPreview = "abc" }, null, Target(), null));

        Assert.False(vm.CanEditText);
        Assert.False(vm.CanSaveToDatabase);
        vm.Text = "zzz";
        var r = await vm.SaveToDatabaseAsync(commit: false);

        Assert.False(r.Success);
        Assert.Empty(exec.Statements);
        var path = Path.Combine(_dir, "ro.txt");
        await vm.SaveToFileAsync(path);
        Assert.Equal("zzz", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Missing_target_blocks_save_with_reason()
    {
        var exec = new FakeExecution();
        var vm = new LobEditorViewModel(new FakeAccessor(), new LobWriter(exec));
        await vm.AttachAsync(new LobEditorSource("C", Session(), new LobValue(LobKind.Clob, 1) { TextPreview = "a" }, null, null, "편집 모드로 여세요"));
        vm.Text = "b";

        var r = await vm.SaveToDatabaseAsync(commit: false);

        Assert.False(r.Success);
        Assert.Contains("편집 모드", r.Message, StringComparison.Ordinal);
        Assert.Empty(exec.Statements);
    }

    // ---- 저장 대상 규칙(그리드 편집 세션과 같은 PK/ROWID) -----------------------------------------

    private static ResultColumn[] DocColumns() =>
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "DOC", "CLOB", ColumnDataKind.Clob),
    ];

    private static async Task<ResultGridModel> LoadGridAsync(ResultColumn[] cols, object?[][] rows, bool knownCount = true)
    {
        var m = new ResultGridModel(new MemResult(cols, rows, knownCount));
        await m.LoadFirstPageAsync();
        return m;
    }

    private static Task<GridEditSession> OpenEditAsync(ResultGridModel grid, string sql, bool readOnly)
    {
        var meta = new OneTable(new TableInfo(Emp, [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }])
        {
            Constraints = [new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["ID"])],
        });
        return GridEditSession.OpenAsync(new RowEditor(new FakeExecution()), meta, Session(readOnly),
            new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" }, grid.Columns,
            Oranda.App.Features.GridEdit.GridEditBinding.OriginalValues(grid), () => grid.LoadedRowCount, target: null, default);
    }

    [Fact]
    public async Task Target_comes_from_edit_session_pk_columns()
    {
        var lob = new LobValue(LobKind.Clob, 2) { TextPreview = "hi" };
        var grid = await LoadGridAsync(DocColumns(), [[5m, lob]]);
        var edit = await OpenEditAsync(grid, "SELECT id, doc FROM scott.emp", readOnly: false);

        var (target, reason) = LobRowViewCommands.ResolveTarget(grid, edit, 0, 1);

        Assert.Null(reason);
        Assert.Equal(Emp, target!.Table);
        Assert.Equal("DOC", target.Column);
        Assert.Equal(5m, target.Key["ID"]);
    }

    [Fact]
    public async Task Edit_binding_shows_pending_values_new_rows_and_maps_rows()
    {
        var grid = await LoadGridAsync(DocColumns(), [[5m, "a"], [6m, "b"]]);
        var edit = await OpenEditAsync(grid, "SELECT id, doc FROM scott.emp", readOnly: false);
        GridEditBinding.Attach(grid, edit);
        var editable = edit.Capability.EditableColumnOrdinals[0];

        Assert.Same(edit, GridEditBinding.Of(grid));
        Assert.True(edit.SetCell(0, editable, 50m));
        Assert.True(grid.IsEdited(0, editable));
        Assert.Equal(GridRowMark.Modified, grid.RowMarkOf(0));
        Assert.True(grid.TryGetValue(0, editable, out var shown));
        Assert.Equal(50m, shown);

        // 새 행은 질의 결과 뒤에 붙고, 화면 행이 음수 편집 행 번호로 바뀐다.
        var inserted = edit.AddRow();
        Assert.Equal(3, grid.RowCount);
        Assert.Equal(2, grid.BaseRowCount);
        Assert.Equal(inserted, GridEditBinding.ToEditRow(grid, edit, 2));
        Assert.Equal(GridRowMark.Inserted, grid.RowMarkOf(2));
        Assert.Equal(1L, GridEditBinding.ToEditRow(grid, edit, 1));

        Assert.Equal(12m, GridEditBinding.Parse(grid, 0, "12"));
        Assert.Null(GridEditBinding.Parse(grid, 0, string.Empty));
    }

    [Fact]
    public async Task Target_missing_without_edit_mode_or_when_result_not_editable()
    {
        var lob = new LobValue(LobKind.Clob, 2) { TextPreview = "hi" };
        var grid = await LoadGridAsync(DocColumns(), [[5m, lob]]);

        var (noEdit, why) = LobRowViewCommands.ResolveTarget(grid, null, 0, 1);
        Assert.Null(noEdit);
        Assert.Contains("편집 모드", why, StringComparison.Ordinal);

        var ro = await OpenEditAsync(grid, "SELECT id, doc FROM scott.emp", readOnly: true);
        var (blocked, why2) = LobRowViewCommands.ResolveTarget(grid, ro, 0, 1);
        Assert.Null(blocked);
        Assert.NotNull(why2);

        var agg = await OpenEditAsync(grid, "SELECT DISTINCT id, doc FROM scott.emp", readOnly: false);
        Assert.Null(LobRowViewCommands.ResolveTarget(grid, agg, 0, 1).Target);
    }

    [Fact]
    public async Task Source_for_non_lob_cell_is_plain_text_read_only()
    {
        var grid = await LoadGridAsync(DocColumns(), [[5m, null]]);
        var src = LobRowViewCommands.BuildSource(grid, 0, 0, null, Session());

        Assert.NotNull(src);
        Assert.Null(src.Lob);
        Assert.Equal("5", src.PlainText);
        Assert.Null(src.Target);
    }

    // ---- 명령 ---------------------------------------------------------------------------------

    [Fact]
    public async Task View_value_command_opens_editor_for_active_cell()
    {
        var lob = new LobValue(LobKind.Clob, 2) { TextPreview = "hi" };
        var grid = await LoadGridAsync(DocColumns(), [[5m, lob]]);
        grid.SelectCell(new GridCell(0, 1));
        ResultGridCommands.Active = grid;
        string? openedPanel = null;
        var handler = new LobRowViewCommandHandler
        {
            OpenDocument = (id, vm, _) =>
            {
                openedPanel = id;
                return vm;
            },
            SessionProvider = () => Session(),
            EditSessionProvider = () => null,
        };

        await handler.ExecuteAsync(new CommandContext("oranda.grid.viewValue", new NoKeys()), default);

        Assert.Equal("oranda.panel.lobEditor", openedPanel);
    }

    [Fact]
    public async Task Row_view_command_shows_tool_and_follows_grid_selection()
    {
        var grid = await LoadGridAsync(DocColumns(), [[1m, null], [2m, new LobValue(LobKind.Clob, 3) { TextPreview = "abc" }]]);
        grid.SelectCell(new GridCell(1, 0));
        await Task.Delay(100); // 그리드 Changed 는 다른 스레드로 게시된다: 창을 붙이기 전에 밀린 알림을 비운다
        ResultGridCommands.Active = grid;
        string? shown = null;
        using var vm = new RowViewerViewModel();
        var handler = new LobRowViewCommandHandler { ShowTool = id => shown = id };

        await handler.ExecuteAsync(new CommandContext("oranda.grid.rowView", new NoKeys()), default);

        Assert.Equal("oranda.panel.rowView", shown);
        Assert.Equal(1, vm.Row);
        Assert.Equal(["ID", "DOC"], vm.Fields.Select(f => f.Name));
        Assert.Equal("2", vm.Fields[0].Text);
        Assert.True(vm.Fields[1].IsLob);
        Assert.Equal("abc", vm.Fields[1].Text);

        grid.SelectCell(new GridCell(0, 0));
        for (var i = 0; i < 100 && (vm.Row != 0 || vm.Fields.Count != 2 || !vm.Fields[1].IsNull); i++)
        {
            await Task.Delay(20); // 그리드 Changed 는 UI 문맥으로 게시된다
        }

        Assert.Equal(0, vm.Row);
        Assert.True(vm.Fields[1].IsNull);

        vm.MoveRow(1);
        Assert.Equal(1, grid.Active.Row);
        for (var i = 0; i < 100 && (vm.Row != 1 || vm.Fields.Count != 2 || vm.Fields[1].IsNull); i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, vm.Row);

        vm.FilterText = "doc";
        Assert.Equal(["DOC"], vm.Fields.Select(f => f.Name));
    }

    // ---- 행 보기 표(R1~R4) ----------------------------------------------------------------------

    private static ResultColumn[] NameColumns() =>
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
    ];

    private static async Task<(RowViewerViewModel Vm, ResultGridModel Grid)> OpenViewerAsync(object?[][] rows, bool knownCount = true)
    {
        var grid = await LoadGridAsync(NameColumns(), rows, knownCount);
        grid.SelectCell(new GridCell(0, 0));
        await Task.Delay(100); // 그리드 Changed 는 다른 스레드로 게시된다: 창을 붙이기 전에 밀린 알림을 비운다
        var vm = new RowViewerViewModel();
        vm.Attach(grid);
        return (vm, grid);
    }

    [Fact]
    public async Task Row_viewer_moves_first_last_and_shows_position()
    {
        var (vm, _) = await OpenViewerAsync([[1m, "a"], [2m, "b"], [3m, "c"]]);
        using var _v = vm;

        Assert.Equal("1 / 3", vm.HeaderText);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanGoForward);
        Assert.Equal(1, vm.Fields[0].Number);
        Assert.Equal("NAME", vm.Fields[1].Name);

        vm.MoveLast();
        for (var i = 0; i < 100 && vm.HeaderText != "3 / 3"; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("3 / 3", vm.HeaderText);
        Assert.False(vm.CanGoForward);

        vm.MoveFirst();
        for (var i = 0; i < 100 && vm.Row != 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("1 / 3", vm.HeaderText);
    }

    [Fact]
    public async Task Row_viewer_marks_unknown_total_with_plus()
    {
        var (vm, grid) = await OpenViewerAsync([[1m, "a"], [2m, "b"]], knownCount: false);
        using var _v = vm;

        Assert.Null(grid.TotalRowCount);
        Assert.Equal("1 / 2+", vm.HeaderText);
    }

    [Fact]
    public async Task Row_viewer_copies_selected_value_and_all_fields_and_shows_null()
    {
        var (vm, _) = await OpenViewerAsync([[1m, null]]);
        using var _v = vm;

        Assert.True(vm.Fields[1].IsNull);
        Assert.Equal("(null)", vm.Fields[1].Text);

        vm.SelectedField = vm.Fields[0];
        Assert.Equal("1", vm.SelectedValueText());
        vm.SelectedField = vm.Fields[1];
        Assert.Equal(string.Empty, vm.SelectedValueText());
        Assert.Equal("ID = 1" + Environment.NewLine + "NAME = (null)", vm.AllFieldsText());

        vm.FilterText = "name";
        Assert.Equal("NAME = (null)", vm.AllFieldsText());
    }

    [Fact]
    public async Task Row_viewer_is_read_only_without_edit_session()
    {
        var (vm, _) = await OpenViewerAsync([[1m, "a"]]);
        using var _v = vm;

        Assert.All(vm.Fields, f => Assert.False(f.CanEdit));
        RowViewerViewModel.BeginEdit(vm.Fields[1]);
        Assert.False(vm.Fields[1].IsEditing);
    }

    /// <summary>NAME 필드. 그리드 Changed 가 다른 스레드에서 목록을 다시 채우는 중이면 null.</summary>
    private static RowField? NameField(RowViewerViewModel vm)
    {
        try
        {
            return vm.Fields.Count > 1 ? vm.Fields[1] : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Row_viewer_key_edit_becomes_the_same_change_as_a_grid_edit()
    {
        var (vm, grid) = await OpenViewerAsync([[1m, "a"], [2m, "b"]]);
        using var _v = vm;
        var meta = new OneTable(new TableInfo(Emp, [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2")])
        {
            Constraints = [new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["ID"])],
        });
        var edit = await GridEditSession.OpenAsync(new RowEditor(new FakeExecution()), meta, Session(false),
            new SqlStatement("SELECT id, name FROM scott.emp", default, StatementKind.Query) { Verb = "SELECT" }, grid.Columns,
            GridEditBinding.OriginalValues(grid), () => grid.LoadedRowCount, target: null, default);
        GridEditBinding.Attach(grid, edit);
        await Task.Delay(100);
        vm.Refresh();

        RowViewerViewModel.BeginEdit(vm.Fields[0]);
        vm.Fields[0].EditText = "99";
        vm.CommitEdit(vm.Fields[0]);

        var change = Assert.Single(edit.BuildChanges());
        Assert.Equal(RowChangeKind.Update, change.Kind);
        Assert.Equal(99m, change.NewValues["ID"]);
        Assert.Equal(1m, change.Key["ID"]); // WHERE 는 원래 키 값
    }

    [Fact]
    public async Task Row_viewer_lists_fields_in_the_grid_column_order_with_positional_numbers()
    {
        var (vm, grid) = await OpenViewerAsync([[1m, "a"], [2m, "b"]]);
        using var _v = vm;

        Assert.True(grid.MoveColumn(1, 0)); // NAME 을 맨 앞으로
        vm.Refresh();

        Assert.Equal(["NAME", "ID"], vm.Fields.Select(f => f.Name));
        Assert.Equal([1, 2], vm.Fields.Select(f => f.Number));
        Assert.Equal([1, 0], vm.Fields.Select(f => f.Ordinal)); // 편집·값 읽기는 원래 열 번호
        Assert.Equal("a", vm.Fields[0].Text);
    }

    [Fact]
    public async Task Row_viewer_edit_updates_grid_allows_key_and_syncs_back()
    {
        var (vm, grid) = await OpenViewerAsync([[1m, "a"], [2m, "b"]]);
        using var _v = vm;
        var meta = new OneTable(new TableInfo(Emp, [new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, new ColumnInfo("NAME", 2, "VARCHAR2")])
        {
            Constraints = [new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["ID"])],
        });
        var edit = await GridEditSession.OpenAsync(new RowEditor(new FakeExecution()), meta, Session(false),
            new SqlStatement("SELECT id, name FROM scott.emp", default, StatementKind.Query) { Verb = "SELECT" }, grid.Columns,
            GridEditBinding.OriginalValues(grid), () => grid.LoadedRowCount, target: null, default);
        GridEditBinding.Attach(grid, edit);
        await Task.Delay(100); // 편집 세션을 붙이며 게시된 알림이 목록을 다시 채우도록 기다린다
        vm.Refresh();

        Assert.True(vm.Fields[0].CanEdit); // 키 열도 그리드와 같은 규칙으로 고칠 수 있다(TSK-13-20)
        Assert.True(vm.Fields[1].CanEdit);

        RowViewerViewModel.BeginEdit(vm.Fields[0]);
        Assert.True(vm.Fields[0].IsEditing);
        vm.Fields[0].IsEditing = false;

        var field = vm.Fields[1];
        RowViewerViewModel.BeginEdit(field);
        Assert.True(field.IsEditing);
        Assert.Equal("a", field.EditText);
        field.EditText = "zzz";
        vm.CommitEdit(field);

        Assert.True(grid.IsEdited(0, 1));
        Assert.True(grid.TryGetValue(0, 1, out var shown));
        Assert.Equal("zzz", shown);
        for (var i = 0; i < 100 && NameField(vm) is not { IsEdited: true }; i++)
        {
            await Task.Delay(20); // 그리드 Changed 는 UI 문맥으로 게시된다
        }

        Assert.True(NameField(vm)!.IsEdited);
        Assert.Equal("zzz", NameField(vm)!.Text);

        // 그리드 쪽에서 고치면 행 보기에도 반영된다.
        Assert.True(edit.SetCell(0, 1, "yyy"));
        for (var i = 0; i < 100 && NameField(vm)?.Text != "yyy"; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("yyy", NameField(vm)!.Text);
    }

    // ---- 가짜 -----------------------------------------------------------------------------------

    private sealed class FakeAccessor : ILobAccessor
    {
        public byte[] Utf8 { get; set; } = [];

        public int Opens { get; private set; }

        public Task<Stream> OpenReadAsync(ISession s, LobValue lob, CancellationToken ct)
        {
            Opens++;
            return Task.FromResult<Stream>(new MemoryStream(Utf8));
        }
    }

    private sealed class NoKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }

    private sealed class MemResult(ResultColumn[] cols, object?[][] rows, bool knownCount = true) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = cols;

        public bool IsComplete => true;

        public long? KnownRowCount => knownCount ? rows.Length : null;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            IReadOnlyList<IReadOnlyList<object?>> data = [.. cols.Select((_, c) => (IReadOnlyList<object?>)rows.Select(r => r[c]).ToList())];
            return Task.FromResult(pageIndex == 0
                ? new ResultPage(0, 0, rows.Length, data) { IsLast = knownCount }
                : new ResultPage(pageIndex, 0, 0, [.. cols.Select(_ => (IReadOnlyList<object?>)[])]) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OneTable(TableInfo table) : IMetadataProvider
    {
        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef t, CancellationToken ct) => Task.FromResult(table with { Table = t });

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
