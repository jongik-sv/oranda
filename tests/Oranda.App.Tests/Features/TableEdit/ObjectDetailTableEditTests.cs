using Oranda.App.Features.ObjectDetail;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 객체 상세 편집 모드(테이블 편집기 설계 7.1, T6): ✎ 토글 대상, 켤 때 캐시 무효화 뒤 새로 읽기(TE-4·TE-28), 페이지 바뀐 건수, 페이지 이동 유지,
/// 끄기·새로 고침·닫기·열 순서 바꾸기 앞 버림 확인, 특수 테이블 제한, 방언 없음. 가짜 메타데이터·엔진.
/// </summary>
public class ObjectDetailTableEditTests
{
    private static async Task<(ObjectDetailViewModel Vm, FakeMeta Meta, Probe Probe)> OpenAsync(TableKind kind = TableKind.Heap, bool dialect = true)
    {
        var (vm, meta, probe) = CreateDetail(kind, dialect);
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        return (vm, meta, probe);
    }

    private static void Select(ObjectDetailViewModel vm, string title) => vm.SelectedSection = vm.Sections.Single(s => s.Title == title);

    [Fact]
    public async Task Edit_toggle_shows_only_on_table_edit_pages()
    {
        var (vm, _, _) = await OpenAsync();
        Assert.Equal("열", vm.SelectedSection?.Title);
        Assert.True(vm.CanToggleTableEdit);
        foreach (var page in new[] { "제약", "인덱스", "설명" })
        {
            Select(vm, page);
            Assert.True(vm.CanToggleTableEdit, page);
        }

        Select(vm, "통계");
        Assert.False(vm.CanToggleTableEdit);
        Select(vm, "일반");
        Assert.False(vm.CanToggleTableEdit);

        // 뷰는 편집 대상이 아니다.
        var (view, _, _) = CreateDetail();
        await view.OpenAsync(new FakeSession(), new DbObjectRef("SAMPLE", "V_ORDERS", "VIEW"), showDdl: false);
        Assert.False(view.CanToggleTableEdit);
        Assert.False(await view.SetTableEditingAsync(true));
    }

    [Fact]
    public async Task Turning_editing_on_invalidates_the_cache_then_reads_the_table_fresh()
    {
        var (vm, _, probe) = await OpenAsync();

        Assert.True(await vm.SetTableEditingAsync(true));

        Assert.Equal(["invalidate ORDERS", "read ORDERS"], probe.Calls); // TE-28: 무효화 뒤 GetTableAsync
        Assert.True(vm.IsTableEditing);
        Assert.True(vm.IsEditPageShown);
        Assert.False(vm.ShowsSectionTable);
        Assert.Equal(4, vm.TableEdit!.Columns.Count);
        Assert.Equal(TableEditPage.Columns, vm.TableEdit.CurrentPage);
        Assert.False(vm.TableEdit.IsDirty);
        Assert.Equal(string.Empty, vm.TableEdit.PreviewText); // 안 고치면 문장 0개
        Assert.False(vm.CanSaveTableEditScript);
        Assert.Null(vm.CloseConfirmation()); // 고친 것이 없으면 닫을 때 묻지 않는다
    }

    [Fact]
    public async Task Without_a_dialect_editing_stays_off_and_says_why()
    {
        var (vm, _, probe) = await OpenAsync(dialect: false);
        vm.TableEditDialect = null;

        Assert.False(await vm.SetTableEditingAsync(true));

        Assert.False(vm.IsTableEditing);
        Assert.Empty(probe.Calls);
        Assert.Contains("테이블 편집 구현이 없다", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Section_titles_show_change_counts_and_edits_survive_page_switches()
    {
        var (vm, _, _) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        var edit = vm.TableEdit!;

        edit.Columns.Single(c => c.Name == "STATUS").LengthText = "20";
        edit.AddRow(TableEditPage.Columns)!.As<Oranda.App.Features.TableEdit.ColumnRowViewModel>().Name = "total_vat";

        Assert.Equal("열 (2)", vm.Sections.Single(s => s.Title == "열").DisplayTitle);
        Assert.Equal("제약", vm.Sections.Single(s => s.Title == "제약").DisplayTitle);

        Select(vm, "제약");
        Assert.True(vm.IsEditingConstraints);
        Assert.Equal(TableEditPage.Constraints, edit.CurrentPage);
        edit.AddRow(TableEditPage.Constraints, ConstraintKind.Unique);
        Assert.Equal("제약 (1)", vm.Sections.Single(s => s.Title == "제약").DisplayTitle);

        // 편집 페이지가 아닌 곳(통계)은 읽기 표 그대로 보이고, 편집 세션은 남는다.
        Select(vm, "통계");
        Assert.True(vm.IsTableEditing);
        Assert.False(vm.IsEditPageShown);
        Assert.True(vm.ShowsSectionTable);

        Select(vm, "열");
        Assert.Same(edit, vm.TableEdit);
        Assert.Equal("TOTAL_VAT", edit.Columns.Last().Column.Name); // 칸 글은 대문자 사전 표기로
        Assert.Equal(3, edit.Script.Statements.Count); // 탭 전체(모든 페이지)의 차이
        Assert.Equal(edit.Script.PreviewText(Environment.NewLine), edit.PreviewText); // 미리보기 = 실행 문장
    }

    [Fact]
    public async Task Turning_off_with_changes_asks_and_keeps_editing_when_declined()
    {
        var (vm, _, probe) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.TableComment = "주문 머리";

        probe.Answer = false;
        Assert.True(await vm.SetTableEditingAsync(false)); // 거절 → 켠 채
        Assert.Contains("편집을 끌까요", Assert.Single(probe.Asked), StringComparison.Ordinal);
        Assert.True(vm.TableEdit!.IsDirty);

        probe.Answer = true;
        Assert.False(await vm.SetTableEditingAsync(false));
        Assert.False(vm.IsTableEditing);
        Assert.All(vm.Sections, s => Assert.Equal(0, s.EditCount));
    }

    [Fact]
    public async Task Turning_off_without_changes_does_not_ask()
    {
        var (vm, _, probe) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.TableComment = "바꿈";
        vm.TableEdit.TableComment = "주문"; // 고쳤다 되돌림 = 문장 0개

        Assert.False(vm.TableEdit.IsDirty);
        Assert.False(await vm.SetTableEditingAsync(false));
        Assert.Empty(probe.Asked);
    }

    [Fact]
    public async Task Refresh_while_editing_asks_before_discarding_and_reloads_when_agreed()
    {
        var (vm, meta, probe) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.Columns[0].Comment = "바뀐 주석";
        var reads = meta.DetailReads;

        probe.Answer = false;
        Assert.False(await vm.RefreshAsync());
        Assert.Equal(reads, meta.DetailReads);
        Assert.True(vm.IsTableEditing);
        Assert.Contains("새로 읽을까요", probe.Asked[0], StringComparison.Ordinal);

        probe.Answer = true;
        Assert.True(await vm.RefreshAsync());
        Assert.Equal(reads + 1, meta.DetailReads);
        Assert.False(vm.IsTableEditing);
    }

    [Fact]
    public async Task Closing_the_tab_asks_only_when_there_are_changes()
    {
        var (vm, _, probe) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        Assert.Null(vm.CloseConfirmation());

        vm.TableEdit!.ToggleDrop(vm.TableEdit.Columns.Single(c => c.Name == "OLD_FLAG").Key);
        var confirm = vm.CloseConfirmation();
        Assert.NotNull(confirm);

        probe.Answer = false;
        Assert.False(await confirm(CancellationToken.None));
        Assert.Contains("탭을 닫을까요", Assert.Single(probe.Asked), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reorder_columns_asks_to_discard_changes_first()
    {
        var (vm, _, probe) = await OpenAsync();
        Assert.True(await vm.ConfirmBeforeReorderAsync()); // 편집 안 함 → 묻지 않음
        Assert.Empty(probe.Asked);

        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.Columns[1].IsNullable = true;
        probe.Answer = false;
        Assert.False(await vm.ConfirmBeforeReorderAsync());
        Assert.True(vm.IsTableEditing);

        probe.Answer = true;
        Assert.True(await vm.ConfirmBeforeReorderAsync());
        Assert.False(vm.IsTableEditing);
        Assert.Contains("열 순서 바꾸기", probe.Asked[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Special_tables_allow_only_column_comments_and_defaults()
    {
        var (vm, _, _) = await OpenAsync(TableKind.Cluster);
        await vm.SetTableEditingAsync(true);
        var edit = vm.TableEdit!;
        var row = edit.Columns[2];

        Assert.True(edit.IsRestricted);
        Assert.Contains("클러스터", edit.RestrictionText, StringComparison.Ordinal);
        Assert.False(row.CanEditName);
        Assert.False(row.CanEditType);
        Assert.False(row.CanEditLength);
        Assert.True(row.CanEditDefault);
        Assert.True(row.CanEditComment);
        Assert.False(vm.CanAddRow);
        Assert.Null(edit.AddColumn());
        Assert.Null(edit.AddConstraint(ConstraintKind.Check));

        edit.ToggleDrop(row.Key); // 지우기도 안 됨
        Assert.False(edit.IsDirty);

        row.DefaultValue = "'A'";
        row.Comment = "임시 상태";
        Assert.Equal(1, edit.CountFor(TableEditPage.Columns));
        Assert.True(row.IsDefaultChanged);
        Assert.True(row.IsCommentChanged);
    }

    [Fact]
    public async Task Opening_again_ends_editing()
    {
        var (vm, _, _) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.TableComment = "x";

        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);

        Assert.False(vm.IsTableEditing);
    }

    [Fact]
    public async Task Script_save_writes_the_request_format_without_bom()
    {
        var (vm, _, _) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.Columns.Single(c => c.Name == "OLD_FLAG").Comment = "옛 표시";
        var path = Path.Combine(Path.GetTempPath(), "oranda-te-" + Guid.NewGuid().ToString("N") + ".sql");
        string? suggested = null;
        vm.PickScriptFile = (name, _) =>
        {
            suggested = name;
            return Task.FromResult<string?>(path);
        };
        vm.Today = () => new DateOnly(2026, 11, 17);
        try
        {
            Assert.Equal(path, await vm.SaveTableEditScriptAsync());

            Assert.Equal("ORDERS.sql", suggested);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]); // UTF-8 BOM 없음(TE-17)
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.Contains("SAMPLE.ORDERS", text, StringComparison.Ordinal);
            Assert.Contains("ALTER TABLE SAMPLE.ORDERS MODIFY OLD_FLAG;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dev-example", text, StringComparison.Ordinal); // 접속 이름은 넣지 않는다
        }
        finally
        {
            File.Delete(path);
        }

        vm.PickScriptFile = (_, _) => Task.FromResult<string?>(null);
        Assert.Null(await vm.SaveTableEditScriptAsync()); // 취소
    }

    [Fact]
    public async Task Open_in_editor_sends_the_preview_text_with_this_connection()
    {
        var (vm, _, _) = await OpenAsync();
        await vm.SetTableEditingAsync(true);
        (string Text, Guid? Profile)? opened = null;
        vm.DdlEditorOpener = (text, profile) =>
        {
            opened = (text, profile);
            return true;
        };
        Assert.False(vm.OpenTableEditDdlInEditor()); // 문장이 없으면 안 연다

        vm.TableEdit!.TableComment = "새 주석";
        Assert.True(vm.OpenTableEditDdlInEditor());

        Assert.StartsWith(vm.TableEdit.PreviewText, opened!.Value.Text, StringComparison.Ordinal);
        Assert.Equal(vm.Session!.Profile.Id, opened.Value.Profile);
    }
}

internal static class RowCast
{
    public static T As<T>(this Oranda.App.Features.TableEdit.TableEditRowViewModel row)
        where T : Oranda.App.Features.TableEdit.TableEditRowViewModel => (T)row;
}
